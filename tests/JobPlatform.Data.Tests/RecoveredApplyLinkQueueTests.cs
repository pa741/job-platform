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
/// The fourth apply-link source - the employer's own applicant tracking system - as the queue
/// serves it.
/// </summary>
/// <remarks>
/// <b>Its own fixture rather than more rows in <c>ApplyQueueTests</c>, and the reason is the bug
/// this file exists to prevent.</b> That class pins the ordering, the clustering and the park
/// clauses with exact id lists, so every row added to it is a row eight unrelated assertions have
/// to be re-derived around - and the fourth provenance needs postings that exist for no other
/// purpose than to hold a column combination nobody would otherwise write down: a published link
/// and a recovered one together, a recovered link on a board that says it hosts the application, a
/// board that was asked and answered nothing. Kept apart, this fixture reads as a table of the
/// ladder itself.
///
/// <b>What is asserted here is that two spellings of one rule agree, four times over.</b>
/// <c>ListApplyableAsync</c> decides which rows come back in a <c>Where</c> that EF translates, and
/// decides what each row calls itself in a projection EF materialises; a shared helper would have
/// to be an expression tree nobody can read, so the rule is written out twice and nothing but a
/// test holds the copies together. The pair has already been caught diverging once. <b>The way it
/// diverged is why every value gets its own case here</b>: a test that asks about three of four
/// arguments proves nothing about the fourth, and an arm that is wrong stays invisible until a run
/// passes the argument that reaches it. So all four provenances and all three channels are asked
/// for by name.
///
/// <b>And every filter is shown to run before the bound where the fixture can show it.</b> A
/// filter applied after <c>Take</c> is not a filter, it is a silent reduction of the limit - three
/// times over in this codebase - so each case asks the filtered queue for fewer rows than the
/// corpus holds and names a row the unfiltered read window would never have reached. Two of the
/// arguments cannot be shown that way and are said so rather than faked: <c>Posting</c> and
/// <c>Ats</c> head both orderings here, so a one-row read proves nothing about when the filter ran,
/// and the page-filling case below is what covers the first of them.
///
/// <b>Nothing here fetches anything.</b> The recovered link is a column; how it got there is the
/// ingestion pass's business and <c>EmployerAtsBoardStoreTests</c>'. What is pinned is that a
/// column written by the employer's own system is served as the employer's own answer and labelled
/// as the inference it is.
/// </remarks>
public sealed class RecoveredApplyLinkQueueTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;

    // One posting per rung of the ladder, plus the rows that only exist to be wrong in a
    // particular way. Named rather than numbered, because the whole file is about which of them
    // the queue picks and a bare id says nothing about why.
    private const long Published = 1;
    private const long Recovered = 2;
    private const long BothLinks = 3;
    private const long Borrowed = 4;
    private const long RecoveredOverBorrowed = 5;
    private const long BoardHosted = 6;
    private const long AskedAndUnmatched = 7;
    private const long BoardSaysItHostsIt = 8;

    /// <summary>The same job twice: one listing with a board page, one with a recovered link.</summary>
    private const long ClusterBoardPage = 20;
    private const long ClusterRecovered = 21;

    /// <summary>Every matched posting, which is not the same set as the queue returns.</summary>
    /// <remarks>
    /// Ten rows and nine entries: the cluster collapses to one job in an unfiltered read, and each
    /// of its two members surfaces on its own under a filter that excludes its twin. So the four
    /// provenance arms partition <i>this</i>, and never the page.
    /// </remarks>
    private static readonly long[] AllMatched =
    [
        Published, Recovered, BothLinks, Borrowed, RecoveredOverBorrowed,
        BoardHosted, AskedAndUnmatched, BoardSaysItHostsIt, ClusterBoardPage, ClusterRecovered,
    ];

    /// <summary>Real board hosts, because <c>AtsVendorDetector</c> reads the host.</summary>
    private const string GreenhouseUrl = "https://boards.greenhouse.io/acme/jobs/1";
    private const string AshbyUrl = "https://jobs.ashbyhq.com/sampura/6f2a9c11";
    private const string LeverUrl = "https://jobs.lever.co/apolloresearch/8c0d1e2f";
    private const string SmartRecruitersUrl = "https://jobs.smartrecruiters.com/BlueOptima/743999";
    private const string BorrowedUrl = "https://acme.wd3.myworkdayjobs.com/en-US/careers/job/4";

    public RecoveredApplyLinkQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = "22222222-2222-2222-2222-222222222222",
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });

        // Every posting has a title and an employer of its own, so no two of them can borrow from
        // each other by accident: the cross-board rule matches on title, employer and city, and a
        // shared title would make half these cases pass for the wrong reason.
        Add(db, Published, "Data Engineer", "Acme", direct: GreenhouseUrl);
        Add(db, Recovered, "Platform Engineer", "Sampura", recovered: AshbyUrl);
        Add(db, BothLinks, "Site Reliability Engineer", "Apollo", direct: GreenhouseUrl, recovered: LeverUrl);
        Add(db, Borrowed, "Backend Engineer", "Delta");
        Add(db, RecoveredOverBorrowed, "Machine Learning Engineer", "BlueOptima", recovered: SmartRecruitersUrl);
        Add(db, BoardHosted, "Analyst", "Epsilon", offsiteApply: false);
        Add(db, AskedAndUnmatched, "Architect", "Zeta", checkedAtUtc: Now);
        Add(db, BoardSaysItHostsIt, "Security Engineer", "Dex", offsiteApply: false, recovered: GreenhouseUrl);

        // The same job on two boards, in the shape the Cloudflare pair has: the row that can be
        // applied through is judged lower than the row that cannot. Neither carries a published
        // link, so neither can borrow from the other - which keeps this pair about the cluster.
        Add(db, ClusterBoardPage, "Distributed Systems Engineer", "Kernel",
            crossBoardKey: CrossBoardKey('k'), offsiteApply: false);
        Add(db, ClusterRecovered, "Distributed Systems Engineer", "Kernel",
            crossBoardKey: CrossBoardKey('k'), recovered: LeverUrl, site: "indeed");

        // Never matched to the profile, so they never enter the queue. They exist to be borrowed
        // from: same title, employer and city, another board, and a link.
        Add(db, 400, "Backend Engineer", "Delta", direct: BorrowedUrl, site: "indeed");
        Add(db, 500, "Machine Learning Engineer", "BlueOptima", direct: BorrowedUrl, site: "indeed");

        // Rank and assessment disagree on every row, so the two orderings are different questions
        // and a filter proved against one of them is not proved by accident against the other.
        Match(db, Published, rank: 100, assessment: 95);
        Match(db, BoardHosted, rank: 90, assessment: 20);
        Match(db, AskedAndUnmatched, rank: 80, assessment: 10);
        Match(db, Borrowed, rank: 70, assessment: 90);
        Match(db, Recovered, rank: 60, assessment: 93);
        Match(db, RecoveredOverBorrowed, rank: 50, assessment: 92);
        Match(db, BoardSaysItHostsIt, rank: 40, assessment: 91);
        Match(db, BothLinks, rank: 30, assessment: 94);
        Match(db, ClusterBoardPage, rank: 25, assessment: 89);
        Match(db, ClusterRecovered, rank: 20, assessment: 70);

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    /// <summary>A cross-board key of the stored width. The content is irrelevant; the width is not.</summary>
    private static string CrossBoardKey(char fill) => new(fill, 64);

    private static void Add(
        JobsDbContext db,
        long id,
        string title,
        string company,
        string? crossBoardKey = null,
        string? direct = null,
        string? recovered = null,
        bool? offsiteApply = null,
        DateTimeOffset? checkedAtUtc = null,
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
            EmployerAtsApplyUrl = recovered,
            // The confidence numbers from one, so it is null exactly when the link is null and can
            // never read as a match that was not made. And a recovered link always arrives with a
            // stamp, because the board that answered was read: writing one without the other would
            // be a row the recording path cannot produce.
            EmployerAtsMatchConfidence = recovered is null ? null : AtsMatchConfidence.TitleAndPlace,
            EmployerAtsCheckedUtc = recovered is null ? checkedAtUtc : Now,
            FirstSeenUtc = Now.AddDays(-30),
            LastSeenUtc = Now,
        });

    private static void Match(JobsDbContext db, long postingId, double rank, int? assessment)
        => db.JobMatches.Add(new JobMatchEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            Score = 50,
            RankScore = rank,
            ScoredAtUtc = Now,
            Verdict = CandidacyVerdict.Strong,
            AssessmentScore = assessment,
            AssessedAtUtc = Now,
            ScorerVersion = MatchResult.CurrentVersion,
        });

    private async Task<IReadOnlyList<ApplyableRow>> QueueAsync(ApplyableQuery query)
    {
        await using var db = CreateContext();

        return await new JobMatchRepository(db).ListApplyableAsync(ProfileId, query);
    }

    private static long[] Ids(IReadOnlyList<ApplyableRow> rows) => [.. rows.Select(row => row.PostingId)];

    private async Task<ApplyableRow> RowAsync(long postingId)
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        return Assert.Single(rows, row => row.PostingId == postingId);
    }

    // -----------------------------------------------------------------------
    // The ladder, one rung at a time
    // -----------------------------------------------------------------------

    /// <summary>A link the employer's own system answered with is served, and says where it came from.</summary>
    /// <remarks>
    /// The whole feature in one row. Of 382 applyable postings measured on 2026-09-07, 309 carried
    /// no employer link and every one of those was LinkedIn, which has stopped publishing them to
    /// signed-out clients; the employer's own board publishes the same link on a public,
    /// documented, unauthenticated endpoint. What arrives is a link and a claim about how it was
    /// arrived at, and the second half is the part a caller cannot reconstruct.
    /// </remarks>
    [Fact]
    public async Task A_recovered_link_is_served_as_the_employers_own_answer()
    {
        var row = await RowAsync(Recovered);

        Assert.Equal(AshbyUrl, row.ApplyUrl);
        Assert.Equal(ApplyUrlSource.MatchedOnEmployerAts, row.ApplyUrlSource);

        // The employer takes the application, so the link settles the channel on its own: this
        // posting's OffsiteApply is null and nothing else on the row says anything at all.
        Assert.Equal(SubmissionChannel.Ats, row.Channel);

        // The vendor is read off the URL after the query runs, because a static call over a column
        // has no SQL. A recovered link is on a board host by construction, so it always resolves.
        Assert.Equal(AtsVendor.Ashby, row.AtsVendor);
        Assert.True(row.AtsVendor.IsEmployerAts());
    }

    /// <summary>A recovered link is never reported as the board's own published one.</summary>
    /// <remarks>
    /// <b>The failure the provenance exists to make visible.</b> Both links open a form and nothing
    /// a browser sees separates them, so only the label can: the board token may belong to another
    /// company - "Dex", "Kernel", "Fin" and "Orbital" are real boards owned by somebody - and the
    /// title match may have landed on the vacancy next to the right one. Folded into
    /// <c>Posting</c>, both failures become invisible, and a caller that cannot tell an inference
    /// from a published fact has no way to notice when the match was wrong.
    /// </remarks>
    [Fact]
    public async Task A_recovered_link_is_never_reported_as_a_published_one()
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        var published = rows.Where(row => row.ApplyUrlSource == ApplyUrlSource.Posting).ToList();

        Assert.Equal([Published, BothLinks], Ids(published));
        Assert.All(published, row => Assert.Equal(GreenhouseUrl, row.ApplyUrl));
    }

    /// <summary>A published link outranks a recovered one on the same posting.</summary>
    /// <remarks>
    /// The recovery is for postings with no link at all, and a posting holding both is one whose
    /// board published a link after the employer's system had already been asked. The published one
    /// is the fact nobody inferred - no token to be right about, no title to match, nothing between
    /// the advert and the link - so it wins on both halves at once: the URL that is served and the
    /// provenance claimed for it.
    /// </remarks>
    [Fact]
    public async Task A_posting_carrying_both_links_keeps_the_published_one()
    {
        var row = await RowAsync(BothLinks);

        Assert.Equal(GreenhouseUrl, row.ApplyUrl);
        Assert.NotEqual(LeverUrl, row.ApplyUrl);
        Assert.Equal(ApplyUrlSource.Posting, row.ApplyUrlSource);
        Assert.Equal(AtsVendor.Greenhouse, row.AtsVendor);
    }

    /// <summary>The employer's own system outranks a link borrowed off a stranger's board.</summary>
    /// <remarks>
    /// Both are inferences and they look interchangeable; what separates them is who was asked and
    /// whether anybody checked. This posting has a namesake on another board carrying a link, so
    /// the older recovery would have answered - and the answer would have been a third party's row
    /// that agreed on three normalised strings and was confirmed by nothing, in place of the
    /// employer's own register of its own vacancies on a token confirmed against a posting before
    /// it was trusted.
    /// </remarks>
    [Fact]
    public async Task A_recovered_link_outranks_one_borrowed_from_another_board()
    {
        var row = await RowAsync(RecoveredOverBorrowed);

        Assert.Equal(SmartRecruitersUrl, row.ApplyUrl);
        Assert.NotEqual(BorrowedUrl, row.ApplyUrl);
        Assert.Equal(ApplyUrlSource.MatchedOnEmployerAts, row.ApplyUrlSource);
        Assert.Equal(AtsVendor.SmartRecruiters, row.AtsVendor);

        // The borrow still answers where the employer's system said nothing, which is what makes
        // the lines above a precedence rather than the cross-board recovery having quietly stopped.
        var borrowed = await RowAsync(Borrowed);

        Assert.Equal(BorrowedUrl, borrowed.ApplyUrl);
        Assert.Equal(ApplyUrlSource.MatchedOnAnotherBoard, borrowed.ApplyUrlSource);
    }

    /// <summary>A recovered link overrules the board's claim to host the application.</summary>
    /// <remarks>
    /// <b>The one rung of this ladder that overrules <c>OffsiteApply == false</c>, and it is
    /// deliberate.</b> That flag vetoes borrowing a link off another board, because a board saying
    /// it hosts the application is talking about <i>this</i> listing rather than one that resembles
    /// it. It does not veto the employer's own answer. The recovery pass never asks about such a
    /// posting - <c>EmployerAtsBoardRepository</c>'s unreachable-posting rule requires
    /// <c>OffsiteApply != false</c> - so a row holding both is one whose board changed its mind
    /// after the column was written, and the employer's own system is the later and better-placed
    /// of the two. Vetoing here would leave the column populated, the channel <c>Board</c>, and the
    /// queue silently declining to hand over a link the employer itself published.
    /// </remarks>
    [Fact]
    public async Task A_recovered_link_overrules_a_board_that_says_it_hosts_the_application()
    {
        var row = await RowAsync(BoardSaysItHostsIt);

        Assert.Equal(GreenhouseUrl, row.ApplyUrl);
        Assert.Equal(ApplyUrlSource.MatchedOnEmployerAts, row.ApplyUrlSource);
        Assert.Equal(SubmissionChannel.Ats, row.Channel);

        // With no recovered link the same posting is exactly the board-hosted case, which is what
        // makes the lines above about the link rather than about the flag being ignored.
        var hosted = await RowAsync(BoardHosted);

        Assert.Equal(ApplyUrlSource.BoardPosting, hosted.ApplyUrlSource);
        Assert.Equal(SubmissionChannel.Board, hosted.Channel);
    }

    /// <summary>A board that was read and matched nothing leaves the row exactly as it was.</summary>
    /// <remarks>
    /// The stamp is the third state that keeps the pass from re-asking the same employer about the
    /// same postings every run, and it is the only trace an abstention leaves - "nothing matched"
    /// and "several listings matched and the rule declined to choose" both leave the link null. It
    /// is a fact about the request and never about the application, so the queue must not read it:
    /// a row that says it was asked and has no link has no link.
    /// </remarks>
    [Fact]
    public async Task A_board_that_answered_nothing_changes_neither_the_link_nor_its_provenance()
    {
        var row = await RowAsync(AskedAndUnmatched);

        Assert.Equal($"https://www.linkedin.com/jobs/view/{AskedAndUnmatched}", row.ApplyUrl);
        Assert.Equal(ApplyUrlSource.BoardPosting, row.ApplyUrlSource);

        // Not Board: nothing was established about where the application is made, which is a
        // different fact from the board saying it hosts it and the distinction OffsiteApply exists
        // to keep.
        Assert.Equal(SubmissionChannel.Unknown, row.Channel);
    }

    // -----------------------------------------------------------------------
    // Every filter, against its projection and against the bound
    // -----------------------------------------------------------------------

    /// <summary>Each of the four provenances, asked for by name, agreeing with what the rows call themselves.</summary>
    /// <remarks>
    /// <b>All four and not only the new one.</b> Adding a source changes every arm of the filter,
    /// not one: a recovered link has to leave <c>MatchedOnAnotherBoard</c> and <c>BoardPosting</c>
    /// as well as join an arm of its own, and an arm that only added itself would answer the new
    /// value correctly while quietly answering two of the old ones wrong. That is the exact shape
    /// the pair has already drifted in, and it stays invisible until a run passes the argument that
    /// reaches the broken arm.
    /// </remarks>
    [Fact]
    public async Task Every_apply_url_source_filter_agrees_with_the_projection()
    {
        var published = await QueueAsync(new ApplyableQuery { ApplyUrlSource = ApplyUrlSource.Posting, Limit = 50 });
        var employer = await QueueAsync(new ApplyableQuery { ApplyUrlSource = ApplyUrlSource.MatchedOnEmployerAts, Limit = 50 });
        var borrowed = await QueueAsync(new ApplyableQuery { ApplyUrlSource = ApplyUrlSource.MatchedOnAnotherBoard, Limit = 50 });
        var board = await QueueAsync(new ApplyableQuery { ApplyUrlSource = ApplyUrlSource.BoardPosting, Limit = 50 });

        Assert.Equal([Published, BothLinks], Ids(published));
        Assert.Equal([Recovered, RecoveredOverBorrowed, BoardSaysItHostsIt, ClusterRecovered], Ids(employer));
        Assert.Equal([Borrowed], Ids(borrowed));
        Assert.Equal([BoardHosted, AskedAndUnmatched, ClusterBoardPage], Ids(board));

        // Each row says it is what was asked for. The filter and the projection are written out
        // twice because EF translates one and materialises the other, and nothing but this holds
        // the two spellings together.
        Assert.All(published, row => Assert.Equal(ApplyUrlSource.Posting, row.ApplyUrlSource));
        Assert.All(employer, row => Assert.Equal(ApplyUrlSource.MatchedOnEmployerAts, row.ApplyUrlSource));
        Assert.All(borrowed, row => Assert.Equal(ApplyUrlSource.MatchedOnAnotherBoard, row.ApplyUrlSource));
        Assert.All(board, row => Assert.Equal(ApplyUrlSource.BoardPosting, row.ApplyUrlSource));

        // The four arms partition the matched corpus: every posting is in exactly one of them, so
        // an arm answering for two values shows up as a posting counted twice, and one answering
        // for none as a posting missing. Against the matched rows rather than against a page,
        // because a page collapses the cluster and each filter surfaces a different half of it.
        var partitioned = Ids(published).Concat(Ids(employer)).Concat(Ids(borrowed)).Concat(Ids(board)).ToList();

        Assert.Equal(AllMatched.Length, partitioned.Count);
        Assert.Equal(AllMatched.Order(), partitioned.Order());
    }

    /// <summary>Each provenance filter runs in SQL, before the bound.</summary>
    /// <remarks>
    /// A filter applied after <c>Take</c> is not a filter, it is a silent reduction of the limit -
    /// three times over in this codebase now. Each row below asks for one job and names a posting
    /// the unfiltered read window would never have reached, so a bound applied first would answer
    /// nothing at all rather than answer less. <c>Posting</c> is absent because it heads both
    /// orderings in this fixture and a one-row read would pass whatever the filter did; the
    /// page-filling case below covers it instead.
    /// </remarks>
    [Theory]
    [InlineData(ApplyUrlSource.MatchedOnEmployerAts, ApplyableSort.Rank, Recovered)]
    [InlineData(ApplyUrlSource.MatchedOnAnotherBoard, ApplyableSort.Rank, Borrowed)]
    [InlineData(ApplyUrlSource.MatchedOnEmployerAts, ApplyableSort.AssessmentScore, Recovered)]
    [InlineData(ApplyUrlSource.BoardPosting, ApplyableSort.AssessmentScore, ClusterBoardPage)]
    public async Task Each_apply_url_source_filter_runs_before_the_bound(
        ApplyUrlSource source, ApplyableSort sort, long expected)
    {
        var bounded = await QueueAsync(new ApplyableQuery
        {
            ApplyUrlSource = source,
            Sort = sort,
            Limit = 1,
        });

        Assert.Equal([expected], Ids(bounded));
        Assert.Equal(source, bounded[0].ApplyUrlSource);
    }

    /// <summary>
    /// The published-link filter fills a page the bound alone would leave short.
    /// </summary>
    /// <remarks>
    /// <c>Posting</c> heads both orderings here, so asking for one row proves nothing about when
    /// the filter ran. Asking for two does: a two-row page reads six ordered rows, only one of the
    /// top six carries a published link, and the second one that does sits eighth - so a bound
    /// applied before the filter comes back one short of a page it could have filled.
    /// </remarks>
    [Fact]
    public async Task The_published_link_filter_fills_a_page_the_bound_alone_would_leave_short()
    {
        var bounded = await QueueAsync(new ApplyableQuery
        {
            ApplyUrlSource = ApplyUrlSource.Posting,
            Limit = 2,
        });

        Assert.Equal([Published, BothLinks], Ids(bounded));
    }

    /// <summary>Each channel, asked for by name, agreeing with what the rows call themselves.</summary>
    /// <remarks>
    /// The recovered link moves rows between all three arms and not only into <c>Ats</c>: a posting
    /// whose board says it hosts the application and whose employer has since published a form is
    /// no longer <c>Board</c>, and one with a recovered link and nothing else established is no
    /// longer <c>Unknown</c>. An <c>Ats</c> arm widened on its own would overlap the other two, and
    /// a row can only be in one channel.
    /// </remarks>
    [Fact]
    public async Task Every_channel_filter_agrees_with_the_projection()
    {
        var ats = await QueueAsync(new ApplyableQuery { Channel = SubmissionChannel.Ats, Limit = 50 });
        var board = await QueueAsync(new ApplyableQuery { Channel = SubmissionChannel.Board, Limit = 50 });
        var unknown = await QueueAsync(new ApplyableQuery { Channel = SubmissionChannel.Unknown, Limit = 50 });

        Assert.Equal(
            [Published, Borrowed, Recovered, RecoveredOverBorrowed, BoardSaysItHostsIt, BothLinks, ClusterRecovered],
            Ids(ats));
        Assert.Equal([BoardHosted, ClusterBoardPage], Ids(board));
        Assert.Equal([AskedAndUnmatched], Ids(unknown));

        Assert.All(ats, row => Assert.Equal(SubmissionChannel.Ats, row.Channel));
        Assert.All(board, row => Assert.Equal(SubmissionChannel.Board, row.Channel));
        Assert.All(unknown, row => Assert.Equal(SubmissionChannel.Unknown, row.Channel));

        // The two rows the new column moves, named rather than left to the lists above: a board
        // that says it hosts the application is not Board once the employer has answered, and a
        // posting that established nothing is not Unknown once it has a link.
        Assert.DoesNotContain(BoardSaysItHostsIt, Ids(board));
        Assert.DoesNotContain(Recovered, Ids(unknown));

        var partitioned = Ids(ats).Concat(Ids(board)).Concat(Ids(unknown)).ToList();

        Assert.Equal(AllMatched.Length, partitioned.Count);
        Assert.Equal(AllMatched.Order(), partitioned.Order());
    }

    /// <summary>Each channel filter runs in SQL, before the bound.</summary>
    /// <remarks>
    /// <c>Ats</c> is absent for the reason <c>Posting</c> is absent above: it heads both orderings
    /// in this fixture, so a one-row read would pass whatever the filter did. The two arms listed
    /// are the two the recovered link narrows, which is where a mistake in this change would land.
    /// </remarks>
    [Theory]
    [InlineData(SubmissionChannel.Board, ApplyableSort.AssessmentScore, ClusterBoardPage)]
    [InlineData(SubmissionChannel.Unknown, ApplyableSort.AssessmentScore, AskedAndUnmatched)]
    public async Task Each_channel_filter_runs_before_the_bound(
        SubmissionChannel channel, ApplyableSort sort, long expected)
    {
        var bounded = await QueueAsync(new ApplyableQuery
        {
            Channel = channel,
            Sort = sort,
            Limit = 1,
        });

        Assert.Equal([expected], Ids(bounded));
        Assert.Equal(channel, bounded[0].Channel);
    }

    // -----------------------------------------------------------------------
    // What the provenance is for, once it reaches the code that ranks on it
    // -----------------------------------------------------------------------

    /// <summary>Of two listings of one job, the one whose employer answered is handed over.</summary>
    /// <remarks>
    /// <b>The Cloudflare case, one rung down the ladder.</b> The twin here is judged 89 against
    /// this row's 70 and all it has is a board page; ordering the pair by the assessment hands an
    /// agent the better-judged row it cannot apply through. <c>PostingCluster.Choose</c> makes that
    /// call, and what this asserts is that the queue feeds it the provenance the projection
    /// computed - which is the only route by which the new rung reaches the choice at all.
    /// </remarks>
    [Fact]
    public async Task The_primary_of_a_cluster_is_the_listing_whose_employer_answered()
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        var job = Assert.Single(rows, row => row.DedupeKey == CrossBoardKey('k'));

        Assert.Equal(ClusterRecovered, job.PostingId);
        Assert.Equal(ApplyUrlSource.MatchedOnEmployerAts, job.ApplyUrlSource);
        Assert.Equal(LeverUrl, job.ApplyUrl);
        Assert.Equal(70, job.AssessmentScore);

        var alternate = Assert.Single(job.AlternatePostings);

        Assert.Equal(ClusterBoardPage, alternate.PostingId);
        Assert.Equal(ApplyUrlSource.BoardPosting, alternate.ApplyUrlSource);
        Assert.Equal(89, alternate.AssessmentScore);

        // A cluster ranks where its best-ranked member ranks, so choosing the applyable row does
        // not also demote the job out of the page.
        Assert.DoesNotContain(ClusterBoardPage, Ids(rows));
    }

    /// <summary>Where an application would be recorded is the link the queue handed over.</summary>
    /// <remarks>
    /// <b>The two reads have to agree or the log stops describing the world.</b>
    /// <c>list_applyable</c> hands an agent the employer's own form and
    /// <c>ResolveApplyTargetAsync</c> is what <c>create_submission</c> writes down; if only the
    /// first read the recovered column, an application made on the employer's form would be filed
    /// as a board application against the board's own posting page - a row that misdescribes where
    /// a real application went, in a log whose entire purpose is that later decisions read it
    /// instead of the world.
    ///
    /// <b><c>Borrowed</c> is deliberately not in this list.</b> The link borrowed off another
    /// board is a subquery over the corpus for a listing that merely resembles this posting, and
    /// <c>ResolveApplyTargetAsync</c> has never read it; that asymmetry predates this change and
    /// widening it is a separate decision with a separate argument. Listing the posting here would
    /// quietly turn this test into the place that decision was made.
    /// </remarks>
    [Fact]
    public async Task The_recorded_apply_target_is_the_link_the_queue_served()
    {
        await using var db = CreateContext();

        var repository = new JobMatchRepository(db);

        var queue = await repository.ListApplyableAsync(ProfileId, new ApplyableQuery { Limit = 50 });

        foreach (var postingId in new[] { Recovered, BoardSaysItHostsIt, BothLinks, BoardHosted, AskedAndUnmatched })
        {
            var row = Assert.Single(queue, candidate => candidate.PostingId == postingId);

            var target = await repository.ResolveApplyTargetAsync(ProfileId, postingId);

            Assert.NotNull(target);
            Assert.Equal(row.ApplyUrl, target.ApplyUrl);
            Assert.Equal(row.Channel, target.Channel);
        }
    }
}
