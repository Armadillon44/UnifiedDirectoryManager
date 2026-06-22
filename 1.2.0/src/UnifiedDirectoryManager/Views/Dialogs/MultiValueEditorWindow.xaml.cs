using System.Collections.ObjectModel;
using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class MultiValueEditorWindow : Window
{
    private readonly ObservableCollection<string> _values = new();

    public MultiValueEditorWindow(string friendlyName, IEnumerable<string> values)
    {
        InitializeComponent();
        Heading.Text = $"Values for “{friendlyName}”";
        foreach (var v in values) _values.Add(v);
        ValuesList.ItemsSource = _values;
    }

    /// <summary>The edited values (valid after the dialog returns true).</summary>
    public IReadOnlyList<string> Values => _values.ToList();

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var value = NewValue.Text.Trim();
        if (value.Length > 0 && !_values.Contains(value))
        {
            _values.Add(value);
            NewValue.Clear();
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (ValuesList.SelectedItem is string s) _values.Remove(s);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
