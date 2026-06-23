using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class CopyGroupsWindow : Window
{
    public CopyGroupsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is CopyGroupsViewModel vm) await vm.LoadAsync(); };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
