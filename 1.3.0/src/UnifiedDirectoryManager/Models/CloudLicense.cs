namespace UnifiedDirectoryManager.Models;

/// <summary>
/// One license SKU assigned to a cloud user. <see cref="FriendlyName"/> is the marketing/product name
/// (e.g. "Office 365 E3") when known, <see cref="SkuPartNumber"/> the native SKU id (e.g. "ENTERPRISEPACK"),
/// and <see cref="AssignedVia"/> records how it was granted: "Direct", "Group: &lt;name&gt;", or a
/// combination (group-based licensing). <see cref="SkuId"/> + the assignment flags drive removal: only a
/// direct assignment can be removed here — a group-inherited license must be removed by changing the group.
/// </summary>
public sealed record CloudLicense(
    Guid SkuId,
    string FriendlyName,
    string SkuPartNumber,
    string AssignedVia,
    bool HasDirect,
    bool HasGroup)
{
    /// <summary>True when this license carries a direct assignment that can be removed in-place.</summary>
    public bool CanRemoveDirectly => HasDirect;

    /// <summary>True when the license is granted only by a group (removal requires a group-membership change).</summary>
    public bool IsInheritedOnly => HasGroup && !HasDirect;
}
