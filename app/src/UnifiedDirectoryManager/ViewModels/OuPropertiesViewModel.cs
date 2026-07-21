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
    private bool _loaded;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _distinguishedName = string.Empty;
    [ObservableProperty] private string _canonicalName = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _protectFromDeletion;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True once the original state has been read — the Save button gates on it so a write can never
    /// precede the load (which would otherwise risk toggling protection from a default value).</summary>
    [ObservableProperty] private bool _isLoaded;

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
        if (ProtectFromDeletion == _originalProtected) { Status = "No changes to apply."; return; }
        IsBusy = true;
        try
        {
            await _directory.ApplyChangesAsync(_dn, new[]
            {
                new PendingChange { Op = ProtectFromDeletion ? ChangeOp.Protect : ChangeOp.Unprotect },
            });
            _originalProtected = ProtectFromDeletion;
            Status = ProtectFromDeletion
                ? "Protected from accidental deletion."
                : "Accidental-deletion protection removed.";
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
