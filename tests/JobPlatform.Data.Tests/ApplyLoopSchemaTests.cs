using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The apply loop's schema, against a real relational engine.
/// </summary>
/// <remarks>
/// <b>These tests are about the database and not about a repository.</b> Everything here writes
/// through <see cref="JobsDbContext"/> directly, because the guarantees being pinned are
/// constraints rather than checks: a rule that only holds while callers behave is not one, and
/// the callers here will be an unattended client and a model naming arguments.
///
/// Two things are asserted, and the first is the reason the second matters.
///
/// <b>The filtered unique indexes actually reject a duplicate.</b> One live answer per question
/// per scope, one cached resolution per question and option set, one unanswered question per
/// wording. Each is written so that <b>no nullable column is ever a key column</b> - SQL Server
/// treats two NULLs as equal in a unique index and SQLite treats them as distinct, so an index
/// over a nullable id would be a production guarantee this file could not test and a test-suite
/// guarantee production did not have. Splitting the rules per scope, and requiring the id to be
/// present in the narrow ones, means every assertion below holds identically on Azure SQL. That
/// is the whole point of writing them this way; see <c>ConfigureFormAnswers</c>.
///
/// <b>The new columns round-trip.</b> Cheap, and it catches the failure that is otherwise
/// invisible until the migration is dispatched: a column mapped but not created, or created at a
/// width that truncates what the validation allows.
/// </remarks>
public sealed class ApplyLoopSchemaTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const long OtherProfileId = 2;

    private const int CompanyId = 10;
    private const int OtherCompanyId = 11;

    /// <summary>A question hash of the right shape. The content is irrelevant; the width is not.</summary>
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OptionsHash = "aaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccdddd";

    public ApplyLoopSchemaTests()
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

        foreach (var id in new[] { CompanyId, OtherCompanyId })
        {
            db.Companies.Add(new CompanyEntity
            {
                Id = id,
                CompanyKey = $"company {id}",
                DisplayName = $"Company {id}",
            });
        }

        for (var id = 1; id <= 3; id++)
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
                LocationCity = "London",
                JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static FormAnswerEntity Answer(
        AnswerScope scope = AnswerScope.Global,
        int? companyId = null,
        long? postingId = null,
        long profileId = ProfileId)
        => new()
        {
            ProfileId = profileId,
            QuestionText = "Do you require sponsorship to work in the UK?",
            QuestionHash = Hash,
            NormalisedQuestion = "do you require sponsorship to work in uk",
            Value = "No",
            Scope = scope,
            CompanyId = companyId,
            PostingId = postingId,
            Source = FormAnswerSource.Candidate,
            AnsweredAtUtc = Now,
        };

    // -----------------------------------------------------------------------
    // One live answer per question, per scope
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_second_live_global_answer_to_one_question_is_refused()
    {
        await using var db = CreateContext();

        db.FormAnswers.Add(Answer());
        await db.SaveChangesAsync();

        db.FormAnswers.Add(Answer());

        // At the database, not at a repository. The write path will check first and supersede
        // rather than insert; this is what holds when it does not.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Superseding_an_answer_makes_room_for_the_one_that_replaces_it()
    {
        await using var db = CreateContext();

        var first = Answer();
        db.FormAnswers.Add(first);
        await db.SaveChangesAsync();

        // The whole point of filtering on SupersededAtUtc: the candidate changes their mind, the
        // old row stays readable, and the new one is not fighting it for the index.
        first.SupersededAtUtc = Now.AddDays(1);
        db.FormAnswers.Add(Answer());
        await db.SaveChangesAsync();

        var stored = await db.FormAnswers.Where(a => a.QuestionHash == Hash).ToListAsync();

        Assert.Equal(2, stored.Count);
        Assert.Single(stored, a => a.SupersededAtUtc is null);
    }

    [Fact]
    public async Task Two_candidates_may_both_answer_the_same_question()
    {
        await using var db = CreateContext();

        db.FormAnswers.Add(Answer());
        db.FormAnswers.Add(Answer(profileId: OtherProfileId));

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.FormAnswers.CountAsync());
    }

    [Fact]
    public async Task One_employer_takes_one_live_answer_and_another_employer_takes_its_own()
    {
        await using var db = CreateContext();

        db.FormAnswers.Add(Answer(AnswerScope.Company, companyId: CompanyId));
        db.FormAnswers.Add(Answer(AnswerScope.Company, companyId: OtherCompanyId));

        // Two employers asking the same question is two answers, not a conflict - that is what
        // the scope is for.
        await db.SaveChangesAsync();

        db.FormAnswers.Add(Answer(AnswerScope.Company, companyId: CompanyId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task One_posting_takes_one_live_answer()
    {
        await using var db = CreateContext();

        db.FormAnswers.Add(Answer(AnswerScope.Posting, postingId: 1));
        db.FormAnswers.Add(Answer(AnswerScope.Posting, postingId: 2));
        await db.SaveChangesAsync();

        db.FormAnswers.Add(Answer(AnswerScope.Posting, postingId: 1));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_global_answer_and_a_scoped_one_to_the_same_question_coexist()
    {
        await using var db = CreateContext();

        db.FormAnswers.Add(Answer());
        db.FormAnswers.Add(Answer(AnswerScope.Company, companyId: CompanyId));
        db.FormAnswers.Add(Answer(AnswerScope.Posting, postingId: 1));

        // Three live answers to one question, and AnswerPrecedence decides between them. The
        // indexes are per scope precisely so that narrowing an answer is not a collision.
        await db.SaveChangesAsync();

        Assert.Equal(3, await db.FormAnswers.CountAsync());
    }

    [Fact]
    public void A_scope_with_no_id_is_refused_before_it_can_reach_an_index()
    {
        // The row the scoped indexes deliberately do not police: Company with no company would
        // apply to every employer, and Global carrying a posting id looks scoped and is not.
        // Both are refused a layer up, which is why an index over the nullable ids - the one
        // shape whose behaviour differs between SQL Server and SQLite - buys nothing here.
        Assert.Throws<ArgumentException>(() => FormAnswer.Create(
            "Why do you want to work here?", "Because", AnswerScope.Company, FormAnswerSource.Candidate, Now));

        Assert.Throws<ArgumentException>(() => FormAnswer.Create(
            "Why do you want to work here?", "Because", AnswerScope.Global, FormAnswerSource.Candidate, Now,
            postingId: 1));
    }

    // -----------------------------------------------------------------------
    // The resolution cache, where a null option set is the ordinary case
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_second_resolution_of_a_free_text_question_is_refused()
    {
        await using var db = CreateContext();

        db.FormAnswerResolutions.Add(Resolution());
        await db.SaveChangesAsync();

        db.FormAnswerResolutions.Add(Resolution());

        // The case an unfiltered index over a nullable OptionsHash would get wrong: SQL Server
        // reads two NULLs as equal and refuses, SQLite reads them as distinct and admits. Split
        // in two, both engines refuse - and most fields are free text, so this is the common
        // path rather than an edge of it.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_same_question_asked_with_and_without_options_caches_twice()
    {
        await using var db = CreateContext();

        db.FormAnswerResolutions.Add(Resolution());
        db.FormAnswerResolutions.Add(Resolution(optionsHash: OptionsHash));

        // A select and a free-text box asking the same thing can resolve differently and
        // honestly, so they are two rows. One row for both would serve the first answer to the
        // second form.
        await db.SaveChangesAsync();

        db.FormAnswerResolutions.Add(Resolution(optionsHash: OptionsHash));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // -----------------------------------------------------------------------
    // One unanswered question per wording
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_same_question_raised_by_two_postings_is_asked_once()
    {
        await using var db = CreateContext();

        db.OpenQuestions.Add(Question(postingId: 1));
        await db.SaveChangesAsync();

        db.OpenQuestions.Add(Question(postingId: 2));

        // The posting is context and never identity: "do you require sponsorship" is the same
        // question whichever advert raised it, and a run meeting it on forty adverts must put it
        // to a person once.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Answering_a_question_closes_it_without_deleting_it()
    {
        await using var db = CreateContext();

        var first = Question(postingId: 1);
        db.OpenQuestions.Add(first);
        await db.SaveChangesAsync();

        first.AnsweredAtUtc = Now.AddHours(1);
        await db.SaveChangesAsync();

        // A question asked again months later is a new row, and what was asked the first time is
        // still readable - the same append-only bargain the event log makes.
        db.OpenQuestions.Add(Question(postingId: 2));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.OpenQuestions.CountAsync());
        Assert.Equal(1, await db.OpenQuestions.CountAsync(q => q.AnsweredAtUtc == null));
    }

    // -----------------------------------------------------------------------
    // The new columns, and the one query shape the enum mapping exists for
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_parked_submission_keeps_its_reason_its_approval_and_its_run()
    {
        await using (var write = CreateContext())
        {
            var run = new RunEntity
            {
                ProfileId = ProfileId,
                StartedAtUtc = Now,
                FinishedAtUtc = Now.AddHours(1),
                SummaryJson = """{"considered":40,"submitted":2,"questions":1}""",
                Note = "Stopped at a login wall on most of them.",
            };

            write.Runs.Add(run);
            await write.SaveChangesAsync();

            write.Submissions.Add(new SubmissionEntity
            {
                ProfileId = ProfileId,
                PostingId = 1,
                Channel = SubmissionChannel.Ats,
                CreatedAtUtc = Now,
                ParkedReason = ParkReason.Captcha,
                ParkedAtUtc = Now,
                UnparkedAtUtc = Now.AddDays(1),
                ApprovedAtUtc = Now.AddDays(1),
                ApprovedBy = "11111111-1111-1111-1111-111111111111",
                DocumentRevision = 2,
                RunId = run.Id,
            });

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var stored = await db.Submissions.SingleAsync();

        Assert.Equal(ParkReason.Captcha, stored.ParkedReason);
        Assert.Equal(Now, stored.ParkedAtUtc);
        Assert.Equal(Now.AddDays(1), stored.UnparkedAtUtc);
        Assert.Equal(Now.AddDays(1), stored.ApprovedAtUtc);
        Assert.Equal("11111111-1111-1111-1111-111111111111", stored.ApprovedBy);
        Assert.Equal(2, stored.DocumentRevision);

        var reported = await db.Runs.SingleAsync();

        Assert.Equal(reported.Id, stored.RunId);
        Assert.Equal("Stopped at a login wall on most of them.", reported.Note);
        Assert.Contains("considered", reported.SummaryJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_permanent_park_reasons_become_an_IN_clause_rather_than_a_client_evaluation()
    {
        await using (var write = CreateContext())
        {
            var reasons = new[] { ParkReason.Expired, ParkReason.Captcha, ParkReason.MissingAnswer };

            for (var index = 0; index < reasons.Length; index++)
            {
                write.Submissions.Add(new SubmissionEntity
                {
                    ProfileId = ProfileId,
                    PostingId = index + 1,
                    Channel = SubmissionChannel.Ats,
                    CreatedAtUtc = Now,
                    ParkedReason = reasons[index],
                    ParkedAtUtc = Now,
                });
            }

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        // The reason ParkReasonPolicy publishes lists at all: a static call over a column has no
        // SQL, so the queue predicate asks Contains and EF turns it into an IN. Written against
        // Retryable this query would not execute, and the exclusion it implements is what decides
        // whether a parked posting ever comes back.
        var gone = await db.Submissions
            .Where(s => s.ParkedReason != null && ParkReasonPolicy.Permanent.Contains(s.ParkedReason.Value))
            .Select(s => s.PostingId)
            .ToListAsync();

        Assert.Equal([1], gone);

        var awaitingAnswer = await db.Submissions
            .Where(s => s.ParkedReason != null && ParkReasonPolicy.AwaitingAnswer.Contains(s.ParkedReason.Value))
            .Select(s => s.PostingId)
            .ToListAsync();

        Assert.Equal([3], awaitingAnswer);
    }

    [Fact]
    public async Task An_event_carries_what_the_browser_captured_while_it_made_the_claim()
    {
        await using (var write = CreateContext())
        {
            var submission = new SubmissionEntity
            {
                ProfileId = ProfileId,
                PostingId = 1,
                Channel = SubmissionChannel.Ats,
                CreatedAtUtc = Now,
            };

            write.Submissions.Add(submission);
            await write.SaveChangesAsync();

            write.SubmissionEvents.Add(new SubmissionEventEntity
            {
                SubmissionId = submission.Id,
                AtUtc = Now,
                Type = SubmissionEventType.Submitted,
                Source = SubmissionEventSource.Client,
                IdempotencyKey = "k1",
                ConfirmationRef = "Application #4417290",
                FinalUrl = "https://boards.example.invalid/confirmation?id=4417290",
                ScreenshotRef = "application-packs/1/1/submitted.png",
                SubmittedFieldsJson = """["full_name","email","work_history[0].employer"]""",
            });

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var stored = await db.SubmissionEvents.SingleAsync();

        Assert.Equal("Application #4417290", stored.ConfirmationRef);
        Assert.Equal("https://boards.example.invalid/confirmation?id=4417290", stored.FinalUrl);
        Assert.Equal("application-packs/1/1/submitted.png", stored.ScreenshotRef);

        // Names, never the answers given to them. The column holds what was filled in, at the
        // only resolution that is safe to keep.
        Assert.Contains("full_name", stored.SubmittedFieldsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("@", stored.SubmittedFieldsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_posting_carries_the_cross_board_key_it_is_deduplicated_on()
    {
        const string key = "cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000";

        await using (var write = CreateContext())
        {
            // Two rows for one job, which is the case the column exists to make findable. The
            // third is left null: no city or no employer means no cross-board identity, and
            // grouping the nulls together would merge every unlocated posting in the corpus.
            foreach (var id in new long[] { 1, 2 })
            {
                (await write.JobPostings.SingleAsync(p => p.Id == id)).CrossBoardKey = key;
            }

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        var cluster = await db.JobPostings
            .Where(p => p.CrossBoardKey == key)
            .Select(p => p.Id)
            .ToListAsync();

        Assert.Equal([1, 2], cluster);
        Assert.Null(await db.JobPostings.Where(p => p.Id == 3).Select(p => p.CrossBoardKey).SingleAsync());
    }

    [Fact]
    public async Task A_generated_document_records_where_its_rendered_files_went()
    {
        await using (var write = CreateContext())
        {
            write.ApplicationDocuments.Add(new ApplicationDocumentEntity
            {
                ProfileId = ProfileId,
                PostingId = 1,
                Revision = 1,
                CurriculumVitaeMarkdown = "# CV",
                CreatedAtUtc = Now,
                DraftedAnswersJson = """[{"questionText":"How did you hear about us?","answer":"LinkedIn","category":"StableFact"}]""",
                CvBlobPath = "application-packs/1/1/Test_Candidate_CV.pdf",
                CvDocxBlobPath = "application-packs/1/1/Test_Candidate_CV.docx",
                CoverLetterBlobPath = "application-packs/1/1/Test_Candidate_Cover_Letter.pdf",
                CvSha256 = Hash,
            });

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var stored = await db.ApplicationDocuments.SingleAsync();

        // Paths and never signed URLs: a user-delegation SAS expires, and an expired link stored
        // beside a document is a dead pointer that still looks live.
        Assert.Equal("application-packs/1/1/Test_Candidate_CV.pdf", stored.CvBlobPath);
        Assert.Equal("application-packs/1/1/Test_Candidate_CV.docx", stored.CvDocxBlobPath);
        Assert.Equal("application-packs/1/1/Test_Candidate_Cover_Letter.pdf", stored.CoverLetterBlobPath);
        Assert.Equal(Hash, stored.CvSha256);
        Assert.Contains("LinkedIn", stored.DraftedAnswersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_at_the_full_declared_length_survives_the_column_it_is_stored_in()
    {
        var value = new string('x', FormAnswerLimits.MaxValueLength);
        var question = new string('q', FormAnswerLimits.MaxQuestionTextLength);

        await using (var write = CreateContext())
        {
            var answer = Answer();
            answer.Value = value;
            answer.QuestionText = question;

            write.FormAnswers.Add(answer);
            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var stored = await db.FormAnswers.SingleAsync();

        // The column and the validation are one decision - FormAnswerLimits - so what
        // FormAnswer.Create accepts has to fit. A truncation here would be a shortened sentence
        // typed into somebody's application and sent to an employer, reading as a fact rather
        // than as a bug.
        Assert.Equal(FormAnswerLimits.MaxValueLength, stored.Value.Length);
        Assert.Equal(FormAnswerLimits.MaxQuestionTextLength, stored.QuestionText.Length);
    }

    private static FormAnswerResolutionEntity Resolution(string? optionsHash = null)
        => new()
        {
            ProfileId = ProfileId,
            QuestionHash = Hash,
            OptionsHash = optionsHash,
            Confidence = 0.9,
            Rationale = "Matched the candidate's stored answer on normalised text.",
            ResolvedAtUtc = Now,
        };

    private static OpenQuestionEntity Question(long postingId)
        => new()
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            QuestionText = "What are your salary expectations?",
            QuestionHash = Hash,
            Sensitive = true,
            AskedAtUtc = Now,
        };
}
