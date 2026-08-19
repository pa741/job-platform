using JobPlatform.Core.Model;
using JobPlatform.Data.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// Exercises the upsert and rollup against a real relational engine (SQLite in memory),
/// so the LINQ actually has to translate. No Azure account or credentials required.
/// </summary>
public sealed class JobPostingRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateOnly ScrapeDate = new(2026, 8, 18);
    private const string SearchTerm = "software-engineer";

    public JobPostingRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static JobPostingRepository CreateRepository(JobsDbContext db)
        => new(db, NullLogger<JobPostingRepository>.Instance);

    private static ScrapeRunContext Context(string blobName, DateOnly? date = null)
    {
        var day = date ?? ScrapeDate;
        return new ScrapeRunContext
        {
            BlobPath = $"jobs/{blobName}",
            SearchTerm = SearchTerm,
            ScrapedAtUtc = new DateTimeOffset(day.ToDateTime(new TimeOnly(20, 30)), TimeSpan.Zero),
        };
    }

    private static JobPosting Posting(
        string site, string id, string title = "Backend Engineer",
        string company = "Northwind Labs", bool remote = false, string? description = "text")
        => new()
        {
            ExternalId = id,
            Site = site,
            Title = title,
            Company = company,
            Location = "London, ENG, GB",
            IsRemote = remote,
            Description = description,
        };

    [Fact]
    public async Task Ingest_records_every_posting_as_new_on_a_first_run()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);

        var postings = new[] { Posting("indeed", "a1"), Posting("linkedin", "b1", title: "SRE") };

        var (run, outcome) = await repository.IngestAsync(Context("run1.csv"), postings, 2, 0);

        Assert.Equal(2, outcome.New);
        Assert.Equal(0, outcome.Updated);
        Assert.Equal(0, outcome.Unchanged);
        Assert.Equal(2, await db.JobPostings.CountAsync());
        Assert.Equal(2, run.ParsedCount);
    }

    [Fact]
    public async Task Ingest_reports_an_identical_second_run_as_unchanged_and_adds_no_rows()
    {
        var postings = new[] { Posting("indeed", "a1"), Posting("linkedin", "b1") };

        await using (var db = CreateContext())
        {
            await CreateRepository(db).IngestAsync(Context("run1.csv"), postings, 2, 0);
        }

        await using (var db = CreateContext())
        {
            var (_, outcome) = await CreateRepository(db).IngestAsync(Context("run2.csv"), postings, 2, 0);

            Assert.Equal(0, outcome.New);
            Assert.Equal(0, outcome.Updated);
            Assert.Equal(2, outcome.Unchanged);
            Assert.Equal(2, await db.JobPostings.CountAsync());
            Assert.All(await db.JobPostings.ToListAsync(), p => Assert.Equal(2, p.SeenCount));
        }
    }

    [Fact]
    public async Task Ingest_counts_a_changed_posting_as_updated_not_new()
    {
        await using (var db = CreateContext())
        {
            await CreateRepository(db).IngestAsync(Context("run1.csv"), [Posting("indeed", "a1")], 1, 0);
        }

        await using (var db = CreateContext())
        {
            // Same posting, retitled by the board.
            var changed = Posting("indeed", "a1", title: "Senior Backend Engineer");
            var (_, outcome) = await CreateRepository(db).IngestAsync(Context("run2.csv"), [changed], 1, 0);

            Assert.Equal(0, outcome.New);
            Assert.Equal(1, outcome.Updated);
            Assert.Equal(1, await db.JobPostings.CountAsync());
            Assert.Equal("Senior Backend Engineer", (await db.JobPostings.SingleAsync()).Title);
        }
    }

    [Fact]
    public async Task Reingesting_the_same_blob_reuses_its_run_rather_than_creating_a_second()
    {
        var postings = new[] { Posting("indeed", "a1") };

        await using (var db = CreateContext())
        {
            await CreateRepository(db).IngestAsync(Context("run1.csv"), postings, 1, 0);
        }

        await using (var db = CreateContext())
        {
            // Event Grid redelivery, or a manual replay.
            await CreateRepository(db).IngestAsync(Context("run1.csv"), postings, 1, 0);

            Assert.Equal(1, await db.ScrapeRuns.CountAsync());
            Assert.Equal(1, await db.JobPostings.CountAsync());
        }
    }

    [Fact]
    public async Task Daily_rollup_buckets_by_scrape_date_not_by_ingest_time()
    {
        // The regression this guards: FirstSeenUtc/LastSeenUtc record when we ingested,
        // which is routinely a different day from the scrape. Bucketing on those produced
        // a rollup of all zeroes.
        await using var db = CreateContext();
        var repository = CreateRepository(db);

        await repository.IngestAsync(
            Context("run1.csv"),
            [Posting("indeed", "a1"), Posting("linkedin", "b1", remote: true)],
            rowsInFile: 2,
            invalidRows: 0);

        var rollup = await repository.BuildDailyRollupAsync(SearchTerm, ScrapeDate);

        Assert.Equal(1, rollup.RunsIngested);
        Assert.Equal(2, rollup.PostingsSeen);
        Assert.Equal(2, rollup.NewPostings);
        Assert.Equal(2, rollup.CumulativePostings);
        Assert.Equal(1, rollup.BySite["indeed"]);
        Assert.Equal(1, rollup.BySite["linkedin"]);
        Assert.Equal(0.5, rollup.RemoteShare);
        Assert.Equal(0, rollup.SalaryCoverage);
        Assert.Equal("2026-08-18", rollup.Date);
    }

    [Fact]
    public async Task Daily_rollup_separates_new_postings_from_ones_carried_over()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);

        var dayOne = ScrapeDate;
        var dayTwo = ScrapeDate.AddDays(1);

        await repository.IngestAsync(Context("d1.csv", dayOne), [Posting("indeed", "a1")], 1, 0);

        // Day two re-lists the same job and adds one genuinely new posting.
        await repository.IngestAsync(
            Context("d2.csv", dayTwo),
            [Posting("indeed", "a1"), Posting("indeed", "a2", title: "Platform Engineer")],
            rowsInFile: 2,
            invalidRows: 0);

        var second = await repository.BuildDailyRollupAsync(SearchTerm, dayTwo);

        Assert.Equal(2, second.PostingsSeen);
        Assert.Equal(1, second.NewPostings);
        Assert.Equal(2, second.CumulativePostings);

        // Day one's rollup still reports only its own run.
        var first = await repository.BuildDailyRollupAsync(SearchTerm, dayOne);
        Assert.Equal(1, first.NewPostings);
        Assert.Equal(1, first.CumulativePostings);
    }

    [Fact]
    public async Task Daily_rollup_of_a_day_with_no_runs_is_empty_rather_than_failing()
    {
        await using var db = CreateContext();

        var rollup = await CreateRepository(db)
            .BuildDailyRollupAsync(SearchTerm, new DateOnly(2020, 1, 1));

        Assert.Equal(0, rollup.RunsIngested);
        Assert.Equal(0, rollup.PostingsSeen);
        Assert.Equal(0, rollup.RemoteShare);
        Assert.Empty(rollup.BySite);
    }
}
