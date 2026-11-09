using System.Windows;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.Views.Dialogs;

/// <summary>
/// Save / Discard / Cancel for changes that are about to be lost. Closing the window any other way — Esc,
/// the title-bar X — counts as Cancel, so nothing is thrown away unless Discard was chosen deliberately.
/// </summary>
public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow(string title, string heading, IEnumerable<string> lines)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = heading;
        Lines.ItemsSource = lines.ToList();
        this.FixLazyRender();
    }

    /// <summary>What the operator chose. Cancel unless Save or Discard was clicked.</summary>
    public UnsavedChoice Choice { get; private set; } = UnsavedChoice.Cancel;

    private void OnSave(object sender, RoutedEventArgs e) { Choice = UnsavedChoice.Save; DialogResult = true; }

    private void OnDiscard(object sender, RoutedEventArgs e) { Choice = UnsavedChoice.Discard; DialogResult = true; }
}
