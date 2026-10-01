using System.Windows.Input;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// One rendered toolbar entry: a button, or a separator.
/// </summary>
/// <remarks>
/// The toolbar is an <c>ItemsControl</c> over these rather than hand-written buttons, because the
/// operator chooses what is on it (audit T1). Each one carries the already-resolved <see cref="Command"/>
/// so the XAML binds to a real object: binding by NAME through the catalogue would fail silently the way
/// a mistyped <c>{Binding}</c> does, and look identical to a command that is simply unavailable.
/// </remarks>
public sealed class ToolbarButtonViewModel
{
    private ToolbarButtonViewModel(ToolbarItem? item, ICommand? command)
    {
        Item = item;
        Command = command;
    }

    /// <summary>Null for a separator.</summary>
    public ToolbarItem? Item { get; }

    public ICommand? Command { get; }

    public bool IsSeparator => Item is null;

    /// <summary>The XAML binds both halves of one template, so it needs the negation too.</summary>
    public bool IsButton => Item is not null;

    public string Label => Item?.Label ?? string.Empty;

    /// <summary>The Segoe MDL2 glyph, or empty when this item shows text alone.</summary>
    public string Glyph => Item is null || ToolbarCatalogue.UsesTextOnly(Item) ? string.Empty : Item.Glyph ?? string.Empty;

    public bool HasGlyph => Glyph.Length > 0;

    public static ToolbarButtonViewModel Separator() => new(null, null);

    public static ToolbarButtonViewModel For(ToolbarItem item, ICommand command) => new(item, command);

    /// <summary>
    /// Resolves a catalogue item's command against <paramref name="root"/>, walking a dotted path for a
    /// nested view model (<c>Cloud.ExportCsvCommand</c>). Returns null when the path does not resolve,
    /// which the caller treats as "leave it off the toolbar" rather than rendering a dead button.
    /// </summary>
    public static ICommand? Resolve(object root, string commandPath)
    {
        object? current = root;
        foreach (var segment in commandPath.Split('.'))
        {
            if (current is null) return null;
            current = current.GetType().GetProperty(segment)?.GetValue(current);
        }
        return current as ICommand;
    }
}
