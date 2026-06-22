using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Backs the File ▸ Settings dialog. Composes the on-prem AD connection form
/// (<see cref="Connection"/>, reused from the startup flow), the cloud sign-in section
/// (<see cref="Cloud"/>), and the Logs section (operation-log folder). A successful reconnect on the
/// AD tab fires the host's refresh callback so the tree/list rebind in place without restarting the app.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly Action _onReconnected;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ICredentialStore _credentials;

    public ConnectionViewModel Connection { get; }
    public CloudSignInViewModel Cloud { get; }

    /// <summary>Operation-log folder override; blank means use the default shown in <see cref="DefaultLogDirectory"/>.</summary>
    [ObservableProperty] private string _operationLogDirectory = string.Empty;
    [ObservableProperty] private string _logStatus = string.Empty;

    // --- Entra Connect (directory sync) account ---
    /// <summary>The Entra Connect server that runs the delta sync (Start-ADSyncSyncCycle over WinRM).</summary>
    [ObservableProperty] private string _syncServer = string.Empty;
    /// <summary>When true, the sync runs as a saved account; otherwise as the current Windows user.</summary>
    [ObservableProperty] private bool _syncUseSavedAccount;
    [ObservableProperty] private string _syncUsername = string.Empty;
    [ObservableProperty] private string _syncStatus = string.Empty;
    /// <summary>True when a password is already saved, so the password box can be left blank to keep it.</summary>
    [ObservableProperty] private bool _syncHasSavedPassword;

    /// <summary>Sync-account password, set from the PasswordBox code-behind (PasswordBox can't be bound).</summary>
    public string SyncPassword { get; set; } = string.Empty;

    /// <summary>The built-in default folder, shown as a hint when no override is set.</summary>
    public string DefaultLogDirectory => OperationLog.DefaultDirectory;

    public SettingsViewModel(ConnectionViewModel connection, CloudSignInViewModel cloud,
        ISettingsStore settingsStore, AppSettings settings, ICredentialStore credentials, Action onReconnected)
    {
        Connection = connection;
        Cloud = cloud;
        _settingsStore = settingsStore;
        _settings = settings;
        _credentials = credentials;
        _onReconnected = onReconnected;
        _operationLogDirectory = settings.OperationLogDirectory ?? string.Empty;
        Connection.ConnectionSucceeded += (_, _) => _onReconnected();

        // Prefill the sync account from the server's saved credential, if any.
        _syncServer = settings.EntraConnectServer ?? string.Empty;
        if (_credentials.TryLoadSyncCredential(_syncServer) is { } savedSync)
        {
            _syncUseSavedAccount = true;
            _syncUsername = savedSync.Username;
            _syncHasSavedPassword = true;
        }
    }

    [RelayCommand]
    private void SaveSyncSettings()
    {
        var server = SyncServer.Trim();
        if (string.IsNullOrWhiteSpace(server)) { SyncStatus = "Enter the Entra Connect server name."; return; }

        _settings.EntraConnectServer = server;
        _settingsStore.Save(_settings);
        SyncServer = server;

        if (!SyncUseSavedAccount)
        {
            // Run as the current Windows user — remove any saved account for this server.
            _credentials.DeleteSyncCredential(server);
            SyncHasSavedPassword = false;
            SyncUsername = string.Empty;
            SyncPassword = string.Empty;
            SyncStatus = $"Saved. The sync on {server} will run as the current Windows user.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SyncUsername))
        {
            SyncStatus = "Enter the sync-account username, or clear “Use a saved account”.";
            return;
        }

        // Keep the existing password when the box is left blank and one is already stored.
        var password = SyncPassword;
        if (string.IsNullOrEmpty(password))
        {
            var existing = _credentials.TryLoadSyncCredential(server);
            if (existing is not null) password = existing.Password;
            else { SyncStatus = "Enter the sync-account password."; return; }
        }

        _credentials.SaveSyncCredential(server, SyncUsername.Trim(), password);
        SyncPassword = string.Empty;
        SyncHasSavedPassword = true;
        SyncStatus = $"Saved. The sync on {server} will run as {SyncUsername.Trim()} (stored in Windows Credential Manager).";
    }

    [RelayCommand]
    private void ClearSyncCredential()
    {
        var server = SyncServer.Trim();
        _credentials.DeleteSyncCredential(server);
        SyncUseSavedAccount = false;
        SyncUsername = string.Empty;
        SyncPassword = string.Empty;
        SyncHasSavedPassword = false;
        SyncStatus = string.IsNullOrWhiteSpace(server)
            ? "Saved sync account cleared."
            : $"Saved sync account for {server} cleared. The sync will run as the current Windows user.";
    }

    [RelayCommand]
    private void SaveLogSettings()
    {
        _settings.OperationLogDirectory = string.IsNullOrWhiteSpace(OperationLogDirectory)
            ? null : OperationLogDirectory.Trim();
        _settingsStore.Save(_settings);
        LogStatus = $"Saved. Logs will be written to: {OperationLog.ResolveDirectory(_settings)}";
    }

    [RelayCommand]
    private void ResetLogDirectory()
    {
        OperationLogDirectory = string.Empty;
        SaveLogSettings();
    }
}
