using JobPlatform.Core.Searches;
using JobPlatform.Data.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The search write path, against a real relational engine.
/// </summary>
/// <remarks>
/// Two things are worth pinning here and neither is a query. The first is the authorisation
/// boundary: a subject id reaches every method and there is no overload without one, so a slug
/// belonging to somebody else must be invisible rather than merely unreachable by convention.
/// The second is that a save is a genuine replace - the child rows are a set the form submits
/// whole, and a merge cannot express "stop scraping LinkedIn".
/// </remarks>
public sealed class ScraperSearchPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private const string OtherSubject = "22222222-2222-2222-2222-222222222222";

    private static readonly FakeTime Time = new(new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero));

    public ScraperSearchPersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static ScraperSearch Search(
        string name = "Software Engineer",
        string term = "software engineer",
        bool enabled = true,
        IReadOnlyList<ScraperSite>? sites = null)
        => new()
        {
            // Ignored on create - the repository assigns it - and supplied on update by the
            // route. Nothing here should ever be able to choose one.
            Slug = "ignored",
            Name = name,
            Enabled = enabled,
            SearchTerm = term,
            Sites = sites ?? [ScraperSite.Indeed, ScraperSite.LinkedIn],
            Location = "London, UK",
            HoursOld = 24,
        };

    [Fact]
    public async Task A_created_search_gets_a_slug_from_its_name()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        var view = await repository.CreateAsync(Subject, Search(), Time);

        Assert.Equal("software-engineer", view.Search.Slug);
        Assert.Equal("Software Engineer", view.Search.Name);
        Assert.Equal([ScraperSite.Indeed, ScraperSite.LinkedIn], view.Search.Sites);
    }

    /// <summary>
    /// The slug namespace is global because everything downstream of the blob name is.
    /// </summary>
    /// <remarks>
    /// Two owners, one name. Both searches have to exist and they cannot share a slug, or one
    /// person's postings would be attributed to the other's search term.
    /// </remarks>
    [Fact]
    public async Task Two_owners_may_share_a_name_but_never_a_slug()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        var mine = await repository.CreateAsync(Subject, Search(), Time);
        var theirs = await repository.CreateAsync(OtherSubject, Search(), Time);

        Assert.Equal("software-engineer", mine.Search.Slug);
        Assert.Equal("software-engineer-2", theirs.Search.Slug);
    }

    [Fact]
    public async Task A_search_is_invisible_to_another_subject()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        await repository.CreateAsync(Subject, Search(), Time);

        Assert.Empty(await repository.ListAsync(OtherSubject));
    }

    /// <summary>
    /// The authorisation boundary, tested from the outside.
    /// </summary>
    /// <remarks>
    /// The slug is real and the subject is not its owner. Both calls have to behave exactly as
    /// they would for a slug that does not exist - the caller must not be able to tell the two
    /// apart, or the API leaks which search terms other people are running.
    /// </remarks>
    [Fact]
    public async Task Another_subject_can_neither_update_nor_delete_it()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        var mine = await repository.CreateAsync(Subject, Search(), Time);

        Assert.Null(await repository.UpdateAsync(OtherSubject, mine.Search.Slug, Search(term: "hijacked"), Time));
        Assert.False(await repository.DeleteAsync(OtherSubject, mine.Search.Slug));

        var stored = Assert.Single(await repository.ListAsync(Subject));
        Assert.Equal("software engineer", stored.Search.SearchTerm);
    }

    /// <summary>A save is a replace: dropping a board has to actually drop it.</summary>
    [Fact]
    public async Task An_update_replaces_the_boards_rather_than_merging_them()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        var created = await repository.CreateAsync(Subject, Search(), Time);

        var updated = await repository.UpdateAsync(
            Subject, created.Search.Slug, Search(sites: [ScraperSite.Freehire]), Time);

        Assert.NotNull(updated);
        Assert.Equal([ScraperSite.Freehire], updated.Search.Sites);

        // Through a fresh context, so this is what the database holds rather than what the
        // change tracker remembers.
        await using var second = CreateContext();
        var reread = Assert.Single(await new ScraperSearchRepository(second).ListAsync(Subject));
        Assert.Equal([ScraperSite.Freehire], reread.Search.Sites);
    }

    /// <summary>
    /// The slug survives an update, whatever the request says.
    /// </summary>
    /// <remarks>
    /// Renaming a search is an edit; renaming its slug would orphan every posting attributed to
    /// the old one. The route supplies the slug and the body cannot override it.
    /// </remarks>
    [Fact]
    public async Task An_update_cannot_move_the_slug()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        var created = await repository.CreateAsync(Subject, Search(), Time);

        var updated = await repository.UpdateAsync(
            Subject, created.Search.Slug, Search(name: "Something Else"), Time);

        Assert.NotNull(updated);
        Assert.Equal("software-engineer", updated.Search.Slug);
        Assert.Equal("Something Else", updated.Search.Name);
    }

    /// <summary>The publisher's query: every owner's enabled searches and nobody's paused ones.</summary>
    [Fact]
    public async Task Publishing_reads_every_owners_enabled_searches()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        await repository.CreateAsync(Subject, Search(name: "Mine"), Time);
        await repository.CreateAsync(OtherSubject, Search(name: "Theirs"), Time);
        await repository.CreateAsync(Subject, Search(name: "Paused", enabled: false), Time);

        var published = await repository.ListForPublishAsync();

        Assert.Equal(["mine", "theirs"], published.Select(search => search.Slug).Order());
    }

    [Fact]
    public async Task A_name_the_caller_already_uses_is_reported_before_the_index_refuses_it()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        var created = await repository.CreateAsync(Subject, Search(), Time);

        Assert.True(await repository.NameTakenAsync(Subject, "Software Engineer"));

        // Its own name does not count against it, or no search could ever be saved twice.
        Assert.False(await repository.NameTakenAsync(Subject, "Software Engineer", created.Search.Slug));

        // And it says nothing about anybody else.
        Assert.False(await repository.NameTakenAsync(OtherSubject, "Software Engineer"));
    }

    [Fact]
    public async Task Deleting_removes_the_search_and_its_children()
    {
        await using var db = CreateContext();
        var repository = new ScraperSearchRepository(db);

        var created = await repository.CreateAsync(Subject, Search(), Time);

        Assert.True(await repository.DeleteAsync(Subject, created.Search.Slug));

        await using var second = CreateContext();
        Assert.Empty(await new ScraperSearchRepository(second).ListAsync(Subject));
        Assert.Empty(await second.ScraperSearchSites.ToListAsync());
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
