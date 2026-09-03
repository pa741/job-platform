using System.Text;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using JobPlatform.Ingestion.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Ingestion.Tests;

/// <summary>
/// The nightly generation pass - <c>GenerateApplicationsFunction</c> - against a real engine.
/// </summary>
/// <remarks>
/// <b>Against SQLite rather than against a stubbed repository, because the selection is the
/// feature.</b> The whole argument for this pass is that it writes for the set
/// <c>ListApplyableAsync</c> returns and never for a set of its own; a test that handed it a list
/// would assert nothing about that, and would keep passing on the day the two definitions drifted.
/// So the fixture is a corpus in miniature and every exclusion is a row rather than a mock.
///
/// <b>Four things are pinned and the first is what the pass is for.</b> It writes for the
/// postings the apply loop will actually reach - assessed above the floor, with the employer's own
/// apply link, not already written for, not already applied to - in the order the loop reads them.
///
/// <b>The bound is real and it is where the money is.</b> The cap stops the batch, the pass
/// resumes from the database rather than from a token, and three consecutive writing failures stop
/// it outright. Each of those is asserted by counting calls to the writer, because a pass that
/// spent the budget and stored nothing looks identical in the row count to one that never ran.
///
/// <b>Two skips cannot be expressed in the query and are therefore the easiest to lose.</b> An
/// aggregator behind a "direct" link and an advert with no body are both stepped over after
/// materialisation, which is exactly where a filter silently shrinks a bound - so each has a test
/// that asks for a batch the skip would otherwise cut.
///
/// <b>The pack store is optional and its absence is a state, not a failure.</b> With no store the
/// draft is written and the paths stay null; with one, the paths and the CV hash land on the row.
/// </remarks>
public sealed class GenerateApplicationsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 4, 30, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const string SubjectId = "11111111-1111-1111-1111-111111111111";

    /// <summary>Best-ranked, on LinkedIn. The first document any run writes.</summary>
    private const long Best = 10;

    /// <summary>Second, and on Indeed - which is what the referral answer has to say.</summary>
    private const long Second = 11;

    /// <summary>A "direct" link that goes to another job board.</summary>
    private const long Aggregated = 12;

    /// <summary>Judged 60, below the floor a run pulls with.</summary>
    private const long Weak = 13;

    /// <summary>No employer link: the board says it hosts the application.</summary>
    private const long BoardHosted = 14;

    /// <summary>Already has a draft. The one posting in the live database that did.</summary>
    private const long Written = 15;

    /// <summary>An advert the scraper never read the body of.</summary>
    private const long Bodyless = 16;

    /// <summary>Already applied to.</summary>
    private const long Applied = 17;

    /// <summary>
    /// Third and fourth in the queue, and they exist to make a bound falsifiable.
    /// </summary>
    /// <remarks>
    /// With only two postings worth writing for, "the pass stopped after three failures" and
    /// "the pass ran out of things to try" are the same observation - so the stop would pass its
    /// test without existing. Four writable rows separate them.
    /// </remarks>
    private const long Third = 18;

    private const long Fourth = 19;

    public GenerateApplicationsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = SubjectId,
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,

            // GetProfileIdsAsync admits a profile only once something has been read out of it -
            // a profile with no concepts scores zero against everything - so a fixture without
            // this is a run with no candidates rather than a run with nothing to write.
            ExtractedAtUtc = Now.AddDays(-2),
        });

        // Every apply URL is a real vendor's shape. The detector reads hosts and query
        // parameters, so a made-up URL would assert nothing about what this pass does on the
        // corpus - and the aggregator case is the whole reason the vendor is read at all.
        Add(db, Best, "Platform Engineer", "Cloudflare", direct: "https://boards.greenhouse.io/cloudflare/jobs/1");
        Add(db, Second, "Data Engineer", "Acme", direct: "https://jobs.lever.co/acme/data", site: "indeed");
        Add(db, Aggregated, "Analyst", "Beta", direct: "https://www.whatjobs.com/job/12");
        Add(db, Weak, "Scientist", "Gamma", direct: "https://jobs.ashbyhq.com/gamma/13");
        Add(db, BoardHosted, "Architect", "Delta", offsiteApply: false);
        Add(db, Written, "Site Reliability Engineer", "Epsilon", direct: "https://apply.workable.com/epsilon/j/15");
        Add(db, Bodyless, "Developer", "Zeta", direct: "https://jobs.smartrecruiters.com/zeta/16", description: "   ");
        Add(db, Applied, "Manager", "Eta", direct: "https://boards.greenhouse.io/eta/jobs/17");
        Add(db, Third, "Consultant", "Theta", direct: "https://jobs.lever.co/theta/18");
        Add(db, Fourth, "Engineer", "Iota", direct: "https://boards.greenhouse.io/iota/jobs/19");

        // Rank and assessment disagree everywhere, so an accidental ordering by the wrong column
        // is visible rather than coincidental.
        Match(db, Best, score: 60, assessment: 95, rank: 9.0);
        Match(db, Second, score: 90, assessment: 82, rank: 8.0);
        Match(db, Aggregated, score: 55, assessment: 93, rank: 8.5);
        Match(db, Weak, score: 99, assessment: 60, rank: 7.0);
        Match(db, BoardHosted, score: 50, assessment: 92, rank: 7.5);
        Match(db, Written, score: 51, assessment: 91, rank: 7.2);
        Match(db, Bodyless, score: 52, assessment: 89, rank: 7.1);
        Match(db, Applied, score: 53, assessment: 88, rank: 7.05);
        Match(db, Third, score: 54, assessment: 81, rank: 6.9);
        Match(db, Fourth, score: 56, assessment: 80, rank: 6.8);

        // The live database's own shape: exactly one posting with documents.
        db.ApplicationDocuments.Add(new ApplicationDocumentEntity
        {
            ProfileId = ProfileId,
            PostingId = Written,
            Revision = 1,
            CurriculumVitaeMarkdown = "# CV",
            CoverLetterMarkdown = "Dear Epsilon",
            CreatedAtUtc = Now,
        });

        db.Submissions.Add(new SubmissionEntity
        {
            ProfileId = ProfileId,
            PostingId = Applied,
            Channel = SubmissionChannel.Ats,
            CreatedAtUtc = Now,
        });

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // -----------------------------------------------------------------------
    // What gets written for
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_pass_writes_for_the_postings_the_apply_loop_will_reach()
    {
        var writer = new StubWriter();

        var summary = await RunAsync(writer);

        var documented = await DocumentedAsync();

        // The four postings a run would reach, in the order it reads them. Everything else is
        // held back by a different rule, and each of those rules has a test of its own below.
        Assert.Equal([Best, Second, Third, Fourth], documented);

        Assert.Equal(4, summary.Written);
        Assert.Equal(4, summary.Considered);

        // The aggregator and the bodyless advert. Counted rather than silent: "the queue is
        // empty" and "the queue is full of things we step over" want opposite fixes.
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task The_best_ranked_posting_is_written_first()
    {
        var writer = new StubWriter();

        await RunAsync(writer, documentsPerNight: 1);

        var documented = await DocumentedAsync();

        Assert.Equal([Best], documented);
        Assert.Equal(Best, Assert.Single(writer.Postings));
    }

    [Fact]
    public async Task A_posting_that_already_has_a_draft_is_never_written_for_again()
    {
        var writer = new StubWriter();

        await RunAsync(writer);

        // Regenerating buys the document that is already there, on the deployment that costs
        // twenty-five times the other one.
        Assert.DoesNotContain(Written, writer.Postings);
        Assert.Equal(1, await RevisionsAsync(Written));
    }

    [Fact]
    public async Task A_posting_already_applied_to_is_never_written_for()
    {
        var writer = new StubWriter();

        await RunAsync(writer);

        Assert.DoesNotContain(Applied, writer.Postings);
    }

    [Fact]
    public async Task A_posting_with_no_employer_link_is_never_written_for()
    {
        var writer = new StubWriter();

        await RunAsync(writer);

        // The board says it hosts the application, so there is no upload box a tailored PDF
        // could be attached to.
        Assert.DoesNotContain(BoardHosted, writer.Postings);
    }

    [Fact]
    public async Task A_direct_link_into_another_job_board_is_skipped()
    {
        var writer = new StubWriter();

        // Asked for a batch of one. The aggregator outranks Second, so a pass that stepped over
        // it after the bound rather than before would write nothing at all here.
        var summary = await RunAsync(writer, documentsPerNight: 2);

        var documented = await DocumentedAsync();

        Assert.DoesNotContain(Aggregated, writer.Postings);
        Assert.Equal([Best, Second], documented);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public async Task An_advert_with_no_body_is_skipped_rather_than_written_generically()
    {
        var writer = new StubWriter();

        await RunAsync(writer);

        // A draft written against a blank advert is a generic CV bought at the tailored price -
        // and it would then satisfy the documents filter and take the posting out of this pass's
        // queue for good.
        Assert.DoesNotContain(Bodyless, writer.Postings);
        Assert.Equal(0, await RevisionsAsync(Bodyless));
    }

    [Fact]
    public async Task The_floor_decides_what_is_worth_the_writing_deployment()
    {
        var writer = new StubWriter();

        await RunAsync(writer, minAssessmentScore: 60);

        // Judged 60, so it arrives only when the run says it would apply at 60 - and it arrives
        // where its rank puts it, below the rows that were already there. The floor changes the
        // set and never the order.
        Assert.Contains(Weak, writer.Postings);
        Assert.True(writer.Postings.IndexOf(Weak) > writer.Postings.IndexOf(Second));
    }

    // -----------------------------------------------------------------------
    // The bound, which is the part that is a bill
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_cap_stops_the_batch_and_nothing_else_does()
    {
        var writer = new StubWriter();

        var summary = await RunAsync(writer, documentsPerNight: 1);

        Assert.Equal(1, summary.Written);

        // Called once. A cap enforced on the write rather than on the call would have paid for
        // the second draft and thrown it away.
        Assert.Single(writer.Postings);
    }

    [Fact]
    public async Task A_cap_of_zero_switches_the_pass_off_without_a_deploy()
    {
        var writer = new StubWriter();

        var summary = await RunAsync(writer, documentsPerNight: 0);

        Assert.Empty(writer.Postings);
        Assert.Equal(0, summary.Written);
    }

    [Fact]
    public async Task The_pass_resumes_from_the_database_rather_than_restarting()
    {
        var first = new StubWriter();
        await RunAsync(first, documentsPerNight: 1);

        var second = new StubWriter();
        await RunAsync(second, documentsPerNight: 1);

        // Nothing is flagged and no attempt is tracked: the document row is what "done" means,
        // so the next run starts where this one stopped.
        Assert.Equal(Best, Assert.Single(first.Postings));
        Assert.Equal(Second, Assert.Single(second.Postings));
    }

    [Fact]
    public async Task Three_writing_calls_that_return_nothing_stop_the_pass()
    {
        var writer = new StubWriter { Draft = null };

        var summary = await RunAsync(writer);

        // Four postings would be written for; a pass with no stop would have made every one of
        // those calls on the most expensive deployment there is, to store nothing.
        Assert.Equal(3, writer.Postings.Count);
        Assert.DoesNotContain(Fourth, writer.Postings);
        Assert.Equal(3, summary.Failed);
        Assert.Equal(0, summary.Written);
        Assert.Empty(await DocumentedAsync());
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_pass()
    {
        var writer = new StubWriter { FailFor = Best };

        await RunAsync(writer);

        var documented = await DocumentedAsync();

        // A provider having a bad minute is not a provider that is gone, and the postings behind
        // the failure are written for rather than lost with it.
        Assert.Equal([Second, Third, Fourth], documented);
    }

    [Fact]
    public async Task With_no_provider_nothing_is_written_and_the_backlog_is_reported()
    {
        var summary = await RunAsync(writer: null);

        Assert.Equal(0, summary.Written);
        Assert.Empty(await DocumentedAsync());

        // The figure that makes the case for configuring one. "No documents exist" is also what a
        // broken pass looks like, so the count of postings waiting is what tells them apart. The
        // aggregator and the bodyless advert are not in it: this pass will never write for either,
        // so counting them would leave a permanent floor under the backlog.
        Assert.Equal(4, summary.Waiting);
    }

    [Fact]
    public async Task The_backlog_falls_as_the_pass_writes()
    {
        var summary = await RunAsync(new StubWriter(), documentsPerNight: 1);

        // Four postings were waiting; one now has a draft.
        Assert.Equal(3, summary.Waiting);
    }

    [Fact]
    public async Task The_http_dispatch_writes_one_document_however_many_are_asked_for()
    {
        var writer = new StubWriter();

        var function = Create(writer, packs: null, documentsPerNight: 10, minAssessmentScore: 80);
        var request = Request("""{"limit":10}""");

        var result = Assert.IsType<OkObjectResult>(
            await function.RunGenerateApplicationsFunction(request, CancellationToken.None));

        var summary = Assert.IsType<GenerateApplicationsFunction.GenerationSummary>(result.Value);

        // A writing call is allowed 180 seconds and the gateway allows about 230, so two is a
        // 504 that carries nothing back. The route is a nudge; calling it again is the batch.
        Assert.Equal(1, summary.Written);
        Assert.Single(writer.Postings);
    }

    // -----------------------------------------------------------------------
    // What is stored beside the draft
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_drafted_free_text_is_stored_in_the_same_write_as_the_documents()
    {
        await RunAsync(new StubWriter());

        var stored = await LatestAsync(Best);

        var answer = Assert.Single(stored.DraftedAnswers);

        Assert.Equal("How did you hear about us?", answer.QuestionText);
        Assert.Equal(FreeTextCategory.StableFact, answer.Category);
    }

    [Fact]
    public async Task The_referral_answer_names_the_board_the_posting_actually_came_from()
    {
        await RunAsync(new StubWriter());

        // A form answer is a statement somebody signs. Writing "LinkedIn" on an advert found on
        // Indeed is a small lie in a document with the candidate's name on it.
        Assert.Equal("LinkedIn", Assert.Single((await LatestAsync(Best)).DraftedAnswers).Answer);
        Assert.Equal("Indeed", Assert.Single((await LatestAsync(Second)).DraftedAnswers).Answer);
    }

    [Fact]
    public async Task The_draft_is_stored_with_null_paths_where_no_pack_store_is_registered()
    {
        await RunAsync(new StubWriter());

        var stored = await LatestAsync(Best);

        // Degraded, never broken: the markdown is the record and the pack already answers "no
        // file is available" for exactly this state.
        Assert.True(stored.Rendered.IsEmpty);
        Assert.Equal("# Test Candidate", stored.CurriculumVitaeMarkdown);
    }

    [Fact]
    public async Task The_rendered_files_are_recorded_where_a_pack_store_is_registered()
    {
        var packs = new StubPackStore();

        var summary = await RunAsync(new StubWriter(), packs, documentsPerNight: 1);

        var stored = await LatestAsync(Best);

        Assert.Equal(1, summary.Rendered);
        Assert.NotNull(stored.Rendered.CvBlobPath);
        Assert.NotNull(stored.Rendered.CvDocxBlobPath);
        Assert.NotNull(stored.Rendered.CoverLetterBlobPath);

        // Paired with the PDF and with nothing else - a checksum describing a different file is
        // worse than no checksum.
        Assert.Equal(64, stored.Rendered.CvSha256!.Length);

        // Three files: the CV twice, because several large ATS vendors parse a DOCX more
        // reliably than a PDF, and the covering letter once.
        Assert.Equal(
            [
                (PackDocument.CurriculumVitae, PackFormat.Pdf),
                (PackDocument.CurriculumVitae, PackFormat.Docx),
                (PackDocument.CoverLetter, PackFormat.Pdf),
            ],
            packs.Files.Select(file => (file.Document, file.Format)));
    }

    [Fact]
    public async Task A_pack_store_that_cannot_store_leaves_the_draft_intact()
    {
        var packs = new StubPackStore { Stores = false };

        var summary = await RunAsync(new StubWriter(), packs, documentsPerNight: 1);

        var stored = await LatestAsync(Best);

        // The model call is the expensive half and it has already succeeded. A role assignment
        // that has not finished propagating costs a re-render, never a regeneration.
        Assert.Equal(1, summary.Written);
        Assert.Equal(0, summary.Rendered);
        Assert.True(stored.Rendered.IsEmpty);
    }

    [Fact]
    public async Task The_writer_is_handed_the_gap_list_and_the_advert_for_each_posting()
    {
        var writer = new StubWriter();

        await RunAsync(writer, documentsPerNight: 1);

        var request = Assert.Single(writer.Requests);

        // The pass generates through the same contract the dashboard does, so the guarantee that
        // a document cannot claim what the profile does not show is the same guarantee here.
        Assert.Equal(Best, request.Posting.PostingId);
        Assert.Equal("Platform Engineer", request.Posting.Title);
        Assert.Contains("Kubernetes", request.Posting.Text, StringComparison.Ordinal);
        Assert.Equal(SubjectId, request.Profile.SubjectId);

        // Nobody is at the keyboard at half past four in the morning, so there is no steer to
        // pass on - and a stored default would be this pass asserting a preference nobody
        // expressed.
        Assert.Null(request.Instructions);
    }

    // -----------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------

    private JobsDbContext CreateContext() => new(_options);

    private Task<GenerateApplicationsFunction.GenerationSummary> RunAsync(
        StubWriter? writer,
        StubPackStore? packs = null,
        int documentsPerNight = 10,
        int minAssessmentScore = 80)
    {
        var function = Create(writer, packs, documentsPerNight, minAssessmentScore);

        return function.RunNightlyAsync(CancellationToken.None);
    }

    private GenerateApplicationsFunction Create(
        StubWriter? writer, StubPackStore? packs, int documentsPerNight, int minAssessmentScore)
    {
        var db = CreateContext();

        return new GenerateApplicationsFunction(
            db,
            new CandidateProfileRepository(db),
            new JobMatchRepository(db),
            new ApplicationDocumentRepository(db),
            Options.Create(new ApplicationGenerationOptions
            {
                DocumentsPerNight = documentsPerNight,
                MinAssessmentScore = minAssessmentScore,
            }),
            new FakeTime(Now),
            NullLogger<GenerateApplicationsFunction>.Instance,
            writer,
            packs);
    }

    /// <summary>Which postings have a draft, in the order they were written.</summary>
    private async Task<long[]> DocumentedAsync()
    {
        await using var db = CreateContext();

        return await db.ApplicationDocuments
            .AsNoTracking()
            .Where(d => d.ProfileId == ProfileId && d.PostingId != Written)
            .OrderBy(d => d.Id)
            .Select(d => d.PostingId)
            .ToArrayAsync();
    }

    private async Task<int> RevisionsAsync(long postingId)
    {
        await using var db = CreateContext();

        return await db.ApplicationDocuments
            .AsNoTracking()
            .CountAsync(d => d.ProfileId == ProfileId && d.PostingId == postingId);
    }

    private async Task<StoredApplication> LatestAsync(long postingId)
    {
        await using var db = CreateContext();

        var stored = await new ApplicationDocumentRepository(db)
            .GetLatestForPostingAsync(ProfileId, postingId);

        return stored ?? throw new InvalidOperationException($"No draft was written for {postingId}.");
    }

    private static void Add(
        JobsDbContext db,
        long id,
        string title,
        string company,
        string? direct = null,
        bool? offsiteApply = null,
        string site = "linkedin",
        string? description = null)
        => db.JobPostings.Add(new JobPostingEntity
        {
            Id = id,
            SourceKey = $"{site}:{id}",
            Site = site,
            ExternalId = id.ToString(),
            ContentHash = new string((char)('a' + (id % 20)), 64),
            Title = title,
            Company = company,
            LocationCity = "London",
            LocationRaw = "London, UK",
            JobUrl = $"https://www.{site}.com/jobs/view/{id}",
            JobUrlDirect = direct,
            OffsiteApply = offsiteApply,
            Description = description ?? $"{title} at {company}. Kubernetes, Terraform, C#.",
            FirstSeenUtc = Now.AddDays(-3),
            LastSeenUtc = Now,
        });

    private static void Match(JobsDbContext db, long postingId, int score, int assessment, double rank)
        => db.JobMatches.Add(new JobMatchEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            Score = score,
            RankScore = rank,
            ScoredAtUtc = Now.AddDays(-1),
            Verdict = CandidacyVerdict.Strong,
            AssessmentScore = assessment,
            AssessedAtUtc = Now.AddHours(-1),
            AssessmentVersion = CandidacyAssessment.CurrentVersion,
            ScorerVersion = MatchResult.CurrentVersion,
        });

    private static HttpRequest Request(string body)
    {
        var context = new DefaultHttpContext();

        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/json";

        return context.Request;
    }

    /// <summary>A clock that does not move, so a stored timestamp is known by construction.</summary>
    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// The writing deployment, without the deployment.
    /// </summary>
    /// <remarks>
    /// It records the postings it was asked about, because that is what the run cost and it is
    /// the only thing that separates a pass which respected its cap from one that paid for drafts
    /// it then threw away. The markdown is the shortest thing both renderers accept, since these
    /// tests run the real ones.
    /// </remarks>
    private sealed class StubWriter : IApplicationWriter
    {
        private static readonly ApplicationDraft Written = new()
        {
            CurriculumVitaeMarkdown = "# Test Candidate",
            CoverLetterMarkdown = "I would like to apply.",
            Emphasised = ["Kubernetes in production."],
            Model = "writing",
        };

        public List<long> Postings { get; } = [];

        public List<ApplicationRequest> Requests { get; } = [];

        /// <summary>What every call returns. Null is a provider answering with nothing usable.</summary>
        public ApplicationDraft? Draft { get; init; } = Written;

        /// <summary>One posting the writer fails on, to separate a bad minute from a dead provider.</summary>
        public long? FailFor { get; init; }

        public Task<ApplicationDraft?> WriteAsync(ApplicationRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            Postings.Add(request.Posting.PostingId);
            Requests.Add(request);

            return Task.FromResult(FailFor == request.Posting.PostingId ? null : Draft);
        }
    }

    /// <summary>
    /// The pack store, without the storage account.
    /// </summary>
    /// <remarks>
    /// Returns the same path and hash the real one would - <c>ApplicationPackFile</c> owns both
    /// conventions, so a stub that invented its own would let a path this system cannot read back
    /// pass a test. <see cref="Stores"/> false is the contract's own failure mode: null for
    /// everything, never an exception.
    /// </remarks>
    private sealed class StubPackStore : IApplicationPackStore
    {
        private const string Container = "application-packs";

        public List<PackFileRequest> Files { get; } = [];

        public bool Stores { get; init; } = true;

        public TimeSpan LinkLifetime => TimeSpan.FromMinutes(15);

        public Task<StoredPackFile?> StoreAsync(PackFileRequest file, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(file);

            Files.Add(file);

            if (!Stores)
            {
                return Task.FromResult<StoredPackFile?>(null);
            }

            var name = ApplicationPackFile.FileName(file.CandidateName, file.Document, file.Format);

            return Task.FromResult<StoredPackFile?>(new StoredPackFile
            {
                BlobPath = ApplicationPackFile.BlobPath(Container, file.ProfileId, file.DocumentId, name),
                FileName = name,
                ContentType = ApplicationPackFile.ContentType(file.Format),
                Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Content)),
                Length = file.Content.Length,
            });
        }

        public Task<Uri?> LinkAsync(string? blobPath, CancellationToken ct = default)
            => Task.FromResult<Uri?>(null);
    }
}
