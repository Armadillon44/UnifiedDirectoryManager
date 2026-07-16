namespace UnifiedDirectoryManager.Models;

/// <summary>
/// A reusable new-user creation template. Attribute defaults are keyed by lDAPDisplayName and
/// may contain tokens ({first}, {last}, {initials}, {sam}, {upnSuffix}, ...) resolved per user.
/// </summary>
public sealed class UserTemplate
{
    public string Name { get; set; } = "New template";
    public string Description { get; set; } = string.Empty;

    /// <summary>DN of the OU/container new users are created in.</summary>
    public string TargetOu { get; set; } = string.Empty;

    /// <summary>UPN suffix (e.g. "corp.example.com") used by the {upnSuffix} token.</summary>
    public string UpnSuffix { get; set; } = string.Empty;

    /// <summary>Attribute defaults / token patterns keyed by lDAPDisplayName (includes mail, userPrincipalName).</summary>
    public Dictionary<string, string> AttributeDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Proxy address patterns (token-supported, one per entry), e.g. "SMTP:{sam}@lacrossefootwear.com".</summary>
    public List<string> ProxyAddressPatterns { get; set; } = new();

    /// <summary>Groups (by DN) the new user is added to.</summary>
    public List<string> GroupDns { get; set; } = new();

    /// <summary>
    /// Entra ID (cloud) security / Microsoft 365 groups the new user is added to after creation (via Microsoft
    /// Graph). Because cloud group membership requires the user to exist in Entra first, selecting any of these
    /// makes a post-create Entra Connect sync mandatory (the new-user wizard runs the sync, waits for the user
    /// to appear, then adds them).
    /// </summary>
    public List<CloudGroupRef> CloudGroups { get; set; } = new();

    /// <summary>
    /// Exchange Online distribution lists / mail-enabled security groups the new user is added to after
    /// creation (via Add-DistributionGroupMember — Graph can't modify these). Like cloud groups, these are
    /// applied post-sync; they additionally require the user to be provisioned as an Exchange recipient, which
    /// can lag a little behind the Entra sync (a create-time add may need the "Retry cloud" step).
    /// </summary>
    public List<DistributionGroupRef> DistributionGroups { get; set; } = new();

    /// <summary>Default account state at creation.</summary>
    public bool EnabledByDefault { get; set; } = true;

    /// <summary>Force password change at next logon. Off by default; still selectable per template.</summary>
    public bool MustChangePasswordAtNextLogon { get; set; }
}
