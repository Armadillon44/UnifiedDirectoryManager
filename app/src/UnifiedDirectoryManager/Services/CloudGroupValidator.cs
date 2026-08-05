using System.Text;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Name and alias rules for cloud group creation. The two backends disagree, so validation is per-type rather
/// than shared: Microsoft Graph rejects one character set for <c>mailNickname</c>, Exchange Online another for
/// <c>Alias</c>. Both are checked here, before the call, because the server-side failures are unhelpful —
/// Exchange in particular does not reject a bad alias, it silently rewrites it (spaces stripped, unsupported
/// characters turned into "?").
/// </summary>
public static class CloudGroupValidator
{
    /// <summary>Graph's documented exclusions for <c>mailNickname</c>, plus the period, which the Update-group
    /// documentation forbids even though Create doesn't — the safe intersection across both operations.</summary>
    private const string GraphForbidden = "@()\\[]\";:<>,. ";

    /// <summary>The punctuation Exchange documents as legal in an alias, alongside letters and digits.</summary>
    private const string ExchangeAliasPunctuation = "!#$%&'*+-/=?^_`{|}~.";

    /// <summary>Longest alias / mail nickname either backend accepts.</summary>
    public const int MaxNicknameLength = 64;

    /// <summary>Longest display name Graph accepts.</summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>Longest name Exchange accepts. <c>New-DistributionGroup -Name</c> caps at 64, and the app doesn't
    /// pass a separate <c>-DisplayName</c>, so this bounds the whole name for the two Exchange kinds.</summary>
    public const int MaxExchangeNameLength = 64;

    /// <summary>True for the two kinds created through Exchange Online rather than Microsoft Graph.</summary>
    public static bool IsExchangeType(CloudGroupType type) =>
        type is CloudGroupType.Distribution or CloudGroupType.MailEnabledSecurity;

    /// <summary>
    /// Suggests a mail nickname from the display name, keeping the two rule sets' intersection so the suggestion
    /// is valid whichever backend the operator ends up choosing. Letters, digits, hyphen and underscore survive;
    /// everything else is dropped rather than substituted, since Exchange would otherwise turn it into "?".
    /// </summary>
    public static string DeriveNickname(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return string.Empty;
        var sb = new StringBuilder(displayName.Length);
        foreach (var c in displayName)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || c is '-' or '_') sb.Append(c);
            else if (c is ' ' or '.' && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-', '_');
        return s.Length > MaxNicknameLength ? s[..MaxNicknameLength] : s;
    }

    /// <summary>
    /// Validates the whole request against the rules of the backend its <see cref="CloudGroupCreateRequest.Type"/>
    /// selects. Returns null when valid, otherwise a message written for the operator.
    /// </summary>
    public static string? Validate(CloudGroupCreateRequest request)
    {
        if ((request.DisplayName?.Trim() ?? string.Empty).Length == 0) return "Enter a name for the group.";
        if (ValidateName(request.Type, request.DisplayName) is { } nameProblem) return nameProblem;

        if ((request.MailNickname?.Trim() ?? string.Empty).Length == 0)
            return IsExchangeType(request.Type)
                ? "Enter an alias. Leaving it to Exchange rewrites the name (spaces stripped, unsupported characters replaced with “?”)."
                : "Enter a mail nickname. Microsoft Graph requires one even for a security group that isn't mail-enabled.";

        return ValidateNickname(request.Type, request.MailNickname);
    }

    /// <summary>
    /// Validates just the name against one backend's length cap. Returns null for an EMPTY name, because this
    /// exists to re-check a value the operator has ALREADY typed when they change the group type — the two
    /// backends cap the name very differently and Exchange's 64 is the one that bites, so a name that was legal
    /// a moment ago can stop being legal without the operator touching it. "You haven't typed it yet" is not a
    /// problem worth reporting at that moment; <see cref="Validate"/> owns the required-field message.
    /// </summary>
    public static string? ValidateName(CloudGroupType type, string? displayName)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0) return null;
        var exchange = IsExchangeType(type);
        var cap = exchange ? MaxExchangeNameLength : MaxDisplayNameLength;
        if (name.Length <= cap) return null;
        return exchange
            ? $"Exchange Online limits a group name to {MaxExchangeNameLength} characters (this one is {name.Length})."
            : $"The name must be {MaxDisplayNameLength} characters or fewer.";
    }

    /// <summary>
    /// Validates just the alias against one backend's character rules, which is the sharper half of the same
    /// problem <see cref="ValidateName"/> solves: Graph and Exchange forbid different characters, so switching
    /// type can invalidate an untouched alias. Returns null for an EMPTY alias.
    /// </summary>
    public static string? ValidateNickname(CloudGroupType type, string? mailNickname)
    {
        var nick = mailNickname?.Trim() ?? string.Empty;
        if (nick.Length == 0) return null;
        if (nick.Length > MaxNicknameLength) return $"The alias must be {MaxNicknameLength} characters or fewer.";
        return IsExchangeType(type) ? ValidateExchangeAlias(nick) : ValidateGraphNickname(nick);
    }

    /// <summary>Graph: ASCII only, and none of <c>@ ( ) \ [ ] " ; : &lt; &gt; , .</c> or space.</summary>
    private static string? ValidateGraphNickname(string nick)
    {
        foreach (var c in nick)
        {
            if (c > 127) return "The mail nickname must use ASCII characters only.";
            if (GraphForbidden.IndexOf(c) >= 0)
                return c == ' '
                    ? "The mail nickname can't contain spaces."
                    : $"The mail nickname can't contain “{c}”.";
        }
        return null;
    }

    /// <summary>
    /// Exchange, checked as an ALLOWLIST rather than a denylist. Exchange documents the characters an alias may
    /// contain; enumerating the ones it may not would silently pass everything nobody thought of — and the
    /// consequence isn't a clean rejection, it's Exchange quietly rewriting the alias and replacing the
    /// offending characters with "?".
    ///
    /// The one addition to the documented set is <c>&amp;</c>: Exchange accepts it, but it breaks Entra Connect
    /// synchronisation, so it's refused here with that reason.
    /// </summary>
    private static string? ValidateExchangeAlias(string alias)
    {
        if (alias.Contains(' ')) return "The alias can't contain spaces.";
        if (alias.Contains('&')) return "The alias can't contain “&” — it isn't supported for directory synchronisation.";
        if (alias[0] == '.' || alias[^1] == '.') return "The alias can't start or end with a period.";
        if (alias.Contains("..")) return "The alias can't contain two periods in a row.";

        foreach (var c in alias)
        {
            if (c > 127) return "The alias must use ASCII characters only.";
            var legal = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                        || ExchangeAliasPunctuation.IndexOf(c) >= 0;
            if (!legal) return $"The alias can't contain “{c}”.";
        }
        return null;
    }
}
