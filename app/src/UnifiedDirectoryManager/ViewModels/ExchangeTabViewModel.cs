using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Backs the edit-pane "Exchange" tab: a read-only view of the selected user's Exchange Online mailbox
/// (type + forwarding), resolved by UPN via <see cref="IExchangeService"/>. Like the Cloud tab, it never
/// auto-fetches on selection — the admin clicks "Look up mailbox" — because connecting to Exchange Online
/// is comparatively slow. Sign-in is inherited from the Entra sign-in (the Exchange token is borrowed from
/// it), so the tab reports the Graph sign-in state. Write actions (convert/forwarding) come in a later pass.
/// </summary>
public partial class ExchangeTabViewModel : ObservableObject
{
    private readonly IExchangeService _exchange;
    private readonly IGraphService _graph;

    private string? _identity; // the mailbox identity (UPN/primary SMTP) of the selected user

    [ObservableProperty] private string? _signedInAccount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasResult;

    // Read-only mailbox detail (populated on a successful look-up).
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _primarySmtpAddress = string.Empty;
    [ObservableProperty] private string _mailboxType = string.Empty;
    [ObservableProperty] private string _forwarding = "(none)";

    public ExchangeTabViewModel(IExchangeService exchange, IGraphService graph)
    {
        _exchange = exchange;
        _graph = graph;
        RefreshStatus();
    }

    /// <summary>Points the tab at the selected user's mailbox identity (called from the edit pane on load).</summary>
    public void SetTarget(AdObjectType type, string? identity)
    {
        _identity = type == AdObjectType.User && !string.IsNullOrWhiteSpace(identity) ? identity!.Trim() : null;
        HasResult = false;
        ClearDetail();
        RefreshStatus();
    }

    public void Reset() => SetTarget(AdObjectType.Unknown, null);

    private void ClearDetail()
    {
        DisplayName = string.Empty;
        PrimarySmtpAddress = string.Empty;
        MailboxType = string.Empty;
        Forwarding = "(none)";
    }

    private void RefreshStatus()
    {
        SignedInAccount = _graph.SignedInAccount;
        if (!_exchange.IsConfigured)
            StatusMessage = "Exchange Online isn't configured — set the tenant under File ▸ Settings ▸ Cloud.";
        else if (!_graph.IsSignedIn)
            StatusMessage = "Not signed in to Entra ID — sign in under File ▸ Settings ▸ Cloud.";
        else if (_identity is null)
            StatusMessage = "This user has no mailbox identity (userPrincipalName) to look up.";
        else if (!HasResult)
            StatusMessage = $"Signed in as {SignedInAccount}. Click Look up mailbox to read the Exchange Online mailbox.";
    }

    [RelayCommand]
    private async Task LookUpMailboxAsync()
    {
        RefreshStatus();
        if (!_exchange.IsConfigured) return;
        if (!_graph.IsSignedIn) return;
        if (_identity is null) return;

        IsBusy = true;
        HasResult = false;
        ClearDetail();
        StatusMessage = "Looking up mailbox… (the first Exchange Online connection can take a few seconds)";
        try
        {
            var mb = await _exchange.GetMailboxAsync(_identity);
            if (mb is null)
            {
                StatusMessage = $"No Exchange Online mailbox found for {_identity}.";
                return;
            }

            DisplayName = mb.DisplayName;
            PrimarySmtpAddress = mb.PrimarySmtpAddress;
            MailboxType = mb.Type.ToString();
            Forwarding = mb.HasForwarding
                ? mb.ForwardingAddress + (mb.DeliverToMailboxAndForward ? " (also delivered to mailbox)" : " (forward only)")
                : "(none)";
            HasResult = true;
            StatusMessage = $"Showing the Exchange Online mailbox for {(string.IsNullOrWhiteSpace(mb.DisplayName) ? _identity : mb.DisplayName)}.";
        }
        catch (Exception ex)
        {
            // ExchangeException messages are already humanized (e.g. missing consent / RBAC); others fall back to the message.
            AppLog.Instance.Error("Exchange mailbox look-up failed.", ex);
            StatusMessage = "Look-up failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
