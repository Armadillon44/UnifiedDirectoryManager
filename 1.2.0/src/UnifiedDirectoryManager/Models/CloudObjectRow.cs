using CommunityToolkit.Mvvm.ComponentModel;

namespace UnifiedDirectoryManager.Models;

/// <summary>The kind of Entra ID object a <see cref="CloudObjectRow"/> represents.</summary>
public enum CloudObjectKind { User, Group, Device, Other }

/// <summary>
/// A row in a cloud (Entra ID) object list. Mirrors <see cref="AdObjectRow"/>'s arbitrary-attribute
/// projection (a <see cref="Values"/> dictionary + a <c>this[key]</c> indexer) so the same dynamic
/// <c>GridView</c> column binding (<c>DisplayMemberBinding="[key]"</c>) works, and adds an observable
/// <see cref="IsChecked"/> for checkbox multi-selection.
/// </summary>
public sealed partial class CloudObjectRow : ObservableObject
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public CloudObjectKind Kind { get; init; }

    /// <summary>Checkbox selection state (drives the future bulk-operation set).</summary>
    [ObservableProperty] private bool _isChecked;

    /// <summary>Column values keyed by the <see cref="Services.CloudColumnCatalog"/> column key.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string key) => Values.TryGetValue(key, out var v) ? v : string.Empty;

    /// <summary>Indexer used by the dynamic GridView columns (XAML <c>DisplayMemberBinding="[key]"</c>).</summary>
    public string this[string key] => Get(key);

    public string Glyph => Kind switch
    {
        CloudObjectKind.User => "👤",
        CloudObjectKind.Group => "👥",
        CloudObjectKind.Device => "💻",
        _ => "•",
    };
}

/// <summary>One page of cloud objects plus the Graph <c>@odata.nextLink</c> for the next page (null = last).</summary>
public sealed record CloudPage(IReadOnlyList<CloudObjectRow> Items, string? NextLink);
