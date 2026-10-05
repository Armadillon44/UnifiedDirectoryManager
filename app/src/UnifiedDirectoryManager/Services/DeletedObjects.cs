using System.Globalization;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>Why a Deleted Objects read returned what it did.</summary>
public enum DeletedObjectsStatus
{
    /// <summary>The container was read. <c>Rows</c> may still be empty, which means nothing is deleted.</summary>
    Ok,

    /// <summary>The caller cannot read the container. Reading it is a Domain Admin right by default.</summary>
    AccessDenied,

    /// <summary>There is no Deleted Objects container, which means this is not an AD domain this app can read.</summary>
    NotFound,

    /// <summary>
    /// The DC refused the Show Deleted Objects control. The control is sent as critical on purpose, so
    /// that this is an error rather than an empty list that would read as "nothing has been deleted".
    /// </summary>
    ControlRefused,

    /// <summary>Anything else. <c>Message</c> carries the server's own words.</summary>
    Failed,
}

/// <summary>One row in the Deleted Objects list.</summary>
/// <param name="Name">The name the object had before it was deleted.</param>
/// <param name="DistinguishedName">Its CURRENT dn, inside the Deleted Objects container, mangled with its GUID.</param>
/// <param name="LastKnownParent">The container it was deleted from, or empty when AD did not record one.</param>
/// <param name="DeletedOn">When it was deleted, as far as <c>whenChanged</c> records it.</param>
/// <param name="IsRecycled">
/// True once the deleted-object lifetime has passed: most attributes are gone and the Recycle Bin can no
/// longer bring it back. These are hidden unless the operator asks for them.
/// </param>
public sealed record DeletedObjectRow(
    string Name,
    string DistinguishedName,
    AdObjectType Type,
    string LastKnownParent,
    DateTime? DeletedOn,
    string SamAccountName,
    bool IsRecycled,
    string ObjectGuid)
{
    /// <summary>Date for the list, blank rather than a fake one when AD recorded no timestamp.</summary>
    public string DeletedOnText => DeletedOn is { } d
        ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>The last known parent as a readable path, for a column too narrow to hold a DN.</summary>
    public string LastKnownParentText => DeletedObjects.ShortenParent(LastKnownParent);

    public string StateText => IsRecycled ? "Recycled" : "Deleted";
}

/// <summary>What one read of the Deleted Objects container found.</summary>
public sealed record DeletedObjectsResult(
    DeletedObjectsStatus Status,
    IReadOnlyList<DeletedObjectRow> Rows,
    bool RecycleBinEnabled,
    int LifetimeDays,
    string? Message);

/// <summary>
/// The parts of reading AD's Deleted Objects container that are decisions rather than LDAP calls.
/// </summary>
/// <remarks>
/// Separated out for the same reason the rest of this codebase separates them: a PowerShell suite cannot
/// construct <c>DirectoryEntry</c> or <c>SearchResult</c>, so anything only reachable through one is
/// anything that never gets tested. Everything here takes and returns primitives.
/// </remarks>
public static class DeletedObjects
{
    /// <summary>The optional feature's RDN, as it appears in <c>msDS-EnabledFeature</c> on the Partitions container.</summary>
    public const string RecycleBinFeatureRdn = "CN=Recycle Bin Feature";

    /// <summary>What Windows has used since Server 2003 SP1 when neither lifetime attribute is set.</summary>
    public const int DefaultLifetimeDays = 180;

    /// <summary>
    /// LDAP OID of the Show Deleted Objects control.
    /// </summary>
    /// <remarks>
    /// This has to ride on the SEARCH, not on a bind. The first version of this feature pointed a
    /// <c>DirectorySearcher</c> at a <c>DirectoryEntry</c> bound to the container and set
    /// <c>Tombstone = true</c>; that fails, because the searcher must bind its root before it can search,
    /// the bind carries no controls, and the container is invisible without one. The bind came back
    /// "no such object" and the window reported a container that was plainly there in ADAC.
    /// </remarks>
    public const string ShowDeletedControlOid = "1.2.840.113556.1.4.417";

    /// <summary>Relative DN of the container, under the domain naming context.</summary>
    public const string ContainerRdn = "CN=Deleted Objects";

    public static string ContainerDn(string defaultNamingContext) =>
        $"{ContainerRdn},{defaultNamingContext}";

