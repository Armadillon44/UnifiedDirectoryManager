using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Backs the edit-pane "ExOL" (Exchange Online) tab: reads the selected user's mailbox (type + forwarding)
/// and runs mailbox actions — convert to/from a shared mailbox and set/clear internal forwarding — resolved
/// by UPN via <see cref="IExchangeService"/>. Like the Cloud tab it never auto-fetches on selection (the admin
/// clicks "Look up mailbox") because connecting to Exchange Online is comparatively slow. Sign-in is inherited
/// from the Entra sign-in (the Exchange token is borrowed from it), so the tab reports the Graph sign-in state.
/// Every write confirms first and re-reads the mailbox afterward so the displayed state stays accurate.
/// </summary>
public partial class ExchangeTabViewModel : ObservableObject
{
    private readonly IExchangeService _exchange;
    private readonly IGraphService _graph;
    private readonly IDialogService _dialogs;

    private string? _identity; // the mailbox identity (UPN/primary SMTP) of the selected user

    [ObservableProperty] private string? _signedInAccount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasResult;

    // Read-only mailbox detail (populated on a successful look-up).
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _primarySmtpAddress = string.Empty;
    [ObservableProperty] private string _mailboxTypeText = string.Empty; // named ...Text to avoid colliding with the MailboxType enum
    [ObservableProperty] private string _forwarding = "(none)";

    /// <summary>True only when the loaded mailbox is Regular or Shared (i.e. convertible). Room/equipment
    /// (Unknown) mailboxes are out of scope, so the convert actions are hidden for them.</summary>
    [ObservableProperty] private bool _canConvert;

    /// <summary>For Set forwarding: also deliver a copy to the mailbox (vs. forward only). Seeded from the
    /// mailbox's current state on load so re-pointing forwarding doesn't silently drop retention.</summary>
    [ObservableProperty] private bool _deliverAndForward;

    public ExchangeTabViewModel(IExchangeService exchange, IGraphService graph, IDialogService dialogs)
    {
        _exchange = exchange;
        _graph = graph;
        _dialogs = dialogs;
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

    private string TargetLabel => HasResult && !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : _identity ?? "(unknown)";

    private void ClearDetail()
    {
        DisplayName = string.Empty;
        PrimarySmtpAddress = string.Empty;
        MailboxTypeText = string.Empty;
        Forwarding = "(none)";
        CanConvert = false;
        DeliverAndForward = false;
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
        if (!EnsureReady()) return;
        await RunAsync("Looking up mailbox… (the first Exchange Online connection can take a few seconds)", LoadCoreAsync);
    }

    [RelayCommand]
    private Task ConvertToSharedAsync() => ConvertAsync(MailboxType.Shared);

    [RelayCommand]
    private Task ConvertToRegularAsync() => ConvertAsync(MailboxType.Regular);

    private async Task ConvertAsync(MailboxType type)
    {
        if (IsBusy || !EnsureReady() || !CanConvert) return; // only Regular/Shared are convertible
        var target = type == MailboxType.Shared ? "shared" : "regular (user)";
        var note = type == MailboxType.Shared
            ? "The account can then be unlicensed without losing the mailbox."
            : "The mailbox becomes a normal user mailbox again (it needs a license to be usable).";
        if (!_dialogs.Confirm("Convert mailbox", $"Convert the mailbox for “{TargetLabel}” to a {target} mailbox?", new[] { note }))
            return;

        await RunWriteAsync($"Converting mailbox to {target}…", $"Converted mailbox to {target}.",
            () => _exchange.ConvertMailboxAsync(_identity!, type));
    }

    [RelayCommand]
    private async Task SetForwardingAsync()
    {
        if (IsBusy || !EnsureReady()) return;

        var picked = _dialogs.PickMailboxRecipient($"Forward “{TargetLabel}” to…");
        if (picked is null) return;

        var deliverNote = DeliverAndForward
            ? "A copy will also be kept in the mailbox."
            : "Mail will be forwarded only (not kept in the mailbox).";
        if (!_dialogs.Confirm("Set forwarding", $"Forward mail for “{TargetLabel}” to “{picked.DisplayName}”?", new[] { deliverNote }))
            return;

        await RunWriteAsync("Setting forwarding…", $"Forwarding set to {picked.DisplayName}.",
            () => _exchange.SetForwardingAsync(_identity!, picked.Identity, DeliverAndForward));
    }

    [RelayCommand]
    private async Task ClearForwardingAsync()
    {
        if (IsBusy || !EnsureReady()) return;
        if (!_dialogs.Confirm("Clear forwarding", $"Clear mailbox forwarding for “{TargetLabel}”?", new[] { "Forwarding will be removed." }))
            return;

        await RunWriteAsync("Clearing forwarding…", "Forwarding cleared.",
            () => _exchange.ClearForwardingAsync(_identity!));
    }

    /// <summary>Reads the mailbox and populates the display fields (or sets a not-found status).</summary>
    private async Task LoadCoreAsync()
    {
        var mb = await _exchange.GetMailboxAsync(_identity!);
        if (mb is null)
        {
            HasResult = false;
            ClearDetail();
            StatusMessage = $"No Exchange Online mailbox found for {_identity}.";
            return;
        }

        DisplayName = mb.DisplayName;
        PrimarySmtpAddress = mb.PrimarySmtpAddress;
        MailboxTypeText = mb.Type.ToString();
        CanConvert = mb.Type is MailboxType.Regular or MailboxType.Shared;
        Forwarding = mb.HasForwarding
            ? mb.ForwardingAddress + (mb.DeliverToMailboxAndForward ? " (also delivered to mailbox)" : " (forward only)")
            : "(none)";
        DeliverAndForward = mb.DeliverToMailboxAndForward; // mirror current state so re-pointing keeps retention
        HasResult = true;
        StatusMessage = $"Showing the Exchange Online mailbox for {(string.IsNullOrWhiteSpace(mb.DisplayName) ? _identity : mb.DisplayName)}.";
    }

    /// <summary>Guards actions: configured + signed in + a mailbox identity present. Sets a status if not.</summary>
    private bool EnsureReady()
    {
        RefreshStatus();
        return _exchange.IsConfigured && _graph.IsSignedIn && _identity is not null;
    }

    /// <summary>Runs an async action with the busy flag + a status line, humanizing failures.</summary>
    private async Task RunAsync(string busyStatus, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = busyStatus;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // ExchangeException messages are already humanized (missing consent/RBAC, not found, …); others fall back.
            AppLog.Instance.Error("Exchange Online action failed.", ex);
            StatusMessage = "Failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Runs a write, then re-reads the mailbox — reporting a write failure ("Failed: …") distinctly
    /// from a post-write refresh failure (the change DID apply, but the view couldn't refresh), so a succeeded
    /// write is never mislabelled as failed.</summary>
    private async Task RunWriteAsync(string busyStatus, string successStatus, Func<Task> write)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = busyStatus;
        try
        {
            await write();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Exchange Online write failed.", ex);
            StatusMessage = "Failed: " + ex.Message;
            IsBusy = false;
            return;
        }

        // The write committed; a failure from here on is only a display-refresh problem.
        try
        {
            await LoadCoreAsync();
            StatusMessage = successStatus;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Post-write mailbox refresh failed: " + ex.Message);
            StatusMessage = successStatus + " — couldn't refresh the view; click Look up mailbox.";
        }
        finally { IsBusy = false; }
    }
}
