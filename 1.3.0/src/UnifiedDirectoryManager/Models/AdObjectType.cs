namespace UnifiedDirectoryManager.Models;

/// <summary>Kind of Active Directory object, used to choose icons, filters and editors.</summary>
public enum AdObjectType
{
    Unknown = 0,
    Domain,
    OrganizationalUnit,
    Container,
    User,
    Computer,
    Group,
    Contact,
}

public static class AdObjectTypeExtensions
{
    /// <summary>True for node types that live in the navigation tree (have children worth expanding).</summary>
    public static bool IsContainerLike(this AdObjectType type) =>
        type is AdObjectType.Domain or AdObjectType.OrganizationalUnit or AdObjectType.Container;

    /// <summary>Maps an object's LDAP objectClass collection to our enum. Order matters (most specific first).</summary>
    public static AdObjectType FromClasses(IEnumerable<string> objectClasses)
    {
        var classes = new HashSet<string>(objectClasses, StringComparer.OrdinalIgnoreCase);
        if (classes.Contains("computer")) return AdObjectType.Computer;
        if (classes.Contains("group")) return AdObjectType.Group;
        if (classes.Contains("organizationalUnit")) return AdObjectType.OrganizationalUnit;
        if (classes.Contains("user")) return AdObjectType.User;
        if (classes.Contains("contact")) return AdObjectType.Contact;
        if (classes.Contains("domainDNS")) return AdObjectType.Domain;
        if (classes.Contains("container") || classes.Contains("builtinDomain")) return AdObjectType.Container;
        return AdObjectType.Unknown;
    }
}
