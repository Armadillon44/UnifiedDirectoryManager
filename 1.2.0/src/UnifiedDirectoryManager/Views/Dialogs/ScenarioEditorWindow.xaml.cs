using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class ScenarioEditorWindow : Window
{
    public ScenarioEditorWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
