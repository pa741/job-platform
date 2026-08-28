using JobPlatform.Core.Ai;
using JobPlatform.Core.Matching;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Gives every recent advert a vector, so the sweep has something to rank with.
/// </summary>
/// <remarks>
/// <b>Its own pass rather than a third stage inside the sweep, and the reason is the failure
/// mode.</b> The sweep's two passes are cheap-then-expensive over the same pairs; this is work
/// on the posting side, shared by every profile, and it is much closer in shape to extraction
/// than to matching. Keeping it separate means an embedding provider having a bad night costs
/// the ranking axis and nothing else - scoring, assessment and the matches page all still run.
///
/// <b>03:00 UTC, half an hour ahead of the sweep.</b> After the ingest and extraction queues
/// have drained and before anything reads what it writes. The ordering is not enforced by
/// anything and does not need to be: a posting the pass has not reached simply ranks without its
/// embedding axis, and the next night picks it up.
///
/// <b>Bounded by wall clock and by attempts, both.</b> The clock is the obvious one and it is
/// what <c>BoundedWalk</c> exists for elsewhere. The attempt set is the less obvious one and it
/// is what stops a posting the provider will never accept from sitting at the head of the queue
/// being re-fetched forever - the query orders newest first, so a permanent failure is a
/// permanent first result. One attempt per posting per run; the retry belongs to the next run.
///
/// <b>And the page is widened by the number that have failed, which is the part that is easy to
/// get wrong.</b> Skipping failures in memory without asking for more rows means that once
/// enough of them accumulate at the head, a whole page comes back already-attempted and the pass
/// stops - with thousands of perfectly good adverts behind them, unembedded, and a log line
/// saying it finished.
/// </remarks>
public sealed class EmbedCorpusFunction(
    EmbeddingRepository embeddings,
    IOptions<Ai.AzureOpenAiOptions> options,
    TimeProvider time,
    ILogger<EmbedCorpusFunction> logger,
    ITextEmbedder? embedder = null)
{
    /// <summary>
    /// How far back the pass looks, and it must not be shorter than the sweep's.
    /// </summary>
    /// <remarks>
    /// Read from <see cref="MatchSweepFunction.LookbackDays"/> rather than repeated, because a
    /// number here smaller than the sweep's would leave the oldest slice of every ranking
    /// permanently unfused with nothing saying why - the pairs would score, rank on the score
    /// alone, and look exactly like pairs the embedding simply did not favour.
    /// </remarks>
    private static int LookbackDays => MatchSweepFunction.LookbackDays;

    /// <summary>Ceiling on how many adverts one nightly pass embeds.</summary>
    /// <remarks>
    /// Generous, because the marginal call is close to free - the embeddings model is priced two
    /// orders of magnitude below the chat deployments, so the whole corpus costs a few pence -
    /// and because the expensive night is the first one. After that the pass only sees what the
    /// scraper added.
    /// </remarks>
    private const int MaxPostings = 20_000;

    /// <summary>Wall-clock budget for the nightly pass.</summary>
    private static readonly TimeSpan TimerBudget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Wall-clock budget for the HTTP route.
    /// </summary>
    /// <remarks>
    /// The platform gives an HTTP trigger roughly 230 seconds, and a bound that leaves no margin
    /// is a 504 - which, as the reprocess endpoint learned, returns no continuation and loses the
    /// caller's place. This pass is resumable from the database rather than from a token, so the
    /// cost is only the batch in flight, but the margin is kept anyway.
    /// </remarks>
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(150);

    [Function(nameof(EmbedCorpusFunction))]
    public async Task RunAsync(
        [TimerTrigger("0 0 3 * * *")] TimerInfo timer, CancellationToken ct)
        => await EmbedAsync(TimerBudget, ct);

    /// <summary>
    /// The same pass, on demand.
    /// </summary>
    /// <remarks>
    /// Exists for the case the timer cannot serve, exactly as <c>run-match-sweep</c> does:
    /// somebody has just loaded a corpus, or bumped
    /// <see cref="EmbeddingVector.EmbeddingVersion"/>, and does not want to wait for 03:00. An
    /// admin route rather than a user-facing one, because it is the path that spends money.
    /// </remarks>
    [Function(nameof(RunEmbedCorpusFunction))]
    public async Task<IActionResult> RunEmbedCorpusFunction(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "run-embed-corpus")]
        HttpRequest request,
        CancellationToken ct)
        => new OkObjectResult(await EmbedAsync(RequestBudget, ct));

    private async Task<EmbedSummary> EmbedAsync(TimeSpan budget, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-LookbackDays);

        if (embedder is null)
        {
            var (already, total) = await embeddings.GetCoverageAsync(since, ct);

            logger.LogInformation(
                "Embedding pass: no AI provider configured; {Embedded} of {Total} advert(s) "
                + "already carry a vector.", already, total);

            return new EmbedSummary(0, 0, 0, already, total);
        }

        var started = time.GetTimestamp();
        var batchSize = Math.Max(1, options.Value.EmbeddingBatchSize);

        // The postings this run has already tried and not embedded. Only failures need tracking:
        // a success leaves the "needs embedding" query on its own, where a failure stays at the
        // head of it and would otherwise be re-fetched every iteration until the budget ran out.
        var failed = new HashSet<long>();

        int requested = 0, written = 0, batches = 0;

        while (time.GetElapsedTime(started) < budget && written < MaxPostings)
        {
            // Widened by the failures, so they can be skipped without skipping the work behind
            // them. Bounded by how many have actually failed rather than by how many have been
            // processed, which is what keeps the page from growing with the corpus.
            var batch = await embeddings.GetPostingsToEmbedAsync(
                since, batchSize + failed.Count, ct);

            var pending = batch.Where(p => !failed.Contains(p.PostingId)).Take(batchSize).ToList();

            if (pending.Count == 0)
            {
                break;
            }

            batches++;
            requested += pending.Count;

            var texts = pending
                .Select(p => EmbeddingText.ForAdvert(p.Title, p.Description))
                .ToList();

            var vectors = await embedder.EmbedAsync(texts, ct);

            var usable = new List<(PostingToEmbed, float[])>(pending.Count);

            for (var i = 0; i < pending.Count; i++)
            {
                if (i < vectors.Count && vectors[i] is { } vector)
                {
                    usable.Add((pending[i], vector));
                }
                else
                {
                    failed.Add(pending[i].PostingId);
                }
            }

            if (usable.Count == 0)
            {
                // The whole batch came back empty. The embedder has already recorded why in the
                // ledger, and carrying on would mean spending the rest of the budget discovering
                // the same thing - the backfill that collected HTTP 429s for an hour is exactly
                // this shape. Stop, and let the next run try again.
                logger.LogWarning(
                    "Embedding pass: batch of {Count} returned nothing usable; stopping early.",
                    pending.Count);
                break;
            }

            written += await embeddings.UpsertPostingEmbeddingsAsync(
                usable, embedder.Deployment, time.GetUtcNow(), ct);
        }

        var (embedded, corpus) = await embeddings.GetCoverageAsync(since, ct);

        // Requested beside written, and coverage beside both. A written count on its own cannot
        // show a loss, and neither count can show what is still missing - which is the question
        // somebody actually has when a ranking looks wrong.
        logger.LogInformation(
            "Embedding pass complete: {Written} vector(s) written of {Requested} requested across "
            + "{Batches} batch(es). {Embedded} of {Total} recent advert(s) now carry one.",
            written, requested, batches, embedded, corpus);

        return new EmbedSummary(batches, requested, written, embedded, corpus);
    }

    /// <param name="Batches">Model calls made.</param>
    /// <param name="Requested">Adverts sent, which is what the run cost.</param>
    /// <param name="Written">Vectors stored. Below <paramref name="Requested"/> means a loss.</param>
    /// <param name="Embedded">Recent adverts carrying a current vector, after this run.</param>
    /// <param name="Corpus">Recent adverts with a description at all, as the denominator.</param>
    public sealed record EmbedSummary(
        int Batches, int Requested, int Written, int Embedded, int Corpus);
}
