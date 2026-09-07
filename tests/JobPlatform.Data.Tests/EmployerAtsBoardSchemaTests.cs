using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The apply-link recovery schema, against a real relational engine.
/// </summary>
/// <remarks>
/// <b>These tests are about the database and not about a repository.</b> Everything writes through
/// <see cref="JobsDbContext"/> directly, because what is being pinned are constraints rather than
/// checks: a rule that holds only while callers behave is not one, and the callers here are an
/// unattended pass that learns boards on one path and probes them on another.
///
/// <b>Nothing below relies on either engine's NULL semantics.</b> All four key columns of
/// <c>IX_EmployerAtsBoards_Identity</c> are required, so every assertion here holds identically on
/// Azure SQL, where two NULLs in a unique index are equal and SQLite's are not. That is the same
/// technique and the same reasoning as <c>CvLibrarySchemaTests</c> and <c>ApplyLoopSchemaTests</c>,
/// and it is most of the argument for the employer being <c>Companies.Id</c> rather than a
/// nullable name.
///
/// <b>Three things are asserted, and the first is why the other two matter.</b> The identity is
/// the whole of what Core calls a board - vendor, token <i>and</i> region - and the index actually
/// rejects a duplicate of it. A recovered link lands beside the board's own published link rather
/// than on top of it. And the new columns round-trip, which is the failure otherwise invisible
/// until a migration is dispatched: a column mapped but not created, or created at a width that
/// truncates what Core will hand it.
///
/// One engine difference is knowingly left in place: SQLite compares <c>Token</c> under
/// <c>BINARY</c> and Azure SQL under a case-insensitive collation, so two spellings of one token
/// are two rows here and one row in production. Nothing below turns on that, because the cost is a
/// duplicate fetch rather than a wrong answer - see the remarks on
/// <see cref="EmployerAtsBoardEntity.Token"/>, which is also why nothing may "fix" it by folding
/// the token.
/// </remarks>
public sealed class EmployerAtsBoardSchemaTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

    private const int Acme = 10;
    private const int Contoso = 11;

    /// <summary>A company id no <c>Companies</c> row carries. See the foreign key test.</summary>
    private const int Absent = 999;

    private const long LinkedInPosting = 1;
    private const long BoardPosting = 2;

    public EmployerAtsBoardSchemaTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        foreach (var id in new[] { Acme, Contoso })
        {
            db.Companies.Add(new CompanyEntity
            {
                Id = id,
                CompanyKey = $"company {id}",
                DisplayName = $"Company {id}",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        // Two postings for one employer: one the board went quiet on, and one where the board
        // published the employer's own link. The pair is what the recovered-link tests need.
        db.JobPostings.Add(Posting(LinkedInPosting, "linkedin", jobUrlDirect: null));
        db.JobPostings.Add(Posting(BoardPosting, "indeed", jobUrlDirect: "https://boards.greenhouse.io/acme/jobs/4012345"));

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static JobPostingEntity Posting(long id, string site, string? jobUrlDirect) => new()
    {
        Id = id,
        SourceKey = $"{site}:{id}",
        Site = site,
        ExternalId = id.ToString(),
        ContentHash = new string((char)('a' + id), 64),
        Title = "Senior Platform Engineer",
        Company = "Acme",
        CompanyId = Acme,
        LocationCity = "London",
        JobUrl = $"https://www.{site}.com/jobs/view/{id}",
        JobUrlDirect = jobUrlDirect,
        FirstSeenUtc = Now,
        LastSeenUtc = Now,
    };

    private static EmployerAtsBoardEntity Board(
        int companyId = Acme,
        AtsVendor vendor = AtsVendor.Greenhouse,
        string token = "acme",
        AtsBoardRegion region = AtsBoardRegion.Default,
        AtsBoardDiscovery discovery = AtsBoardDiscovery.Learned,
        DateTimeOffset? confirmedAtUtc = null) => new()
    {
        CompanyId = companyId,
        Vendor = vendor,
        Token = token,
        Region = region,
        Discovery = discovery,
        DiscoveredAtUtc = Now,
        ConfirmedAtUtc = confirmedAtUtc,
    };

    // -----------------------------------------------------------------------
    // One board is one row
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_board_already_known_for_this_employer_is_not_written_twice()
    {
        await using var db = CreateContext();

        db.EmployerAtsBoards.Add(Board());
        await db.SaveChangesAsync();

        db.EmployerAtsBoards.Add(Board());

        // At the database rather than at a repository. The learned path and the probe path are
        // different code reaching the same employer in the same pass, and a read-then-insert is
        // two statements with a gap in the middle that both of them find empty.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_same_token_on_two_regions_is_two_boards()
    {
        await using var db = CreateContext();

        db.EmployerAtsBoards.Add(Board(vendor: AtsVendor.Lever, token: "acme"));
        db.EmployerAtsBoards.Add(Board(vendor: AtsVendor.Lever, token: "acme", region: AtsBoardRegion.Europe));

        // The region is part of the identity and not a label on it. jobs.lever.co/acme and
        // jobs.eu.lever.co/acme are served by different API hosts and the namespaces are
        // independent, so folding them to one row would let whichever was written first answer for
        // both - and the loser's employer would be asked of an endpoint that knows nothing about
        // them, which reads as "not on Lever" rather than as an error.
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.EmployerAtsBoards.CountAsync());
    }

    [Fact]
    public async Task The_same_token_at_two_employers_is_two_boards()
    {
        await using var db = CreateContext();

        db.EmployerAtsBoards.Add(Board(companyId: Acme, token: "orbital", discovery: AtsBoardDiscovery.Probed));
        db.EmployerAtsBoards.Add(Board(companyId: Contoso, token: "orbital"));

        // "Orbital" is one of the four measured collisions: a real board belonging to somebody,
        // probed from a name that resolved to a stranger. Keying the token globally would let that
        // wrong probe block the employer who can actually prove the token from a link they
        // published - the stronger evidence losing to the weaker one on insert order.
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.EmployerAtsBoards.CountAsync());
    }

    [Fact]
    public async Task The_same_token_on_two_vendors_is_two_boards()
    {
        await using var db = CreateContext();

        db.EmployerAtsBoards.Add(Board(vendor: AtsVendor.Greenhouse));
        db.EmployerAtsBoards.Add(Board(vendor: AtsVendor.Ashby));

        // An employer that moved between vendors, or was learned on one and probed on the other.
        // Both are fetchable and neither may evict the other.
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.EmployerAtsBoards.CountAsync());
    }

    [Fact]
    public async Task A_board_for_an_employer_the_company_table_does_not_hold_is_refused()
    {
        await using var db = CreateContext();

        db.EmployerAtsBoards.Add(Board(companyId: Absent));

        // The board hangs off Companies.Id, and that is the whole employer identity here: a row
        // pointing at no employer would be a board nothing can ever join to, holding a token
        // somebody probed and nobody can attribute.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // -----------------------------------------------------------------------
    // The stored row is the identity Core reads off a link
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_stored_board_rebuilds_the_identity_Core_read_off_the_link()
    {
        // A European Lever board, which is the shape that loses its meaning if any part of the
        // identity is dropped on the way into the database.
        var learned = AtsBoardToken.FromUrl("https://jobs.eu.lever.co/acme/8f2c1a44-0d3e-4c11-9a77-1b2c3d4e5f60");

        Assert.NotNull(learned);

        await using (var write = CreateContext())
        {
            write.EmployerAtsBoards.Add(new EmployerAtsBoardEntity
            {
                CompanyId = Acme,
                Vendor = learned.Vendor,
                Token = learned.Token,
                Region = learned.Region,
                Discovery = AtsBoardDiscovery.Learned,
                DiscoveredAtUtc = Now,
                ConfirmedAtUtc = Now,
            });

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var stored = await db.EmployerAtsBoards.SingleAsync();

        // Reconstructed rather than compared column by column, because the claim being made is
        // that the row holds everything the fetch needs and nothing has to be inferred from the
        // employer to make up the difference. A region guessed from a London address would be a
        // guess about a contract with the vendor that is invisible from here.
        Assert.Equal(learned, new AtsBoard(stored.Vendor, stored.Token, stored.Region));
        Assert.Equal(AtsBoardRegion.Europe, stored.Region);
    }

    [Fact]
    public async Task Every_token_Core_accepts_fits_the_column_it_is_stored_in()
    {
        var longest = new string('a', EmployerAtsBoardEntity.MaxTokenLength);

        // The pin: this file has to be told when Core's private bound moves, because the column
        // width cannot reference it. A token one character over is refused by Core and so never
        // reaches the column - which is what makes the width safe rather than lucky.
        Assert.True(AtsBoardToken.IsToken(longest));
        Assert.False(AtsBoardToken.IsToken(longest + "a"));

        await using (var write = CreateContext())
        {
            write.EmployerAtsBoards.Add(Board(token: longest));
            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        // A truncated token is worse than a refused one: it is still a slug, and it names the
        // board of whoever registered the shorter name.
        Assert.Equal(longest, (await db.EmployerAtsBoards.SingleAsync()).Token);
    }

    // -----------------------------------------------------------------------
    // A probed board that was never confirmed must never be used
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_probed_board_is_stored_unconfirmed_and_says_so()
    {
        await using (var write = CreateContext())
        {
            write.EmployerAtsBoards.Add(Board(
                companyId: Acme,
                token: "acmecorp",
                discovery: AtsBoardDiscovery.Probed,
                confirmedAtUtc: null));

            write.EmployerAtsBoards.Add(Board(
                companyId: Contoso,
                token: "contoso",
                discovery: AtsBoardDiscovery.Probed,
                confirmedAtUtc: Now));

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        // Stored rather than withheld: a token that answered and did not confirm is a request
        // already spent on somebody else's API, and remembering it is how the next pass avoids
        // spending it again. What must not happen is the row reading as usable, and the usable
        // test is one clause over a column no unset enum can fill in.
        var usable = await db.EmployerAtsBoards
            .Where(b => b.ConfirmedAtUtc != null)
            .ToListAsync();

        Assert.Equal(2, await db.EmployerAtsBoards.CountAsync());
        Assert.Equal(Contoso, Assert.Single(usable).CompanyId);
    }

    [Fact]
    public async Task A_board_that_has_never_been_fetched_is_not_a_board_that_was_never_confirmed()
    {
        await using (var write = CreateContext())
        {
            var board = Board(confirmedAtUtc: Now);
            board.LastFetchedUtc = null;

            write.EmployerAtsBoards.Add(board);
            await write.SaveChangesAsync();
        }

        await using (var fetch = CreateContext())
        {
            // AsTracking() out loud on a read that is then mutated. This host once ran a global
            // NoTracking under which four repositories silently saved nothing, and a pass that
            // stamps a fetch it did not really record would fetch the same board every run.
            var board = await fetch.EmployerAtsBoards.AsTracking().SingleAsync();

            board.LastFetchedUtc = Now.AddHours(1);

            await fetch.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var stored = await db.EmployerAtsBoards.SingleAsync();

        // Two timestamps, two questions, two different nulls. "Never fetched" is an employer still
        // to be asked about; "never confirmed" is a board that may not be used whatever it answers.
        Assert.Equal(Now, stored.ConfirmedAtUtc);
        Assert.Equal(Now.AddHours(1), stored.LastFetchedUtc);
        Assert.Equal(Now, stored.DiscoveredAtUtc);
        Assert.Equal(AtsBoardDiscovery.Learned, stored.Discovery);
    }

    // -----------------------------------------------------------------------
    // The recovered link, beside the published one
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_recovered_link_does_not_touch_the_column_the_board_published()
    {
        const string recovered = "https://boards.greenhouse.io/acme/jobs/4012999";

        await using (var write = CreateContext())
        {
            // AsTracking(), because these rows are read and then written.
            foreach (var posting in await write.JobPostings.AsTracking().ToListAsync())
            {
                posting.EmployerAtsApplyUrl = recovered;
                posting.EmployerAtsMatchConfidence = AtsMatchConfidence.TitleAndPlace;
                posting.EmployerAtsCheckedUtc = Now;
            }

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        var quiet = await db.JobPostings.SingleAsync(p => p.Id == LinkedInPosting);
        var published = await db.JobPostings.SingleAsync(p => p.Id == BoardPosting);

        // The posting whose board published nothing gains a link and keeps its empty column: the
        // absence of JobUrlDirect is still the fact that LinkedIn published no apply URL.
        Assert.Null(quiet.JobUrlDirect);
        Assert.Equal(recovered, quiet.EmployerAtsApplyUrl);

        // And the posting whose board did publish one keeps it, unchanged and distinguishable. If
        // a recovery ever writes over this column the provenance is gone for good - both open a
        // form, so nothing downstream could tell afterwards which of them was the guess.
        Assert.Equal("https://boards.greenhouse.io/acme/jobs/4012345", published.JobUrlDirect);
        Assert.Equal(recovered, published.EmployerAtsApplyUrl);
    }

    [Fact]
    public async Task A_posting_nobody_asked_about_is_not_a_posting_the_board_had_nothing_for()
    {
        await using (var write = CreateContext())
        {
            var asked = await write.JobPostings.AsTracking().SingleAsync(p => p.Id == LinkedInPosting);

            // Asked, and the board had nothing that matched - or matched two listings in two
            // cities and the rule declined to choose between them. Either way: no link.
            asked.EmployerAtsCheckedUtc = Now;

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        var asked2 = await db.JobPostings.SingleAsync(p => p.Id == LinkedInPosting);
        var never = await db.JobPostings.SingleAsync(p => p.Id == BoardPosting);

        // Three states, not two. Without the second column both rows are "no recovered link", the
        // pass cannot tell its work list from its settled rows, and every run re-asks the same
        // employer about the same postings - which is the fault OffsiteApply exists to undo,
        // repeated one column along.
        Assert.Null(asked2.EmployerAtsApplyUrl);
        Assert.Equal(Now, asked2.EmployerAtsCheckedUtc);

        Assert.Null(never.EmployerAtsApplyUrl);
        Assert.Null(never.EmployerAtsCheckedUtc);
        Assert.Null(never.EmployerAtsMatchConfidence);
    }

    [Fact]
    public async Task A_recovered_link_at_the_full_width_of_the_column_survives_it()
    {
        // 1000 characters, the width JobUrlDirect and Submissions.ApplyUrl both carry: a recovered
        // link is copied into a submission as the record of where an application actually went,
        // and a truncated URL still looks like a URL.
        var url = "https://boards.greenhouse.io/acme/jobs/4012999?utm_source=" + new string('x', 942);

        Assert.Equal(1000, url.Length);

        await using (var write = CreateContext())
        {
            var posting = await write.JobPostings.AsTracking().SingleAsync(p => p.Id == LinkedInPosting);

            posting.EmployerAtsApplyUrl = url;
            posting.EmployerAtsMatchConfidence = AtsMatchConfidence.TitleOnly;
            posting.EmployerAtsCheckedUtc = Now;

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var stored = await db.JobPostings.SingleAsync(p => p.Id == LinkedInPosting);

        Assert.Equal(url, stored.EmployerAtsApplyUrl);

        // The weaker of the two confidences, stored as itself. A caller that wants only matches
        // the city confirmed has to be able to ask, and re-deriving this would mean fetching
        // somebody else's board again to answer a question already answered.
        Assert.Equal(AtsMatchConfidence.TitleOnly, stored.EmployerAtsMatchConfidence);
    }
}
