using System.Text.Json;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// Where the rendered files for one draft were stored. <b>References, never bytes.</b>
/// </summary>
/// <remarks>
/// <b>Named members rather than positional, for the reason <c>SubmissionEvidence</c> has them.</b>
/// Four nullable strings in a row take a transposed CV path and cover-letter path without a word
/// from the compiler, and the mistake is invisible afterwards because both values are
/// plausible-looking paths nobody re-reads - until somebody downloads a CV and gets a covering
/// letter. Named members also keep the type projectable from EF: an object initialiser is a
/// member-init node the provider translates.
///
/// <b>A null member means "nothing to say about this file", never "clear the one on the row".</b>
/// The two renders and the hash arrive from steps that fail independently - a DOCX backend can
/// throw where the PDF did not - so a partial record is the ordinary case rather than an error.
/// Clearing a reference is the one thing this must never do by accident: the blob still exists,
/// was paid for, and can never be found again, with nothing in the row admitting it. That is
/// <see cref="SubmissionLimits.MaxScreenshotRefLength"/>'s argument restated on the write path.
/// </remarks>
public sealed record RenderedDocuments
{
    /// <summary>Where the rendered CV was stored. A path, never a signed URL.</summary>
    /// <remarks>
    /// A user-delegation SAS expires, and an expired URL stored beside a document is a dead
    /// pointer that still looks live. Whoever serves the pack mints a fresh short-lived one from
    /// this path - which is also why nothing in this layer talks to Azure: a repository that
    /// minted URLs would need the container and the identity, and the reference is the only half
    /// of that which belongs in SQL.
    /// </remarks>
    public string? CvBlobPath { get; init; }

    /// <summary>Where the DOCX rendering was stored. A second file, not a second document.</summary>
    public string? CvDocxBlobPath { get; init; }

    /// <summary>Where the rendered cover letter was stored.</summary>
    public string? CoverLetterBlobPath { get; init; }

    /// <summary>SHA-256 of the CV bytes as rendered, lower-case hex.</summary>
    /// <remarks>
    /// Over the rendered file rather than over the markdown, because that is the version an
    /// employer read: a renderer change moves the bytes without moving a character of the source.
    /// It is what makes the blob checkable against this row afterwards - a path alone cannot say
    /// whether the file at the end of it is still the one that was sent.
    /// </remarks>
    public string? CvSha256 { get; init; }

    /// <summary>Whether anything was actually rendered.</summary>
    /// <remarks>
    /// Asked rather than inferred, so the write path can skip a round trip that would change no
    /// column - the same reason <c>SubmissionEvidence.IsEmpty</c> exists. Blank counts as nothing:
    /// a renderer that returned <c>""</c> stored no file.
    /// </remarks>
    public bool IsEmpty
        => string.IsNullOrWhiteSpace(CvBlobPath)
            && string.IsNullOrWhiteSpace(CvDocxBlobPath)
            && string.IsNullOrWhiteSpace(CoverLetterBlobPath)
            && string.IsNullOrWhiteSpace(CvSha256);
}

/// <summary>One stored draft, with what it was generated from and what was rendered from it.</summary>
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
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<DraftedAnswer> DraftedAnswers,
    RenderedDocuments Rendered);

/// <summary>
/// Whether one posting has documents, and how far they got.
/// </summary>
/// <remarks>
/// <b>The answer for a page of postings, from one read.</b> The queue projection needs a
/// <c>hasDocuments</c> flag per row and the shortlist needs a <c>documentsReady</c> filter; asking
/// either posting by posting is a round trip each against a database billed on wall-clock time,
/// which is the cost this codebase avoids everywhere else. See
/// <see cref="ApplicationDocumentRepository.GetAvailabilityAsync"/> on where that join belongs
/// when the caller is a query rather than a materialised page.
///
/// <b>It describes the latest revision and nothing older.</b> A regeneration supersedes what came
/// before it, so "documents are ready" has to mean the documents the pack would actually hand
/// over; answering from an older revision's render would promise a file for a draft nobody would
/// send. Named members for the reason <see cref="RenderedDocuments"/> has them - two bools side by
/// side are a transposition nothing would catch.
/// </remarks>
public sealed record DocumentAvailability
{
    /// <summary>The posting these documents were written for.</summary>
    public required long PostingId { get; init; }

    /// <summary>The newest revision, which is the one the pack serves.</summary>
    /// <remarks>
    /// Carried rather than merely counted, because it is what an outcome is correlated against: a
    /// rejection means something different for a first draft than for the rewrite that replaced
    /// it.
    /// </remarks>
    public required int Revision { get; init; }

