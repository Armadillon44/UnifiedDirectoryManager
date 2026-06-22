using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// The Cloud (Entra ID) section of the Settings dialog: collects the app-registration identifiers
/// (tenant + client id), remembers them in settings, and signs the admin in/out interactively via
/// <see cref="IGraphService"/>. No secret is collected — the app is a public client using PKCE.
/// </summary>
public partial class CloudSignInViewModel : ObservableObject
{
    private readonly IGraphService _graph;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;

    [ObservableProperty] private string _tenantId = string.Empty;
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _status = string.Empty;

    public CloudSignInViewModel(IGraphService graph, ISettingsStore settingsStore, AppSettings settings)
    {
        _graph = graph;
        _settingsStore = settingsStore;
        _settings = settings;
        _tenantId = settings.EntraTenantId ?? string.Empty;
        _clientId = settings.EntraClientId ?? string.Empty;
        RefreshState();
    }

    private void RefreshState()
    {
        IsSignedIn = _graph.IsSignedIn;
        Status = _graph.IsSignedIn
            ? $"Signed in as {_graph.SignedInAccount}."
            : "Not signed in.";
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId))
        {
            Status = "Enter both the tenant ID and the client (application) ID.";
            return;
        }

        IsBusy = true;
        Status = "Opening the sign-in window…";
        try
        {
            _graph.Configure(TenantId.Trim(), ClientId.Trim());
            await _graph.SignInAsync();

            // Remember the identifiers (not secrets) for next launch.
            _settings.EntraTenantId = TenantId.Trim();
            _settings.EntraClientId = ClientId.Trim();
            _settingsStore.Save(_settings);

            RefreshState();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Entra ID interactive sign-in failed.", ex);
            Status = "Sign-in failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void SignOut()
    {
        _graph.SignOut();
        RefreshState();
    }
}
