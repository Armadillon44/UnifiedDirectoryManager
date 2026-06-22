namespace UnifiedDirectoryManager.Models;

/// <summary>
/// A subscribed license SKU available in the tenant. <see cref="FriendlyName"/> is the marketing/product
/// name when known (else the part number); <see cref="Enabled"/>/<see cref="Consumed"/> are the prepaid and
/// in-use unit counts. <see cref="AssigningGroups"/> lists groups that grant this SKU via group-based
/// licensing (empty when none) — used to nudge admins toward group membership over direct assignment.
/// </summary>
public sealed record CloudSku(
    Guid SkuId,
    string SkuPartNumber,
    string FriendlyName,
    int Enabled,
    int Consumed,
    IReadOnlyList<string> AssigningGroups)
{
    /// <summary>Units still assignable (never negative).</summary>
    public int Available => Math.Max(0, Enabled - Consumed);

    /// <summary>True when at least one group grants this SKU (prefer adding the user to that group).</summary>
    public bool HasGroupAssignment => AssigningGroups.Count > 0;

    /// <summary>"Enterprise E3 — 12 of 50 available · also via group" style summary for the picker.</summary>
    public string AvailabilityText
    {
        get
        {
            var basic = $"{Available} of {Enabled} available";
            return HasGroupAssignment ? basic + " · assigned via group" : basic;
        }
    }
}
