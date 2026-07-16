using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>A friendly-name/value pair shown in the new-user preview.</summary>
public sealed record PreviewRow(string Friendly, string LdapName, string Value);

/// <summary>
/// Template-driven new-user wizard: pick a template, enter per-user fields, preview the resolved
/// attributes (tokens like {first}.{last}), then create the user and add it to the template's groups.
/// </summary>
public partial class NewUserViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly ITemplateStore _store;
    private readonly IDialogService _dialogs;
    private readonly IGraphService _graph;
    private readonly CloudProvisioningService _cloudProvisioning;
    private readonly AppSettings _settings;

    public ObservableCollection<UserTemplate> Templates { get; } = new();
    public ObservableCollection<PreviewRow> Preview { get; } = new();

    /// <summary>Editable group memberships — auto-filled from the template, then the operator can add/remove.
    /// One unified picker feeds all three by channel: on-prem AD, Entra ID (Graph), and Exchange distribution.</summary>
    public ObservableCollection<TemplateCopyGroupRow> OnPremGroups { get; } = new();
    public ObservableCollection<TemplateCopyGroupRow> CloudGroups { get; } = new();
    public ObservableCollection<TemplateCopyGroupRow> DistributionGroups { get; } = new();

    /// <summary>Live progress lines for the create → sync → wait → add-cloud-groups sequence.</summary>
    public ObservableCollection<string> ProgressSteps { get; } = new();

    [ObservableProperty] private UserTemplate? _selectedTemplate;
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private string _middleName = string.Empty;
    [ObservableProperty] private string _initials = string.Empty;
    [ObservableProperty] private string _samOverride = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _generatedPassword = string.Empty;
    [ObservableProperty] private string _targetOu = string.Empty;
    [ObservableProperty] private string _upnSuffix = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _upn = string.Empty;
    [ObservableProperty] private string _proxyAddressesText = string.Empty;
    [ObservableProperty] private string _managerDisplay = "(none)";
    private string? _managerDn; // DN of the chosen manager (null = none)

    // Last auto-suggested values, so we only overwrite fields the user hasn't edited.
    private string _lastEmailSuggestion = string.Empty;
    private string _lastUpnSuggestion = string.Empty;
    private string _lastProxySuggestion = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _mustChangePassword;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    // Once the on-prem account exists, the Create button is hidden so a second create can't be attempted.
    [ObservableProperty] private bool _canCreate = true;

    // Set when the post-create Entra Connect sync didn't complete, so the operator can re-attempt it.
    [ObservableProperty] private bool _canRetrySync;

    // Cloud + distribution groups still to be applied — captured for a sync retry after a failure.
    private IReadOnlyList<CloudGroupRef> _pendingCloudGroups = Array.Empty<CloudGroupRef>();
    private IReadOnlyList<DistributionGroupRef> _pendingDistributionGroups = Array.Empty<DistributionGroupRef>();

    // Post-create Entra Connect sync (optional; mandatory when the template has cloud groups).
    [ObservableProperty] private bool _runEntraSync;
    [ObservableProperty] private string _entraConnectServer = string.Empty;
    [ObservableProperty] private bool _showProgress;

    // Sync credentials: by default the current Windows user runs the remote sync (WinRM); when that
    // isn't feasible the admin can supply an explicit account.
    [ObservableProperty] private bool _syncSpecifyCredentials;
    [ObservableProperty] private string _syncUsername = string.Empty;

    /// <summary>Sync-account password, set from the PasswordBox code-behind (PasswordBox can't be bound).</summary>
    public string SyncPassword { get; set; } = string.Empty;

    // Temporary Access Pass (Entra ID): a time-limited passcode for passwordless onboarding. Issued after the
    // user syncs to Entra, so — like cloud groups — selecting it forces a post-create sync. Default 24h, multi-use.
    [ObservableProperty] private bool _issueTap;
    [ObservableProperty] private int _tapLifetimeMinutes = 1440;
    [ObservableProperty] private bool _tapOneTimeUse;
    /// <summary>The issued pass — shown read-only with a Copy button; visible only once (never persisted).</summary>
    [ObservableProperty] private string _tapCode = string.Empty;

    /// <summary>True when cloud/Exchange groups or a Temporary Access Pass are selected — the post-create sync is then required.</summary>
    public bool SyncMandatory => CloudGroups.Count > 0 || DistributionGroups.Count > 0 || IssueTap;

    /// <summary>Section-header visibility for the New User group list.</summary>
    public bool HasCloudGroups => CloudGroups.Count > 0;
    public bool HasDistributionGroups => DistributionGroups.Count > 0;

    /// <summary>The sync checkbox is only toggleable when sync isn't mandatory.</summary>
    public bool SyncCheckboxEnabled => !SyncMandatory;

    // Batch-capture mode: the window is being used to configure a row for the bulk-create batch rather than
    // to create a user now. Password + Entra-sync sections are hidden (the batch generates a passphrase per
    // user and runs ONE sync), and the primary button becomes "Add to batch".
    [ObservableProperty] private bool _isBatchCapture;
    public bool IsNormalMode => !IsBatchCapture;
    public bool ShowCreateButton => IsNormalMode && CanCreate;
    public bool ShowRetryButton => IsNormalMode && CanRetrySync;

    /// <summary>The chosen manager's DN (null = none) — exposed so a batch capture can read it back.</summary>
    public string? ManagerDn => _managerDn;

    public bool Created { get; private set; }

    /// <summary>Raised after a user is successfully created (so a non-modal host can refresh).</summary>
    public event Action? UserCreated;

    /// <summary>Raised when a password is generated so the view can push it into the PasswordBox.</summary>
    public event Action<string>? PasswordGenerated;

    public NewUserViewModel(IDirectoryService directory, ITemplateStore store, IDialogService dialogs,
        IGraphService graph, CloudProvisioningService cloudProvisioning, AppSettings settings)
    {
        _directory = directory;
        _store = store;
        _dialogs = dialogs;
        _graph = graph;
        _cloudProvisioning = cloudProvisioning;
        _settings = settings;
        _entraConnectServer = settings.EntraConnectServer ?? string.Empty;
        // Selecting cloud / Exchange groups makes the sync mandatory; keep the dependent state in sync.
        void OnGroupsChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
        {
            OnPropertyChanged(nameof(SyncMandatory));
            OnPropertyChanged(nameof(SyncCheckboxEnabled));
            OnPropertyChanged(nameof(HasCloudGroups));
            OnPropertyChanged(nameof(HasDistributionGroups));
            if (SyncMandatory) RunEntraSync = true;
        }
        CloudGroups.CollectionChanged += OnGroupsChanged;
        DistributionGroups.CollectionChanged += OnGroupsChanged;
        ReloadTemplates();
    }

    public string? DefaultOu { private get; set; }

    partial void OnIsBatchCaptureChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNormalMode));
        OnPropertyChanged(nameof(ShowCreateButton));
        OnPropertyChanged(nameof(ShowRetryButton));
    }

    partial void OnCanCreateChanged(bool value) => OnPropertyChanged(nameof(ShowCreateButton));
    partial void OnCanRetrySyncChanged(bool value) => OnPropertyChanged(nameof(ShowRetryButton));

    /// <summary>Sets the manager from outside (used when seeding a batch row for editing).</summary>
    public void SetManager(string? dn, string display)
    {
        _managerDn = dn;
        ManagerDisplay = string.IsNullOrWhiteSpace(display) ? "(none)" : display;
        RefreshPreview();
    }

    /// <summary>
    /// Validates the current inputs for adding to a bulk batch (no directory I/O — the batch creates later).
    /// Returns false with a user-facing <paramref name="error"/> when something's missing.
    /// </summary>
    public bool ValidateForCapture(out string error)
    {
        error = string.Empty;
        if (SelectedTemplate is null) { error = "Select a template first."; return false; }
        if (string.IsNullOrWhiteSpace(TargetOu)) { error = "A target OU (DN) is required."; return false; }
        var attrs = BuildAttributes();
        if (!attrs.TryGetValue("cn", out var cn) || string.IsNullOrWhiteSpace(cn)) { error = "Could not derive a common name (cn)."; return false; }
        if (!attrs.ContainsKey("sAMAccountName")) { error = "Could not derive a logon name (sAMAccountName)."; return false; }
        var needsCloud = CloudGroups.Any(g => g.Include) || DistributionGroups.Any(g => g.Include) || IssueTap;
        if (needsCloud && string.IsNullOrWhiteSpace(Upn)) { error = "A routable UPN is required for cloud / Exchange groups or a Temporary Access Pass."; return false; }
        if (IssueTap && (TapLifetimeMinutes < 10 || TapLifetimeMinutes > 43200)) { error = "Temporary Access Pass lifetime must be 10–43200 minutes."; return false; }
        return true;
    }

    // Set while ReloadTemplates() rebuilds the template list so the transient SelectedTemplate churn
    // (Clear() → null, then re-assign a fresh instance of the same template) does NOT re-seed the form
    // and wipe the operator's in-progress edits. A genuine user template change still re-seeds.
    private bool _suppressReseed;

    public void ReloadTemplates()
    {
        var previous = SelectedTemplate?.Name;
        _suppressReseed = true;
        try
        {
            Templates.Clear();
            foreach (var t in _store.LoadAll()) Templates.Add(t);
            SelectedTemplate = Templates.FirstOrDefault(t => t.Name == previous) ?? Templates.FirstOrDefault();
        }
        finally { _suppressReseed = false; }

        // Re-seed only when the effective selection actually changed (first load, or the previously
        // selected template was deleted) — never on a background reload of the same template, which
        // would otherwise discard edited groups / OU / name fields each time the window reactivates.
        if (!string.Equals(SelectedTemplate?.Name, previous, StringComparison.OrdinalIgnoreCase))
            ReseedFromTemplate(SelectedTemplate);
    }

    partial void OnSelectedTemplateChanged(UserTemplate? value)
    {
        if (_suppressReseed) return; // background reload — keep the in-progress form intact
        ReseedFromTemplate(value);
    }

    /// <summary>Resets the editable form (groups, OU, manager, suggestions) to the template's defaults.</summary>
    private void ReseedFromTemplate(UserTemplate? value)
    {
        OnPremGroups.Clear();
        CloudGroups.Clear();
        DistributionGroups.Clear();
        if (value is not null)
        {
            TargetOu = string.IsNullOrWhiteSpace(value.TargetOu) ? (DefaultOu ?? string.Empty) : value.TargetOu;
            UpnSuffix = value.UpnSuffix;
            Enabled = value.EnabledByDefault;
            MustChangePassword = value.MustChangePasswordAtNextLogon;
            _managerDn = value.AttributeDefaults.TryGetValue("manager", out var mgr) && !string.IsNullOrWhiteSpace(mgr) ? mgr : null;
            ManagerDisplay = _managerDn is null ? "(none)" : NameResolver.RdnFallback(_managerDn);

            // Seed the editable group lists from the template (the operator can add/remove before creating).
            foreach (var dn in value.GroupDns)
                OnPremGroups.Add(new TemplateCopyGroupRow { Name = NameResolver.RdnFallback(dn), Id = dn, Channel = GroupChannel.OnPremAd });
            foreach (var g in value.CloudGroups)
                CloudGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.Id, Channel = GroupChannel.EntraGraph });
            foreach (var g in value.DistributionGroups)
                DistributionGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.Id, Channel = GroupChannel.ExchangeOnline, Smtp = g.Smtp });
        }
        // (CloudGroups.CollectionChanged keeps SyncMandatory/RunEntraSync in sync.)
        ApplySuggestions(force: true); // a new template resets the suggested email/UPN/proxies
        RefreshPreview();
    }

    partial void OnIssueTapChanged(bool value)
    {
        OnPropertyChanged(nameof(SyncMandatory));
        OnPropertyChanged(nameof(SyncCheckboxEnabled));
        if (value) RunEntraSync = true; // a TAP needs the user in Entra first → force the post-create sync
    }

    partial void OnFirstNameChanged(string value) { ApplySuggestions(force: false); RefreshPreview(); }
    partial void OnLastNameChanged(string value) { ApplySuggestions(force: false); RefreshPreview(); }
    partial void OnMiddleNameChanged(string value) { ApplySuggestions(force: false); RefreshPreview(); }
    partial void OnInitialsChanged(string value) { ApplySuggestions(force: false); RefreshPreview(); }
    partial void OnSamOverrideChanged(string value) { ApplySuggestions(force: false); RefreshPreview(); }
    partial void OnUpnSuffixChanged(string value) { ApplySuggestions(force: false); RefreshPreview(); }
    partial void OnEmailChanged(string value) => RefreshPreview();
    partial void OnUpnChanged(string value) => RefreshPreview();
    partial void OnProxyAddressesTextChanged(string value) => RefreshPreview();

    /// <summary>Snapshots the current entry fields into the shared builder's input.</summary>
    private UserAttributeBuilder.Input BuilderInput() => new()
    {
        Template = SelectedTemplate!,
        FirstName = FirstName, MiddleName = MiddleName, LastName = LastName, Initials = Initials,
        SamOverride = SamOverride, UpnSuffix = UpnSuffix,
        Email = Email, Upn = Upn, ManagerDn = _managerDn, ProxyAddressesText = ProxyAddressesText,
    };

    /// <summary>
    /// Recomputes suggested email / UPN / proxy values from the template and the entered name (via the
    /// shared <see cref="UserAttributeBuilder"/>). When not forcing, only fields the user hasn't manually
    /// changed are updated.
    /// </summary>
    private void ApplySuggestions(bool force)
    {
        if (SelectedTemplate is null) return;
        var s = UserAttributeBuilder.Suggest(BuilderInput());
        if (force || Email == _lastEmailSuggestion) { Email = s.Email; _lastEmailSuggestion = s.Email; }
        if (force || Upn == _lastUpnSuggestion) { Upn = s.Upn; _lastUpnSuggestion = s.Upn; }
        if (force || ProxyAddressesText == _lastProxySuggestion) { ProxyAddressesText = s.ProxyText; _lastProxySuggestion = s.ProxyText; }
    }

    private IReadOnlyList<string> ResolvedProxies() => UserAttributeBuilder.ResolveProxies(ProxyAddressesText);

    [RelayCommand]
    private void GeneratePassword()
    {
        var pwd = PassphraseGenerator.Generate();
        Password = pwd;
        GeneratedPassword = pwd;          // shown read-only so the admin can copy/relay it
        PasswordGenerated?.Invoke(pwd);   // mirror into the (unbindable) PasswordBox
    }

    [RelayCommand]
    private void ManageTemplates()
    {
        _dialogs.ShowTemplateEditor();
        ReloadTemplates();
    }

    [RelayCommand]
    private void BrowseOu()
    {
        var dn = _dialogs.PickContainer(TargetOu);
        if (dn is not null) TargetOu = dn;
    }

    [RelayCommand]
    private void PickManager()
    {
        var picked = _dialogs.PickObjects("Select manager", AdObjectType.User, multiSelect: false);
        if (picked is null || picked.Count == 0) return;
        _managerDn = picked[0].DistinguishedName;
        ManagerDisplay = picked[0].Name;
        RefreshPreview();
    }

    [RelayCommand]
    private void ClearManager()
    {
        _managerDn = null;
        ManagerDisplay = "(none)";
        RefreshPreview();
    }

    [RelayCommand]
    private void AddGroups()
    {
        // One picker spans on-prem AD, Entra ID (Graph), and Exchange Online distribution groups; route each
        // pick into the collection that matches its apply channel.
        var picked = _dialogs.PickGroupsHybrid("Add the new user to groups (on-prem + cloud + Exchange)");
        if (picked is null) return;
        foreach (var g in picked)
        {
            switch (g.Channel)
            {
                case GroupChannel.OnPremAd when g.Dn is not null:
                    if (OnPremGroups.All(x => !string.Equals(x.Id, g.Dn, StringComparison.OrdinalIgnoreCase)))
                        OnPremGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.Dn, Channel = GroupChannel.OnPremAd });
                    break;
                case GroupChannel.EntraGraph when g.CloudId is not null:
                    if (CloudGroups.All(x => !string.Equals(x.Id, g.CloudId, StringComparison.OrdinalIgnoreCase)))
                        CloudGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.CloudId, Channel = GroupChannel.EntraGraph });
                    break;
                case GroupChannel.ExchangeOnline:
                    if (DistributionGroups.All(x => !string.Equals(x.Id, g.CloudId ?? g.Smtp, StringComparison.OrdinalIgnoreCase)))
                        DistributionGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.CloudId ?? string.Empty, Channel = GroupChannel.ExchangeOnline, Smtp = g.Smtp });
                    break;
            }
        }
    }

    [RelayCommand]
    private void RemoveGroup(TemplateCopyGroupRow? row) { if (row is not null) OnPremGroups.Remove(row); }

    [RelayCommand]
    private void RemoveCloudGroup(TemplateCopyGroupRow? row) { if (row is not null) CloudGroups.Remove(row); }

    [RelayCommand]
    private void RemoveDistributionGroup(TemplateCopyGroupRow? row) { if (row is not null) DistributionGroups.Remove(row); }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (SelectedTemplate is null) { Status = "Select a template first."; return; }
        if (string.IsNullOrWhiteSpace(TargetOu)) { Status = "A target OU (DN) is required."; return; }

        var attributes = BuildAttributes();
        if (!attributes.TryGetValue("cn", out var cn) || string.IsNullOrWhiteSpace(cn)) { Status = "Could not derive a common name (cn)."; return; }
        if (!attributes.ContainsKey("sAMAccountName")) { Status = "Could not derive a logon name (sAMAccountName)."; return; }

        var cloudGroups = CloudGroups.Where(g => g.Include).Select(g => new CloudGroupRef { Id = g.Id, Name = g.Name }).ToList();
        var distributionGroups = DistributionGroups.Where(g => g.Include).Select(g => new DistributionGroupRef { Id = g.Id, Name = g.Name, Smtp = g.Smtp ?? string.Empty }).ToList();
        var selectedOnPremDns = OnPremGroups.Where(g => g.Include).Select(g => g.Id).ToList();
        var doSync = RunEntraSync || cloudGroups.Count > 0 || distributionGroups.Count > 0 || IssueTap;

        // Fail fast on cloud prerequisites BEFORE creating anything on-prem.
        if (cloudGroups.Count > 0 || distributionGroups.Count > 0 || IssueTap)
        {
            if (!_graph.IsSignedIn) { Status = "Sign in to Entra ID (File ▸ Settings ▸ Cloud) before creating a user with cloud / Exchange groups or a Temporary Access Pass."; return; }
            if (string.IsNullOrWhiteSpace(Upn)) { Status = "A routable UPN is required to match the user in Entra ID for cloud / Exchange groups or a Temporary Access Pass."; return; }
        }
        if (IssueTap && (TapLifetimeMinutes < 10 || TapLifetimeMinutes > 43200))
        {
            Status = "Temporary Access Pass lifetime must be between 10 and 43200 minutes (30 days).";
            return;
        }
        if (doSync && string.IsNullOrWhiteSpace(EntraConnectServer))
        {
            Status = "Enter the Entra Connect server to run the post-create sync.";
            return;
        }
        if (doSync && SyncSpecifyCredentials && string.IsNullOrWhiteSpace(SyncUsername))
        {
            Status = "Enter the sync-account username, or clear “Use a specific account” to run as the current Windows user.";
            return;
        }

        // Reject a duplicate logon name up front (AD would also reject it, but with a cryptic error).
        var sam = attributes["sAMAccountName"];
        Status = "Checking logon name…";
        try
        {
            if ((await _directory.FindExistingSamAccountNamesAsync(new[] { sam })).Contains(sam))
            {
                Status = $"The logon name “{sam}” already exists — choose a different one.";
                _dialogs.Alert("Logon name already exists",
                    $"A user or object with the logon name (sAMAccountName) “{sam}” already exists in the directory.\n\n" +
                    "Change the logon name and try again.");
                return;
            }
        }
        catch (Exception ex)
        {
            // Couldn't verify (transient/search error) — let AD enforce uniqueness on create rather than blocking.
            AppLog.Instance.Warn($"Could not pre-check logon-name uniqueness for '{sam}': {DirectoryService.Friendly(ex)}");
        }

        // Validate the chosen groups still exist; missing ones are reported and skipped (not a hard failure).
        Status = "Checking groups…";
        var (onPremGroups, missingOnPrem) = await PartitionExistingOnPremGroupsAsync(selectedOnPremDns);
        var (validCloudGroups, missingCloud) = cloudGroups.Count > 0
            ? await PartitionExistingCloudGroupsAsync(cloudGroups)
            : (new List<CloudGroupRef>(), new List<string>());

        var lines = new List<string> { $"Create user in: {TargetOu}" };
        lines.AddRange(Preview.Select(p => $"{p.Friendly}: {p.Value}"));
        if (onPremGroups.Count > 0) lines.Add($"Add to {onPremGroups.Count} on-prem group(s)");
        if (validCloudGroups.Count > 0) lines.Add($"Add to {validCloudGroups.Count} cloud group(s) after an Entra Connect sync");
        if (distributionGroups.Count > 0) lines.Add($"Add to {distributionGroups.Count} Exchange distribution group(s) after an Entra Connect sync");
        if (validCloudGroups.Count == 0 && distributionGroups.Count == 0 && doSync)
            lines.Add($"Run an Entra Connect delta sync on {EntraConnectServer} after creating");
        if (IssueTap) lines.Add($"Issue a Temporary Access Pass (valid {TapLifetimeMinutes} min, {(TapOneTimeUse ? "one-time use" : "multi-use")}) once the user syncs");
        if (missingOnPrem.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("⚠ These on-prem groups in the template no longer exist and will be SKIPPED:");
            lines.AddRange(missingOnPrem.Select(m => "   • " + m));
        }
        if (missingCloud.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("⚠ These cloud groups in the template no longer exist and will be SKIPPED:");
            lines.AddRange(missingCloud.Select(m => "   • " + m));
        }

        if (!_dialogs.Confirm("Create user", $"Create “{cn}”?", lines))
            return;

        IsBusy = true;
        ProgressSteps.Clear();
        ShowProgress = true; // always show the outcome log; the window no longer auto-closes
        Status = "Creating…";
        try
        {
            if (missingOnPrem.Count > 0) Step($"⚠ Skipping {missingOnPrem.Count} missing on-prem group(s): {string.Join(", ", missingOnPrem)}");
            if (missingCloud.Count > 0) Step($"⚠ Skipping {missingCloud.Count} missing cloud group(s): {string.Join(", ", missingCloud)}");

            var passwordRequested = !string.IsNullOrEmpty(Password);
            var result = await _directory.CreateUserAsync(
                TargetOu, attributes, onPremGroups,
                passwordRequested ? Password : null, Enabled, MustChangePassword,
                ResolvedProxies());
            Created = true;
            CanCreate = false; // on-prem account now exists — never offer Create again for this window
            UserCreated?.Invoke();
            Step($"✓ Created {result.DistinguishedName}");

            if (passwordRequested)
            {
                if (result.PasswordSet)
                {
                    Step("✓ Password set.");
                }
                else
                {
                    // Both ADSI SetPassword and the unicodePwd fallback failed — the channel isn't encrypted.
                    Step("⚠ Password was NOT set — account left disabled. The connection isn't encrypted (no LDAPS and the bind didn't negotiate Kerberos sign+seal).");
                    _dialogs.Alert("Password not set",
                        $"{result.DistinguishedName} was created, but the password could NOT be set, so the account is DISABLED.\n\n" +
                        "The connection isn't encrypted (no LDAPS, and the bind didn't negotiate Kerberos sign+seal). " +
                        "Reconnect securely, then reset the password and enable the account.");
                }
            }

            if (!doSync)
            {
                Status = $"Created {result.DistinguishedName}.";
                return; // outcome stays visible in the progress panel; the user closes the window
            }

            await RunPostCreateCloudAsync(validCloudGroups, distributionGroups);
        }
        catch (Exception ex)
        {
            Status = DirectoryService.Friendly(ex);
            Step("✗ " + Status);
            // The on-prem account was created but the cloud sync step threw — let the operator re-attempt the sync.
            if (Created && doSync)
            {
                _pendingCloudGroups = validCloudGroups;
                _pendingDistributionGroups = distributionGroups;
                CanRetrySync = true;
                _dialogs.Alert("Entra Connect sync failed",
                    $"The on-prem user account was created, but the Entra Connect sync step failed:\n\n{Status}\n\n" +
                    "You can retry the sync, or close this window and run a sync later.");
            }
            else if (!ShowProgress) _dialogs.Alert("Create failed", Status);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Runs the Entra Connect delta sync, waits for the new user to appear in Entra, then adds cloud
    /// (Graph) groups and Exchange Online distribution groups.</summary>
    private async Task RunPostCreateCloudAsync(IReadOnlyList<CloudGroupRef> cloudGroups, IReadOnlyList<DistributionGroupRef> distributionGroups)
    {
        // A fresh attempt: clear any prior retry offer; it's re-enabled below if this attempt doesn't complete.
        CanRetrySync = false;

        // 1. Kick off a delta sync on the Entra Connect server (current Windows identity, or a supplied account, over WinRM).
        var sync = await _cloudProvisioning.RunDeltaSyncAsync(
            EntraConnectServer, SyncSpecifyCredentials ? SyncUsername : null,
            SyncSpecifyCredentials ? SyncPassword : null, _settings, Step);
        if (!sync.Success)
        {
            // The on-prem account is already created — surface the sync failure but keep the window open so the
            // operator can re-attempt the sync (button) or close.
            var error = sync.Output.Replace(Environment.NewLine, " ").Trim();
            Step("✗ Entra Connect sync failed: " + error);
            Status = "The on-prem account was created, but the Entra Connect sync failed. Retry the sync or close.";
            _pendingCloudGroups = cloudGroups;
            _pendingDistributionGroups = distributionGroups;
            CanRetrySync = true;
            _dialogs.Alert("Entra Connect sync failed",
                $"The on-prem user account was created successfully, but the Entra Connect delta sync failed:\n\n{sync.Output}\n\n" +
                "You can retry the sync, or close this window and run a sync later.");
            return;
        }
        Step("✓ Delta sync started.");

        // Cloud/Exchange groups and/or a Temporary Access Pass all need the synced user; otherwise the sync was the whole job.
        bool needCloudUser = cloudGroups.Count > 0 || distributionGroups.Count > 0 || IssueTap;
        if (!needCloudUser) { Status = "User created and a delta sync was started."; Step("Done."); return; }

        // 2. Wait for the synced user to show up in Entra (initial settle, then poll).
        Step("• Waiting for the user to appear in Entra ID (this can take a minute)…");
        var cloudUser = await _cloudProvisioning.PollForCloudUserAsync(Upn.Trim(), Step);
        if (cloudUser is null)
        {
            Status = "User created, but it hadn't synced to Entra ID in time — cloud groups / Temporary Access Pass were NOT applied.";
            Step("✗ Not found in Entra ID within the wait window. Retry the sync, or run one later and add cloud / Exchange groups / issue a TAP from the user's Cloud tab.");
            _pendingCloudGroups = cloudGroups;
            _pendingDistributionGroups = distributionGroups;
            CanRetrySync = true; // let the operator re-run the sync without recreating the user
            return;
        }
        Step("✓ Found in Entra ID.");

        // 3a. Add the user to each Entra (Graph) cloud group.
        int ok = 0, failed = 0;
        if (cloudGroups.Count > 0)
        {
            Step("• Adding cloud groups…");
            (ok, failed) = await _cloudProvisioning.AddUserToGroupsAsync(cloudUser.Id, cloudGroups, Step);
        }

        // 3b. Add the user to each Exchange Online distribution group (Graph can't; member identity = the UPN).
        int dok = 0, dfailed = 0;
        if (distributionGroups.Count > 0)
        {
            Step("• Adding Exchange distribution groups…");
            (dok, dfailed) = await _cloudProvisioning.AddUserToDistributionGroupsAsync(Upn.Trim(), distributionGroups, Step);
        }

        // 4. Issue a Temporary Access Pass, if requested. The pass is captured into TapCode (shown once + copyable).
        if (IssueTap)
        {
            Step("• Issuing a Temporary Access Pass…");
            var tap = await _cloudProvisioning.IssueTemporaryAccessPassAsync(cloudUser.Id, TapLifetimeMinutes, TapOneTimeUse, Step);
            if (tap is { Pass.Length: > 0 }) TapCode = tap.Pass;
        }

        var summary = "User created";
        if (cloudGroups.Count > 0) summary += $"; added to {ok} cloud group(s)" + (failed > 0 ? $", {failed} failed" : "");
        if (distributionGroups.Count > 0) summary += $"; added to {dok} distribution group(s)" + (dfailed > 0 ? $", {dfailed} failed" : "");
        if (TapCode.Length > 0) summary += "; Temporary Access Pass issued (copy it now)";
        Status = summary + ".";
        Step("Done.");
    }

    /// <summary>
    /// Re-attempts the post-create Entra Connect sync (and any pending cloud-group adds) after a failure.
    /// The on-prem account already exists, so this never recreates the user.
    /// </summary>
    [RelayCommand]
    private async Task RetrySyncAsync()
    {
        if (!Created) return;
        IsBusy = true;
        Step("• Retrying Entra Connect sync…");
        try
        {
            await RunPostCreateCloudAsync(_pendingCloudGroups, _pendingDistributionGroups);
        }
        catch (Exception ex)
        {
            Status = DirectoryService.Friendly(ex);
            Step("✗ " + Status);
            CanRetrySync = true; // leave the door open for another attempt
        }
        finally { IsBusy = false; }
    }

    private void Step(string text)
    {
        ProgressSteps.Add(text);
        Status = text;
    }

    /// <summary>Splits the template's on-prem group DNs into those that still exist and the (display names of) missing ones.</summary>
    private async Task<(List<string> Existing, List<string> MissingNames)> PartitionExistingOnPremGroupsAsync(IReadOnlyList<string> dns)
    {
        var existing = new List<string>();
        var missing = new List<string>();
        foreach (var dn in dns)
        {
            bool ok;
            try { ok = await _directory.ExistsAsync(dn); }
            catch { ok = true; } // a transient lookup failure shouldn't silently drop the group — let the add try
            if (ok) existing.Add(dn);
            else missing.Add(NameResolver.RdnFallback(dn));
        }
        return (existing, missing);
    }

    /// <summary>Splits the template's cloud groups into those that still exist and the names of the missing ones.</summary>
    private async Task<(List<CloudGroupRef> Existing, List<string> MissingNames)> PartitionExistingCloudGroupsAsync(IReadOnlyList<CloudGroupRef> groups)
    {
        var existing = new List<CloudGroupRef>();
        var missing = new List<string>();
        foreach (var g in groups)
        {
            bool ok;
            try { ok = await _graph.GroupExistsAsync(g.Id); }
            catch { ok = true; } // don't pre-skip on a transient/permission error; the add step will report it
            if (ok) existing.Add(g);
            else missing.Add(string.IsNullOrWhiteSpace(g.Name) ? g.Id : g.Name);
        }
        return (existing, missing);
    }

    private void RefreshPreview()
    {
        Preview.Clear();
        foreach (var (ldap, value) in BuildAttributes())
            Preview.Add(new PreviewRow(AttributeCatalog.Friendly(ldap), ldap, value));
        foreach (var proxy in ResolvedProxies())
            Preview.Add(new PreviewRow("Proxy address", "proxyAddresses", proxy));
    }

    /// <summary>Resolves the template defaults + computed cn/sam/upn into a concrete attribute set
    /// (delegated to the shared <see cref="UserAttributeBuilder"/>).</summary>
    private IReadOnlyDictionary<string, string> BuildAttributes() =>
        SelectedTemplate is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : UserAttributeBuilder.Build(BuilderInput()).Attributes;
}
