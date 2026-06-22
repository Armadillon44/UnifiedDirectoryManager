namespace UnifiedDirectoryManager.Models;

public enum ChangeOp
{
    /// <summary>Replace the attribute's value(s).</summary>
    Set,
    /// <summary>Clear/remove the attribute entirely.</summary>
    Clear,
    /// <summary>Add the object to groups (Values = group DNs).</summary>
    AddToGroups,
    /// <summary>Remove the object from groups (Values = group DNs).</summary>
    RemoveFromGroups,
    /// <summary>Enable the account.</summary>
    Enable,
    /// <summary>Disable the account.</summary>
    Disable,
    /// <summary>Protect the object from accidental deletion (Deny-Everyone Delete/DeleteTree ACE).</summary>
    Protect,
    /// <summary>Remove accidental-deletion protection from the object.</summary>
    Unprotect,
}

/// <summary>
/// A single pending modification, used both for edit-pane saves and bulk edits.
/// LdapName carries the real attribute name; FriendlyName is for the confirmation UI only.
/// </summary>
public sealed class PendingChange
{
    public required ChangeOp Op { get; init; }

    /// <summary>lDAPDisplayName for Set/Clear; ignored for group/enable ops.</summary>
    public string LdapName { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>New values (Set) or group DNs (AddToGroups/RemoveFromGroups).</summary>
    public List<string> Values { get; init; } = new();

    /// <summary>Friendly description for data binding (delegates to <see cref="Describe"/>).</summary>
    public string Description => Describe();

    /// <summary>Friendly description shown in confirm/preview dialogs.</summary>
    public string Describe()
    {
        return Op switch
        {
            ChangeOp.Set => $"Set \"{FriendlyName}\" = {string.Join(", ", Values)}",
            ChangeOp.Clear => $"Clear \"{FriendlyName}\"",
            ChangeOp.AddToGroups => $"Add to {Values.Count} group(s)",
            ChangeOp.RemoveFromGroups => $"Remove from {Values.Count} group(s)",
            ChangeOp.Enable => "Enable account",
            ChangeOp.Disable => "Disable account",
            ChangeOp.Protect => "Protect from accidental deletion",
            ChangeOp.Unprotect => "Remove accidental-deletion protection",
            _ => Op.ToString(),
        };
    }
}
