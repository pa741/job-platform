using JobPlatform.Core.Applications;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// What a generated application carries besides its markdown: the rendered files, the drafted
/// free-text answers, and the cheap answer to "which of these postings have documents".
/// </summary>
/// <remarks>
/// Against a real relational engine, because two of the three guarantees here are about columns
/// rather than about code. The <b>rendered references</b> are pointers into blob storage and the
/// column is fixed- or bounded-width, so "it round-trips" is a claim about the schema; the
/// <b>drafted answers</b> round-trip through a JSON column whose enum is written by name, which is
/// a property of the serializer plus the attribute on the enum and is invisible in a type check.
///
/// Three failures are what these are written against, and each has a precedent in this codebase.
/// A <b>reference silently truncated or padded</b> into its column - a file that exists, was paid
/// for, and can never be found again, with nothing in the row admitting it. An <b>enum written by
/// number</b>, so a member inserted later reinterprets every answer already stored. And a
/// <b>malformed stored column taking down the read</b>, when a candidate looking at their own
/// documents should see the answers missing rather than a 500.
/// </remarks>
public sealed class ApplicationDocumentArtefactTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const long OtherProfileId = 2;

    /// <summary>A hash of the right shape. The content is irrelevant; the shape is the assertion.</summary>
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public ApplicationDocumentArtefactTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        foreach (var (id, subject) in new[]
                 {
                     (ProfileId, "11111111-1111-1111-1111-111111111111"),
                     (OtherProfileId, "22222222-2222-2222-2222-222222222222"),
                 })
        {
            db.CandidateProfiles.Add(new CandidateProfileEntity
            {
                Id = id,
                SubjectId = subject,
                FullName = "Test Candidate",
                Email = "candidate@example.invalid",
                CreatedUtc = Now,
                UpdatedUtc = Now,
            });
        }

        for (var id = 1; id <= 4; id++)
        {
            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"linkedin:{id}",
                Site = "linkedin",
                ExternalId = id.ToString(),
                ContentHash = new string((char)('a' + id), 64),
                Title = $"Role {id}",
                Company = $"Company {id}",
                JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
                LocationCity = "London",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static ApplicationDraft Draft(string cv = "# Curriculum vitae")
        => new()
        {
            CurriculumVitaeMarkdown = cv,
            CoverLetterMarkdown = "Dear hiring manager",
            Emphasised = ["Ran the platform migration"],
            Model = "writing-deployment",
        };

    // -----------------------------------------------------------------------
    // Rendered artefacts - references only, attached to one revision
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Rendered_references_attach_to_the_revision_they_were_rendered_from()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var first = await repository.AddAsync(ProfileId, 1, Draft(), null, Now);
        var second = await repository.AddAsync(ProfileId, 1, Draft("# Rewritten"), "shorter", Now.AddHours(1));

        var recorded = await repository.RecordRenderedAsync(
            ProfileId,
            second.Id,
            new RenderedDocuments
            {
                CvBlobPath = "application-packs/1/2/Pablo_De_Groot_CV.pdf",
                CvDocxBlobPath = "application-packs/1/2/Pablo_De_Groot_CV.docx",
                CoverLetterBlobPath = "application-packs/1/2/Pablo_De_Groot_Cover_Letter.pdf",
                CvSha256 = Sha,
            });

        Assert.True(recorded);

        var latest = await repository.GetLatestForPostingAsync(ProfileId, 1);

        Assert.NotNull(latest);
        Assert.Equal(2, latest.Revision);
        Assert.Equal("application-packs/1/2/Pablo_De_Groot_CV.pdf", latest.Rendered.CvBlobPath);
        Assert.Equal("application-packs/1/2/Pablo_De_Groot_CV.docx", latest.Rendered.CvDocxBlobPath);
        Assert.Equal(Sha, latest.Rendered.CvSha256);

        // The revision below it is untouched. A render is attached to what it rendered, and the
        // revision is what an outcome is correlated against - moving a file reference onto a draft
        // that was never rendered would answer "which CV did they send" with the wrong one.
        var earlier = await repository.GetAsync(ProfileId, first.Id);

        Assert.NotNull(earlier);
        Assert.True(earlier.Rendered.IsEmpty);
    }

    [Fact]
    public async Task A_render_recorded_against_somebody_elses_document_changes_nothing()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var mine = await repository.AddAsync(ProfileId, 1, Draft(), null, Now);

        var recorded = await repository.RecordRenderedAsync(
            OtherProfileId, mine.Id, new RenderedDocuments { CvBlobPath = "packs/stranger.pdf" });

        // False rather than an exception, and indistinguishable from "no such document" - the same
        // rule the submission log applies to an id a model named.
        Assert.False(recorded);

        var untouched = await repository.GetAsync(ProfileId, mine.Id);

        Assert.NotNull(untouched);
        Assert.True(untouched.Rendered.IsEmpty);
    }

    [Fact]
    public async Task A_reference_that_was_not_rendered_leaves_the_stored_one_alone()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(ProfileId, 1, Draft(), null, Now);

        await repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvBlobPath = "packs/cv.pdf", CvSha256 = Sha });

        // The DOCX backend finishes after the PDF, and it reports only what it wrote. A null must
        // not be read as "delete the PDF": the blob exists, was paid for, and nothing else knows
        // its path.
        await repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvDocxBlobPath = "packs/cv.docx" });

        var latest = await repository.GetLatestForPostingAsync(ProfileId, 1);

        Assert.NotNull(latest);
        Assert.Equal("packs/cv.pdf", latest.Rendered.CvBlobPath);
        Assert.Equal("packs/cv.docx", latest.Rendered.CvDocxBlobPath);
        Assert.Equal(Sha, latest.Rendered.CvSha256);
        Assert.Null(latest.Rendered.CoverLetterBlobPath);
    }

    [Fact]
    public async Task A_re_render_of_one_revision_replaces_the_hash_rather_than_keeping_both()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(ProfileId, 1, Draft(), null, Now);

        await repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvBlobPath = "packs/cv.pdf", CvSha256 = Sha });

        var rerendered = new string('f', 64);

        await repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvBlobPath = "packs/cv.pdf", CvSha256 = rerendered });

        var latest = await repository.GetLatestForPostingAsync(ProfileId, 1);

        // The path is derived from the document, so a re-render overwrote the blob too. Keeping the
        // first hash would claim the file at that path is one nobody can produce any more.
        Assert.NotNull(latest);
        Assert.Equal(rerendered, latest.Rendered.CvSha256);
        Assert.Equal(1, latest.Revision);
    }

    [Fact]
    public async Task A_hash_that_is_not_a_sha256_is_refused_rather_than_padded_into_the_column()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(ProfileId, 1, Draft(), null, Now);

        // nchar(64) pads a short value with spaces, so a stored short hash still looks like a hash
        // and never matches the file it describes. Refused loudly instead - it can only be a bug
        // here, since nothing types these.
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvSha256 = "abc123" }));

        await Assert.ThrowsAsync<ArgumentException>(() => repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvSha256 = new string('z', 64) }));

        var latest = await repository.GetLatestForPostingAsync(ProfileId, 1);

        Assert.NotNull(latest);
        Assert.Null(latest.Rendered.CvSha256);
    }

    [Fact]
    public async Task A_blob_path_longer_than_the_store_allows_is_refused_rather_than_truncated()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(ProfileId, 1, Draft(), null, Now);

        var overlong = new string('p', SubmissionLimits.MaxScreenshotRefLength + 1);

        // Trimming free text loses readability; trimming a pointer loses the file. The bound is the
        // storage account's own ceiling, so a longer path names nothing that could exist.
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvBlobPath = overlong }));

        var atTheCeiling = new string('p', SubmissionLimits.MaxScreenshotRefLength);

        Assert.True(await repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvBlobPath = atTheCeiling }));

        var latest = await repository.GetLatestForPostingAsync(ProfileId, 1);

        Assert.NotNull(latest);
        Assert.Equal(atTheCeiling, latest.Rendered.CvBlobPath);
    }

    [Fact]
    public async Task The_refusal_does_not_depend_on_the_document_existing()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        // Validation runs before the row is looked up, so a malformed reference reads the same way
        // whether the caller also got the id wrong. The alternative answers "not yours" to a bug.
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RecordRenderedAsync(
            ProfileId, 9999, new RenderedDocuments { CvSha256 = "not-a-hash" }));
    }

    // -----------------------------------------------------------------------
    // Drafted answers
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Drafted_answers_round_trip_with_their_category_written_by_name()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(
            ProfileId,
            1,
            Draft(),
            null,
            [
                new DraftedAnswer("Why do you want to work at this company?", "Because of the platform work.", FreeTextCategory.PostingSpecific),
                new DraftedAnswer("How did you hear about us?", "LinkedIn", FreeTextCategory.StableFact),
            ],
            Now);

        Assert.Equal(2, stored.DraftedAnswers.Count);

        var latest = await repository.GetLatestForPostingAsync(ProfileId, 1);

        Assert.NotNull(latest);
        Assert.Equal(
            [FreeTextCategory.PostingSpecific, FreeTextCategory.StableFact],
            latest.DraftedAnswers.Select(a => a.Category));
        Assert.Equal("LinkedIn", latest.DraftedAnswers[1].Answer);

        // Read round the repository: the guarantee is about the bytes in the column, not about the
        // reader agreeing with the writer. A number here would mean a member inserted later
        // silently reinterprets every answer already stored.
        await using var raw = CreateContext();
        var json = await raw.ApplicationDocuments
            .Where(d => d.Id == latest.Id)
            .Select(d => d.DraftedAnswersJson)
            .FirstAsync();

        Assert.NotNull(json);
        Assert.Contains("PostingSpecific", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"category\":2", json);
    }

    [Fact]
    public async Task A_draft_stored_without_answers_leaves_the_column_null_rather_than_empty_json()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(ProfileId, 1, Draft(), null, [], Now);

        Assert.Empty(stored.DraftedAnswers);

        await using var raw = CreateContext();
        var json = await raw.ApplicationDocuments
            .Where(d => d.Id == stored.Id)
            .Select(d => d.DraftedAnswersJson)
            .FirstAsync();

        // Null and "[]" read back identically, so there is no third state; null is the cheaper of
        // the two ways to say nothing was drafted.
        Assert.Null(json);
    }

    [Fact]
    public async Task Drafted_answers_that_no_longer_parse_read_back_as_none()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(ProfileId, 1, Draft(), null, Now);

        await using (var corrupt = CreateContext())
        {
            var row = await corrupt.ApplicationDocuments.FirstAsync(d => d.Id == stored.Id);
            row.DraftedAnswersJson = "{ this was written by a build that has since changed shape";
            await corrupt.SaveChangesAsync();
        }

        await using var reader = CreateContext();
        var latest = await new ApplicationDocumentRepository(reader).GetLatestForPostingAsync(ProfileId, 1);

        // Stored JSON is history rather than input. A candidate looking at their own documents gets
        // the answers missing, never a 500 - and the rest of the row survives it.
        Assert.NotNull(latest);
        Assert.Empty(latest.DraftedAnswers);
        Assert.Equal("# Curriculum vitae", latest.CurriculumVitaeMarkdown);
    }

    [Fact]
    public async Task A_blank_drafted_answer_is_dropped_and_the_ones_beside_it_survive()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(
            ProfileId,
            1,
            Draft(),
            null,
            [
                new DraftedAnswer("Why this role?", "   ", FreeTextCategory.PostingSpecific),
                new DraftedAnswer("How did you hear about us?", " LinkedIn ", FreeTextCategory.StableFact),
            ],
            Now);

        // A blank typed into a form reads to an employer as an answer, so it is not one - and it is
        // not a reason to discard the answers around it either.
        var kept = Assert.Single(stored.DraftedAnswers);
        Assert.Equal("LinkedIn", kept.Answer);
    }

    // -----------------------------------------------------------------------
    // "Which of these postings have documents"
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Availability_answers_for_a_page_of_postings_from_one_read()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var rendered = await repository.AddAsync(ProfileId, 2, Draft(), null, Now);
        await repository.RecordRenderedAsync(
            ProfileId,
            rendered.Id,
            new RenderedDocuments
            {
                CvBlobPath = "packs/2/cv.pdf",
                CoverLetterBlobPath = "packs/2/letter.pdf",
                CvSha256 = Sha,
            });

        await repository.AddAsync(ProfileId, 1, Draft(), null, Now);

        var availability = await repository.GetAvailabilityAsync(ProfileId, [1, 2, 3]);

        // Posting 3 has no documents and is absent rather than present-and-false: the caller asked
        // which of these have documents, and forty-nine empty records would be a second way to say
        // the same thing.
        Assert.False(availability.ContainsKey(3));

        Assert.True(availability[1].Revision == 1);
        Assert.False(availability[1].HasRenderedCv);

        Assert.True(availability[2].HasRenderedCv);
        Assert.True(availability[2].HasRenderedCoverLetter);
    }

    [Fact]
    public async Task Availability_describes_the_latest_revision_rather_than_any_revision()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var first = await repository.AddAsync(ProfileId, 1, Draft(), null, Now);
        await repository.RecordRenderedAsync(
            ProfileId, first.Id, new RenderedDocuments { CvBlobPath = "packs/1/v1.pdf", CvSha256 = Sha });

        await repository.AddAsync(ProfileId, 1, Draft("# Rewritten"), "shorter", Now.AddHours(1));

        var availability = await repository.GetAvailabilityAsync(ProfileId, [1]);

        // The pack serves revision 2, and revision 2 has no file. Answering from revision 1's
        // render would promise an agent a document nobody would send.
        Assert.Equal(2, availability[1].Revision);
        Assert.False(availability[1].HasRenderedCv);
    }

    [Fact]
    public async Task Availability_shows_a_caller_nothing_of_somebody_elses_documents()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        await repository.AddAsync(OtherProfileId, 4, Draft(), null, Now);

        Assert.Empty(await repository.GetAvailabilityAsync(ProfileId, [4]));
        Assert.Single(await repository.GetAvailabilityAsync(OtherProfileId, [4]));

        // An empty page costs no round trip, and answers the same way.
        Assert.Empty(await repository.GetAvailabilityAsync(ProfileId, []));
    }

    // -----------------------------------------------------------------------
    // The list projection
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_list_carries_the_references_and_leaves_the_prose_out()
    {
        await using var db = CreateContext();
        var repository = new ApplicationDocumentRepository(db);

        var stored = await repository.AddAsync(
            ProfileId,
            1,
            Draft(),
            null,
            [new DraftedAnswer("Why this role?", "Because of the platform work.", FreeTextCategory.PostingSpecific)],
            Now);

        await repository.RecordRenderedAsync(
            ProfileId, stored.Id, new RenderedDocuments { CvBlobPath = "packs/1/cv.pdf", CvSha256 = Sha });

        var listed = Assert.Single(await repository.ListAsync(ProfileId, 20));

        // Two whole documents and five paragraphs each is megabytes for a page showing titles and
        // dates. The references are bounded columns and are what the page is actually asked.
        Assert.Equal(string.Empty, listed.CurriculumVitaeMarkdown);
        Assert.Empty(listed.DraftedAnswers);
        Assert.Equal("packs/1/cv.pdf", listed.Rendered.CvBlobPath);
        Assert.Equal(Sha, listed.Rendered.CvSha256);
    }
}
