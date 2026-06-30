using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class BulkCreateUsersWindow : Window
{
    public BulkCreateUsersWindow()
    {
        InitializeComponent();
        SyncPasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is BulkCreateUsersViewModel vm) vm.SyncPassword = SyncPasswordBox.Password;
        };
        // Note: templates are loaded on open. We deliberately don't reload on Activated — re-activating after
        // the modal capture window closes would re-fire SelectedTemplate and reset the batch OU/UPN suffix.
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not BulkCreateUsersViewModel vm) return;
        // Ignore double-clicks that land on the Edit/Remove buttons (they handle themselves).
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null) return;
        if ((sender as DataGrid)?.SelectedItem is BulkCreateRowViewModel row) vm.EditRowCommand.Execute(row);
    }

    private static T? FindAncestor<T>(DependencyObject from) where T : DependencyObject
    {
        for (DependencyObject? cur = from; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is T match) return match;
        return null;
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        // The window stays open after a run so the operator can read inline status and retry the cloud phase.
        if (DataContext is BulkCreateUsersViewModel vm) await vm.RunCommand.ExecuteAsync(null);
    }

    private async void OnRetryCloud(object sender, RoutedEventArgs e)
    {
        if (DataContext is BulkCreateUsersViewModel vm) await vm.RetryCloudCommand.ExecuteAsync(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
