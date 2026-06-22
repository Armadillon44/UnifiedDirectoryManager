using System.Windows;
using System.Windows.Controls;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Controls;

/// <summary>
/// The reusable Active Directory connection form (domain, DCs, credentials). Hosted both by the
/// startup <c>ConnectionWindow</c> and the Settings dialog's On-prem AD tab. The host supplies the
/// action buttons (Connect / Reconnect); this control owns only the fields and the PasswordBox
/// mirroring (a PasswordBox can't be data-bound).
/// </summary>
public partial class ConnectionFormView : UserControl
{
    public ConnectionFormView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is ConnectionViewModel vm) vm.Password = PasswordBox.Password;
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ConnectionViewModel oldVm)
            oldVm.CredentialsLoaded -= OnCredentialsLoaded;
        if (e.NewValue is ConnectionViewModel vm)
        {
            vm.CredentialsLoaded += OnCredentialsLoaded;
            if (!string.IsNullOrEmpty(vm.Password)) PasswordBox.Password = vm.Password;
        }
    }

    private void OnCredentialsLoaded(object? sender, System.EventArgs e)
    {
        if (DataContext is ConnectionViewModel vm && !string.IsNullOrEmpty(vm.Password))
            PasswordBox.Password = vm.Password;
    }
}
