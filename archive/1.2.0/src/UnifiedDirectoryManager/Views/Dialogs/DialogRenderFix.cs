using System.Windows;
using System.Windows.Threading;

namespace UnifiedDirectoryManager.Views.Dialogs;

/// <summary>
/// Works around a WPF rendering glitch seen on some display/GPU configurations (and over RDP) where a
/// modal dialog's content stays blank until the window is invalidated — by hovering a control, resizing,
/// minimizing/maximizing, or occluding then revealing it. After the dialog's first render we briefly nudge
/// the window width by one pixel and restore it, which forces a full re-layout + repaint so every control
/// shows immediately. The nudge is one-time and visually imperceptible on a centered modal.
/// </summary>
internal static class DialogRenderFix
{
    public static void FixLazyRender(this Window window)
    {
        window.ContentRendered += OnContentRendered;

        static void OnContentRendered(object? sender, EventArgs e)
        {
            if (sender is not Window w) return;
            w.ContentRendered -= OnContentRendered; // once is enough
            w.NudgeRender();
        }
    }

    /// <summary>
    /// Forces a re-layout + repaint, for content that can hit the blank-until-invalidated glitch — both the
    /// window's first render and content realized later (e.g. a TabItem's body, built lazily on first selection).
    /// Nudges the size by a pixel and restores it after a short delay. The delay matters: restoring on the very
    /// next dispatcher pass lets WPF coalesce the nudge and the restore into a single layout pass with no net
    /// change (so nothing repaints) — waiting a real frame guarantees the nudged size actually composites first.
    /// When the window sizes to content (Width is NaN) the position is nudged instead. Safe to call repeatedly.
    /// </summary>
    public static void NudgeRender(this Window window)
    {
        if (window.WindowState != WindowState.Normal) return;

        var nudgeWidth = !double.IsNaN(window.Width);
        if (nudgeWidth)
        {
            var width = window.Width;
            window.Width = width + 1;
            RestoreAfterFrame(window, () => window.Width = width);
        }
        else
        {
            var left = window.Left;
            if (double.IsNaN(left)) return;
            window.Left = left + 1;
            RestoreAfterFrame(window, () => window.Left = left);
        }
    }

    private static void RestoreAfterFrame(Window window, Action restore)
    {
        // ~50ms is comfortably more than one frame at 60Hz, so the nudged size is guaranteed to paint before we
        // restore. A DispatcherTimer roots itself while running, so it isn't collected before it fires.
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _2) =>
        {
            timer.Stop();
            if (window.WindowState == WindowState.Normal) restore();
        };
        timer.Start();
    }
}
