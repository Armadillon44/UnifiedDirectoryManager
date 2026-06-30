using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class LogViewerWindow : Window
{
    public LogViewerWindow()
    {
        InitializeComponent();
        LoadNewest();
    }

    /// <summary>Loads the most recently written log file, tolerating the logger holding it open.</summary>
    private void LoadNewest()
    {
        var dir = AppLog.LogDirectory;
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                PathText.Text = "(no log directory)";
                LogText.Text = string.Empty;
                return;
            }

            var file = Directory.EnumerateFiles(dir, "UnifiedDirectoryManager-*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            if (file is null)
            {
                PathText.Text = dir;
                LogText.Text = "(no log files yet)";
                return;
            }

            PathText.Text = file;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            LogText.Text = reader.ReadToEnd();
            LogText.CaretIndex = LogText.Text.Length;
            LogText.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogText.Text = "Could not read log file: " + ex.Message;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadNewest();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppLog.LogDirectory;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
