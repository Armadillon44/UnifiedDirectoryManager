using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Read-only details of one cloud (Entra ID) object, shown in the properties pane and the
/// double-click properties window. Shows a quick summary immediately from the list row, then loads the
/// full grouped property set (and, for a user, licenses + memberships; for a group, members).
/// </summary>
public partial class CloudObjectDetailViewModel : ObservableObject
{
    private readonly IGraphService _graph;
    private readonly IDialogService _dialogs;
    private readonly IExchangeService _exchange; // for the license-removal mailbox guardrail
    private CloudObjectRow? _currentTarget; // guards stale async results on fast re-selection

    // Re-selecting the SAME row is a real case (a write path calls SetTarget(row) to re-read), and the row
    // reference alone can't tell those apart. Each SetTarget bumps this; a load whose token is stale has been
    // superseded and must not touch Sections.
    private int _detailToken;

    /// <summary>The selected mailbox's ExchangeGuid, captured from the detail read; null for other kinds.</summary>
    private string? _mailboxExchangeGuid;

    /// <summary>
    /// The recipients each editable list currently holds, including edits not yet saved. The row itself keeps
    /// only addresses; reopening the editor has to show the same people the operator last chose, not the ones
    /// Exchange still has. Dies with the pane, because the rows do.
    /// </summary>
    private readonly Dictionary<CloudProperty, List<MailboxRecipient>> _pendingRecipients = new();

    /// <summary>
    /// Entries a list holds that this app cannot write back — a role group such as Organization Management is
    /// not a mail recipient, and neither is a deleted account. They are left exactly as they are, so they stay
    /// in what the row displays: dropping them from the text would make the pane lie about the group.
    /// </summary>
    private readonly Dictionary<CloudProperty, List<string>> _retainedRecipients = new();

    /// <summary>The group's Exchange GUID, captured on load: the identifier a write addresses, because
    /// changing the alias can rewrite the address the row was found by.</summary>
    private string? _exchangeGroupGuid;

    [ObservableProperty] private bool _hasTarget;
    // User action-bar visibility (cloud user writes).
    [ObservableProperty] private bool _showEnable;
    [ObservableProperty] private bool _showDisable;
    [ObservableProperty] private string _emptyHint = "Select an object to view its properties.";
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _kindLabel = string.Empty;
    [ObservableProperty] private bool _isUser;
    [ObservableProperty] private bool _isGroup;
    [ObservableProperty] private bool _isDevice;
    [ObservableProperty] private bool _isMailbox;

    /// <summary>True for a distribution list or mail-enabled security group — a group Exchange owns, not Entra.</summary>
    [ObservableProperty] private bool _isExchangeGroup;

    /// <summary>Which service describes this object — the header used to say "Entra ID" for everything.</summary>
    [ObservableProperty] private string _sourceLabel = "Entra ID";

    /// <summary>True once size and usage have been fetched, so the button doesn't invite a second expensive read.</summary>
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private bool _hasLicenses;
    [ObservableProperty] private bool _hasMemberships;
    [ObservableProperty] private bool _hasMembers;
    [ObservableProperty] private bool _canAddMembers; // cloud-only group (synced group membership is on-prem-mastered)
    [ObservableProperty] private bool _canManageLicenses; // users only (assign/remove direct licenses)
    [ObservableProperty] private bool _canManageMemberships; // users + devices (add/remove cloud group membership)
    private string? _usageLocation; // captured at load; required before a license can be assigned

    public ObservableCollection<CloudPropertySection> Sections { get; } = new();
    public ObservableCollection<CloudLicense> Licenses { get; } = new();
    public ObservableCollection<CloudGroup> Memberships { get; } = new();
    public ObservableCollection<CloudMember> Members { get; } = new();

    public CloudObjectDetailViewModel(IGraphService graph, IExchangeService exchange, IDialogService dialogs)
    {
        _graph = graph;
        _exchange = exchange;
        _dialogs = dialogs;
    }

    /// <summary>Shows a row's details (null clears the pane).</summary>
    public void SetTarget(CloudObjectRow? row)
    {
        Clear();
        _currentTarget = row;
        _detailToken++; // supersede any load still in flight, including one for this same row
        if (row is null) { HasTarget = false; EmptyHint = "Select an object to view its properties."; return; }

        HasTarget = true;
        EmptyHint = string.Empty;
        Title = row.DisplayName;
        KindLabel = row.Kind switch
        {
            CloudObjectKind.User => "User",
            CloudObjectKind.Group => "Group",
            CloudObjectKind.Device => "Device",
            CloudObjectKind.Mailbox => "Mailbox",
            _ => "Object",
        };
        var fromExchange = row.Source == CloudObjectSource.Exchange;
        IsUser = row.Kind == CloudObjectKind.User;
        // IsGroup drives the Graph-backed Members tab and its add/remove buttons, so it must stay FALSE for a
        // distribution list: Graph answers that membership with 403, and the members editor is its own dialog.
        IsGroup = row.Kind == CloudObjectKind.Group && !fromExchange;
        IsDevice = row.Kind == CloudObjectKind.Device;
        IsMailbox = row.Kind == CloudObjectKind.Mailbox;
        IsExchangeGroup = row.Kind == CloudObjectKind.Group && fromExchange;
        // The header said "Entra ID {kind}" unconditionally, which is wrong for anything Exchange describes.
        SourceLabel = fromExchange ? "Exchange Online" : "Entra ID";
        HasUsage = false;
        CanManageLicenses = IsUser;
        CanManageMemberships = IsUser || IsDevice;

        // Account-action visibility (the list row carries the current enabled state).
        var enabled = !string.Equals(row.Get("accountEnabled"), "No", StringComparison.OrdinalIgnoreCase);
        ShowDisable = IsUser && enabled;
        ShowEnable = IsUser && !enabled;

        // Instant summary from the list row, replaced by the full grouped set once it loads.
        Sections.Add(BuildSummary(row));
        SelectSection(0);
        _ = LoadDetailAsync(row);
    }

