using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class ConfirmPhraseWindow : Window
{
    private readonly string _requiredPhrase;

    public ConfirmPhraseWindow(string title, string heading, IEnumerable<string> lines, string requiredPhrase)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = heading;
        Lines.ItemsSource = lines.ToList();
        _requiredPhrase = requiredPhrase;
        Prompt.Text = $"Type “{requiredPhrase}” to confirm:";
        this.FixLazyRender();
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        OkButton.IsEnabled = string.Equals(Input.Text.Trim(), _requiredPhrase, StringComparison.Ordinal);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled) return;
        DialogResult = true;
        Close();
    }
}
