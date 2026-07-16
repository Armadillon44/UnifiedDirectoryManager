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
        // Don't close mid-run (the scenario is making real directory changes) — but treat the X as a request to
        // cancel: start cancellation and keep the window open until the run actually stops, then it can be closed.
        if (DataContext is ScenarioProgressViewModel { IsRunning: true } vm)
        {
            e.Cancel = true;
            if (vm.CancelCommand.CanExecute(null)) vm.CancelCommand.Execute(null);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
