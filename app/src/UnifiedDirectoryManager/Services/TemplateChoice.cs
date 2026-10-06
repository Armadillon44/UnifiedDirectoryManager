using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// One entry in the New User template dropdown: either a template, or “start from scratch”.
/// </summary>
/// <remarks>
/// <para>
/// A wrapper rather than a literal <c>null</c> in the list, which is what the first attempt used. WPF
/// cannot display a null selection: <c>ComboBox</c> substitutes <c>string.Empty</c> for
/// <c>SelectionBoxItem</c>, so the closed box renders <b>blank</b> however the item template is
/// written — the operator picks “no template” and the control looks like it did nothing at all.
/// </para>
/// <para>
/// Deliberately <b>not</b> a sentinel <see cref="UserTemplate"/>. The store, Save as template and
/// Export all deal in <see cref="UserTemplate"/>, and a sentinel of that type could be saved or
/// exported by accident; a <see cref="TemplateChoice"/> cannot be mistaken for one.
/// </para>
/// </remarks>
public sealed class TemplateChoice
{
    public const string FromScratchLabel = "(No template — start from scratch)";

    /// <summary>
    /// What <see cref="AppSettings.DefaultTemplateName"/> holds when the default is “start from scratch”.
    /// </summary>
    /// <remarks>
    /// The empty string, which is safe as a marker because the template store refuses to save a template
    /// whose name is blank — so no real template can ever collide with it. It is also distinct from null,
    /// which means “no default chosen” and must keep behaving as it did before defaults existed.
    /// </remarks>
    public const string FromScratchSetting = "";

    public TemplateChoice(UserTemplate? template) => Template = template;

    /// <summary>The template, or null for “start from scratch”.</summary>
    public UserTemplate? Template { get; }

    public bool IsFromScratch => Template is null;

    /// <summary>What the dropdown shows, closed or open.</summary>
    public string Label => Template?.Name ?? FromScratchLabel;

    /// <summary>
    /// Which entry to land on after the list has been rebuilt.
    /// </summary>
    /// <param name="choices">The freshly built list. Always carries the from-scratch entry.</param>
    /// <param name="previous">
    /// What was selected before the rebuild, or null if nothing was — which happens only on the very
    /// first load.
    /// </param>
    /// <param name="defaultName">
    /// <see cref="AppSettings.DefaultTemplateName"/>: null for none chosen,
    /// <see cref="FromScratchSetting"/> for from-scratch, otherwise a template name. Consulted ONLY on
    /// the first load — a default is where the window opens, not a selection it keeps re-imposing.
    /// </param>
    /// <remarks>
    /// <para>
    /// Out here rather than inside the view model because the rule has three cases that look alike,
    /// and one of them silently undid the operator's choice.
    /// </para>
    /// <para>
    /// It takes the previous CHOICE rather than the previous template's name. A name cannot express
    /// “from scratch”: it would be null, which is also what “nothing was selected” and “no template
    /// matched” look like. Conflating those is exactly what sent an operator who picked “no template”
    /// straight back to the first template in the list on the next reload.
    /// </para>
    /// </remarks>
    public static TemplateChoice Resolve(
        IReadOnlyList<TemplateChoice> choices, TemplateChoice? previous, string? defaultName = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0) throw new ArgumentException("The list always carries the from-scratch entry.", nameof(choices));

        // The window is opening: honour the operator's default. With none set, the first real template,
        // so that from-scratch stays something chosen rather than something defaulted into.
        if (previous is null) return MatchDefault(choices, defaultName) ?? FirstReal(choices) ?? FromScratchIn(choices);

        // From scratch was chosen. A reload refreshes the LIST; it is not a decision about the selection.
        if (previous.IsFromScratch) return FromScratchIn(choices);

        // A named template: keep it, or fall back if it has been deleted since.
        return choices.FirstOrDefault(c =>
                   string.Equals(c.Template?.Name, previous.Template!.Name, StringComparison.OrdinalIgnoreCase))
               ?? FirstReal(choices)
               ?? FromScratchIn(choices);
    }

    /// <summary>
    /// The stored default, or null when none is set or it names a template that no longer exists.
    /// </summary>
    /// <remarks>
    /// A deleted default falls back rather than throwing. Templates are files an operator can remove at
    /// any time, and refusing to open New User because a setting is stale would be the worse failure.
    /// </remarks>
    public static TemplateChoice? MatchDefault(IReadOnlyList<TemplateChoice> choices, string? defaultName)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (defaultName is null) return null;
        if (defaultName.Length == 0) return choices.FirstOrDefault(c => c.IsFromScratch);
        return choices.FirstOrDefault(c =>
            string.Equals(c.Template?.Name, defaultName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What to store in settings so this choice is the default next time.</summary>
    public string SettingValue => Template?.Name ?? FromScratchSetting;

    private static TemplateChoice? FirstReal(IReadOnlyList<TemplateChoice> choices) =>
        choices.FirstOrDefault(c => !c.IsFromScratch);

    private static TemplateChoice FromScratchIn(IReadOnlyList<TemplateChoice> choices) =>
        choices.FirstOrDefault(c => c.IsFromScratch)
        ?? throw new ArgumentException("The list must carry the from-scratch entry.", nameof(choices));
}
