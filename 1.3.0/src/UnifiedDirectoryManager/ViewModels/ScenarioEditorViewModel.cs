using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>One editable step row in the scenario editor. Only the fields relevant to the chosen
/// <see cref="Action"/> are shown (driven by the Show* flags) and saved (see <see cref="ToStep"/>).</summary>
public partial class ScenarioStepRow : ObservableObject
{
    private readonly IDialogService _dialogs;

    public ScenarioStepRow(IDialogService dialogs, ScenarioStep? step = null)
    {
        _dialogs = dialogs;
        if (step is null) return;

        _action = step.Action;
        _attribute = string.IsNullOrWhiteSpace(step.Attribute) ? "description" : step.Attribute;
        _value = step.Value;
        _targetOu = step.TargetOu;
        foreach (var dn in step.GroupDns)
            Groups.Add(new AdObjectRow { DistinguishedName = dn, Name = NameResolver.RdnFallback(dn), Type = AdObjectType.Group });
        foreach (var g in step.CloudGroups)
            CloudGroups.Add(new CloudGroupRef { Id = g.Id, Name = g.Name });
    }

    [ObservableProperty] private ScenarioActionType _action = ScenarioActionType.Disable;
    [ObservableProperty] private string _attribute = "description";
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _targetOu = string.Empty;

    /// <summary>Groups for Add/Remove-from-groups steps (display rows; DNs are what get saved).</summary>
    public ObservableCollection<AdObjectRow> Groups { get; } = new();

    /// <summary>Entra ID groups for Cloud Add/Remove-from-groups steps.</summary>
    public ObservableCollection<CloudGroupRef> CloudGroups { get; } = new();

    public IReadOnlyList<ScenarioActionType> AllActions { get; } = Enum.GetValues<ScenarioActionType>();

    // Include DN-valued attributes (e.g. manager) — clearing/setting them is valid in a scenario,
    // and "clear manager" is a common termination step. Only read-only attributes are excluded.
    public IReadOnlyList<AttributeMeta> Attributes { get; } =
        AttributeCatalog.All.Where(a => !a.IsReadOnly)
                            .OrderBy(a => a.Friendly, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool ShowAttribute => Action is ScenarioActionType.SetAttribute or ScenarioActionType.ClearAttribute;
    public bool ShowValue => Action is ScenarioActionType.SetAttribute or ScenarioActionType.SetDescription;
    public bool ShowGroups => Action is ScenarioActionType.AddToGroups or ScenarioActionType.RemoveFromGroups;
    public bool ShowCloudGroups => Action is ScenarioActionType.CloudAddToGroups or ScenarioActionType.CloudRemoveFromGroups;
    public bool ShowOu => Action is ScenarioActionType.MoveToOu;
    public bool ShowLogNote => Action is ScenarioActionType.SaveOperationLog;

    partial void OnActionChanged(ScenarioActionType value)
    {
        OnPropertyChanged(nameof(ShowAttribute));
        OnPropertyChanged(nameof(ShowValue));
        OnPropertyChanged(nameof(ShowGroups));
        OnPropertyChanged(nameof(ShowCloudGroups));
        OnPropertyChanged(nameof(ShowOu));
        OnPropertyChanged(nameof(ShowLogNote));
    }

    [RelayCommand]
    private void PickGroups()
    {
        var verb = Action == ScenarioActionType.RemoveFromGroups ? "remove from" : "add to";
        var picked = _dialogs.PickObjects($"Pick groups to {verb}", AdObjectType.Group, multiSelect: true);
        if (picked is null) return;
        foreach (var g in picked)
            if (Groups.All(x => !string.Equals(x.DistinguishedName, g.DistinguishedName, StringComparison.OrdinalIgnoreCase)))
                Groups.Add(g);
    }

    [RelayCommand]
    private void RemoveGroup(AdObjectRow? row) { if (row is not null) Groups.Remove(row); }

    [RelayCommand]
    private void PickCloudGroups()
    {
        var verb = Action == ScenarioActionType.CloudRemoveFromGroups ? "remove from" : "add to";
        var picked = _dialogs.PickCloudGroups($"Pick Entra ID groups to {verb}");
        if (picked is null) return;
        foreach (var g in picked)
            if (CloudGroups.All(x => !string.Equals(x.Id, g.Id, StringComparison.OrdinalIgnoreCase)))
                CloudGroups.Add(new CloudGroupRef { Id = g.Id, Name = g.DisplayName });
    }

    [RelayCommand]
    private void RemoveCloudGroup(CloudGroupRef? row) { if (row is not null) CloudGroups.Remove(row); }

    [RelayCommand]
    private void BrowseOu()
    {
        var dn = _dialogs.PickContainer(TargetOu);
        if (dn is not null) TargetOu = dn;
    }

    /// <summary>Snapshots this row into a persistable step (only the relevant fields).</summary>
    public ScenarioStep ToStep() => new()
    {
        Action = Action,
        Attribute = ShowAttribute ? Attribute.Trim() : string.Empty,
        Value = ShowValue ? Value : string.Empty,
        TargetOu = ShowOu ? TargetOu.Trim() : string.Empty,
        GroupDns = ShowGroups ? Groups.Select(g => g.DistinguishedName).ToList() : new List<string>(),
        CloudGroups = ShowCloudGroups
            ? CloudGroups.Select(g => new CloudGroupRef { Id = g.Id, Name = g.Name }).ToList()
            : new List<CloudGroupRef>(),
    };
}

/// <summary>Create / save / recall / edit / delete reusable action scenarios.</summary>
public partial class ScenarioEditorViewModel : ObservableObject
{
    private readonly IScenarioStore _store;
    private readonly IDialogService _dialogs;
    private readonly Action? _onChanged;
    private string? _originalName;

