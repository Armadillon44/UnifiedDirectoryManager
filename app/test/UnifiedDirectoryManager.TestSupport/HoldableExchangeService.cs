using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// An <see cref="InertExchangeService"/> whose distribution-group read and write park until the test
/// releases them.
///
/// The real channel serialises every call through one gate, so a save and the re-read that follows it queue
/// behind whatever else is in flight — which is exactly the window in which the operator clicks another
/// row. That window is not reachable against a live tenant on any schedule a test can rely on, so it is
/// reproduced here.
///
/// Everything not overridden still throws by name (see <see cref="InertExchangeService"/>).
/// </summary>
public sealed class HoldableExchangeService : InertExchangeService
{
    private readonly List<TaskCompletionSource<IReadOnlyList<CloudPropertySection>>> _detailWaiters = new();

    public override bool IsConfigured => true;
    public override bool IsConnected => true;

    /// <summary>When true, <see cref="GetDistributionGroupDetailAsync"/> parks until released.</summary>
    public bool Hold { get; set; }

    /// <summary>Identities the detail read was asked for, in order.</summary>
    public List<string> DetailReads { get; } = new();

    /// <summary>Identities the write was addressed to, in order.</summary>
    public List<string> Writes { get; } = new();

    /// <summary>Sections a released read hands back. Swap between releases to model two different groups.</summary>
    public List<CloudPropertySection> Sections { get; } = new();

    public int PendingDetails => _detailWaiters.Count;

    /// <summary>Completes the oldest parked read with whatever <see cref="Sections"/> holds right now.</summary>
    public bool ReleaseDetail()
    {
        if (_detailWaiters.Count == 0) return false;
        var tcs = _detailWaiters[0];
        _detailWaiters.RemoveAt(0);
        tcs.SetResult(Sections.ToList());
        return true;
    }

    /// <summary>Fails the oldest parked read, so the Failed outcome can be told from the Superseded one.</summary>
    public bool FailDetail(string message = "Exchange said no")
    {
        if (_detailWaiters.Count == 0) return false;
        var tcs = _detailWaiters[0];
        _detailWaiters.RemoveAt(0);
        tcs.SetException(new InvalidOperationException(message));
        return true;
    }

    public override Task<IReadOnlyList<CloudPropertySection>> GetDistributionGroupDetailAsync(
        string identity, CancellationToken cancellationToken = default)
    {
        DetailReads.Add(identity);
        if (!Hold) return Task.FromResult((IReadOnlyList<CloudPropertySection>)Sections.ToList());
        var tcs = new TaskCompletionSource<IReadOnlyList<CloudPropertySection>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _detailWaiters.Add(tcs);
        return tcs.Task;
    }

    /// <summary>Accepts every change (nothing reported as already matching) and records who it was sent to.</summary>
    public override Task<IReadOnlyList<string>> SetDistributionGroupPropertiesAsync(
        string identity, IReadOnlyList<CloudProperty> changes, CancellationToken cancellationToken = default)
    {
        Writes.Add(identity);
        return Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
    }
}
