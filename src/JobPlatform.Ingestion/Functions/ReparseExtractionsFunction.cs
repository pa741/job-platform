using JobPlatform.Ai.Extraction;
using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Applies a parser or vocabulary change to the whole corpus without calling a model.
/// </summary>
/// <remarks>
/// <b>This exists because <c>PostingExtractions.PayloadJson</c> keeps the model's raw answer.</b>
/// The expensive half of extraction is asking; parsing what came back is a string and some
/// dictionary lookups. So a change to <c>ExtractionPrompt.Parse</c>, or an addition to
/// <c>concepts.json</c> that lets a previously unknown key resolve, can reach every posting
/// already read for the price of a query.
///
/// Measured on 2026-08-31: 5,822 postings have been read, at roughly 1,700 tokens each, so
/// re-extracting the corpus is about **10 million tokens**. Re-parsing it is free. The first use
/// was the change that resolves the model's <c>unknownSkills</c> through the graph, which turned
/// "AI" in 89 postings, "machine learning" in 52 and "generative AI" in 43 from unresolved
/// mentions into assertions the matcher can see.
///
/// <b>It deliberately does not bump <c>DocumentExtraction.CurrentVersion</c>, and nothing here
/// should.</b> That constant means "the stored answer is stale and must be asked for again", and
/// a parser change does not make the answer stale - it makes the reading of it better. Bumping it
/// would mark 5,822 rows for re-extraction and leave a ten-million-token bill for whoever next
/// ran the backfill, to buy nothing. This pass is explicit and idempotent instead: run it when
/// the parser or the vocabulary changes, run it twice if you are unsure, and it converges.
///
/// <b>What it cannot fix</b> is the model's own choice of key. If the model never mentioned a
/// technology at all, no amount of re-reading its answer will invent one - that genuinely needs
/// re-extraction, and it is the only thing that does.
/// </remarks>
public sealed class ReparseExtractionsFunction(
    JobsDbContext db,
    PostingExtractionWriter writer,
    TimeProvider time,
    ILogger<ReparseExtractionsFunction> logger)
{
    /// <summary>How many stored extractions to read in one page.</summary>
    /// <remarks>
    /// Payloads are unbounded text, so this is a memory bound rather than a round-trip one. A
    /// hundred of them is a few megabytes; the whole corpus at once would not be.
    /// </remarks>
    private const int PageSize = 100;

    /// <summary>
    /// Wall-clock budget for one HTTP invocation.
    /// </summary>
    /// <remarks>
    /// The platform allows roughly 230 seconds and a bound with no margin is a 504, which -
    /// as the reprocess endpoint learned - returns nothing the caller can resume from. This pass
    /// resumes from a posting id in the response rather than from a continuation token, so a 504
    /// costs only the page in flight, but the margin is kept anyway.
    /// </remarks>
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(150);

    [Function(nameof(ReparseExtractionsFunction))]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reparse-extractions")]
        HttpRequest request,
        CancellationToken ct)
    {
        var body = await RequestBody.ReadAsync<ReparseRequest>(request, ct);

        return new OkObjectResult(await ReparseAsync(body?.AfterPostingId ?? 0, ct));
    }

    /// <param name="AfterPostingId">
    /// Resume point: the <c>NextPostingId</c> from the previous call's response. Zero starts
    /// again from the beginning, which is safe - the pass is idempotent - and slow.
    /// </param>
    public sealed record ReparseRequest(long? AfterPostingId);

    private async Task<ReparseSummary> ReparseAsync(long afterPostingId, CancellationToken ct)
    {
        var started = time.GetTimestamp();

        // The whole concept table, once. Per posting it would be the round trip that turns a
        // corpus pass into a bill - the same reason the backfill reads it once per batch.
        var conceptIds = await writer.GetConceptIdsAsync(ct);

        int read = 0, rewritten = 0, unparseable = 0;
        var cursor = afterPostingId;
        var exhausted = false;

        while (time.GetElapsedTime(started) < RequestBudget)
        {
            // Posting ids first, then their extractions. Two queries rather than one because
            // a posting can hold SEVERAL rows at the current extractor version - the key is
            // (PostingId, ExtractorVersion, InputHash), so an advert re-listed with edited text
            // has one row per text - and taking a page of rows would process such a posting
            // twice. That is not merely wasteful: the second pass through would rewrite its
            // assertions from whichever InputHash came last in the page, which may be the older
            // reading of older text.
            var ids = await db.PostingExtractions
                .AsNoTracking()
                .Where(e => e.PostingId > cursor
                    && e.PayloadJson != null
                    && e.ExtractorVersion == DocumentExtraction.CurrentVersion)
                .Select(e => e.PostingId)
                .Distinct()
                .OrderBy(id => id)
                .Take(PageSize)
                .ToListAsync(ct);

            if (ids.Count == 0)
            {
                exhausted = true;
                break;
            }

            var rows = await db.PostingExtractions
                .AsNoTracking()
                .Where(e => ids.Contains(e.PostingId)
                    && e.PayloadJson != null
                    && e.ExtractorVersion == DocumentExtraction.CurrentVersion)
                .Select(e => new { e.PostingId, e.InputHash, e.PayloadJson, e.Model, e.ExtractedAtUtc })
                .ToListAsync(ct);

            // The newest reading of the newest text, one per posting. Ordered in memory: the
            // grouping is over a page of a hundred rows, and EF's translation of a grouped
            // First() is the kind of thing that compiles and then fails at runtime.
            var page = rows
                .GroupBy(r => r.PostingId)
                .Select(g => g.OrderByDescending(r => r.ExtractedAtUtc).First())
                .OrderBy(r => r.PostingId)
                .ToList();

            foreach (var row in page)
            {
                cursor = row.PostingId;
                read++;

                // A payload that will not parse is a stored answer nobody can use. Counted and
                // skipped rather than thrown on: one bad row must not stop a corpus pass, and the
                // posting keeps whatever assertions it already had.
                if (StoredExtraction.Reparse(row.PayloadJson, row.Model) is not { } extraction)
                {
                    unparseable++;
                    logger.LogWarning(
                        "Posting {PostingId}: stored extraction payload will not parse.",
                        row.PostingId);
                    continue;
                }

                await writer.ApplyAsync(
                    row.PostingId, row.InputHash, extraction, conceptIds, time.GetUtcNow(), ct);

                // Saved per posting, not per page, and this is not a style choice. The writer's
                // delete runs through ExecuteDelete and commits immediately while its inserts wait
                // for a save - so a pass that batches the save and then throws has deleted every
                // touched posting's model rows and written none of them back. That is exactly what
                // the first run of this pass did before it was fixed. Per posting the window is
                // one row wide, and the pass is idempotent, so re-running closes it.
                await db.SaveChangesAsync(ct);

                // The tracker would otherwise accumulate every row written - tens of thousands by
                // the end of a budget - and a key collision between two postings' entities then
                // surfaces as an exception naming PostingConceptEntity, a long way from the loop
                // that caused it. Nothing here reads back what it wrote.
                db.ChangeTracker.Clear();

                rewritten++;
            }
        }

        logger.LogInformation(
            "Reparse: {Rewritten} posting(s) rewritten of {Read} read, {Unparseable} unparseable. "
            + "{State} at posting {Cursor}.",
            rewritten, read, unparseable, exhausted ? "Finished" : "Stopped", cursor);

        return new ReparseSummary(read, rewritten, unparseable, exhausted ? null : cursor, exhausted);
    }

    /// <param name="Read">Stored extractions examined.</param>
    /// <param name="Rewritten">Postings whose derived rows were rebuilt.</param>
    /// <param name="Unparseable">Payloads that would not parse. Should be zero.</param>
    /// <param name="NextPostingId">Pass back as <c>afterPostingId</c> to continue. Null when done.</param>
    /// <param name="Finished">True when the pass reached the end of the corpus.</param>
    public sealed record ReparseSummary(
        int Read, int Rewritten, int Unparseable, long? NextPostingId, bool Finished);
}
