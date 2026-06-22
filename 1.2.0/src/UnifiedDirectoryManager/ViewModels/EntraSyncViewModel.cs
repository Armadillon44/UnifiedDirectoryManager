using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>Runs an Entra Connect delta sync on a remote server, using a saved sync account,
/// the current Windows user, or supplied credentials.</summary>
public partial class EntraSyncViewModel : ObservableObject
{
    private readonly EntraSyncService _service;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ICredentialStore _credentials;

    [ObservableProperty] private string _server = string.Empty;

    // Credential source (mutually-exclusive radio options).
    [ObservableProperty] private bool _useSavedAccount;
    [ObservableProperty] private bool _useCurrentUser;
    [ObservableProperty] private bool _useSpecificCredentials;

    /// <summary>True when a sync account is saved (in Settings) for the current server.</summary>
    [ObservableProperty] private bool _hasSavedAccount;
    /// <summary>Username of the saved account, shown on the "use saved account" option.</summary>
    [ObservableProperty] private string _savedAccountName = string.Empty;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _output = string.Empty;

    /// <summary>Password, set from the PasswordBox code-behind (PasswordBox can't be bound).</summary>
    public string Password { get; set; } = string.Empty;

    public EntraSyncViewModel(EntraSyncService service, ISettingsStore settingsStore, AppSettings settings,
        ICredentialStore credentials)
    {
        _service = service;
        _settingsStore = settingsStore;
        _settings = settings;
        _credentials = credentials;
        _server = settings.EntraConnectServer ?? string.Empty;
        RefreshSavedAccount();
        // Default to the saved account when one exists, otherwise the current Windows user.
        if (HasSavedAccount) UseSavedAccount = true; else UseCurrentUser = true;
    }

    // Re-check for a saved account whenever the server changes (the operator may target a different server).
    partial void OnServerChanged(string value) => RefreshSavedAccount();

    private void RefreshSavedAccount()
    {
        var saved = _credentials.TryLoadSyncCredential(Server?.Trim() ?? string.Empty);
        HasSavedAccount = saved is not null;
        SavedAccountName = saved?.Username ?? string.Empty;
        // If the saved option was selected but no account exists for the new server, fall back to current user.
        if (UseSavedAccount && !HasSavedAccount) UseCurrentUser = true;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(Server)) { Output = "Enter the Entra Connect server name."; return; }
        if (UseSpecificCredentials && string.IsNullOrWhiteSpace(Username)) { Output = "Enter a username, or choose another credential option."; return; }

        string? user, pw;
        if (UseSavedAccount)
        {
            var saved = _credentials.TryLoadSyncCredential(Server.Trim());
            if (saved is null) { Output = "No saved account for this server. Save one in Settings ▸ Entra Connect, or choose another option."; return; }
            user = saved.Username;
            pw = saved.Password;
        }
        else if (UseSpecificCredentials) { user = Username.Trim(); pw = Password; }
        else { user = null; pw = null; } // current Windows user

        IsBusy = true;
        Output = $"Starting delta sync on {Server}…";
        try
        {
            // The tool resolves credentials explicitly, so suppress the service's saved-account fallback —
            // an explicit "current user" choice must stay the current user even when an account is saved.
            var result = await _service.RunDeltaSyncAsync(Server.Trim(), user, pw, allowSavedFallback: false);
            Output = result.Output;

            // Remember the server for next time.
            _settings.EntraConnectServer = Server.Trim();
            _settingsStore.Save(_settings);
        }
        finally { IsBusy = false; }
    }
}
