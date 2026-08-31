using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Model;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// Writing a model extraction back against its posting.
/// </summary>
/// <remarks>
/// These exist because the collision below reached production twice - once through the queue
/// consumer and once through the batch collector, which was written by copying it. Against a
/// real relational engine, so the primary key actually has to hold rather than being asserted.
/// </remarks>
public sealed class PostingExtractionWriterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    public PostingExtractionWriterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();
        ConceptSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext Context() => new(_options);

    /// <summary>Stores one posting and returns its id.</summary>
    private async Task<long> SeedPostingAsync()
    {
        await using var db = Context();

        await new JobPostingRepository(db, NullLogger<JobPostingRepository>.Instance).IngestAsync(
            new ScrapeRunContext
            {
                BlobPath = "jobs/seed.csv",
                SearchTerm = "platform-engineer",
                ScrapedAtUtc = Now,
            },
            [
                new JobPosting
                {
                    ExternalId = "x1",
                    Site = "indeed",
                    Title = "Platform Engineer",
                    Description = "We use SharePoint.",
                },
            ],
            1,
            0);

        return await db.JobPostings.Select(p => p.Id).FirstAsync();
    }

    [Fact]
    public async Task A_form_another_pass_already_recorded_does_not_collide()
    {
        // The exact production failure: PostingMentions is keyed on (PostingId, SurfaceForm)
        // and the delete before an apply is scoped to model rows, so a board-flagged form
        // survives it and owns the key the model's row wants.
        var postingId = await SeedPostingAsync();

        await using (var db = Context())
        {
            db.PostingMentions.Add(new PostingMentionEntity
            {
                PostingId = postingId,
                SurfaceForm = "SharePoint",
                Reason = MentionReason.UnknownBoardSkill,
                Occurrences = 1,
                ResolverVersion = 1,
            });

            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var writer = new PostingExtractionWriter(db);

            await writer.ApplyAsync(
                postingId,
                Hash,
                new DocumentExtraction
                {
                    Mentions = [new UnresolvedMention("SharePoint", MentionReason.UnknownModelSkill)],
                },
                await writer.GetConceptIdsAsync(),
                Now);

            await db.SaveChangesAsync();
        }

        await using var read = Context();
        var mention = Assert.Single(await read.PostingMentions.ToListAsync());

        // The surviving row is the board's. Both passes failing to place the word is one fact,
        // and the stronger evidence for it is the one an employer published.
        Assert.Equal(MentionReason.UnknownBoardSkill, mention.Reason);
    }

    [Fact]
    public async Task Two_extractions_for_one_posting_on_one_context_do_not_collide()
    {
        // The production failure that stopped the first reparse pass. PostingExtractions is keyed
        // on (PostingId, ExtractorVersion, InputHash), so an advert re-listed with edited text
        // holds several rows at the same version - and a corpus pass that reads rows rather than
        // postings applies the same posting twice on one DbContext. ExecuteDelete does not touch
        // the change tracker, so the second Add collides with the first still tracked as
        // Unchanged, and the exception names PostingConceptEntity rather than the loop that
        // caused it.
        //
        // The pass now takes the newest extraction per posting, so this should not arise. It is
        // pinned anyway, because a writer that cannot be called twice is a sharp edge for the
        // queue consumer and the batch collector too, and nothing else says so.
        var postingId = await SeedPostingAsync();

        await using var db = Context();
        var writer = new PostingExtractionWriter(db);
        var conceptIds = await writer.GetConceptIdsAsync();

        DocumentExtraction Extraction(string form) => new()
        {
            Concepts = [new ConceptAssertion("skill.sharepoint", AssertionSource.Model)],
            Mentions = [new UnresolvedMention(form, MentionReason.UnknownModelSkill)],
        };

        await writer.ApplyAsync(postingId, Hash, Extraction("first"), conceptIds, Now);
        await db.SaveChangesAsync();

        await writer.ApplyAsync(postingId, "b" + Hash[1..], Extraction("second"), conceptIds, Now);
        await db.SaveChangesAsync();

        await using var read = Context();

        // The later apply wins outright: its delete removed the earlier model rows first.
        Assert.Equal("second", (await read.PostingMentions.SingleAsync()).SurfaceForm);
    }

    [Fact]
    public async Task A_response_naming_one_form_twice_does_not_collide_with_itself()
    {
        var postingId = await SeedPostingAsync();

        await using (var db = Context())
        {
            var writer = new PostingExtractionWriter(db);

            await writer.ApplyAsync(
                postingId,
                Hash,
                new DocumentExtraction
                {
                    Mentions =
                    [
                        new UnresolvedMention("SharePoint", MentionReason.UnknownModelSkill),
                        new UnresolvedMention("sharepoint", MentionReason.UnknownModelSkill),
                    ],
                },
                await writer.GetConceptIdsAsync(),
                Now);

            await db.SaveChangesAsync();
        }

        await using var read = Context();
        Assert.Single(await read.PostingMentions.ToListAsync());
    }

    [Fact]
    public async Task Applying_twice_converges_rather_than_duplicating()
    {
        // A replayed queue message, or a batch collected twice. Both are possible and both must
        // land on the same rows rather than accumulating them.
        var postingId = await SeedPostingAsync();

        for (var i = 0; i < 2; i++)
        {
            await using var db = Context();
            var writer = new PostingExtractionWriter(db);

            await writer.ApplyAsync(
                postingId,
                Hash,
                new DocumentExtraction
                {
                    Concepts =
                    [
                        new ConceptAssertion(
                            "skill.kubernetes", AssertionSource.Model, AssertionPolarity.Required),
                    ],
                    Mentions = [new UnresolvedMention("Frobnicator", MentionReason.UnknownModelSkill)],
                },
                await writer.GetConceptIdsAsync(),
                Now);

            await db.SaveChangesAsync();
        }

        await using var read = Context();

        Assert.Single(await read.PostingConcepts.ToListAsync());
        Assert.Single(await read.PostingMentions.ToListAsync());
    }

    [Fact]
    public async Task Only_the_models_own_assertions_are_replaced()
    {
        // Board-supplied and text-matched rows are different evidence from a different pass, and
        // an extraction has no business overwriting them.
        var postingId = await SeedPostingAsync();

        await using (var db = Context())
        {
            var conceptId = await db.Concepts
                .Where(c => c.ConceptKey == "skill.python")
                .Select(c => c.Id)
                .FirstAsync();

            db.PostingConcepts.Add(new PostingConceptEntity
            {
                PostingId = postingId,
                ConceptId = conceptId,
                Source = AssertionSource.Board,
                Polarity = AssertionPolarity.Unspecified,
                ResolverVersion = 1,
            });

            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var writer = new PostingExtractionWriter(db);

            await writer.ApplyAsync(
                postingId,
                Hash,
                new DocumentExtraction
                {
                    Concepts =
                    [
                        new ConceptAssertion(
                            "skill.kubernetes", AssertionSource.Model, AssertionPolarity.Required),
                    ],
                },
                await writer.GetConceptIdsAsync(),
                Now);

            await db.SaveChangesAsync();
        }

        await using var read = Context();
        var stored = await read.PostingConcepts.Include(c => c.Concept).ToListAsync();

        Assert.Contains(stored, c => c.Source == AssertionSource.Board && c.Concept!.ConceptKey == "skill.python");
        Assert.Contains(stored, c => c.Source == AssertionSource.Model && c.Concept!.ConceptKey == "skill.kubernetes");
    }
}
