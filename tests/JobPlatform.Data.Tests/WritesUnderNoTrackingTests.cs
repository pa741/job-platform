using JobPlatform.Core.Applications;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// Every write that reads a row and stamps a column still writes when the host tracks nothing.
/// </summary>
/// <remarks>
/// <b>Written because a whole class of write was silently doing nothing, and no suite could see
/// it.</b> The API registered its context with <c>QueryTrackingBehavior.NoTracking</c> under the
/// comment "the API never writes to SQL". That was true when it was written and stopped being
/// true when the apply loop arrived: answering a question, superseding an answer, parking an
/// application, ending a park, and recording where a rendered file went are all read-a-row,
/// set-a-column, save. Under a global <c>NoTracking</c> every one of them saved nothing and threw
/// nothing.
///
/// <b>Neither existing suite could catch it.</b> These Data tests build their own context and
/// never set the behaviour, so they tracked and passed. The API suite's fixture <i>replaced</i>
/// the registration and copied the <c>NoTracking</c> across, so it reproduced the fault instead of
/// finding it - and because the fixture replaces rather than inherits, no test there can observe
/// what <c>Program.cs</c> configures at all.
///
/// So the default was corrected and each of these reads now asks for tracking out loud, which is
/// what this pins: the writes are correct under either default, and cannot be broken again by a
/// line in a composition root a long way from them. The assertions all read the row back through
/// a <i>second</i> context, because reading it through the one that wrote it would be answered
/// from the change tracker and would pass whether or not anything reached the database.
/// </remarks>
public sealed class WritesUnderNoTrackingTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;

    public WritesUnderNoTrackingTests()
    {
        _connection.Open();

        // The host's setting, reproduced deliberately. This is the configuration the production
        // API ran under, and the one every test below has to survive.
        _options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlite(_connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        using var db = new JobsDbContext(_options);

        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = "11111111-1111-1111-1111-111111111111",
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });

        db.JobPostings.Add(new JobPostingEntity
        {
            Id = 1,
            SourceKey = "linkedin:1",
            Site = "linkedin",
            ExternalId = "1",
            ContentHash = new string('a', 64),
            Title = "Backend Engineer",
            Company = "Northwind",
            JobUrl = "https://www.linkedin.com/jobs/view/1",
            LocationCity = "London",
            FirstSeenUtc = Now,
            LastSeenUtc = Now,
        });

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    [Fact]
    public async Task Parking_an_application_writes_the_reason()
    {
        await using (var db = CreateContext())
        {
            await new SubmissionRepository(db).ParkAsync(ProfileId, 1, ParkReason.Captcha, Now);
        }

        await using var read = CreateContext();

        var row = await read.Submissions.SingleAsync(s => s.PostingId == 1);

        Assert.Equal(ParkReason.Captcha, row.ParkedReason);
    }

    /// <summary>Re-parking mutates a row that already exists, which is the shape that failed.</summary>
    [Fact]
    public async Task Re_parking_an_existing_row_writes_the_new_reason()
    {
        await using (var db = CreateContext())
        {
            var repository = new SubmissionRepository(db);

            await repository.ParkAsync(ProfileId, 1, ParkReason.Captcha, Now);
            await repository.ParkAsync(ProfileId, 1, ParkReason.LoginRequired, Now.AddDays(1));
        }

        await using var read = CreateContext();

        var row = await read.Submissions.SingleAsync(s => s.PostingId == 1);

        Assert.Equal(ParkReason.LoginRequired, row.ParkedReason);
    }

    /// <summary>The fix that stops the loop applying twice, under the host that would have hidden it.</summary>
    [Fact]
    public async Task Recording_an_event_ends_the_park()
    {
        await using (var db = CreateContext())
        {
            var repository = new SubmissionRepository(db);

            var (parked, _) = await repository.ParkAsync(ProfileId, 1, ParkReason.Captcha, Now);

            await repository.AddEventAsync(
                ProfileId,
                parked.Id,
                new SubmissionEvent(Now.AddDays(1), SubmissionEventType.Submitted, null, SubmissionEventSource.Client, null),
                "k");
        }

        await using var read = CreateContext();

        var row = await read.Submissions.SingleAsync(s => s.PostingId == 1);

        Assert.Equal(Now.AddDays(1), row.UnparkedAtUtc);
    }

    /// <summary>Superseding is the write whose silent failure resubmits last year's figure.</summary>
    [Fact]
    public async Task A_replaced_answer_is_actually_superseded()
    {
        await using (var db = CreateContext())
        {
            var repository = new FormAnswerRepository(db);

            await repository.RecordAsync(
                ProfileId,
                FormAnswer.Create("What is your notice period?", "1 month", AnswerScope.Global, FormAnswerSource.Candidate, Now),
                Now);

            await repository.RecordAsync(
                ProfileId,
                FormAnswer.Create("What is your notice period?", "3 months", AnswerScope.Global, FormAnswerSource.Candidate, Now.AddDays(1)),
                Now.AddDays(1));
        }

        await using var read = CreateContext();

        var live = await read.FormAnswers.Where(a => a.SupersededAtUtc == null).ToListAsync();

        // One live answer, and it is the new one. Without the write landing there would be two,
        // and the filtered unique index would have refused the second insert instead.
        Assert.Equal("3 months", Assert.Single(live).Value);
    }

    [Fact]
    public async Task Answering_a_question_actually_closes_it()
    {
        long questionId;

        await using (var db = CreateContext())
        {
            var questions = new OpenQuestionRepository(db);

            var opened = await questions.OpenAsync(
                ProfileId,
                "How many years of Kubernetes do you have?",
                options: null,
                sensitive: false,
                postingId: 1,
                runId: null,
                Now);

            questionId = opened.Row.Id;

            var answer = await new FormAnswerRepository(db).RecordAsync(
                ProfileId,
                FormAnswer.Create(
                    "How many years of Kubernetes do you have?", "Four", AnswerScope.Global,
                    FormAnswerSource.Candidate, Now),
                Now);

            await questions.AnswerAsync(ProfileId, questionId, answer.Answer.Answer.Id, Now);
        }

        await using var read = CreateContext();

        var question = await read.OpenQuestions.SingleAsync(q => q.Id == questionId);

        // The causal link the whole queue rests on: a question that stays open keeps its posting
        // parked, so a close that does not land is a posting held back for ever.
        Assert.NotNull(question.AnsweredAtUtc);
    }

    [Fact]
    public async Task A_rendered_document_records_where_its_files_went()
    {
        long documentId;

        await using (var db = CreateContext())
        {
            var documents = new ApplicationDocumentRepository(db);

            var stored = await documents.AddAsync(
                ProfileId,
                1,
                new ApplicationDraft
                {
                    CurriculumVitaeMarkdown = "# CV",
                    CoverLetterMarkdown = "Dear Northwind",
                },
                instructions: null,
                draftedAnswers: null,
                Now);

            documentId = stored.Id;

            await documents.RecordRenderedAsync(
                ProfileId,
                documentId,
                new RenderedDocuments { CvBlobPath = "application-packs/1/1/Test_Candidate_CV.pdf" });
        }

        await using var read = CreateContext();

        var document = await read.ApplicationDocuments.SingleAsync(d => d.Id == documentId);

        Assert.Equal("application-packs/1/1/Test_Candidate_CV.pdf", document.CvBlobPath);
    }
}
