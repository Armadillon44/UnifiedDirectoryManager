using System.Collections;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Controls;

public partial class EditPaneView : UserControl
{
    public EditPaneView() => InitializeComponent();

    // Click-to-sort for the Member Of and Members lists (per-list direction tracked by GridViewSorter).
    private void OnMemberOfHeaderClick(object sender, RoutedEventArgs e) => GridViewSorter.HandleHeaderClick(sender, e);
    private void OnMembersHeaderClick(object sender, RoutedEventArgs e) => GridViewSorter.HandleHeaderClick(sender, e);

    /// <summary>
    /// Double-clicking a member opens it in this pane. Only a ROW counts: a double-click on a column header
    /// or on the blank space under the last row is not a request to open whatever is still selected.
    /// </summary>
    private void OnMemberDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not EditPaneViewModel vm) return;
        if (e.OriginalSource is not DependencyObject source) return;
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ListViewItem item) continue;
            if (item.DataContext is GroupMemberRow member) vm.OpenMemberCommand.Execute(member);
            return;
        }
    }

    // --- Copy the Member Of groups to the clipboard (Ctrl+C, the Copy button, or right-click ▸ Copy) as
    //     tab-separated text with a header row, so it pastes cleanly into a text file or an Excel sheet. ---
    private void OnMemberOfKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyGroups(MemberOfList.SelectedItems.Count > 0 ? MemberOfList.SelectedItems : MemberOfList.Items);
            e.Handled = true;
        }
    }

    // Copy button / "Copy selected": the selection, or everything when nothing is selected.
    private void OnCopyMemberOf(object sender, RoutedEventArgs e) =>
        CopyGroups(MemberOfList.SelectedItems.Count > 0 ? MemberOfList.SelectedItems : MemberOfList.Items);

    private void OnCopyAllMemberOf(object sender, RoutedEventArgs e) => CopyGroups(MemberOfList.Items);

    private static void CopyGroups(IList? items)
    {
        if (items is null || items.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("Group\tType\tSource");
        foreach (var item in items)
            if (item is GroupMembership g)
                sb.AppendLine($"{g.Name}\t{g.Kind}\t{g.Source}");
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard can transiently fail; nothing actionable */ }
    }
}
