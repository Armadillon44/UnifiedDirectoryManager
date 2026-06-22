using System.Windows;
using System.Windows.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class HybridGroupPickerWindow : Window
{
    public HybridGroupPickerWindow()
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
        if (DataContext is HybridGroupPickerViewModel vm && ResultsList.SelectedItem is GroupRef)
            vm.AddToBasketCommand.Execute(ResultsList.SelectedItems);
    }

    private void OnBasketDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HybridGroupPickerViewModel vm && Basket.SelectedItem is GroupRef row)
            vm.RemoveFromBasketCommand.Execute(row);
    }
}
