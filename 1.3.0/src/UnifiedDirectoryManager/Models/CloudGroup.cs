namespace UnifiedDirectoryManager.Models;

/// <summary>
/// Read-only projection of an Entra ID group. <see cref="GroupKind"/> is a friendly classification
/// ("Microsoft 365" / "Security" / "Mail-enabled security" / "Distribution") derived from the group's
/// type flags; <see cref="MembershipKind"/> is "Dynamic" or "Assigned"; <see cref="Origin"/> is
/// "Synced" (from on-premises) or "Cloud-only"; <see cref="IsTeam"/> is true when the M365 group backs a
/// Microsoft Teams team.
/// </summary>
public sealed record CloudGroup(
    string Id,
    string DisplayName,
    string? Description,
    string? Mail,
    string GroupKind,
    string MembershipKind,
    string Origin,
    bool IsTeam)
{
    /// <summary>True when the group is synced from on-prem AD — its membership is mastered on-prem and is
    /// NOT manageable in the cloud, so it should be treated as an on-prem group, not a cloud-only one.</summary>
    public bool IsSynced => string.Equals(Origin, "Synced", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the group's membership is rule-managed by Entra (can't add/remove members directly).</summary>
    public bool IsDynamic => string.Equals(MembershipKind, "Dynamic", StringComparison.OrdinalIgnoreCase);

    /// <summary>Friendly classification for display — the group kind, annotated "(Teams)" when the
    /// Microsoft 365 group backs a Microsoft Teams team.</summary>
    public string KindLabel => IsTeam ? $"{GroupKind} (Teams)" : GroupKind;
}
