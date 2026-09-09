using System.Reflection;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using JobPlatform.Core.Matching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The store for what has been learned about employers' own applicant tracking boards.
/// </summary>
/// <remarks>
/// <b>These run against a real relational engine rather than an in-memory provider</b>, for the
/// reason <c>EmployerAtsBoardSchemaTests</c> gives: what is being asserted here is arithmetic the
/// database performs - correlated counts, an ordering, a bounded page - and an in-memory provider
/// would answer them in LINQ-to-objects and pass whether or not any of it translated to SQL.
///
/// <b>Two things are being pinned and only one of them is behaviour.</b> The first is the rules:
/// a probe never displaces a link the employer published, an unconfirmed token never reaches a
/// caller, a recovered link never lands on the column the advert's own board filled. The second is
/// that the queries translate at all - <c>ListBoardsToFetchAsync</c> orders on a correlated count
/// and pages after it, which is exactly the shape that compiles happily and throws "could not be
/// translated" the first time it is run.
///
/// <b>Nothing here is scoped to a candidate, and that is deliberate rather than missing.</b> Every
/// other store in this folder takes a profile id; this one holds corpus data about employers,
/// which is the same fact for every candidate. <see cref="The_reads_are_not_scoped_to_a_candidate"/>
/// is what stops a scope being added by reflex, since nothing else in the build would fail.
/// </remarks>
public sealed class EmployerAtsBoardStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;
    private readonly DbContextOptions<JobsDbContext> _untracked;

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

    private const int Acme = 10;
    private const int Contoso = 11;
    private const int Fabrikam = 12;
    private const int Initech = 13;
    private const int Umbrella = 14;

    private static readonly int[] Employers = [Acme, Contoso, Fabrikam, Initech, Umbrella];

    private long _nextPostingId = 1;

    public EmployerAtsBoardStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        // The host's own setting, reproduced deliberately - see WritesUnderNoTrackingTests. A
        // read-then-mutate under this saves nothing and throws nothing, which is why the writes
        // here are exercised under it as well as under the default.
        _untracked = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlite(_connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        using var db = new JobsDbContext(_options);

        db.Database.EnsureCreated();

        foreach (var id in Employers)
        {
            db.Companies.Add(new CompanyEntity
            {
                Id = id,
                CompanyKey = $"company {id}",
                DisplayName = $"Company {id}",
                // The column the boards must never drag across. Nothing in this suite reads it;
                // a test that started to would be reading the employer blurb per employer into a
                // pass that wanted a name and a token.
                Description = new string('d', 4096),
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private EmployerAtsBoardRepository CreateRepository(JobsDbContext db) => new(db);

    private static AtsBoard Board(
        string token = "acme",
        AtsVendor vendor = AtsVendor.Greenhouse,
        AtsBoardRegion region = AtsBoardRegion.Default)
        => new(vendor, token, region);

    private static AtsListing Listing(string applyUrl)
        => new("Senior Platform Engineer", "London", applyUrl);

    /// <summary>Adds postings for one employer, blocked unless a link is given.</summary>
    private async Task<long[]> AddPostingsAsync(
        int companyId,
        int count,
        string? jobUrlDirect = null,
        bool? offsiteApply = null,
        string? employerAtsApplyUrl = null,
        DateTimeOffset? lastSeenUtc = null,
        AtsVendor? applyVendor = null)
    {
        await using var db = CreateContext();

        var ids = new long[count];

        for (var index = 0; index < count; index++)
        {
            var id = _nextPostingId++;

            ids[index] = id;

            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"linkedin:{id}",
                Site = "linkedin",
                ExternalId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ContentHash = id.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0'),
                Title = "Senior Platform Engineer",
                Company = $"Company {companyId}",
                CompanyId = companyId,
                LocationCity = "London",
                JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
                JobUrlDirect = jobUrlDirect,
                OffsiteApply = offsiteApply,
                EmployerAtsApplyUrl = employerAtsApplyUrl,
                ApplyVendor = applyVendor,
                FirstSeenUtc = Now,
                LastSeenUtc = lastSeenUtc ?? Now,
            });
        }

        await db.SaveChangesAsync();

        return ids;
    }

    /// <summary>Writes a board row straight to the table, past everything this file is testing.</summary>
    private async Task AddBoardAsync(
        int companyId,
        AtsVendor vendor,
        string token,
        AtsBoardDiscovery discovery,
        DateTimeOffset? confirmedAtUtc,
        DateTimeOffset? lastFetchedUtc = null)
    {
        await using var db = CreateContext();

        db.EmployerAtsBoards.Add(new EmployerAtsBoardEntity
        {
            CompanyId = companyId,
            Vendor = vendor,
            Token = token,
            Region = AtsBoardRegion.Default,
            Discovery = discovery,
            DiscoveredAtUtc = Now.AddDays(-30),
            ConfirmedAtUtc = confirmedAtUtc,
            LastFetchedUtc = lastFetchedUtc,
        });

        await db.SaveChangesAsync();
    }

    private async Task<EmployerAtsBoardEntity> StoredAsync(int companyId)
    {
        await using var db = CreateContext();

        return await db.EmployerAtsBoards.SingleAsync(b => b.CompanyId == companyId);
    }

    private async Task<JobPostingEntity> PostingAsync(long id)
    {
        await using var db = CreateContext();

        return await db.JobPostings.SingleAsync(p => p.Id == id);
    }

    // -----------------------------------------------------------------------
    // Learning a board from a link the employer published
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_board_learned_from_a_link_is_usable_the_moment_it_is_learned()
    {
        var learned = AtsBoardToken.FromUrl("https://boards.greenhouse.io/acme/jobs/4012345");

        Assert.NotNull(learned);

        await using (var db = CreateContext())
        {
            Assert.True(await CreateRepository(db).LearnAsync(Acme, learned, Now));
        }

        var stored = await StoredAsync(Acme);

        // The confirmation is the link itself: the token was lifted out of a URL the board
        // carrying the advert published as this employer's apply link, so the evidence exists and
        // its date is the date it was read. It is also the only route by which a Lever board is
        // ever usable, because Lever's feed publishes no name to agree with.
        Assert.Equal(AtsBoardDiscovery.Learned, stored.Discovery);
        Assert.Equal(Now, stored.ConfirmedAtUtc);
        Assert.Equal(Now, stored.DiscoveredAtUtc);
        Assert.Null(stored.LastFetchedUtc);
    }

    [Fact]
    public async Task Learning_a_board_twice_writes_nothing_the_second_time()
    {
        await using var db = CreateContext();
        var store = CreateRepository(db);

        Assert.True(await store.LearnAsync(Acme, Board(), Now));

        // The learned path runs over links already held, so it meets the same board every pass. A
        // version of this that re-stamped would make every date in the table say "today" and would
        // erase the only evidence that a board has been quiet since March.
        Assert.False(await store.LearnAsync(Acme, Board(), Now.AddDays(7)));

        var stored = await StoredAsync(Acme);

        Assert.Equal(Now, stored.ConfirmedAtUtc);
        Assert.Equal(Now, stored.DiscoveredAtUtc);
    }

    [Fact]
    public async Task A_link_upgrades_a_token_that_was_only_ever_probed()
    {
        await AddBoardAsync(Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Probed, confirmedAtUtc: null);

        await using (var db = CreateContext())
        {
            Assert.True(await CreateRepository(db).LearnAsync(Acme, Board(), Now));
        }

        var stored = await StoredAsync(Acme);

        // Learned beats probed: a token read off a link the employer published is a fact, and the
        // guess it displaces was worth exactly one request. The discovery date does not move,
        // because "when did we start believing this" is the first question asked about a board
        // that turns out to belong to somebody else.
        Assert.Equal(AtsBoardDiscovery.Learned, stored.Discovery);
        Assert.Equal(Now, stored.ConfirmedAtUtc);
        Assert.Equal(Now.AddDays(-30), stored.DiscoveredAtUtc);
    }

    [Fact]
    public async Task A_probe_never_touches_a_board_the_employer_published()
    {
        await using var db = CreateContext();
        var store = CreateRepository(db);

        Assert.True(await store.LearnAsync(Acme, Board(), Now));

        // The same token reached by the other path, later. Nothing about the row may move: not the
        // discovery, which would turn a fact back into a guess; not the confirmation, which the
        // link earned; not the date, which is when the believing started.
        Assert.False(await store.RecordProbeAsync(Acme, Board(), Now.AddDays(7)));

        var stored = await StoredAsync(Acme);

        Assert.Equal(AtsBoardDiscovery.Learned, stored.Discovery);
        Assert.Equal(Now, stored.ConfirmedAtUtc);
        Assert.Equal(Now, stored.DiscoveredAtUtc);
    }

    [Fact]
    public async Task The_same_token_on_two_regions_is_learned_as_two_boards()
    {
        var european = AtsBoardToken.FromUrl("https://jobs.eu.lever.co/acme/8f2c1a44-0d3e-4c11-9a77-1b2c3d4e5f60");
        var ordinary = AtsBoardToken.FromUrl("https://jobs.lever.co/acme/8f2c1a44-0d3e-4c11-9a77-1b2c3d4e5f60");

        Assert.NotNull(european);
        Assert.NotNull(ordinary);

        await using (var db = CreateContext())
        {
            var store = CreateRepository(db);

            Assert.True(await store.LearnAsync(Acme, european, Now));

            // Not "already known". They are two boards on two API hosts, and folding them would
            // send one employer's postings to an endpoint that answers nothing - which reads as
            // "not on Lever" rather than as an error, so no count moves.
            Assert.True(await store.LearnAsync(Acme, ordinary, Now));
        }

        await using var read = CreateContext();

        Assert.Equal(2, await read.EmployerAtsBoards.CountAsync(b => b.CompanyId == Acme));
    }

    // -----------------------------------------------------------------------
    // A probe, and the confirmation it owes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_probe_is_stored_unconfirmed_and_is_not_offered_for_fetching()
    {
        await AddPostingsAsync(Contoso, 2);

        await using (var db = CreateContext())
        {
            Assert.True(await CreateRepository(db).RecordProbeAsync(Contoso, Board("contoso"), Now));
        }

        var stored = await StoredAsync(Contoso);

        Assert.Equal(AtsBoardDiscovery.Probed, stored.Discovery);
        Assert.Null(stored.ConfirmedAtUtc);

        await using var read = CreateContext();

        // The row exists so the next pass does not spend the request again, and it may not be
        // fetched through: "Dex", "Kernel", "Fin" and "Orbital" are all real boards belonging to
        // somebody, not necessarily to the employer on the advert.
        Assert.Empty(await CreateRepository(read).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10));
    }

    [Fact]
    public async Task A_confirmation_without_evidence_writes_nothing()
    {
        await AddPostingsAsync(Contoso, 1);
        await AddBoardAsync(Contoso, AtsVendor.Greenhouse, "contoso", AtsBoardDiscovery.Probed, confirmedAtUtc: null);

        await using (var db = CreateContext())
        {
            // Zero refuses. A value nobody set, a struct default and a member deserialised from
            // nothing all arrive as Unconfirmed, and every one of them costs a recovered link
            // rather than sending a covering letter to a stranger.
            Assert.False(await CreateRepository(db)
                .ConfirmAsync(Contoso, Board("contoso"), AtsBoardConfidence.Unconfirmed, Now));
        }

        Assert.Null((await StoredAsync(Contoso)).ConfirmedAtUtc);

        await using var read = CreateContext();

        Assert.Empty(await CreateRepository(read).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10));
    }

    [Fact]
    public async Task The_first_confirmation_stands()
    {
        await AddPostingsAsync(Contoso, 1);
        await AddBoardAsync(Contoso, AtsVendor.Greenhouse, "contoso", AtsBoardDiscovery.Probed, confirmedAtUtc: null);

        await using (var db = CreateContext())
        {
            var store = CreateRepository(db);

            Assert.True(await store.ConfirmAsync(Contoso, Board("contoso"), AtsBoardConfidence.NameAgrees, Now));

            // A date that crept forward on every pass would become "today" and stop being able to
            // answer the question it exists for. Freshness is LastFetchedUtc, a different column.
            Assert.False(await store.ConfirmAsync(
                Contoso, Board("contoso"), AtsBoardConfidence.NameAndPostingAgree, Now.AddDays(3)));
        }

        Assert.Equal(Now, (await StoredAsync(Contoso)).ConfirmedAtUtc);

        await using var read = CreateContext();

        var offered = Assert.Single(
            await CreateRepository(read).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10));

        Assert.Equal(Contoso, offered.CompanyId);
    }

    // -----------------------------------------------------------------------
    // Which boards are worth a request
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_board_blocking_the_most_postings_comes_first()
    {
        await AddPostingsAsync(Acme, 3);
        await AddPostingsAsync(Contoso, 1);
        await AddPostingsAsync(Umbrella, 2);

        await AddBoardAsync(Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Contoso, AtsVendor.Ashby, "contoso", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Umbrella, AtsVendor.Lever, "umbrella", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        await using var db = CreateContext();

        var boards = await CreateRepository(db).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10);

        // This exists to unblock applications rather than to fill a table, and the corpus is
        // lopsided: one board answers 333 jobs and several employers hold a single posting each.
        Assert.Equal([Acme, Umbrella, Contoso], boards.Select(b => b.CompanyId));
        Assert.Equal([3, 2, 1], boards.Select(b => b.BlockedPostings));

        // The whole identity comes back, so the caller needs nothing else to build the request.
        Assert.Equal(new AtsBoard(AtsVendor.Lever, "umbrella"), boards[1].Board);
    }

    [Fact]
    public async Task A_board_never_asked_leads_one_asked_before()
    {
        await AddPostingsAsync(Acme, 2);
        await AddPostingsAsync(Contoso, 2);

        await AddBoardAsync(
            Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30),
            lastFetchedUtc: Now.AddDays(-5));

        await AddBoardAsync(Contoso, AtsVendor.Ashby, "contoso", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        await using var db = CreateContext();

        var boards = await CreateRepository(db).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10);

        // Equal on the count, so the tie-break decides - and it is coalesced rather than left to
        // an engine's idea of where a null sorts, because SQL Server and SQLite agreeing today is
        // not something a page's contents should depend on.
        Assert.Equal([Contoso, Acme], boards.Select(b => b.CompanyId));
        Assert.Null(boards[0].LastFetchedUtc);
        Assert.Equal(Now.AddDays(-5), boards[1].LastFetchedUtc);
    }

    [Fact]
    public async Task A_board_asked_inside_the_window_is_not_offered_again()
    {
        await AddPostingsAsync(Acme, 2);

        await AddBoardAsync(
            Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30),
            lastFetchedUtc: Now.AddHours(-1));

        await using var db = CreateContext();

        var store = CreateRepository(db);

        // One request per employer per pass is the bound the whole feature runs under: the board
        // answers every vacancy in one call, so asking again inside the window is the same bytes
        // twice from an API that exists to serve that vendor's own customers.
        Assert.Empty(await store.ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10));
        Assert.Single(await store.ListBoardsToFetchAsync(Now, TimeSpan.FromMinutes(30), limit: 10));
    }

    [Fact]
    public async Task An_employer_whose_postings_all_have_links_is_not_worth_a_request()
    {
        // The three ways a posting is not blocked, one per employer: the advert's own board
        // published the employer's link; a pass has already recovered one; and the board says it
        // hosts the application itself, which is a statement about this listing rather than about
        // one that resembles it.
        await AddPostingsAsync(Acme, 2, jobUrlDirect: "https://boards.greenhouse.io/acme/jobs/1");
        await AddPostingsAsync(Contoso, 2, employerAtsApplyUrl: "https://jobs.ashbyhq.com/contoso/1");
        await AddPostingsAsync(Umbrella, 2, offsiteApply: false);

        // And the state that is not one of them: nothing was established about where the
        // application is made, which is the ordinary state of a posting nobody opened.
        await AddPostingsAsync(Fabrikam, 1, offsiteApply: null);

        await AddBoardAsync(Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Contoso, AtsVendor.Ashby, "contoso", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Umbrella, AtsVendor.Lever, "umbrella", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Fabrikam, AtsVendor.SmartRecruiters, "Fabrikam", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        await using var db = CreateContext();

        var offered = Assert.Single(
            await CreateRepository(db).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10));

        Assert.Equal(Fabrikam, offered.CompanyId);

        // Case preserved on the way out, because SmartRecruiters keys its listings on a
        // case-sensitive company id and a folded token resolves to nothing at all.
        Assert.Equal("Fabrikam", offered.Board.Token);
    }

    [Fact]
    public async Task A_board_on_a_vendor_that_publishes_no_listing_is_dropped_rather_than_thrown_over()
    {
        await AddPostingsAsync(Acme, 5);
        await AddPostingsAsync(Contoso, 1);

        // Nothing at the database refuses this - a check constraint would rot the day a sixth
        // vendor joins the list - so a row naming Workday is possible, and AtsBoard's constructor
        // would refuse it inside the projection and take the whole page with it.
        await AddBoardAsync(Acme, AtsVendor.Workday, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Contoso, AtsVendor.Greenhouse, "contoso", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        await using var db = CreateContext();

        var offered = Assert.Single(
            await CreateRepository(db).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10));

        // The unreadable board sorted first on the count and is gone; the one behind it survived.
        Assert.Equal(Contoso, offered.CompanyId);
    }

    [Fact]
    public async Task The_fetch_list_carries_the_employer_name_a_confirmation_is_made_against()
    {
        await AddPostingsAsync(Acme, 1);
        await AddBoardAsync(Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Probed, Now.AddDays(-30));

        await using var db = CreateContext();

        var offered = Assert.Single(
            await CreateRepository(db).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10));

        // AtsBoardCandidates.Confirm takes the employer's name, and a caller sent to fetch it
        // would go to Companies - where the unbounded blurb lives. One bounded column instead.
        Assert.Equal("Company 10", offered.Company);
    }

    [Fact]
    public async Task A_posting_seen_too_long_ago_is_not_worth_a_request()
    {
        await AddPostingsAsync(Acme, 2, lastSeenUtc: Now.AddDays(-90));
        await AddPostingsAsync(Contoso, 1);

        await AddBoardAsync(Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Contoso, AtsVendor.Ashby, "contoso", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        await using var db = CreateContext();

        var offered = Assert.Single(await CreateRepository(db)
            .ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10, seenSince: Now.AddDays(-30)));

        Assert.Equal(Contoso, offered.CompanyId);
    }

    // -----------------------------------------------------------------------
    // Which employers are still worth a guess
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_employer_with_any_board_row_is_not_probed_again()
    {
        await AddPostingsAsync(Acme, 2);
        await AddPostingsAsync(Contoso, 2);
        await AddPostingsAsync(Initech, 2);

        await AddBoardAsync(Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        // Answered, and named a different company. The row is worth keeping precisely because the
        // request was already spent on an API doing us a favour by answering at all.
        await AddBoardAsync(Contoso, AtsVendor.Greenhouse, "orbital", AtsBoardDiscovery.Probed, confirmedAtUtc: null);

        await using var db = CreateContext();

        var employer = Assert.Single(await CreateRepository(db).ListEmployersToProbeAsync(limit: 10));

        Assert.Equal(Initech, employer.CompanyId);
    }

    [Fact]
    public async Task Employers_blocking_the_most_postings_are_probed_first()
    {
        await AddPostingsAsync(Initech, 3);
        await AddPostingsAsync(Umbrella, 1);
        await AddPostingsAsync(Contoso, 2);

        // Nothing blocked, so nothing to probe for.
        await AddPostingsAsync(Fabrikam, 4, jobUrlDirect: "https://jobs.lever.co/fabrikam/1");

        await using var db = CreateContext();

        var employers = await CreateRepository(db).ListEmployersToProbeAsync(limit: 10);

        // Probing is the weaker half of this feature - 21 of 120 against 122 of 309 from links
        // already held - so the budget it spends goes where the most postings are stuck.
        Assert.Equal([Initech, Contoso, Umbrella], employers.Select(e => e.CompanyId));
        Assert.Equal([3, 2, 1], employers.Select(e => e.BlockedPostings));

        // The name is what AtsBoardCandidates.For builds a token out of, folded by
        // CompanyNormalizer rather than taken raw off one advert.
        Assert.Equal("Company 13", employers[0].Company);
    }

    [Fact]
    public async Task A_bounded_read_is_bounded()
    {
        await AddPostingsAsync(Initech, 3);
        await AddPostingsAsync(Contoso, 2);
        await AddPostingsAsync(Umbrella, 1);

        await using var db = CreateContext();

        var store = CreateRepository(db);

        Assert.Equal([Initech, Contoso], (await store.ListEmployersToProbeAsync(limit: 2)).Select(e => e.CompanyId));
        Assert.Empty(await store.ListEmployersToProbeAsync(limit: 0));
        Assert.Empty(await store.ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 0));
        Assert.Empty(await store.ListSilentBoardsAsync(Now, TimeSpan.FromDays(1), limit: 0));
    }

    // -----------------------------------------------------------------------
    // The recovered link, beside the published one
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_recovered_link_never_touches_the_column_the_board_published()
    {
        const string published = "https://boards.greenhouse.io/acme/jobs/4012345";
        const string recovered = "https://boards.greenhouse.io/acme/jobs/4012999";

        var quiet = (await AddPostingsAsync(Acme, 1))[0];
        var loud = (await AddPostingsAsync(Acme, 1, jobUrlDirect: published))[0];

        await using (var db = CreateContext())
        {
            var stamped = await CreateRepository(db).RecordMatchesAsync(
                [
                    new AtsPostingMatch(quiet, AtsListingMatch.For(Listing(recovered), AtsMatchConfidence.TitleAndPlace)),
                    new AtsPostingMatch(loud, AtsListingMatch.For(Listing(recovered), AtsMatchConfidence.TitleOnly)),
                ],
                Now);

            Assert.Equal(2, stamped);
        }

        var withoutLink = await PostingAsync(quiet);
        var withLink = await PostingAsync(loud);

        // The posting whose board published nothing gains a link and keeps its empty column: the
        // absence of JobUrlDirect is still the fact that LinkedIn published no apply URL.
        Assert.Null(withoutLink.JobUrlDirect);
        Assert.Equal(recovered, withoutLink.EmployerAtsApplyUrl);
        Assert.Equal(AtsMatchConfidence.TitleAndPlace, withoutLink.EmployerAtsMatchConfidence);
        Assert.Equal(Now, withoutLink.EmployerAtsCheckedUtc);

        // And the one whose board did publish one keeps it, unchanged and distinguishable. Both
        // links open a form, so the column is the only thing that can say afterwards which of them
        // was the inference.
        Assert.Equal(published, withLink.JobUrlDirect);
        Assert.Equal(recovered, withLink.EmployerAtsApplyUrl);
        Assert.Equal(AtsMatchConfidence.TitleOnly, withLink.EmployerAtsMatchConfidence);
    }

    /// <summary>
    /// The vendor column moves with the link, because that is what recovering one is worth.
    /// </summary>
    /// <remarks>
    /// <b>A recovered link is exactly the event that changes the answer.</b> The row was a
    /// LinkedIn posting page - which <c>AtsVendorDetector</c> reads as <c>Aggregator</c>, because
    /// LinkedIn is another job board whatever else it is - and it is now an employer's own form.
    /// The shortlist's facet hides aggregators, so a recovery that left the column alone would be
    /// invisible on the page it was built for: the posting would stay hidden behind the facet
    /// while carrying a perfectly good employer link.
    ///
    /// <b>And it must not move where the advert's own board published a link.</b> That is the top
    /// rung of the ladder, so <c>EmployerAtsApplyUrl</c> is not the link such a posting hands
    /// over and its vendor is not the one to write. The guard is in SQL rather than assumed from
    /// the work list: the list is built one board fetch earlier and a scrape in between may have
    /// filled the column.
    /// </remarks>
    [Fact]
    public async Task A_recovered_link_moves_the_vendor_only_where_the_board_published_none()
    {
        const string published = "https://jobs.lever.co/acme/9f3c";
        const string recovered = "https://boards.greenhouse.io/acme/jobs/4012999";

        var quiet = (await AddPostingsAsync(Acme, 1, applyVendor: AtsVendor.Aggregator))[0];

        var loud = (await AddPostingsAsync(
            Acme, 1, jobUrlDirect: published, applyVendor: AtsVendor.Lever))[0];

        await using (var db = CreateContext())
        {
            Assert.Equal(2, await CreateRepository(db).RecordMatchesAsync(
                [
                    new AtsPostingMatch(quiet, AtsListingMatch.For(Listing(recovered), AtsMatchConfidence.TitleAndPlace)),
                    new AtsPostingMatch(loud, AtsListingMatch.For(Listing(recovered), AtsMatchConfidence.TitleOnly)),
                ],
                Now));
        }

        // The link-less posting stops being an aggregator row, which is the whole point of the
        // recovery pass reaching the dashboard.
        Assert.Equal(AtsVendor.Greenhouse, (await PostingAsync(quiet)).ApplyVendor);

        // The one whose own board published a link keeps that link's vendor. Its stored apply URL
        // is still the published one; the recovered column beside it is not what it hands over.
        Assert.Equal(AtsVendor.Lever, (await PostingAsync(loud)).ApplyVendor);
    }

    [Fact]
    public async Task A_posting_the_board_had_nothing_for_is_stamped_as_asked()
    {
        var ids = await AddPostingsAsync(Acme, 2);

        await using (var db = CreateContext())
        {
            Assert.True(await CreateRepository(db).RecordMatchAsync(ids[0], AtsListingMatch.None, Now));
        }

        var asked = await PostingAsync(ids[0]);
        var never = await PostingAsync(ids[1]);

        // Without the stamp a null link means either "the board was read and had nothing" or
        // "nobody has asked yet", and those want opposite work - the first is settled until the
        // board changes, the second is the pass's entire work list.
        Assert.Equal(Now, asked.EmployerAtsCheckedUtc);
        Assert.Null(asked.EmployerAtsApplyUrl);
        Assert.Null(asked.EmployerAtsMatchConfidence);
        Assert.Null(never.EmployerAtsCheckedUtc);
    }

    [Fact]
    public async Task An_abstention_is_recorded_as_asked_and_writes_no_link()
    {
        var id = (await AddPostingsAsync(Acme, 1))[0];

        var ambiguous = AtsListingMatch.Abstained(
        [
            Listing("https://boards.greenhouse.io/acme/jobs/1"),
            Listing("https://boards.greenhouse.io/acme/jobs/2"),
        ]);

        await using (var db = CreateContext())
        {
            Assert.True(await CreateRepository(db).RecordMatchAsync(id, ambiguous, Now));
        }

        var stamped = await PostingAsync(id);

        // One employer advertising one title in several cities is better than a quarter of the
        // cross-board candidates, and settling it on board order would dress a coin toss as
        // arithmetic. The stamp is the only trace the abstention leaves on the row.
        Assert.Equal(Now, stamped.EmployerAtsCheckedUtc);
        Assert.Null(stamped.EmployerAtsApplyUrl);
    }

    [Fact]
    public async Task A_pass_that_finds_nothing_does_not_erase_what_an_earlier_one_found()
    {
        const string recovered = "https://jobs.ashbyhq.com/acme/8f2c1a44-0d3e-4c11-9a77-1b2c3d4e5f60";

        var id = (await AddPostingsAsync(Acme, 1))[0];

        await using (var db = CreateContext())
        {
            var store = CreateRepository(db);

            await store.RecordMatchAsync(id, AtsListingMatch.For(Listing(recovered), AtsMatchConfidence.TitleAndPlace), Now);

            // The vacancy closed, or the title match went ambiguous this time. The two look
            // identical from here, and clearing a good link on the second costs an application to
            // save a stale link that costs a click.
            await store.RecordMatchAsync(id, AtsListingMatch.None, Now.AddDays(1));
        }

        var stamped = await PostingAsync(id);

        Assert.Equal(recovered, stamped.EmployerAtsApplyUrl);
        Assert.Equal(AtsMatchConfidence.TitleAndPlace, stamped.EmployerAtsMatchConfidence);
        Assert.Equal(Now.AddDays(1), stamped.EmployerAtsCheckedUtc);
    }

    [Fact]
    public async Task A_recovered_link_too_long_for_its_column_is_refused_rather_than_truncated()
    {
        var id = (await AddPostingsAsync(Acme, 1))[0];
        var overlong = "https://boards.greenhouse.io/acme/jobs/" + new string('9', 1000);

        await using var db = CreateContext();

        // A truncated apply URL is worse than a refused one: it is still a link, it still opens
        // something, and what it opens is not the employer's form. FormAnswerRepository makes the
        // same choice, and SubmissionRepository the opposite one, correctly.
        await Assert.ThrowsAsync<ArgumentException>(() => CreateRepository(db)
            .RecordMatchAsync(id, AtsListingMatch.For(Listing(overlong), AtsMatchConfidence.TitleOnly), Now));

        Assert.Null((await PostingAsync(id)).EmployerAtsApplyUrl);
    }

    [Fact]
    public async Task A_posting_named_twice_in_one_board_read_is_refused()
    {
        var id = (await AddPostingsAsync(Acme, 1))[0];

        await using var db = CreateContext();

        // A batch that says two things about one row has no correct order to apply them in, and
        // the wrong one is a link written and then silently withdrawn.
        await Assert.ThrowsAsync<ArgumentException>(() => CreateRepository(db).RecordMatchesAsync(
            [
                new AtsPostingMatch(id, AtsListingMatch.For(Listing("https://boards.greenhouse.io/acme/jobs/1"), AtsMatchConfidence.TitleOnly)),
                new AtsPostingMatch(id, AtsListingMatch.None),
            ],
            Now));

        Assert.Null((await PostingAsync(id)).EmployerAtsCheckedUtc);
    }

    [Fact]
    public async Task Writes_land_under_a_host_that_tracks_nothing()
    {
        var id = (await AddPostingsAsync(Acme, 1))[0];

        await using (var db = new JobsDbContext(_untracked))
        {
            var store = CreateRepository(db);

            Assert.True(await store.LearnAsync(Acme, Board(), Now));
            Assert.True(await store.RecordFetchAsync(Acme, Board(), Now.AddHours(1)));
            Assert.True(await store.RecordMatchAsync(
                id,
                AtsListingMatch.For(Listing("https://boards.greenhouse.io/acme/jobs/1"), AtsMatchConfidence.TitleOnly),
                Now.AddHours(1)));
        }

        // Read back through a context that did not write them: reading through the writer would be
        // answered from the change tracker and would pass whether or not anything reached the
        // database. This host ran a global NoTracking under which four write paths saved nothing.
        var stored = await StoredAsync(Acme);

        Assert.Equal(Now, stored.ConfirmedAtUtc);
        Assert.Equal(Now.AddHours(1), stored.LastFetchedUtc);
        Assert.Equal("https://boards.greenhouse.io/acme/jobs/1", (await PostingAsync(id)).EmployerAtsApplyUrl);
    }

    // -----------------------------------------------------------------------
    // A board that has stopped answering
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_board_that_has_stopped_answering_is_told_from_one_nobody_has_asked_about()
    {
        var live = await AddPostingsAsync(Acme, 2);
        await AddPostingsAsync(Contoso, 3);
        await AddPostingsAsync(Initech, 1);

        await AddBoardAsync(
            Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30),
            lastFetchedUtc: Now.AddDays(-10));

        await AddBoardAsync(
            Contoso, AtsVendor.Lever, "contoso", AtsBoardDiscovery.Learned, Now.AddDays(-30),
            lastFetchedUtc: Now.AddDays(-10));

        // Never asked. Not a dead board - an employer still owed a request - and the type this
        // read returns cannot express it, so it cannot arrive in a report of dead ones.
        await AddBoardAsync(Initech, AtsVendor.Ashby, "initech", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        // What a live board leaves behind: the employer's postings were matched against it. The
        // quiet one left nothing, which is the whole of the evidence - none of the three columns
        // on the board row means "it answered", and reusing the confirmation as an aliveness clock
        // would report every learned Lever board as dead the day after it was first fetched.
        await using (var db = CreateContext())
        {
            await CreateRepository(db).RecordMatchAsync(live[0], AtsListingMatch.None, Now.AddDays(-10));
        }

        await using var read = CreateContext();

        var silent = Assert.Single(
            await CreateRepository(read).ListSilentBoardsAsync(Now, TimeSpan.FromDays(2), limit: 10));

        Assert.Equal(Contoso, silent.CompanyId);
        Assert.Equal(new AtsBoard(AtsVendor.Lever, "contoso"), silent.Board);
        Assert.Equal(Now.AddDays(-10), silent.LastFetchedUtc);

        // What the silence is costing, so a report can say so rather than only name a token.
        Assert.Equal(3, silent.BlockedPostings);
    }

    [Fact]
    public async Task A_board_asked_moments_ago_has_not_gone_quiet()
    {
        await AddPostingsAsync(Acme, 1);

        await AddBoardAsync(
            Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30),
            lastFetchedUtc: Now.AddMinutes(-5));

        await using var db = CreateContext();

        // Mid-pass, not dead. The window is what separates a board that has stopped answering from
        // one whose answer has not been recorded yet.
        Assert.Empty(await CreateRepository(db).ListSilentBoardsAsync(Now, TimeSpan.FromDays(2), limit: 10));
    }

    [Fact]
    public async Task An_attempt_is_recorded_whether_or_not_the_board_answered()
    {
        await AddPostingsAsync(Acme, 1);

        await using (var db = CreateContext())
        {
            var store = CreateRepository(db);

            await store.LearnAsync(Acme, Board(), Now.AddDays(-30));

            // Recorded before the request, so a board that times out is not asked again for every
            // posting that employer has - and so "nothing checked since the board was asked"
            // cannot be produced by the order two writes happened to land in.
            Assert.True(await store.RecordFetchAsync(Acme, Board(), Now.AddDays(-10)));
        }

        var stored = await StoredAsync(Acme);

        // The fetch does not confirm and does not un-confirm: answering proves the token resolves,
        // which is not the claim that the board is this employer's.
        Assert.Equal(Now.AddDays(-10), stored.LastFetchedUtc);
        Assert.Equal(Now.AddDays(-30), stored.ConfirmedAtUtc);

        await using var read = CreateContext();

        var silent = Assert.Single(
            await CreateRepository(read).ListSilentBoardsAsync(Now, TimeSpan.FromDays(2), limit: 10));

        Assert.Equal(Acme, silent.CompanyId);
    }

    [Fact]
    public async Task A_fetch_of_a_board_nobody_has_learned_writes_nothing()
    {
        await using var db = CreateContext();

        var store = CreateRepository(db);

        Assert.False(await store.RecordFetchAsync(Acme, Board(), Now));
        Assert.False(await store.ConfirmAsync(Acme, Board(), AtsBoardConfidence.NameAgrees, Now));
        Assert.Equal(0, await db.EmployerAtsBoards.CountAsync());
    }

    // -----------------------------------------------------------------------
    // What this store is not
    // -----------------------------------------------------------------------

    [Fact]
    public void The_reads_are_not_scoped_to_a_candidate()
    {
        var scoped = typeof(EmployerAtsBoardRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Where(parameter =>
                parameter.Name?.Contains("profile", StringComparison.OrdinalIgnoreCase) == true
                || parameter.ParameterType.Name.Contains("Profile", StringComparison.Ordinal))
            .Select(parameter => $"{parameter.Member.Name}({parameter.Name})")
            .ToArray();

        // Every other store in this folder takes a profile id, so adding one here is the reflex
        // and nothing else in the build would fail. There is nothing to scope: a board token is a
        // fact about an employer, the same for every candidate - and a per-candidate work list
        // multiplies the requests to somebody else's API by the number of candidates, to fetch
        // bytes that were already fetched.
        Assert.Empty(scoped);
    }

    /// <summary>
    /// A recovery is recorded under the retry policy the Functions host actually configures.
    /// </summary>
    /// <remarks>
    /// <b>Written because the suite could not see the fault it is about.</b> Both hosts register
    /// the context with <c>EnableRetryOnFailure</c>, and that execution strategy REFUSES a
    /// user-initiated transaction - it throws <see cref="InvalidOperationException"/> the moment a
    /// statement runs inside one. <c>RecordMatchesAsync</c> needs a transaction, because the
    /// no-match stamp and the matched rows must land together, so it has to open one THROUGH the
    /// strategy rather than beside it.
    ///
    /// <b>The failure it prevents is silent and total.</b> The first board read of the first nightly
    /// pass would throw before anything was stamped; the pass catches <c>DbUpdateException</c> and
    /// not this, so it escapes the whole invocation before the summary is logged. No link is ever
    /// recovered, all 309 link-less postings stay link-less every night, and the only symptom is a
    /// function that failed in the dark.
    ///
    /// These tests otherwise build a context with no execution strategy at all, which is how the
    /// fault hid - the same shape as the host's global NoTracking, which four repositories were
    /// silently saving nothing under until a fixture stopped reproducing the fault and started
    /// catching it. So this one configures the strategy on purpose.
    /// </remarks>
    [Fact]
    public async Task A_recovery_is_recorded_under_the_hosts_own_retry_policy()
    {
        var retrying = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlite(
                _connection,
                sqlite => sqlite.ExecutionStrategy(d => new TestRetryingExecutionStrategy(d)))
            .Options;

        var postings = await AddPostingsAsync(Acme, 2);

        await using (var db = new JobsDbContext(retrying))
        {
            var stamped = await new EmployerAtsBoardRepository(db).RecordMatchesAsync(
                [
                    new AtsPostingMatch(
                        postings[0],
                        AtsListingMatch.For(
                            Listing("https://boards.greenhouse.io/acme/jobs/1"),
                            AtsMatchConfidence.TitleAndPlace)),

                    // The posting the board had nothing for. It shares the write, which is the
                    // whole reason a transaction is here at all.
                    new AtsPostingMatch(postings[1], AtsListingMatch.None),
                ],
                Now);

            Assert.Equal(2, stamped);
        }

        // Read back through a second context, so the assertion is about what reached the database
        // rather than about what the change tracker remembers.
        Assert.Equal(
            "https://boards.greenhouse.io/acme/jobs/1",
            (await PostingAsync(postings[0])).EmployerAtsApplyUrl);

        // And the posting nothing matched is stamped as asked, which is what stops the pass
        // re-reading the same employer every night.
        var unmatched = await PostingAsync(postings[1]);

        Assert.Null(unmatched.EmployerAtsApplyUrl);
        Assert.Equal(Now, unmatched.EmployerAtsCheckedUtc);
    }

    /// <summary>A strategy that retries, so the refusal this suite is about is reachable on SQLite.</summary>
    /// <remarks>
    /// <c>SqlServerRetryingExecutionStrategy</c> cannot be used against SQLite, and the property
    /// that matters is not which errors it retries but that <c>RetriesOnFailure</c> is true - that
    /// alone is what makes EF refuse a user-initiated transaction. Nothing here needs a retry to
    /// actually happen.
    /// </remarks>
    private sealed class TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    /// <summary>Makes a posting one somebody would actually apply to.</summary>
    /// <remarks>
    /// The liveness triple <c>ListApplyableAsync</c> uses: assessed, judged at least
    /// <see cref="CandidacyVerdict.Possible"/>, and not dismissed.
    /// </remarks>
    private async Task WantAsync(params long[] postingIds)
    {
        await using var db = CreateContext();

        if (!await db.CandidateProfiles.AnyAsync())
        {
            db.CandidateProfiles.Add(new CandidateProfileEntity
            {
                Id = 1,
                SubjectId = "subject",
                CreatedUtc = Now,
                UpdatedUtc = Now,
            });

            await db.SaveChangesAsync();
        }

        foreach (var postingId in postingIds)
        {
            db.JobMatches.Add(new JobMatchEntity
            {
                ProfileId = 1,
                PostingId = postingId,
                Score = 90,
                ScoredAtUtc = Now,
                Verdict = CandidacyVerdict.Strong,
                AssessedAtUtc = Now,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A board blocking one posting somebody would apply to outranks one blocking more postings
    /// nobody would.
    /// </summary>
    /// <remarks>
    /// <b>Written from a measurement rather than from a reading of the code, and the code read
    /// fine.</b> The first pass ordered by how many blocked postings an employer had in the corpus,
    /// which is a different question from the one the feature exists to answer. Measured on the
    /// 2026-09-08 run: of 45 boards fetched, <b>5</b> were among the 22 that would have unblocked a
    /// posting the candidate could apply to; corpus-wide recoveries went 29 to 175 while the
    /// applyable queue moved by 9. A bounded nightly budget spent top-down on the wrong ranking
    /// spends almost all of itself on employers nobody is applying to.
    ///
    /// Nothing failed and nothing could have: every count was correct, every board was eligible,
    /// and the pass reported exactly what it had done. **A ranking cannot be wrong in a way a unit
    /// test notices unless the test says what the ranking is for**, so this one is written in those
    /// terms - Contoso holds a third of Acme's blocked postings and comes first because its one
    /// posting is the one an application is waiting on.
    ///
    /// The sibling assertion is the load-bearing half: Acme is still returned. This is a sort key
    /// and not a filter, so a link is still recovered for a posting no profile has matched - after
    /// the ones somebody is waiting on rather than instead of them.
    /// </remarks>
    [Fact]
    public async Task A_board_blocking_an_application_somebody_wants_comes_first()
    {
        await AddPostingsAsync(Acme, 3);
        var wanted = await AddPostingsAsync(Contoso, 1);

        await WantAsync(wanted[0]);

        await AddBoardAsync(Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Contoso, AtsVendor.Ashby, "contoso", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        await using var db = CreateContext();

        var boards = await CreateRepository(db).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10);

        Assert.Equal([Contoso, Acme], boards.Select(b => b.CompanyId));
    }

    /// <summary>A dismissed posting is not one somebody is waiting on.</summary>
    /// <remarks>
    /// The one part of the liveness triple that is a person's own decision rather than the model's,
    /// and the one this repository has already got wrong once: <c>ListApplyableAsync</c> was the
    /// only match query that did not honour <c>DismissedAtUtc</c>, so a posting the candidate had
    /// refused came back to the agent on every run. Spending a request on the employer behind it
    /// is the cheaper version of the same fault, and it fails the same silent way - <b>a board
    /// ranked too high looks exactly like a board ranked correctly</b>.
    /// </remarks>
    [Fact]
    public async Task A_dismissed_posting_does_not_promote_its_employer()
    {
        await AddPostingsAsync(Acme, 3);
        var dismissed = await AddPostingsAsync(Contoso, 1);

        await WantAsync(dismissed[0]);
        await DismissAsync(dismissed[0]);

        await AddBoardAsync(Acme, AtsVendor.Greenhouse, "acme", AtsBoardDiscovery.Learned, Now.AddDays(-30));
        await AddBoardAsync(Contoso, AtsVendor.Ashby, "contoso", AtsBoardDiscovery.Learned, Now.AddDays(-30));

        await using var db = CreateContext();

        var boards = await CreateRepository(db).ListBoardsToFetchAsync(Now, TimeSpan.FromDays(1), limit: 10);

        Assert.Equal([Acme, Contoso], boards.Select(b => b.CompanyId));
    }

    private async Task DismissAsync(long postingId)
    {
        await using var db = CreateContext();

        await db.JobMatches
            .Where(m => m.PostingId == postingId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.DismissedAtUtc, Now));
    }


}
