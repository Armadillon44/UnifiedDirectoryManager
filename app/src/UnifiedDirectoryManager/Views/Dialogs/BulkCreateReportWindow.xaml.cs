using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class BulkCreateReportWindow : Window
{
    public BulkCreateReportWindow()
    {
        InitializeComponent();
    }

    private void OnCopyPassword(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is BulkCreateUserResult { GeneratedPassword.Length: > 0 } r)
            try { SensitiveClipboard.SetText(r.GeneratedPassword); } catch { /* clipboard can transiently fail */ }
    }

    private void OnCopyTap(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is BulkCreateUserResult { TapCode.Length: > 0 } r)
            try { SensitiveClipboard.SetText(r.TapCode!); } catch { /* clipboard can transiently fail */ }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BulkCreateReportViewModel vm) return;

        var warn = MessageBox.Show(this,
            "The exported CSV will contain the plaintext passwords (and any Temporary Access Passes). " +
            "Store it securely and delete it once the credentials have been handed out.\n\nExport anyway?",
            "Export contains secrets", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (warn != MessageBoxResult.OK) return;

        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = "bulk-create-results.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        try { File.WriteAllText(dialog.FileName, vm.BuildCsv()); }
        catch (System.Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
