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

    [ObservableProperty] private string _value;

    public bool IsEditable => Editability == CloudPropertyEditability.Editable;
    public bool IsDirty => IsEditable && !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

    public CloudProperty(string key, string label, string value, CloudPropertyEditability editability, string? tooltip)
    {
        Key = key;
        Label = label;
        OriginalValue = value;
        Editability = editability;
        Tooltip = tooltip;
        _value = value;
    }
}

/// <summary>A titled group of <see cref="CloudProperty"/> rows (e.g. "On-premises sync").</summary>
public sealed record CloudPropertySection(string Title, IReadOnlyList<CloudProperty> Properties);
