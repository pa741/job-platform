using System.Text.Json;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>One stored draft, with what it was generated from.</summary>
public sealed record StoredApplication(
    long Id,
    long PostingId,
    string PostingTitle,
    string? Company,
    int Revision,
    string CurriculumVitaeMarkdown,
    string CoverLetterMarkdown,
    IReadOnlyList<string> Emphasised,
    string? Instructions,
    string? Model,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// The generated CVs and cover letters.
/// </summary>
/// <remarks>
/// Like <see cref="CandidateProfileRepository"/>, every method is scoped to a profile id the
/// caller has already proved is theirs, and there is no method that resolves a document by its
/// id alone. A generated CV contains someone's entire employment history; an endpoint that
/// could be talked into returning a stranger's is not a bug that should be possible to write.
/// </remarks>
public sealed class ApplicationDocumentRepository(JobsDbContext db)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Stores a draft as the next revision for this pair.
    /// </summary>
    /// <remarks>
    /// The revision is read and incremented rather than counted, so a regeneration after a
    /// deletion does not reuse a number that has already been handed to the candidate.
    /// </remarks>
    public async Task<StoredApplication> AddAsync(
        long profileId,
        long postingId,
        ApplicationDraft draft,
        string? instructions,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var previous = await db.ApplicationDocuments
            .Where(d => d.ProfileId == profileId && d.PostingId == postingId)
            .MaxAsync(d => (int?)d.Revision, ct);

        var entity = new ApplicationDocumentEntity
        {
            ProfileId = profileId,
            PostingId = postingId,
            Revision = (previous ?? 0) + 1,
            CurriculumVitaeMarkdown = draft.CurriculumVitaeMarkdown,
            CoverLetterMarkdown = draft.CoverLetterMarkdown,
            EmphasisedJson = JsonSerializer.Serialize(draft.Emphasised, Json),
            Instructions = instructions,
            Model = draft.Model,
            WriterVersion = draft.Version,
            CreatedAtUtc = now,
        };

        db.ApplicationDocuments.Add(entity);
        await db.SaveChangesAsync(ct);

        var posting = await db.JobPostings
            .AsNoTracking()
            .Where(p => p.Id == postingId)
            .Select(p => new { p.Title, p.Company })
            .FirstAsync(ct);

        return Map(entity, posting.Title, posting.Company);
    }

    /// <summary>One draft, by id, provably belonging to this profile.</summary>
    public async Task<StoredApplication?> GetAsync(
        long profileId, long documentId, CancellationToken ct = default)
    {
        var row = await db.ApplicationDocuments
            .AsNoTracking()
            .Where(d => d.Id == documentId && d.ProfileId == profileId)
            .Select(d => new { Entity = d, d.Posting!.Title, d.Posting.Company })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : Map(row.Entity, row.Title, row.Company);
    }

    /// <summary>
    /// This candidate's drafts, newest first.
    /// </summary>
    /// <remarks>
    /// The markdown is excluded. A list of thirty drafts carrying two full documents each is
    /// megabytes of response for a page that shows titles and dates - the same reasoning that
    /// keeps <c>Description</c> out of <c>PostingSummary</c>.
    /// </remarks>
    public async Task<IReadOnlyList<StoredApplication>> ListAsync(
        long profileId, int limit, CancellationToken ct = default)
        => await db.ApplicationDocuments
            .AsNoTracking()
            .Where(d => d.ProfileId == profileId)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(limit)
            .Select(d => new StoredApplication(
                d.Id,
                d.PostingId,
                d.Posting!.Title,
                d.Posting.Company,
                d.Revision,
                string.Empty,
                string.Empty,
                new List<string>(),
                d.Instructions,
                d.Model,
                d.CreatedAtUtc))
            .ToListAsync(ct);

    private static StoredApplication Map(ApplicationDocumentEntity entity, string title, string? company)
        => new(
            entity.Id,
            entity.PostingId,
            title,
            company,
            entity.Revision,
            entity.CurriculumVitaeMarkdown ?? string.Empty,
            entity.CoverLetterMarkdown ?? string.Empty,
            Deserialize(entity.EmphasisedJson),
            entity.Instructions,
            entity.Model,
            entity.CreatedAtUtc);

    private static IReadOnlyList<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
