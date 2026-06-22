namespace UnifiedDirectoryManager.Models;

/// <summary>Which directory a <see cref="GroupRef"/> came from.</summary>
public enum GroupOrigin { OnPrem, Cloud }

/// <summary>
/// A group selectable from either directory in the hybrid picker. On-prem groups carry a
/// <see cref="Dn"/>; cloud groups carry a <see cref="CloudId"/>. <see cref="Detail"/> is a short
/// description (e.g. "OU=…" for on-prem, the group kind/origin for cloud).
/// </summary>
public sealed record GroupRef(GroupOrigin Origin, string Name, string? Dn, string? CloudId, string Detail)
{
    public string OriginLabel => Origin == GroupOrigin.OnPrem ? "On-prem" : "Cloud";

    /// <summary>Stable key for de-duplicating the basket (DN for on-prem, id for cloud).</summary>
    public string Key => Origin == GroupOrigin.OnPrem ? (Dn ?? Name) : (CloudId ?? Name);
}
