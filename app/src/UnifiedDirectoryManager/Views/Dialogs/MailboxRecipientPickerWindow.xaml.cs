using System.Windows;
using System.Windows.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class MailboxRecipientPickerWindow : Window
{
    public MailboxRecipientPickerWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is MailboxRecipientPickerViewModel { MultiSelect: true } vm)
        {
            // Highlighting rows and pressing OK is the obvious thing to do, so treat it as intent rather than
            // closing on an empty basket — which returns null, indistinguishable from Cancel, and adds nobody
            // without a word to say so.
            if (vm.Basket.Count == 0) vm.AddToBasketCommand.Execute(ResultsList.SelectedItems);
            // An emptied basket that STARTED with entries is a deliberate clear, and refusing it is what
            // makes a list that declares itself clearable impossible to clear.
            if (vm.Basket.Count == 0 && !vm.AllowEmpty)
            {
                vm.Status = "Add at least one recipient to the list on the right.";
                return; // stay open
            }
        }

        DialogResult = true;
        Close();
    }

    private void OnAddToBasket(object sender, RoutedEventArgs e)
    {
        // The command needs the ListView's live selection, which only the view has.
        if (DataContext is MailboxRecipientPickerViewModel vm)
            vm.AddToBasketCommand.Execute(ResultsList.SelectedItems);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not MailboxRecipient) return;

        // In multi-select, a double-click adds to the basket rather than closing: committing on the first
        // double-click would discard everything already gathered.
        if (DataContext is MailboxRecipientPickerViewModel { MultiSelect: true } vm)
        {
            vm.AddToBasketCommand.Execute(ResultsList.SelectedItems);
            return;
        }

        DialogResult = true;
        Close();
    }
}
