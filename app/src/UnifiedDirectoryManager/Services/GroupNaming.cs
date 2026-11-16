namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Rules and suggestions for group names and addresses that need no service: what each store accepts, and how a
/// rename carries a group's existing conventions over to its new name.
/// </summary>
public static class GroupNaming
{
    /// <summary>AD's upper bound on <c>cn</c>.</summary>
    public const int MaxCommonName = 64;
    /// <summary>The SAM API's upper bound on a group's pre-Windows 2000 name (a user's is 20).</summary>
    public const int MaxGroupSam = 256;
    /// <summary>AD's and Entra's upper bound on a display name.</summary>
    public const int MaxDisplayName = 256;
    /// <summary>Exchange's upper bound on a recipient's <c>Name</c>.</summary>
    public const int MaxExchangeName = 64;

    /// <summary>The characters AD refuses in a pre-Windows 2000 name.</summary>
    private const string SamForbidden = "\"/\\[]:;|=,+*?<>";

    /// <summary>
    /// Derives a group's pre-Windows 2000 name from its name the way ADUC does: keep the name as typed (case,
    /// spaces and hyphens are all legal in a group's sAMAccountName) and drop only what AD forbids. Deliberately
    /// NOT <see cref="UserAttributeBuilder.SanitizeSam"/>, which builds a PERSON's logon name and would turn
    /// "IT-Admins" into "itadmins". Shared by New Group and Rename so the two derive the same name.
    /// </summary>
    public static string DeriveGroupSam(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            if (SamForbidden.IndexOf(c) < 0 && !char.IsControl(c)) sb.Append(c);
        // AD rejects a logon name that ends with a period or is only whitespace.
        return sb.ToString().Trim().TrimEnd('.').Trim();
    }

    /// <summary>Why AD would refuse this pre-Windows 2000 name for a group, or null when it would accept it.</summary>
    public static string? SamProblem(string sam)
    {
        if (string.IsNullOrWhiteSpace(sam)) return "Enter a pre-Windows 2000 name.";
        if (sam.Length > MaxGroupSam) return $"The pre-Windows 2000 name is {sam.Length} characters; AD allows {MaxGroupSam}.";
        var bad = sam.Where(c => SamForbidden.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The pre-Windows 2000 name can't contain {string.Join(" ", bad.Select(c => char.IsControl(c) ? "control characters" : c.ToString()))}.";
        if (sam.TrimEnd().EndsWith('.')) return "The pre-Windows 2000 name can't end with a period.";
        return null;
    }

    /// <summary>Why a name can't be used, or null. Control characters would be stored but cannot be typed back.</summary>
    public static string? NameProblem(string name, string what, int max)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"Enter a {what}.";
        if (name.Length > max) return $"The {what} is {name.Length} characters; the limit is {max}.";
        if (name.Any(char.IsControl)) return $"The {what} can't contain control characters.";
        return null;
    }

    /// <summary>Why an address is not a usable SMTP address, or null.</summary>
    public static string? AddressProblem(string address)
    {
        var a = (address ?? string.Empty).Trim();
        if (a.Length == 0) return "Enter the new email address.";
        // A type prefix is exactly how an address becomes the primary by accident: "SMTP:" on any entry promotes it.
        if (a.Contains(':')) return $"Type the address without a prefix: “{a}”.";
        var at = a.IndexOf('@');
        if (at <= 0 || at != a.LastIndexOf('@') || at == a.Length - 1 || a.Any(char.IsWhiteSpace))
            return $"“{a}” is not a valid email address.";
        if (!a[(at + 1)..].Contains('.')) return $"“{a}” has no domain after the @.";
        return null;
    }

    public static string LocalPart(string address)
    {
        var at = address.IndexOf('@');
        return at < 0 ? address : address[..at];
    }

    public static string Domain(string address)
    {
        var at = address.IndexOf('@');
        return at < 0 ? string.Empty : address[(at + 1)..];
    }

    /// <summary>The ways a group's address is commonly made from its name, tried in this order.</summary>
    private static readonly Func<string, string>[] AddressStyles =
    {
        s => s.Replace(" ", string.Empty),
        s => s.Replace(" ", "."),
        s => s.Replace(" ", "-"),
        s => s.Replace(" ", "_"),
    };

    /// <summary>
    /// Suggests a new primary address by carrying the group's own convention over to its new name: if the old
    /// address is the old name with its spaces removed, or turned into dots, hyphens or underscores, the new one
    /// is built the same way, at the same domain. Null when the old address follows none of those, because a
    /// guess the operator would have to spot and undo is worse than an empty box.
    /// </summary>
    public static string? SuggestAddress(string oldName, string? oldPrimary, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldPrimary) || string.IsNullOrWhiteSpace(newName)) return null;
        var local = LocalPart(oldPrimary);
        var domain = Domain(oldPrimary);
        if (domain.Length == 0) return null;
        foreach (var style in AddressStyles)
        {
            if (!string.Equals(Clean(style(oldName.Trim())), local, StringComparison.OrdinalIgnoreCase)) continue;
            var made = Clean(style(newName.Trim()));
            if (made.Length == 0) return null;
            // Keep the old address's case: almost always all lower case.
            if (local == local.ToLowerInvariant()) made = made.ToLowerInvariant();
            return made + "@" + domain;
        }
        return null;

        // What an address can hold of a name: letters, digits, and the few separators that are safe everywhere.
        static string Clean(string s) =>
            new(s.Where(c => c < 128 && (char.IsLetterOrDigit(c) || c is '.' or '-' or '_')).ToArray());
    }

    /// <summary>
    /// The <c>proxyAddresses</c> an on-prem group should hold after its primary address changes: the new address
    /// as the primary, the old primary demoted to a secondary so mail sent to it still arrives, and every other
    /// entry (secondaries, X500, SIP, routing addresses) exactly as it was.
    /// </summary>
    public static IReadOnlyList<string> PlanProxyAddresses(IReadOnlyList<string> current, string newPrimary)
    {
        var result = new List<string> { "SMTP:" + newPrimary.Trim() };
        foreach (var entry in current ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var isSmtp = entry.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase);
            // The new address is already in the list (as a secondary, say): it is the primary now, not twice over.
            if (isSmtp && string.Equals(entry[5..], newPrimary.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            // Case-sensitive on purpose: uppercase SMTP: is the primary, and only that one is demoted.
            result.Add(entry.StartsWith("SMTP:", StringComparison.Ordinal) ? "smtp:" + entry[5..] : entry);
        }
        return result;
    }

    /// <summary>The primary address in a prefixed list: the one entry whose prefix is uppercase "SMTP:".</summary>
    public static string? PrimaryOf(IEnumerable<string>? addresses) =>
        addresses?.FirstOrDefault(a => a.StartsWith("SMTP:", StringComparison.Ordinal)) is { } p ? p[5..] : null;

    /// <summary>True when <paramref name="addresses"/> holds <paramref name="address"/> as any kind of SMTP address.</summary>
    public static bool Holds(IEnumerable<string>? addresses, string address) =>
        addresses?.Any(a => a.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(a[5..], address, StringComparison.OrdinalIgnoreCase)) == true;
}