    /// <summary>
    /// Whether the Recycle Bin is on, from the <c>msDS-EnabledFeature</c> values of the Partitions container.
    /// </summary>
    /// <remarks>
    /// Enabling the Recycle Bin writes the feature's DN into that multi-valued attribute. It is matched on
    /// the leading RDN rather than the whole DN, because the rest of the DN contains the forest root's own
    /// naming context and differs per domain.
    /// </remarks>
    public static bool IsRecycleBinEnabled(IEnumerable<string>? enabledFeatures) =>
        enabledFeatures?.Any(dn =>
            !string.IsNullOrWhiteSpace(dn) &&
            dn.TrimStart().StartsWith(RecycleBinFeatureRdn, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// How long a deleted object stays recoverable.
    /// </summary>
    /// <remarks>
    /// <c>msDS-deletedObjectLifetime</c> governs it when set. When it is not, AD falls back to
    /// <c>tombstoneLifetime</c>, and when THAT is unset the effective value is 180 days rather than zero —
    /// an unset attribute means "use the default", not "expire immediately". Reading an absent attribute
    /// as 0 would tell the operator everything had already expired.
    /// </remarks>
    public static int ResolveLifetimeDays(int? deletedObjectLifetime, int? tombstoneLifetime)
    {
        if (deletedObjectLifetime is > 0) return deletedObjectLifetime.Value;
        if (tombstoneLifetime is > 0) return tombstoneLifetime.Value;
        return DefaultLifetimeDays;
    }

    /// <summary>
    /// The LDAP filter for the container's contents.
    /// </summary>
    /// <remarks>
    /// <c>isRecycled</c> is absent on every object in a domain that never had the Recycle Bin enabled, and
    /// absent is not TRUE, so the negated clause is correct there too and does not need a special case.
    /// </remarks>
    public static string Filter(bool includeRecycled) =>
        includeRecycled ? "(isDeleted=TRUE)" : "(&(isDeleted=TRUE)(!(isRecycled=TRUE)))";

    /// <summary>
    /// The name an object had before it was deleted.
    /// </summary>
    /// <remarks>
    /// A deleted object's <c>cn</c> is not its name: AD rewrites it to <c>&lt;name&gt;\0ADEL:&lt;guid&gt;</c>
    /// so that two objects deleted from different places cannot collide inside one flat container. Showing
    /// that raw would put <c>Jane Doe\0ADEL:4f2c…</c> in front of an operator. <c>msDS-LastKnownRDN</c>
    /// holds the real name and is preferred; the suffix is stripped only when it is missing, which happens
    /// on tombstones from before the Recycle Bin was enabled.
    /// </remarks>
    public static string DisplayName(string? lastKnownRdn, string? cn)
    {
        if (!string.IsNullOrWhiteSpace(lastKnownRdn)) return lastKnownRdn.Trim();
        return StripDeletedSuffix(cn);
    }

    /// <summary>Removes AD's <c>\0ADEL:&lt;guid&gt;</c> uniquifier from a deleted object's name.</summary>
    public static string StripDeletedSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        // The separator is an escaped newline. It reaches managed code either still escaped as the two
        // characters "\0A" or already decoded to one newline, depending on which API returned it, so both
        // spellings are cut.
        foreach (var marker in new[] { "\\0ADEL:", "\nDEL:", "\u000ADEL:" })
        {
            var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return text[..at].Trim();
        }
        return text;
    }

    /// <summary>
    /// The last known parent, shortened to something that fits a column.
    /// </summary>
    /// <remarks>
    /// Returns the path from the domain down, most significant last reversed into reading order, e.g.
    /// <c>Sales/Users</c> for <c>OU=Users,OU=Sales,DC=contoso,DC=net</c>. The domain components are
    /// dropped because every row shares them. A parent that was itself deleted carries the same
    /// <c>\0ADEL:</c> uniquifier, so each component is stripped.
    /// </remarks>
    public static string ShortenParent(string? parentDn)
    {
        if (string.IsNullOrWhiteSpace(parentDn)) return string.Empty;
        var parts = SplitDn(parentDn)
            .Where(p => !p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => StripDeletedSuffix(ValueOf(p)))
            .Where(p => p.Length > 0)
            .Reverse()
            .ToList();
        return parts.Count == 0 ? "(domain root)" : string.Join("/", parts);
    }

    /// <summary>Splits a DN on unescaped commas, so a name containing an escaped comma survives.</summary>
    public static IReadOnlyList<string> SplitDn(string dn)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] != ',') continue;
            if (i > 0 && dn[i - 1] == '\\') continue;   // escaped, part of the name
            parts.Add(dn[start..i]);
            start = i + 1;
        }
        if (start < dn.Length) parts.Add(dn[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>The value half of one <c>type=value</c> DN component.</summary>
    public static string ValueOf(string component)
    {
        var at = component.IndexOf('=');
        return at < 0 ? component.Trim() : component[(at + 1)..].Trim();
    }

    /// <summary>
    /// Parses an LDAP generalized time, e.g. <c>20260928140211.0Z</c>, into local time.
    /// </summary>
    /// <remarks>
    /// ADSI hands <c>whenChanged</c> over as a <c>DateTime</c> already; raw LDAP does not, and the string
    /// it returns is not a format <c>DateTime.Parse</c> recognises. Parsed with the invariant culture
    /// because it is a wire format, and because a workstation whose default calendar is Buddhist would
    /// otherwise read 2026 as 2569 — the same trap F23 recorded in the scenario runner.
    /// </remarks>
    public static DateTime? ParseGeneralizedTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        // yyyyMMddHHmmss, then an optional fraction, then an optional zone. AD always sends ".0Z"; only
        // the 14 leading digits are needed and the rest is tolerated rather than required.
        if (text.Length < 14) return null;
        var core = text[..14];
        if (!core.All(char.IsAsciiDigit)) return null;
        if (!DateTime.TryParseExact(core, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return null;
        return utc.ToLocalTime();
    }

    /// <summary>
    /// Turns an LDAP result code into a status the window can explain in a sentence.
    /// </summary>
    /// <remarks>
    /// The codes are the protocol's own (50 insufficientAccessRights, 32 noSuchObject), not Win32 ones:
    /// going through <c>LdapConnection</c> rather than ADSI means the server's own answer arrives intact
    /// instead of wrapped in a COM HRESULT. <c>unavailableCriticalExtension</c> gets its own words because
    /// it means precisely one thing here — the DC would not honour the Show Deleted Objects control.
    /// </remarks>
    public static DeletedObjectsStatus ClassifyLdap(int resultCode, string? message)
    {
        switch (resultCode)
        {
            case 50: return DeletedObjectsStatus.AccessDenied;          // insufficientAccessRights
            case 8:  return DeletedObjectsStatus.AccessDenied;          // strongerAuthRequired
            case 32: return DeletedObjectsStatus.NotFound;              // noSuchObject
            case 12: return DeletedObjectsStatus.ControlRefused;        // unavailableCriticalExtension
        }
        return Classify(0, message);
    }

    /// <summary>
    /// Turns a failed read into a status the window can explain in a sentence.
    /// </summary>
    /// <remarks>
    /// Reading Deleted Objects is a Domain Admin right by default, so "you cannot read this" is the single
    /// most likely outcome for an ordinary operator and deserves its own words rather than an LDAP code.
    /// Matched on the Win32 facility codes rather than on message text, because those messages are
    /// localised and this has to work on a German workstation.
    /// </remarks>
    public static DeletedObjectsStatus Classify(int hresult, string? message)
    {
        // 0x80072098 ERROR_DS_INSUFF_ACCESS_RIGHTS, 0x80070005 E_ACCESSDENIED.
        if (hresult is unchecked((int)0x80072098) or unchecked((int)0x80070005)) return DeletedObjectsStatus.AccessDenied;
        // 0x80072030 ERROR_DS_NO_SUCH_OBJECT.
        if (hresult == unchecked((int)0x80072030)) return DeletedObjectsStatus.NotFound;

        // The extended error, when the COM layer passed one through rather than a distinct HRESULT. These
        // are the numeric codes AD puts in its text, which are not localised even when the prose is.
        var text = message ?? string.Empty;
        if (text.Contains("00002098", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("INSUFF_ACCESS_RIGHTS", StringComparison.OrdinalIgnoreCase)) return DeletedObjectsStatus.AccessDenied;
        if (text.Contains("00002030", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NO_OBJECT", StringComparison.OrdinalIgnoreCase)) return DeletedObjectsStatus.NotFound;

        return DeletedObjectsStatus.Failed;
    }

    /// <summary>
    /// The sentence shown above the list, which has to tell three situations apart that all look like an
    /// empty list.
    /// </summary>
    /// <remarks>
    /// This app has already shipped a bug where "could not read" was reported for a group that was simply
    /// empty, so the distinction between <i>nothing deleted</i>, <i>not allowed to look</i> and <i>the
    /// feature is off</i> is written down here and tested, rather than assembled at three call sites.
    /// </remarks>
    public static string Describe(DeletedObjectsResult result, string domain, bool includeRecycled)
    {
        var where = string.IsNullOrWhiteSpace(domain) ? "this domain" : domain;
        switch (result.Status)
        {
            case DeletedObjectsStatus.AccessDenied:
                return "You do not have permission to read Deleted Objects. Reading it is a Domain Admin " +
                       "right by default, so this usually means the account you are connected as has not " +
                       "been granted it.";

            case DeletedObjectsStatus.NotFound:
                return $"No Deleted Objects container was found in {where}. " +
                       (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : result.Message!.Trim());

            case DeletedObjectsStatus.ControlRefused:
                return "The domain controller would not honour the Show Deleted Objects control, so the " +
                       "Deleted Objects container cannot be read. " +
                       (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : result.Message!.Trim());

            case DeletedObjectsStatus.Failed:
                return string.IsNullOrWhiteSpace(result.Message)
                    ? "Deleted Objects could not be read."
                    : "Deleted Objects could not be read: " + result.Message!.Trim();
        }

        var binState = result.RecycleBinEnabled
            ? $"The AD Recycle Bin is enabled for {where}. Deleted objects stay recoverable for {result.LifetimeDays} days."
            : $"The AD Recycle Bin is NOT enabled for {where}. What is listed are tombstones, which keep only " +
              $"a few attributes and cannot be restored with their group memberships. They are kept for " +
              $"{result.LifetimeDays} days.";

        if (result.Rows.Count > 0) return binState;

        // An empty list is a RESULT, not a failure, and saying so is the whole point of this method.
        var nothing = includeRecycled
            ? $"Nothing has been deleted in the last {result.LifetimeDays} days."
            : $"Nothing recoverable has been deleted in the last {result.LifetimeDays} days. " +
              "Tick “Include recycled” to also list objects that are past that window.";
        return binState + " " + nothing;
    }
}
