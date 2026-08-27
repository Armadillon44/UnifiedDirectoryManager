using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

/// <summary>
/// One Active Directory object's properties in its own window, hosting the same edit pane the main window
/// uses. Opened from a group's members list, where taking over the pane behind you loses the group you were
/// working on.
/// </summary>
public partial class AdObjectPropertiesWindow : Window
{
    public AdObjectPropertiesWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
