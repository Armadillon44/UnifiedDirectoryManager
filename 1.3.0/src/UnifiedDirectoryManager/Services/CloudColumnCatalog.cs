using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>Which cloud list is being shown (drives the column set and the loader).</summary>
public enum CloudListMode { Users, Groups, Devices, GroupMembers }

/// <summary>
/// Defines the selectable columns for each cloud list mode (key → friendly header + default visibility).
/// Keys match the <see cref="CloudObjectRow.Values"/> keys populated by <see cref="GraphService"/>.
/// <c>DisplayName</c> is a fixed leading column (not listed here), like the on-prem list's "Name".
/// </summary>
public static class CloudColumnCatalog
{
    private static readonly (string Key, string Header, bool Visible)[] UserCols =
    {
        ("userPrincipalName", "User principal name", true),
        ("id", "Object ID", true),
        ("mail", "Email", true),
        ("accountEnabled", "Enabled", true),
        ("onPremisesSyncEnabled", "Directory sync", true),
        ("jobTitle", "Job title", false),
        ("department", "Department", false),
        ("usageLocation", "Usage location", false),
        ("userType", "User type", false),
        ("createdDateTime", "Created", false),
    };

    private static readonly (string Key, string Header, bool Visible)[] GroupCols =
    {
        ("id", "Object ID", true),
        ("groupType", "Type", true),
        ("origin", "Origin", true),
        ("membership", "Membership", true),
        ("teams", "Teams", true),
        ("mail", "Email", false),
        ("visibility", "Visibility", false),
        ("description", "Description", false),
    };

    private static readonly (string Key, string Header, bool Visible)[] DeviceCols =
    {
        ("id", "Object ID", true),
        ("operatingSystem", "OS", true),
        ("operatingSystemVersion", "OS version", true),
        ("trustType", "Join type", true),
        ("isCompliant", "Compliant", true),
        ("isManaged", "Managed", false),
        ("accountEnabled", "Enabled", true),
        ("approximateLastSignInDateTime", "Last sign-in", false),
    };

    private static readonly (string Key, string Header, bool Visible)[] MemberCols =
    {
        ("type", "Type", true),
        ("userPrincipalName", "UPN / mail", true),
        ("id", "Object ID", true),
    };

    private static (string Key, string Header, bool Visible)[] Defs(CloudListMode mode) => mode switch
    {
        CloudListMode.Users => UserCols,
        CloudListMode.Groups => GroupCols,
        CloudListMode.Devices => DeviceCols,
        CloudListMode.GroupMembers => MemberCols,
        _ => UserCols,
    };

    public static IEnumerable<ColumnDefinition> Columns(CloudListMode mode) =>
        Defs(mode).Select(d => new ColumnDefinition { LdapName = d.Key, Header = d.Header, IsVisible = d.Visible });

    /// <summary>Friendly header for each column key (for label/value detail views).</summary>
    public static IReadOnlyDictionary<string, string> Headers(CloudListMode mode) =>
        Defs(mode).ToDictionary(d => d.Key, d => d.Header, StringComparer.OrdinalIgnoreCase);
}
