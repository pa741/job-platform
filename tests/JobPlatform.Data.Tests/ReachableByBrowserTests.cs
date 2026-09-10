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
/// <c>ApplyableQuery.ReachableByBrowser</c>, held against <see cref="ApplyRoute"/>.
/// </summary>
/// <remarks>
/// <b>A rule with two spellings needs a test that reads both, and this file is that test.</b> The
/// filter runs in SQL because a predicate applied after <c>Take</c> is a silent reduction of the
/// limit rather than a filter; <see cref="ApplyRoute"/> runs after materialisation because the
/// generation pass has a vendor in hand by then. EF cannot translate the function, so the words
/// exist twice, and nothing but an assertion keeps them in step.
///
/// <b>They are deliberately not equal, and the whole value of this file is that it names both
/// differences as exact sets rather than asserting a count.</b>
///
/// <b>Wider in SQL, by one shape.</b> The filter cannot read the vendor -
/// <c>AtsVendorDetector.Detect</c> parses a URL rather than compares one - so a published link
/// into another job board passes it and is caught a moment later by the skip in
/// <c>GenerateApplicationsFunction</c>.
///
/// <b>Narrower in SQL, by one shape.</b> A link borrowed from the same job on another board is an
/// inference, and a document tailored to an advert is not bought on a title match against a
/// listing somewhere else. <see cref="ApplyRoute"/> admits it because it is answering about the
/// route; this filter refuses it because it is also deciding what to spend on.
///
/// <b>Both are asserted as equality against a named row.</b> The narrowing direction is the one
/// that hides - a row dropped in SQL never reaches the function, so it moves no count and fails no
/// other test - which is why the set is pinned rather than its size.
/// </remarks>
public sealed class ReachableByBrowserTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;

    /// <summary>A published link to an employer's own board.</summary>
    private const long PublishedEmployer = 10;

    /// <summary>A published link that goes to another job board.</summary>
    private const long PublishedAggregator = 11;

    /// <summary>The LinkedIn offsite shape: the board withheld the address.</summary>
    private const long OffsiteWithheld = 12;

    /// <summary>The board hosts the application - Easy Apply.</summary>
    private const long BoardHosted = 13;

    /// <summary>Nothing established about where the application is made.</summary>
    private const long RouteUnknown = 14;

    /// <summary>An address the employer's own board answered with.</summary>
    private const long RecoveredFromEmployerAts = 15;

    /// <summary>No address of its own; the same job on another board has one.</summary>
    private const long BorrowedFromATwin = 16;

    /// <summary>That twin. Never matched, so it is evidence rather than a queue row.</summary>
    private const long Twin = 17;

    public ReachableByBrowserTests()
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
            ExtractedAtUtc = Now.AddDays(-2),
        });

        // Real vendor shapes throughout: the vendor is parsed out of the URL, so a made-up host
        // would assert nothing about the one axis these two rules disagree on.
        Add(db, PublishedEmployer, "Platform Engineer", "Cloudflare",
            direct: "https://boards.greenhouse.io/cloudflare/jobs/1");
        Add(db, PublishedAggregator, "Analyst", "Beta",
            direct: "https://www.whatjobs.com/job/11");
        Add(db, OffsiteWithheld, "Researcher", "Kappa", offsiteApply: true);
        Add(db, BoardHosted, "Architect", "Delta", offsiteApply: false);
        Add(db, RouteUnknown, "Developer", "Zeta");
        Add(db, RecoveredFromEmployerAts, "Scientist", "Gamma",
            recovered: "https://jobs.ashbyhq.com/gamma/15", offsiteApply: true);
        Add(db, BorrowedFromATwin, "Engineer", "Iota");
        Add(db, Twin, "Engineer", "Iota", direct: "https://boards.greenhouse.io/iota/jobs/17",
            site: "indeed");

        foreach (var id in new[]
        {
            PublishedEmployer, PublishedAggregator, OffsiteWithheld,
            BoardHosted, RouteUnknown, RecoveredFromEmployerAts, BorrowedFromATwin,
        })
        {
            Match(db, id);
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task The_only_row_the_filter_drops_that_ApplyRoute_admits_is_a_borrowed_link()
    {
        var everything = await QueueAsync(new ApplyableQuery { Limit = 50 });
        var filtered = await QueueAsync(new ApplyableQuery { Limit = 50, ReachableByBrowser = true });

        var kept = filtered.Select(r => r.PostingId).ToHashSet();

        var droppedButAdmitted = everything
            .Where(r => ApplyRoute.ReachesAnEmployer(r.Channel, r.ApplyUrlSource, r.AtsVendor))
            .Select(r => r.PostingId)
            .Where(id => !kept.Contains(id))
            .OrderBy(id => id)
            .ToList();

        // This is the direction that hides. A row the SQL drops never reaches the function, so a
        // narrowing moves no count and fails no test except this one - which is why the exact set
        // is asserted rather than its size. The borrow is refused on purpose: a document tailored
        // to an advert is not bought on a title match against a listing somewhere else.
        Assert.Equal([BorrowedFromATwin], droppedButAdmitted);
    }

    [Fact]
    public async Task The_only_thing_the_filter_admits_beyond_ApplyRoute_is_a_published_aggregator()
    {
        var filtered = await QueueAsync(new ApplyableQuery { Limit = 50, ReachableByBrowser = true });

        var refusedByTheFunction = filtered
            .Where(r => !ApplyRoute.ReachesAnEmployer(r.Channel, r.ApplyUrlSource, r.AtsVendor))
            .Select(r => r.PostingId)
            .OrderBy(id => id)
            .ToList();

        // Exactly the row the SQL cannot judge, and it is caught after materialisation by the
        // skip in GenerateApplicationsFunction. If this list ever grows, the two rules have
        // drifted on an axis nobody chose.
        Assert.Equal([PublishedAggregator], refusedByTheFunction);
    }

    [Fact]
    public async Task An_offsite_posting_whose_address_was_withheld_is_kept()
    {
        var filtered = await QueueAsync(new ApplyableQuery { Limit = 50, ReachableByBrowser = true });

        var row = Assert.Single(filtered, r => r.PostingId == OffsiteWithheld);

        // The row this filter was widened for, and the two facts that admit it.
        Assert.Equal(SubmissionChannel.Ats, row.Channel);
        Assert.Equal(ApplyUrlSource.BoardPosting, row.ApplyUrlSource);
    }

    [Fact]
    public async Task A_board_hosted_and_a_route_unknown_posting_are_both_dropped()
    {
        var filtered = await QueueAsync(new ApplyableQuery { Limit = 50, ReachableByBrowser = true });
        var kept = filtered.Select(r => r.PostingId).ToHashSet();

        // Easy Apply, and a listing that says nothing at all. Neither has a form this pass could
        // attach a document to, and the second is not the first - it reclassifies when a later
        // search turns it up again.
        Assert.DoesNotContain(BoardHosted, kept);
        Assert.DoesNotContain(RouteUnknown, kept);
    }

    [Fact]
    public async Task The_filter_changes_nothing_when_it_is_not_asked_for()
    {
        var everything = await QueueAsync(new ApplyableQuery { Limit = 50 });

        // Off by default, like ExcludeAggregators beside it and for the same reason: a queue that
        // quietly returned a subset would read as a market that had gone quiet. Seven rows, not
        // eight: the twin is evidence for the borrow and was never matched.
        Assert.Equal(7, everything.Count);
    }

    private async Task<IReadOnlyList<ApplyableRow>> QueueAsync(ApplyableQuery query)
    {
        await using var db = new JobsDbContext(_options);
        return await new JobMatchRepository(db).ListApplyableAsync(ProfileId, query);
    }

    private static void Add(
        JobsDbContext db,
        long id,
        string title,
        string company,
        string? direct = null,
        string? recovered = null,
        bool? offsiteApply = null,
        string site = "linkedin")
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
            JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
            JobUrlDirect = direct,
            EmployerAtsApplyUrl = recovered,
            OffsiteApply = offsiteApply,
            Description = $"{title} at {company}.",
            FirstSeenUtc = Now.AddDays(-1),
            LastSeenUtc = Now,
        });

    private static void Match(JobsDbContext db, long postingId)
        => db.JobMatches.Add(new JobMatchEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            Score = 70,
            RankScore = 100 - postingId,
            ScoredAtUtc = Now,
            Verdict = CandidacyVerdict.Strong,
            AssessmentScore = 85,
            AssessedAtUtc = Now,
            ScorerVersion = MatchResult.CurrentVersion,
        });
}
