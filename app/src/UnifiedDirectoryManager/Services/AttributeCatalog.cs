using System.Text;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>Metadata for one AD attribute: its real name, friendly label, and value characteristics.</summary>
public sealed record AttributeMeta(
    string LdapName,
    string Friendly,
    bool IsMultiValued = false,
    bool IsDnValued = false,
    bool IsReadOnly = false,
    AttributeCategory Category = AttributeCategory.General,
    // INTEGER / INTEGER8 syntax: these don't marshal as a string through DirectoryEntry, so writes
    // must go via an LDAP modify using the decimal-string representation (see DirectoryService).
    bool IsInteger = false);

public enum AttributeCategory
{
    General,
    Account,
    Address,
    Organization,
    Email,
    Membership,
    System,
    Computer,
}

/// <summary>
/// Central friendly-name mediation. Logic everywhere uses lDAPDisplayName; the UI shows the friendly
/// label resolved here. Unknown attributes humanize their lDAPDisplayName so nothing breaks.
/// </summary>
public static class AttributeCatalog
{
    private static readonly AttributeMeta[] Definitions =
    {
        // --- General / identity ---
        new("cn", "Common name", Category: AttributeCategory.General, IsReadOnly: true),
        new("name", "Name", IsReadOnly: true, Category: AttributeCategory.General),
        new("displayName", "Display name", Category: AttributeCategory.General),
        new("givenName", "First name", Category: AttributeCategory.General),
        new("sn", "Last name", Category: AttributeCategory.General),
        new("middleName", "Middle name", Category: AttributeCategory.General),
        new("initials", "Initials", Category: AttributeCategory.General),
        new("description", "Description", Category: AttributeCategory.General),
        new("physicalDeliveryOfficeName", "Office", Category: AttributeCategory.General),
        new("telephoneNumber", "Telephone number", Category: AttributeCategory.General),
        new("wWWHomePage", "Web page", Category: AttributeCategory.General),
        new("info", "Notes", Category: AttributeCategory.General),

        // --- Account ---
        new("sAMAccountName", "Logon name (pre-Windows 2000)", Category: AttributeCategory.Account),
        new("userPrincipalName", "User logon name (UPN)", Category: AttributeCategory.Account),
        new("userAccountControl", "Account options", IsReadOnly: true, Category: AttributeCategory.Account, IsInteger: true),
        new("accountExpires", "Account expires", IsReadOnly: true, Category: AttributeCategory.Account, IsInteger: true),
        new("pwdLastSet", "Password last set", IsReadOnly: true, Category: AttributeCategory.Account, IsInteger: true),
        new("lastLogonTimestamp", "Last logon (approx.)", IsReadOnly: true, Category: AttributeCategory.Account, IsInteger: true),
        new("lockoutTime", "Locked out", IsReadOnly: true, Category: AttributeCategory.Account, IsInteger: true),
        new("homeDirectory", "Home folder path", Category: AttributeCategory.Account),
        new("homeDrive", "Home drive", Category: AttributeCategory.Account),
        new("scriptPath", "Logon script", Category: AttributeCategory.Account),
        new("profilePath", "Profile path", Category: AttributeCategory.Account),

        // --- Address ---
        new("streetAddress", "Street", Category: AttributeCategory.Address),
        new("postOfficeBox", "P.O. Box", IsMultiValued: true, Category: AttributeCategory.Address),
        new("l", "City", Category: AttributeCategory.Address),
        new("st", "State/Province", Category: AttributeCategory.Address),
        new("postalCode", "ZIP/Postal code", Category: AttributeCategory.Address),
        new("c", "Country/region code", Category: AttributeCategory.Address),
        new("co", "Country/region", Category: AttributeCategory.Address),
        new("countryCode", "Country code (numeric)", Category: AttributeCategory.Address, IsInteger: true),

        // --- Organization ---
        new("title", "Job title", Category: AttributeCategory.Organization),
        new("department", "Department", Category: AttributeCategory.Organization),
        new("company", "Company", Category: AttributeCategory.Organization),
        new("division", "Division", Category: AttributeCategory.Organization),
        new("employeeID", "Employee ID", Category: AttributeCategory.Organization),
        new("employeeNumber", "Employee number", Category: AttributeCategory.Organization),
        new("manager", "Manager", IsDnValued: true, Category: AttributeCategory.Organization),
        new("directReports", "Direct reports", IsMultiValued: true, IsDnValued: true, IsReadOnly: true, Category: AttributeCategory.Organization),
        new("mobile", "Mobile", Category: AttributeCategory.Organization),
        new("homePhone", "Home phone", Category: AttributeCategory.Organization),
        new("pager", "Pager", Category: AttributeCategory.Organization),
        new("facsimileTelephoneNumber", "Fax", Category: AttributeCategory.Organization),
        new("ipPhone", "IP phone", Category: AttributeCategory.Organization),

        // --- Email ---
        new("mail", "Email address", Category: AttributeCategory.Email),
        new("proxyAddresses", "Proxy addresses", IsMultiValued: true, Category: AttributeCategory.Email),
        new("mailNickname", "Email alias", Category: AttributeCategory.Email),
        new("targetAddress", "Target address", Category: AttributeCategory.Email),

        // --- Membership ---
        new("memberOf", "Member of", IsMultiValued: true, IsDnValued: true, IsReadOnly: true, Category: AttributeCategory.Membership),
        new("member", "Members", IsMultiValued: true, IsDnValued: true, IsReadOnly: true, Category: AttributeCategory.Membership),
        // Read-only: settable only to a group the object already belongs to, easy to break resource
        // access, and managed via the directory's own primary-group UI — not hand-edited here.
        new("primaryGroupID", "Primary group ID", IsReadOnly: true, Category: AttributeCategory.Membership, IsInteger: true),
        new("managedBy", "Managed by", IsDnValued: true, Category: AttributeCategory.Membership),
        new("groupType", "Group type", IsReadOnly: true, Category: AttributeCategory.Membership, IsInteger: true),

        // --- Computer ---
        new("dNSHostName", "DNS host name", Category: AttributeCategory.Computer),
        new("operatingSystem", "Operating system", IsReadOnly: true, Category: AttributeCategory.Computer),
        new("operatingSystemVersion", "OS version", IsReadOnly: true, Category: AttributeCategory.Computer),
        new("operatingSystemServicePack", "OS service pack", IsReadOnly: true, Category: AttributeCategory.Computer),
        new("location", "Location", Category: AttributeCategory.Computer),

        // --- System / read-only ---
        new("distinguishedName", "Distinguished name", IsReadOnly: true, Category: AttributeCategory.System),
        new("objectSid", "Security ID (SID)", IsReadOnly: true, Category: AttributeCategory.System),
        new("objectGUID", "Object GUID", IsReadOnly: true, Category: AttributeCategory.System),
        new("objectClass", "Object class", IsMultiValued: true, IsReadOnly: true, Category: AttributeCategory.System),
        new("objectCategory", "Object category", IsReadOnly: true, Category: AttributeCategory.System),
        new("whenCreated", "Created", IsReadOnly: true, Category: AttributeCategory.System),
        new("whenChanged", "Modified", IsReadOnly: true, Category: AttributeCategory.System),
        new("uSNCreated", "USN created", IsReadOnly: true, Category: AttributeCategory.System, IsInteger: true),
        new("uSNChanged", "USN changed", IsReadOnly: true, Category: AttributeCategory.System, IsInteger: true),
    };

