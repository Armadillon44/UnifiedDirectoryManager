namespace UnifiedDirectoryManager.Services;

/// <summary>Which view an item applies to, so customisation cannot pin a button that is dead there.</summary>
public enum ToolbarScope
{
    /// <summary>Useful in both the on-prem and the cloud view.</summary>
    Both,
    /// <summary>On-prem only; hidden in the cloud view, as the hand-written toolbar always did.</summary>
    OnPremOnly,
    /// <summary>Cloud only.</summary>
    CloudOnly,
}

/// <summary>
/// One thing the operator may put on the toolbar.
/// </summary>
/// <param name="Id">Stable key written to settings. Never change one; retire it and add a new one.</param>
/// <param name="Label">
/// The canonical label — the SAME words the menu uses (audit P6). Not a shorter synonym: an operator who
/// sees it on the toolbar has to be able to find it in the menus by scanning for the word they saw.
/// </param>
/// <param name="CommandName">
/// Property name on <c>MainViewModel</c>, dotted for a nested view model (<c>Cloud.ExportCsvCommand</c>).
/// </param>
/// <param name="Scope">Which view it applies to.</param>
/// <param name="Glyph">
/// A Segoe MDL2 Assets codepoint, or null for text only. Null is a real choice, not a gap: no icon is
/// better than a vague icon.
/// </param>
public sealed record ToolbarItem(
    string Id, string Label, string CommandName, ToolbarScope Scope, string? Glyph = null);

/// <summary>
/// Every command that MAY go on the toolbar, and the default arrangement.
/// </summary>
/// <remarks>
/// <para>
/// The toolbar owning nothing used to be a happy accident. Once it is customisable it becomes a safety
/// requirement: if an operator can remove a button, no command may be reachable <i>only</i> from the
/// toolbar, or removing it destroys access to a feature with no way back except re-customising.
/// </para>
/// <para>
/// This catalogue is how that is checked rather than remembered. Everything offered here is also in the
/// menu bar, and <c>test-ui-placement</c> walks the list and fails by name if one ever is not.
/// </para>
/// <para>
/// Glyph codepoints were verified against the installed Segoe MDL2 Assets rather than copied from a
/// table: a wrong codepoint renders as an empty box, which looks like a missing font rather than a typo.
/// The suite re-checks them, so a glyph that is dropped from a future Windows is found here and not by an
/// operator.
/// </para>
/// </remarks>
public static class ToolbarCatalogue
{
    /// <summary>A gap between groups. Repeatable, unlike every other id.</summary>
    public const string SeparatorId = "separator";

