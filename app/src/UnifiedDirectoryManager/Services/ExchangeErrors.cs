namespace UnifiedDirectoryManager.Services;

/// <summary>
/// An Exchange Online operation failed. Carries a message already made readable for the UI
/// (mirrors how the Graph layer surfaces <c>ODataError</c> messages).
/// </summary>
public sealed class ExchangeException : Exception
{
    public ExchangeException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Turns Exchange/PowerShell error text into a readable one-line message for the UI.</summary>
public static class ExchangeErrors
{
    public static string Friendly(string? message) => Humanize(message);

    public static string Friendly(Exception ex) => Humanize(ex.Message);

    private static string Humanize(string? msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "The Exchange Online operation failed.";

        // Map the few error shapes operators will actually hit to plain guidance.
        if (msg.Contains("ManagementObjectNotFound", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("couldn't be found", StringComparison.OrdinalIgnoreCase))
        {
            // Keep the identity Exchange named. Creating a group with several members means several candidates
            // for "not found", and a message that names none of them leaves the operator guessing which.
            var who = QuotedIdentity(msg);
            return who is null
                ? "That mailbox or recipient could not be found in Exchange Online."
                : $"This mailbox or recipient could not be found in Exchange Online: {who}";
        }

        // RBAC failures KEEP Exchange's own text and get a hint appended, rather than being replaced by one.
        //
        // Two reasons the previous fixed sentence was worse than nothing. It named Recipient Management, which
        // does not carry group creation (that needs Distribution Groups, and the member ops' hardcoded
        // -BypassSecurityGroupManagerCheck needs Organization Management or Security Group Creation and
        // Membership) — so it confidently sent the operator to the wrong role. And it discarded the original
        // message, which is exactly what a tenant policy rejection needs: group-creation and naming-policy
        // errors are the app's problem to surface verbatim, not to reinterpret.
        //
        // A bare "insufficient" is deliberately NOT matched for the same reason: policy rejections contain it,
        // and rewriting one as a permissions problem is the failure mode this branch used to cause.
        if (msg.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("insufficient access rights", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("insufficient permission", StringComparison.OrdinalIgnoreCase))
            return msg.Trim() +
                   " — the signed-in admin is probably missing the Exchange management role this particular " +
                   "action needs (they differ per operation; see the README).";

        return msg.Trim();
    }

    /// <summary>
    /// Pulls the object identity out of an Exchange "couldn't be found" message, which quotes it — usually with
    /// typographic quotes ("…"), sometimes straight ones. Returns null when nothing quoted is present, so the
    /// caller can fall back to the generic wording rather than printing an empty pair of quotes.
    /// </summary>
    private static string? QuotedIdentity(string message)
    {
        foreach (var (open, close) in new[] { ('“', '”'), ('\'', '\''), ('"', '"') })
        {
            var start = message.IndexOf(open);
            if (start < 0) continue;
            var end = message.IndexOf(close, start + 1);
            if (end <= start + 1) continue;
            var inner = message[(start + 1)..end].Trim();
            if (inner.Length > 0) return "“" + inner + "”";
        }
        return null;
    }
}
