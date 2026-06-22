using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Backs the edit-pane "Cloud" tab, unifying a synced on-prem object with its Entra ID twin. It resolves
/// the cloud object by the right key (user → UPN, group → on-prem SID, computer → display name) and then
/// shows the SAME comprehensive read-only detail (and cloud user actions) as the standalone cloud section,
/// via a hosted <see cref="CloudObjectDetailViewModel"/>. Sign-in is configured in File ▸ Settings.
/// </summary>
public partial class CloudTabViewModel : ObservableObject
{
    private readonly IGraphService _graph;

    private AdObjectType _type;
    private string? _upn;
    private string? _sid;
    private string? _computerName;

    [ObservableProperty] private string? _signedInAccount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasResult;

    /// <summary>The resolved cloud object's full detail (same view as the cloud section).</summary>
    public CloudObjectDetailViewModel Detail { get; }

    public CloudTabViewModel(IGraphService graph, IDialogService dialogs)
    {
        _graph = graph;
        Detail = new CloudObjectDetailViewModel(graph, dialogs);
        RefreshSignInDisplay();
    }

    /// <summary>Points the tab at the selected on-prem object's identity (called from the edit pane on load).</summary>
    public void SetTarget(AdObjectType type, string? upn, string? onPremSid, string? computerName)
    {
        _type = type;
        _upn = Trim(upn);
        _sid = Trim(onPremSid);
        _computerName = Trim(computerName);
        HasResult = false;
        Detail.Reset();
        RefreshSignInDisplay();
    }

    public void Reset() => SetTarget(AdObjectType.Unknown, null, null, null);

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private string? IdentityForType() => _type switch
    {
        AdObjectType.User => _upn,
        AdObjectType.Group => _sid,
        AdObjectType.Computer => _computerName,
        _ => null,
    };

    private void RefreshSignInDisplay()
    {
        SignedInAccount = _graph.SignedInAccount;
        if (!_graph.IsSignedIn)
            StatusMessage = "Not signed in to Entra ID — sign in under File ▸ Settings ▸ Cloud.";
        else if (IdentityForType() is null)
            StatusMessage = "This object has no key to match in Entra ID.";
        else if (!HasResult)
            StatusMessage = $"Signed in as {SignedInAccount}. Click Look up in Entra ID to find the synced object.";
    }

    [RelayCommand]
    private async Task LookUpAsync()
    {
        RefreshSignInDisplay();
        if (!_graph.IsSignedIn) { StatusMessage = "Not signed in to Entra ID — sign in under File ▸ Settings ▸ Cloud."; return; }
        if (IdentityForType() is null) { StatusMessage = "This object has no key to match in Entra ID."; return; }

        IsBusy = true;
        HasResult = false;
        Detail.Reset();
        StatusMessage = "Looking up in Entra ID…";
        try
        {
            var row = _type switch
            {
                AdObjectType.User => await BuildUserRowAsync(),
                AdObjectType.Group => await BuildGroupRowAsync(),
                AdObjectType.Computer => await BuildDeviceRowAsync(),
                _ => null,
            };

            if (row is null)
            {
                StatusMessage = "No matching Entra ID object found (it may not be synced to the cloud).";
                return;
            }

            Detail.SetTarget(row);
            HasResult = true;
            StatusMessage = $"Showing Entra ID details for {row.DisplayName}.";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Entra ID lookup failed.", ex);
            StatusMessage = "Lookup failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task<CloudObjectRow?> BuildUserRowAsync()
    {
        var info = await _graph.GetUserByUpnAsync(_upn!);
        if (info is null) return null;
        var row = new CloudObjectRow { Id = info.Id, DisplayName = info.DisplayName ?? _upn!, Kind = CloudObjectKind.User };
        row.Values["userPrincipalName"] = info.UserPrincipalName ?? _upn!;
        row.Values["accountEnabled"] = info.AccountEnabled switch { true => "Yes", false => "No", _ => string.Empty };
        return row;
    }

    private async Task<CloudObjectRow?> BuildGroupRowAsync()
    {
        var g = await _graph.GetGroupByOnPremSidAsync(_sid!);
        return g is null ? null : new CloudObjectRow { Id = g.Id, DisplayName = g.DisplayName, Kind = CloudObjectKind.Group };
    }

    private async Task<CloudObjectRow?> BuildDeviceRowAsync()
    {
        var d = (await _graph.GetDevicesByComputerAsync(_computerName!, _sid)).FirstOrDefault();
        if (d is null) return null;
        var row = new CloudObjectRow { Id = d.Id, DisplayName = d.DisplayName ?? _computerName!, Kind = CloudObjectKind.Device };
        row.Values["accountEnabled"] = d.AccountEnabled switch { true => "Yes", false => "No", _ => string.Empty };
        return row;
    }
}
