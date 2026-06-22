using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class ResetPasswordWindow : Window
{
    private ResetPasswordViewModel? _vm;

    public ResetPasswordWindow()
    {
        InitializeComponent();
        // PasswordBox.Password isn't bindable; push edits into the view model.
        PasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is ResetPasswordViewModel vm) vm.Password = PasswordBox.Password;
        };
        ConfirmBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is ResetPasswordViewModel vm) vm.ConfirmPassword = ConfirmBox.Password;
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PasswordGenerated -= OnPasswordGenerated;
            _vm = DataContext as ResetPasswordViewModel;
            if (_vm is not null) _vm.PasswordGenerated += OnPasswordGenerated;
        };
    }

    private void OnPasswordGenerated(string password)
    {
        PasswordBox.Password = password;
        ConfirmBox.Password = password;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ResetPasswordViewModel vm || !vm.Commit()) return;
        DialogResult = true;
        Close();
    }

    private void OnCopyPassword(object sender, RoutedEventArgs e)
    {
        if (DataContext is ResetPasswordViewModel { GeneratedPassword.Length: > 0 } vm)
        {
            // Excluded from Clipboard History / Cloud Clipboard — this is a freshly set password.
            try { SensitiveClipboard.SetText(vm.GeneratedPassword); } catch { /* clipboard can transiently fail */ }
        }
    }
}
