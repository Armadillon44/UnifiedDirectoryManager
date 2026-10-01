using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>One row in either list on the Toolbar settings page.</summary>
/// <param name="Id">Catalogue id, or <see cref="ToolbarCatalogue.SeparatorId"/>.</param>
/// <param name="Label">What to show. Separators get a drawn-looking label of their own.</param>
/// <param name="Scope">Shown beside on-prem/cloud-only items so their absence is explicable.</param>
public sealed record ToolbarEditorRow(string Id, string Label, ToolbarScope Scope)
{
    public string ScopeNote => Scope switch
    {
        ToolbarScope.OnPremOnly => "on-prem view",
        ToolbarScope.CloudOnly => "cloud view",
        _ => string.Empty,
    };

    public bool HasScopeNote => ScopeNote.Length > 0;
}

/// <summary>
/// Backs the Toolbar page of the Settings dialog (audit T1).
/// </summary>
/// <remarks>
/// <para>
/// Two lists and four buttons, which is the shape Windows has used for this since Office 97, so there is
/// nothing to learn. The catalogue is the only source of what may be added, which is what keeps rule 3
/// true: a command that is not also in the menu bar is never offered here, so no amount of customising
/// can leave a feature with no route to it.
/// </para>
/// <para>
/// Changes are saved when <see cref="Save"/> runs, not as they are made, so Cancel on the dialog leaves
/// the toolbar alone.
/// </para>
/// </remarks>
public partial class ToolbarEditorViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;

    /// <summary>What may be added: the whole catalogue, plus a separator that can be added repeatedly.</summary>
    public ObservableCollection<ToolbarEditorRow> Available { get; } = new();

    /// <summary>What is on the toolbar, in order.</summary>
    public ObservableCollection<ToolbarEditorRow> Chosen { get; } = new();

    [ObservableProperty] private ToolbarEditorRow? _selectedAvailable;
    [ObservableProperty] private ToolbarEditorRow? _selectedChosen;
    [ObservableProperty] private string _status = string.Empty;

    public ToolbarEditorViewModel(ISettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;

        Available.Add(SeparatorRow());
        foreach (var item in ToolbarCatalogue.All)
            Available.Add(new ToolbarEditorRow(item.Id, item.Label, item.Scope));

        Load(ToolbarCatalogue.Normalise(settings.ToolbarItemIds));
    }

    private static ToolbarEditorRow SeparatorRow() =>
        new(ToolbarCatalogue.SeparatorId, "— separator —", ToolbarScope.Both);

    private void Load(IEnumerable<string> ids)
    {
        Chosen.Clear();
        foreach (var id in ids)
        {
            if (id == ToolbarCatalogue.SeparatorId) { Chosen.Add(SeparatorRow()); continue; }
            if (ToolbarCatalogue.Find(id) is { } item)
                Chosen.Add(new ToolbarEditorRow(item.Id, item.Label, item.Scope));
        }
        NotifyAll();
    }

    // --- the four buttons ---------------------------------------------------------------------------

    private bool CanAdd() => SelectedAvailable is not null &&
                             (SelectedAvailable.Id == ToolbarCatalogue.SeparatorId ||
                              Chosen.All(c => c.Id != SelectedAvailable.Id));

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (SelectedAvailable is not { } row) return;
        var at = SelectedChosen is { } sel ? Chosen.IndexOf(sel) + 1 : Chosen.Count;
        Chosen.Insert(at, row.Id == ToolbarCatalogue.SeparatorId ? SeparatorRow() : row);
        SelectedChosen = Chosen[at];
        NotifyAll();
    }

    // The last item cannot be removed: an empty saved list means "use the defaults" (so that an existing
    // settings file needs no migration), which would make emptying the toolbar silently restore it.
    private bool CanRemove() => SelectedChosen is not null && Chosen.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (SelectedChosen is not { } row) return;
        var at = Chosen.IndexOf(row);
        Chosen.RemoveAt(at);
        SelectedChosen = Chosen.Count == 0 ? null : Chosen[Math.Min(at, Chosen.Count - 1)];
        NotifyAll();
    }

    private bool CanMoveUp() => SelectedChosen is not null && Chosen.IndexOf(SelectedChosen) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    private bool CanMoveDown() =>
        SelectedChosen is not null && Chosen.IndexOf(SelectedChosen) is var i && i >= 0 && i < Chosen.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(+1);

    private void Move(int delta)
    {
        if (SelectedChosen is not { } row) return;
        var from = Chosen.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Chosen.Count) return;
        Chosen.Move(from, to);
        SelectedChosen = Chosen[to];
        NotifyAll();
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        Load(ToolbarCatalogue.DefaultIds);
        Status = "Back to the default toolbar. Save to keep it.";
    }

    [RelayCommand]
    private void Save()
    {
        // Normalise here too: the editor permits a layout the renderer would tidy (a trailing separator,
        // say), and storing what will actually be shown keeps the page honest when it is reopened.
        _settings.ToolbarItemIds = ToolbarCatalogue.Normalise(Chosen.Select(c => c.Id)).ToList();
        _settingsStore.Save(_settings);
        Load(_settings.ToolbarItemIds);
        Status = $"Saved. {Chosen.Count(c => c.Id != ToolbarCatalogue.SeparatorId)} button(s) on the toolbar.";
    }

    partial void OnSelectedAvailableChanged(ToolbarEditorRow? value) => NotifyAll();
    partial void OnSelectedChosenChanged(ToolbarEditorRow? value) => NotifyAll();

    private void NotifyAll()
    {
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
