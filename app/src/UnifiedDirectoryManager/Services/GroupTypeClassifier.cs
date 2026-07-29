namespace UnifiedDirectoryManager.Services;

/// <summary>An AD group's scope — how widely it can be used and what it may contain.</summary>
public enum GroupScope { Global, DomainLocal, Universal }

/// <summary>An AD group's category — security groups can be granted permissions; distribution groups are mail-only.</summary>
public enum GroupCategory { Security, Distribution }

/// <summary>
/// Converts between an Active Directory <c>groupType</c> bitmask and the friendly classification — the category
/// (Security / Distribution) plus the scope (Global / Domain local / Universal), e.g. "Security · Global".
/// </summary>
public static class GroupTypeClassifier
{
    // groupType flags (see MS-ADTS 2.2.12). The security-enabled bit (0x80000000) sets the sign bit of a
    // signed 32-bit int, so AD often returns the value as a negative number — classify against the uint.
    private const uint Global = 0x00000002;
    private const uint DomainLocal = 0x00000004;
    private const uint Universal = 0x00000008;
    private const uint SecurityEnabled = 0x80000000;

    /// <summary>Classifies a raw groupType string (as returned by AD); "" when it can't be parsed.</summary>
    public static string Describe(string? groupType) =>
        string.IsNullOrWhiteSpace(groupType) || !long.TryParse(groupType, out var raw)
            ? string.Empty
            : Describe(unchecked((uint)raw));

    /// <summary>Classifies a groupType bitmask, e.g. "Security · Global" or "Distribution".</summary>
    public static string Describe(uint flags)
    {
        var category = (flags & SecurityEnabled) != 0 ? "Security" : "Distribution";
        var scope =
            (flags & Global) != 0 ? "Global" :
            (flags & DomainLocal) != 0 ? "Domain local" :
            (flags & Universal) != 0 ? "Universal" : null;
        return scope is null ? category : $"{category} · {scope}";
    }

    /// <summary>
    /// Splits a raw groupType string into its scope + category. False when the value is missing, unparseable, or
    /// carries no scope bit (e.g. a built-in group), in which case the caller shouldn't offer the type editor.
    /// </summary>
    public static bool TryParse(string? groupType, out GroupScope scope, out GroupCategory category)
    {
        scope = GroupScope.Global;
        category = GroupCategory.Security;
        if (string.IsNullOrWhiteSpace(groupType) || !long.TryParse(groupType, out var raw)) return false;

        var flags = unchecked((uint)raw);
        category = (flags & SecurityEnabled) != 0 ? GroupCategory.Security : GroupCategory.Distribution;
        if ((flags & Global) != 0) scope = GroupScope.Global;
        else if ((flags & DomainLocal) != 0) scope = GroupScope.DomainLocal;
        else if ((flags & Universal) != 0) scope = GroupScope.Universal;
        else return false; // no recognised scope bit (built-in / system group) — don't pretend we can edit it
        return true;
    }

    /// <summary>
    /// Builds the groupType value to write for a scope + category. Returned as a SIGNED int because groupType's
    /// LDAP syntax is a 32-bit enumeration and the security bit (0x80000000) overflows Int32 — a security group is
    /// written as a negative number (e.g. Global security = -2147483646).
    /// </summary>
    public static int Build(GroupScope scope, GroupCategory category)
    {
        var flags = scope switch
        {
            GroupScope.DomainLocal => DomainLocal,
            GroupScope.Universal => Universal,
            _ => Global,
        };
        if (category == GroupCategory.Security) flags |= SecurityEnabled;
        return unchecked((int)flags);
    }

    /// <summary>
    /// True when Active Directory forbids changing directly between two scopes: Global ↔ Domain local is not a
    /// legal single step (the group must be converted to Universal first). Other conversions are attempted and
    /// AD's own rules (e.g. a Global group that belongs to another Global group) decide the outcome.
    /// </summary>
    public static bool IsIllegalScopeChange(GroupScope from, GroupScope to) =>
        (from == GroupScope.Global && to == GroupScope.DomainLocal)
        || (from == GroupScope.DomainLocal && to == GroupScope.Global);

    /// <summary>Friendly label for a scope (matches the wording AD/ADUC uses).</summary>
    public static string Label(GroupScope scope) => scope switch
    {
        GroupScope.DomainLocal => "Domain local",
        GroupScope.Universal => "Universal",
        _ => "Global",
    };

    /// <summary>Friendly label for a category.</summary>
    public static string Label(GroupCategory category) =>
        category == GroupCategory.Distribution ? "Distribution" : "Security";
}
