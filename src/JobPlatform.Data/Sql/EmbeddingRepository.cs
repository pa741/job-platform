using JobPlatform.Core.Matching;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>One advert the embedding pass still owes a vector, with the text to embed.</summary>
/// <param name="ContentHash">Carried through so the written row records what was actually read.</param>
public sealed record PostingToEmbed(
    long PostingId, string Title, string Description, string ContentHash, int DescriptionLength);

/// <summary>
/// Every read and write of a stored vector.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="JobMatchRepository"/> even though the sweep uses both, because the
/// two answer to different lifetimes: a vector is a property of one document and survives every
/// re-score, where a match is a property of a pair and is rewritten whenever either side moves.
/// Folding them together would put the expensive blob behind the query that runs most often.
///
/// Nothing here reads a description except <see cref="GetPostingsToEmbedAsync"/>, which has to -
/// it is the one that needs the text. The staleness question is answered entirely from short
/// columns, which is what keeps a nightly pass over the whole corpus off the unbounded ones.
/// </remarks>
public sealed class EmbeddingRepository(JobsDbContext db)
{
    /// <summary>
    /// The adverts whose stored vector is missing or stale, newest first.
    /// </summary>
    /// <remarks>
    /// <b>Three ways a vector goes stale, and none of them costs a description read to detect.</b>
    /// The row can be absent, which is the common case and covers every new posting. Its
    /// <c>EmbeddingVersion</c> can be behind, which is how a change of model or dimension marks
    /// the corpus without deleting anything. Or the posting's own
    /// <see cref="JobPostingEntity.ContentHash"/> or description length can have moved, which is
    /// the same pair of signals <c>JobPostingRepository.HasMaterialChange</c> uses to decide an
    /// advert was edited rather than merely re-listed - read here rather than judged again, so
    /// the two cannot drift.
    ///
    /// Newest first because that is the order a candidate will look in, so a pass that runs out
    /// of budget has embedded the postings that matter soonest.
    /// </remarks>
    public async Task<IReadOnlyList<PostingToEmbed>> GetPostingsToEmbedAsync(
        DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        var rows = await db.JobPostings
            .AsNoTracking()
            .Where(p => p.LastSeenUtc >= since && p.Description != null && p.Description != "")
            .Where(p => !db.PostingEmbeddings.Any(e =>
                e.PostingId == p.Id
                && e.EmbeddingVersion == EmbeddingVector.EmbeddingVersion
                && e.ContentHash == p.ContentHash
                && e.DescriptionLength == p.DescriptionLength))
            .OrderByDescending(p => p.LastSeenUtc)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Description,
                p.ContentHash,
                p.DescriptionLength,
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => new PostingToEmbed(
            r.Id, r.Title, r.Description!, r.ContentHash, r.DescriptionLength))];
    }

    /// <summary>Writes a batch of vectors, replacing whatever was there for the same postings.</summary>
    public async Task<int> UpsertPostingEmbeddingsAsync(
        IReadOnlyList<(PostingToEmbed Posting, float[] Vector)> embeddings,
        string model,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        if (embeddings.Count == 0)
        {
            return 0;
        }

        var ids = embeddings.Select(e => e.Posting.PostingId).ToList();

        var existing = await db.PostingEmbeddings
            .Where(e => ids.Contains(e.PostingId))
            .ToDictionaryAsync(e => e.PostingId, ct);

        foreach (var (posting, vector) in embeddings)
        {
            if (!existing.TryGetValue(posting.PostingId, out var entity))
            {
                entity = new PostingEmbeddingEntity { PostingId = posting.PostingId };
                db.PostingEmbeddings.Add(entity);
            }

            entity.Vector = EmbeddingVector.Pack(vector);
            entity.Dimensions = vector.Length;
            entity.Model = model;
            entity.ContentHash = posting.ContentHash;
            entity.DescriptionLength = posting.DescriptionLength;
            entity.EmbeddingVersion = EmbeddingVector.EmbeddingVersion;
            entity.EmbeddedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);

        return embeddings.Count;
    }

    /// <summary>
    /// Every current vector for the slice the sweep is scoring, by posting id.
    /// </summary>
    /// <remarks>
    /// Fetched once for the whole sweep, not once per profile: the vectors do not depend on
    /// which candidate is being scored, and re-reading a few megabytes of blob per profile is
    /// the kind of thing that turns a nightly job into a monthly bill on a database charged by
    /// wall-clock time.
    ///
    /// Rows below the current <see cref="EmbeddingVector.EmbeddingVersion"/> are left out rather
    /// than used. A vector from a different model is not a slightly worse answer to the same
    /// question - it is an answer to a different one, and mixing the two would put every pair on
    /// an incomparable scale while looking exactly like a working ranking.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, float[]>> GetPostingVectorsAsync(
        DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        var rows = await db.PostingEmbeddings
            .AsNoTracking()
            .Where(e => e.EmbeddingVersion == EmbeddingVector.EmbeddingVersion
                && e.Posting!.LastSeenUtc >= since)
            .OrderByDescending(e => e.Posting!.LastSeenUtc)
            .Take(limit)
            .Select(e => new { e.PostingId, e.Vector })
            .ToListAsync(ct);

        var vectors = new Dictionary<long, float[]>(rows.Count);

        foreach (var row in rows)
        {
            // A malformed blob is dropped rather than thrown on. The pair simply ranks without
            // its embedding axis, which is the same degradation as a posting the pass has not
            // reached yet - and a great deal better than a sweep that dies part way through
            // scoring every profile because one row is short.
            if (EmbeddingVector.Unpack(row.Vector) is { } vector)
            {
                vectors[row.PostingId] = vector;
            }
        }

        return vectors;
    }

    /// <summary>
    /// The profile's vector, or null where it is missing or was taken against older text.
    /// </summary>
    /// <remarks>
    /// <paramref name="currentInputHash"/> is the profile's own <c>ExtractionInputHash</c>, so
    /// this returns null in exactly the cases a re-extraction would also be due: the document
    /// the candidate is described by has changed. A save that edited only a phone number changes
    /// neither.
    /// </remarks>
    public async Task<float[]?> GetProfileVectorAsync(
        long profileId, string? currentInputHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currentInputHash))
        {
            return null;
        }

        var row = await db.ProfileEmbeddings
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId
                && e.EmbeddingVersion == EmbeddingVector.EmbeddingVersion
                && e.InputHash == currentInputHash)
            .Select(e => e.Vector)
            .FirstOrDefaultAsync(ct);

        return EmbeddingVector.Unpack(row);
    }

    public async Task UpsertProfileEmbeddingAsync(
        long profileId,
        float[] vector,
        string inputHash,
        string model,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var entity = await db.ProfileEmbeddings.FirstOrDefaultAsync(e => e.ProfileId == profileId, ct);

        if (entity is null)
        {
            entity = new ProfileEmbeddingEntity { ProfileId = profileId };
            db.ProfileEmbeddings.Add(entity);
        }

        entity.Vector = EmbeddingVector.Pack(vector);
        entity.Dimensions = vector.Length;
        entity.Model = model;
        entity.InputHash = inputHash;
        entity.EmbeddingVersion = EmbeddingVector.EmbeddingVersion;
        entity.EmbeddedAtUtc = now;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>How many of the slice carry a current vector, for the pass to report against.</summary>
    /// <remarks>
    /// Reported beside what was written, for the reason the sweep reports requested beside
    /// assessed: a count of rows written cannot show what is still missing, and "the pass ran"
    /// is not the same claim as "the corpus is embedded".
    /// </remarks>
    public async Task<(int Embedded, int Total)> GetCoverageAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        var total = await db.JobPostings
            .AsNoTracking()
            .CountAsync(p => p.LastSeenUtc >= since && p.Description != null && p.Description != "", ct);

        var embedded = await db.PostingEmbeddings
            .AsNoTracking()
            .CountAsync(
                e => e.EmbeddingVersion == EmbeddingVector.EmbeddingVersion
                    && e.Posting!.LastSeenUtc >= since
                    && e.Posting.Description != null
                    && e.Posting.Description != "",
                ct);

        return (embedded, total);
    }
}