    private static readonly Dictionary<string, AttributeMeta> ByLdap =
        Definitions.ToDictionary(d => d.LdapName, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, AttributeMeta> ByFriendly =
        Definitions
            .GroupBy(d => d.Friendly, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    // System / operational / constructed attributes that Active Directory computes or manages itself.
    // These must never be hand-edited in the Attribute Editor, even though many aren't in the curated
    // (editable) catalog above. Custom/extension attributes (e.g. extensionAttribute1-15) are NOT here,
    // so they remain editable — only AD-managed plumbing is locked down.
    private static readonly HashSet<string> SystemReadOnlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Identity / security (system-assigned or binary)
        "objectSid", "objectGUID", "sIDHistory", "nTSecurityDescriptor", "sAMAccountType",
        "primaryGroupToken", "msDS-KeyVersionNumber",
        // Account state computed / replicated by domain controllers
        "userAccountControl", "badPwdCount", "badPasswordTime", "lastLogon", "lastLogoff",
        "lastLogonTimestamp", "logonCount", "lockoutTime", "pwdLastSet",
        "msDS-User-Account-Control-Computed", "msDS-UserPasswordExpiryTimeComputed",
        "msDS-LastSuccessfulInteractiveLogonTime", "msDS-FailedInteractiveLogonCount",
        // Replication / metadata / structural plumbing
        "uSNCreated", "uSNChanged", "whenCreated", "whenChanged", "createTimeStamp", "modifyTimeStamp",
        "dSCorePropagationData", "instanceType", "objectCategory", "objectClass", "structuralObjectClass",
        "subSchemaSubEntry", "replPropertyMetaData", "replUpToDateVector", "repsFrom", "repsTo",
        "isCriticalSystemObject", "systemFlags", "fSMORoleOwner", "distinguishedName", "canonicalName",
        "name", "cn",
        // Back-links (maintained by AD from the other side of the link)
        "memberOf", "directReports",
    };

    // Constructed attribute families that are always read-only.
    private static readonly string[] SystemReadOnlyPrefixes =
        { "tokenGroups", "allowedAttributes", "allowedChildClasses", "msDS-Approx", "sDRightsEffective" };

    /// <summary>
    /// True for AD-managed system / operational / constructed attributes that must never be hand-edited,
    /// regardless of whether they appear in the curated catalog. Drives read-only in the Attribute Editor.
    /// </summary>
    public static bool IsSystemReadOnly(string ldapName) =>
        SystemReadOnlyNames.Contains(ldapName) ||
        SystemReadOnlyPrefixes.Any(p => ldapName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>All curated attributes, useful for column/condition pickers.</summary>
    public static IReadOnlyList<AttributeMeta> All => Definitions;

    /// <summary>Friendly label for an attribute; humanizes unknown lDAPDisplayNames.</summary>
    public static string Friendly(string ldapName) =>
        ByLdap.TryGetValue(ldapName, out var m) ? m.Friendly : Humanize(ldapName);

    /// <summary>Real lDAPDisplayName for a friendly label; returns the input unchanged if unknown.</summary>
    public static string Ldap(string friendly) =>
        ByFriendly.TryGetValue(friendly, out var m) ? m.LdapName : friendly;

    /// <summary>
    /// Metadata for an attribute; synthesizes a default for unknown names. AD-managed system attributes
    /// are forced read-only even if they aren't curated (and even if a curated entry forgot the flag).
    /// </summary>
    public static AttributeMeta Meta(string ldapName)
    {
        if (ByLdap.TryGetValue(ldapName, out var m))
            return IsSystemReadOnly(ldapName) && !m.IsReadOnly ? m with { IsReadOnly = true } : m;
        return new AttributeMeta(ldapName, Humanize(ldapName), IsReadOnly: IsSystemReadOnly(ldapName));
    }

    public static bool IsKnown(string ldapName) => ByLdap.ContainsKey(ldapName);

    /// <summary>True for INTEGER/INTEGER8-syntax attributes, which must be written via LDAP (decimal string) rather than DirectoryEntry.</summary>
    public static bool IsInteger(string ldapName) => ByLdap.TryGetValue(ldapName, out var m) && m.IsInteger;

    /// <summary>Turns "physicalDeliveryOfficeName" into "Physical Delivery Office Name".</summary>
    internal static string Humanize(string ldapName)
    {
        if (string.IsNullOrWhiteSpace(ldapName)) return ldapName;
        var sb = new StringBuilder(ldapName.Length + 8);
        for (int i = 0; i < ldapName.Length; i++)
        {
            var ch = ldapName[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(ldapName[i - 1]))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(ch) : ch);
        }
        return sb.ToString();
    }
}
