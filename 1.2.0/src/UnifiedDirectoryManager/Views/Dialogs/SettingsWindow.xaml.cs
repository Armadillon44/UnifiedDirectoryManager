using System.Windows;
using UnifiedDirectoryManager.Services;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // A TabItem's body is realized lazily on first selection and can hit the blank-until-invalidated glitch
    // (the one-time FixLazyRender nudge already fired for the first tab). Re-nudge when the tab actually changes.
    // (SelectionChanged bubbles from inner selectors too, so only act when it's the TabControl's own change.)
    private void OnTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TabControl)
            this.NudgeRender();
    }

    private void OnSyncPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is System.Windows.Controls.PasswordBox box)
            vm.SyncPassword = box.Password;
    }

    private void OnBrowseLogFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var initial = string.IsNullOrWhiteSpace(vm.OperationLogDirectory)
            ? OperationLog.ResolveDirectory(new AppSettings()) // the default folder
            : vm.OperationLogDirectory;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the operation-log folder", InitialDirectory = initial };
        if (dlg.ShowDialog(this) == true)
            vm.OperationLogDirectory = dlg.FolderName;
    }
}
