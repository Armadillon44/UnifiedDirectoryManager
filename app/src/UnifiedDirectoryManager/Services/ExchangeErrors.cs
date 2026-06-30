using System.Management.Automation;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// An Exchange Online operation failed. Carries a message already made readable for the UI
/// (mirrors how the Graph layer surfaces <c>ODataError</c> messages).
/// </summary>
public sealed class ExchangeException : Exception
{
    public ExchangeException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Turns PowerShell/Exchange errors into a readable one-line message for the UI.</summary>
public static class ExchangeErrors
{
    /// <summary>The most specific message available from a PowerShell error record.</summary>
    public static string Friendly(ErrorRecord error)
    {
        // The exception message is usually the cmdlet's own text; fall back to the record's string form.
        var msg = error.Exception?.Message;
        if (string.IsNullOrWhiteSpace(msg)) msg = error.ToString();
        return Humanize(msg);
    }

    public static string Friendly(Exception ex) => Humanize(ex.Message);

    private static string Humanize(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "The Exchange Online operation failed.";

        // Map the few error shapes operators will actually hit to plain guidance.
        if (msg.Contains("ManagementObjectNotFound", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("couldn't be found", StringComparison.OrdinalIgnoreCase))
            return "That mailbox or recipient could not be found in Exchange Online.";

        if (msg.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
            return "Access denied. The signed-in admin needs an Exchange role (e.g. Recipient Management) for this action.";

        return msg.Trim();
    }
}
