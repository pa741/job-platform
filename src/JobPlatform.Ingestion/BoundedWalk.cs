namespace JobPlatform.Ingestion;

/// <summary>One page of a listing: its items, and the token that resumes at the page after it.</summary>
/// <remarks>
/// Deliberately not <c>Azure.Page&lt;T&gt;</c>. Nothing about the bound depends on the item being
/// a blob or the listing being Azure's, and a walk that needs a storage account to run is a walk
/// that cannot be asserted exactly — the same reason <c>MatchScorer</c> and
/// <c>MetricsCalculator</c> take records rather than a database.
/// </remarks>
public readonly record struct WalkPage<T>(IReadOnlyList<T> Items, string? ContinuationToken);

/// <param name="ResumeToken">
/// Where a caller should carry on. Null means either "start from the beginning" or "there is
/// nothing left" — <see cref="Exhausted"/> is what tells those apart, and the two must never be
/// collapsed into a null check.
/// </param>
/// <param name="Exhausted">The listing ran out. Only then is the work actually finished.</param>
/// <param name="Processed">How many items this pass handed to the callback.</param>
/// <param name="StoppedMidPage">
/// The walk gave up part way through a page, so <see cref="ResumeToken"/> points at the start of
/// that page and the items already done in it will be handed over again. Reported so a caller
/// can log it rather than wonder why a count exceeds the container.
/// </param>
public readonly record struct WalkOutcome(
    string? ResumeToken, bool Exhausted, int Processed, bool StoppedMidPage);

/// <summary>
/// Walks a paged listing under a count bound and a wall-clock bound, resumably.
/// </summary>
/// <remarks>
/// <b>The bound has to be able to act inside a page, or it is not a bound.</b> This is the
/// correction to the first version, which checked the clock only where the continuation token is
/// accurate — at a page boundary — and was therefore committed to a whole further page every time
/// the check passed. Measured against the real container, pages of five blobs took 4s, 11s, 12s,
/// 47s and 151s: one page outlasted the entire 150s budget, so a call that checked at 149s ran to
/// roughly 225s and the gateway gave up at ~240s. A 504 carries no token, so the caller lost its
/// place and restarted the listing from the beginning.
///
/// The fix is not a smaller budget — no budget survives a page that costs more than all of it.
/// The walk stops between items and hands back the token for the <i>start</i> of the page it was
/// in, so the resume point is still a real boundary and nothing is skipped. What it costs is
/// redoing the items already done in that page, which is free here: ingestion is idempotent by
/// contract, and a blob whose content has not changed converges in about a second.
///
/// <b>It will not stop before a page has completed.</b> Bailing out of the first page would hand
/// back the token the call started with, and the next call — with a fresh clock — would stop in
/// the same place, forever. Where a single page cannot fit in the budget the only useful thing to
/// do is finish it, so the first page runs to completion and the page size is what bounds it.
/// </remarks>
public static class BoundedWalk
{
    public static async Task<WalkOutcome> RunAsync<T>(
        IAsyncEnumerable<WalkPage<T>> pages,
        string? startToken,
        int limit,
        TimeSpan budget,
        Func<TimeSpan> elapsed,
        Func<T, CancellationToken, Task> process,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(elapsed);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Where the page currently being read begins. Only advanced once a page is finished, so
        // it is always a boundary the listing can be resumed from.
        var resume = startToken;
        var processed = 0;
        var completedPages = 0;

        await foreach (var page in pages.WithCancellation(ct))
        {
            foreach (var item in page.Items)
            {
                if (completedPages > 0 && elapsed() >= budget)
                {
                    return new WalkOutcome(resume, Exhausted: false, processed, StoppedMidPage: true);
                }

                await process(item, ct);
                processed++;
            }

            completedPages++;
            resume = page.ContinuationToken;

            if (string.IsNullOrEmpty(resume))
            {
                return new WalkOutcome(null, Exhausted: true, processed, StoppedMidPage: false);
            }

            // The count bound is read here rather than between items on purpose: the page size is
            // capped at the limit by the caller, so a page cannot overshoot it, and stopping on a
            // boundary means no item is handed over twice for the sake of it.
            if (processed >= limit || elapsed() >= budget)
            {
                return new WalkOutcome(resume, Exhausted: false, processed, StoppedMidPage: false);
            }
        }

        // The listing ended without a final empty token. Nothing left either way.
        return new WalkOutcome(null, Exhausted: true, processed, StoppedMidPage: false);
    }
}
