namespace UnifiedDirectoryManager.Models;

/// <summary>
/// Read-only projection of an Entra ID device. <see cref="TrustType"/> is the raw Graph value
/// ("AzureAd" = Entra joined, "ServerAd" = on-prem domain / hybrid joined, "Workplace" = registered);
/// used to correlate an on-prem computer with its hybrid-joined cloud device.
/// </summary>
public sealed record CloudDevice(
    string Id,
    string? DeviceId,
    string? DisplayName,
    string? OperatingSystem,
    string? OperatingSystemVersion,
    string? TrustType,
    bool? IsCompliant,
    bool? IsManaged,
    bool? AccountEnabled,
    DateTimeOffset? ApproximateLastSignInDateTime,
    bool? OnPremisesSyncEnabled);
