using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// One user in the bulk-create batch. Rows are configured through the standard New User window (capture
/// mode) or seeded from a CSV import, so this carries the full per-user picture — identity fields, target
/// OU, proxies, on-prem + cloud groups, manager, enabled state, and TAP options — plus summary properties
/// for the read-only batch grid and the live result fields filled in as the batch runs.
/// </summary>
public partial class BulkCreateRowViewModel : ObservableObject
{
    public UserTemplate? Template { get; set; }
    public string UpnSuffix { get; set; } = string.Empty;

    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _middleName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private string _initials = string.Empty;
    [ObservableProperty] private string _samOverride = string.Empty;
    [ObservableProperty] private string _upn = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _targetOu = string.Empty;
    [ObservableProperty] private string _proxyAddressesText = string.Empty;
    [ObservableProperty] private bool _enabled = true;

    public string? ManagerDn { get; set; }
    [ObservableProperty] private string _managerDisplay = "(none)";

    [ObservableProperty] private bool _issueTap;
    [ObservableProperty] private int _tapLifetimeMinutes = 1440;
    [ObservableProperty] private bool _tapOneTimeUse;

    /// <summary>Per-row on-prem groups (Name + DN in <c>Id</c>).</summary>
    public ObservableCollection<TemplateCopyGroupRow> OnPremGroups { get; } = new();
    /// <summary>Per-row Entra ID groups.</summary>
    public ObservableCollection<CloudGroupRef> CloudGroups { get; } = new();

    /// <summary>Extra attribute overrides keyed by lDAPDisplayName (from a CSV import).</summary>
    public Dictionary<string, string> AttributeOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Live result (filled in during/after the run).
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _generatedPassword = string.Empty;
    [ObservableProperty] private string _tapCode = string.Empty;

    public BulkCreateRowViewModel()
    {
        OnPremGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(OnPremGroupsSummary));
        CloudGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CloudGroupsSummary));
    }

    /// <summary>Computed logon name preview for this row.</summary>
    public string Sam => UserAttributeBuilder.ComputeSam(ToInput());

    /// <summary>Friendly display name for the grid (full name, falling back to the logon name).</summary>
    public string DisplayName
    {
        get
        {
            var full = $"{FirstName} {LastName}".Trim();
            return full.Length > 0 ? full : Sam;
        }
    }

    public string OuRdn => string.IsNullOrWhiteSpace(TargetOu) ? "(batch OU)" : NameResolver.RdnFallback(TargetOu);
    public string OnPremGroupsSummary => OnPremGroups.Count == 0 ? "(none)" : $"{OnPremGroups.Count} group(s)";
    public string CloudGroupsSummary => CloudGroups.Count == 0 ? "(none)" : string.Join(", ", CloudGroups.Select(g => g.Name));
    public string TapText => IssueTap ? $"{TapLifetimeMinutes} min" : string.Empty;

    /// <summary>True when this row has no identity data at all (a blank trailing row is simply skipped).</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName)
        && string.IsNullOrWhiteSpace(SamOverride);

    public IReadOnlyList<string> OnPremGroupDns => OnPremGroups.Select(g => g.Id).ToList();

    public UserAttributeBuilder.Input ToInput() => new()
    {
        Template = Template ?? new UserTemplate(),
        FirstName = FirstName, MiddleName = MiddleName, LastName = LastName, Initials = Initials,
        SamOverride = SamOverride, UpnSuffix = UpnSuffix, Email = Email, Upn = Upn, ManagerDn = ManagerDn,
        ProxyAddressesText = ProxyAddressesText,
    };

    partial void OnFirstNameChanged(string value) => RefreshDerived();
    partial void OnMiddleNameChanged(string value) => RefreshDerived();
    partial void OnLastNameChanged(string value) => RefreshDerived();
    partial void OnInitialsChanged(string value) => RefreshDerived();
    partial void OnSamOverrideChanged(string value) => RefreshDerived();
    partial void OnTargetOuChanged(string value) => OnPropertyChanged(nameof(OuRdn));
    partial void OnIssueTapChanged(bool value) => OnPropertyChanged(nameof(TapText));
    partial void OnTapLifetimeMinutesChanged(int value) => OnPropertyChanged(nameof(TapText));

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(Sam));
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>Seeds a row from a CSV import (template supplies OU/groups/enabled defaults).</summary>
    public static BulkCreateRowViewModel FromImport(ImportedUserRow src, UserTemplate? template, string upnSuffix, string? defaultOu)
    {
        var row = new BulkCreateRowViewModel
        {
            Template = template,
            UpnSuffix = upnSuffix,
            FirstName = src.FirstName,
            MiddleName = src.MiddleName,
            LastName = src.LastName,
            Initials = src.Initials,
            SamOverride = src.SamOverride,
            Upn = src.Upn,
            Email = src.Email,
            ManagerDn = src.ManagerDn,
            ManagerDisplay = string.IsNullOrWhiteSpace(src.ManagerDisplay) ? "(none)" : src.ManagerDisplay,
            IssueTap = src.IssueTap,
            TargetOu = string.IsNullOrWhiteSpace(template?.TargetOu) ? (defaultOu ?? string.Empty) : template!.TargetOu,
            Enabled = template?.EnabledByDefault ?? true,
        };
        foreach (var g in src.CloudGroups) row.CloudGroups.Add(g);
        if (row.CloudGroups.Count == 0 && template is not null)
            foreach (var g in template.CloudGroups) row.CloudGroups.Add(new CloudGroupRef { Id = g.Id, Name = g.Name });
        if (template is not null)
            foreach (var dn in template.GroupDns)
                row.OnPremGroups.Add(new TemplateCopyGroupRow { Name = NameResolver.RdnFallback(dn), Id = dn });
        foreach (var (k, v) in src.AttributeOverrides) row.AttributeOverrides[k] = v;
        return row;
    }
}
