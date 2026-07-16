namespace UnifiedDirectoryManager.Models;

/// <summary>The mailbox delegate permission types (a flags set, since a delegate can hold several).</summary>
[Flags]
public enum DelegateAccess
{
    None = 0,
    /// <summary>Open the mailbox and read/manage its contents (Add-MailboxPermission -AccessRights FullAccess).</summary>
    FullAccess = 1,
    /// <summary>Send so mail appears to come FROM the mailbox (Add-RecipientPermission -AccessRights SendAs).</summary>
    SendAs = 2,
    /// <summary>Send as "delegate on behalf of the mailbox" (Set-Mailbox -GrantSendOnBehalfTo).</summary>
    SendOnBehalf = 4,
}

/// <summary>
/// A delegate on a mailbox — a recipient granted one or more of Full Access / Send As / Send on Behalf.
/// A projection that merges the three separate Exchange permission sources into one row per delegate.
/// </summary>
public sealed class MailboxDelegate
{
    /// <summary>The delegate's identity (primary SMTP address) for grant/remove operations.</summary>
    public string Identity { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool FullAccess { get; init; }
    public bool SendAs { get; init; }
    public bool SendOnBehalf { get; init; }

    /// <summary>The permissions this delegate holds, as a flags set.</summary>
    public DelegateAccess Access =>
        (FullAccess ? DelegateAccess.FullAccess : 0)
        | (SendAs ? DelegateAccess.SendAs : 0)
        | (SendOnBehalf ? DelegateAccess.SendOnBehalf : 0);

    /// <summary>Human-readable list of the delegate's permissions (for the UI).</summary>
    public string AccessSummary
    {
        get
        {
            var parts = new List<string>(3);
            if (FullAccess) parts.Add("Full Access");
            if (SendAs) parts.Add("Send As");
            if (SendOnBehalf) parts.Add("Send on Behalf");
            return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
        }
    }
}
