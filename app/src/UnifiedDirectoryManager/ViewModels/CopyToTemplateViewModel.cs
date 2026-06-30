using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>One copyable attribute (checkbox + friendly label + the source user's value).</summary>
public partial class TemplateCopyAttrRow : ObservableObject
{
    [ObservableProperty] private bool _include = true;
    public string LdapName { get; init; } = string.Empty;
    public string Friendly { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

/// <summary>One copyable group membership (on-prem by DN, or cloud by object id).</summary>
public partial class TemplateCopyGroupRow : ObservableObject
{
    [ObservableProperty] private bool _include = true;
    public string Name { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty; // on-prem DN, or Entra group id
}

/// <summary>
/// Copies an existing user into a new creation template: the admin ticks which properties and group
/// memberships to carry over. Identity fields (name, sAMAccountName, UPN, email, given/surname, …) are
/// intentionally excluded — templates derive those per-user from tokens, which are seeded on save.
/// </summary>
public partial class CopyToTemplateViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IGraphService _graph;
    private readonly ITemplateStore _store;
    private readonly IDialogService _dialogs;
    private readonly string _userDn;

    [ObservableProperty] private string _name = "Copied template";
    [ObservableProperty] private string _targetOu = string.Empty;
    [ObservableProperty] private string _upnSuffix = string.Empty;
    [ObservableProperty] private bool _enabledByDefault = true;
    [ObservableProperty] private bool _mustChangePassword = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _hasCloudGroups;

    public ObservableCollection<TemplateCopyAttrRow> Attributes { get; } = new();
    public ObservableCollection<TemplateCopyGroupRow> OnPremGroups { get; } = new();
    public ObservableCollection<TemplateCopyGroupRow> CloudGroups { get; } = new();

    public bool Saved { get; private set; }

    // Per-user identity attributes never copied literally — templates token-derive these (seeded on save).
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "cn", "name", "sAMAccountName", "userPrincipalName", "mail", "mailNickname",
        "givenName", "sn", "displayName", "initials", "middleName", "proxyAddresses",
    };

    public CopyToTemplateViewModel(IDirectoryService directory, IGraphService graph, ITemplateStore store,
        IDialogService dialogs, string userDn)
    {
        _directory = directory;
        _graph = graph;
        _store = store;
        _dialogs = dialogs;
        _userDn = userDn;
    }

    public string TemplatesDirectory => _store.TemplatesDirectory;

    /// <summary>Loads the source user and populates the checkable attribute / group lists.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Loading user…";
        try
        {
            var attrs = await _directory.LoadObjectAsync(_userDn);
            var map = attrs.GroupBy(a => a.LdapName, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            TargetOu = DirectoryService.ParentDn(_userDn);

            // Derive the UPN suffix from the source user's UPN (the per-user prefix is dropped).
            if (map.TryGetValue("userPrincipalName", out var upnAttr) && upnAttr.RawValues.Count > 0)
            {
                var upn = upnAttr.RawValues[0];
                var at = upn.IndexOf('@');
                if (at >= 0 && at < upn.Length - 1) UpnSuffix = upn[(at + 1)..];
            }

            var display = map.TryGetValue("displayName", out var d) && d.DisplayValues.Count > 0
                ? d.DisplayValues[0] : DirectoryService.FirstRdn(_userDn);
            Name = $"Template from {display}";

            // Only curated, writable, single-valued, non-DN attributes are offered (keeps operational/system
            // attributes out of templates) — minus the identity set above.
            var catalog = AttributeCatalog.All
                .Where(m => !m.IsReadOnly && !m.IsMultiValued && !m.IsDnValued)
                .ToDictionary(m => m.LdapName, StringComparer.OrdinalIgnoreCase);

            foreach (var a in attrs)
            {
                if (Excluded.Contains(a.LdapName)) continue;
                if (!catalog.TryGetValue(a.LdapName, out var meta)) continue;
                if (a.RawValues.Count == 0 || string.IsNullOrWhiteSpace(a.RawValues[0])) continue;
                // Store the catalog's canonical lDAPDisplayName casing (DirectorySearcher returns names
                // lowercased, e.g. "streetaddress"), so the template editor's attribute dropdown — which
                // matches the catalog's camelCase by exact value — resolves instead of showing blank.
                Attributes.Add(new TemplateCopyAttrRow { LdapName = meta.LdapName, Friendly = meta.Friendly, Value = a.RawValues[0] });
            }
            foreach (var row in Attributes.OrderBy(r => r.Friendly, StringComparer.CurrentCultureIgnoreCase).ToList())
            {
                Attributes.Remove(row); Attributes.Add(row); // stable re-sort by friendly name
            }

            // On-prem group memberships.
            if (map.TryGetValue("memberOf", out var memberOf))
                for (int i = 0; i < memberOf.DisplayValues.Count; i++)
                {
                    var dn = i < memberOf.RawValues.Count ? memberOf.RawValues[i] : memberOf.DisplayValues[i];
                    OnPremGroups.Add(new TemplateCopyGroupRow { Name = memberOf.DisplayValues[i], Id = dn });
                }

            // Cloud group memberships (best-effort, only when signed in to Entra).
            if (_graph.IsSignedIn && map.TryGetValue("userPrincipalName", out var upn2) && upn2.RawValues.Count > 0)
            {
                try
                {
                    var groups = await _graph.GetUserGroupsByUpnAsync(upn2.RawValues[0]);
                    // Only cloud-only groups belong here — synced groups are already listed above under on-prem
                    // (and can't be added to in the cloud), so including them would double-count and mislead.
                    foreach (var g in groups.Where(g => !g.IsSynced))
                        CloudGroups.Add(new TemplateCopyGroupRow { Name = g.DisplayName, Id = g.Id });
                    HasCloudGroups = CloudGroups.Count > 0;
                }
                catch (Exception ex) { AppLog.Instance.Warn("Could not load cloud groups for copy-to-template: " + ex.Message); }
            }

            Status = $"{Attributes.Count} attribute(s), {OnPremGroups.Count} on-prem group(s), {CloudGroups.Count} cloud group(s) available.";
        }
        catch (Exception ex)
        {
            Status = "Could not load the user: " + DirectoryService.Friendly(ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void BrowseOu()
    {
        var dn = _dialogs.PickContainer(TargetOu);
        if (dn is not null) TargetOu = dn;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status = "Enter a template name."; return; }

        var template = new UserTemplate
        {
            Name = Name.Trim(),
            Description = "Created by copying a user.",
            TargetOu = TargetOu.Trim(),
            UpnSuffix = UpnSuffix.Trim(),
            EnabledByDefault = EnabledByDefault,
            MustChangePasswordAtNextLogon = MustChangePassword,
            GroupDns = OnPremGroups.Where(g => g.Include).Select(g => g.Id).ToList(),
            CloudGroups = CloudGroups.Where(g => g.Include).Select(g => new CloudGroupRef { Id = g.Id, Name = g.Name }).ToList(),
        };
        foreach (var row in Attributes.Where(r => r.Include))
            template.AttributeDefaults[row.LdapName] = row.Value;

        // Seed the per-user identity tokens (NOT copied from the source) so the template is usable as-is.
        template.AttributeDefaults["sAMAccountName"] = "{first}.{last}";
        template.AttributeDefaults["displayName"] = "{first} {last}";
        if (!string.IsNullOrWhiteSpace(UpnSuffix))
            template.AttributeDefaults["userPrincipalName"] = "{sam}@" + UpnSuffix.Trim();

        try
        {
            if (_store.LoadAll().Any(t => string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase))
                && !_dialogs.Confirm("Save template", $"A template named “{template.Name}” already exists. Overwrite it?", new[] { template.Name }))
                return;

            _store.Save(template, originalName: null);
            Saved = true;
            Status = $"Saved template “{template.Name}”.";
            _dialogs.Alert("Template saved", $"Saved “{template.Name}”.\n\nRefine it any time under File ▸ User Creation Templates.");
        }
        catch (Exception ex) { Status = ex.Message; }
    }
}
