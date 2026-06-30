namespace UnifiedDirectoryManager.Models;

/// <summary>
/// A leaf object (user / computer / group) shown as a row in the object-list pane.
/// Surfaced attribute values are keyed by their real lDAPDisplayName; columns read them by name.
/// </summary>
public sealed class AdObjectRow
{
    public required string DistinguishedName { get; init; }
    public required string Name { get; init; }
    public AdObjectType Type { get; init; }

    /// <summary>True when the account's userAccountControl has the ACCOUNTDISABLE flag set.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>True when an Everyone:Deny Delete/DeleteTree ACE protects the object from accidental deletion.</summary>
    public bool IsProtected { get; set; }

    /// <summary>Account status text for display/sorting (blank for objects without an account state).</summary>
    public string StatusText => Type is AdObjectType.User or AdObjectType.Computer
        ? (IsDisabled ? "Disabled" : "Enabled")
        : string.Empty;

    /// <summary>Lock glyph for the "Protected" column (blank when the object is not protected).</summary>
    public string ProtectionGlyph => IsProtected ? "🔒" : string.Empty;

    /// <summary>Display-ready values keyed by lDAPDisplayName (DN-valued attrs already resolved to names).</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the display value for a column's lDAPDisplayName, or empty string if absent.</summary>
    public string Get(string ldapName) =>
        Values.TryGetValue(ldapName, out var v) ? v : string.Empty;

    /// <summary>Indexer for XAML column bindings ("[sAMAccountName]"); never throws on a missing key.</summary>
    public string this[string ldapName] => Get(ldapName);
}
