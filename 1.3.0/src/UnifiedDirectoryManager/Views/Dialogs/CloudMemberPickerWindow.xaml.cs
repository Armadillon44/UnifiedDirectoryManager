using System.Windows;
using System.Windows.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class CloudMemberPickerWindow : Window
{
    public CloudMemberPickerWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CloudMemberPickerViewModel vm && ResultsList.SelectedItem is CloudObjectRow)
            vm.AddToBasketCommand.Execute(ResultsList.SelectedItems);
    }

    private void OnBasketDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CloudMemberPickerViewModel vm && Basket.SelectedItem is CloudObjectRow row)
            vm.RemoveFromBasketCommand.Execute(row);
    }
}
