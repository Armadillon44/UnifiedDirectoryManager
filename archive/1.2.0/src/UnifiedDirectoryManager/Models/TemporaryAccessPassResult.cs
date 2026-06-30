namespace UnifiedDirectoryManager.Models;

/// <summary>
/// The outcome of issuing a Temporary Access Pass (TAP) on a cloud user. The <see cref="Pass"/> code
/// is returned only at creation time by Graph — it can never be read back — so callers must surface it
/// to the operator immediately (copy/relay) and not persist it.
/// </summary>
public sealed record TemporaryAccessPassResult(
    string Pass,
    DateTimeOffset? StartDateTime,
    int? LifetimeInMinutes,
    bool IsUsableOnce);
