using JobPlatform.Core.Applications;
using JobPlatform.Core.Dedup;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The apply queue - <c>JobMatchRepository.ListApplyableAsync</c> - against a real engine.
/// </summary>
/// <remarks>
/// Four things are pinned here and the first is the one the change was for.
///
/// <b>What a submission row does to the queue depends on what it says.</b> Excluding on the row
/// merely existing is what made parking impossible: a park has to write a row to park against, so
/// "come back to this once the captcha is gone" and "never show me this again" were the same
/// operation. A live application and a permanent park exclude; every other park comes back, and
/// the one that waits on an answer comes back when the answers this candidate owes have arrived.
///
/// <b>That last clause is per candidate and not per posting, and the tests here are what stop it
/// being narrowed back.</b> One unanswered question per wording is the whole point of
/// <c>OpenQuestions</c>, so the row a second advert converges on names the first advert - and a
/// clause asking whether an unanswered question names <i>this</i> posting therefore holds the
/// first advert and offers every other one, on every run, forever. What it costs to ask the
/// looser question is that a posting whose own answer has arrived waits for the rest of the queue
/// to drain, which is a delay rather than a loop.
///
/// <b>Every filter agrees with the projection, and every filter runs before the bound.</b> The
/// channel filter and its projection are written out twice because EF translates one and
/// materialises the other, and the apply-URL provenance filter has the same shape and the same
/// hazard. Each pair gets a test that asks for a row the bound alone would have cut, because a
/// filter applied after <c>Take</c> is not a filter - it is a silent reduction of the limit.
///
/// <b>The same job listed twice is one entry.</b> The primary is the row that can be applied
/// through rather than the better-judged one, and the queue's limit therefore counts jobs and not
/// rows.
///
/// <b>Dismissed pairs never come back.</b> This was the only match query in the file that ignored
/// <c>DismissedAtUtc</c>, so a posting the candidate had refused on the dashboard was handed to
/// the agent on every run.
/// </remarks>
public sealed class ApplyQueueTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;

    /// <summary>The pair that is one job on two boards. Cloudflare 3020 and 3030, in miniature.</summary>
    private const long Applyable = 10;
    private const long Twin = 11;

    public ApplyQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

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

        // One posting per case the projection has to answer, and every apply URL is a real
        // vendor's shape - the detector reads hosts and query parameters, so a made-up URL would
        // assert nothing about what it does on the corpus.
        Add(db, 1, "Data Engineer", "Acme", Day(1), CrossBoardKey('1'), direct: "https://boards.greenhouse.io/acme/jobs/1");
        Add(db, 2, "Analyst", "Beta", Day(2), crossBoardKey: null, offsiteApply: false);
        Add(db, 3, "Scientist", "Gamma", Day(3), crossBoardKey: null, direct: "https://www.whatjobs.com/job/3");
        Add(db, 4, "Engineer", "Delta", Day(4), CrossBoardKey('4'));
        Add(db, 5, "Architect", "Epsilon", Day(5), CrossBoardKey('5'), offsiteApply: false);

        // The same job on two boards, and the tie-break that matters: the row carrying the
        // employer's own apply URL is assessed lower than the one that has only a board page.
        Add(db, Applyable, "Platform Engineer", "Cloudflare", Day(6), CrossBoardKey('c'),
            direct: "https://jobs.lever.co/cloudflare/platform");
        Add(db, Twin, "Platform Engineer", "Cloudflare", Day(6), CrossBoardKey('c'), offsiteApply: false, site: "indeed");

        // Posting 4's sibling on another board, carrying the link its own board stopped
        // publishing. Never matched to the profile: it exists to be borrowed from.
        Add(db, 400, "Engineer", "Delta", Day(4), crossBoardKey: null, site: "indeed",
            direct: "https://acme.wd3.myworkdayjobs.com/en-US/careers/job/4");

        // Rank, score and assessment disagree on every pair, so an accidental ordering by the
        // wrong column is visible rather than coincidental. Posting 5 is judged without a score.
        Match(db, 1, score: 50, assessment: 90, rank: 10, Day(10));
        Match(db, 2, score: 95, assessment: 60, rank: 8, Day(11), CandidacyVerdict.Possible);
        Match(db, 3, score: 70, assessment: 75, rank: 7, Day(12));
        Match(db, 4, score: 55, assessment: 70, rank: 6, Day(13), CandidacyVerdict.Possible);
        Match(db, 5, score: 40, assessment: null, rank: 4, Day(14), CandidacyVerdict.Possible);
        Match(db, Applyable, score: 60, assessment: 85, rank: 5, Day(15));
        Match(db, Twin, score: 65, assessment: 92, rank: 9, Day(15));

        // Exactly one posting in the whole database has documents, which is what the live corpus
        // looks like and the reason the loop's bottleneck is generation rather than the queue.
        db.ApplicationDocuments.Add(new ApplicationDocumentEntity
        {
            ProfileId = ProfileId,
            PostingId = 1,
            Revision = 1,
            CurriculumVitaeMarkdown = "# CV",
            CreatedAtUtc = Now,
        });

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static DateTimeOffset Day(int day) => new(2026, 8, day, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A cross-board key of the stored width. The content is irrelevant; the width is not.</summary>
    private static string CrossBoardKey(char fill) => new(fill, 64);

    private static void Add(
        JobsDbContext db,
        long id,
        string title,
        string company,
        DateTimeOffset firstSeen,
        string? crossBoardKey,
        string? direct = null,
        bool? offsiteApply = null,
        string site = "linkedin")
        => db.JobPostings.Add(new JobPostingEntity
        {
            Id = id,
            SourceKey = $"{site}:{id}",
            Site = site,
            ExternalId = id.ToString(),
            ContentHash = new string((char)('a' + (id % 20)), 64),
            CrossBoardKey = crossBoardKey,
            Title = title,
            Company = company,
            LocationCity = "London",
            LocationRaw = "London, UK",
            JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
            JobUrlDirect = direct,
            OffsiteApply = offsiteApply,
            FirstSeenUtc = firstSeen,
            LastSeenUtc = Now,
        });

    private static void Match(
        JobsDbContext db,
        long postingId,
        int score,
        int? assessment,
        double rank,
        DateTimeOffset assessedAt,
        CandidacyVerdict verdict = CandidacyVerdict.Strong)
        => db.JobMatches.Add(new JobMatchEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            Score = score,
            RankScore = rank,
            ScoredAtUtc = Now,
            Verdict = verdict,
            AssessmentScore = assessment,
            AssessedAtUtc = assessedAt,
            ScorerVersion = MatchResult.CurrentVersion,
        });

    /// <summary>
    /// Writes a submission row directly, in whatever state the queue has to read.
    /// </summary>
    /// <remarks>
    /// Round the repository deliberately, the way this project's index assertions are: the queue
    /// predicate reads these columns, so a test that went through a writer would be asserting the
    /// writer's opinion of them instead of the predicate's.
    /// </remarks>
    private async Task RecordAsync(
        long postingId, ParkReason? parkedReason = null, DateTimeOffset? unparkedAtUtc = null)
    {
        await using var db = CreateContext();

        db.Submissions.Add(new SubmissionEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            Channel = SubmissionChannel.Ats,
            ApplyUrl = null,
            CreatedAtUtc = Now,
            ParkedReason = parkedReason,
            ParkedAtUtc = parkedReason is null ? null : Now,
            UnparkedAtUtc = unparkedAtUtc,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Queues a question the way a run's convergence leaves it: one row, naming one advert.
    /// </summary>
    /// <remarks>
    /// Keyed by <c>QuestionKey.Hash</c> over the wording rather than by a made-up constant, so
    /// two wordings are two rows and one wording is one - which is the deduplication the queue
    /// predicate has to survive, and a fixed hash would make the second ask an index violation
    /// instead.
    /// </remarks>
    private async Task AskAsync(
        long? postingId,
        DateTimeOffset? answeredAtUtc = null,
        string question = "How many years of Kubernetes do you have?")
    {
        await using var db = CreateContext();

        db.OpenQuestions.Add(new OpenQuestionEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            QuestionText = question,
            QuestionHash = QuestionKey.Hash(question),
            AskedAtUtc = Now,
            AnsweredAtUtc = answeredAtUtc,
        });

        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<ApplyableRow>> QueueAsync(ApplyableQuery query)
    {
        await using var db = CreateContext();

        return await new JobMatchRepository(db).ListApplyableAsync(ProfileId, query);
    }

    private static long[] Ids(IReadOnlyList<ApplyableRow> rows) => [.. rows.Select(row => row.PostingId)];

    // -----------------------------------------------------------------------
    // What a submission row does to the queue
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_queue_returns_one_entry_for_each_job_best_first()
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        // The twin ranks second and the row returned in its place is the one that can be applied
        // through - the cluster keeps the position of its best-ranked member.
        Assert.Equal([1, Applyable, 2, 3, 4, 5], Ids(rows));
    }

    [Fact]
    public async Task A_dismissed_posting_never_comes_back_to_the_queue()
    {
        await using (var db = CreateContext())
        {
            var match = await db.JobMatches.SingleAsync(m => m.PostingId == 1);
            match.DismissedAtUtc = Now;
            await db.SaveChangesAsync();
        }

        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        // Every other query on this table already excluded these. This one did not, so a posting
        // the candidate had said no to on the dashboard was handed back to the agent every run,
        // and the agent had no way to know it had been refused.
        Assert.DoesNotContain(1L, Ids(rows));
    }

    [Fact]
    public async Task A_live_application_suppresses_every_listing_of_the_same_job()
    {
        await RecordAsync(Twin);

        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        // Applied to through one board, so the other board's copy goes too. Applying twice to one
        // vacancy is worse than not applying at all, and the recruiter sees both.
        Assert.DoesNotContain(Twin, Ids(rows));
        Assert.DoesNotContain(Applyable, Ids(rows));
    }

    [Fact]
    public async Task A_permanent_park_takes_one_listing_and_not_the_job()
    {
        await RecordAsync(Twin, ParkReason.Expired);

        var row = Assert.Single(await QueueAsync(new ApplyableQuery { Limit = 50 }), r => r.PostingId == Applyable);

        // An expired page on one board says that board's listing is gone, never that the twin is.
        // The cluster is drawn from what survived the exclusions, so the parked row is not offered
        // as an alternate either - that would be the exclusion leaking out through the dedupe.
        Assert.Empty(row.AlternatePostings);
    }

    [Fact]
    public async Task A_retryable_park_leaves_the_posting_in_the_queue()
    {
        await RecordAsync(1, ParkReason.Captcha);
        await RecordAsync(2, ParkReason.OutOfQuota);

        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        // The whole point of parking. A challenge served once is a fact about the attempt and the
        // spent daily cap is a fact about this system's afternoon; neither is a fact about the
        // vacancy, and the row that records them must not remove it.
        Assert.Contains(1L, Ids(rows));
        Assert.Contains(2L, Ids(rows));
    }

    [Fact]
    public async Task A_posting_parked_for_a_missing_answer_returns_once_the_question_is_answered()
    {
        await RecordAsync(1, ParkReason.MissingAnswer);
        await AskAsync(1);

        Assert.DoesNotContain(1L, Ids(await QueueAsync(new ApplyableQuery { Limit = 50 })));

        await using (var db = CreateContext())
        {
            var question = await db.OpenQuestions.SingleAsync();
            question.AnsweredAtUtc = Now;
            await db.SaveChangesAsync();
        }

        // Retryable, but not on the next run: offering it before the answer exists produces the
        // same park every run, which is a loop rather than a retry.
        Assert.Contains(1L, Ids(await QueueAsync(new ApplyableQuery { Limit = 50 })));
    }

    /// <summary>The second advert to raise a question waits on it too.</summary>
    /// <remarks>
    /// <b>The loop the fourth clause exists to prevent, arriving through the deduplication
    /// instead of through the clause.</b> <c>OpenQuestions</c> keeps one unanswered row per
    /// wording, so when a second advert asks what a first already asked there is one row and it
    /// names the first. A clause keyed on <c>q.PostingId == m.PostingId</c> therefore finds
    /// nothing for the second posting, offers it, and is handed the same park back on the next
    /// run - and the run after that, for as long as the question goes unanswered.
    /// </remarks>
    [Fact]
    public async Task A_second_posting_parked_on_the_same_question_is_held_with_the_first()
    {
        await RecordAsync(1, ParkReason.MissingAnswer);
        await AskAsync(1);

        // Posting 2 meets the same wording. OpenAsync converges on the row above rather than
        // queueing it twice, so nothing anywhere records that posting 2 is the one waiting.
        await RecordAsync(2, ParkReason.MissingAnswer);

        var ids = Ids(await QueueAsync(new ApplyableQuery { Limit = 50 }));

        Assert.DoesNotContain(1L, ids);
        Assert.DoesNotContain(2L, ids);
    }

    /// <summary>One answer releases every advert that was waiting on it.</summary>
    /// <remarks>
    /// The other half of the same property: holding the second posting is worth nothing unless
    /// the answer that arrives lets it out again. Both halves were broken by the same clause -
    /// the second advert was never held, so it was never released either, it was simply offered
    /// and parked on every run.
    /// </remarks>
    [Fact]
    public async Task Answering_a_shared_question_returns_every_posting_parked_on_it()
    {
        await RecordAsync(1, ParkReason.MissingAnswer);
        await RecordAsync(2, ParkReason.MissingAnswer);
        await AskAsync(1);

        var held = Ids(await QueueAsync(new ApplyableQuery { Limit = 50 }));

        Assert.DoesNotContain(1L, held);
        Assert.DoesNotContain(2L, held);

        await using (var db = CreateContext())
        {
            var question = await db.OpenQuestions.SingleAsync();
            question.AnsweredAtUtc = Now.AddHours(1);
            await db.SaveChangesAsync();
        }

        var released = Ids(await QueueAsync(new ApplyableQuery { Limit = 50 }));

        Assert.Contains(1L, released);
        Assert.Contains(2L, released);
    }

    /// <summary>A second park on a question raised since the first one still holds.</summary>
    /// <remarks>
    /// <b>Why the clause cannot be bounded by when the posting was parked</b>, which is the
    /// tempting way to make it name the question it is waiting on. <c>ParkAsync</c> is idempotent
    /// by state, deliberately - re-parking for the same reason leaves <c>ParkedAtUtc</c> where it
    /// was, so "blocked since Tuesday" does not become "blocked a minute ago" every night. A
    /// posting released by its first answer and stopped by a second question therefore carries a
    /// park older than the question it is now waiting for, and any rule reading that timestamp as
    /// a bound offers it again on every run.
    /// </remarks>
    [Fact]
    public async Task A_posting_parked_again_on_a_question_raised_since_is_still_held()
    {
        await using var db = CreateContext();

        var repository = new SubmissionRepository(db);

        // Run one stops on a question, which is then answered.
        await repository.ParkAsync(ProfileId, 1, ParkReason.MissingAnswer, Now);
        await AskAsync(1, answeredAtUtc: Now.AddHours(1));

        Assert.Contains(1L, Ids(await QueueAsync(new ApplyableQuery { Limit = 50 })));

        // Run two gets further and stops on a second question - one another advert had already
        // raised, so it is queued against that advert. The re-park writes nothing, because the
        // reason has not changed.
        await AskAsync(2, question: "What is your notice period?");
        await repository.ParkAsync(ProfileId, 1, ParkReason.MissingAnswer, Now.AddHours(2));

        Assert.DoesNotContain(1L, Ids(await QueueAsync(new ApplyableQuery { Limit = 50 })));
    }

    [Fact]
    public async Task An_open_question_holds_back_only_the_postings_parked_for_an_answer()
    {
        await RecordAsync(1, ParkReason.Captcha);
        await AskAsync(2);

        var ids = Ids(await QueueAsync(new ApplyableQuery { Limit = 50 }));

        // The clause is scoped by what the park says and never by the queue of questions alone.
        // A captcha is a fact about the attempt that no answer changes, and a posting nobody
        // parked is waiting on nothing at all - so an unanswered question must not empty the
        // queue, only hold back what was put down for want of an answer.
        Assert.Contains(1L, ids);
        Assert.Contains(3L, ids);
    }

    [Fact]
    public async Task A_question_raised_from_the_dashboard_holds_no_posting_back()
    {
        await RecordAsync(1, ParkReason.MissingAnswer);
        await AskAsync(postingId: null, question: "Should I ask for more than 85k?");

        // A note somebody wrote themselves is not what any application is waiting for. Counting
        // it would strand every posting parked for an answer the moment the dashboard was used.
        Assert.Contains(1L, Ids(await QueueAsync(new ApplyableQuery { Limit = 50 })));
    }

    [Fact]
    public async Task An_unparked_row_reads_as_an_application_rather_than_a_return_to_the_queue()
    {
        await RecordAsync(1, ParkReason.Captcha, unparkedAtUtc: Now);

        // A submission is live if it was never parked or has since been unparked: the park ended
        // because the application was made, which is the one transition that turns a parked row
        // into a real one. A retryable park does not need the column to come back - it comes back
        // because it is retryable.
        Assert.DoesNotContain(1L, Ids(await QueueAsync(new ApplyableQuery { Limit = 50 })));
    }

    // -----------------------------------------------------------------------
    // The same job, listed twice
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_primary_of_a_cluster_is_the_row_that_can_be_applied_through()
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        var job = Assert.Single(rows, r => r.DedupeKey == CrossBoardKey('c'));

        // Apply-URL strength first and the assessment second: the twin is judged 92 against this
        // row's 85, and all the twin has is a board page. Ordering the other way hands an agent
        // the better-judged row it cannot apply through.
        Assert.Equal(Applyable, job.PostingId);
        Assert.Equal(85, job.AssessmentScore);

        var alternate = Assert.Single(job.AlternatePostings);

        Assert.Equal(Twin, alternate.PostingId);
        Assert.Equal(ApplyUrlSource.BoardPosting, alternate.ApplyUrlSource);
        Assert.DoesNotContain(Twin, Ids(rows));
    }

    [Fact]
    public async Task Postings_with_no_cross_board_identity_are_never_one_cluster()
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        // Two nulls are not a match. Grouping them would merge every posting whose employer or
        // city is unknown into one enormous cluster - the collision the key answers null to
        // prevent - and one application would then suppress all of them.
        Assert.Contains(2L, Ids(rows));
        Assert.Contains(3L, Ids(rows));
        Assert.All(rows.Where(r => r.PostingId is 2 or 3), r => Assert.Null(r.DedupeKey));
        Assert.All(rows.Where(r => r.PostingId is 2 or 3), r => Assert.Empty(r.AlternatePostings));
    }

    [Fact]
    public async Task The_limit_counts_jobs_rather_than_rows()
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 2 });

        // Three rows are read to answer this and two jobs come back. A limit that counted rows
        // would return the same two jobs and call one of them a page of three.
        Assert.Equal([1, Applyable], Ids(rows));
        Assert.Single(rows[1].AlternatePostings);
    }

    // -----------------------------------------------------------------------
    // Every filter, against its projection and against the bound
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_documents_filter_agrees_with_the_projection_and_filters_before_the_bound()
    {
        var ready = await QueueAsync(new ApplyableQuery { DocumentsReady = true, Limit = 50 });
        var pending = await QueueAsync(new ApplyableQuery { DocumentsReady = false, Limit = 50 });

        Assert.Equal([1], Ids(ready));
        Assert.All(ready, row => Assert.True(row.HasDocuments));
        Assert.All(pending, row => Assert.False(row.HasDocuments));
        Assert.DoesNotContain(1L, Ids(pending));

        // Ordered by the deterministic score the one posting with documents ranks last of six, so
        // a bound applied first would answer nothing. A filter applied after Take is not a filter,
        // it is a silent reduction of the limit.
        var bounded = await QueueAsync(new ApplyableQuery
        {
            DocumentsReady = true,
            Sort = ApplyableSort.Score,
            Limit = 1,
        });

        Assert.Equal([1], Ids(bounded));
    }

    [Fact]
    public async Task The_apply_url_source_filter_agrees_with_the_projection_and_filters_before_the_bound()
    {
        var published = await QueueAsync(new ApplyableQuery { ApplyUrlSource = ApplyUrlSource.Posting, Limit = 50 });
        var matched = await QueueAsync(new ApplyableQuery { ApplyUrlSource = ApplyUrlSource.MatchedOnAnotherBoard, Limit = 50 });
        var board = await QueueAsync(new ApplyableQuery { ApplyUrlSource = ApplyUrlSource.BoardPosting, Limit = 50 });

        Assert.Equal([1, 3, Applyable], Ids(published));
        Assert.Equal([4], Ids(matched));
        Assert.Equal([Twin, 2, 5], Ids(board));

        // Each row says it is what the filter asked for. The filter and the projection are
        // written out twice because EF translates one and materialises the other, and nothing but
        // this holds them together.
        Assert.All(published, row => Assert.Equal(ApplyUrlSource.Posting, row.ApplyUrlSource));
        Assert.All(matched, row => Assert.Equal(ApplyUrlSource.MatchedOnAnotherBoard, row.ApplyUrlSource));
        Assert.All(board, row => Assert.Equal(ApplyUrlSource.BoardPosting, row.ApplyUrlSource));

        // Asking the filtered queue for one row: posting 4 is fifth by rank, so a bound applied
        // before the filter would answer nothing at all.
        var bounded = await QueueAsync(new ApplyableQuery
        {
            ApplyUrlSource = ApplyUrlSource.MatchedOnAnotherBoard,
            Limit = 1,
        });

        Assert.Equal([4], Ids(bounded));
    }

    [Fact]
    public async Task The_assessment_floor_is_enforced_in_the_query_and_filters_before_the_bound()
    {
        var strong = await QueueAsync(new ApplyableQuery { MinAssessmentScore = 80, Limit = 50 });

        Assert.Equal([1, Applyable], Ids(strong));
        Assert.All(strong, row => Assert.True(row.AssessmentScore >= 80));

        // Posting 5 was judged worth applying to and scored no number for it. A floor it cannot be
        // compared against is not a floor it clears: reading the null as "not judged, so let it
        // through" turns the rail into a way round one.
        Assert.DoesNotContain(5L, Ids(strong));

        // 91 leaves only the twin, which is also the proof that the cluster is drawn from the
        // filtered queue: its 85-scoring primary is gone, so the twin stands alone.
        var bounded = await QueueAsync(new ApplyableQuery { MinAssessmentScore = 91, Limit = 1 });

        Assert.Equal([Twin], Ids(bounded));
        Assert.Empty(bounded[0].AlternatePostings);
    }

    [Fact]
    public async Task The_first_seen_filter_agrees_with_the_projection_and_filters_before_the_bound()
    {
        var recent = await QueueAsync(new ApplyableQuery { Since = Day(4), Limit = 50 });

        Assert.Equal([Applyable, 4, 5], Ids(recent));
        Assert.All(recent, row => Assert.True(row.FirstSeenUtc >= Day(4)));

        // Posting 1 is the top of the unfiltered queue and the only row older than this, so a
        // bound applied first would return it and then drop it.
        var bounded = await QueueAsync(new ApplyableQuery { Since = Day(2), Limit = 1 });

        Assert.Equal([Applyable], Ids(bounded));
    }

    [Fact]
    public async Task The_assessed_since_filter_agrees_with_the_projection_and_filters_before_the_bound()
    {
        var judged = await QueueAsync(new ApplyableQuery { AssessedSince = Day(12), Limit = 50 });

        Assert.Equal([Applyable, 3, 4, 5], Ids(judged));
        Assert.All(judged, row => Assert.True(row.AssessedAtUtc >= Day(12)));

        // A different question from the one above, and both exist because the two dates move
        // independently: the nightly pass judges postings that arrived weeks ago.
        var bounded = await QueueAsync(new ApplyableQuery { AssessedSince = Day(11), Limit = 1 });

        Assert.Equal([Applyable], Ids(bounded));
    }

    [Fact]
    public async Task The_queue_can_be_ordered_by_either_score_instead_of_the_ranking_key()
    {
        var byScore = await QueueAsync(new ApplyableQuery { Sort = ApplyableSort.Score, Limit = 50 });
        var byAssessment = await QueueAsync(new ApplyableQuery { Sort = ApplyableSort.AssessmentScore, Limit = 50 });

        Assert.Equal([2, 3, Applyable, 4, 1, 5], Ids(byScore));

        // The cluster takes the twin's 92 and hands back the row that can be applied through, so
        // ordering by the assessment does not undo the choice between them.
        Assert.Equal([Applyable, 1, 3, 4, 2, 5], Ids(byAssessment));

        // A pair judged without a number sorts below one judged badly, never above it: a genuine
        // score of zero is a judgement and an absent one is not.
        Assert.Equal(5, byAssessment[^1].PostingId);
    }

    [Fact]
    public async Task The_vendor_is_read_off_the_apply_url_and_an_aggregator_is_not_an_ats()
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });
        var vendors = rows.ToDictionary(row => row.PostingId, row => row.AtsVendor);

        Assert.Equal(AtsVendor.Greenhouse, vendors[1]);
        Assert.Equal(AtsVendor.Lever, vendors[Applyable]);

        // Borrowed from the other board, so the vendor is the borrowed link's and not the board's.
        Assert.Equal(AtsVendor.Workday, vendors[4]);

        // A "direct" URL into another job board is another job board. It is a distinct value from
        // Other because the loop should skip it rather than treat it as an employer's form.
        Assert.Equal(AtsVendor.Aggregator, vendors[3]);
        Assert.False(vendors[3].IsEmployerAts());
    }

    /// <summary>
    /// A parked posting that is then actually applied to does not come back a third time.
    /// </summary>
    /// <remarks>
    /// <b>The sequence the loop runs, and the one place a duplicate application could come
    /// from.</b> Run one meets a captcha and parks; the queue deliberately offers the posting
    /// again, which is the whole point of a retryable reason. Run two gets through and records
    /// that it did. Nothing in the queue's four clauses reads the event log, so unless recording
    /// the event also ends the park, run three sees a row that is not a live application, not
    /// permanently parked and not awaiting an answer - and offers a vacancy that has already been
    /// applied to.
    ///
    /// That failure is not recoverable by anything in this system: the application exists in the
    /// world, and it is the outcome <see cref="ParkReason.Duplicate"/> is documented as calling
    /// worse than not applying at all. It would also spend a second slot of the daily cap, since
    /// event idempotency is per key rather than per type.
    /// </remarks>
    [Fact]
    public async Task A_posting_applied_to_after_a_park_does_not_return_to_the_queue()
    {
        await using var db = CreateContext();

        var repository = new SubmissionRepository(db);

        // Run one: the captcha.
        var (parked, _) = await repository.ParkAsync(ProfileId, 1, ParkReason.Captcha, Now);

        Assert.Contains(1L, Ids(await QueueAsync(new ApplyableQuery { Limit = 50 })));

        // Run two: through the wall, and recorded.
        var recorded = await repository.AddEventAsync(
            ProfileId,
            parked.Id,
            new SubmissionEvent(Now.AddDays(1), SubmissionEventType.Submitted, null, SubmissionEventSource.Client, null),
            "run-2:1:Submitted");

        Assert.Equal(SubmissionEventResult.Recorded, recorded);

        // Run three: gone, because the row now reads as the application it is.
        Assert.DoesNotContain(1L, Ids(await QueueAsync(new ApplyableQuery { Limit = 50 })));
    }

    /// <summary>The park's own history survives the application that ended it.</summary>
    /// <remarks>
    /// "Was never parked" and "was parked for a captcha in March and applied to in April" are
    /// different histories, and only the second explains why an application is a day late. So the
    /// reason and the date it was put down stay, and the end of the park is one further column.
    /// </remarks>
    [Fact]
    public async Task Applying_ends_a_park_without_erasing_why_it_was_parked()
    {
        await using var db = CreateContext();

        var repository = new SubmissionRepository(db);

        var (parked, _) = await repository.ParkAsync(ProfileId, 1, ParkReason.Captcha, Now);

        await repository.AddEventAsync(
            ProfileId,
            parked.Id,
            new SubmissionEvent(Now.AddDays(1), SubmissionEventType.Submitted, null, SubmissionEventSource.Client, null),
            "k");

        await using var read = CreateContext();

        var row = await read.Submissions.SingleAsync(x => x.PostingId == 1);

        Assert.Equal(ParkReason.Captcha, row.ParkedReason);
        Assert.Equal(Now, row.ParkedAtUtc);
        Assert.Equal(Now.AddDays(1), row.UnparkedAtUtc);
    }

    /// <summary>
    /// Answering one advert's question returns that advert, while another waits on its own.
    /// </summary>
    /// <remarks>
    /// <b>The case that separates "returns once the answer exists" from "returns once every
    /// answer exists".</b> Holding every awaiting-answer park while any question is outstanding
    /// also ends the loop and needs no column, but it serialises the whole parked queue behind
    /// the slowest question - so an advert whose own answer arrived this morning waits on an
    /// unrelated one nobody has got to. The loop was specified to do the first, and the park
    /// naming its question is what makes the difference expressible.
    /// </remarks>
    [Fact]
    public async Task Answering_one_question_returns_only_the_posting_that_was_waiting_on_it()
    {
        var first = await AskWithHashAsync(1, new string('1', 64));
        var second = await AskWithHashAsync(2, new string('2', 64));

        await ParkAwaitingAsync(1, first);
        await ParkAwaitingAsync(2, second);

        var parked = Ids(await QueueAsync(new ApplyableQuery { Limit = 50 }));

        Assert.DoesNotContain(1L, parked);
        Assert.DoesNotContain(2L, parked);

        await AnswerAsync(first);

        var ids = Ids(await QueueAsync(new ApplyableQuery { Limit = 50 }));

        // Posting 1's answer arrived, so posting 1 comes back. Posting 2 is still waiting on a
        // question of its own, which is a different fact and must not be released by this one.
        Assert.Contains(1L, ids);
        Assert.DoesNotContain(2L, ids);
    }

    /// <summary>Two adverts naming one question are both released by the one answer.</summary>
    /// <remarks>
    /// The precise rule has to keep the property the coarse one had: a question is stored once
    /// however many adverts asked it, so the answer has to let all of them out at once.
    /// </remarks>
    [Fact]
    public async Task Answering_a_question_two_postings_named_returns_both()
    {
        var shared = await AskWithHashAsync(1, new string('3', 64));

        await ParkAwaitingAsync(1, shared);
        await ParkAwaitingAsync(2, shared);

        var parked = Ids(await QueueAsync(new ApplyableQuery { Limit = 50 }));

        Assert.DoesNotContain(1L, parked);
        Assert.DoesNotContain(2L, parked);

        await AnswerAsync(shared);

        var ids = Ids(await QueueAsync(new ApplyableQuery { Limit = 50 }));

        Assert.Contains(1L, ids);
        Assert.Contains(2L, ids);
    }

    private async Task<long> AskWithHashAsync(long postingId, string questionHash)
    {
        await using var db = CreateContext();

        var question = new OpenQuestionEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            QuestionText = $"Question keyed {questionHash[..4]}",
            QuestionHash = questionHash,
            AskedAtUtc = Now,
        };

        db.OpenQuestions.Add(question);

        await db.SaveChangesAsync();

        return question.Id;
    }

    private async Task ParkAwaitingAsync(long postingId, long questionId)
    {
        await using var db = CreateContext();

        await new SubmissionRepository(db).ParkAsync(
            ProfileId, postingId, ParkReason.MissingAnswer, Now, awaitingQuestionId: questionId);
    }

    private async Task AnswerAsync(long questionId)
    {
        await using var db = CreateContext();

        var question = await db.OpenQuestions.SingleAsync(q => q.Id == questionId);

        question.AnsweredAtUtc = Now;

        await db.SaveChangesAsync();
    }


}
