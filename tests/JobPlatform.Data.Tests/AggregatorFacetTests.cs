using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The shortlist's aggregator facet, against a real relational engine.
/// </summary>
/// <remarks>
/// <b>The facet exists in SQL only because <c>JobPostings.ApplyVendor</c> does, and this file is
/// what makes that trade honest.</b> <c>AtsVendorDetector.Detect</c> reads a URL rather than
/// compares one, so it has no translation and runs after materialisation everywhere else in the
/// repository. After materialisation is exactly where a filter must not run on a
/// <c>Skip</c>/<c>Take</c> paged query: it stops being a filter and becomes a silent reduction of
/// the page size, with an offset that steps over rows the caller never saw. This codebase has paid
/// for that three times, so the assertion here asks for a row the bound alone would have cut.
///
/// <b>The stored column is a derivation and can therefore drift from the rule that fills it.</b>
/// The ladder is asserted directly - the published link, then the employer's own system, then the
/// board's page - because the two writers reach it by different routes and neither of them can
/// see this test's arithmetic.
///
/// <b>Null is kept, and it is the case most likely to be "fixed" into a bug.</b> A posting nothing
/// has rewritten since the column was added has no vendor, which is not the same claim as
/// <c>AtsVendor.Unknown</c>. A filter that hid what it had not judged would empty the shortlist on
/// the morning of the deploy, so the null case is asserted here rather than left to EF's null
/// semantics being remembered correctly by the next person to touch the clause.
/// </remarks>
public sealed class AggregatorFacetTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 3, 30, 0, TimeSpan.Zero);

    private const long ProfileId = 1;

    /// <summary>Highest ranked, and a LinkedIn posting page. The row the facet is for.</summary>
    private const long TopAggregator = 1;

    /// <summary>Ranked below it, and an employer's own form. The row the facet is protecting.</summary>
    private const long Employer = 2;

    /// <summary>A "direct" link that re-lists the advert on another board. Also hidden.</summary>
    private const long DirectToAnotherBoard = 3;

    /// <summary>Nothing has derived a vendor for it. Kept, whatever the facet says.</summary>
    private const long NotYetDerived = 4;

    public AggregatorFacetTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = "55555555-5555-5555-5555-555555555555",
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });

        // Rank descending with the aggregator at the head. The order is the test: with the
        // employer row first, a limit of one would return it however the filter behaved, and the
        // after-the-bound failure this file exists to catch would be invisible.
        Add(db, TopAggregator, AtsVendor.Aggregator, rank: 99);
        Add(db, Employer, AtsVendor.Greenhouse, rank: 80);
        Add(db, DirectToAnotherBoard, AtsVendor.Aggregator, rank: 70);
        Add(db, NotYetDerived, null, rank: 60);

        db.SaveChanges();

        static void Add(JobsDbContext db, long id, AtsVendor? vendor, double rank)
        {
            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"test:{id}",
                Site = "test",
                ExternalId = id.ToString(),
                ContentHash = new string((char)('a' + id), 64),
                Title = $"Role {id}",
                Description = $"Advert {id}",
                DescriptionLength = 9,
                ApplyVendor = vendor,
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });

            db.JobMatches.Add(new JobMatchEntity
            {
                ProfileId = ProfileId,
                PostingId = id,
                Score = 90,
                RankScore = rank,
                ScoredAtUtc = Now,
            });
        }
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    [Fact]
    public async Task The_shortlist_returns_every_row_unless_the_facet_is_asked_for()
    {
        await using var db = CreateContext();

        var rows = await new JobMatchRepository(db)
            .ListAsync(ProfileId, minimumScore: 0, assessedOnly: false, limit: 50, offset: 0);

        // Opt-in, like the age filter and for the reason PostedWithin defaults to "any time": a
        // list quietly showing a subset reads as a market that has gone quiet.
        Assert.Equal(
            [TopAggregator, Employer, DirectToAnotherBoard, NotYetDerived],
            rows.Select(r => r.PostingId).ToArray());
    }

    [Fact]
    public async Task The_facet_hides_the_postings_whose_apply_link_is_another_job_board()
    {
        await using var db = CreateContext();

        var rows = await new JobMatchRepository(db)
            .ListAsync(ProfileId, minimumScore: 0, assessedOnly: false, limit: 50, offset: 0,
                excludeAggregators: true);

        // Both aggregator rows leave and the one nothing has derived stays. Spelled out rather
        // than counted: a filter that dropped everything would satisfy a count.
        Assert.Equal([Employer, NotYetDerived], rows.Select(r => r.PostingId).ToArray());
    }

    [Fact]
    public async Task The_facet_runs_before_the_bound_rather_than_over_the_page()
    {
        await using var db = CreateContext();

        var rows = await new JobMatchRepository(db)
            .ListAsync(ProfileId, minimumScore: 0, assessedOnly: false, limit: 1, offset: 0,
                excludeAggregators: true);

        // A limit of one against a queue whose head is an aggregator. Filtered in SQL this is the
        // employer row; filtered over the materialised page it is nothing at all, and the caller
        // reads "no employer has a job for you" off a page size.
        Assert.Equal([Employer], rows.Select(r => r.PostingId).ToArray());
    }

    [Fact]
    public async Task A_posting_nobody_has_derived_a_vendor_for_is_kept()
    {
        await using var db = CreateContext();

        var rows = await new JobMatchRepository(db)
            .ListAsync(ProfileId, minimumScore: 0, assessedOnly: false, limit: 50, offset: 0,
                excludeAggregators: true);

        // Null is "nobody has looked", not "there is nothing at the end of this link" - that is
        // AtsVendor.Unknown, and it is a verdict. The whole corpus is null on the morning of the
        // deploy, so a facet reading the two the same way would empty the page.
        var row = Assert.Single(rows, r => r.PostingId == NotYetDerived);

        Assert.Null(row.ApplyVendor);
    }

    [Fact]
    public async Task The_row_carries_the_vendor_so_a_reader_can_see_what_the_facet_would_hide()
    {
        await using var db = CreateContext();

        var rows = await new JobMatchRepository(db)
            .ListAsync(ProfileId, minimumScore: 0, assessedOnly: false, limit: 50, offset: 0);

        Assert.Equal(AtsVendor.Aggregator, rows.Single(r => r.PostingId == TopAggregator).ApplyVendor);
        Assert.Equal(AtsVendor.Greenhouse, rows.Single(r => r.PostingId == Employer).ApplyVendor);

        // The detail read projects it too, or expanding a row would contradict the list it was
        // expanded from.
        var detail = await new JobMatchRepository(db).GetDetailAsync(ProfileId, TopAggregator);

        Assert.Equal(AtsVendor.Aggregator, detail!.ApplyVendor);
    }

    // -----------------------------------------------------------------------
    // The ladder the column stores
    // -----------------------------------------------------------------------

    [Fact]
    public void The_vendor_is_read_off_the_link_the_posting_would_hand_over()
    {
        const string Published = "https://boards.greenhouse.io/acme/jobs/4012345";
        const string Recovered = "https://jobs.lever.co/acme/9f3c";
        const string BoardPage = "https://www.linkedin.com/jobs/view/4295887";

        // The published link outranks both, because nothing came between the advert and it.
        Assert.Equal(
            AtsVendor.Greenhouse,
            JobPostingEntity.VendorOf(Published, Recovered, BoardPage));

        // The employer's own system next, where the board carrying the advert said nothing.
        Assert.Equal(
            AtsVendor.Lever,
            JobPostingEntity.VendorOf(null, Recovered, BoardPage));

        // And the board's own posting page last - which is the case the facet is mostly about,
        // because LinkedIn stopped publishing apply URLs and is where the corpus's link-less
        // postings come from.
        Assert.Equal(
            AtsVendor.Aggregator,
            JobPostingEntity.VendorOf(null, null, BoardPage));

        // A "direct" link that re-lists the advert somewhere else is an aggregator too, and this
        // is the half the facet could not have had from the site column alone.
        Assert.Equal(
            AtsVendor.Aggregator,
            JobPostingEntity.VendorOf("https://uk.whatjobs.com/pub_api__cpl__1234", null, BoardPage));

        // No link anywhere is Unknown - a verdict about the row - and never null, which is the
        // column's way of saying nobody has derived one.
        Assert.Equal(AtsVendor.Unknown, JobPostingEntity.VendorOf(null, null, null));
    }
}