    public void Reset() => SetTarget(null);

    private void Clear()
    {
        Title = KindLabel = Status = string.Empty;
        IsUser = IsGroup = IsDevice = IsMailbox = IsExchangeGroup = false;
        HasUsage = false;
        _mailboxExchangeGuid = null;
        _exchangeGroupGuid = null;
        _pendingRecipients.Clear();
        _retainedRecipients.Clear();
        ShowEnable = ShowDisable = false;
        CanAddMembers = false;
        CanManageLicenses = false;
        CanManageMemberships = false;
        _usageLocation = null;
        UnwireSections();
        Sections.Clear();
        HasChanges = false;
        Licenses.Clear(); HasLicenses = false;
        Memberships.Clear(); HasMemberships = false;
        Members.Clear(); HasMembers = false;
    }

    // --- Editing (dirty tracking + Save/Revert) ---

    [ObservableProperty] private bool _hasChanges;

    partial void OnHasChangesChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Which tab the strip shows. The strip mixes the section tabs with fixed TabItems (Licenses, Member Of,
    /// Members) that are Collapsed for whatever they do not apply to. Left to itself the TabControl selects
    /// the first REAL TabItem it can find, and at first measure the sections have not arrived, so it lands on
    /// a collapsed fixed tab and stays there — which is why a distribution group's pane rendered blank until
    /// a tab was clicked. Refilling the sections never moves it. Every rebuild names the tab it means.
    /// </summary>
    [ObservableProperty] private int _selectedSectionIndex = -1;

    /// <summary>
    /// Selects a tab, forced through -1 first. If the property already holds the target value the binding
    /// raises nothing, and the strip stays on the nothing it was left with by the rebuild.
    /// </summary>
    private void SelectSection(int index)
    {
        SelectedSectionIndex = -1;
        if (index >= 0 && index < Sections.Count) SelectedSectionIndex = index;
    }

    private void WireSections()
    {
        foreach (var p in Sections.SelectMany(s => s.Properties))
            p.PropertyChanged += OnPropertyValueChanged;
        RecomputeHasChanges();
    }

    private void UnwireSections()
    {
        foreach (var p in Sections.SelectMany(s => s.Properties))
            p.PropertyChanged -= OnPropertyValueChanged;
    }

    private void OnPropertyValueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudProperty.Value)) RecomputeHasChanges();
    }

    private void RecomputeHasChanges() =>
        HasChanges = Sections.SelectMany(s => s.Properties).Any(p => p.IsDirty);

    private bool CanSave() => HasChanges && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var row = _currentTarget;
        if (row is null) return;
        var dirty = Sections.SelectMany(s => s.Properties).Where(p => p.IsDirty).ToList();
        if (dirty.Count == 0) return;

        var lines = dirty.Select(p => $"{p.Label}: {(string.IsNullOrWhiteSpace(p.Value) ? "(clear)" : p.Value)}").ToList();
        // A distribution list and an Entra security group are both CloudObjectKind.Group; only the source says
        // which service owns the write, and Graph answers a write to a distribution list with 400.
        var toExchange = row.Kind == CloudObjectKind.Group && row.Source == CloudObjectSource.Exchange;
        // The one edit on this pane with no way back through this app.
        if (toExchange && dirty.Any(p => p.Key == "roomList" && p.Value == "Yes"))
            lines.Add("Note: marking a group as a room list cannot be undone here. Exchange Online has no "
                      + "supported way to turn a room list back into an ordinary distribution list.");
        if (!_dialogs.Confirm("Save cloud changes", $"Apply {dirty.Count} change(s) to “{row.DisplayName}”?", lines))
            return;

        IsBusy = true;
        try
        {
            if (toExchange)
            {
                // Address the write by the GUID captured on load, not by the value the row was found with: an
                // alias change can rewrite the primary address, and the reload afterwards has to still find it.
                var identity = _exchangeGroupGuid ?? row.Get("primarySmtpAddress");
                if (string.IsNullOrWhiteSpace(identity))
                {
                    Status = "This group has no identifier to save against."; IsBusy = false; return;
                }
                var unchanged = await _exchange.SetDistributionGroupPropertiesAsync(identity, dirty);
                // A row can be edited into a value Exchange considers identical. Counting those as saved
                // would report work that never happened, and the reload would then undo them on screen.
                var applied = dirty.Count - unchanged.Count;
                var saved = applied > 0 ? $"Saved {applied} change(s)." : "Nothing needed saving.";
                if (unchanged.Count > 0)
                {
                    var same = dirty.Where(p => unchanged.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                                    .Select(p => p.Label);
                    saved += $" {string.Join(", ", same)} already matched what was typed; Exchange ignores case and order.";
                }
                IsBusy = false;
                if (await LoadExchangeDetailAsync(row, mailbox: false, identityOverride: identity))
                {
                    SyncRowFromSections(row);
                    Status = saved;
                }
                else if (ReferenceEquals(_currentTarget, row))
                {
                    // The write landed; only the re-read failed. Leaving the edited rows on screen would keep
                    // the Save bar armed over changes that are already applied, and invite a second send of the
                    // same edit. Fall back to the list-row summary, which is read-only and still true, and keep
                    // both facts in the status rather than letting the failure erase the success.
                    var why = Status;
                    UnwireSections();
                    Sections.Clear();
                    Sections.Add(BuildSummary(row));
                    WireSections();
                    SelectSection(0);
                    Status = saved + " " + why;
                }
                return;
            }

            var changes = dirty.ToDictionary(p => p.Key, p => string.IsNullOrWhiteSpace(p.Value) ? (string?)null : p.Value.Trim());
            if (row.Kind == CloudObjectKind.User) await _graph.UpdateUserAsync(row.Id, changes);
            else if (row.Kind == CloudObjectKind.Group) await _graph.UpdateGroupAsync(row.Id, changes);
            else { Status = "This object type can't be edited."; IsBusy = false; return; }

            Status = $"Saved {changes.Count} change(s).";
            SetTarget(row); // re-read live state (also clears IsBusy via load)
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Cloud property save failed.", ex);
            Status = "Save failed: " + ex.Message;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Edits one recipient-valued setting through the picker. The addresses are resolved here rather than when
    /// the pane opens: it costs a directory lookup per entry, and they are only needed to seed the picker.
    /// </summary>
    [RelayCommand]
    private async Task EditRecipientsAsync(CloudProperty? property)
    {
        var row = _currentTarget;
        if (row is null || property is null || !property.UsesRecipientEditor) return;

        var identity = _exchangeGroupGuid ?? row.Get("primarySmtpAddress");
        if (string.IsNullOrWhiteSpace(identity))
        {
            Status = "This group has no identifier, so its recipient lists can't be read.";
            return;
        }

        // Reopening shows the operator what they last chose. Re-reading Exchange would replace a pending edit
        // with the server's list, and they would have to make it again without being told it had gone.
        var seed = _pendingRecipients.TryGetValue(property, out var pending) ? pending : null;
        if (seed is null)
        {
            var token = _detailToken;
            DlRecipientList resolved;
            IsBusy = true;
            try { resolved = await _exchange.GetDistributionGroupRecipientsAsync(identity, property.Key); }
            catch (Exception ex)
            {
                AppLog.Instance.Warn($"Could not read '{property.Key}' for '{identity}': {ex.Message}");
                if (token == _detailToken) Status = $"Could not read the current {property.Label}: {ex.Message}";
                return;
            }
            finally { if (token == _detailToken) IsBusy = false; }

            // The resolve is a real round trip on a serialised channel. If the operator has moved on, this
            // property no longer belongs to anything on screen: editing it would write into an orphan.
            if (token != _detailToken || !ReferenceEquals(_currentTarget, row)) return;

            // Only a list too long to resolve in one read is refused: the picker would show part of it, and
            // an operator cannot sensibly edit a list whose remainder is hidden from them.
            if (resolved.NotLookedUp > 0)
            {
                _dialogs.Alert(property.Label,
                    $"This list has more entries than can be resolved in one read ({resolved.NotLookedUp} of "
                    + $"{resolved.Entries.Count} were not looked up), so it can't be edited here. Change it in "
                    + "the Exchange admin center.");
                return;
            }

            // Entries with no usable identity are NOT a reason to refuse. They are left exactly as they are:
            // the save sends only the difference, so a role group like Organization Management stays an owner
            // whatever else changes. They stay in the row's text so the pane keeps telling the truth.
            _retainedRecipients[property] = resolved.Unresolved.ToList();
            var writable = resolved.Entries.Where(r => !string.IsNullOrWhiteSpace(r.PrimarySmtpAddress)).ToList();

            // Taken once, and only from the server: this is what the save diffs against.
            property.SetRecipientBaseline(writable.Select(r => r.PrimarySmtpAddress).ToList());
            seed = writable;
            _pendingRecipients[property] = seed;
        }

        var picked = _dialogs.PickMailboxRecipients($"{property.Label} for “{row.DisplayName}”", seed);
        if (picked is null) return; // cancelled: whatever was pending stays pending

        _pendingRecipients[property] = picked.ToList();
        // The retained entries lead, because that is the order Exchange returned them in and they are still
        // part of the list whether or not this app can name them.
        var retained = _retainedRecipients.TryGetValue(property, out var kept) ? kept : [];
        property.SetRecipients(
            picked.Select(r => r.PrimarySmtpAddress).ToList(),
            string.Join("; ", retained.Concat(picked.Select(r => r.DisplayName))));
    }

    /// <summary>
    /// Opens the distribution group's membership editor — the same dialog activating the row opens, not a
    /// second implementation of it. Membership is not shown in this pane because Microsoft Graph answers a
    /// distribution list's membership with 403, so it is read through Exchange in its own window.
    /// </summary>
    [RelayCommand]
    private void ManageMembers()
    {
        var row = _currentTarget;
        if (row is null || !IsExchangeGroup) return;

        // The GUID captured on load is exact; the address is the fallback before the detail has arrived.
        var identity = _exchangeGroupGuid ?? row.Get("primarySmtpAddress");
        if (string.IsNullOrWhiteSpace(identity))
        {
            Status = "This group has no identifier, so its members can't be read.";
            return;
        }
        // The editor refuses every write for a synced group and says why, so it is told up front.
        var synced = string.Equals(row.Get("dirSynced"), "Synced", StringComparison.OrdinalIgnoreCase);
        _dialogs.ShowDistributionGroupMembers(identity, row.DisplayName, synced);
    }

    [RelayCommand]
    private void Revert()
    {
        if (_currentTarget is { } row) SetTarget(row); // reload discards edits
    }

    /// <summary>Adds picked Entra users/devices to the current cloud group.</summary>
    [RelayCommand]
    private async Task AddMembersAsync()
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.Group) return;

        var picked = _dialogs.PickCloudMembers($"Add members to “{row.DisplayName}”");
        if (picked is null || picked.Count == 0) return;
        if (!_dialogs.Confirm("Add members", $"Add {picked.Count} member(s) to “{row.DisplayName}”?",
                picked.Select(p => $"{p.Kind}: {p.DisplayName}")))
            return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var m in picked)
        {
            try { await _graph.AddMemberToGroupAsync(row.Id, m.Id); items.Add(new BulkItemResult(m.Id, m.DisplayName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(m.Id, m.DisplayName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // refresh the members list
    }

    /// <summary>Removes the selected members from the current cloud group.</summary>
    [RelayCommand]
    private async Task RemoveMembersAsync(System.Collections.IList? selected)
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.Group) return;

        var members = selected?.Cast<CloudMember>().ToList() ?? new List<CloudMember>();
        if (members.Count == 0) { _dialogs.Alert("Remove members", "Select one or more members to remove."); return; }

        var heading = members.Count == 1
            ? $"Remove this member from “{row.DisplayName}”?"
            : $"Remove {members.Count} members from “{row.DisplayName}”?";
        if (!_dialogs.Confirm("Remove members", heading, members.Select(m => $"{m.ObjectType}: {m.DisplayName}")))
            return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var m in members)
        {
            try { await _graph.RemoveMemberFromGroupAsync(row.Id, m.Id); items.Add(new BulkItemResult(m.Id, m.DisplayName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(m.Id, m.DisplayName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // refresh the members list
    }

    private static CloudPropertySection BuildSummary(CloudObjectRow row)
    {
        // Instant read-only placeholder from the list row; replaced by the full classified sections on load.
        var headers = CloudColumnCatalog.Headers(ModeFor(row.Kind, row.Source));
        var props = new List<CloudProperty>
        {
            new("displayName", "Display name", row.DisplayName, CloudPropertyEditability.SystemReadOnly, null),
        };
        foreach (var kv in row.Values)
            props.Add(new CloudProperty(kv.Key, headers.TryGetValue(kv.Key, out var h) ? h : kv.Key,
                string.IsNullOrEmpty(kv.Value) ? "—" : kv.Value, CloudPropertyEditability.SystemReadOnly, null));
        return new CloudPropertySection("Summary", props);
    }

    private async Task LoadDetailAsync(CloudObjectRow row)
    {
        if (!_graph.IsSignedIn) return;
        // A mailbox is described by Exchange, not Graph — mailbox type, forwarding, holds, quotas, archive state
        // and the protocol flags have no Graph equivalent at all.
        if (row.Kind == CloudObjectKind.Mailbox) { await LoadExchangeDetailAsync(row, mailbox: true); return; }
        if (row.Kind == CloudObjectKind.Group && row.Source == CloudObjectSource.Exchange)
        {
            await LoadExchangeDetailAsync(row, mailbox: false);
            return;
        }

        IsBusy = true;
        try
        {
            var sections = await _graph.GetObjectDetailAsync(row.Id, row.Kind);
            if (!ReferenceEquals(_currentTarget, row)) return; // selection moved on
            if (sections.Count > 0)
            {
                UnwireSections();
                Sections.Clear();
                foreach (var s in sections) Sections.Add(s);
                WireSections();
                SelectSection(0);
            }

            if (IsUser)
            {
                var upn = row.Get("userPrincipalName");
                if (!string.IsNullOrEmpty(upn))
                {
                    var info = await _graph.GetUserByUpnAsync(upn);
                    if (!ReferenceEquals(_currentTarget, row)) return;
                    if (info is not null)
                    {
                        _usageLocation = info.UsageLocation;
                        foreach (var l in info.Licenses) Licenses.Add(l);
                        HasLicenses = Licenses.Count > 0;
                        foreach (var g in info.Groups) Memberships.Add(g);
                        HasMemberships = Memberships.Count > 0;
                    }
                }
            }
            else if (IsGroup)
            {
                // Members can be managed here only for cloud-only, assigned (non-dynamic) groups: synced group
                // membership is on-prem-mastered, and a dynamic group's membership is rule-managed by Entra.
                var origin = Sections.SelectMany(s => s.Properties).FirstOrDefault(p => p.Key == "origin")?.Value;
                var rule = Sections.SelectMany(s => s.Properties).FirstOrDefault(p => p.Key == "membershipRule")?.Value;
                var isDynamic = !string.IsNullOrWhiteSpace(rule) && rule != "—";
                CanAddMembers = !string.Equals(origin, "Synced", StringComparison.OrdinalIgnoreCase) && !isDynamic;

                var members = await _graph.GetGroupMembersAsync(row.Id);
                if (!ReferenceEquals(_currentTarget, row)) return;
                foreach (var m in members) Members.Add(m);
                HasMembers = Members.Count > 0;
            }
            else if (IsDevice)
            {
                // Devices can be group members too — load their memberships so they can be managed here.
                var groups = await _graph.GetObjectMemberOfAsync(row.Id, row.Kind);
                if (!ReferenceEquals(_currentTarget, row)) return;
                foreach (var g in groups) Memberships.Add(g);
                HasMemberships = Memberships.Count > 0;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Could not load full cloud details: " + ex.Message);
            if (ReferenceEquals(_currentTarget, row)) Status = "Could not load full details: " + ex.Message;
        }
        finally { if (ReferenceEquals(_currentTarget, row)) IsBusy = false; }
    }

    /// <summary>
    /// Loads read-only property sections from Exchange, for either a mailbox or a distribution group. One
    /// method because everything around the call is identical — identity resolution, the staleness guard, and
    /// leaving the row summary on screen when the read fails. Only the service call differs.
    /// </summary>
    /// <param name="identityOverride">Addresses this read directly instead of resolving it from the row. Used
    /// after a save, where changing the alias may have rewritten the address the row still carries.</param>
    /// <returns>False only when the read itself failed. A read superseded by another selection reports true:
    /// the pane has moved on, and its caller must not act on a target that is no longer on screen.</returns>
    private async Task<bool> LoadExchangeDetailAsync(CloudObjectRow row, bool mailbox, string? identityOverride = null)
    {
        // A group is addressed by its SMTP; a mailbox may be reached by UPN too.
        var identity = identityOverride ?? (mailbox
            ? MailboxIdentityFor(row) ?? row.Get("primarySmtpAddress")
            : row.Get("primarySmtpAddress"));
        if (string.IsNullOrWhiteSpace(identity))
        {
            Status = mailbox
                ? "This mailbox has no address, so Exchange can't be asked about it."
                : "This group has no email address, so Exchange can't be asked about it.";
            return false;
        }

        var token = _detailToken;
        IsBusy = true;
        try
        {
            var sections = mailbox
                ? await _exchange.GetMailboxDetailAsync(identity)
                : await _exchange.GetDistributionGroupDetailAsync(identity);
            if (token != _detailToken || !ReferenceEquals(_currentTarget, row)) return true; // superseded
            UnwireSections();
            Sections.Clear();
            foreach (var s in sections) Sections.Add(s);
            WireSections();
            SelectSection(0);
            HasUsage = false; // the rebuilt section list no longer holds the usage rows
            // Keep the ExchangeGuid: it is how the usage read addresses the mailbox exactly, and the only
            // identifier that read echoes back to be checked against.
            var guid = sections.SelectMany(s => s.Properties).FirstOrDefault(p => p.Key == "exchangeGuid")?.Value;
            guid = string.IsNullOrWhiteSpace(guid) || guid == "—" ? null : guid;
            // For a mailbox this addresses the usage read exactly. For a group it addresses the WRITE, which
            // matters because changing the alias can rewrite the primary address the row was found by.
            if (mailbox) _mailboxExchangeGuid = guid; else _exchangeGroupGuid = guid;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"Could not load Exchange details for '{identity}': {ex.Message}");
            // The summary built from the list row stays on screen, so the pane still shows something true.
            if (token == _detailToken)
                Status = (mailbox ? "Could not load mailbox details: " : "Could not load group details: ") + ex.Message;
            return false;
        }
        finally { if (token == _detailToken) IsBusy = false; }
    }

    /// <summary>
    /// Copies the values a save can have changed out of the freshly-read sections and back into the list row.
    /// The row is what every later identity resolution reads — the next detail load, Revert, and the instant
    /// summary all address the group by <c>primarySmtpAddress</c> — and an alias change can move that address.
    /// Without this the row keeps pointing at an address that no longer names the group.
    /// </summary>
    private void SyncRowFromSections(CloudObjectRow row)
    {
        var byKey = Sections.SelectMany(s => s.Properties)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        void Copy(string rowKey, string propertyKey)
        {
            // The pane writes an em dash where there is no value. It is a placeholder, and a stale address in
            // the row is less wrong than a placeholder that resolves to nothing.
            if (byKey.TryGetValue(propertyKey, out var v) && !string.IsNullOrWhiteSpace(v) && v != "—")
                row.Values[rowKey] = v;
        }
        Copy("primarySmtpAddress", "primaryAddress");
        Copy("alias", "alias");
        Copy("groupType", "groupType");
        Copy("hiddenFromAddressLists", "hiddenFromAddressLists");
        Copy("externalSenders", "externalSenders");
        Copy("joinRestriction", "joinRestriction");
        row.NotifyValuesChanged(); // the grid columns bind through the indexer
    }

    /// <summary>
    /// Fetches size, item counts and last logon on demand. Kept off the open path deliberately: this reads the
    /// mailbox store rather than the directory, handles one mailbox per call, and fanning it across a list is
    /// the documented way to exhaust the Exchange throttling budget.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadUsage))]
    private async Task LoadUsageAsync()
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.Mailbox || IsBusy) return;
        var identity = MailboxIdentityFor(row) ?? row.Get("primarySmtpAddress");
        if (string.IsNullOrWhiteSpace(identity)) return;

        // The generation counter, not just the row reference: re-selecting the SAME row rebuilds Sections, so a
        // late result would otherwise append to a pane that has already been torn down and rebuilt.
        var token = ++_detailToken;
        IsBusy = true;
        try
        {
            var usage = await _exchange.GetMailboxUsageAsync(identity, _mailboxExchangeGuid);
            if (token != _detailToken || !ReferenceEquals(_currentTarget, row)) return; // superseded
            UnwireSections();
            Sections.Add(usage);
            WireSections();
            SelectSection(Sections.Count - 1);
            HasUsage = true;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"Could not read mailbox usage for '{identity}': {ex.Message}");
            if (token == _detailToken) Status = "Could not read size and usage: " + ex.Message;
        }
        finally { if (token == _detailToken) IsBusy = false; }
    }

    /// <summary>Gated on the pane being idle as well as the figures being unread, so the button reflects what it
    /// will actually do instead of looking live while a load is in flight.</summary>
    private bool CanLoadUsage() => IsMailbox && !HasUsage && !IsBusy;

    partial void OnHasUsageChanged(bool value) => LoadUsageCommand.NotifyCanExecuteChanged();
    partial void OnIsMailboxChanged(bool value) => LoadUsageCommand.NotifyCanExecuteChanged();

    // --- Cloud user write actions (confirm first) ---

    [RelayCommand] private Task EnableAsync() => SetEnabledAsync(true);
    [RelayCommand] private Task DisableAsync() => SetEnabledAsync(false);

    private async Task SetEnabledAsync(bool enabled)
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.User) return;
        var verb = enabled ? "Enable" : "Disable";
        if (!_dialogs.Confirm($"{verb} account", $"{verb} the cloud account “{row.DisplayName}”?",
                new[] { $"{verb} {row.DisplayName}" }))
            return;
        IsBusy = true;
        try
        {
            await _graph.SetUserAccountEnabledAsync(row.Id, enabled);
            Status = $"{verb}d {row.DisplayName}.";
            SetTarget(row); // re-read live state
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Cloud user enable/disable failed.", ex);
            Status = $"{verb} failed: " + ex.Message;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeSessionsAsync()
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.User) return;
        if (!_dialogs.Confirm("Revoke sessions", $"Revoke all sign-in sessions for “{row.DisplayName}”?",
                new[] { "Invalidates refresh tokens — the user must sign in again everywhere." }))
            return;
        IsBusy = true;
        try
        {
            await _graph.RevokeSignInSessionsAsync(row.Id);
            Status = $"Revoked sign-in sessions for {row.DisplayName}.";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Revoke sessions failed.", ex);
            Status = "Revoke failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    // --- License management (users only; prefer group-based licensing) ---

    /// <summary>Assigns one or more directly-chosen license SKUs, nudging toward group membership where it exists.</summary>
    [RelayCommand]
    private async Task AssignLicenseAsync()
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.User) return;

        // Microsoft requires a usage location before any license assignment.
        var usage = !string.IsNullOrWhiteSpace(_usageLocation) ? _usageLocation : row.Get("usageLocation");
        if (string.IsNullOrWhiteSpace(usage))
        {
            _dialogs.Alert("Assign license",
                "Set the user's Usage location first (in the properties above, then Save) — Microsoft requires it before a license can be assigned.");
            return;
        }

        IReadOnlyList<CloudSku> all;
        IsBusy = true;
        try { all = await _graph.GetSubscribedSkusAsync(); }
        catch (Exception ex) { IsBusy = false; Status = "Couldn't read tenant SKUs: " + ex.Message; return; }
        IsBusy = false;

        // Offer SKUs the user doesn't already hold directly (a group-only SKU can still be added directly).
        var heldDirect = new HashSet<Guid>(Licenses.Where(l => l.HasDirect).Select(l => l.SkuId));
        var candidates = all.Where(s => !heldDirect.Contains(s.SkuId)).ToList();
        if (candidates.Count == 0)
        {
            _dialogs.Alert("Assign license", "The user already holds every available license directly.");
            return;
        }

        var picked = _dialogs.PickLicenses($"Assign licenses to “{row.DisplayName}”", candidates);
        if (picked is null || picked.Count == 0) return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var sku in picked)
        {
            // Group-first guardrail: when a group already grants this SKU, recommend group membership.
            if (sku.HasGroupAssignment)
            {
                var lines = new List<string> { "This license is granted by group-based licensing via:" };
                lines.AddRange(sku.AssigningGroups.Select(g => "• " + g));
                lines.Add("Adding the user to one of those groups is preferred over a direct assignment.");
                lines.Add("Continue with a DIRECT assignment anyway?");
                if (!_dialogs.Confirm("Prefer group membership?", $"“{sku.FriendlyName}” is available via a group", lines))
                {
                    items.Add(new BulkItemResult(sku.SkuId.ToString(), sku.FriendlyName, false, "Skipped — use group membership instead"));
                    continue;
                }
            }
            try { await _graph.AssignLicenseToUserAsync(row.Id, sku.SkuId); items.Add(new BulkItemResult(sku.SkuId.ToString(), sku.FriendlyName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(sku.SkuId.ToString(), sku.FriendlyName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // re-read live license state
    }

    /// <summary>Removes the selected directly-assigned licenses; group-inherited ones are skipped (change the group).</summary>
    [RelayCommand]
    private async Task RemoveLicensesAsync(System.Collections.IList? selected)
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.User) return;

        var lics = selected?.Cast<CloudLicense>().ToList() ?? new List<CloudLicense>();
        if (lics.Count == 0) { _dialogs.Alert("Remove license", "Select one or more licenses to remove."); return; }

        var removable = lics.Where(l => l.CanRemoveDirectly).ToList();
        var inheritedOnly = lics.Where(l => l.IsInheritedOnly).ToList();
        if (removable.Count == 0)
        {
            _dialogs.Alert("Remove license",
                "The selected license(s) are inherited from a group and can't be removed here — remove the user from the assigning group instead (see the “Assigned via” column).");
            return;
        }

        var heading = removable.Count == 1
            ? $"Remove this license from “{row.DisplayName}”?"
            : $"Remove {removable.Count} licenses from “{row.DisplayName}”?";
        var lines = removable.Select(l => l.FriendlyName).ToList();
        if (inheritedOnly.Count > 0)
            lines.Add($"(skipping {inheritedOnly.Count} group-inherited license(s) — remove via the group)");

        // Guardrail: unlicensing a REGULAR mailbox deletes it after ~30 days; a shared mailbox survives. If we can
        // determine (best-effort) the user still has a regular mailbox, warn prominently in the confirm dialog.
        await AddMailboxGuardrailAsync(row, lines);

        if (!_dialogs.Confirm("Remove license", heading, lines)) return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var l in removable)
        {
            try { await _graph.RemoveLicenseFromUserAsync(row.Id, l.SkuId); items.Add(new BulkItemResult(l.SkuId.ToString(), l.FriendlyName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(l.SkuId.ToString(), l.FriendlyName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // re-read live license state
    }

    /// <summary>Best-effort: if the user still has a REGULAR mailbox, append a strong caution to the confirm
    /// lines — removing an Exchange-providing license from a regular mailbox deletes it after ~30 days, whereas
    /// a shared mailbox survives unlicensed. Skipped silently when Exchange can't be reached.</summary>
    private async Task AddMailboxGuardrailAsync(CloudObjectRow row, List<string> lines)
    {
        if (!_exchange.IsConfigured || !_graph.IsSignedIn) return; // no connectable Exchange session to check with
        var mailboxId = MailboxIdentityFor(row);
        if (mailboxId is null) return;
        try
        {
            var mb = await _exchange.GetMailboxAsync(mailboxId);
            if (mb is not null && mb.Type == MailboxType.Regular)
            {
                lines.Add(string.Empty);
                lines.Add("⚠ This user still has a REGULAR mailbox. If a removed license provides Exchange Online, "
                        + "the mailbox will be DELETED after ~30 days. Convert it to a shared mailbox first "
                        + "(ExOL tab ▸ Convert to Shared) to keep it after unlicensing.");
            }
        }
        catch (Exception ex) { AppLog.Instance.Warn("License guardrail: couldn't check the mailbox type: " + ex.Message); }
    }

    private static string? MailboxIdentityFor(CloudObjectRow row)
    {
        var upn = row.Get("userPrincipalName");
        if (!string.IsNullOrWhiteSpace(upn)) return upn;
        var mail = row.Get("mail");
        return string.IsNullOrWhiteSpace(mail) ? null : mail;
    }

    // --- Cloud group membership (users + devices; add/remove this object to/from Entra groups) ---

    /// <summary>Adds this object to one or more picked Entra groups.</summary>
    [RelayCommand]
    private async Task AddToGroupsAsync()
    {
        var row = _currentTarget;
        if (row is null || !(row.Kind == CloudObjectKind.User || row.Kind == CloudObjectKind.Device)) return;

        var picked = _dialogs.PickCloudGroups($"Add “{row.DisplayName}” to Entra groups");
        if (picked is null || picked.Count == 0) return;
        if (!_dialogs.Confirm("Add to groups", $"Add “{row.DisplayName}” to {picked.Count} group(s)?",
                picked.Select(g => $"{g.GroupKind}: {g.DisplayName}")))
            return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var g in picked)
        {
            // Membership of dynamic groups (rule-managed) and on-prem-synced groups can't be set directly.
            if (string.Equals(g.MembershipKind, "Dynamic", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkItemResult(g.Id, g.DisplayName, false, "Dynamic group — membership is rule-managed by Entra; can't add directly."));
                continue;
            }
            if (string.Equals(g.Origin, "Synced", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkItemResult(g.Id, g.DisplayName, false, "Synced from on-prem AD — manage this membership in Active Directory."));
                continue;
            }
            items.Add(await ChangeMembershipAsync(g, row, add: true));
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // refresh memberships
    }

    /// <summary>Removes this object from the selected Entra groups (synced groups are on-prem-mastered → reported).</summary>
    [RelayCommand]
    private async Task RemoveFromGroupsAsync(System.Collections.IList? selected)
    {
        var row = _currentTarget;
        if (row is null || !(row.Kind == CloudObjectKind.User || row.Kind == CloudObjectKind.Device)) return;

        var groups = selected?.Cast<CloudGroup>().ToList() ?? new List<CloudGroup>();
        if (groups.Count == 0) { _dialogs.Alert("Remove from groups", "Select one or more groups to remove."); return; }

        var heading = groups.Count == 1
            ? $"Remove “{row.DisplayName}” from this group?"
            : $"Remove “{row.DisplayName}” from these {groups.Count} groups?";
        var lines = groups.Select(g => $"{g.GroupKind}: {g.DisplayName}").ToList();
        if (groups.Any(g => string.Equals(g.Origin, "Synced", StringComparison.OrdinalIgnoreCase)))
            lines.Add("Note: membership of a group synced from on-prem AD is mastered on-prem and can't be removed in the cloud.");
        if (groups.Any(g => string.Equals(g.MembershipKind, "Dynamic", StringComparison.OrdinalIgnoreCase)))
            lines.Add("Note: dynamic-group membership is rule-managed by Entra and can't be removed directly.");
        if (!_dialogs.Confirm("Remove from groups", heading, lines)) return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var g in groups)
        {
            if (string.Equals(g.MembershipKind, "Dynamic", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkItemResult(g.Id, g.DisplayName, false, "Dynamic group — membership is rule-managed by Entra; can't remove directly."));
                continue;
            }
            if (string.Equals(g.Origin, "Synced", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkItemResult(g.Id, g.DisplayName, false, "Synced from on-prem AD — manage this membership in Active Directory."));
                continue;
            }
            items.Add(await ChangeMembershipAsync(g, row, add: false));
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // refresh memberships
    }

    /// <summary>
    /// Adds or removes one object's membership of one cloud group, through whichever backend actually owns that
    /// group. Distribution lists and mail-enabled security groups are read-only in Microsoft Graph — it answers
    /// their membership with 403 — so those route to Exchange, which addresses both sides by SMTP rather than by
    /// directory object id.
    /// </summary>
    private async Task<BulkItemResult> ChangeMembershipAsync(CloudGroup group, CloudObjectRow row, bool add)
    {
        try
        {
            if (group.IsExchangeManaged)
            {
                if (string.IsNullOrWhiteSpace(group.Mail))
                    return new BulkItemResult(group.Id, group.DisplayName, false,
                        "This group is managed through Exchange Online but has no email address, so Exchange can't address it.");

                var member = MailboxIdentityFor(row);
                if (string.IsNullOrWhiteSpace(member))
                    return new BulkItemResult(group.Id, group.DisplayName, false,
                        $"“{row.DisplayName}” has no email address, and Exchange addresses members by address. Devices can't be members of a distribution group.");

                if (add) await _exchange.AddDistributionGroupMemberAsync(group.Mail!, member!);
                else await _exchange.RemoveDistributionGroupMemberAsync(group.Mail!, member!);
                return new BulkItemResult(group.Id, group.DisplayName, true, null);
            }

            if (add) await _graph.AddMemberToGroupAsync(group.Id, row.Id);
            else await _graph.RemoveMemberFromGroupAsync(group.Id, row.Id);
            return new BulkItemResult(group.Id, group.DisplayName, true, null);
        }
        catch (Exception ex)
        {
            // ExchangeService already humanizes its failures before throwing, so running them through
            // ExchangeErrors again duplicates the guidance sentence. Graph exceptions still need translating.
            var friendly = group.IsExchangeManaged ? ex.Message : GraphErrors.Friendly(ex);
            return new BulkItemResult(group.Id, group.DisplayName, false, friendly);
        }
    }

    private static CloudListMode ModeFor(CloudObjectKind kind, CloudObjectSource source)
    {
        // A distribution list and an Entra security group are both CloudObjectKind.Group, and they are
        // described by different column sets, so the kind alone cannot pick the headers.
        if (kind == CloudObjectKind.Group && source == CloudObjectSource.Exchange) return CloudListMode.DistributionGroups;
        return kind switch
        {
            CloudObjectKind.Group => CloudListMode.Groups,
            CloudObjectKind.Device => CloudListMode.Devices,
            // Without this arm a mailbox would borrow the Entra user headers, and every mailbox-specific key
            // would miss the lookup and be labelled with its raw camelCase name.
            CloudObjectKind.Mailbox => CloudListMode.Mailboxes,
            _ => CloudListMode.Users,
        };
    }
}
