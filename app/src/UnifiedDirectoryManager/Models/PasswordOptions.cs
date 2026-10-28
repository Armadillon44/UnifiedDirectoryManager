namespace UnifiedDirectoryManager.Models;

/// <summary>
/// The two password options shown on the Account tab.
/// </summary>
/// <remarks>
/// Shown side by side but stored in completely different places. <see cref="NeverExpires"/> is the
/// DONT_EXPIRE_PASSWORD flag in userAccountControl. <see cref="CannotChange"/> is a permission on the
/// user object — deny ACEs for the Change Password right on Everyone and SELF — because the matching
/// userAccountControl bit cannot be set by writing userAccountControl.
/// </remarks>
public sealed record PasswordOptions(bool NeverExpires, bool CannotChange);
