using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views;

public partial class ConnectionWindow : Window
{
    public ConnectionWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ConnectionViewModel oldVm)
            oldVm.ConnectionSucceeded -= OnConnectionSucceeded;
        if (e.NewValue is ConnectionViewModel vm)
            vm.ConnectionSucceeded += OnConnectionSucceeded;
    }

    // The startup dialog closes itself on a successful connection (the form lives in ConnectionFormView).
    private void OnConnectionSucceeded(object? sender, System.EventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
