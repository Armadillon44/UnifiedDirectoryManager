using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class DeletedObjectsWindow : Window
{
    public DeletedObjectsWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
