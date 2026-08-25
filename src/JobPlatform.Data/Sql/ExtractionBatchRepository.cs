using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>One document inside an open batch, with the text hash it was submitted under.</summary>
public readonly record struct PendingBatchItem(long PostingId, string InputHash);

/// <summary>
/// The record of what has been handed to a batch provider and not yet collected.
/// </summary>
/// <remarks>
/// The whole point of this type is that a batch spans executions. Everything it does is either
/// "write down what we just sent" or "read back what we sent so the answer can be applied to the
/// right posting under the right hash".
/// </remarks>
public sealed class ExtractionBatchRepository(JobsDbContext db)
{
    /// <summary>Records a submission and the documents it carried.</summary>
    public async Task<long> RecordAsync(
        BatchSubmission submission,
        IReadOnlyList<PendingBatchItem> items,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(items);

        var batch = new ExtractionBatchEntity
        {
            ProviderBatchId = submission.ProviderBatchId,
            Model = submission.Model,
            State = BatchState.Running,
            Requested = submission.Requested,
            SubmittedAtUtc = now,
        };

        db.ExtractionBatches.Add(batch);

        foreach (var item in items)
        {
            batch.Items.Add(new ExtractionBatchItemEntity
            {
                PostingId = item.PostingId,
                InputHash = item.InputHash,
            });
        }

        await db.SaveChangesAsync(ct);

        return batch.Id;
    }

    /// <summary>
    /// Batches still waiting on the provider, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first because a batch has a completion window: the one nearest expiry is the one
    /// whose results are most at risk of being lost, so it is the one worth collecting first if
    /// a tick only gets through some of them.
    /// </remarks>
    public Task<List<string>> GetOpenAsync(int limit, CancellationToken ct = default)
        => db.ExtractionBatches
            .AsNoTracking()
            .Where(b => b.State == BatchState.Running)
            .OrderBy(b => b.SubmittedAtUtc)
            .Select(b => b.ProviderBatchId)
            .Take(limit)
            .ToListAsync(ct);

    /// <summary>
    /// What a batch carried, keyed by the correlation id handed to the provider.
    /// </summary>
    /// <remarks>
    /// The correlation id is the posting id as a string. Reading the hash back from here rather
    /// than recomputing it from the posting is the point: the description may have changed since
    /// submission, and an extraction row has to be keyed on the text that was actually read.
    /// </remarks>
    public async Task<(long BatchId, IReadOnlyDictionary<string, PendingBatchItem> Items)?> GetItemsAsync(
        string providerBatchId, CancellationToken ct = default)
    {
        var batch = await db.ExtractionBatches
            .AsNoTracking()
            .Where(b => b.ProviderBatchId == providerBatchId)
            .Select(b => new { b.Id })
            .FirstOrDefaultAsync(ct);

        if (batch is null)
        {
            return null;
        }

        var items = await db.ExtractionBatchItems
            .AsNoTracking()
            .Where(i => i.BatchId == batch.Id)
            .Select(i => new PendingBatchItem(i.PostingId, i.InputHash))
            .ToListAsync(ct);

        return (batch.Id, items.ToDictionary(
            i => i.PostingId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            i => i,
            StringComparer.Ordinal));
    }

    /// <summary>Closes a batch out with what actually came back.</summary>
    public Task CompleteAsync(
        long batchId,
        BatchState state,
        int succeeded,
        int failed,
        string? error,
        DateTimeOffset now,
        CancellationToken ct = default)
        => db.ExtractionBatches
            .Where(b => b.Id == batchId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(b => b.State, state)
                    .SetProperty(b => b.Succeeded, succeeded)
                    .SetProperty(b => b.Failed, failed)
                    .SetProperty(b => b.Error, error)
                    .SetProperty(b => b.CompletedAtUtc, (DateTimeOffset?)now),
                ct);

    /// <summary>
    /// Postings already inside an open batch.
    /// </summary>
    /// <remarks>
    /// The guard that stops a second backfill submitting the same postings again while the first
    /// is still in flight. Without it, running the endpoint twice in a day pays twice and races
    /// two collectors onto one set of rows.
    /// </remarks>
    public Task<List<long>> GetInFlightPostingIdsAsync(CancellationToken ct = default)
        => db.ExtractionBatchItems
            .AsNoTracking()
            .Where(i => i.Batch!.State == BatchState.Running)
            .Select(i => i.PostingId)
            .Distinct()
            .ToListAsync(ct);
}
