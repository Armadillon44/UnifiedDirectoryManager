using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Basic properties for an OU / container: its name, the object's DN in both standard AD formats
/// (LDAP distinguished name + canonical name), its description, and the editable "protect from accidental
/// deletion" flag (an ACL change applied via <see cref="ChangeOp.Protect"/>/<see cref="ChangeOp.Unprotect"/>).
/// </summary>
public partial class OuPropertiesViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly string _dn;
    private bool _originalProtected;
    private string _originalDescription = string.Empty;
    /// <summary>Description values beyond the first (the dialog shows/edits only the first, but description is
    /// multi-valued in the schema) — carried through a save so unseen values aren't silently destroyed.</summary>
    private IReadOnlyList<string> _descriptionExtras = Array.Empty<string>();
    private bool _loaded;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _distinguishedName = string.Empty;
    [ObservableProperty] private string _canonicalName = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _protectFromDeletion;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True once the original state has been read — gates Save so a write can never precede the load
    /// (which would otherwise risk toggling protection from a default value).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] private bool _isLoaded;

    /// <summary>Editable-fields gate: loaded and not mid-save. Disabling the inputs during the async write closes
    /// the window where a toggle/edit could desync the saved baseline or be silently overwritten on completion.</summary>
    public bool CanEdit => IsLoaded && !IsBusy;

    public OuPropertiesViewModel(IDirectoryService directory, string distinguishedName, string name)
    {
        _directory = directory;
        _dn = distinguishedName;
        _name = name;
        _distinguishedName = distinguishedName;
        _canonicalName = CanonicalFromDn(distinguishedName); // shown immediately; replaced by the authoritative value on load
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        IsBusy = true;
        Status = "Loading…";
        try
        {
            var info = await _directory.GetBasicInfoAsync(_dn);
            if (!string.IsNullOrWhiteSpace(info.Name)) Name = info.Name;
            DistinguishedName = info.DistinguishedName;
            CanonicalName = string.IsNullOrWhiteSpace(info.CanonicalName) ? CanonicalFromDn(info.DistinguishedName) : info.CanonicalName;
            Description = info.Description ?? string.Empty;
            _originalDescription = Description;
            _descriptionExtras = info.DescriptionValues.Count > 1 ? info.DescriptionValues.Skip(1).ToList() : Array.Empty<string>();

            try { _originalProtected = await _directory.GetDeletionProtectionAsync(_dn); }
            catch { _originalProtected = false; } // unreadable DACL: show as unprotected rather than failing the load
            ProtectFromDeletion = _originalProtected;

            _loaded = true;
            IsLoaded = true;
            Status = string.Empty;
        }
        catch (Exception ex) { Status = "Could not load properties: " + DirectoryService.Friendly(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!_loaded) return;

        // Snapshot the intended values before the async round-trip so a toggle/edit during it can't desync the
        // baseline from what was actually written. Collapse newlines — description is single-line by convention.
        var protect = ProtectFromDeletion;
        var desc = Regex.Replace(Description, @"[\r\n]+", " ").Trim();

        var changes = new List<PendingChange>();

        // Description (compared trimmed so pure-whitespace edits don't trigger a spurious write). Carry any extra
        // (unshown) values through, so editing/clearing the visible value never silently drops them.
        if (!string.Equals(desc, _originalDescription.Trim(), StringComparison.Ordinal))
        {
            var values = new List<string>();
            if (desc.Length > 0) values.Add(desc);
            values.AddRange(_descriptionExtras);
            changes.Add(values.Count == 0
                ? new PendingChange { Op = ChangeOp.Clear, LdapName = "description", FriendlyName = "Description" }
                : new PendingChange { Op = ChangeOp.Set, LdapName = "description", FriendlyName = "Description", Values = values });
        }

        if (protect != _originalProtected)
            changes.Add(new PendingChange { Op = protect ? ChangeOp.Protect : ChangeOp.Unprotect });

        if (changes.Count == 0) { Status = "No changes to apply."; return; }

        IsBusy = true;
        try
        {
            await _directory.ApplyChangesAsync(_dn, changes);
            _originalProtected = protect;
            _originalDescription = desc;
            Description = desc; // reflect the saved (normalized) value (the field is disabled during the save)
            Status = "Changes saved.";
        }
        catch (Exception ex) { Status = "Save failed: " + DirectoryService.Friendly(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>Best-effort DN → canonical-name conversion (domain first, then the OU/CN path root-most first),
    /// used only as a fallback when AD doesn't return the constructed <c>canonicalName</c>.</summary>
    private static string CanonicalFromDn(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return string.Empty;
        var rdns = Regex.Split(dn.Trim(), @"(?<!\\),").Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        var domain = string.Join('.', rdns.Where(r => r.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(r => r[3..]));
        var path = rdns.Where(r => !r.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                       .Select(r => r.Contains('=') ? r[(r.IndexOf('=') + 1)..] : r)
                       .Reverse()
                       .ToList();
        return path.Count > 0 ? $"{domain}/{string.Join('/', path)}" : domain;
    }
}
