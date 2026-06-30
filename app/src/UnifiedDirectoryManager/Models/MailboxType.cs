namespace UnifiedDirectoryManager.Models;

/// <summary>
/// The Exchange Online mailbox types this app manages. Only <see cref="Regular"/> and
/// <see cref="Shared"/> are convertible by the app (the v2.0 scope); other types
/// (room/equipment) surface as <see cref="Unknown"/> so the UI can show them read-only.
/// </summary>
public enum MailboxType
{
    /// <summary>A type the app doesn't manage (room, equipment, or an unrecognized RecipientTypeDetails).</summary>
    Unknown,
    /// <summary>A normal user mailbox (RecipientTypeDetails = UserMailbox).</summary>
    Regular,
    /// <summary>A shared mailbox (RecipientTypeDetails = SharedMailbox).</summary>
    Shared,
}
