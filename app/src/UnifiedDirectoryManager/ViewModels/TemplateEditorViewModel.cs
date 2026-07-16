using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>One attribute-default row in the template editor (friendly attribute + value/token).</summary>
public partial class TemplateAttributeRow : ObservableObject
{
    [ObservableProperty] private string _ldapName = "title";
    [ObservableProperty] private string _value = string.Empty;

    public IReadOnlyList<AttributeMeta> Attributes { get; } =
        AttributeCatalog.All.Where(a => !a.IsReadOnly && !a.IsDnValued)
                            .OrderBy(a => a.Friendly, StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>Create / save / recall / edit / delete new-user templates.</summary>
public partial class TemplateEditorViewModel : ObservableObject
{
    private readonly ITemplateStore _store;
    private readonly IDialogService _dialogs;
    private string? _originalName;

    public ObservableCollection<UserTemplate> Templates { get; } = new();
    public ObservableCollection<TemplateAttributeRow> AttributeRows { get; } = new();

    /// <summary>One combined bucket of groups spanning on-prem AD, Entra ID (Graph), and Exchange Online
    /// distribution groups. Split back into the template's typed lists (by <see cref="GroupRef.Channel"/>) on save.</summary>
    public ObservableCollection<GroupRef> Groups { get; } = new();

    [ObservableProperty] private UserTemplate? _selectedTemplate;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _targetOu = string.Empty;
    [ObservableProperty] private string _upnSuffix = string.Empty;
    [ObservableProperty] private bool _enabledByDefault = true;
    [ObservableProperty] private bool _mustChangePassword;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _managerDisplay = "(none)";
    private string? _managerDn; // template default manager DN (null = none)

    // Country picker: choosing a friendly name stores co / c / countryCode in the template.
    public IReadOnlyList<CountryInfo> Countries => Services.Countries.All;
    [ObservableProperty] private CountryInfo? _selectedCountry;

    // Email / UPN / proxy addresses have dedicated fields (token-supported).
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _upn = string.Empty;
    [ObservableProperty] private string _proxyAddressesText = string.Empty;

    // Attributes handled by dedicated fields, excluded from the generic attribute-row list.
    private static readonly string[] HandledLdap = { "co", "c", "countryCode", "mail", "userPrincipalName", "proxyAddresses", "manager" };

    public string TemplatesDirectory => _store.TemplatesDirectory;

    public TemplateEditorViewModel(ITemplateStore store, IDialogService dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        ReloadTemplates();
        // Guard the initial form setup so it isn't seen as an unsaved-changes "switch".
        _loadingForm = true;
        try { NewTemplate(); }
        finally { _loadingForm = false; }
    }

    // Guards/dirty-tracking so switching templates doesn't silently lose unsaved edits.
    private bool _loadingForm;
    private string _cleanSnapshot = string.Empty;

    private string Snapshot() => System.Text.Json.JsonSerializer.Serialize(BuildTemplate());
    private void MarkClean() => _cleanSnapshot = Snapshot();
    private bool IsDirty() => !_loadingForm && Snapshot() != _cleanSnapshot;

    partial void OnSelectedTemplateChanged(UserTemplate? value)
    {
        if (_loadingForm) return; // we're populating the form ourselves — not a user-driven switch

        // The form still holds the PREVIOUSLY loaded template's edits here. Offer to save them before
        // replacing the form, so switching templates never silently discards work.
        if (IsDirty())
        {
            var save = _dialogs.Confirm("Unsaved template changes",
                $"You have unsaved changes to “{Name}”. Save them before switching?",
                new[] { "OK saves your changes; Cancel discards them." });
            var targetName = value?.Name;
            if (save)
            {
                try { _store.Save(BuildTemplate(), _originalName); }
                catch (Exception ex) { Status = "Save failed: " + ex.Message; }
            }
            // Refresh the in-memory list from disk (so the saved item — or the discard — is reflected),
            // then re-point to the same new selection by name without re-entering this handler.
            _loadingForm = true;
            try
            {
                Templates.Clear();
                foreach (var t in _store.LoadAll()) Templates.Add(t);
                value = targetName is null ? null : Templates.FirstOrDefault(t => t.Name == targetName);
                SelectedTemplate = value;
            }
            finally { _loadingForm = false; }
        }

        if (value is null) return; // cleared (e.g. New) — NewTemplate populates + marks clean

        _loadingForm = true;
        try { LoadFields(value); MarkClean(); }
        finally { _loadingForm = false; }
    }

    /// <summary>Populates the editor fields from a template (call inside the <see cref="_loadingForm"/> guard).</summary>
    private void LoadFields(UserTemplate value)
    {
        _originalName = value.Name;
        Name = value.Name;
        Description = value.Description;
        TargetOu = value.TargetOu;
        UpnSuffix = value.UpnSuffix;
        EnabledByDefault = value.EnabledByDefault;
        MustChangePassword = value.MustChangePasswordAtNextLogon;
        _managerDn = value.AttributeDefaults.TryGetValue("manager", out var mgr) && !string.IsNullOrWhiteSpace(mgr) ? mgr : null;
        ManagerDisplay = _managerDn is null ? "(none)" : NameResolver.RdnFallback(_managerDn);

        AttributeRows.Clear();
        foreach (var kv in value.AttributeDefaults)
            if (!HandledLdap.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) // handled by dedicated fields
                AttributeRows.Add(new TemplateAttributeRow { LdapName = kv.Key, Value = kv.Value });

        // Dedicated fields.
        Email = value.AttributeDefaults.TryGetValue("mail", out var mail) ? mail : string.Empty;
        Upn = value.AttributeDefaults.TryGetValue("userPrincipalName", out var upn) ? upn : string.Empty;
        ProxyAddressesText = string.Join(Environment.NewLine, value.ProxyAddressPatterns);

        // Restore the country picker from c / co.
        SelectedCountry = (value.AttributeDefaults.TryGetValue("c", out var c) ? Services.Countries.ByAlpha2(c) : null)
            ?? (value.AttributeDefaults.TryGetValue("co", out var co) ? Services.Countries.ByName(co) : null)
            ?? Services.Countries.NotSet;

        // Combine the template's three typed group lists into the one editable bucket.
        Groups.Clear();
        foreach (var dn in value.GroupDns)
            Groups.Add(new GroupRef(GroupChannel.OnPremAd, NameResolver.RdnFallback(dn), dn, null, null, DirectoryService.ParentDn(dn)));
        foreach (var g in value.CloudGroups)
            Groups.Add(new GroupRef(GroupChannel.EntraGraph, g.Name, null, g.Id, null, "Entra ID group"));
        foreach (var g in value.DistributionGroups)
            Groups.Add(new GroupRef(GroupChannel.ExchangeOnline, g.Name, null, string.IsNullOrWhiteSpace(g.Id) ? null : g.Id, g.Smtp, "Distribution group"));
    }

    [RelayCommand]
    private void NewTemplate()
    {
        // Clearing the selection first lets OnSelectedTemplateChanged offer to save the current edits
        // (it still sees the old _originalName) before we reset the form to defaults.
        SelectedTemplate = null;
        _originalName = null;
        Name = "New template";
        Description = string.Empty;
        TargetOu = string.Empty;
        UpnSuffix = string.Empty;
        EnabledByDefault = true;
        MustChangePassword = false;
        AttributeRows.Clear();
        AttributeRows.Add(new TemplateAttributeRow { LdapName = "sAMAccountName", Value = "{first}.{last}" });
        AttributeRows.Add(new TemplateAttributeRow { LdapName = "displayName", Value = "{first} {last}" });
        Groups.Clear();
        _managerDn = null;
        ManagerDisplay = "(none)";
        SelectedCountry = Services.Countries.NotSet;
        Email = "{sam}@lacrossefootwear.com";
        Upn = "{sam}@{upnSuffix}";
        ProxyAddressesText = "SMTP:{sam}@lacrossefootwear.com" + Environment.NewLine + "smtp:{sam}@danner.com";
        MarkClean(); // a fresh template starts clean
    }

    [RelayCommand]
    private void Clone()
    {
        // Duplicate the current editor contents as a brand-new, unsaved template.
        _loadingForm = true;
        try { SelectedTemplate = null; } // drop the list selection without prompting/reloading
        finally { _loadingForm = false; }
        _originalName = null;             // Save will create a new template
        if (!Name.StartsWith("Copy of ", StringComparison.OrdinalIgnoreCase)) Name = "Copy of " + Name;
        Status = "Cloned — edit if needed, then Save to create the copy.";
        // Intentionally left dirty (no MarkClean) so switching away offers to save the clone.
    }

    [RelayCommand]
    private void BrowseOu()
    {
        var dn = _dialogs.PickContainer(TargetOu);
        if (dn is not null) TargetOu = dn;
    }

    [RelayCommand] private void AddAttribute() => AttributeRows.Add(new TemplateAttributeRow());
    [RelayCommand] private void RemoveAttribute(TemplateAttributeRow? row) { if (row is not null) AttributeRows.Remove(row); }

    [RelayCommand]
    private void PickGroups()
    {
        // One picker spans on-prem AD, Entra ID (Graph), and Exchange Online distribution groups.
        var picked = _dialogs.PickGroupsHybrid("Template groups (on-prem + cloud + Exchange)");
        if (picked is null) return;
        foreach (var g in picked)
            if (Groups.All(x => !string.Equals(x.Key, g.Key, StringComparison.OrdinalIgnoreCase)))
                Groups.Add(g);
    }

    [RelayCommand] private void RemoveGroup(GroupRef? row) { if (row is not null) Groups.Remove(row); }

    [RelayCommand]
    private void PickManager()
    {
        var picked = _dialogs.PickObjects("Select template manager", AdObjectType.User, multiSelect: false);
        if (picked is null || picked.Count == 0) return;
        _managerDn = picked[0].DistinguishedName;
        ManagerDisplay = picked[0].Name;
    }

    [RelayCommand]
    private void ClearManager()
    {
        _managerDn = null;
        ManagerDisplay = "(none)";
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status = "Template name is required."; return; }
        var template = BuildTemplate();
        try
        {
            _store.Save(template, _originalName);
            _originalName = template.Name;
            ReloadTemplates();
            // Re-point to the saved item and mark the form clean WITHOUT re-entering the dirty prompt.
            _loadingForm = true;
            try
            {
                SelectedTemplate = Templates.FirstOrDefault(t => t.Name == template.Name);
                MarkClean();
            }
            finally { _loadingForm = false; }
            Status = $"Saved “{template.Name}”.";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void Delete()
    {
        if (string.IsNullOrWhiteSpace(_originalName)) { Status = "Nothing to delete."; return; }
        if (!_dialogs.Confirm("Delete template", $"Delete template “{_originalName}”?", new[] { _originalName! })) return;
        try
        {
            _store.Delete(_originalName!);
            MarkClean(); // discard any edits to the just-deleted template so New doesn't offer to re-save it
            ReloadTemplates();
            NewTemplate();
            Status = "Template deleted.";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void Export()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status = "Give the template a name before exporting."; return; }
        var template = BuildTemplate();
        var path = _dialogs.PromptSaveFile("Template files (*.json)|*.json|All files (*.*)|*.*", Name + ".json");
        if (path is null) return;
        try
        {
            _store.ExportTo(template, path);
            Status = $"Exported to {path}.";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void Import()
    {
        var path = _dialogs.PromptOpenFile("Template files (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var imported = _store.ImportFrom(path);
            // Avoid clobbering an existing template silently.
            if (Templates.Any(t => string.Equals(t.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                && !_dialogs.Confirm("Import template", $"A template named “{imported.Name}” already exists. Overwrite it?", new[] { imported.Name }))
                return;

            _store.Save(imported, originalName: null);
            ReloadTemplates();
            SelectedTemplate = Templates.FirstOrDefault(t => t.Name == imported.Name);
            Status = $"Imported “{imported.Name}”.";
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
    }

    private UserTemplate BuildTemplate()
    {
        var template = new UserTemplate
        {
            Name = Name.Trim(),
            Description = Description,
            TargetOu = TargetOu.Trim(),
            UpnSuffix = UpnSuffix.Trim(),
            EnabledByDefault = EnabledByDefault,
            MustChangePasswordAtNextLogon = MustChangePassword,
            // Split the one combined bucket back into the template's typed lists by apply channel.
            GroupDns = Groups.Where(g => g.Channel == GroupChannel.OnPremAd && g.Dn is not null)
                             .Select(g => g.Dn!).ToList(),
            CloudGroups = Groups.Where(g => g.Channel == GroupChannel.EntraGraph && g.CloudId is not null)
                                .Select(g => new CloudGroupRef { Id = g.CloudId!, Name = g.Name }).ToList(),
            DistributionGroups = Groups.Where(g => g.Channel == GroupChannel.ExchangeOnline)
                                       .Select(g => new DistributionGroupRef { Id = g.CloudId ?? string.Empty, Name = g.Name, Smtp = g.Smtp ?? string.Empty }).ToList(),
        };
        foreach (var row in AttributeRows)
            if (!string.IsNullOrWhiteSpace(row.LdapName) && !HandledLdap.Contains(row.LdapName.Trim(), StringComparer.OrdinalIgnoreCase))
                template.AttributeDefaults[row.LdapName.Trim()] = row.Value;

        // Email / UPN (token-supported).
        if (!string.IsNullOrWhiteSpace(Email)) template.AttributeDefaults["mail"] = Email.Trim();
        if (!string.IsNullOrWhiteSpace(Upn)) template.AttributeDefaults["userPrincipalName"] = Upn.Trim();

        // Manager (DN) — a fixed default applied to every user created from this template.
        if (!string.IsNullOrWhiteSpace(_managerDn)) template.AttributeDefaults["manager"] = _managerDn;

        // Proxy addresses (token-supported, one per line).
        template.ProxyAddressPatterns = ProxyAddressesText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Country: store the three AD attributes together so created users get all of them.
        if (SelectedCountry is { } country && !string.IsNullOrEmpty(country.Alpha2))
        {
            template.AttributeDefaults["co"] = country.Name;
            template.AttributeDefaults["c"] = country.Alpha2;
            template.AttributeDefaults["countryCode"] = country.Numeric.ToString();
        }
        return template;
    }

    private void ReloadTemplates()
    {
        // Guard so the Clear()-driven selection change doesn't re-enter OnSelectedTemplateChanged (which,
        // when the form is dirty, would run its own reload and leave the list with duplicate entries).
        var wasLoading = _loadingForm;
        _loadingForm = true;
        try
        {
            Templates.Clear();
            foreach (var t in _store.LoadAll()) Templates.Add(t);
        }
        finally { _loadingForm = wasLoading; }
    }
}
