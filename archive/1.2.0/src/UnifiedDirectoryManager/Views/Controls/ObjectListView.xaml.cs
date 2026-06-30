using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Controls;

public partial class ObjectListView : UserControl
{
    /// <summary>Clipboard/drag format for moving rows onto a tree OU (in-process object reference).</summary>
    public const string MoveDataFormat = "UnifiedDirectoryManager.AdObjectRows";

    private ObjectListViewModel? _vm;
    private readonly Dictionary<GridViewColumn, string> _columnKeys = new();
    private string? _sortKey;
    private bool _sortAscending = true;

    private Point _dragStart;
    private bool _maybeDrag;

    public ObjectListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.ColumnsChanged -= OnColumnsChanged;
        _vm = DataContext as ObjectListViewModel;
        if (_vm is not null) _vm.ColumnsChanged += OnColumnsChanged;
        RebuildColumns();
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => RebuildColumns();

    /// <summary>Rebuilds the GridView: a fixed Name + Type column, then one per visible attribute.</summary>
    private void RebuildColumns()
    {
        Grid.Columns.Clear();
        _columnKeys.Clear();
        if (_vm is null) return;

        AddColumn("Name", "Name", width: 200);
        AddColumn("Type", "Type", width: 90);
        AddColumn("Status", "StatusText", width: 80);
        AddColumn("Protected", "ProtectionGlyph", width: 70);

        foreach (var col in _vm.Columns)
        {
            if (!col.IsVisible) continue;
            AddColumn(col.Header, $"[{col.LdapName}]", col.Width, sortKey: col.LdapName);
        }
    }

    private void AddColumn(string header, string bindingPath, double width, string? sortKey = null)
    {
        var column = new GridViewColumn
        {
            Header = header,
            Width = width,
            DisplayMemberBinding = new Binding(bindingPath),
        };
        Grid.Columns.Add(column);
        _columnKeys[column] = sortKey ?? header; // "Name" / "Type" sort by their own property
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || _vm is null) return;
        if (!_columnKeys.TryGetValue(column, out var key)) return;

        if (_sortKey == key) _sortAscending = !_sortAscending;
        else { _sortKey = key; _sortAscending = true; }

        if (_vm.RowsView is ListCollectionView view)
            view.CustomSort = new RowComparer(key, _sortAscending);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only open when the double-click actually landed on a row — not the scrollbar / header / empty space.
        if (_vm is not null && RowFrom(e.OriginalSource as DependencyObject) is { } row)
            _vm.RequestOpen(row);
    }

    // --- Drag source: drag selected rows onto an OU in the navigation tree to move them ---

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        // Only arm a move-drag when the press lands on a row that is ALREADY selected and no selection
        // modifier is held. This keeps plain clicks, empty-space clicks, and Ctrl/Shift range-selection
        // working, and prevents dragging a stale/empty selection. (WPF defers collapsing a multi-selection
        // until mouse-up, so pressing a selected row still lets us drag the whole selection.)
        var row = RowFrom(e.OriginalSource as DependencyObject);
        _maybeDrag = row is not null
                     && List.SelectedItems.Contains(row)
                     && Keyboard.Modifiers == ModifierKeys.None;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _maybeDrag = false;
        var rows = List.SelectedItems.OfType<AdObjectRow>().ToList();
        if (rows.Count == 0) return;

        try { DragDrop.DoDragDrop(List, new DataObject(MoveDataFormat, rows), DragDropEffects.Move); }
        catch { /* a drag that the OS aborts is harmless */ }
    }

    /// <summary>Right-click selects the row under the cursor (unless it's already part of the selection),
    /// so the context-menu actions target what the user clicked.</summary>
    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = RowFrom(e.OriginalSource as DependencyObject);
        if (row is not null && !List.SelectedItems.Contains(row))
            List.SelectedItem = row;
    }

    private static AdObjectRow? RowFrom(DependencyObject? source)
    {
        while (source is not null and not ListViewItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListViewItem)?.DataContext as AdObjectRow;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SelectedRows.Clear();
        foreach (var item in List.SelectedItems)
            if (item is AdObjectRow row) _vm.SelectedRows.Add(row);

        // Drive the edit pane from the single (most-recent) selection.
        if (List.SelectedItems.Count >= 1)
            _vm.SelectedRow = List.SelectedItems[^1] as AdObjectRow;
        else
            _vm.SelectedRow = null;
    }

    /// <summary>Compares rows by a column key (Name/Type property or an attribute value).</summary>
    private sealed class RowComparer : IComparer
    {
        private readonly string _key;
        private readonly int _direction;

        public RowComparer(string key, bool ascending)
        {
            _key = key;
            _direction = ascending ? 1 : -1;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not AdObjectRow a || y is not AdObjectRow b) return 0;
            var av = ValueFor(a);
            var bv = ValueFor(b);
            return _direction * string.Compare(av, bv, StringComparison.CurrentCultureIgnoreCase);
        }

        private string ValueFor(AdObjectRow row) => _key switch
        {
            "Name" => row.Name,
            "Type" => row.Type.ToString(),
            // Sort active accounts before disabled ones (ascending).
            "Status" => row.IsDisabled ? "1" : "0",
            // Sort protected objects before unprotected ones (ascending).
            "Protected" => row.IsProtected ? "0" : "1",
            _ => row.Get(_key),
        };
    }
}
