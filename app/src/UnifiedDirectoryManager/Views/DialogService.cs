using System.Linq;
using System.Windows;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;
using UnifiedDirectoryManager.ViewModels;
using UnifiedDirectoryManager.Views.Dialogs;

namespace UnifiedDirectoryManager.Views;

/// <summary>Creates and shows the app's dialogs, constructing each window's view model.</summary>
public sealed class DialogService : IDialogService
{
    private readonly IDirectoryService _directory;
    private readonly ITemplateStore _templates;
    private readonly IScenarioStore _scenarios;
    private readonly ISavedSearchStore _savedSearches;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IGraphService _graph;
    private readonly IExchangeService _exchange;
    private readonly IDomainLocator _locator;
    private readonly ICredentialStore _credentials;
    private readonly EntraSyncService _entraSync;
    private readonly CloudProvisioningService _cloudProvisioning;
    private readonly ScenarioRunner _scenarioRunner;
    private readonly BulkUserCreator _bulkCreator;
    private readonly BulkUserCsvImporter _csvImporter;

    public DialogService(IDirectoryService directory, ITemplateStore templates, IScenarioStore scenarios,
        ISavedSearchStore savedSearches, ISettingsStore settingsStore, AppSettings settings, IGraphService graph,
        IExchangeService exchange, IDomainLocator locator, ICredentialStore credentials, ScenarioRunner scenarioRunner)
    {
        _directory = directory;
        _templates = templates;
        _scenarios = scenarios;
        _savedSearches = savedSearches;
        _settingsStore = settingsStore;
        _settings = settings;
        _graph = graph;
        _exchange = exchange;
        _locator = locator;
        _credentials = credentials;
        _scenarioRunner = scenarioRunner;
        _entraSync = new EntraSyncService(credentials); // saved-sync-credential fallback comes from the store
        _cloudProvisioning = new CloudProvisioningService(graph, exchange, _entraSync, settingsStore);
        _bulkCreator = new BulkUserCreator(directory, _cloudProvisioning, settings);
        _csvImporter = new BulkUserCsvImporter(directory, graph);
    }

    private static Window? Owner =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current.MainWindow;

    public IReadOnlyList<AdObjectRow>? PickObjects(string title, AdObjectType type, bool multiSelect)
    {
        var vm = new ObjectPickerViewModel(_directory, type, multiSelect);
        var window = new ObjectPickerWindow { DataContext = vm, Title = title, Owner = Owner };
        return window.ShowDialog() == true && vm.Commit() ? vm.Picked : null;
    }

    public IReadOnlyList<GroupRef>? PickGroupsHybrid(string title)
    {
        var vm = new HybridGroupPickerViewModel(_directory, _graph);
        var window = new HybridGroupPickerWindow { DataContext = vm, Title = title, Owner = Owner };
        return window.ShowDialog() == true && vm.Commit() ? vm.Picked : null;
    }

    public IReadOnlyList<CloudObjectRow>? PickCloudMembers(string title)
    {
        var vm = new CloudMemberPickerViewModel(_graph);
        var window = new CloudMemberPickerWindow { DataContext = vm, Title = title, Owner = Owner };
        return window.ShowDialog() == true && vm.Commit() ? vm.Picked : null;
    }

    public IReadOnlyList<CloudSku>? PickLicenses(string title, IReadOnlyList<CloudSku> candidates)
    {
        var vm = new CloudLicensePickerViewModel(candidates);
        var window = new CloudLicensePickerWindow { DataContext = vm, Title = title, Owner = Owner };
        return window.ShowDialog() == true && vm.Picked.Count > 0 ? vm.Picked : null;
    }

    public IReadOnlyList<CloudGroup>? PickCloudGroups(string title)
    {
        var vm = new CloudGroupPickerViewModel(_graph);
        var window = new CloudGroupPickerWindow { DataContext = vm, Title = title, Owner = Owner };
        return window.ShowDialog() == true && vm.Commit() ? vm.Picked : null;
    }

    public MailboxRecipient? PickMailboxRecipient(string title)
    {
        var vm = new MailboxRecipientPickerViewModel(_exchange);
        var window = new MailboxRecipientPickerWindow { DataContext = vm, Title = title, Owner = Owner };
        return window.ShowDialog() == true && vm.Commit() ? vm.Picked[0] : null;
    }

