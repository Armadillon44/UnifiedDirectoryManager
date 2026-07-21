namespace UnifiedDirectoryManager.Models;

/// <summary>
/// A directory object's basic identity, for a lightweight properties view. Carries the name plus the two
/// standard AD naming formats: the LDAP <see cref="DistinguishedName"/>
/// (e.g. <c>OU=Sales,DC=corp,DC=example,DC=com</c>) and the <see cref="CanonicalName"/>
/// (e.g. <c>corp.example.com/Sales</c>), read from the constructed <c>canonicalName</c> attribute.
/// </summary>
public sealed record ObjectBasicInfo(
    string Name,
    string DistinguishedName,
    string CanonicalName,
    string? Description,
    IReadOnlyList<string> DescriptionValues);
