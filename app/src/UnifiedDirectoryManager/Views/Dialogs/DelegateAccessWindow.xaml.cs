using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class DelegateAccessWindow : Window
{
    public DelegateAccessWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
