using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UnifiedDirectoryManager.ViewModels;
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

    /// <summary>
    /// Hands a list's live selection to a command. These three buttons used an ElementName binding to the
    /// list beside them, which does NOT resolve here: the tabs they live on are items of a
    /// CompositeCollection, so the parser's names are not reachable the way they are from an ordinary child.
    /// The CommandParameter arrived null and every one of them reported "select one or more" no matter what
    /// was selected.
    ///
    /// The list is found by walking out from the button rather than by name, so it does not depend on a
    /// namescope at all — the same shape the distribution-group members dialog already uses.
    /// </summary>
    private void RunWithSelection(object sender, Func<CloudObjectDetailViewModel, System.Windows.Input.ICommand> pick)
    {
        if (DataContext is not CloudObjectDetailViewModel vm) return;
        if (FindList(sender as DependencyObject) is not { } list) return;

        var command = pick(vm);
        if (command.CanExecute(list.SelectedItems)) command.Execute(list.SelectedItems);
    }

    /// <summary>Finds the list this button belongs to: up to the panel that holds both, then down to the list.</summary>
    private static ListView? FindList(DependencyObject? from)
    {
        for (var node = from; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Panel or ContentPresenter && Descend(node) is { } found)
                return found;
        return null;

        static ListView? Descend(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ListView list) return list;
                if (Descend(child) is { } deeper) return deeper;
            }
            return null;
        }
    }

    private void OnRemoveSelectedMembers(object sender, RoutedEventArgs e) =>
        RunWithSelection(sender, vm => vm.RemoveMembersCommand);

    private void OnRemoveSelectedLicenses(object sender, RoutedEventArgs e) =>
        RunWithSelection(sender, vm => vm.RemoveLicensesCommand);

    private void OnRemoveSelectedMemberships(object sender, RoutedEventArgs e) =>
        RunWithSelection(sender, vm => vm.RemoveFromGroupsCommand);

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
