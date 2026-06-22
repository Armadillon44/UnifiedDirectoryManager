using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace UnifiedDirectoryManager.Views.Controls;

/// <summary>
/// Click-to-sort for GridView column headers. Attach <c>GridViewColumnHeader.Click="…"</c> on a ListView and
/// call <see cref="HandleHeaderClick"/> from the handler: the list is sorted by the clicked column's bound
/// property, toggling ascending/descending on repeat clicks. Sort state is tracked per-ListView, so several
/// independently-sortable lists can share the same handler. Works for any GridView column whose value comes
/// from a <c>DisplayMemberBinding</c>.
/// </summary>
internal static class GridViewSorter
{
    private sealed class SortState { public string? Path; public ListSortDirection Direction; }

    private static readonly ConditionalWeakTable<ListView, SortState> _state = new();

    public static void HandleHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || sender is not ListView list) return;
        if ((column.DisplayMemberBinding as Binding)?.Path?.Path is not { Length: > 0 } path) return;

        var state = _state.GetOrCreateValue(list);
        // Toggle direction when re-clicking the same column; otherwise start ascending on the new column.
        state.Direction = path == state.Path && state.Direction == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        state.Path = path;

        var view = list.Items;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(path, state.Direction));
        view.Refresh();
    }
}
