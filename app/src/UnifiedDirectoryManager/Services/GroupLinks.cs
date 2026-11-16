namespace UnifiedDirectoryManager.Services;

/// <summary>What resolving a list of stored group references found.</summary>
/// <param name="CurrentDns">The DNs to act on now, in stored order, with the groups that could not be found left out.</param>
/// <param name="Missing">Stored DNs, as stored, whose group could not be found.</param>
/// <param name="Changed">True when the stored lists were updated: a DN followed its group, or a GUID was learnt.</param>
public sealed record GroupLinkResolution(IReadOnlyList<string> CurrentDns, IReadOnlyList<string> Missing, bool Changed);

/// <summary>
/// Keeps stored references to on-prem groups (template and scenario group lists) pointing at the right group.
/// </summary>
/// <remarks>
/// <para>
/// A group's DN changes whenever it is renamed or moved, in this app or in ADUC. Its objectGUID never does.
/// Microsoft's guidance is that an application storing references to directory objects should store the GUID
/// for exactly this reason, so each stored DN carries its GUID beside it.
/// </para>
/// <para>
/// The GUID decides. A reference with a GUID resolves through it and nothing else, because the DN alone can be
/// wrong in the worst way: rename "Sales" to "Sales East", create a new "Sales", and the old DN now names a
/// different group. A reference without a GUID (written before this existed) is looked up by its DN once, and
/// the GUID found is recorded so it never depends on the DN again.
/// </para>
/// </remarks>
public static class GroupLinks
{
    /// <summary>
    /// The GUID stored for <paramref name="dn"/>. Matched without regard to case, as AD matches DNs, and by
    /// walking the map because a map read back from JSON uses the default case-sensitive comparer.
    /// </summary>
    public static string? GuidFor(IReadOnlyDictionary<string, string>? guids, string dn)
    {
        if (guids is null || string.IsNullOrEmpty(dn)) return null;
        foreach (var (key, value) in guids)
            if (string.Equals(key, dn, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    /// <summary>
    /// Resolves each stored DN to where its group is now, healing the lists in place: a DN that has moved is
    /// replaced with the current one, a missing GUID is filled in, and GUID entries for DNs no longer listed are
    /// dropped. Missing groups stay in the list as they were, so the operator can see what went missing.
    /// </summary>
    public static async Task<GroupLinkResolution> ResolveAsync(
        IDirectoryService directory, List<string> dns, Dictionary<string, string> guids,
        CancellationToken cancellationToken = default)
    {
        var current = new List<string>();
        var missing = new List<string>();
        var changed = false;

        for (var i = 0; i < dns.Count; i++)
        {
            var dn = dns[i];
            if (string.IsNullOrWhiteSpace(dn)) continue;
            var guid = GuidFor(guids, dn);
            if (guid is not null)
            {
                // GUID-first, and GUID-only: if the GUID is gone, the group was deleted. Whatever now holds
                // the old DN is a different group and must not be acted on in its place.
                var now = await directory.FindDnByGuidAsync(guid, cancellationToken);
                if (now is null) { missing.Add(dn); continue; }
                // Ordinal, so a rename that only changes the case of the name is written down too.
                if (!string.Equals(now, dn, StringComparison.Ordinal))
                {
                    RemoveKey(guids, dn);
                    dns[i] = now;
                    guids[now] = guid;
                    changed = true;
                }
                current.Add(now);
            }
            else
            {
                var learnt = await directory.GetObjectGuidAsync(dn, cancellationToken);
                if (learnt is null) { missing.Add(dn); continue; }
                guids[dn] = learnt;
                changed = true;
                current.Add(dn);
            }
        }

        // An editor removed a group: its GUID has nothing left to identify.
        foreach (var key in guids.Keys.ToList())
            if (!dns.Any(d => string.Equals(d, key, StringComparison.OrdinalIgnoreCase)))
            {
                guids.Remove(key);
                changed = true;
            }

        return new GroupLinkResolution(current, missing, changed);
    }

    /// <summary>
    /// After this app renames or moves a group: points every reference to it at <paramref name="newDn"/> and
    /// records its GUID. A reference is to this group if it carries this GUID, or, when it carries no GUID yet,
    /// if it names the group's old DN.
    /// </summary>
    /// <returns>True when anything changed.</returns>
    public static bool Retarget(List<string> dns, Dictionary<string, string> guids, string groupGuid, string oldDn, string newDn)
    {
        var changed = false;
        for (var i = 0; i < dns.Count; i++)
        {
            var dn = dns[i];
            var guid = GuidFor(guids, dn);
            var isThisGroup = guid is not null
                ? string.Equals(guid, groupGuid, StringComparison.OrdinalIgnoreCase)
                : string.Equals(dn, oldDn, StringComparison.OrdinalIgnoreCase);
            if (!isThisGroup) continue;
            if (guid is not null && string.Equals(dn, newDn, StringComparison.Ordinal)) continue; // already right
            RemoveKey(guids, dn);
            dns[i] = newDn;
            guids[newDn] = groupGuid;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The GUIDs to save with an edited DN list: the entries whose DN is still listed. An editor rebuilds the
    /// object it saves, so without this every save would throw away what was known.
    /// </summary>
    public static Dictionary<string, string> Carry(IReadOnlyDictionary<string, string>? previous, IEnumerable<string> dns)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dn in dns)
            if (GuidFor(previous, dn) is { } guid) kept[dn] = guid;
        return kept;
    }

    /// <summary>Removes a key whatever its case.</summary>
    private static void RemoveKey(Dictionary<string, string> guids, string dn)
    {
        foreach (var key in guids.Keys.Where(k => string.Equals(k, dn, StringComparison.OrdinalIgnoreCase)).ToList())
            guids.Remove(key);
    }
}