    public IReadOnlyList<MailboxRecipient>? PickMailboxRecipients(string title, IReadOnlyList<MailboxRecipient>? initial = null)
    {
        var vm = new MailboxRecipientPickerViewModel(_exchange, multiSelect: true, initial);
        var window = new MailboxRecipientPickerWindow { DataContext = vm, Title = title, Owner = Owner };
        return window.ShowDialog() == true && vm.Commit() ? vm.Picked : null;
    }

    public IReadOnlyList<MemberCandidate>? PasteMembers(string title, MemberBackend backend, IEnumerable<string> alreadyMembers, string? selfIdentity)
    {
        // The resolver is chosen by whoever owns the group's membership, never by what the operator pasted.
        IMemberResolver resolver = backend switch
        {
            MemberBackend.ActiveDirectory => new AdMemberResolver(_directory),
            MemberBackend.Entra => new EntraMemberResolver(_graph),
            _ => new ExchangeMemberResolver(_exchange),
        };
        var vm = new PasteMembersViewModel(resolver, alreadyMembers, selfIdentity);
        var window = new PasteMembersWindow { DataContext = vm, Title = title, Owner = Owner };
        return window.ShowDialog() == true ? vm.Accepted : null;
    }

    public void ShowDistributionGroupMembers(string identity, string groupName, bool isSynced)
    {
        var vm = new DistributionGroupMembersViewModel(_exchange, this, identity, groupName, isSynced);
        new DistributionGroupMembersWindow { DataContext = vm, Owner = Owner }.ShowDialog(); // modal; loads on open
    }

    public (DelegateAccess Access, bool AutoMapping)? EditDelegateAccess(string delegateName, DelegateAccess current)
    {
        var vm = new DelegateAccessViewModel(delegateName, current);
        var window = new DelegateAccessWindow { DataContext = vm, Owner = Owner };
        return window.ShowDialog() == true ? (vm.SelectedAccess, vm.AutoMapping) : null;
    }

    public bool Confirm(string title, string heading, IEnumerable<string> lines)
    {
        var window = new ConfirmWindow(title, heading, lines) { Owner = Owner };
        return window.ShowDialog() == true;
    }

    public string? PickContainer(string? initialDn)
    {
        var window = new OuPickerWindow(_directory, initialDn) { Owner = Owner };
        return window.ShowDialog() == true ? window.SelectedDn : null;
    }

    public IReadOnlyList<string>? PickContainers(IEnumerable<string> initialDns)
    {
        var window = new OuPickerWindow(_directory, initialDns, multiSelect: true) { Owner = Owner };
        return window.ShowDialog() == true ? window.SelectedDns : null;
    }

