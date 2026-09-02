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
/// the one that waits on an answer comes back when the answer arrives.
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

    private async Task AskAsync(long postingId, DateTimeOffset? answeredAtUtc = null)
    {
        await using var db = CreateContext();

        db.OpenQuestions.Add(new OpenQuestionEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            QuestionText = "How many years of Kubernetes do you have?",
            QuestionHash = new string('9', 64),
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

    [Fact]
    public async Task A_question_left_open_on_another_posting_does_not_hold_this_one_back()
    {
        await RecordAsync(1, ParkReason.MissingAnswer);
        await AskAsync(2);

        // The join is per posting. Keyed on the profile alone, one unanswered question anywhere
        // would strand every posting parked for a missing answer - and the questions this raises
        // are per form.
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
}
