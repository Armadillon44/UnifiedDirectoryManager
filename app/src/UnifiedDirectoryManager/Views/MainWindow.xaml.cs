using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UnifiedDirectoryManager.Services;
using UnifiedDirectoryManager.ViewModels;
using UnifiedDirectoryManager.Views.Controls;

namespace UnifiedDirectoryManager.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _settingsApplied;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as MainViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
            ApplySettings();
            ApplyDock();
        };
        Loaded += (_, _) => { ApplySettings(); ApplyDock(); };
        Closing += OnClosing;
    }

    /// <summary>Applies persisted window + tree sizes once the view model is available.</summary>
    private void ApplySettings()
    {
        if (_vm is null || _settingsApplied) return;
        var s = _vm.Settings;

        if (s.WindowWidth > 200) Width = s.WindowWidth;
        if (s.WindowHeight > 200) Height = s.WindowHeight;
        WindowState = s.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        if (s.TreeWidth > 80) RootGrid.ColumnDefinitions[0].Width = new GridLength(s.TreeWidth);

        _settingsApplied = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_vm is null) return;
        var s = _vm.Settings;

        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = ActualWidth;
            s.WindowHeight = ActualHeight;
        }
        s.TreeWidth = RootGrid.ColumnDefinitions[0].ActualWidth;

        if (_vm.EditDock == EditPaneDock.Right && PaneHost.ColumnDefinitions.Count == 3)
            s.EditPaneWidth = PaneHost.ColumnDefinitions[2].ActualWidth;
        else if (_vm.EditDock == EditPaneDock.Bottom && PaneHost.RowDefinitions.Count == 3)
            s.EditPaneHeight = PaneHost.RowDefinitions[2].ActualHeight;

        _vm.SaveSettings();
    }

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm is not null && e.NewValue is TreeNodeViewModel node)
            _vm.SelectedNode = node;
    }

    // --- Right-click ▸ Properties on an OU/container tree node ---

    /// <summary>Suppresses the tree context menu on non-container nodes (cloud sections, "Loading…"),
    /// so only OU/container/domain nodes offer Properties.</summary>
    private void OnNodeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TreeNodeViewModel { IsContainerNode: true }) return;
        e.Handled = true;
    }

    private void OnNodePropertiesClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        // The ContextMenu is attached to the TreeViewItem, so the menu item inherits the node as its DataContext;
        // fall back to the menu's PlacementTarget if that ever isn't set.
        var node = (sender as FrameworkElement)?.DataContext as TreeNodeViewModel;
        if (node is null && sender is MenuItem { Parent: ContextMenu cm })
            node = (cm.PlacementTarget as FrameworkElement)?.DataContext as TreeNodeViewModel;
        _vm.ShowNodeProperties(node);
    }

    // --- Drop target: dropping list rows onto an OU node moves them there ---

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        var overOu = e.Data.GetDataPresent(ObjectListView.MoveDataFormat)
                     && NodeFrom(e.OriginalSource as DependencyObject) is { IsPlaceholder: false };
        e.Effects = overOu ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (_vm is null) return;
        if (e.Data.GetData(ObjectListView.MoveDataFormat) is not List<Models.AdObjectRow> rows || rows.Count == 0) return;
        if (NodeFrom(e.OriginalSource as DependencyObject) is not { IsPlaceholder: false } node) return;
        await _vm.MoveRowsToOuAsync(rows, node.DistinguishedName);
    }

    private static TreeNodeViewModel? NodeFrom(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as TreeViewItem)?.DataContext as TreeNodeViewModel;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.EditDock))
            ApplyDock();
    }

    /// <summary>Re-lays out BOTH the on-prem (list+edit) and cloud (list+properties) panes for the
    /// current Right/Bottom dock setting, so the one toggle moves both.</summary>
    private void ApplyDock()
    {
        if (_vm is null) return;
        var s = _vm.Settings;
        var right = _vm.EditDock == EditPaneDock.Right;

        // Preserve the current pane size before tearing the layout down, so a dock toggle keeps it.
        if (PaneHost.ColumnDefinitions.Count == 3 && PaneHost.ColumnDefinitions[2].ActualWidth > 80) s.EditPaneWidth = PaneHost.ColumnDefinitions[2].ActualWidth;
        if (PaneHost.RowDefinitions.Count == 3 && PaneHost.RowDefinitions[2].ActualHeight > 80) s.EditPaneHeight = PaneHost.RowDefinitions[2].ActualHeight;

        LayoutPane(PaneHost, ListHost, PaneSplitter, EditHost, right, s);
        LayoutPane(CloudPaneHost, CloudListHost, CloudPaneSplitter, CloudDetailHost, right, s);
    }

    /// <summary>Lays a list + splitter + detail trio into a host grid, docked right or bottom.</summary>
    private static void LayoutPane(Grid host, FrameworkElement list, GridSplitter splitter, FrameworkElement detail, bool right, AppSettings s)
    {
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();

        if (right)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(280, s.EditPaneWidth)), MinWidth = 280 });

            SetPosition(list, row: 0, column: 0);
            SetPosition(detail, row: 0, column: 2);
            SetPosition(splitter, row: 0, column: 1);
            splitter.Width = 5;
            splitter.Height = double.NaN;
            splitter.HorizontalAlignment = HorizontalAlignment.Center;
            splitter.VerticalAlignment = VerticalAlignment.Stretch;
            if (detail is Border rb) rb.BorderThickness = new Thickness(1, 0, 0, 0);
        }
        else
        {
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(160, s.EditPaneHeight)), MinHeight = 160 });

            SetPosition(list, row: 0, column: 0);
            SetPosition(detail, row: 2, column: 0);
            SetPosition(splitter, row: 1, column: 0);
            splitter.Height = 5;
            splitter.Width = double.NaN;
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            splitter.VerticalAlignment = VerticalAlignment.Center;
            if (detail is Border bb) bb.BorderThickness = new Thickness(0, 1, 0, 0);
        }
    }

    private static void SetPosition(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }
}
