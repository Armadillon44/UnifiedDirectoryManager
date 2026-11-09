using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views;

/// <summary>
/// Stops a window closing over unsaved edits without asking: save, discard, or stay.
/// </summary>
/// <remarks>
/// <para>
/// Closing cancels first and asks afterwards, because the answer may be Save — an asynchronous write that the
/// Closing event cannot wait for. Once the prompt is answered the window closes itself again, and that second
/// close goes through.
/// </para>
/// <para>
/// It yields before prompting so the dialog opens after the Closing event has returned, not inside it.
/// </para>
/// </remarks>
public static class UnsavedChangesGuard
{
    /// <summary>For a window whose DataContext is one edit pane: the object editor windows.</summary>
    public static void Attach(Window window) =>
        AttachFor(window, () => window.DataContext is EditPaneViewModel vm ? new[] { vm } : Array.Empty<EditPaneViewModel>());

    /// <summary>
    /// For a window that must account for several edit panes — the main window, whose closing ends the app and
    /// takes every open editor window with it.
    /// </summary>
    public static void AttachFor(Window window, Func<IEnumerable<EditPaneViewModel>> panes)
    {
        var confirmed = false;
        window.Closing += async (_, e) =>
        {
            if (confirmed) return;
            var unsaved = panes().Where(p => p.HasUnsavedChanges()).ToList();
            if (unsaved.Count == 0) return;

            e.Cancel = true;
            await Task.Yield();
            foreach (var pane in unsaved)
                if (!await pane.ConfirmLeaveAsync()) return; // the operator chose to stay

            confirmed = true;
            window.Close();
        };
    }

    /// <summary>The edit panes in every open object editor window, besides <paramref name="except"/>.</summary>
    public static IEnumerable<EditPaneViewModel> EditorPanes(Window except) =>
        Application.Current?.Windows.OfType<Window>()
            .Where(w => !ReferenceEquals(w, except))
            .Select(w => w.DataContext)
            .OfType<EditPaneViewModel>()
        ?? Enumerable.Empty<EditPaneViewModel>();
}
