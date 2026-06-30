using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Classifies cloud (Entra) properties by editability so the UI can edit the editable ones and gray out the
/// rest with an explanatory tooltip. Synced objects (onPremisesSyncEnabled) have their directory attributes
/// mastered on-premises (read-only in the cloud); cloud-only objects can edit them. A small set
/// (e.g. usageLocation) is always cloud-mastered. Conservative by default: unknown keys are read-only.
/// </summary>
public static class CloudPropertyRules
{
    // Single-valued directory attributes: editable on a cloud-only object; on-prem-mastered when synced.
    private static readonly HashSet<string> UserDirectory = new(StringComparer.OrdinalIgnoreCase)
    {
        "displayName", "givenName", "surname", "jobTitle", "department", "companyName", "employeeId",
        "employeeType", "officeLocation", "streetAddress", "city", "state", "postalCode", "country",
        "mobilePhone", "faxNumber",
    };

    // Cloud-mastered even for synced users (needed for licensing, etc.).
    private static readonly HashSet<string> UserAlwaysCloud = new(StringComparer.OrdinalIgnoreCase) { "usageLocation" };

    private static readonly HashSet<string> GroupDirectory = new(StringComparer.OrdinalIgnoreCase)
    {
        "displayName", "description", "mailNickname",
    };

    public static CloudPropertyEditability Classify(CloudObjectKind kind, string key, bool isSynced) => kind switch
    {
        CloudObjectKind.User when UserAlwaysCloud.Contains(key) => CloudPropertyEditability.Editable,
        CloudObjectKind.User when UserDirectory.Contains(key) =>
            isSynced ? CloudPropertyEditability.OnPremMastered : CloudPropertyEditability.Editable,
        CloudObjectKind.Group when GroupDirectory.Contains(key) =>
            isSynced ? CloudPropertyEditability.OnPremMastered : CloudPropertyEditability.Editable,
        _ => CloudPropertyEditability.SystemReadOnly, // devices + everything not listed
    };

    public static string? Tooltip(CloudPropertyEditability e) => e switch
    {
        CloudPropertyEditability.OnPremMastered => "Synced from on-premises AD — edit it in Active Directory and run a directory sync.",
        CloudPropertyEditability.SystemReadOnly => "System-managed — not editable in Entra ID.",
        _ => null,
    };

    /// <summary>Builds a classified row: editable rows keep the raw value (blank when empty); others show "—".</summary>
    public static CloudProperty Make(CloudObjectKind kind, bool isSynced, string key, string label, string? value)
    {
        var editability = Classify(kind, key, isSynced);
        var display = string.IsNullOrWhiteSpace(value)
            ? (editability == CloudPropertyEditability.Editable ? string.Empty : "—")
            : value!;
        return new CloudProperty(key, label, display, editability, Tooltip(editability));
    }

    /// <summary>Writable single-valued user keys (for the PATCH key→setter mapping).</summary>
    public static bool IsUserWritable(string key) => UserAlwaysCloud.Contains(key) || UserDirectory.Contains(key);

    /// <summary>Writable single-valued group keys.</summary>
    public static bool IsGroupWritable(string key) => GroupDirectory.Contains(key);
}
