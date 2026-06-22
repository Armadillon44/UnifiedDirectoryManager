using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class CopyUserWindow : Window
{
    private CopyUserViewModel? _vm;

    public CopyUserWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
        PasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is CopyUserViewModel vm) vm.Password = PasswordBox.Password;
        };
        SyncPasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is CopyUserViewModel vm) vm.SyncPassword = SyncPasswordBox.Password;
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PasswordGenerated -= OnPasswordGenerated;
            _vm = DataContext as CopyUserViewModel;
            if (_vm is not null) _vm.PasswordGenerated += OnPasswordGenerated;
        };
        Loaded += async (_, _) =>
        {
            if (DataContext is CopyUserViewModel vm) await vm.LoadAsync();
        };
    }

    private void OnPasswordGenerated(string password) => PasswordBox.Password = password;

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CopyUserViewModel vm) return;
        // Stay open after creating so the operator can read the outcome / create another.
        await vm.CreateCommand.ExecuteAsync(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCopyPassword(object sender, RoutedEventArgs e)
    {
        if (DataContext is CopyUserViewModel { GeneratedPassword.Length: > 0 } vm)
        {
            try { SensitiveClipboard.SetText(vm.GeneratedPassword); } catch { /* clipboard can transiently fail */ }
        }
    }

    private void OnCopyTap(object sender, RoutedEventArgs e)
    {
        if (DataContext is CopyUserViewModel { TapCode.Length: > 0 } vm)
        {
            // The TAP is a credential — keep it out of Clipboard History / Cloud Clipboard, like the password.
            try { SensitiveClipboard.SetText(vm.TapCode); } catch { /* clipboard can transiently fail */ }
        }
    }
}
