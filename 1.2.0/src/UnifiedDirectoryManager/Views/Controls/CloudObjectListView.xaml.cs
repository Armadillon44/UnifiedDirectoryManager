using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Controls;

public partial class CloudObjectListView : UserControl
{
    private CloudObjectListViewModel? _vm;
    private readonly Dictionary<GridViewColumn, string> _columnKeys = new();
    private string? _sortKey;
    private bool _sortAscending = true;

    public CloudObjectListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.ColumnsChanged -= OnColumnsChanged;
        _vm = DataContext as CloudObjectListViewModel;
        if (_vm is not null) _vm.ColumnsChanged += OnColumnsChanged;
        RebuildColumns();
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => RebuildColumns();

    /// <summary>Rebuilds the GridView: a select-all CheckBox column, a fixed Name column, then one per visible attribute.</summary>
    private void RebuildColumns()
    {
        Grid.Columns.Clear();
        _columnKeys.Clear();
        if (_vm is null) return;

        // Leading checkbox column with a "select all" header bound to the VM.
        var selectAll = new CheckBox { DataContext = _vm, ToolTip = "Select all loaded rows" };
        selectAll.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(CloudObjectListViewModel.SelectAll)) { Mode = BindingMode.TwoWay });
        Grid.Columns.Add(new GridViewColumn
        {
            Header = selectAll,
            Width = 34,
            CellTemplate = (DataTemplate)Resources["CheckCell"],
        });

        AddColumn("Name", "DisplayName", width: 220, sortKey: "Name");
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
        _columnKeys[column] = sortKey ?? header;
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || _vm is null) return;
        if (!_columnKeys.TryGetValue(column, out var key)) return; // checkbox header has no sort key

        if (_sortKey == key) _sortAscending = !_sortAscending;
        else { _sortKey = key; _sortAscending = true; }

        if (_vm.RowsView is ListCollectionView view)
            view.CustomSort = new RowComparer(key, _sortAscending);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only open when the double-click actually landed on a row — not the scrollbar / header / empty space.
        if (_vm is not null && RowFrom(e.OriginalSource as DependencyObject) is { } row)
            _vm.RequestOpen(row); // host opens a properties window
    }

    private static CloudObjectRow? RowFrom(DependencyObject? source)
    {
        while (source is not null and not ListViewItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListViewItem)?.DataContext as CloudObjectRow;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null) _vm.SelectedRow = List.SelectedItem as CloudObjectRow;
    }

    /// <summary>Compares rows by Name or an attribute value.</summary>
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
            if (x is not CloudObjectRow a || y is not CloudObjectRow b) return 0;
            var av = _key == "Name" ? a.DisplayName : a.Get(_key);
            var bv = _key == "Name" ? b.DisplayName : b.Get(_key);
            return _direction * string.Compare(av, bv, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
