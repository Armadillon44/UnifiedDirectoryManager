using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Creates a new organizational unit under a chosen parent: a name (required), an optional description, and the
/// "protect from accidental deletion" flag — on by default, matching ADUC's New-OU behavior.
/// </summary>
public partial class NewOuViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private readonly string _parentDn;
    private bool _creating; // guards against a double-submit while the async create is in flight

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _protectFromDeletion = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>The parent container the OU is created under (shown for context).</summary>
    public string ParentDn => _parentDn;

    /// <summary>The new OU's distinguished name once created; null until then.</summary>
    public string? CreatedDistinguishedName { get; private set; }

    /// <summary>Raised after a successful create so the (modal) window can close.</summary>
    public event Action? Created;

    public NewOuViewModel(IDirectoryService directory, IDialogService dialogs, string parentDn)
    {
        _directory = directory;
        _dialogs = dialogs;
        _parentDn = parentDn;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var ouName = Name.Trim();
        if (string.IsNullOrWhiteSpace(ouName)) { Status = "Enter a name for the OU."; return; }
        if (_creating) return;
        _creating = true;
        IsBusy = true;
        Status = "Creating…";
        try
        {
            var (dn, protectionError) = await _directory.CreateOrganizationalUnitAsync(_parentDn, ouName, ProtectFromDeletion, Description);
            CreatedDistinguishedName = dn; // the OU exists; a protection error below is non-fatal
            if (protectionError is not null)
                _dialogs.Alert("OU created — protection not applied",
                    $"“{ouName}” was created, but accidental-deletion protection could not be applied:\n\n{protectionError}\n\n" +
                    "You can set it later from the OU's Properties.");
            Created?.Invoke();
        }
        catch (Exception ex) { Status = "Create failed: " + DirectoryService.Friendly(ex); }
        finally { IsBusy = false; _creating = false; }
    }
}
