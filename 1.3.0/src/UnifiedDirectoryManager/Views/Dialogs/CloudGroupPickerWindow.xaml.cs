using System.Windows;
using System.Windows.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class CloudGroupPickerWindow : Window
{
    public CloudGroupPickerWindow()
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
        if (DataContext is CloudGroupPickerViewModel vm && ResultsList.SelectedItem is CloudGroup)
            vm.AddToBasketCommand.Execute(ResultsList.SelectedItems);
    }

    private void OnBasketDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CloudGroupPickerViewModel vm && Basket.SelectedItem is CloudGroup row)
            vm.RemoveFromBasketCommand.Execute(row);
    }
}
