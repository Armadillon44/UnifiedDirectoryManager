using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Resolves a new-user template plus per-user inputs into a concrete LDAP attribute set. This is the
/// single source of truth for the token logic ({first}, {last}, {sam}, …), the computed
/// sAMAccountName/cn, and the suggested mail/UPN/proxy values — shared by the single-user New User
/// wizard and the bulk-create engine so both produce identical accounts.
/// </summary>
public static class UserAttributeBuilder
{
    /// <summary>Per-user inputs layered over a template. String fields hold the effective values
    /// (already containing any suggestion the caller chose to seed); blank means "not specified".</summary>
    public sealed record Input
    {
        public required UserTemplate Template { get; init; }
        public string FirstName { get; init; } = string.Empty;
        public string MiddleName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string Initials { get; init; } = string.Empty;
        public string SamOverride { get; init; } = string.Empty;
        public string UpnSuffix { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string Upn { get; init; } = string.Empty;
        public string? ManagerDn { get; init; }
        public string ProxyAddressesText { get; init; } = string.Empty;
    }

    /// <summary>Template-derived suggestions for the editable mail/UPN/proxy fields.</summary>
    public sealed record Suggestions(string Email, string Upn, string ProxyText);

    /// <summary>The resolved attribute set plus the resolved (multi-valued) proxy addresses.</summary>
    public sealed record Built(IReadOnlyDictionary<string, string> Attributes, IReadOnlyList<string> Proxies);

    /// <summary>Computes the sanitized sAMAccountName from the override, the template pattern, or the default.</summary>
    public static string ComputeSam(Input i)
    {
        var samPattern = i.SamOverride;
        if (string.IsNullOrWhiteSpace(samPattern))
            i.Template.AttributeDefaults.TryGetValue("sAMAccountName", out samPattern);
        if (string.IsNullOrWhiteSpace(samPattern)) samPattern = "{first}.{last}";
        return SanitizeSam(Resolve(i, samPattern, sam: string.Empty));
    }

    /// <summary>Resolves the template's mail/UPN/proxy patterns into suggested values for the entered name.</summary>
    public static Suggestions Suggest(Input i)
    {
        var sam = ComputeSam(i);

        var email = i.Template.AttributeDefaults.TryGetValue("mail", out var mailPat)
            ? Resolve(i, mailPat, sam) : string.Empty;
        var upn = i.Template.AttributeDefaults.TryGetValue("userPrincipalName", out var upnPat)
            ? Resolve(i, upnPat, sam)
            : (!string.IsNullOrWhiteSpace(i.UpnSuffix) && !string.IsNullOrWhiteSpace(sam) ? $"{sam}@{i.UpnSuffix.Trim()}" : string.Empty);
        var proxy = string.Join(Environment.NewLine,
            i.Template.ProxyAddressPatterns.Select(p => Resolve(i, p, sam)).Where(s => !string.IsNullOrWhiteSpace(s)));

        return new Suggestions(email, upn, proxy);
    }

    /// <summary>Resolves the template defaults + computed cn/sam/upn + explicit overrides into a concrete attribute set.</summary>
    public static Built Build(Input i)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sam = ComputeSam(i);

        foreach (var (ldap, pattern) in i.Template.AttributeDefaults)
        {
            if (ldap.Equals("sAMAccountName", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Resolve(i, pattern, sam);
            if (!string.IsNullOrWhiteSpace(value)) result[ldap] = value;
        }

        // Ensure the essentials exist.
        result["sAMAccountName"] = sam;
        if (!result.ContainsKey("givenName") && !string.IsNullOrWhiteSpace(i.FirstName)) result["givenName"] = i.FirstName.Trim();
        if (!result.ContainsKey("sn") && !string.IsNullOrWhiteSpace(i.LastName)) result["sn"] = i.LastName.Trim();
        if (!result.ContainsKey("middleName") && !string.IsNullOrWhiteSpace(i.MiddleName)) result["middleName"] = i.MiddleName.Trim();
        if (!result.ContainsKey("displayName")) result["displayName"] = $"{i.FirstName} {i.LastName}".Trim();
        if (!result.ContainsKey("cn")) result["cn"] = result.TryGetValue("displayName", out var dn) && !string.IsNullOrWhiteSpace(dn) ? dn : sam;
        if (!result.ContainsKey("userPrincipalName") && !string.IsNullOrWhiteSpace(i.UpnSuffix)) result["userPrincipalName"] = $"{sam}@{i.UpnSuffix.Trim()}";

        // Explicit Email / UPN fields override whatever the template produced.
        if (!string.IsNullOrWhiteSpace(i.Email)) result["mail"] = i.Email.Trim();
        if (!string.IsNullOrWhiteSpace(i.Upn)) result["userPrincipalName"] = i.Upn.Trim();

        // Manager (DN): the explicit value is authoritative — overrides any template default, or clears it.
        if (!string.IsNullOrWhiteSpace(i.ManagerDn)) result["manager"] = i.ManagerDn;
        else result.Remove("manager");

        var attributes = result.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                               .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return new Built(attributes, ResolveProxies(i.ProxyAddressesText));
    }

    /// <summary>Splits the multi-line proxy-addresses text into trimmed, non-empty entries.</summary>
    public static IReadOnlyList<string> ResolveProxies(string proxyText) =>
        proxyText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Resolve(Input i, string pattern, string sam)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;
        return Regex.Replace(pattern,
            "{(first|last|middle|firstInitial|lastInitial|middleInitial|initials|sam|upnSuffix)}",
            m => m.Groups[1].Value.ToLowerInvariant() switch
            {
                "first" => i.FirstName.Trim(),
                "last" => i.LastName.Trim(),
                "middle" => i.MiddleName.Trim(),
                "firstinitial" => Initial(i.FirstName),
                "lastinitial" => Initial(i.LastName),
                "middleinitial" => Initial(i.MiddleName),
                "initials" => i.Initials.Trim(),
                "sam" => sam,
                "upnsuffix" => i.UpnSuffix.Trim(),
                _ => m.Value,
            }, RegexOptions.IgnoreCase);
    }

    private static string Initial(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 ? trimmed[..1] : string.Empty;
    }

    /// <summary>
    /// Sanitizes a resolved value into a valid sAMAccountName: folds accents to ASCII (José → jose),
    /// drops every special character — including hyphen and underscore — keeping only ASCII letters,
    /// digits, and the "." separator from a "{first}.{last}"-style pattern, then lowercases. Leading/
    /// trailing dots (such as a trailing "." while the last name hasn't been typed yet) are trimmed.
    /// Shared by the single-user New User wizard, Copy User, and the bulk-create engine so all three
    /// produce identically-conventioned logon names.
    /// </summary>
    public static string SanitizeSam(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var folded = FoldDiacritics(value);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
            if (c < 128 && (char.IsLetterOrDigit(c) || c == '.'))
                sb.Append(c);
        return sb.ToString().Trim('.').ToLowerInvariant();
    }

    /// <summary>Decomposes accented characters and strips the combining marks (é → e, ñ → n).</summary>
    private static string FoldDiacritics(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }
}
