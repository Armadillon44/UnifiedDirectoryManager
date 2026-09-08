namespace UnifiedDirectoryManager.Services;

/// <summary>
/// The paging loop behind every "read the whole list" call, extracted so it can be tested on its own.
///
/// It exists because a single-page read is the quietest bug this app can have: the first page comes back,
/// looks like a complete answer, and nothing anywhere says otherwise. That shipped once — cloud group
/// membership silently stopped at 200 — and the destructive "remove all cloud groups" step iterated the
/// short list and reported success.
///
/// Keeping the loop here rather than inline in <see cref="GraphService"/> is deliberate. Inline, the only
/// way to exercise it is to stand up an HTTP-level fake of the Graph SDK; as a delegate over a fetch
/// function, the parts that are OURS to get wrong — does it stop, does it drop the last page, what does it
/// do when the pages never end — are testable with a few lines and no network.
/// </summary>
internal static class PageDrain
{
    /// <summary>One page of results: the items, and the link to the next page (null/empty when last).</summary>
    internal readonly record struct Page<T>(IEnumerable<T>? Items, string? NextLink);

    /// <summary>
    /// Reads pages until there are no more, and returns every item.
    /// </summary>
    /// <param name="fetch">
    /// Fetches a page. Receives null for the first page, then whatever <see cref="Page{T}.NextLink"/> the
    /// previous page carried.
    /// </param>
    /// <param name="maxPages">
    /// Runaway guard. Reaching it THROWS rather than returning what has been read: handing back a partial
    /// list silently is the exact defect this loop exists to prevent, so a loud failure is the lesser harm.
    /// </param>
    /// <param name="describe">
    /// Builds the message for that failure, given the number of items read so far. Takes the count rather
    /// than computing one from page size, because a page size is a maximum the service may undershoot —
    /// quoting a number nobody measured is how an error message starts lying.
    /// </param>
    internal static async Task<List<T>> DrainAsync<T>(
        Func<string?, CancellationToken, Task<Page<T>>> fetch,
        int maxPages,
        Func<int, string> describe,
        CancellationToken cancellationToken = default)
    {
        if (fetch is null) throw new ArgumentNullException(nameof(fetch));
        if (maxPages < 1) throw new ArgumentOutOfRangeException(nameof(maxPages), "At least one page must be allowed.");

        var all = new List<T>();
        string? next = null;

        for (var pages = 1; ; pages++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await fetch(next, cancellationToken).ConfigureAwait(false);
            if (page.Items is not null) all.AddRange(page.Items);

            // No continuation: that was the last page, and `all` is the complete answer.
            if (string.IsNullOrEmpty(page.NextLink)) return all;

            // A service that keeps handing back the same link would otherwise spin forever.
            if (string.Equals(page.NextLink, next, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "refusing to return a partial list: the service repeated the same continuation link, so "
                    + $"paging cannot advance. {describe(all.Count)}");

            if (pages >= maxPages)
                throw new InvalidOperationException("refusing to return a partial list: " + describe(all.Count));

            next = page.NextLink;
        }
    }
}
