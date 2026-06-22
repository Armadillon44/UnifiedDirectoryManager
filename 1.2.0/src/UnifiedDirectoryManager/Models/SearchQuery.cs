using System.DirectoryServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UnifiedDirectoryManager.Models;

public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    Present,
    NotPresent,
    GreaterOrEqual,
    LessOrEqual,
}

/// <summary>A single advanced-search clause: attribute (by lDAPDisplayName) + operator + value.</summary>
public partial class SearchCondition : ObservableObject
{
    [ObservableProperty] private string _ldapName = "cn";
    [ObservableProperty] private ConditionOperator _operator = ConditionOperator.Contains;
    [ObservableProperty] private string _value = string.Empty;

    /// <summary>Renders this clause as an LDAP filter fragment with proper value escaping.</summary>
    public string ToFilterFragment()
    {
        var attr = LdapName.Trim();
        var esc = LdapFilter.EscapeValue(Value);
        return Operator switch
        {
            ConditionOperator.Equals => $"({attr}={esc})",
            ConditionOperator.NotEquals => $"(!({attr}={esc}))",
            ConditionOperator.Contains => $"({attr}=*{esc}*)",
            ConditionOperator.StartsWith => $"({attr}={esc}*)",
            ConditionOperator.EndsWith => $"({attr}=*{esc})",
            ConditionOperator.Present => $"({attr}=*)",
            ConditionOperator.NotPresent => $"(!({attr}=*))",
            ConditionOperator.GreaterOrEqual => $"({attr}>={esc})",
            ConditionOperator.LessOrEqual => $"({attr}<={esc})",
            _ => $"({attr}={esc})",
        };
    }
}

/// <summary>A complete advanced query: object type clamp + conditions + scope + base DN, or a raw filter.</summary>
public sealed class SearchQuery
{
    public AdObjectType ObjectType { get; set; } = AdObjectType.User;
    public List<SearchCondition> Conditions { get; set; } = new();

    /// <summary>AND (true) vs OR (false) across the conditions.</summary>
    public bool MatchAll { get; set; } = true;

    public SearchScope Scope { get; set; } = SearchScope.Subtree;

    /// <summary>Base DN to search under; empty means the domain root. Used for single-container listings.</summary>
    public string BaseDn { get; set; } = string.Empty;

    /// <summary>
    /// Base DNs to search under, each searched with <see cref="Scope"/> and the results merged. When
    /// non-empty this takes precedence over <see cref="BaseDn"/>; when empty the search falls back to
    /// <see cref="BaseDn"/>, and an empty <see cref="BaseDn"/> means the whole domain.
    /// </summary>
    public List<string> BaseDns { get; set; } = new();

    /// <summary>If set, this raw LDAP filter is used verbatim (escape hatch), ignoring Conditions.</summary>
    public string? RawFilter { get; set; }

    /// <summary>
    /// The distinct base DNs to actually search, in order. Prefers <see cref="BaseDns"/>, then
    /// <see cref="BaseDn"/>; empty means the caller should substitute the domain root.
    /// </summary>
    public IReadOnlyList<string> EffectiveBaseDns()
    {
        var bases = BaseDns
            .Where(dn => !string.IsNullOrWhiteSpace(dn))
            .Select(dn => dn.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (bases.Count > 0) return bases;
        return string.IsNullOrWhiteSpace(BaseDn) ? Array.Empty<string>() : new[] { BaseDn.Trim() };
    }

    /// <summary>Builds the effective LDAP filter, clamped to the chosen object class.</summary>
    public string BuildFilter()
    {
        var classFilter = ObjectClassFilter(ObjectType);

        if (!string.IsNullOrWhiteSpace(RawFilter))
        {
            var raw = RawFilter.Trim();
            return $"(&{classFilter}{raw})";
        }

        var fragments = Conditions
            .Where(c => c.Operator is ConditionOperator.Present or ConditionOperator.NotPresent
                        || !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.ToFilterFragment())
            .ToList();

        if (fragments.Count == 0)
            return classFilter;

        var op = MatchAll ? "&" : "|";
        var combined = fragments.Count == 1 ? fragments[0] : $"({op}{string.Concat(fragments)})";
        return $"(&{classFilter}{combined})";
    }

    private static string ObjectClassFilter(AdObjectType type) => type switch
    {
        // person but not computer (computers derive from user)
        AdObjectType.User => "(&(objectCategory=person)(objectClass=user))",
        AdObjectType.Computer => "(objectCategory=computer)",
        AdObjectType.Group => "(objectCategory=group)",
        AdObjectType.Contact => "(objectCategory=contact)",
        AdObjectType.OrganizationalUnit => "(objectCategory=organizationalUnit)",
        _ => "(|(objectCategory=person)(objectCategory=computer)(objectCategory=group))",
    };
}

/// <summary>LDAP filter value escaping per RFC 4515.</summary>
public static class LdapFilter
{
    public static string EscapeValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                case '/': sb.Append("\\2f"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
