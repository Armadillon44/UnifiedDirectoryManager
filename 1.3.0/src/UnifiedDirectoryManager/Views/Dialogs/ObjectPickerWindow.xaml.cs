using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class ObjectPickerWindow : Window
{
    public ObjectPickerWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A single-select picker (e.g. Manager) doesn't need the "Add → basket" multi-select UI:
        // hide it and let the operator just pick a row (double-click or highlight + OK).
        if (DataContext is ObjectPickerViewModel { MultiSelect: false })
        {
            AddPanel.Visibility = Visibility.Collapsed;
            BasketPanel.Visibility = Visibility.Collapsed;
            AddCol.Width = new GridLength(0);
            BasketCol.Width = new GridLength(0);
            ResultsList.SelectionMode = SelectionMode.Single;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ObjectPickerViewModel vm || ResultsList.SelectedItem is not AdObjectRow) return;
        // Single-select: a double-click is the pick — commit and close. Multi-select: add to the basket.
        if (!vm.MultiSelect)
        {
            DialogResult = true;
            Close();
            return;
        }
        vm.AddToBasketCommand.Execute(ResultsList.SelectedItems);
    }

    private void OnBasketDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ObjectPickerViewModel vm && Basket.SelectedItem is AdObjectRow row)
            vm.RemoveFromBasketCommand.Execute(row);
    }
}
