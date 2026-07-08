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
        DialogResult = true;
        Close();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is MailboxRecipient)
        {
            DialogResult = true;
            Close();
        }
    }
}
