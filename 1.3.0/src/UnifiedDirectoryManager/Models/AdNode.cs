namespace UnifiedDirectoryManager.Models;

/// <summary>A container in the directory tree (domain root, OU, or container).</summary>
public sealed class AdNode
{
    public required string DistinguishedName { get; init; }

    /// <summary>Friendly RDN value (e.g. "Sales" for OU=Sales,...).</summary>
    public required string Name { get; init; }

    public AdObjectType Type { get; init; } = AdObjectType.Container;

    /// <summary>Hint that the node has expandable children; used to show the lazy expander arrow.</summary>
    public bool HasChildren { get; init; } = true;
}
