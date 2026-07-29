using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Creates a new group under a chosen OU/container: name, logon name (auto-derived from the name until the
/// operator edits it), scope + category, an optional description and initial members, and the "protect from
/// accidental deletion" flag — on by default, matching the New OU dialog.
/// </summary>
public partial class NewGroupViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private bool _creating;              // guards against a double-submit while the async create is in flight
    private string _lastSamSuggestion = string.Empty; // only overwrite a logon name the operator hasn't edited

    /// <summary>The OU/container the group is created in. Pre-filled from the tree when available, and always
    /// changeable via Browse… so File ▸ New Group works without first selecting a container.</summary>
    [ObservableProperty] private string _parentDn = string.Empty;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _samAccountName = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private GroupScope _scope = GroupScope.Global;
    [ObservableProperty] private GroupCategory _category = GroupCategory.Security;
    [ObservableProperty] private bool _protectFromDeletion = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Objects to add as members once the group exists (optional).</summary>
    public ObservableCollection<GroupMemberRow> Members { get; } = new();

    public IReadOnlyList<OptionItem<GroupScope>> ScopeOptions { get; } = new[]
    {
        new OptionItem<GroupScope>(GroupScope.Global, "Global"),
        new OptionItem<GroupScope>(GroupScope.DomainLocal, "Domain local"),
        new OptionItem<GroupScope>(GroupScope.Universal, "Universal"),
    };

    public IReadOnlyList<OptionItem<GroupCategory>> CategoryOptions { get; } = new[]
    {
        new OptionItem<GroupCategory>(GroupCategory.Security, "Security"),
        new OptionItem<GroupCategory>(GroupCategory.Distribution, "Distribution"),
    };

    /// <summary>The new group's distinguished name once created; null until then.</summary>
    public string? CreatedDistinguishedName { get; private set; }

    /// <summary>Raised after a successful create so the (modal) window can close.</summary>
    public event Action? Created;

    public NewGroupViewModel(IDirectoryService directory, IDialogService dialogs, string? parentDn)
    {
        _directory = directory;
        _dialogs = dialogs;
        _parentDn = parentDn ?? string.Empty;
    }

    /// <summary>Browses the directory tree for the OU/container to create the group in.</summary>
    [RelayCommand]
    private void BrowseParent()
    {
        var dn = _dialogs.PickContainer(string.IsNullOrWhiteSpace(ParentDn) ? null : ParentDn);
        if (dn is not null) ParentDn = dn;
    }

    /// <summary>Keeps the logon name in step with the name until the operator types their own.</summary>
    partial void OnNameChanged(string value)
    {
        if (!string.Equals(SamAccountName, _lastSamSuggestion, StringComparison.Ordinal)) return; // manually edited
        _lastSamSuggestion = DeriveSam(value);
        SamAccountName = _lastSamSuggestion;
    }

    /// <summary>
    /// Derives a group's pre-Windows-2000 logon name from its name the way ADUC does: keep the name as typed —
    /// case, spaces and hyphens are all legal in a group's sAMAccountName — and drop only the characters AD
    /// forbids. Deliberately NOT <see cref="UserAttributeBuilder.SanitizeSam"/>, which lowercases and strips
    /// hyphens/spaces to build a PERSON's logon name ("IT-Admins" would become "itadmins").
    /// </summary>
    private static string DeriveSam(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        const string forbidden = "\"/\\[]:;|=,+*?<>";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            if (forbidden.IndexOf(c) < 0 && !char.IsControl(c)) sb.Append(c);
        // AD rejects a logon name that ends with a period or is only whitespace.
        return sb.ToString().Trim().TrimEnd('.').Trim();
    }

    [RelayCommand]
    private void AddMembers()
    {
        var picked = _dialogs.PickObjects("Add members to the new group", AdObjectType.Unknown, multiSelect: true);
        if (picked is null) return;
        foreach (var p in picked)
            if (Members.All(m => !string.Equals(m.Dn, p.DistinguishedName, StringComparison.OrdinalIgnoreCase)))
                Members.Add(new GroupMemberRow(p.Name, p.DistinguishedName));
    }

    [RelayCommand]
    private void RemoveMember(GroupMemberRow? row)
    {
        if (row is not null) Members.Remove(row);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var parent = ParentDn.Trim();
        if (string.IsNullOrWhiteSpace(parent)) { Status = "Choose the OU or container to create the group in."; return; }
        var groupName = Name.Trim();
        if (string.IsNullOrWhiteSpace(groupName)) { Status = "Enter a name for the group."; return; }
        var sam = SamAccountName.Trim();
        if (string.IsNullOrWhiteSpace(sam)) { Status = "Enter a logon name (sAMAccountName) for the group."; return; }
        if (_creating) return;
        _creating = true;
        IsBusy = true;
        Status = "Creating…";
        try
        {
            // sAMAccountName is unique across every security principal in the domain, so reject a duplicate up
            // front rather than letting the DC return a cryptic constraint violation.
            try
            {
                if ((await _directory.FindExistingSamAccountNamesAsync(new[] { sam })).Contains(sam))
                {
                    Status = $"The logon name “{sam}” is already in use — choose a different one.";
                    return;
                }
            }
            catch (Exception ex)
            {
                // A transient lookup failure shouldn't block the create; AD still enforces uniqueness.
                AppLog.Instance.Warn($"Could not pre-check group logon name '{sam}': {DirectoryService.Friendly(ex)}");
            }

            var result = await _directory.CreateGroupAsync(
                parent, groupName, sam, Scope, Category, Description,
                managedByDn: null, ProtectFromDeletion, Members.Select(m => m.Dn).ToList());

            CreatedDistinguishedName = result.DistinguishedName; // the group exists; warnings below are non-fatal
            if (result.Warnings.Count > 0)
                _dialogs.Alert("Group created — some steps didn't complete",
                    $"“{groupName}” was created, but:\n\n• " + string.Join("\n• ", result.Warnings) +
                    "\n\nYou can finish these from the group's Properties.");
            Created?.Invoke();
        }
        catch (Exception ex) { Status = "Create failed: " + DirectoryService.Friendly(ex); }
        finally { IsBusy = false; _creating = false; }
    }
}
