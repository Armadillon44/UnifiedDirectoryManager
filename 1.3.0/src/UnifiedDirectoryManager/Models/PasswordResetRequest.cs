namespace UnifiedDirectoryManager.Models;

/// <summary>
/// The result of the reset-password dialog: the new password and the post-reset options the admin
/// chose. Carried back to the edit pane, which confirms and then calls the directory service.
/// </summary>
public sealed record PasswordResetRequest(string Password, bool MustChangeAtNextLogon, bool Unlock);
