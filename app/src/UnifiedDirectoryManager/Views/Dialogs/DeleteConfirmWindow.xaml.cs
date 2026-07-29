using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

/// <summary>
/// Delete confirmation that can additionally offer "save a record first" and/or require a typed phrase. The
/// generic <see cref="ConfirmWindow"/> has no checkbox and <see cref="ConfirmPhraseWindow"/> always demands a
/// phrase, so this covers the group-delete case where both are conditional.
/// </summary>
public partial class DeleteConfirmWindow : Window
{
    private readonly string? _requiredPhrase;

    public DeleteConfirmWindow(string title, string heading, IEnumerable<string> lines,
                               string? requiredPhrase, bool offerRecord, bool recordDefault)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = heading;
        Lines.ItemsSource = lines.ToList();
        _requiredPhrase = requiredPhrase;

        RecordCheck.Visibility = offerRecord ? Visibility.Visible : Visibility.Collapsed;
        RecordCheck.IsChecked = offerRecord && recordDefault;

        if (string.IsNullOrEmpty(requiredPhrase))
        {
            PhrasePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            Prompt.Text = $"Type “{requiredPhrase}” to confirm:";
            OkButton.IsEnabled = false; // enabled once the typed phrase matches exactly
        }
        this.FixLazyRender();
    }

    /// <summary>True when the operator asked for a record to be written before deleting.</summary>
    public bool SaveRecord => RecordCheck.IsChecked == true;

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        OkButton.IsEnabled = string.Equals(Input.Text.Trim(), _requiredPhrase, StringComparison.Ordinal);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled) return;
        DialogResult = true;
        Close();
    }
}
