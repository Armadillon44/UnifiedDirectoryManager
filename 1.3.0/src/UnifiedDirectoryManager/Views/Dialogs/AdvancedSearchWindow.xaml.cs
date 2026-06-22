using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class AdvancedSearchWindow : Window
{
    public AdvancedSearchWindow() => InitializeComponent();

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        if (DataContext is AdvancedSearchViewModel vm)
            vm.SearchCommand.Execute(null);
        DialogResult = true;
        Close();
    }
}
