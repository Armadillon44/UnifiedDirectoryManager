using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>Drives the connection dialog: domain, DCs (manual + best-effort discovery), credentials.</summary>
public partial class ConnectionViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IDomainLocator _locator;
    private readonly ICredentialStore _credentials;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;

    [ObservableProperty] private string _domainFqdn = string.Empty;
    [ObservableProperty] private string _primaryDc = string.Empty;
    [ObservableProperty] private string _fallbackDcsText = string.Empty;
    [ObservableProperty] private bool _useLdaps;
    [ObservableProperty] private bool _ignoreCertificateErrors;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _saveCredentials = true;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Plain password, set from the PasswordBox code-behind (PasswordBox can't be bound).</summary>
    public string Password { get; set; } = string.Empty;

    public ObservableCollection<string> DiscoveredDcs { get; } = new();

    public bool Connected { get; private set; }
    public event EventHandler? ConnectionSucceeded;

    public ConnectionViewModel(IDirectoryService directory, IDomainLocator locator, ICredentialStore credentials,
        ISettingsStore settingsStore, AppSettings settings)
    {
        _directory = directory;
        _locator = locator;
        _credentials = credentials;
        _settingsStore = settingsStore;
        _settings = settings;

        // Prefer the last successful connection so the user doesn't re-enter (or re-discover) the DC.
        if (!string.IsNullOrWhiteSpace(settings.LastDomainFqdn))
        {
            DomainFqdn = settings.LastDomainFqdn!;
            PrimaryDc = settings.LastPrimaryDc ?? string.Empty;
            FallbackDcsText = string.Join(Environment.NewLine, settings.LastFallbackDcs);
            UseLdaps = settings.LastUseLdaps;
            if (!string.IsNullOrWhiteSpace(settings.LastUsername))
                Username = settings.LastUsername!;
        }
        else
        {
            // Best-effort prefill — only if the machine happens to expose a domain (absent on Entra-only).
            var envDomain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
            if (!string.IsNullOrWhiteSpace(envDomain))
                DomainFqdn = envDomain.ToLowerInvariant();
        }
    }

    partial void OnDomainFqdnChanged(string value) => TryLoadSavedCredentials();

    /// <summary>Loads any saved credential for the entered domain so the user doesn't retype it.</summary>
    public void TryLoadSavedCredentials()
    {
        if (string.IsNullOrWhiteSpace(DomainFqdn)) return;
        try
        {
            var saved = _credentials.TryLoad(DomainFqdn);
            if (saved is not null)
            {
                Username = saved.Username;
                Password = saved.Password;
                SaveCredentials = true;
                CredentialsLoaded?.Invoke(this, EventArgs.Empty);
            }
        }
        catch { /* a vault read failure must not block the dialog */ }
    }

    /// <summary>Raised when a saved password was loaded so the view can populate the PasswordBox.</summary>
    public event EventHandler? CredentialsLoaded;

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        IsBusy = true;
        Status = "Discovering domain controllers…";
        try
        {
            var result = await _locator.LocateAsync(DomainFqdn);
            DiscoveredDcs.Clear();
            foreach (var dc in result.DomainControllers) DiscoveredDcs.Add(dc);
            if (string.IsNullOrWhiteSpace(PrimaryDc) && result.Found)
                PrimaryDc = result.DomainControllers[0];
            Status = result.Status;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(DomainFqdn)) { Status = "Enter the domain FQDN."; return; }
        if (string.IsNullOrWhiteSpace(Username)) { Status = "Enter a username (DOMAIN\\user or user@domain)."; return; }
        if (string.IsNullOrEmpty(Password)) { Status = "Enter a password."; return; }

        var profile = new ConnectionProfile
        {
            DomainFqdn = DomainFqdn.Trim(),
            PrimaryDc = PrimaryDc.Trim(),
            UseLdaps = UseLdaps,
            IgnoreCertificateErrors = IgnoreCertificateErrors,
            Username = Username.Trim(),
            SaveCredentials = SaveCredentials,
            FallbackDcs = ParseList(FallbackDcsText),
        };

        // Fold any discovered DCs in as additional fall-backs.
        foreach (var dc in DiscoveredDcs)
            if (!string.Equals(dc, profile.PrimaryDc, StringComparison.OrdinalIgnoreCase) && !profile.FallbackDcs.Contains(dc))
                profile.FallbackDcs.Add(dc);

        if (string.IsNullOrWhiteSpace(profile.PrimaryDc))
        {
            if (profile.FallbackDcs.Count > 0)
            {
                profile.PrimaryDc = profile.FallbackDcs[0];
                profile.FallbackDcs.RemoveAt(0);
            }
            else { Status = "Enter a primary DC hostname or IP (discovery found none)."; return; }
        }

        IsBusy = true;
        Status = "Connecting…";
        try
        {
            await _directory.ConnectAsync(profile, Password);

            if (SaveCredentials) _credentials.Save(profile.DomainFqdn, profile.Username, Password);
            else _credentials.Delete(profile.DomainFqdn);

            // Remember this connection so the next launch defaults to the same DC.
            _settings.LastDomainFqdn = profile.DomainFqdn;
            _settings.LastPrimaryDc = _directory.Current!.Server; // the DC we actually bound to
            _settings.LastFallbackDcs = profile.FallbackDcs;
            _settings.LastUseLdaps = profile.UseLdaps;
            _settings.LastUsername = profile.Username;
            _settingsStore.Save(_settings);

            Status = $"Connected to {_directory.Current!.Server}.";
            Connected = true;
            ConnectionSucceeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private static List<string> ParseList(string text) =>
        text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