    public ObservableCollection<Scenario> Scenarios { get; } = new();
    public ObservableCollection<ScenarioStepRow> Steps { get; } = new();

    [ObservableProperty] private Scenario? _selectedScenario;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    public string ScenariosDirectory => _store.ScenariosDirectory;

    public ScenarioEditorViewModel(IScenarioStore store, IDialogService dialogs, Action? onChanged = null)
    {
        _store = store;
        _dialogs = dialogs;
        _onChanged = onChanged;
        Reload();
        NewScenario();
    }

    partial void OnSelectedScenarioChanged(Scenario? value)
    {
        if (value is null) return;
        _originalName = value.Name;
        Name = value.Name;
        Description = value.Description;
        Steps.Clear();
        foreach (var step in value.Steps) Steps.Add(new ScenarioStepRow(_dialogs, step));
    }

    [RelayCommand]
    private void NewScenario()
    {
        _originalName = null;
        SelectedScenario = null;
        Name = "New scenario";
        Description = string.Empty;
        Steps.Clear();
    }

    [RelayCommand]
    private void Clone()
    {
        // Duplicate the current editor contents (steps stay as-is) as a brand-new, unsaved scenario.
        SelectedScenario = null;  // OnSelectedScenarioChanged ignores null, so the loaded steps are kept
        _originalName = null;     // Save will create a new scenario
        if (!Name.StartsWith("Copy of ", StringComparison.OrdinalIgnoreCase)) Name = "Copy of " + Name;
        Status = "Cloned — edit if needed, then Save to create the copy.";
    }

    [RelayCommand] private void AddStep() => Steps.Add(new ScenarioStepRow(_dialogs));
    [RelayCommand] private void RemoveStep(ScenarioStepRow? row) { if (row is not null) Steps.Remove(row); }

    [RelayCommand]
    private void MoveStepUp(ScenarioStepRow? row)
    {
        var i = row is null ? -1 : Steps.IndexOf(row);
        if (i > 0) Steps.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveStepDown(ScenarioStepRow? row)
    {
        var i = row is null ? -1 : Steps.IndexOf(row);
        if (i >= 0 && i < Steps.Count - 1) Steps.Move(i, i + 1);
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status = "Scenario name is required."; return; }
        if (Steps.Count == 0) { Status = "Add at least one step."; return; }
        var scenario = Build();
        try
        {
            _store.Save(scenario, _originalName);
            _originalName = scenario.Name;
            Reload();
            SelectedScenario = Scenarios.FirstOrDefault(s => s.Name == scenario.Name);
            Status = $"Saved “{scenario.Name}”.";
            _onChanged?.Invoke();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void Delete()
    {
        if (string.IsNullOrWhiteSpace(_originalName)) { Status = "Nothing to delete."; return; }
        if (!_dialogs.Confirm("Delete scenario", $"Delete scenario “{_originalName}”?", new[] { _originalName! })) return;
        try
        {
            _store.Delete(_originalName!);
            Reload();
            NewScenario();
            Status = "Scenario deleted.";
            _onChanged?.Invoke();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void Export()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status = "Give the scenario a name before exporting."; return; }
        var path = _dialogs.PromptSaveFile("Scenario files (*.json)|*.json|All files (*.*)|*.*", Name + ".json");
        if (path is null) return;
        try { _store.ExportTo(Build(), path); Status = $"Exported to {path}."; }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void Import()
    {
        var path = _dialogs.PromptOpenFile("Scenario files (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var imported = _store.ImportFrom(path);
            if (Scenarios.Any(s => string.Equals(s.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                && !_dialogs.Confirm("Import scenario", $"A scenario named “{imported.Name}” already exists. Overwrite it?", new[] { imported.Name }))
                return;

            _store.Save(imported, originalName: null);
            Reload();
            SelectedScenario = Scenarios.FirstOrDefault(s => s.Name == imported.Name);
            Status = $"Imported “{imported.Name}”.";
            _onChanged?.Invoke();
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
    }

    private Scenario Build() => new()
    {
        Name = Name.Trim(),
        Description = Description,
        Steps = Steps.Select(s => s.ToStep()).ToList(),
    };

    private void Reload()
    {
        Scenarios.Clear();
        foreach (var s in _store.LoadAll()) Scenarios.Add(s);
    }
}
