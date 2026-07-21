using System.ComponentModel;
using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class NewOuWindow : Window
{
    public NewOuWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The create isn't cancellable, so block Cancel/Esc/X while it's in flight: dismissing mid-write would
        // orphan a half-created OU and then set DialogResult on a closed window. The success path sets
        // DialogResult=true before Close(), so it's allowed through.
        if (DialogResult != true && DataContext is NewOuViewModel { IsBusy: true })
            e.Cancel = true;
        base.OnClosing(e);
    }
}
