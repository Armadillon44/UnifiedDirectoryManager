namespace UnifiedDirectoryManager.Models;

/// <summary>
/// Where a row in a membership list stands relative to the directory: as loaded, or changed on screen and
/// waiting for Save.
/// </summary>
/// <remarks>
/// Membership changes used to be written the moment they were confirmed, after which the whole pane
/// reloaded — discarding any property edit that had not been saved yet. They now wait for Save like every
/// other edit, and this is how a row says so.
/// </remarks>
public enum PendingMembership
{
    /// <summary>As it is in the directory.</summary>
    None,
    /// <summary>Added on screen; written on Save.</summary>
    Adding,
    /// <summary>Removed on screen; written on Save.</summary>
    Removing,
}

/// <summary>Display text for a pending state, shared so the three lists say it the same way.</summary>
public static class PendingMembershipText
{
    public static string Label(PendingMembership state) => state switch
    {
        PendingMembership.Adding => "Adding (unsaved)",
        PendingMembership.Removing => "Removing (unsaved)",
        _ => string.Empty,
    };
}
