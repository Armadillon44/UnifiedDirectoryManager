using System.ComponentModel;
using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class RenameGroupWindow : Window
{
    public RenameGroupWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
        Loaded += async (_, _) =>
        {
            if (DataContext is not RenameGroupViewModel vm) return;
            await vm.LoadAsync();
            // An Entra group has only the display name, so that is where typing starts.
            var first = NameBox.IsVisible ? NameBox : DisplayNameBox;
            first.Focus();
            first.SelectAll();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A rename is not cancellable once it is writing: closing mid-way would leave the operator not knowing
        // which names changed. The check and the writes are short, so the window simply waits.
        if (DataContext is RenameGroupViewModel { IsBusy: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
