namespace UnifiedDirectoryManager.Models;

/// <summary>
/// The Exchange Online mailbox state the app reads and displays (a small projection of
/// Get-Mailbox). Forwarding here is the internal-recipient form (<c>ForwardingAddress</c>);
/// external SMTP forwarding is intentionally out of scope for v2.0.
/// </summary>
public sealed class MailboxInfo
{
    /// <summary>The mailbox identity the app addresses it by (the UPN / primary SMTP).</summary>
    public string Identity { get; init; } = string.Empty;

    /// <summary>Display name, for the UI.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Primary SMTP address.</summary>
    public string PrimarySmtpAddress { get; init; } = string.Empty;

    /// <summary>Regular, Shared, or Unknown (mapped from RecipientTypeDetails).</summary>
    public MailboxType Type { get; init; } = MailboxType.Unknown;

    /// <summary>The internal forwarding target's display name (from ForwardingAddress); null/empty when no forwarding is set.</summary>
    public string? ForwardingAddress { get; init; }

    /// <summary>True when mail is delivered to this mailbox AND forwarded; false when only forwarded.</summary>
    public bool DeliverToMailboxAndForward { get; init; }

    /// <summary>True when an internal forwarding target is set.</summary>
    public bool HasForwarding => !string.IsNullOrWhiteSpace(ForwardingAddress);
}