    public static IReadOnlyList<ToolbarItem> All { get; } = new[]
    {
        // --- creating things ---
        new ToolbarItem("new-user",        "New User…",                    "NewUserCommand",             ToolbarScope.OnPremOnly, "\uE8FA"), // AddFriend
        new ToolbarItem("bulk-create",     "Bulk Create Users…",           "BulkCreateUsersCommand",     ToolbarScope.OnPremOnly, "\uE716"), // People
        new ToolbarItem("new-group",       "New Group…",                   "NewGroupCommand",            ToolbarScope.OnPremOnly, "\uE902"), // Group
        new ToolbarItem("new-ou",          "New OU…",                      "CreateOuHereCommand",        ToolbarScope.OnPremOnly, "\uE838"), // OpenFolder
        new ToolbarItem("new-cloud-group", "New Cloud Group…",             "NewCloudGroupCommand",       ToolbarScope.CloudOnly,  "\uE902"), // Group
        new ToolbarItem("templates",       "User Templates…",              "ManageTemplatesCommand",     ToolbarScope.OnPremOnly, "\uE7C3"), // Page

        // --- acting on the selection ---
        new ToolbarItem("properties",      "Properties…",                  "OpenSelectedCommand",        ToolbarScope.OnPremOnly, "\uE946"), // Info
        new ToolbarItem("enable",          "Enable Account(s)",            "EnableSelectedCommand",      ToolbarScope.OnPremOnly),
        new ToolbarItem("disable",         "Disable Account(s)",           "DisableSelectedCommand",     ToolbarScope.OnPremOnly),
        new ToolbarItem("unlock",          "Unlock Account(s)",            "UnlockSelectedCommand",      ToolbarScope.OnPremOnly, "\uE785"), // Unlock
        new ToolbarItem("reset-password",  "Reset Password…",              "ResetPasswordSelectedCommand", ToolbarScope.OnPremOnly, "\uE192"), // Permissions
        new ToolbarItem("copy-user",       "Copy User…",                   "CopyUserCommand",            ToolbarScope.OnPremOnly, "\uE8C8"), // Copy
        new ToolbarItem("copy-groups",     "Copy Groups to User…",         "CopyGroupsToUserCommand",    ToolbarScope.OnPremOnly),
        new ToolbarItem("save-template",   "Save as Template…",            "SaveSelectedAsTemplateCommand", ToolbarScope.OnPremOnly),
        new ToolbarItem("add-to-groups",   "Add to Groups…",               "AddSelectedToGroupsCommand", ToolbarScope.OnPremOnly, "\uE716"), // People
        new ToolbarItem("move-to-ou",      "Move to OU…",                  "MoveSelectedToOuCommand",    ToolbarScope.OnPremOnly, "\uE8DE"), // MoveToFolder
        new ToolbarItem("bulk-edit",       "Bulk Edit…",                   "BulkEditCommand",            ToolbarScope.OnPremOnly, "\uE70F"), // Edit
        // Destructive items keep their words, whatever the operator has customised — see UsesTextOnly.
        new ToolbarItem("delete",          "Delete Selected…",             "DeleteSelectedCommand",      ToolbarScope.OnPremOnly, "\uE74D"), // Delete

        // --- exporting ---
        new ToolbarItem("export-list",     "Export List to CSV…",          "ExportCsvCommand",           ToolbarScope.OnPremOnly, "\uE896"), // Download
        new ToolbarItem("export-members",  "Export Group Members to CSV…", "ExportGroupMembersCommand",  ToolbarScope.OnPremOnly, "\uE896"), // Download
        new ToolbarItem("append-members",  "Append Group Members to CSV…", "AppendGroupMembersCommand",  ToolbarScope.OnPremOnly),
        new ToolbarItem("export-loaded",   "Export Loaded to CSV…",        "Cloud.ExportCsvCommand",     ToolbarScope.CloudOnly,  "\uE896"), // Download
        new ToolbarItem("export-all",      "Export All to CSV…",           "Cloud.ExportAllCsvCommand",  ToolbarScope.CloudOnly,  "\uE896"), // Download

        // --- finding things, and the app itself ---
        new ToolbarItem("advanced-search", "Advanced Search…",             "AdvancedSearchCommand",      ToolbarScope.OnPremOnly, "\uE721"), // Search
        new ToolbarItem("refresh",         "Refresh",                      "RefreshCommand",             ToolbarScope.Both,       "\uE72C"), // Refresh
        new ToolbarItem("toggle-dock",     "Toggle Pane Dock (Right / Bottom)", "ToggleDockCommand",     ToolbarScope.Both,       "\uE744"), // DockBottom
        new ToolbarItem("delta-sync",      "Entra Connect Delta Sync…",    "EntraSyncCommand",           ToolbarScope.Both,       "\uE895"), // Sync
        new ToolbarItem("scenarios",       "Manage Scenarios…",            "ManageScenariosCommand",     ToolbarScope.Both),
        new ToolbarItem("view-log",        "View Log File…",               "ViewLogCommand",             ToolbarScope.Both),
        new ToolbarItem("logs-folder",     "Open Logs Folder",             "OpenLogsCommand",            ToolbarScope.Both,       "\uE8BC"), // ShowResults
        new ToolbarItem("settings",        "Settings…",                    "OpenSettingsCommand",        ToolbarScope.Both,       "\uE713"), // Settings
    };

    /// <summary>
    /// Exactly the toolbar as it shipped before it was customisable, so nobody's layout changes on
    /// upgrade and an untouched <c>settings.json</c> needs no migration.
    /// </summary>
    public static IReadOnlyList<string> DefaultIds { get; } = new[]
    {
        "new-user", "bulk-create", "templates",
        SeparatorId,
        "advanced-search", "add-to-groups", "bulk-edit",
        SeparatorId,
        "refresh", "export-list",
        SeparatorId,
        "logs-folder",
    };

    /// <summary>Items that never render as a bare icon, however the operator has customised things.</summary>
    public static bool UsesTextOnly(ToolbarItem item) => item.Id == "delete";

    public static ToolbarItem? Find(string id) =>
        All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Turns whatever is in settings into a layout that can actually be rendered.
    /// </summary>
    /// <remarks>
    /// Three rules, in order:
    /// <list type="number">
    /// <item>Nothing saved means the defaults, so an existing settings file needs no migration.</item>
    /// <item>An id this build does not know is DROPPED and the rest survive — a file written by a newer
    /// build that knew about more buttons must still open, minus the buttons that do not exist here.</item>
    /// <item>Separators are tidied: none leading, none trailing, never two in a row. A saved layout can
    /// end up with stray ones once unknown ids are dropped, and a line floating at the end of a toolbar
    /// reads as a rendering fault rather than as a choice.</item>
    /// </list>
    /// A layout left with no BUTTONS falls back to the defaults. Empty means defaults by rule 1, so an
    /// operator cannot store an empty toolbar anyway, and a row of nothing but separators is an artefact
    /// rather than something anyone chose.
    /// </remarks>
    public static IReadOnlyList<string> Normalise(IEnumerable<string>? saved)
    {
        var ids = saved?.ToList();
        if (ids is null || ids.Count == 0) return DefaultIds;

        var known = ids
            .Where(id => id == SeparatorId || Find(id) is not null)
            .ToList();

        var tidied = new List<string>();
        foreach (var id in known)
        {
            if (id != SeparatorId) { tidied.Add(id); continue; }
            if (tidied.Count == 0) continue;              // no leading separator
            if (tidied[^1] == SeparatorId) continue;      // no doubled separator
            tidied.Add(id);
        }
        while (tidied.Count > 0 && tidied[^1] == SeparatorId) tidied.RemoveAt(tidied.Count - 1);

        return tidied.Any(id => id != SeparatorId) ? tidied : DefaultIds;
    }
}
