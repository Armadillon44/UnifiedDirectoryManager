using CommunityToolkit.Mvvm.ComponentModel;

namespace UnifiedDirectoryManager.Models;

/// <summary>Whether a cloud property can be edited in Entra ID, and why not if it can't.</summary>
public enum CloudPropertyEditability
{
    /// <summary>Cloud-mastered and writable here.</summary>
    Editable,
    /// <summary>Synced from on-premises AD — must be edited in AD (read-only in the cloud).</summary>
    OnPremMastered,
    /// <summary>System-managed / never directly editable.</summary>
    SystemReadOnly,
}

/// <summary>Which control edits a property, for the rows that can be edited at all.</summary>
public enum CloudPropertyEditor
{
    /// <summary>Free text.</summary>
    Text,
    /// <summary>One of <see cref="CloudProperty.Choices"/>. Used for every yes/no and enum setting: typing
    /// "Yes" into a text box is a spelling test, and a wrong answer is either a silent no-op or a service error.</summary>
    Choice,
}

/// <summary>
/// One property row in a cloud object's details. <see cref="Value"/> is editable (two-way) for
/// <see cref="Editability"/> == <see cref="CloudPropertyEditability.Editable"/>; otherwise the row is shown
/// grayed/read-only with a <see cref="Tooltip"/> explaining why.
/// </summary>
public sealed partial class CloudProperty : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public string OriginalValue { get; }
    public CloudPropertyEditability Editability { get; }
    public string? Tooltip { get; }
    public CloudPropertyEditor Editor { get; }

    /// <summary>The allowed values for <see cref="CloudPropertyEditor.Choice"/>; null otherwise.</summary>
    public IReadOnlyList<string>? Choices { get; }

    [ObservableProperty] private string _value;

    public bool IsEditable => Editability == CloudPropertyEditability.Editable;
    public bool IsDirty => IsEditable && !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

    /// <summary>
    /// True when this row draws a drop-down. A read-only row never does, whatever its editor: the grayed text
    /// box is how every uneditable row in this pane already reads, and a disabled drop-down would suggest the
    /// value is merely unavailable right now rather than not this app's to change.
    /// </summary>
    public bool UsesChoiceEditor => IsEditable && Editor == CloudPropertyEditor.Choice && Choices is { Count: > 0 };

    /// <summary>True when this row draws a text box — the default, and the fallback for every read-only row.</summary>
    public bool UsesTextEditor => !UsesChoiceEditor;

    /// <summary>What this setting is, in one sentence. Null when nothing has been written for the key, in
    /// which case no "?" is offered — an absent explanation beats a guessed one.</summary>
    public string? Help { get; }

    public bool HasHelp => !string.IsNullOrWhiteSpace(HelpText);

    /// <summary>
    /// What the "?" shows: what the setting is, and underneath it the reason it cannot be changed when that
    /// applies. One place answers both questions, rather than making the reason a separate hover on the
    /// control that an operator has to discover.
    /// </summary>
    public string HelpText =>
        (Help, Tooltip) switch
        {
            (null, null) => string.Empty,
            (null, var why) => why!,
            (var what, null) => what!,
            var (what, why) => what + "\n\n" + why,
        };

    public CloudProperty(string key, string label, string value, CloudPropertyEditability editability, string? tooltip,
                         CloudPropertyEditor editor = CloudPropertyEditor.Text, IReadOnlyList<string>? choices = null,
                         string? help = null)
    {
        Help = help;
        Key = key;
        Label = label;
        OriginalValue = value;
        Editability = editability;
        Tooltip = tooltip;
        Editor = editor;
        Choices = choices;
        _value = value;
    }
}

/// <summary>A titled group of <see cref="CloudProperty"/> rows (e.g. "On-premises sync").</summary>
public sealed record CloudPropertySection(string Title, IReadOnlyList<CloudProperty> Properties);
