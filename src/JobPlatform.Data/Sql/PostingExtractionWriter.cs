using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

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
public sealed class PostingExtractionWriter(JobsDbContext db)
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
        var alreadyRecorded = await db.PostingMentions
            .Where(m => m.PostingId == postingId)
            .Select(m => m.SurfaceForm)
            .ToListAsync(ct);

        var taken = alreadyRecorded.ToHashSet(StringComparer.OrdinalIgnoreCase);

        db.PostingExtractions.Add(new PostingExtractionEntity
        {
            PostingId = postingId,
            ExtractorVersion = extraction.Version,
            InputHash = inputHash,
            Model = extraction.Model,
            ExtractedAtUtc = now,
            PayloadJson = extraction.PayloadJson,
        });

        foreach (var assertion in extraction.Concepts)
        {
            if (!conceptIds.TryGetValue(assertion.ConceptKey, out var conceptId))
            {
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
    }

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
