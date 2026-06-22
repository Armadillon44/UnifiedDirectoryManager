using System.Security.Principal;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Formats raw AD attribute values (as returned by DirectorySearcher: string/int/long/byte[]/DateTime)
/// into human-readable display text. Knows the quirks of FILETIME integers, SIDs, GUIDs, and UAC flags.
/// </summary>
public static class AdValueFormatter
{
    private static readonly HashSet<string> FileTimeAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "pwdLastSet", "accountExpires", "lastLogon", "lastLogonTimestamp",
        "lockoutTime", "badPasswordTime", "lastLogoff",
    };

    [Flags]
    public enum Uac
    {
        AccountDisable = 0x0002,
        Lockout = 0x0010,
        PasswordNotRequired = 0x0020,
        NormalAccount = 0x0200,
        DontExpirePassword = 0x10000,
        SmartcardRequired = 0x40000,
        PasswordExpired = 0x800000,
    }

    public static bool IsAccountDisabled(int userAccountControl) =>
        (userAccountControl & (int)Uac.AccountDisable) != 0;

    /// <summary>Formats a single raw value for the given attribute.</summary>
    public static string Format(string ldapName, object? raw)
    {
        switch (raw)
        {
            case null:
                return string.Empty;

            case byte[] bytes when ldapName.Equals("objectSid", StringComparison.OrdinalIgnoreCase):
                try { return new SecurityIdentifier(bytes, 0).ToString(); } catch { return ToHex(bytes); }

            case byte[] guid when ldapName.Equals("objectGUID", StringComparison.OrdinalIgnoreCase) && guid.Length == 16:
                return new Guid(guid).ToString();

            case byte[] other:
                return other.Length > 32 ? $"(binary, {other.Length} bytes)" : ToHex(other);

            case long l when FileTimeAttributes.Contains(ldapName):
                return FormatFileTime(l);

            case long l:
                return l.ToString();

            case int i when ldapName.Equals("userAccountControl", StringComparison.OrdinalIgnoreCase):
                return FormatUac(i);

            case DateTime dt:
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

            default:
                return raw.ToString() ?? string.Empty;
        }
    }

    /// <summary>Joins multiple raw values into one display string.</summary>
    public static string FormatMulti(string ldapName, IEnumerable<object?> values) =>
        string.Join("; ", values.Select(v => Format(ldapName, v)).Where(s => s.Length > 0));

    public static string FormatFileTime(long value)
    {
        if (value <= 0) return "(not set)";
        if (value == long.MaxValue) return "Never";
        try { return DateTime.FromFileTimeUtc(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
        catch { return "Never"; }
    }

    public static string FormatUac(int uac)
    {
        var state = IsAccountDisabled(uac) ? "Disabled" : "Enabled";
        var flags = new List<string>();
        if ((uac & (int)Uac.Lockout) != 0) flags.Add("Locked out");
        if ((uac & (int)Uac.DontExpirePassword) != 0) flags.Add("Password never expires");
        if ((uac & (int)Uac.PasswordNotRequired) != 0) flags.Add("Password not required");
        if ((uac & (int)Uac.SmartcardRequired) != 0) flags.Add("Smartcard required");
        return flags.Count > 0 ? $"{state} ({string.Join(", ", flags)})" : state;
    }

    private static string ToHex(byte[] bytes) => "0x" + Convert.ToHexString(bytes);
}
