using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Searchable picker for internal Exchange recipients (users, shared mailboxes, distribution groups), backed by
/// <see cref="IExchangeService.SearchRecipientsAsync"/>.
///
/// Two modes from one view model, matching <see cref="ObjectPickerViewModel"/>: single-select for "forward this
/// mailbox to…", and multi-select with a basket for "add these members…". The basket matters because the search
/// is server-side and returns one page — without somewhere to put results, choosing people who don't share a
/// search term means one dialog trip each.
/// </summary>
public partial class MailboxRecipientPickerViewModel : ObservableObject
{
    private readonly IExchangeService _exchange;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Search for a recipient (user, shared mailbox, or distribution group).";
    [ObservableProperty] private MailboxRecipient? _selectedResult;

    public ObservableCollection<MailboxRecipient> Results { get; } = new();

    /// <summary>Chosen recipients, held across searches. Empty and unused in single-select mode.</summary>
    public ObservableCollection<MailboxRecipient> Basket { get; } = new();

    /// <summary>True when the dialog collects several recipients rather than one.</summary>
    public bool MultiSelect { get; }

    /// <summary>
    /// True when the basket was seeded, which makes emptying it a deliberate instruction to clear the list
    /// rather than a person who has not chosen anybody yet. Without this the six recipient settings declare
    /// themselves clearable and offer no way to clear them.
    /// </summary>
    public bool AllowEmpty { get; }

    /// <summary>Drives the results list's selection mode.</summary>
    public string ResultsSelectionMode => MultiSelect ? "Extended" : "Single";

    /// <summary>
    /// The instruction at the top of the dialog. Mode-aware because the two modes need opposite actions —
    /// telling a multi-select operator to "select one and click OK" is an instruction to add nobody.
    /// </summary>
    public string Heading => MultiSelect
        ? "Search for recipients — users, shared mailboxes, or distribution groups — then Add them to the list on the right and click OK."
        : "Search for a recipient — a user, shared mailbox, or distribution group — then select one and click OK.";

    /// <summary>The final selection, set on OK. A list in both modes; the single-select caller takes the first.</summary>
    public List<MailboxRecipient> Picked { get; } = new();

    /// <param name="initial">Seeds the basket. Editing an existing list means starting from what is already
    /// there — an empty basket would make every edit a replacement typed from memory.</param>
    public MailboxRecipientPickerViewModel(IExchangeService exchange, bool multiSelect = false,
                                          IEnumerable<MailboxRecipient>? initial = null)
    {
        _exchange = exchange;
        MultiSelect = multiSelect;
        AllowEmpty = multiSelect && initial is not null;
        if (multiSelect)
        {
            if (initial is not null)
                foreach (var r in initial) Basket.Add(r);
            Status = Basket.Count > 0
                ? $"{Basket.Count} already in the list. Add or remove, then click OK."
                : "Search for recipients, add the ones you want, then click OK.";
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsBusy = true;
        Status = "Searching…";
        Results.Clear();
        try
        {
            var recipients = await _exchange.SearchRecipientsAsync(SearchText);
            foreach (var r in recipients) Results.Add(r);
            Status = Results.Count == 0 ? "No matching recipients." : $"{Results.Count} recipient(s).";
        }
        catch (Exception ex) { Status = "Search failed: " + ExchangeErrors.Friendly(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void AddToBasket(System.Collections.IList? selected)
    {
        var rows = selected?.Cast<MailboxRecipient>().ToList() ?? new List<MailboxRecipient>();
        if (rows.Count == 0 && SelectedResult is not null) rows.Add(SelectedResult);

        // "Nothing selected" and "everything selected was a duplicate" are different answers, and conflating
        // them tells the operator their search hits are already members when nothing was even highlighted.
        if (rows.Count == 0)
        {
            Status = "Select one or more recipients in the results list first.";
            return;
        }

        var added = 0;
        foreach (var r in rows)
        {
            if (Basket.Any(b => SameRecipient(b, r))) continue;
            Basket.Add(r);
            added++;
        }
        Status = added == 0
            ? "Nothing added — those recipients are already in the list."
            : $"{Basket.Count} selected.";
    }

    [RelayCommand]
    private void RemoveFromBasket(MailboxRecipient? row)
    {
        if (row is null) return;
        Basket.Remove(row);
        Status = $"{Basket.Count} selected.";
    }

    /// <summary>Matches on the identity Exchange is actually addressed by, so the same person found through two
    /// different searches can't be added twice.</summary>
    private static bool SameRecipient(MailboxRecipient a, MailboxRecipient b) =>
        string.Equals(a.Identity, b.Identity, StringComparison.OrdinalIgnoreCase);

    public bool Commit()
    {
        Picked.Clear();
        if (MultiSelect) Picked.AddRange(Basket);
        else if (SelectedResult is not null) Picked.Add(SelectedResult);
        // An emptied seeded basket is an answer, not an absence of one.
        return Picked.Count > 0 || AllowEmpty;
    }
}
