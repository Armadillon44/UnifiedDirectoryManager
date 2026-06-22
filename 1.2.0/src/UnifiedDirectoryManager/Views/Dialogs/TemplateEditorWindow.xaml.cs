using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class TemplateEditorWindow : Window
{
    public TemplateEditorWindow() => InitializeComponent();

    private void OnClose(object sender, System.Windows.RoutedEventArgs e) => Close();
}
