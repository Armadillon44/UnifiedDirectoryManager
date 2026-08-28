namespace UnifiedDirectoryManager.Models;

/// <summary>
/// A mail-enabled recipient returned by recipient search — used to pick the internal target of
/// a forwarding rule (a user, shared mailbox, or distribution group). A thin projection of
/// Get-Recipient.
/// </summary>
public sealed class MailboxRecipient
{
    /// <summary>The identity to pass to Set-Mailbox -ForwardingAddress (the primary SMTP address).</summary>
    public string Identity { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string PrimarySmtpAddress { get; init; } = string.Empty;

    /// <summary>The Exchange RecipientTypeDetails (e.g. UserMailbox, SharedMailbox, MailUniversalDistributionGroup), for display.</summary>
    public string RecipientType { get; init; } = string.Empty;
}

/// <summary>
/// One recipient-valued property, resolved to addresses. The three outcomes are kept apart deliberately:
/// an entry Exchange could not name is a deleted account, while one that was never looked up is this app's
/// own budget running out. Reporting the second as the first tells an operator something untrue about their
/// directory, and the fix for each is different.
/// </summary>
/// <param name="Entries">Every entry, in order, whether or not it resolved.</param>
/// <param name="Unresolved">Display names of entries Exchange could not give an addressable identity for.</param>
/// <param name="NotLookedUp">How many entries were left unresolved because the list was longer than one read resolves.</param>
public sealed record DlRecipientList(
    IReadOnlyList<MailboxRecipient> Entries,
    IReadOnlyList<string> Unresolved,
    int NotLookedUp);
