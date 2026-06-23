using System.Collections;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Controls;

public partial class EditPaneView : UserControl
{
    public EditPaneView() => InitializeComponent();

    // Click-to-sort for the Member Of and Members lists (per-list direction tracked by GridViewSorter).
    private void OnMemberOfHeaderClick(object sender, RoutedEventArgs e) => GridViewSorter.HandleHeaderClick(sender, e);
    private void OnMembersHeaderClick(object sender, RoutedEventArgs e) => GridViewSorter.HandleHeaderClick(sender, e);

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
