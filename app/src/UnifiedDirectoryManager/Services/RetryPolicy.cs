using System.Globalization;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// How long to keep asking a service that has answered "not yet".
/// </summary>
/// <remarks>
/// <para>
/// Creating a user on-prem and then using it in the cloud is a race against replication: Entra Connect has
/// to sync the object, Entra has to make it visible to Graph, and Exchange Online has to provision a
/// recipient for it. Each of those finishes at its own pace, and until it does the service answers with a
/// "cannot find it" that means <i>not yet</i> rather than <i>never</i>.
/// </para>
/// <para>
/// Entra and Exchange get separate policies because their per-attempt cost differs by roughly ninety
/// times. A Graph failure comes back in well under a second, so an attempt costs only the wait. An
/// Exchange call that <i>hangs</i> rather than answering costs the full Exchange operation budget — 90
/// seconds — before the wait even starts. One number governing both would either be too impatient for
/// Exchange or needlessly slow for Entra.
/// </para>
/// </remarks>
public sealed record RetryPolicy(int Attempts, int WaitSeconds)
{
    /// <summary>Fewest attempts worth offering. Below this the retry stops being a retry.</summary>
    public const int MinAttempts = 5;

    /// <summary>
    /// Most attempts worth offering. High enough to ride out a slow tenant, and paired with
    /// <see cref="MaxWaitSeconds"/> so the two cannot multiply into something absurd.
    /// </summary>
    public const int MaxAttempts = 50;

    public const int MinWaitSeconds = 5;

    /// <summary>
    /// Longest gap between attempts.
    /// </summary>
    /// <remarks>
    /// Capped at a minute deliberately. At the maximum attempt count a five-minute gap would mean over
    /// four hours of waiting on a single group, which is not a setting so much as a way to lose an
    /// afternoon. A minute keeps the worst case under an hour and still rides out the lag this exists for.
    /// </remarks>
    public const int MaxWaitSeconds = 60;

    /// <summary>What both services use unless the operator has changed it.</summary>
    public static RetryPolicy Default { get; } = new(10, 10);

    /// <summary>
    /// The policy with both values forced into range.
    /// </summary>
    /// <remarks>
    /// Clamped rather than rejected: these come from a settings file that a newer build, a text editor or
    /// a bad merge may have put anything into, and refusing to provision a user because a number is out of
    /// range would be a worse failure than quietly using the nearest sane one. Zero and negative values
    /// mean "unset", so they clamp up to the minimum rather than disabling retries altogether.
    /// </remarks>
    public RetryPolicy Clamped() => new(
        Math.Clamp(Attempts, MinAttempts, MaxAttempts),
        Math.Clamp(WaitSeconds, MinWaitSeconds, MaxWaitSeconds));

    /// <summary>Gap between attempts.</summary>
    public TimeSpan Wait => TimeSpan.FromSeconds(Math.Clamp(WaitSeconds, MinWaitSeconds, MaxWaitSeconds));

    /// <summary>
    /// Total time spent WAITING, which is one gap fewer than the attempt count — the last attempt is
    /// allowed to fail rather than being followed by another pause.
    /// </summary>
    public TimeSpan TotalWait =>
        TimeSpan.FromSeconds((Math.Clamp(Attempts, MinAttempts, MaxAttempts) - 1) * Wait.TotalSeconds);

    /// <summary>
    /// The worst case including the cost of the attempts themselves.
    /// </summary>
    /// <param name="perAttemptSeconds">
    /// How long one attempt can take before it gives up. Near zero for Graph, which fails fast; the
    /// Exchange operation budget for Exchange, which may hang instead of answering.
    /// </param>
    public TimeSpan WorstCase(double perAttemptSeconds) =>
        TotalWait + TimeSpan.FromSeconds(Math.Clamp(Attempts, MinAttempts, MaxAttempts) * perAttemptSeconds);

    /// <summary>
    /// A plain-English duration: "90 seconds", "1 min 30 s", "2 hr 5 min".
    /// </summary>
    /// <remarks>
    /// Shown live in Settings as the numbers are typed, so that the cost of a choice is never abstract.
    /// "50 attempts, 60 seconds apart" means nothing to read; "up to 49 minutes of waiting per group" is
    /// the thing an operator can actually decide about.
    /// </remarks>
    public static string Humanise(TimeSpan span)
    {
        var total = Math.Max(0, (int)Math.Round(span.TotalSeconds));
        if (total < 60) return $"{total} second{(total == 1 ? "" : "s")}";

        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;

        if (hours > 0)
            return minutes > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} hr {1} min", hours, minutes)
                : string.Format(CultureInfo.InvariantCulture, "{0} hr", hours);

        return seconds > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} min {1} s", minutes, seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0} min", minutes);
    }
}
