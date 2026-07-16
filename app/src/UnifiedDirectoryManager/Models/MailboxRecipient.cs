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