    /// <summary>Whether that revision has a rendered CV to hand an upload box.</summary>
    public bool HasRenderedCv { get; init; }

    /// <summary>Whether that revision has a rendered cover letter.</summary>
    public bool HasRenderedCoverLetter { get; init; }
}

/// <summary>
/// The generated CVs and cover letters.
/// </summary>
/// <remarks>
/// Like <see cref="CandidateProfileRepository"/>, every method is scoped to a profile id the
/// caller has already proved is theirs, and there is no method that resolves a document by its
/// id alone. A generated CV contains someone's entire employment history; an endpoint that
/// could be talked into returning a stranger's is not a bug that should be possible to write.
///
/// <b>It stores references and never bytes, and it never talks to Azure.</b> The markdown is the
/// record and a rendering of it lives in a blob; this table holds the path to that blob. Putting
/// the file here would put megabytes into a database billed by the second and billed again on
/// every read, and putting a blob client here would drag a container name and an identity into the
/// layer whose whole job is rows. Minting a short-lived URL from a stored path is the API's step,
/// and it has to be: a stored URL expires while the row carrying it goes on looking live.
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
    public Task<StoredApplication> AddAsync(
        long profileId,
        long postingId,
        ApplicationDraft draft,
        string? instructions,
        DateTimeOffset now,
        CancellationToken ct = default)
        => AddAsync(profileId, postingId, draft, instructions, draftedAnswers: null, now, ct);

    /// <summary>
    /// Stores a draft together with the free-text answers drafted in the same call.
    /// </summary>
    /// <remarks>
    /// <b>The answers arrive with the documents rather than through a setter of their own, and
    /// that is deliberate.</b> They come out of the same model call - the advert, the profile, the
    /// gap list and the emphasise list are already in that prompt, so drafting them costs a few
    /// hundred output tokens against work already paid for - and they are assertions made in the
    /// voice of <i>this</i> revision's CV. A second write would leave a window in which a revision
    /// exists whose answers do not, and would let a later call attach answers to a document
    /// written from a different argument. One row, one generation, one set of assertions.
    ///
    /// <b>Written through <see cref="DraftedAnswerCatalog.Serialise"/> rather than through this
    /// class's own options.</b> The category round-trips by <i>name</i>, and that depends on the
    /// converter attribute on the enum and on the serializer options together; a second
    /// <c>JsonSerializerOptions</c> here would look identical and would quietly start writing
    /// numbers, so a member inserted later would reinterpret every answer already stored.
    ///
    /// Nothing to store is stored as null. The catalogue reads null and <c>"[]"</c> back the same
    /// way, so there is no third state to get wrong, and a null column is the cheaper of the two
    /// ways to say nothing.
    /// </remarks>
    public async Task<StoredApplication> AddAsync(
        long profileId,
        long postingId,
        ApplicationDraft draft,
        string? instructions,
        IReadOnlyList<DraftedAnswer>? draftedAnswers,
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
            DraftedAnswersJson = draftedAnswers is null or { Count: 0 }
                ? null
                : DraftedAnswerCatalog.Serialise(draftedAnswers),
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

    /// <summary>
    /// Records where this revision's rendered files were stored. False where it is not theirs.
    /// </summary>
    /// <remarks>
    /// <b>A second write to the row, and the only one, because rendering cannot happen at
    /// generation time.</b> A renderer reads the markdown this row already holds and writes to a
    /// path that names the document it rendered, so the row has to exist first. That is the whole
    /// of the exception: nothing else here edits a stored draft, and the markdown, the revision
    /// and the emphasise list are never touched again.
    ///
    /// <b>The revision's meaning is untouched.</b> This attaches files to the revision they were
    /// rendered from; it never supersedes one, never renumbers, and never moves a reference from
    /// one revision to another - which is what keeps the revision usable as the thing an outcome
    /// is correlated against. A re-render of the same revision overwrites, which is right rather
    /// than lossy: the path is derived from the document, so the blob was overwritten too, and a
    /// row keeping the old hash would claim the file at that path is something it is not.
    ///
    /// <b>Null leaves what is already there alone.</b> See <see cref="RenderedDocuments"/>:
    /// erasing a reference loses a file that exists rather than a value that can be recomputed, so
    /// this write is deliberately incapable of it.
    ///
    /// <b>Validation runs before the row is looked up</b>, so a malformed reference is refused the
    /// same way whether or not the document happened to exist. These paths are built by this
    /// system and typed by nobody, so a bad one is a bug in the caller and should read as one.
    /// </remarks>
    public async Task<bool> RecordRenderedAsync(
        long profileId,
        long documentId,
        RenderedDocuments rendered,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendered);

        var cv = Reference(rendered.CvBlobPath, nameof(RenderedDocuments.CvBlobPath));
        var docx = Reference(rendered.CvDocxBlobPath, nameof(RenderedDocuments.CvDocxBlobPath));
        var letter = Reference(rendered.CoverLetterBlobPath, nameof(RenderedDocuments.CoverLetterBlobPath));
        var hash = Hash(rendered.CvSha256);

        // Tracked deliberately - the one query in this repository that means to write back what
        // it reads - and said out loud rather than left to the default, because the API host once
        // set NoTracking globally and a read-then-mutate under that saves nothing and throws
        // nothing. Resolved through the caller's profile id, so a document id from a route or from
        // a model's argument cannot attach a file to a stranger's draft.
        var entity = await db.ApplicationDocuments
            .AsTracking()
            .Where(d => d.Id == documentId && d.ProfileId == profileId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
        {
            return false;
        }

        if (rendered.IsEmpty)
        {
            return true;
        }

        entity.CvBlobPath = cv ?? entity.CvBlobPath;
        entity.CvDocxBlobPath = docx ?? entity.CvDocxBlobPath;
        entity.CoverLetterBlobPath = letter ?? entity.CoverLetterBlobPath;
        entity.CvSha256 = hash ?? entity.CvSha256;

        await db.SaveChangesAsync(ct);

        return true;
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

    /// <summary>The newest draft for one posting, whole, or null where none has been written.</summary>
    /// <remarks>
    /// By posting rather than by document id, because that is the question the agent surface asks:
    /// "what am I sending for this job". The highest revision wins - a regeneration supersedes
    /// what came before it, and handing an agent an older draft than the candidate last looked at
    /// would put a document in front of an employer that nobody chose.
    /// </remarks>
    public async Task<StoredApplication?> GetLatestForPostingAsync(
        long profileId, long postingId, CancellationToken ct = default)
    {
        var row = await db.ApplicationDocuments
            .AsNoTracking()
            .Where(d => d.PostingId == postingId && d.ProfileId == profileId)
            .OrderByDescending(d => d.Revision)
            .Select(d => new { Entity = d, d.Posting!.Title, d.Posting.Company })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : Map(row.Entity, row.Title, row.Company);
    }

    /// <summary>
    /// Which of these postings have documents, and whether those documents have been rendered.
    /// </summary>
    /// <remarks>
    /// <b>One read for a whole page, because the alternative is a probe per posting.</b> A queue
    /// row needs to say whether documents exist and a person looking at a shortlist needs the same
    /// answer for every row on it; asked one at a time that is a round trip each against a database
    /// billed on wall-clock time.
    ///
    /// <b>Where the caller is a query rather than a page, the join belongs inside that query.</b> A
    /// <c>documentsReady</c> filter on <c>ListApplyableAsync</c> cannot be served from here at all:
    /// it has to run before the bound, or a page of fifty is quietly filtered down to whatever
    /// survived - the failure this codebase has already had twice. That filter, and the
    /// <c>hasDocuments</c> flag in the same projection, are a
    /// <c>db.ApplicationDocuments.Any(...)</c> subquery written out in <c>JobMatchRepository</c>,
    /// beside the channel rules that are written out twice for the same reason. This method is for
    /// callers holding materialised rows - the pack, the dashboard - and handing them an
    /// <c>IQueryable</c> or a shared predicate instead would be an expression tree nobody can read.
    ///
    /// <b>Folded in memory rather than grouped in SQL</b>, deliberately. The set is one page of
    /// postings and four narrow values per revision, so the transfer is trivial; a <c>GroupBy</c>
    /// would have to aggregate "the rendered state of the row with the highest revision", which is
    /// not an aggregate at all and is exactly the shape EF compiles and then fails to translate at
    /// runtime.
    ///
    /// Postings with no documents are absent rather than present-and-false. A caller asking about
    /// fifty and getting one entry back has its answer in the shape it wanted; forty-nine empty
    /// records would be a second way of saying the same thing.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, DocumentAvailability>> GetAvailabilityAsync(
        long profileId,
        IReadOnlyCollection<long> postingIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(postingIds);

        long[] ids = [.. postingIds.Distinct()];

        if (ids.Length == 0)
        {
            // No query rather than an IN with nothing in it. An empty page is the ordinary case at
            // the end of a queue and it should not cost a round trip.
            return new Dictionary<long, DocumentAvailability>();
        }

        var rows = await db.ApplicationDocuments
            .AsNoTracking()
            .Where(d => d.ProfileId == profileId && ids.Contains(d.PostingId))
            .Select(d => new
            {
                d.PostingId,
                d.Revision,
                HasCv = d.CvBlobPath != null,
                HasCoverLetter = d.CoverLetterBlobPath != null,
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.PostingId)
            .Select(g => g.OrderByDescending(r => r.Revision).First())
            .ToDictionary(
                r => r.PostingId,
                r => new DocumentAvailability
                {
                    PostingId = r.PostingId,
                    Revision = r.Revision,
                    HasRenderedCv = r.HasCv,
                    HasRenderedCoverLetter = r.HasCoverLetter,
                });
    }

    /// <summary>
    /// This candidate's drafts, newest first.
    /// </summary>
    /// <remarks>
    /// The markdown is excluded, and so are the drafted answers, which are prose on the same
    /// scale. A list of thirty drafts carrying two whole documents and five paragraphs each is
    /// megabytes of response for a page that shows titles and dates - the same reasoning that
    /// keeps <c>Description</c> out of <c>PostingSummary</c>. <b>An empty
    /// <see cref="StoredApplication.DraftedAnswers"/> here means "not projected", never "none were
    /// drafted"</b>, exactly as the empty markdown does; the single-row reads answer that question.
    ///
    /// The rendered references stay, because they are what a list is actually asked - which of
    /// these has a file ready - and they are bounded columns rather than documents.
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
                d.CreatedAtUtc,
                new List<DraftedAnswer>(),
                new RenderedDocuments
                {
                    CvBlobPath = d.CvBlobPath,
                    CvDocxBlobPath = d.CvDocxBlobPath,
                    CoverLetterBlobPath = d.CoverLetterBlobPath,
                    CvSha256 = d.CvSha256,
                }))
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
            entity.CreatedAtUtc,
            // Never throws on what is stored. A candidate looking at their own documents must not
            // get a 500 because one column was written by a build that has since changed shape -
            // the rule this repository already follows for EmphasisedJson, and the one
            // JobMatchRepository.Read follows for every JSON column on a match.
            DraftedAnswerCatalog.Deserialise(entity.DraftedAnswersJson),
            new RenderedDocuments
            {
                CvBlobPath = entity.CvBlobPath,
                CvDocxBlobPath = entity.CvDocxBlobPath,
                CoverLetterBlobPath = entity.CoverLetterBlobPath,
                CvSha256 = entity.CvSha256,
            });

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

    /// <summary>
    /// One stored path, trimmed, or null where there is nothing to record.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than trimmed to the column's width</b>, which inverts what
    /// <c>SubmissionRepository.Bound</c> does to free text, and that inversion is the point.
    /// Truncating a sentence costs readability and a reader can see it stop short; truncating a
    /// pointer costs the thing pointed at - a file that exists, was paid for and can never be found
    /// again, while the row goes on carrying something that still looks like a reference.
    /// <see cref="SubmissionLimits.MaxScreenshotRefLength"/> is the storage account's own blob-name
    /// ceiling, so a longer path is one the store would have refused: it names no file that exists,
    /// and it can only have come from a bug here.
    /// </remarks>
    private static string? Reference(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= SubmissionLimits.MaxScreenshotRefLength
            ? trimmed
            : throw new ArgumentException(
                $"A blob path is at most {SubmissionLimits.MaxScreenshotRefLength} characters - the "
                + "storage account's own ceiling - so this one names no file that exists.",
                name);
    }

    /// <summary>
    /// One SHA-256, lower-cased, or null where none was captured.
    /// </summary>
    /// <remarks>
    /// The width is a fact about SHA-256 rather than a bound anybody chose, so it is spelled out
    /// here the way the hash columns spell it in <c>JobsDbContext</c>, rather than promoted to a
    /// constant that could only ever say the algorithm has not changed.
    ///
    /// <b>The shape is checked because the column is fixed-length.</b> <c>nchar(64)</c> pads a
    /// short value with spaces, so a hash of the wrong length would be stored looking like a hash
    /// and would never match the file it claims to describe - the failure this column exists to
    /// catch, arriving through the column itself. Lower-cased on the way in because every hash this
    /// system writes comes from <c>Convert.ToHexStringLower</c>, and two spellings of one value
    /// compare unequal.
    /// </remarks>
    private static string? Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToLowerInvariant();

        return trimmed.Length == 64 && trimmed.All(char.IsAsciiHexDigit)
            ? trimmed
            : throw new ArgumentException(
                "A CV hash is 64 hex characters of SHA-256. A value of any other shape is padded "
                + "into the column and never matches the file it claims to describe.",
                nameof(RenderedDocuments.CvSha256));
    }
}
