namespace UnifiedDirectoryManager.Models;

/// <summary>
/// A member of an Entra ID group. <see cref="ObjectType"/> is a friendly object class
/// ("User" / "Group" / "Device" / "Service principal" / "Contact"); <see cref="Upn"/> is populated
/// for users only.
/// </summary>
public sealed record CloudMember(string Id, string DisplayName, string? Upn, string ObjectType);
