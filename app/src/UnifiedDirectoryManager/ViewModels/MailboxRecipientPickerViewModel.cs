using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Single-select searchable picker for an internal Exchange recipient (user, shared mailbox, or distribution
/// group) to forward a mailbox to. Backed by <see cref="IExchangeService.SearchRecipientsAsync"/>.
/// </summary>
public partial class MailboxRecipientPickerViewModel : ObservableObject
{
    private readonly IExchangeService _exchange;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Search for the recipient to forward to (user, shared mailbox, or distribution group).";
    [ObservableProperty] private MailboxRecipient? _selectedResult;

    public ObservableCollection<MailboxRecipient> Results { get; } = new();

    /// <summary>The chosen recipient, set on OK.</summary>
    public MailboxRecipient? Picked { get; private set; }

    public MailboxRecipientPickerViewModel(IExchangeService exchange) => _exchange = exchange;

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
        catch (Exception ex) { Status = "Search failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public bool Commit()
    {
        Picked = SelectedResult;
        return Picked is not null;
    }
}
