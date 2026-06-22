using System.Windows;
using System.Windows.Controls;

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
}
