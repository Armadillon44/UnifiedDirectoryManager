using CommunityToolkit.Mvvm.ComponentModel;

namespace UnifiedDirectoryManager.Models;

/// <summary>A selectable/sortable column in the object-list pane, identified by lDAPDisplayName.</summary>
public partial class ColumnDefinition : ObservableObject
{
    public required string LdapName { get; init; }

    /// <summary>Friendly header text shown to the user.</summary>
    public required string Header { get; init; }

    [ObservableProperty]
    private bool _isVisible;

    public double Width { get; set; } = 160;
}
