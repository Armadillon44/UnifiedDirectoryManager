using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UnifiedDirectoryManager.Models;

/// <summary>
/// One attribute of a loaded object, shown in the edit pane / attribute editor.
/// Logic always uses <see cref="LdapName"/>; the UI shows <see cref="FriendlyName"/>.
/// For DN-valued attributes, <see cref="DisplayValues"/> holds resolved friendly names while
/// <see cref="RawValues"/> keeps the underlying DNs for write operations.
/// </summary>
public partial class AdAttribute : ObservableObject
{
    public required string LdapName { get; init; }
    public required string FriendlyName { get; init; }
    public bool IsMultiValued { get; init; }
    public bool IsDnValued { get; init; }
    public bool IsReadOnly { get; init; }

    /// <summary>Underlying values used for logic/writes (DNs for DN-valued attributes).</summary>
    public ObservableCollection<string> RawValues { get; } = new();

    /// <summary>Friendly, display-ready values (resolved names for DN-valued attributes).</summary>
    public ObservableCollection<string> DisplayValues { get; } = new();

    /// <summary>Original single-line value captured at load time, used for dirty detection.</summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>Editable single-line representation for single-valued attributes.</summary>
    [ObservableProperty]
    private string _editText = string.Empty;

    public bool IsDirty => !IsMultiValued && !string.Equals(EditText, OriginalText, StringComparison.Ordinal);

    /// <summary>Set when a multi-valued attribute's values were changed via the multi-value editor.</summary>
    public bool MultiValueEdited { get; set; }

    /// <summary>Comma-joined display values for compact presentation of multi-valued attributes.</summary>
    public string DisplaySummary => string.Join("; ", DisplayValues);

    /// <summary>Editable flag for binding (true when the attribute can be changed in the editor).</summary>
    public bool IsEditable => !IsReadOnly;

    /// <summary>Convenience for XAML visibility binding.</summary>
    public bool IsSingleValued => !IsMultiValued;

    /// <summary>A single-valued attribute the operator can edit inline (shown as an editable field).</summary>
    public bool IsSingleValueEditable => IsSingleValued && !IsReadOnly;

    /// <summary>A single-valued attribute that can't be edited — shown as static, grayed-out text, not a field.</summary>
    public bool IsSingleValueReadOnly => IsSingleValued && IsReadOnly;

    /// <summary>Raises change notification for the summary after multi-value edits.</summary>
    public void NotifyValuesChanged() => OnPropertyChanged(nameof(DisplaySummary));
}
