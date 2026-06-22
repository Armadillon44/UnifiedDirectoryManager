using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class NewUserWindow : Window
{
    private NewUserViewModel? _vm;

    public NewUserWindow()
    {
        InitializeComponent();
        PasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is NewUserViewModel vm) vm.Password = PasswordBox.Password;
        };
        SyncPasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is NewUserViewModel vm) vm.SyncPassword = SyncPasswordBox.Password;
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PasswordGenerated -= OnPasswordGenerated;
            _vm = DataContext as NewUserViewModel;
            if (_vm is not null) _vm.PasswordGenerated += OnPasswordGenerated;
        };
        // Non-modal: pick up template changes made in the (also non-modal) template editor. Skipped in
        // batch-capture mode — a reload reassigns SelectedTemplate and would reset the seeded OU/groups
        // each time the modal capture window reactivates (e.g. after a group/manager picker closes).
        Activated += (_, _) => { if (_vm is { IsBatchCapture: false }) _vm.ReloadTemplates(); };
    }

    private void OnPasswordGenerated(string password) => PasswordBox.Password = password;

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NewUserViewModel vm) return;
        // Do NOT auto-close on success — the window stays open so the admin can read the outcome
        // (created DN, password status, sync progress, cloud-group results). They close it via Close.
        await vm.CreateCommand.ExecuteAsync(null);
    }

    private async void OnRetrySync(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NewUserViewModel vm) return;
        await vm.RetrySyncCommand.ExecuteAsync(null);
    }

    private void OnAddToBatch(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NewUserViewModel vm) return;
        if (!vm.ValidateForCapture(out var error)) { vm.Status = error; return; }
        DialogResult = true; // closes the modal capture dialog; the host reads the configured values back
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCopyPassword(object sender, RoutedEventArgs e)
    {
        if (DataContext is NewUserViewModel { GeneratedPassword.Length: > 0 } vm)
        {
            // Excluded from Clipboard History / Cloud Clipboard — this is a freshly set password.
            try { SensitiveClipboard.SetText(vm.GeneratedPassword); } catch { /* clipboard can transiently fail */ }
        }
    }

    private void OnCopyTap(object sender, RoutedEventArgs e)
    {
        if (DataContext is NewUserViewModel { TapCode.Length: > 0 } vm)
        {
            // The TAP is a credential — keep it out of Clipboard History / Cloud Clipboard, like the password.
            try { SensitiveClipboard.SetText(vm.TapCode); } catch { /* clipboard can transiently fail */ }
        }
    }
}
