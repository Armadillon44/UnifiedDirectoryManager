using CommunityToolkit.Mvvm.ComponentModel;

namespace UnifiedDirectoryManager.Models;

/// <summary>The kind of cloud object a <see cref="CloudObjectRow"/> represents.</summary>
public enum CloudObjectKind { User, Group, Device, Mailbox, Other }

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
        CloudObjectKind.Mailbox => "📬",
        _ => "•",
    };
}

/// <summary>One page of cloud objects plus the Graph <c>@odata.nextLink</c> for the next page (null = last).</summary>
public sealed record CloudPage(IReadOnlyList<CloudObjectRow> Items, string? NextLink);

/// <summary>
/// One capped result set from Exchange Online. Exchange PowerShell has no skip/continuation token — a cmdlet
/// either streams everything or stops at <c>-ResultSize</c> — so there is no next-page cursor to hand back.
/// <see cref="Capped"/> is true when the cap was reached and results were therefore left behind, which callers
/// must surface: silently showing the first N of an unknown total reads as "that's all of them".
/// </summary>
public sealed record ExchangePage(IReadOnlyList<CloudObjectRow> Items, bool Capped);
