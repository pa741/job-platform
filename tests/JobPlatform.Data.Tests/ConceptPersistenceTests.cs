using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Model;
using JobPlatform.Data.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The write side of the concept graph, against a real relational engine so the LINQ has to
/// translate and the foreign keys have to hold.
/// </summary>
public sealed class ConceptPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private const string SearchTerm = "software-engineer";
    private static readonly DateOnly ScrapeDate = new(2026, 8, 18);

    public ConceptPersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();
        ConceptSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static JobPostingRepository Repository(JobsDbContext db)
        => new(db, NullLogger<JobPostingRepository>.Instance);

    private static ScrapeRunContext Context(string blobName) => new()
    {
        BlobPath = $"jobs/{blobName}",
        SearchTerm = SearchTerm,
        ScrapedAtUtc = new DateTimeOffset(ScrapeDate.ToDateTime(new TimeOnly(20, 30)), TimeSpan.Zero),
    };

    private static JobPosting Posting(
        string id = "a1",
        string title = "Senior Backend Engineer",
        string? description = "You will work with C# and Kubernetes. Hybrid, 2 days in the office.",
        string company = "Northwind Labs Ltd",
        string? jobType = "fulltime",
        IReadOnlyList<string>? skills = null)
        => new()
        {
            ExternalId = id,
            Site = "indeed",
            Title = title,
            Company = company,
            Location = "London, ENG, GB",
            Description = description,
            JobType = jobType,
            Skills = skills ?? [],
        };

    [Fact]
    public async Task The_seed_projects_the_whole_vocabulary()
    {
        await using var db = CreateContext();
        var graph = ConceptGraph.Default;

        Assert.Equal(graph.Concepts.Count, await db.Concepts.CountAsync());
        Assert.Equal(graph.Closure().Count(), await db.ConceptClosure.CountAsync());
        Assert.Equal(graph.Relations().Count(), await db.ConceptRelations.CountAsync());
        Assert.True(await ConceptSeeder.IsCurrentAsync(db));
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        await using var db = CreateContext();

        var before = await db.Concepts.CountAsync();
        var result = await ConceptSeeder.SeedAsync(db);

        Assert.Equal(0, result.ConceptsAdded);
        Assert.Equal(0, result.ConceptsDeactivated);
        Assert.Equal(before, await db.Concepts.CountAsync());
    }

    [Fact]
    public async Task Ingest_writes_the_concepts_a_posting_asserts()
    {
        await using var db = CreateContext();
        await Repository(db).IngestAsync(Context("run1.csv"), [Posting()], 1, 0);

        var keys = await db.PostingConcepts
            .Select(pc => pc.Concept!.ConceptKey)
            .ToListAsync();

        Assert.Contains("skill.csharp", keys);
        Assert.Contains("skill.kubernetes", keys);
    }

    [Fact]
    public async Task Evidence_and_source_survive_the_round_trip()
    {
        await using var db = CreateContext();

        await Repository(db).IngestAsync(
            Context("run1.csv"),
            [Posting(description: "Strong k8s experience.", skills: ["Terraform"])],
            1, 0);

        var byText = await db.PostingConcepts
            .Include(pc => pc.Concept)
            .ToDictionaryAsync(pc => pc.Concept!.ConceptKey);

        Assert.Equal("k8s", byText["skill.kubernetes"].EvidenceText);
        Assert.Equal(AssertionSource.Taxonomy, byText["skill.kubernetes"].Source);
        Assert.Equal(AssertionSource.Board, byText["skill.terraform"].Source);
    }

    [Fact]
    public async Task Unresolved_mentions_are_recorded_rather_than_dropped()
    {
        await using var db = CreateContext();

        await Repository(db).IngestAsync(
            Context("run1.csv"),
            [Posting(description: "You will go above and beyond.", skills: ["Contoso Internal Tool"])],
            1, 0);

        var mentions = await db.PostingMentions.ToListAsync();

        Assert.Contains(mentions, m => m.Reason == MentionReason.Ambiguous);
        Assert.Contains(mentions, m => m.Reason == MentionReason.UnknownBoardSkill
            && m.SurfaceForm == "Contoso Internal Tool");
    }

    [Fact]
    public async Task Re_ingesting_an_unchanged_posting_does_not_duplicate_its_rows()
    {
        // The idempotency contract, applied to the bridge tables. Event Grid redelivers and a
        // replayed blob has to converge, not accumulate.
        var postings = new[] { Posting() };

        await using (var db = CreateContext())
        {
            await Repository(db).IngestAsync(Context("run1.csv"), postings, 1, 0);
        }

        int concepts, tags, jobTypes;

        await using (var db = CreateContext())
        {
            concepts = await db.PostingConcepts.CountAsync();
            tags = await db.PostingTags.CountAsync();
            jobTypes = await db.JobPostingJobTypes.CountAsync();
        }

        await using (var db = CreateContext())
        {
            await Repository(db).IngestAsync(Context("run2.csv"), postings, 1, 0);
        }

        await using (var db = CreateContext())
        {
            Assert.Equal(concepts, await db.PostingConcepts.CountAsync());
            Assert.Equal(tags, await db.PostingTags.CountAsync());
            Assert.Equal(jobTypes, await db.JobPostingJobTypes.CountAsync());
        }
    }

    [Fact]
    public async Task A_changed_description_replaces_the_old_assertions()
    {
        await using (var db = CreateContext())
        {
            await Repository(db).IngestAsync(
                Context("run1.csv"), [Posting(description: "We use C# here.")], 1, 0);
        }

        await using (var db = CreateContext())
        {
            await Repository(db).IngestAsync(
                Context("run2.csv"), [Posting(description: "We use Python here.")], 1, 0);
        }

        await using (var db = CreateContext())
        {
            var keys = await db.PostingConcepts.Select(pc => pc.Concept!.ConceptKey).ToListAsync();

            // Stale rows must go, or the posting accumulates every stack it ever advertised.
            Assert.Contains("skill.python", keys);
            Assert.DoesNotContain("skill.csharp", keys);
        }
    }

    [Fact]
    public async Task Two_blobs_sharing_a_posting_can_be_ingested_through_one_context()
    {
        // Regression, found in production. Every other test here uses a fresh context per
        // blob, which is what the blob trigger gets - one invocation, one scope. The
        // reprocess endpoint looped over blobs inside a single scope, so the change tracker
        // carried entities from one blob into the next and the second attach collided on
        // (PostingId, ConceptId, Source). It was invisible while ingest only mutated scalars.
        await using var db = CreateContext();
        var repository = Repository(db);

        await repository.IngestAsync(Context("run1.csv"), [Posting(id: "a1")], 1, 0);
        await repository.IngestAsync(Context("run2.csv"), [Posting(id: "a1")], 1, 0);

        Assert.Equal(1, await db.JobPostings.CountAsync());
    }

    [Fact]
    public async Task One_surface_form_yields_one_mention_however_many_sources_saw_it()
    {
        // "Go" appears in the description as an ambiguous word and in the board's skills
        // list as an unresolvable entry. Two reasons, one (PostingId, SurfaceForm) key.
        await using var db = CreateContext();

        await Repository(db).IngestAsync(
            Context("run1.csv"),
            [Posting(description: "You will go above and beyond.", skills: ["Go"])],
            1, 0);

        var mentions = await db.PostingMentions.ToListAsync();

        // The two sources wrote different cases - "go" in the prose, "Go" in the skills list.
        // SQL Server's collation is case-insensitive, so those are one primary key there even
        // though SQLite and EF's change tracker compare them ordinally. Deduplicating on the
        // way in is what makes the two engines agree.
        var forGo = mentions.Where(
            m => m.SurfaceForm.Equals("go", StringComparison.OrdinalIgnoreCase)).ToList();

        var mention = Assert.Single(forGo);

        // The ambiguous reading wins: the vocabulary knows that form and distrusts it, which
        // is a more specific statement than "never heard of it".
        Assert.Equal(MentionReason.Ambiguous, mention.Reason);
    }

    [Fact]
    public async Task Companies_are_deduplicated_across_spellings()
    {
        await using var db = CreateContext();

        await Repository(db).IngestAsync(
            Context("run1.csv"),
            [
                Posting(id: "a1", company: "Northwind Labs Ltd"),
                Posting(id: "a2", company: "Northwind Labs Limited"),
                Posting(id: "a3", company: "NORTHWIND LABS"),
            ],
            3, 0);

        // One employer, one row - the thing that makes "who is hiring most" correct.
        var company = Assert.Single(await db.Companies.ToListAsync());
        Assert.Equal("northwind labs", company.CompanyKey);

        Assert.Equal(3, await db.JobPostings.CountAsync(p => p.CompanyId == company.Id));
    }

    [Fact]
    public async Task Derived_columns_land_on_the_posting()
    {
        await using var db = CreateContext();

        await Repository(db).IngestAsync(
            Context("run1.csv"),
            [Posting(description: "Hybrid, 3 days a week in the office. Outside IR35. "
                + "Paying £70,000 - £90,000 per annum. SC clearance required.")],
            1, 0);

        var posting = await db.JobPostings.SingleAsync();

        Assert.Equal(Seniority.Senior, posting.Seniority);
        Assert.Equal(RoleFamily.Backend, posting.RoleFamily);
        Assert.Equal(WorkArrangement.Hybrid, posting.WorkArrangement);
        Assert.Equal(3, posting.HybridDaysInOffice);
        Assert.Equal(70_000m, posting.AnnualSalaryMin);
        Assert.True(posting.SalaryFromText);
        Assert.Equal("outside", posting.Ir35);
        Assert.True(posting.RequiresSecurityClearance);
        Assert.Equal(EnrichedPosting.CurrentVersion, posting.EnrichmentVersion);
    }

    [Fact]
    public async Task The_closure_makes_a_rollup_an_ordinary_join()
    {
        await using var db = CreateContext();

        await Repository(db).IngestAsync(
            Context("run1.csv"),
            [
                Posting(id: "a1", description: "We use C# here."),
                Posting(id: "a2", description: "We use Java here."),
                Posting(id: "a3", description: "We use React here."),
            ],
            3, 0);

        // "How many postings want a backend skill" - one join, no recursion.
        var backend = await db.PostingConcepts
            .Where(pc => db.ConceptClosure.Any(c =>
                c.DescendantId == pc.ConceptId && c.Ancestor!.ConceptKey == "area.backend"))
            .Select(pc => pc.PostingId)
            .Distinct()
            .CountAsync();

        Assert.Equal(2, backend);
    }
}
