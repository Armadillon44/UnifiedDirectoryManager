namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Turns an Active Directory <c>groupType</c> bitmask into a friendly classification — the category
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
}
