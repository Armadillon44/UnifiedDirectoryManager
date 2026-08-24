using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedDirectoryManager.Views.Controls;

/// <summary>Read-only properties view for a cloud object; hosted in the cloud pane and the properties window.</summary>
public partial class CloudDetailView : UserControl
{
    public CloudDetailView()
    {
        InitializeComponent();
    }

    // Click-to-sort for the cloud group's Members list.
    private void OnMembersHeaderClick(object sender, RoutedEventArgs e) => GridViewSorter.HandleHeaderClick(sender, e);

    /// <summary>The "?" this control opened by hand, so the next one can close it.</summary>
    private ToolTip? _openHelp;

    /// <summary>
    /// Opens a property's "?" tooltip on click. WPF shows a tooltip on hover only, which leaves the glyph
    /// looking clickable and doing nothing, and leaves a touch user no way to read it at all.
    ///
    /// A tooltip opened this way is NOT owned by WPF's tooltip service, so nothing else will ever close it:
    /// not moving away, not switching tabs, not tearing the rows down. StaysOpen=false makes the popup take
    /// the mouse, so the next click anywhere dismisses it, and the previous one is closed explicitly rather
    /// than left stacked behind the new one.
    /// </summary>
    private void OnHelpGlyphClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { ToolTip: ToolTip tip }) return;

        if (_openHelp is { } previous && !ReferenceEquals(previous, tip)) previous.IsOpen = false;
        tip.PlacementTarget = (UIElement)sender;
        tip.StaysOpen = false;
        tip.IsOpen = true;
        _openHelp = tip;
        e.Handled = true;
    }
}
