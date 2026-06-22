using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class CloudObjectPropertiesWindow : Window
{
    public CloudObjectPropertiesWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
