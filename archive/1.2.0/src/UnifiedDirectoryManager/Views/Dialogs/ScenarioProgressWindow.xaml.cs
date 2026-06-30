using System.ComponentModel;
using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

/// <summary>
/// Modal live-progress window for a scenario run: shows each step as it happens and only enables Close
/// (and allows the window to be closed) once the run completes.
/// </summary>
public partial class ScenarioProgressWindow : Window
{
    public ScenarioProgressWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioProgressViewModel vm) return;
        vm.LineAdded += ScrollToEnd;
        await vm.RunAsync();
    }

    private void ScrollToEnd() => LogScroller.ScrollToEnd();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Don't let the operator close mid-run — the scenario is making real directory changes.
        if (DataContext is ScenarioProgressViewModel { IsRunning: true }) e.Cancel = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
