using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string heading, IEnumerable<string> lines)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = heading;
        Lines.ItemsSource = lines.ToList();
        this.FixLazyRender();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
