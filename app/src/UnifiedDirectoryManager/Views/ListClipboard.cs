using System.Collections;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.Views;

/// <summary>
/// Ctrl+C on any list in the app copies it as tab-separated text, with a header row.
/// </summary>
/// <remarks>
/// <para>
/// One class-level command binding on <see cref="ListView"/> rather than a handler per list. There are
/// about twenty lists of users and groups — members, Member Of, direct reports, cloud memberships, every
/// picker's results — and wiring each one separately is how some of them would end up without it, and how
/// the next list added would too.
/// </para>
/// <para>
/// Tab-separated because that is what pastes as a table into Excel, Teams and Outlook, which is where a
/// list of members is usually going. The columns are whatever the list shows, in the order it shows them,
/// so the copy matches the screen.
/// </para>
/// <para>
/// A list that handles Ctrl+C itself (the on-prem Member Of tab) keeps doing so: its own key handler marks
/// the key handled before the command is raised. A text box inside a list keeps copying its own text,
/// because the command routes from the focused element and the text box answers first.
/// </para>
/// </remarks>
public static class ListClipboard
{
    /// <summary>Installs the binding for every ListView in the application. Call once, at startup.</summary>
    public static void Register() =>
        CommandManager.RegisterClassCommandBinding(typeof(ListView),
            new CommandBinding(ApplicationCommands.Copy, OnCopy, OnCanCopy));

    private static void OnCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        if (sender is ListView { View: GridView } list && list.Items.Count > 0)
        {
            e.CanExecute = true;
            e.Handled = true;
        }
    }

    private static void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not ListView list) return;
        var text = BuildText(list, RowsToCopy(list));
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { AppLog.Instance.Warn("Could not copy the list to the clipboard: " + ex.Message); }
        e.Handled = true;
    }

    /// <summary>
    /// The rows Ctrl+C copies: the selection, or the whole list when nothing is selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the order the list SHOWS them, not the order they were selected — SelectedItems is in click
    /// order, and a copy that reshuffles the rows reads as wrong.
    /// </para>
    /// <para>
    /// A single-selection list always copies everything. Its highlighted row is where you are, not a
    /// choice of what to copy — clicking a row is how you look at it — so treating it as "copy one row"
    /// would make it impossible to copy those lists whole. The cloud object lists are single-selection
    /// and use their checkboxes for picking rows.
    /// </para>
    /// <para>
    /// Items, not the source collection, so a filtered or sorted list copies what is on screen.
    /// </para>
    /// </remarks>
    internal static IList RowsToCopy(ListView list)
    {
        if (list.SelectionMode == SelectionMode.Single || list.SelectedItems.Count == 0)
            return list.Items.Cast<object>().ToList();

        var selected = new HashSet<object>(list.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);
        return list.Items.Cast<object>().Where(selected.Contains).ToList();
    }

    /// <summary>
    /// The rows as tab-separated text: a header row, then one line per row.
    /// </summary>
    /// <remarks>
    /// A column is included when it has a text header and something readable — a display binding, or a
    /// cell template whose text comes from a binding. Checkbox and button columns are left out: a column of
    /// "True"/"False" or of "Remove" is noise in a pasted list. Each value is read through the column's own
    /// binding, so string formats and converters apply and the copy says what the screen says.
    /// </remarks>
    internal static string BuildText(ListView list, IEnumerable rows)
    {
        if (list.View is not GridView grid) return string.Empty;

        var columns = new List<(string Header, BindingBase Binding)>();
        foreach (var column in grid.Columns)
        {
            if (column.Header is not string header || string.IsNullOrWhiteSpace(header)) continue;
            var binding = column.DisplayMemberBinding ?? TextBindingOf(column.CellTemplate);
            if (binding is null) continue;
            columns.Add((header.Trim(), binding));
        }
        if (columns.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', columns.Select(c => Clean(c.Header))));

        // One detached TextBlock evaluates every cell. Detached is fine: the bindings here read the row
        // through DataContext, which is set directly.
        var probe = new TextBlock();
        foreach (var row in rows)
        {
            probe.DataContext = row;
            var cells = new List<string>(columns.Count);
            foreach (var (_, binding) in columns)
            {
                BindingOperations.SetBinding(probe, TextBlock.TextProperty, binding);
                cells.Add(Clean(probe.Text));
                BindingOperations.ClearBinding(probe, TextBlock.TextProperty);
            }
            sb.AppendLine(string.Join('\t', cells));
        }
        return sb.ToString();
    }

    /// <summary>The binding behind a cell template's text, or null when the template shows no bound text.</summary>
    private static BindingBase? TextBindingOf(DataTemplate? template)
    {
        if (template is null) return null;
        try
        {
            if (template.LoadContent() is not DependencyObject root) return null;
            foreach (var element in SelfAndDescendants(root))
            {
                var binding = element switch
                {
                    TextBlock t => BindingOperations.GetBindingBase(t, TextBlock.TextProperty),
                    TextBox t => BindingOperations.GetBindingBase(t, TextBox.TextProperty),
                    _ => null,
                };
                if (binding is not null) return binding;
            }
        }
        catch { /* a template that cannot be instantiated here is a column we leave out */ }
        return null;
    }

    private static IEnumerable<DependencyObject> SelfAndDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in SelfAndDescendants(child))
                yield return descendant;
    }

    /// <summary>A tab or line break inside a value would split the row; flatten them to spaces.</summary>
    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
}
