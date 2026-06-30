using System.Windows;
using System.Windows.Controls;

namespace UnifiedDirectoryManager.Views.Controls;

public partial class EditPaneView : UserControl
{
    public EditPaneView() => InitializeComponent();

    // Click-to-sort for the Member Of and Members lists (per-list direction tracked by GridViewSorter).
    private void OnMemberOfHeaderClick(object sender, RoutedEventArgs e) => GridViewSorter.HandleHeaderClick(sender, e);
    private void OnMembersHeaderClick(object sender, RoutedEventArgs e) => GridViewSorter.HandleHeaderClick(sender, e);
}
