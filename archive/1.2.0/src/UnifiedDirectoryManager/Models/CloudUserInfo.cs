namespace UnifiedDirectoryManager.Models;

/// <summary>
/// Read-only projection of a user's Entra ID (cloud) identity, as returned by Microsoft Graph.
/// Nullable members reflect attributes that may be absent or unreadable. <see cref="Licenses"/> carries
/// friendly + native SKU names and how each was assigned; <see cref="Groups"/> is the user's cloud group
/// memberships.
/// </summary>
public sealed record CloudUserInfo(
    string Id,
    string? DisplayName,
    string? UserPrincipalName,
    bool? AccountEnabled,
    bool? OnPremisesSyncEnabled,
    string? UserType,
    string? UsageLocation,
    DateTimeOffset? CreatedDateTime,
    IReadOnlyList<CloudLicense> Licenses,
    IReadOnlyList<CloudGroup> Groups);
