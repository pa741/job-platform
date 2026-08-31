using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Data.Sql;

/// <summary>
/// Writes one model extraction back against its posting.
/// </summary>
/// <remarks>
/// <b>One implementation, because there are two callers and they must not drift.</b> The queue
/// consumer applies extractions that came back in seconds; the batch collector applies ones that
/// came back a day later. Everything after "the model answered" is identical, and it was
/// duplicated - which is how the mention collision below reached production twice over.
///
/// Only the <see cref="AssertionSource.Model"/> rows are replaced. The board-supplied and
/// text-matched assertions are different evidence produced by a different pass, and this one has
/// no business overwriting them - the same separation that puts <c>Source</c> in
/// <c>PostingConcepts</c>' primary key.
/// </remarks>
public sealed class PostingExtractionWriter(
    JobsDbContext db, ILogger<PostingExtractionWriter>? logger = null)
{
    /// <summary>
    /// Records the extraction and rewrites this posting's model-sourced rows.
    /// </summary>
    /// <param name="inputHash">
    /// The hash of the text that was actually sent. Supplied by the caller rather than computed
    /// here, because the batch path submits and collects a day apart and the posting may have
    /// been re-listed with different text in between.
    /// </param>
    public async Task ApplyAsync(
        long postingId,
        string inputHash,
        DocumentExtraction extraction,
        IReadOnlyDictionary<string, int> conceptIds,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(conceptIds);

        await db.PostingConcepts
            .Where(c => c.PostingId == postingId && c.Source == AssertionSource.Model)
            .ExecuteDeleteAsync(ct);

        await db.PostingMentions
            .Where(m => m.PostingId == postingId && m.Reason == MentionReason.UnknownModelSkill)
            .ExecuteDeleteAsync(ct);

        // What survived that delete: forms recorded by another pass, which own the same primary
        // key this one is about to insert into.
        //
        // PostingMentions is keyed on (PostingId, SurfaceForm) and deliberately not on Reason -
        // a mention answers "this advert said a word we could not place", and one row per word
        // per posting is what the vocabulary growth loop wants to count. But the delete above is
        // scoped to model rows, so a form the board already flagged as unresolvable survives it
        // and then collides: "Violation of PRIMARY KEY constraint 'PK_PostingMentions' ...
        // duplicate key value is (2123, SharePoint)". Both passes failing to resolve SharePoint
        // is one fact, not two, so the surviving row stands and the model's is dropped.
        var survivingForms = await db.PostingMentions
            .Where(m => m.PostingId == postingId)
            .Select(m => m.SurfaceForm)
            .ToListAsync(ct);

        var taken = survivingForms.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // (PostingId, ExtractorVersion, InputHash) is unique, and it is the idempotency key
        // this whole design leans on: a posting re-listed with unchanged text is extracted once,
        // and a replayed message converges rather than duplicating. The queue consumer checks
        // for this before it calls the model, but the collector cannot - by then the money is
        // already spent - and a batch left open by a failure between SaveChanges and its status
        // write is collected again on the next tick. So the guard belongs here, where both
        // callers get it.
        var alreadyRecorded = await db.PostingExtractions.AnyAsync(
            e => e.PostingId == postingId
                && e.ExtractorVersion == extraction.Version
                && e.InputHash == inputHash,
            ct);

        if (!alreadyRecorded)
        {
            db.PostingExtractions.Add(new PostingExtractionEntity
            {
                PostingId = postingId,
                ExtractorVersion = extraction.Version,
                InputHash = inputHash,
                Model = extraction.Model,
                ExtractedAtUtc = now,
                PayloadJson = extraction.PayloadJson,
            });
        }

        // Keys the model returned that the SQL projection has no row for. Counted rather than
        // merely skipped: the vocabulary ships in the build and these tables are a projection of
        // it, so this is not "the model invented a key" - KernelDocumentExtractor already refuses
        // those - it is "concepts.json is ahead of the database". That happens for exactly one
        // reason, and it has a one-line fix.
        List<string>? unseeded = null;

        foreach (var assertion in extraction.Concepts)
        {
            if (!conceptIds.TryGetValue(assertion.ConceptKey, out var conceptId))
            {
                (unseeded ??= []).Add(assertion.ConceptKey);
                continue;
            }

            db.PostingConcepts.Add(new PostingConceptEntity
            {
                PostingId = postingId,
                ConceptId = conceptId,
                Source = AssertionSource.Model,
                Polarity = assertion.Polarity,
                YearsMin = assertion.YearsMin,
                YearsMax = assertion.YearsMax,
                EvidenceText = assertion.EvidenceText,
                Confidence = assertion.Confidence,
                ResolverVersion = extraction.Version,
            });
        }

        foreach (var mention in extraction.Mentions)
        {
            // Case-insensitively, and against what is already stored as well as what this loop
            // has added: a model that returns "SharePoint" and "sharepoint" in one response
            // would otherwise collide with itself.
            if (!taken.Add(mention.SurfaceForm))
            {
                continue;
            }

            db.PostingMentions.Add(new PostingMentionEntity
            {
                PostingId = postingId,
                SurfaceForm = mention.SurfaceForm,
                Reason = mention.Reason,
                Occurrences = mention.Occurrences,
                ResolverVersion = extraction.Version,
            });
        }

        // Warned rather than swallowed, because the failure is silent in the worst way: the
        // model-sourced rows are deleted above and then simply not rewritten, so the posting
        // loses assertions it had and the pass reports success. A corpus-wide reparse run before
        // seeding would do that to every posting naming a newly added concept.
        if (unseeded is { Count: > 0 })
        {
            logger?.LogWarning(
                "Posting {PostingId}: {Count} concept key(s) the model returned have no row in "
                + "the Concepts table and were dropped: {Keys}. concepts.json is ahead of the "
                + "database - run `dbadmin seed-concepts` and apply this extraction again.",
                postingId,
                unseeded.Count,
                string.Join(", ", unseeded.Take(UnseededKeysLogged))
                    + (unseeded.Count > UnseededKeysLogged ? ", ..." : string.Empty));
        }
    }

    /// <summary>How many unseeded keys to name before truncating. Enough to see the pattern.</summary>
    private const int UnseededKeysLogged = 10;

    /// <summary>Concept keys to their ids, for resolving what the model returned.</summary>
    /// <remarks>
    /// Read once per batch by the caller rather than per posting: it is the whole concept table,
    /// and fetching it for each of several hundred postings would be the round trip that turns a
    /// backfill into a bill.
    /// </remarks>
    public Task<Dictionary<string, int>> GetConceptIdsAsync(CancellationToken ct = default)
        => db.Concepts
            .AsNoTracking()
            .Select(c => new { c.ConceptKey, c.Id })
            .ToDictionaryAsync(c => c.ConceptKey, c => c.Id, StringComparer.Ordinal, ct);
}