    public void Alert(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowBulkResult(BulkResult result) =>
        new BulkResultWindow(result) { Owner = Owner }.ShowDialog();

    public BulkResult ShowScenarioRun(Scenario scenario, IReadOnlyList<AdObjectRow> targets, IList<string>? operationLog)
    {
        var vm = new ScenarioProgressViewModel(scenario, targets, _scenarioRunner, operationLog);
        new ScenarioProgressWindow { DataContext = vm, Owner = Owner }.ShowDialog(); // modal; runs on load
        return vm.Result ?? new BulkResult(Array.Empty<BulkItemResult>());
    }

    public void ShowNewUser(string? defaultOuDn, Action onCreated)
    {
        var vm = new NewUserViewModel(_directory, _templates, this, _graph, _cloudProvisioning, _settings) { DefaultOu = defaultOuDn };
        vm.ReloadTemplates();
        vm.UserCreated += () => onCreated();
        new NewUserWindow { DataContext = vm, Owner = Owner }.Show(); // non-modal
    }

    public void ShowBulkCreateUsers(string? defaultOuDn, Action onCreated)
    {
        var vm = new BulkCreateUsersViewModel(_directory, _templates, this, _graph, _bulkCreator, _csvImporter, _settings)
        { DefaultOu = defaultOuDn };
        vm.ReloadTemplates();
        vm.UsersCreated += () => onCreated();
        new BulkCreateUsersWindow { DataContext = vm, Owner = Owner }.Show(); // non-modal
    }

    public void ShowBulkCreateReport(BulkCreateReport report)
    {
        var vm = new BulkCreateReportViewModel(report);
        new BulkCreateReportWindow { DataContext = vm, Owner = Owner }.ShowDialog(); // modal — secrets shown once
    }

    public void CaptureBatchUser(UserTemplate? defaultTemplate, string? defaultOuDn, string? upnSuffix, BulkCreateRowViewModel? existing, Action<BulkCreateRowViewModel> onCaptured)
    {
        // Reuse the actual New User window so each batch row is configured with the exact same form, but in
        // capture mode: it validates and hands the inputs back instead of creating the account now. Shown
        // non-modal so the operator can switch to other windows (e.g. the directory) while configuring a row.
        var vm = new NewUserViewModel(_directory, _templates, this, _graph, _cloudProvisioning, _settings)
        { IsBatchCapture = true, DefaultOu = defaultOuDn };
        vm.ReloadTemplates();
        var tpl = existing?.Template ?? defaultTemplate;
        if (tpl is not null)
            vm.SelectedTemplate = vm.Templates.FirstOrDefault(t => t.Name == tpl.Name) ?? vm.SelectedTemplate;
        if (!string.IsNullOrWhiteSpace(upnSuffix)) vm.UpnSuffix = upnSuffix!;
        if (existing is not null) SeedNewUserFromRow(vm, existing);

        var win = new NewUserWindow
        {
            DataContext = vm,
            Owner = Owner,
            Title = existing is null ? "Add User to Batch" : "Edit Batch User",
        };
        win.BatchCaptured += () => onCaptured(MapRowFromNewUser(vm));
        win.Show(); // non-modal
    }

    /// <summary>Prefills the New User capture window from an existing batch row (for editing).</summary>
    private static void SeedNewUserFromRow(NewUserViewModel vm, BulkCreateRowViewModel r)
    {
        // Names first (they re-trigger suggestions), then explicit email/UPN/proxy so the row's values win.
        vm.FirstName = r.FirstName; vm.MiddleName = r.MiddleName; vm.LastName = r.LastName; vm.Initials = r.Initials;
        vm.SamOverride = r.SamOverride;
        vm.TargetOu = r.TargetOu;
        vm.Enabled = r.Enabled;
        vm.Email = r.Email; vm.Upn = r.Upn; vm.ProxyAddressesText = r.ProxyAddressesText;
        vm.SetManager(r.ManagerDn, r.ManagerDisplay);
        vm.IssueTap = r.IssueTap; vm.TapLifetimeMinutes = r.TapLifetimeMinutes; vm.TapOneTimeUse = r.TapOneTimeUse;
        vm.OnPremGroups.Clear();
        foreach (var g in r.OnPremGroups) vm.OnPremGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.Id, Channel = GroupChannel.OnPremAd });
        vm.CloudGroups.Clear();
        foreach (var g in r.CloudGroups) vm.CloudGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.Id, Channel = GroupChannel.EntraGraph });
        vm.DistributionGroups.Clear();
        foreach (var g in r.DistributionGroups) vm.DistributionGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.Id, Channel = GroupChannel.ExchangeOnline, Smtp = g.Smtp });
    }

    /// <summary>Reads the configured values back out of the capture window into a batch row.</summary>
    private static BulkCreateRowViewModel MapRowFromNewUser(NewUserViewModel vm)
    {
        var row = new BulkCreateRowViewModel
        {
            Template = vm.SelectedTemplate,
            UpnSuffix = vm.UpnSuffix,
            FirstName = vm.FirstName, MiddleName = vm.MiddleName, LastName = vm.LastName, Initials = vm.Initials,
            SamOverride = vm.SamOverride, Upn = vm.Upn, Email = vm.Email,
            TargetOu = vm.TargetOu, ProxyAddressesText = vm.ProxyAddressesText, Enabled = vm.Enabled,
            ManagerDn = vm.ManagerDn, ManagerDisplay = vm.ManagerDisplay,
            IssueTap = vm.IssueTap, TapLifetimeMinutes = vm.TapLifetimeMinutes, TapOneTimeUse = vm.TapOneTimeUse,
        };
        foreach (var g in vm.OnPremGroups.Where(g => g.Include))
            row.OnPremGroups.Add(new TemplateCopyGroupRow { Name = g.Name, Id = g.Id, Channel = GroupChannel.OnPremAd });
        foreach (var g in vm.CloudGroups.Where(g => g.Include))
            row.CloudGroups.Add(new CloudGroupRef { Id = g.Id, Name = g.Name });
        foreach (var g in vm.DistributionGroups.Where(g => g.Include))
            row.DistributionGroups.Add(new DistributionGroupRef { Id = g.Id, Name = g.Name, Smtp = g.Smtp ?? string.Empty });
        return row;
    }

    public void ShowTemplateEditor()
    {
        var vm = new TemplateEditorViewModel(_templates, this);
        new TemplateEditorWindow { DataContext = vm, Owner = Owner }.Show(); // non-modal
    }

    public void ShowCopyUserToTemplate(string userDistinguishedName)
    {
        var vm = new CopyToTemplateViewModel(_directory, _graph, _templates, this, userDistinguishedName);
        new CopyToTemplateWindow { DataContext = vm, Owner = Owner }.ShowDialog(); // modal; loads on Loaded
    }

    public void ShowCopyUser(string sourceUserDistinguishedName, Action onCreated)
    {
        var vm = new CopyUserViewModel(_directory, _templates, this, _graph, _cloudProvisioning, _settings, sourceUserDistinguishedName);
        vm.UserCreated += () => onCreated();
        new CopyUserWindow { DataContext = vm, Owner = Owner }.Show(); // non-modal; loads on Loaded
    }

    public bool ShowCopyGroupsToUser(string sourceUserDistinguishedName)
    {
        var vm = new CopyGroupsViewModel(_directory, _graph, _exchange, this, sourceUserDistinguishedName);
        new CopyGroupsWindow { DataContext = vm, Owner = Owner }.ShowDialog(); // modal; loads on Loaded
        return vm.Applied;
    }

    public void ShowScenarioEditor(Action onChanged)
    {
        var vm = new ScenarioEditorViewModel(_scenarios, this, onChanged);
        new ScenarioEditorWindow { DataContext = vm, Owner = Owner }.Show(); // non-modal
    }

    public void ShowReadme() => new ReadmeWindow { Owner = Owner }.Show();

    public void ShowEntraSync()
    {
        var vm = new EntraSyncViewModel(_entraSync, _settingsStore, _settings, _credentials);
        new EntraSyncWindow { DataContext = vm, Owner = Owner }.Show(); // non-modal
    }

    public void ShowSettings(Action onReconnected)
    {
        var connection = new ConnectionViewModel(_directory, _locator, _credentials, _settingsStore, _settings);
        var cloud = new CloudSignInViewModel(_graph, _settingsStore, _settings);
        var vm = new SettingsViewModel(connection, cloud, _settingsStore, _settings, _credentials, onReconnected);
        new SettingsWindow { DataContext = vm, Owner = Owner }.ShowDialog(); // modal
    }

    public void ShowCloudObjectProperties(CloudObjectRow row)
    {
        var vm = new CloudObjectDetailViewModel(_graph, _exchange, this);
        vm.SetTarget(row);
        new CloudObjectPropertiesWindow { DataContext = vm, Owner = Owner }.Show(); // non-modal
    }

    public void ShowAdObjectProperties(string distinguishedName, AdObjectType type)
    {
        // Its own view model, so the window and the main pane cannot fight over one object's edit state.
        var vm = new EditPaneViewModel(_directory, this, msg => Alert("Error", msg), _graph, _exchange);
        _ = vm.LoadAsync(distinguishedName, type);
        new AdObjectPropertiesWindow { DataContext = vm, Owner = Owner }.Show(); // non-modal
    }

    public void ShowOuProperties(string distinguishedName, string name)
    {
        var vm = new OuPropertiesViewModel(_directory, distinguishedName, name);
        new OuPropertiesWindow { DataContext = vm, Title = $"{name} — Properties", Owner = Owner }.ShowDialog(); // modal; loads on Loaded
    }

    public string? ShowNewOu(string parentDn)
    {
        var vm = new NewOuViewModel(_directory, this, parentDn);
        var win = new NewOuWindow { DataContext = vm, Owner = Owner };
        vm.Created += () => { win.DialogResult = true; win.Close(); }; // close the modal once the OU is created
        return win.ShowDialog() == true ? vm.CreatedDistinguishedName : null;
    }

    public string? ShowNewGroup(string? parentDn)
    {
        var vm = new NewGroupViewModel(_directory, this, parentDn);
        var win = new NewGroupWindow { DataContext = vm, Owner = Owner };
        vm.Created += () => { win.DialogResult = true; win.Close(); }; // close the modal once the group is created
        return win.ShowDialog() == true ? vm.CreatedDistinguishedName : null;
    }

    public (string Id, string Name, bool FromExchange)? ShowNewCloudGroup(CloudGroupType? initialType)
    {
        var vm = new NewCloudGroupViewModel(_graph, _exchange, this, initialType);
        var win = new NewCloudGroupWindow { DataContext = vm, Owner = Owner };
        vm.Created += () => { win.DialogResult = true; win.Close(); }; // close the modal once the group is created
        // Defaulting the owner to the signed-in admin needs a Graph lookup. Start it and open the dialog straight
        // away rather than stalling on it: ShowDialog pumps messages, so the continuation lands back on the UI
        // thread and the row appears a moment later. It fails soft internally, so there is nothing to await here.
        _ = vm.InitializeAsync();
        return win.ShowDialog() == true && vm.CreatedId is { Length: > 0 } id
            ? (id, vm.CreatedName ?? string.Empty, vm.CreatedInExchange)
            : null;
    }

    public SearchQuery? ShowAdvancedSearch(string defaultBaseDn)
    {
        var vm = new AdvancedSearchViewModel(this, _savedSearches);
        if (!string.IsNullOrWhiteSpace(defaultBaseDn))
            vm.AddBase(defaultBaseDn);
        new AdvancedSearchWindow { DataContext = vm, Owner = Owner }.ShowDialog();
        return vm.Result;
    }

    public bool ShowBulkEdit(IReadOnlyList<AdObjectRow> rows)
    {
        var vm = new BulkEditViewModel(_directory, this, rows);
        new BulkEditWindow { DataContext = vm, Owner = Owner }.ShowDialog();
        return vm.Applied;
    }

    public IReadOnlyList<string>? EditMultiValue(string friendlyName, IEnumerable<string> values)
    {
        var window = new MultiValueEditorWindow(friendlyName, values) { Owner = Owner };
        return window.ShowDialog() == true ? window.Values : null;
    }

    public void OpenObjectEditor(string distinguishedName, AdObjectType type, string title, Action onChanged)
    {
        var vm = new EditPaneViewModel(_directory, this, AppLog.Instance.Warn, _graph, _exchange);
        vm.ObjectChanged += () => onChanged();
        var window = new ObjectEditorWindow { DataContext = vm, Title = title, Owner = Owner };
        window.Show(); // non-modal so several editors can be open at once
        _ = vm.LoadAsync(distinguishedName, type);
    }

    public bool ConfirmWithPhrase(string title, string heading, IEnumerable<string> lines, string requiredPhrase)
    {
        var window = new ConfirmPhraseWindow(title, heading, lines, requiredPhrase) { Owner = Owner };
        return window.ShowDialog() == true;
    }

    public bool? ConfirmDelete(string title, string heading, IEnumerable<string> lines,
                               string? requiredPhrase, bool offerRecord, bool recordDefault)
    {
        var window = new DeleteConfirmWindow(title, heading, lines, requiredPhrase, offerRecord, recordDefault) { Owner = Owner };
        return window.ShowDialog() == true ? window.SaveRecord : null;
    }

    public PasswordResetRequest? PromptPasswordReset(string accountTitle)
    {
        var vm = new ResetPasswordViewModel { AccountTitle = accountTitle };
        var window = new ResetPasswordWindow { DataContext = vm, Owner = Owner };
        return window.ShowDialog() == true ? vm.Result : null;
    }

    public string? PromptSaveFile(string filter, string defaultFileName, string? initialDirectory = null)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = defaultFileName, AddExtension = true };
        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            // Pre-create and point the dialog at the configured folder so the reminder path is where it opens.
            try { System.IO.Directory.CreateDirectory(initialDirectory); dlg.InitialDirectory = initialDirectory; }
            catch { /* fall back to the OS default location */ }
        }
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PromptOpenFile(string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public void ShowLogViewer() => new LogViewerWindow { Owner = Owner }.ShowDialog();

    public void ShowAbout() => new AboutWindow { Owner = Owner }.ShowDialog();
}
