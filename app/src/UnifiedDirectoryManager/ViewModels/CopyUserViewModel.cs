using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Creates a new user from an existing one — like the New User wizard but with no template to pick. Naming
/// conventions (sAM / UPN / email / display name) come from the "Standard User" template and autofill from the
/// entered first/last name. A fixed set of attributes is copied from the source into EDITABLE fields (address
/// details, office, title, department, manager) plus its on-prem and cloud group memberships (all editable).
/// </summary>
public partial class CopyUserViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly ITemplateStore _store;
    private readonly IDialogService _dialogs;
    private readonly IGraphService _graph;
    private readonly CloudProvisioningService _cloudProvisioning;
    private readonly AppSettings _settings;
    private readonly string _sourceDn;

    // Identity (cleared; autofilled from first/last).
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private string _middleName = string.Empty;
    [ObservableProperty] private string _initials = string.Empty;

    /// <summary>
    /// Employee ID for the NEW user. Deliberately left blank rather than copied: it identifies a person,
    /// so carrying the source user's across would quietly give two people the same one. The field is here
    /// so the real value can be typed while the account is being made, instead of afterwards.
    /// </summary>
    [ObservableProperty] private string _employeeId = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _samAccountName = string.Empty;
    [ObservableProperty] private string _upn = string.Empty;
    [ObservableProperty] private string _email = string.Empty;

    // Naming-convention patterns from the chosen template (fall back to sensible defaults). The operator
    // picks which template's naming convention to apply via SelectedNamingTemplate (defaults to "Standard User").
    private string _samPattern = "{first}.{last}";
    private string _displayPattern = "{first} {last}";
    private string _upnPattern = "{sam}@{upnSuffix}";
    private string _mailPattern = "{sam}@{upnSuffix}";
    private string _upnSuffix = string.Empty;
    private string _sourceUpnDomain = string.Empty; // source user's UPN domain — fallback suffix when a template has none
    private string _lastSam = string.Empty, _lastUpn = string.Empty, _lastEmail = string.Empty, _lastDisplay = string.Empty;

    /// <summary>The templates the operator can choose a naming convention from (sAM / UPN / email / display patterns).</summary>
    public ObservableCollection<UserTemplate> NamingTemplates { get; } = new();
    [ObservableProperty] private UserTemplate? _selectedNamingTemplate;

    // Copied, editable detail fields (prefilled from the source user).
    [ObservableProperty] private string _street = string.Empty;
    [ObservableProperty] private string _city = string.Empty;
    [ObservableProperty] private string _state = string.Empty;
    [ObservableProperty] private string _postalCode = string.Empty;
    [ObservableProperty] private string _office = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _department = string.Empty;
    // Country/region: the friendly pick sets co (name) + c (two-letter) + countryCode (numeric) together.
    public IReadOnlyList<CountryInfo> Countries => Services.Countries.All;
    [ObservableProperty] private CountryInfo? _selectedCountry;

    [ObservableProperty] private string _targetOu = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _generatedPassword = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _mustChangePassword;
    [ObservableProperty] private string _managerDisplay = "(none)";
    private string? _managerDn;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    // Cloud-group copying: needs a post-create Entra Connect sync, then waits for the user to appear.
    [ObservableProperty] private bool _runEntraSync;
    [ObservableProperty] private string _entraConnectServer = string.Empty;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _hasCloudGroups;
    [ObservableProperty] private bool _syncSpecifyCredentials;
    [ObservableProperty] private string _syncUsername = string.Empty;
    /// <summary>Sync-account password, set from the PasswordBox code-behind.</summary>
    public string SyncPassword { get; set; } = string.Empty;

    // Temporary Access Pass (Entra ID): issued after the user syncs to Entra (so it forces a post-create sync,
    // like cloud groups). Default 24h, multi-use.
    [ObservableProperty] private bool _issueTap;
    [ObservableProperty] private int _tapLifetimeMinutes = 1440;
    [ObservableProperty] private bool _tapOneTimeUse;
    /// <summary>The issued pass — shown read-only with a Copy button; visible only once (never persisted).</summary>
    [ObservableProperty] private string _tapCode = string.Empty;

    /// <summary>
    /// True once there is a password or access pass to capture, which reveals the panel that shows them.
    /// Both are held only in memory, so the panel is the last chance to copy either.
    /// </summary>
    public bool HasSecrets => GeneratedPassword.Length > 0 || TapCode.Length > 0;

    // Without these the panel never appears: HasSecrets is computed, so it has to be told.
    partial void OnGeneratedPasswordChanged(string value) => OnPropertyChanged(nameof(HasSecrets));
    partial void OnTapCodeChanged(string value) => OnPropertyChanged(nameof(HasSecrets));

    public ObservableCollection<TemplateCopyGroupRow> Groups { get; } = new();       // on-prem memberships to copy
    public ObservableCollection<TemplateCopyGroupRow> CloudGroups { get; } = new();  // cloud-only memberships to copy
    public ObservableCollection<string> ProgressSteps { get; } = new();

    public bool Created { get; private set; }
    public event Action? UserCreated;
    public event Action<string>? PasswordGenerated;

    public CopyUserViewModel(IDirectoryService directory, ITemplateStore store, IDialogService dialogs,
        IGraphService graph, CloudProvisioningService cloudProvisioning, AppSettings settings, string sourceDn)
    {
        _directory = directory;
        _store = store;
        _dialogs = dialogs;
        _graph = graph;
        _cloudProvisioning = cloudProvisioning;
        _settings = settings;
        _sourceDn = sourceDn;
        _entraConnectServer = settings.EntraConnectServer ?? string.Empty;

        // Offer every template as a naming-convention choice; default to "Standard User" (the prior behavior).
        try { foreach (var t in _store.LoadAll()) NamingTemplates.Add(t); }
        catch (Exception ex) { AppLog.Instance.Warn("Could not load templates for the Copy User naming picker: " + ex.Message); }
        SelectedNamingTemplate = NamingTemplates.FirstOrDefault(t => string.Equals(t.Name, "Standard User", StringComparison.OrdinalIgnoreCase))
            ?? NamingTemplates.FirstOrDefault();
    }

    /// <summary>Switching the naming template re-applies its sAM / UPN / email / display patterns to the entered name.</summary>
    partial void OnSelectedNamingTemplateChanged(UserTemplate? value)
    {
        ApplyNamingFromTemplate(value);
        ApplySuggestions(force: true); // the operator explicitly chose this convention — apply it even over autofilled values
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Loading source user…";
        try
        {
            var attrs = await _directory.LoadObjectAsync(_sourceDn);
            var map = attrs.GroupBy(a => a.LdapName, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            TargetOu = DirectoryService.ParentDn(_sourceDn);
            // If the chosen naming template didn't carry a UPN suffix, fall back to the source user's UPN domain
            // (remembered so the fallback still applies if the operator switches templates after load).
            _sourceUpnDomain = DomainOf(map, "userPrincipalName");
            if (string.IsNullOrWhiteSpace(_upnSuffix)) { _upnSuffix = _sourceUpnDomain; ApplySuggestions(force: true); }

            // Manager copied (editable via the picker).
            if (map.TryGetValue("manager", out var mgr) && mgr.RawValues.Count > 0 && !string.IsNullOrWhiteSpace(mgr.RawValues[0]))
            {
                _managerDn = mgr.RawValues[0];
                ManagerDisplay = mgr.DisplayValues.Count > 0 ? mgr.DisplayValues[0] : NameResolver.RdnFallback(_managerDn);
            }

            // Copy ONLY the whitelisted detail attributes (address / office / title / department) into editable
            // fields. Country/region is a single friendly pick that drives co + c + countryCode on create.
            Street = Raw(map, "streetAddress");
            City = Raw(map, "l");
            State = Raw(map, "st");
            PostalCode = Raw(map, "postalCode");
            Office = Raw(map, "physicalDeliveryOfficeName");
            Title = Raw(map, "title");
            Department = Raw(map, "department");
            SelectedCountry = Services.Countries.ByAlpha2(Raw(map, "c"))
                ?? Services.Countries.ByName(Raw(map, "co"))
                ?? Services.Countries.NotSet;

            // On-prem group memberships (checkable; copied to the new user).
            if (map.TryGetValue("memberOf", out var memberOf))
                for (int i = 0; i < memberOf.DisplayValues.Count; i++)
                {
                    var dn = i < memberOf.RawValues.Count ? memberOf.RawValues[i] : memberOf.DisplayValues[i];
                    Groups.Add(new TemplateCopyGroupRow { Name = memberOf.DisplayValues[i], Id = dn });
                }

            // Cloud-only group memberships (best-effort, when signed in). Synced groups are excluded — they
            // come across via the on-prem groups above. Copying these needs a post-create Entra Connect sync.
            //
            // A failed read has to be visible: the copy form otherwise presents a short list as the source
            // user's full membership.
            var cloudUnread = false;
            if (_graph.IsSignedIn && map.TryGetValue("userPrincipalName", out var srcUpn) && srcUpn.RawValues.Count > 0)
            {
                try
                {
                    var cloud = await _graph.GetUserGroupsByUpnAsync(srcUpn.RawValues[0]);
                    foreach (var g in cloud.Where(g => !g.IsSynced))
                    {
                        // Distribution lists / mail-enabled security groups apply via Exchange Online, not Graph.
                        var viaExchange = string.Equals(g.GroupKind, "Distribution", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(g.GroupKind, "Mail-enabled security", StringComparison.OrdinalIgnoreCase);
                        CloudGroups.Add(new TemplateCopyGroupRow
                        {
                            Name = g.DisplayName,
                            Id = g.Id,
                            Channel = viaExchange ? GroupChannel.ExchangeOnline : GroupChannel.EntraGraph,
                            Smtp = viaExchange ? g.Mail : null,
                        });
                    }
                    HasCloudGroups = CloudGroups.Count > 0;
                }
                catch (Exception ex)
                {
                    cloudUnread = true;
                    AppLog.Instance.Warn("Could not load source cloud groups for copy: " + ex.Message);
                }
            }

            Status = "Enter the new user's name. Address, office, title, department, manager and group memberships were copied from the source and can be edited.";
            if (cloudUnread) Status = "⚠ The source user's cloud groups could not be read, so any they belong to are MISSING from this list. " + Status;
        }
        catch (Exception ex) { Status = "Could not load the source user: " + DirectoryService.Friendly(ex); }
        finally { IsBusy = false; }
    }

    partial void OnFirstNameChanged(string value) => ApplySuggestions();
    partial void OnLastNameChanged(string value) => ApplySuggestions();

    /// <summary>Assembles the new user's attribute set: name-derived identity + the (editable) copied details + manager.</summary>
    private Dictionary<string, string> BuildAttributes()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(FirstName)) result["givenName"] = FirstName.Trim();
        if (!string.IsNullOrWhiteSpace(LastName)) result["sn"] = LastName.Trim();
        if (!string.IsNullOrWhiteSpace(MiddleName)) result["middleName"] = MiddleName.Trim();
        if (!string.IsNullOrWhiteSpace(Initials)) result["initials"] = Initials.Trim();
        // Blank unless typed — LoadAsync never seeds it from the source user. See the property.
        if (!string.IsNullOrWhiteSpace(EmployeeId)) result["employeeID"] = EmployeeId.Trim();
        var display = string.IsNullOrWhiteSpace(DisplayName) ? $"{FirstName} {LastName}".Trim() : DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(display)) result["displayName"] = display;
        if (!string.IsNullOrWhiteSpace(SamAccountName)) result["sAMAccountName"] = SamAccountName.Trim();
        result["cn"] = !string.IsNullOrWhiteSpace(display) ? display : SamAccountName.Trim();
        if (!string.IsNullOrWhiteSpace(Upn)) result["userPrincipalName"] = Upn.Trim();
        if (!string.IsNullOrWhiteSpace(Email)) result["mail"] = Email.Trim();
        if (!string.IsNullOrWhiteSpace(_managerDn)) result["manager"] = _managerDn;

        // Copied detail fields (editable).
        if (!string.IsNullOrWhiteSpace(Street)) result["streetAddress"] = Street.Trim();
        if (!string.IsNullOrWhiteSpace(City)) result["l"] = City.Trim();
        if (!string.IsNullOrWhiteSpace(State)) result["st"] = State.Trim();
        if (!string.IsNullOrWhiteSpace(PostalCode)) result["postalCode"] = PostalCode.Trim();
        if (!string.IsNullOrWhiteSpace(Office)) result["physicalDeliveryOfficeName"] = Office.Trim();
        if (!string.IsNullOrWhiteSpace(Title)) result["title"] = Title.Trim();
        if (!string.IsNullOrWhiteSpace(Department)) result["department"] = Department.Trim();

        // Country/region: one friendly pick → co (name) + c (two-letter) + countryCode (numeric).
        if (SelectedCountry is { } c && !string.IsNullOrEmpty(c.Alpha2))
        {
            result["co"] = c.Name;
            result["c"] = c.Alpha2;
            result["countryCode"] = c.Numeric.ToString();
        }

        return result.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                     .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string Raw(IReadOnlyDictionary<string, AdAttribute> map, string ldap) =>
        map.TryGetValue(ldap, out var a) && a.RawValues.Count > 0 ? a.RawValues[0] : string.Empty;

    /// <summary>Suggests sAM / UPN / email / display name from the entered name using the chosen template's
    /// naming patterns. When <paramref name="force"/> is false, only fields still holding the last auto-suggested
    /// value are updated (so manual edits are preserved); on a template switch the caller forces a re-apply.</summary>
    private void ApplySuggestions(bool force = false)
    {
        var sam = Sanitize(Resolve(_samPattern, sam: string.Empty));
        var display = Resolve(_displayPattern, sam);
        var upn = Resolve(_upnPattern, sam);
        var email = Resolve(_mailPattern, sam);

        if (force || SamAccountName == _lastSam) { SamAccountName = sam; _lastSam = sam; }
        if (force || DisplayName == _lastDisplay) { DisplayName = display; _lastDisplay = display; }
        if (force || Upn == _lastUpn) { Upn = upn; _lastUpn = upn; }
        if (force || Email == _lastEmail) { Email = email; _lastEmail = email; }
    }

    /// <summary>Loads the naming patterns (sAM / display / UPN / email + UPN suffix) from the chosen template,
    /// resetting to sensible defaults first so an unset pattern doesn't carry over from a previously-selected one.</summary>
    private void ApplyNamingFromTemplate(UserTemplate? template)
    {
        _samPattern = "{first}.{last}";
        _displayPattern = "{first} {last}";
        _upnPattern = "{sam}@{upnSuffix}";
        _mailPattern = "{sam}@{upnSuffix}";
        if (template is null) { _upnSuffix = _sourceUpnDomain; return; }

        _upnSuffix = !string.IsNullOrWhiteSpace(template.UpnSuffix) ? template.UpnSuffix : _sourceUpnDomain;
        if (template.AttributeDefaults.TryGetValue("sAMAccountName", out var s) && !string.IsNullOrWhiteSpace(s)) _samPattern = s;
        if (template.AttributeDefaults.TryGetValue("displayName", out var d) && !string.IsNullOrWhiteSpace(d)) _displayPattern = d;
        if (template.AttributeDefaults.TryGetValue("userPrincipalName", out var u) && !string.IsNullOrWhiteSpace(u)) _upnPattern = u;
        if (template.AttributeDefaults.TryGetValue("mail", out var m) && !string.IsNullOrWhiteSpace(m)) _mailPattern = m;
    }

    [RelayCommand]
    private void GeneratePassword()
    {
        var pwd = PassphraseGenerator.Generate();
        Password = pwd;
        GeneratedPassword = pwd;
        PasswordGenerated?.Invoke(pwd);
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
    }

    [RelayCommand]
    private void ClearManager()
    {
        _managerDn = null;
        ManagerDisplay = "(none)";
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(SamAccountName)) { Status = "A logon name (sAMAccountName) is required."; return; }
        if (string.IsNullOrWhiteSpace(TargetOu)) { Status = "A target OU (DN) is required."; return; }

        var attributes = BuildAttributes();
        if (!attributes.TryGetValue("cn", out var cn) || string.IsNullOrWhiteSpace(cn)) { Status = "Could not derive a common name (cn)."; return; }

        var groupDns = Groups.Where(g => g.Include).Select(g => g.Id).ToList();
        var cloudGroups = CloudGroups.Where(g => g.Include).ToList();
        var doSync = RunEntraSync || cloudGroups.Count > 0 || IssueTap;

        // Fail fast on cloud prerequisites before creating anything on-prem.
        if (cloudGroups.Count > 0 || IssueTap)
        {
            if (!_graph.IsSignedIn) { Status = "Sign in to Entra ID (File ▸ Settings ▸ Cloud) before copying cloud groups or issuing a Temporary Access Pass."; return; }
            if (string.IsNullOrWhiteSpace(Upn)) { Status = "A routable UPN is required to match the new user in Entra ID for cloud groups / a Temporary Access Pass."; return; }
        }
        if (IssueTap && (TapLifetimeMinutes < 10 || TapLifetimeMinutes > 43200)) { Status = "Temporary Access Pass lifetime must be between 10 and 43200 minutes (30 days)."; return; }
        if (doSync && string.IsNullOrWhiteSpace(EntraConnectServer)) { Status = "Enter the Entra Connect server to run the post-create sync."; return; }
        if (doSync && SyncSpecifyCredentials && string.IsNullOrWhiteSpace(SyncUsername)) { Status = "Enter the sync-account username, or clear “Use a specific account”."; return; }

        // Reject a duplicate logon name before creating (AD would reject it too, but cryptically).
        var sam = attributes.TryGetValue("sAMAccountName", out var samValue) && !string.IsNullOrWhiteSpace(samValue)
            ? samValue : SamAccountName.Trim();
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
            AppLog.Instance.Warn($"Could not pre-check logon-name uniqueness for '{sam}': {DirectoryService.Friendly(ex)}");
        }

        var lines = new List<string> { $"Create user in: {TargetOu}" };
        lines.AddRange(attributes.OrderBy(kv => kv.Key).Select(kv => $"{AttributeCatalog.Friendly(kv.Key)}: {kv.Value}"));
        if (groupDns.Count > 0) lines.Add($"Add to {groupDns.Count} on-prem group(s)");
        if (cloudGroups.Count > 0) lines.Add($"Add to {cloudGroups.Count} cloud group(s) after an Entra Connect sync");
        else if (doSync) lines.Add($"Run an Entra Connect delta sync on {EntraConnectServer} after creating");
        if (IssueTap) lines.Add($"Issue a Temporary Access Pass (valid {TapLifetimeMinutes} min, {(TapOneTimeUse ? "one-time use" : "multi-use")}) once the user syncs");
        if (!_dialogs.Confirm("Copy user", $"Create “{attributes["cn"]}”?", lines))
            return;

        IsBusy = true;
        ProgressSteps.Clear();
        ShowProgress = doSync;
        Status = "Creating…";
        try
        {
            var pwRequested = !string.IsNullOrEmpty(Password);
            var result = await _directory.CreateUserAsync(
                TargetOu, attributes, groupDns,
                pwRequested ? Password : null, Enabled, MustChangePassword, null);
            Created = true;
            _createdDn = result.DistinguishedName;
            UserCreated?.Invoke();
            if (doSync) Step($"✓ Created {result.DistinguishedName}");

            // What the account was actually created with. Gated on doSync like every other step here,
            // so this does not start producing a log in the cases that deliberately have none. Copy User
            // sends no proxy addresses, unlike New User.
            if (doSync)
                foreach (var line in OperationLog.DescribeAttributes(attributes))
                    Step(line);

            if (pwRequested && !result.PasswordSet)
            {
                if (doSync) Step("⚠ Password was NOT set — account left disabled (the connection isn't encrypted).");
                _dialogs.Alert("Password not set",
                    $"{result.DistinguishedName} was created, but the password could NOT be set, so the account is DISABLED.\n\n" +
                    "Reconnect securely (LDAPS or Kerberos sign+seal), then reset the password and enable the account.");
            }

            if (!doSync) { Status = $"Created {result.DistinguishedName}."; return; }
            await RunPostCreateCloudAsync(cloudGroups);
        }
        catch (Exception ex)
        {
            Status = DirectoryService.Friendly(ex);
            if (ShowProgress) Step("✗ " + Status); else _dialogs.Alert("Create failed", Status);
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Resolves a naming pattern through the SHARED resolver. This used to be a near-verbatim private copy
    /// of it — same regex, same nine tokens, same switch — and it had already drifted: it resolved
    /// {upnSuffix} without trimming, so a template whose suffix carried a trailing space produced
    /// "jdoe@contoso.com " here and "jdoe@contoso.com" in New User, and the trailing space went into AD.
    /// A token added to the builder never reached this window at all.
    /// </summary>
    private string Resolve(string pattern, string sam) =>
        UserAttributeBuilder.Resolve(
            new UserAttributeBuilder.NameTokens(FirstName, MiddleName, LastName, Initials, _upnSuffix),
            pattern, sam);

    private static string DomainOf(IReadOnlyDictionary<string, AdAttribute> map, string ldap)
    {
        if (map.TryGetValue(ldap, out var a) && a.RawValues.Count > 0)
        {
            var v = a.RawValues[0];
            var at = v.IndexOf('@');
            if (at >= 0 && at < v.Length - 1) return v[(at + 1)..];
        }
        return string.Empty;
    }

    // Use the shared sanitizer so Copy User honors the same logon-name conventions as New User / bulk
    // create (lowercased, accents folded, special characters dropped).
    private static string Sanitize(string sam) => UserAttributeBuilder.SanitizeSam(sam);

    // --- Post-create cloud provisioning (Entra Connect sync → wait for the user → add cloud groups) ---

    /// <summary>
    /// Cancels the cloud phase — the waiting for Entra to catch up, and the group adds that follow.
    /// </summary>
    /// <remarks>
    /// The on-prem account is already created by the time any of this runs, so cancelling never undoes
    /// anything; it stops waiting. Without it a retry tuned for a slow tenant has no exit but killing the
    /// app — the very failure the Entra Connect sync timeout was added to prevent.
    /// </remarks>
    private CancellationTokenSource? _cloudCts;

    /// <summary>True while the cloud phase is running and can still be stopped.</summary>
    [ObservableProperty] private bool _canCancelCloud;

    [RelayCommand(CanExecute = nameof(CanCancelCloud))]
    private void CancelCloud()
    {
        Step("• Cancelling… the account is already created; this stops the cloud steps only.");
        _cloudCts?.Cancel();
    }

    /// <summary>
    /// Owns the cancellation, so the phase itself keeps its shape and its early returns. A cancel reports
    /// what DID happen: the account exists, and whichever groups were added before the stop are still
    /// added — a flat “cancelled” would send someone looking for a user that is already there.
    /// </summary>
    private async Task RunPostCreateCloudAsync(IReadOnlyList<TemplateCopyGroupRow> cloudGroups)
    {
        _cloudCts?.Dispose();
        _cloudCts = new CancellationTokenSource();
        CanCancelCloud = true;
        CancelCloudCommand.NotifyCanExecuteChanged();
        try
        {
            await RunPostCreateCloudCoreAsync(cloudGroups, _cloudCts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. The user was created; some cloud steps may not have run.";
            Step("✗ Cancelled. The account exists. Add cloud / Exchange groups or issue a TAP from its Cloud tab.");
        }
        finally
        {
            CanCancelCloud = false;
            CancelCloudCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RunPostCreateCloudCoreAsync(
        IReadOnlyList<TemplateCopyGroupRow> cloudGroups, CancellationToken ct)
    {
        var sync = await _cloudProvisioning.RunDeltaSyncAsync(
            EntraConnectServer, SyncSpecifyCredentials ? SyncUsername : null,
            SyncSpecifyCredentials ? SyncPassword : null, _settings, Step);
        Step(sync.Success ? "✓ Delta sync started." : "⚠ Sync command reported a problem: " + sync.Output.Replace(Environment.NewLine, " "));

        // Cloud groups and/or a Temporary Access Pass both need the synced user; otherwise the sync was the whole job.
        bool needCloudUser = cloudGroups.Count > 0 || IssueTap;
        if (!needCloudUser) { Status = "User created and a delta sync was started."; Step("Done."); return; }

        Step("• Waiting for the user to appear in Entra ID (this can take a minute)…");
        var cloudUser = await _cloudProvisioning.PollForCloudUserAsync(Upn.Trim(), Step);
        if (cloudUser is null)
        {
            Status = "User created, but it hadn't synced to Entra ID in time — cloud groups / Temporary Access Pass were NOT applied.";
            Step("✗ Not found in Entra ID within the wait window. Run a sync later, then add cloud groups / issue a TAP from the user's Cloud tab.");
            return;
        }
        Step("✓ Found in Entra ID.");

        // Split the copied cloud memberships by apply channel: Graph groups vs Exchange distribution groups.
        var graphGroups = cloudGroups.Where(g => g.Channel != GroupChannel.ExchangeOnline).ToList();
        var distributionGroups = cloudGroups.Where(g => g.Channel == GroupChannel.ExchangeOnline).ToList();

        int ok = 0, failed = 0;
        if (graphGroups.Count > 0)
        {
            Step("• Adding cloud groups…");
            var refs = graphGroups.Select(g => new CloudGroupRef { Id = g.Id, Name = g.Name });
            (ok, failed) = await _cloudProvisioning.AddUserToGroupsAsync(cloudUser.Id, refs, Step, ct);
        }

        int dok = 0, dfailed = 0;
        if (distributionGroups.Count > 0)
        {
            Step("• Adding Exchange distribution groups…");
            var refs = distributionGroups.Select(g => new DistributionGroupRef { Id = g.Id, Name = g.Name, Smtp = g.Smtp ?? string.Empty });
            (dok, dfailed) = await _cloudProvisioning.AddUserToDistributionGroupsAsync(Upn.Trim(), refs, Step, ct);
        }

        if (IssueTap)
        {
            Step("• Issuing a Temporary Access Pass…");
            var tap = await _cloudProvisioning.IssueTemporaryAccessPassAsync(cloudUser.Id, TapLifetimeMinutes, TapOneTimeUse, Step, ct);
            if (tap is { Pass.Length: > 0 }) TapCode = tap.Pass;
        }

        var summary = "User created";
        if (graphGroups.Count > 0) summary += $"; added to {ok} cloud group(s)" + (failed > 0 ? $", {failed} failed" : "");
        if (distributionGroups.Count > 0) summary += $"; added to {dok} distribution group(s)" + (dfailed > 0 ? $", {dfailed} failed" : "");
        if (TapCode.Length > 0) summary += "; Temporary Access Pass issued (copy it now)";
        Status = summary + ".";
        Step("Done.");
    }


    /// <summary>
    /// The progress lines as one block of text, with a header naming what was done, to whom, by whom and
    /// when. Backs both **Copy log** and **Save log…**.
    ///
    /// The steps deliberately carry no secret: the password step says only that it was set, and the
    /// Temporary Access Pass reporter says only that one was issued. Neither value is ever written here.
    /// There is a test that keeps it that way.
    /// </summary>
    /// <summary>
    /// The distinguished name of the account this window created, once it has one. Null before that, and
    /// after a failure — which is exactly when the record has to fall back to what the operator typed.
    /// </summary>
    private string? _createdDn;

    /// <summary>Who the record is about: the real DN once it exists, else the best name available.</summary>
    private string LogSubject => string.IsNullOrWhiteSpace(_createdDn) ? LogFileSubject : _createdDn!;

    /// <summary>
    /// The short name a log FILE is named after. Deliberately not the DN: a distinguished name is full of
    /// commas and equals signs, and sanitising one produces an unreadable file name.
    /// </summary>
    private string LogFileSubject =>
        !string.IsNullOrWhiteSpace(SamAccountName) ? SamAccountName.Trim()
        : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName.Trim()
        : "user";

    public string LogText => OperationLog.BuildCreationRecord(
        "Copy user", LogSubject, _directory.Current?.Username, DateTime.Now, ProgressSteps, Status);

    /// <summary>There is nothing to copy or save until something has actually been attempted.</summary>
    public bool HasLog => ProgressSteps.Count > 0;

    /// <summary>
    /// Writes the record to a file the operator chooses, defaulting to the operation-log folder — the same
    /// one scenario logs and deleted-group records go to, so a ticket's paperwork ends up together.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasLog))]
    private void SaveLog()
    {
        if (!HasLog) return; // belt and braces: the command is gated on the same thing

        var suggested = OperationLog.SuggestFileName("copy-user", LogFileSubject, DateTime.Now);
        var folder = OperationLog.ResolveDirectory(_settings);

        var path = _dialogs.PromptSaveFile(
            "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*", suggested, folder);
        // Empty counts as cancelled too: File.WriteAllText throws on an empty path, which would read
        // to the operator as the SAVE having failed when they had simply closed the dialog.
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path, LogText);
            Status = $"Log saved to {path}.";
            AppLog.Instance.Info($"Copy user record saved to {path}.");
        }
        catch (Exception ex)
        {
            // The account was still created; a failed log write must not read as a failed creation.
            Status = "The user was created, but the log could not be saved: " + ex.Message;
            AppLog.Instance.Warn("Could not save the creation record: " + ex.Message);
        }
    }

    private void Step(string text)
    {
        ProgressSteps.Add(text);
        OnPropertyChanged(nameof(HasLog));
        SaveLogCommand.NotifyCanExecuteChanged();
        Status = text;
    }
}
