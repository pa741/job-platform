using System.Collections.Concurrent;
using System.Net;
using System.Text;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using JobPlatform.Ingestion.Ats;
using JobPlatform.Ingestion.Functions;
using JobPlatform.Ingestion.Tests.Ats;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Ingestion.Tests;

/// <summary>
/// The apply-link recovery pass - <c>RecoverApplyLinksFunction</c> - against a real engine and
/// stubbed vendors.
/// </summary>
/// <remarks>
/// <b>Against SQLite rather than a stubbed repository, because the selection is the feature.</b>
/// What decides whether a board is fetched, whether a probe is offered and whether a posting is
/// blocked is arithmetic the database performs - correlated counts, a bounded page, an ordering -
/// and a test that handed the pass a list would assert the list rather than the rules, and would go
/// on passing on the day the two definitions drifted. So the fixture is a corpus in miniature and
/// every exclusion is a row.
///
/// <b>The vendors are stubbed at <c>IAtsBoardClient</c> and not at HTTP</b>, deliberately. What the
/// JSON of four differently shaped board endpoints parses into is <c>AtsBoardClientTests</c>'s
/// subject; what a whole pass asks for, how often, and what it does with the answer is this one's -
/// and the seam between them is exactly the interface, so a test that reached past it would be
/// asserting both at once and pinning neither. The real <c>AtsBoardReader</c> is used, because its
/// deduplication and its refusal to ask a vendor nobody has a reader for are part of what is being
/// asserted here.
///
/// <b>Four things are pinned and the first is the one the feature exists for.</b> A link is
/// recovered onto <c>EmployerAtsApplyUrl</c> and never onto <c>JobUrlDirect</c>, so the provenance
/// stays legible afterwards - a recovered link opens a form exactly like a published one, and the
/// column is the only thing that can ever tell them apart.
///
/// <b>Second, a guess is confirmed before it is trusted.</b> A probed board that answers and does
/// not name the employer produces no link at all, and the row it leaves behind is what stops the
/// request being spent again.
///
/// <b>Third, the bounds are real, and they bound requests to somebody else's API rather than rows
/// in this database.</b> A board is read once however many postings it unblocks, a vendor with no
/// reader costs nothing, an employer whose name yields no distinctive token costs nothing, and each
/// of the two ceilings stops the pass where it says it does.
///
/// <b>Fourth, the pass resumes from the database.</b> Learned boards leave the learn query, probed
/// employers leave the probe list and fetched boards leave the fetch list, so a second pass over an
/// unchanged corpus makes no requests at all.
/// </remarks>
public sealed class RecoverApplyLinksTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 2, 30, 0, TimeSpan.Zero);

    // --- employers -------------------------------------------------------------------

    /// <summary>Publishes a Greenhouse link on one posting, so its board is learned for free.</summary>
    private const int Cloudflare = 10;

    /// <summary>No link anywhere. Reachable only by a probe, and its board names itself.</summary>
    private const int Contoso = 11;

    /// <summary>Three letters. <c>AtsBoardCandidates.For</c> refuses to guess at all.</summary>
    private const int Dex = 12;

    /// <summary>A probe finds a board that will not name itself, so it cannot be confirmed.</summary>
    private const int Orbital = 13;

    /// <summary>Its board is learned and this build has no reader for the vendor.</summary>
    private const int Umbrella = 14;

    /// <summary>Its only direct link is a shortener: it proves the vendor and hides the employer.</summary>
    private const int Shortened = 15;

    // --- postings --------------------------------------------------------------------

    /// <summary>Cloudflare's own published link. The learn phase's whole input.</summary>
    private const long Published = 100;

    /// <summary>On the board, in the same city. The recovery this feature exists for.</summary>
    private const long OnTheBoard = 101;

    /// <summary>Not on the board: the vacancy closed. Stamped as asked, and no link.</summary>
    private const long Closed = 102;

    /// <summary>The board carries this title twice. An abstention rather than a coin toss.</summary>
    private const long Duplicated = 103;

    /// <summary>The board carrying the advert says it hosts the application itself.</summary>
    private const long BoardHosted = 104;

    /// <summary>Already carries a recovered link from an earlier pass.</summary>
    private const long AlreadyRecovered = 105;

    /// <summary>Contoso's one blocked posting, recovered through a probe that confirmed.</summary>
    private const long Probed = 110;

    /// <summary>Orbital's, which stays blocked because the board would not name itself.</summary>
    private const long Unconfirmable = 120;

    /// <summary>Umbrella's published Workable link. Learnable, and unreadable afterwards.</summary>
    private const long WorkablePublished = 130;

    /// <summary>Umbrella's blocked posting, behind a vendor nobody has written a reader for.</summary>
    private const long BehindWorkable = 131;

    /// <summary>The shortener. Greenhouse for certain, and no employer named.</summary>
    private const long Shortener = 140;

    private const long BehindShortener = 141;

    private const long AtDex = 150;

    /// <summary>Where a Greenhouse board publishes a vacancy.</summary>
    private const string VoidZeroUrl = "https://boards.greenhouse.io/cloudflare/jobs/9001";

    private const string DataOneUrl = "https://boards.greenhouse.io/cloudflare/jobs/9002";
    private const string DataTwoUrl = "https://boards.greenhouse.io/cloudflare/jobs/9003";
    private const string ContosoUrl = "https://jobs.smartrecruiters.com/ContosoHoldings/1234";

    /// <summary>Where Dex's own careers page lives, as the ingest folded it onto the employer.</summary>
    private const string DexCareersUrl = "https://careers.dex.example/";

    private const string OrbitalCareersUrl = "https://careers.orbitallabs.example/";
    private const string AcmeCareersUrl = "https://careers.acmeindustries.example/";
    private const string DexJobUrl = "https://boards.greenhouse.io/dex/jobs/7001";

    /// <summary>
    /// A Greenhouse embed under the employer's own domain, with the board named beside it.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing and a fixture with either alone would assert half the rule. The
    /// <c>gh_jid</c> link is the shape the live corpus is full of - <c>careers.withwaymo.com</c>
    /// carries one - and it names the vendor and no token, which <c>AtsBoardToken.FromUrl</c>
    /// answers null for on purpose. The board link is where the token is.
    /// </remarks>
    private const string DexCareersPage = """
        <!doctype html>
        <html lang="en">
          <body>
            <a href="https://careers.dex.example/jobs?gh_jid=6304904">Junior Engineer</a>
            <a href="https://boards.greenhouse.io/dex">See every opening</a>
          </body>
        </html>
        """;

    /// <summary>An agency's page, naming a board that belongs to one of their clients.</summary>
    private const string BorrowedBoardPage = """
        <!doctype html>
        <html lang="en">
          <body>
            <a href="https://jobs.lever.co/someoneelse/2f1c9d7e">A role with one of our clients</a>
          </body>
        </html>
        """;

    public RecoverApplyLinksTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);

        db.Database.EnsureCreated();

        // The display name is what a probe builds a token out of and what a confirmation compares
        // against, so each of these is chosen for what AtsBoardCandidates does with it rather than
        // for flavour: "Dex" is below the length floor, "Orbital Labs" strips its descriptor to a
        // word that is not distinctive on its own and is therefore probed unstripped, and "Contoso
        // Holdings" does the same and has a board that names itself.
        Employer(db, Cloudflare, "Cloudflare");
        Employer(db, Contoso, "Contoso Holdings");
        Employer(db, Dex, "Dex");
        Employer(db, Orbital, "Orbital Labs");
        Employer(db, Umbrella, "Umbrella");
        Employer(db, Shortened, "Acme Industries");

        Add(db, Published, Cloudflare, "Systems Engineer", direct: "https://boards.greenhouse.io/cloudflare/jobs/1");
        Add(db, OnTheBoard, Cloudflare, "VoidZero Engineer");
        Add(db, Closed, Cloudflare, "Ghost Engineer");
        Add(db, Duplicated, Cloudflare, "Data Engineer");
        Add(db, BoardHosted, Cloudflare, "Support Engineer", offsiteApply: false);
        Add(db, AlreadyRecovered, Cloudflare, "Network Engineer", recovered: "https://boards.greenhouse.io/cloudflare/jobs/8000");

        Add(db, Probed, Contoso, "Platform Engineer", city: "Manchester");
        Add(db, Unconfirmable, Orbital, "Rocket Engineer");

        Add(db, WorkablePublished, Umbrella, "Site Reliability Engineer", direct: "https://apply.workable.com/umbrella/j/AB12CD34");
        Add(db, BehindWorkable, Umbrella, "Security Engineer");

        Add(db, Shortener, Shortened, "Delivery Manager", direct: "https://grnh.se/abc123");
        Add(db, BehindShortener, Shortened, "Analyst", city: "Leeds");

        Add(db, AtDex, Dex, "Junior Engineer");

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // -----------------------------------------------------------------------
    // Learn: 39% of the gap, and no request at all
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_board_is_learned_from_a_link_the_employer_already_published_and_costs_no_request()
    {
        var vendors = Vendors();

        // Both request-making phases off, so what is left is the join over links already held -
        // which is where 122 of the 309 link-less postings are reachable, at no cost to anybody.
        var summary = await RunAsync(vendors, boards: 0, employers: 0);

        Assert.Equal(2, summary.Learned);
        Assert.Empty(vendors.Requests);

        Assert.Collection(
            await BoardsAsync(),
            row => Board(row, Cloudflare, AtsVendor.Greenhouse, "cloudflare"),
            // Learned even though nothing in this build can read a Workable board. The token was
            // published by the employer, so it is a fact worth keeping against the day an endpoint
            // for it reaches ATS-ENDPOINTS.md.
            row => Board(row, Umbrella, AtsVendor.Workable, "umbrella"));
    }

    [Fact]
    public async Task A_learned_board_is_confirmed_the_moment_it_is_learned()
    {
        await RunAsync(Vendors(), boards: 0, employers: 0);

        var learned = Assert.Single(await BoardsAsync(), row => row.CompanyId == Cloudflare);

        // The confirmation for a learned board is the link the employer published, so the usable
        // test stays one clause - ConfirmedAtUtc != null - rather than one clause per discovery
        // path. It is also the only route by which a Greenhouse board is ever usable at all, since
        // that endpoint publishes no company name for a probe to agree with.
        Assert.Equal(AtsBoardDiscovery.Learned, learned.Discovery);
        Assert.Equal(Now, learned.ConfirmedAtUtc);
    }

    [Fact]
    public async Task A_shortener_proves_the_vendor_and_names_no_employer_so_nothing_is_learned()
    {
        await RunAsync(Vendors(), boards: 0, employers: 0);

        // grnh.se is Greenhouse for certain and its target is knowable only by following it, which
        // this layer may not do. A guess here would attach one employer's postings to another
        // employer's board, and the resulting apply link would look entirely ordinary.
        Assert.DoesNotContain(await BoardsAsync(), row => row.CompanyId == Shortened);
    }

    [Fact]
    public async Task Learning_does_not_stop_at_the_lookback_window()
    {
        await using (var db = CreateContext())
        {
            // Last advertised a year ago. A board an employer published then is still the board
            // their current vacancies are on, and learning it costs a read rather than a request -
            // so the recency bound that keeps the fetch and the probe pointed at the live corpus
            // would only lose boards here for nothing.
            var stale = await db.JobPostings.AsTracking().SingleAsync(posting => posting.Id == Published);

            stale.LastSeenUtc = Now.AddDays(-365);

            await db.SaveChangesAsync();
        }

        var summary = await RunAsync(Vendors(), boards: 0, employers: 0);

        Assert.Equal(2, summary.Learned);
    }

    // -----------------------------------------------------------------------
    // Fetch: one request per employer, and the link it recovers says what it is
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_board_is_read_once_however_many_postings_it_unblocks()
    {
        var vendors = Vendors();

        var summary = await RunAsync(vendors, employers: 0);

        // Cloudflare's board answers its whole catalogue in one request - 333 jobs on the live one -
        // so three blocked postings are three matches against one read rather than three requests
        // for the same bytes.
        Assert.Equal([(AtsVendor.Greenhouse, "cloudflare")], vendors.Requests);
        Assert.Equal(1, summary.Fetched);
        Assert.Equal(3, summary.Checked);
    }

    [Fact]
    public async Task A_recovered_link_lands_beside_what_the_board_published_and_never_over_it()
    {
        await RunAsync(Vendors(), employers: 0);

        var recovered = await PostingAsync(OnTheBoard);

        Assert.Equal(VoidZeroUrl, recovered.EmployerAtsApplyUrl);
        Assert.Equal(AtsMatchConfidence.TitleAndPlace, recovered.EmployerAtsMatchConfidence);
        Assert.Equal(Now, recovered.EmployerAtsCheckedUtc);

        // The whole provenance argument, made physical. Both columns open a form and nothing a
        // browser sees separates them, so a recovered link written into JobUrlDirect would erase
        // the only thing that can ever say which of the two was a guess that went wrong.
        Assert.Null(recovered.JobUrlDirect);

        var published = await PostingAsync(Published);

        Assert.Equal("https://boards.greenhouse.io/cloudflare/jobs/1", published.JobUrlDirect);
        Assert.Null(published.EmployerAtsApplyUrl);
    }

    [Fact]
    public async Task A_posting_the_board_no_longer_carries_is_stamped_as_asked_and_gets_no_link()
    {
        await RunAsync(Vendors(), employers: 0);

        var closed = await PostingAsync(Closed);

        Assert.Null(closed.EmployerAtsApplyUrl);

        // The stamp is the third state, and without it a null link means either "the board was read
        // and had nothing" or "nobody has asked yet" - which want opposite work, and the second of
        // which would have this employer re-asked every pass for ever.
        Assert.Equal(Now, closed.EmployerAtsCheckedUtc);
    }

    [Fact]
    public async Task Two_listings_under_one_title_are_an_abstention_rather_than_a_choice()
    {
        var summary = await RunAsync(Vendors(), employers: 0);

        var ambiguous = await PostingAsync(Duplicated);

        // One employer advertising one title more than once was 74 of 285 cross-board candidates -
        // better than a quarter - and settling it on board order or on the shortest location string
        // would dress a coin toss as arithmetic. A posting declined is exactly where it was; a
        // posting got wrong is an application sent to a job nobody chose.
        Assert.Null(ambiguous.EmployerAtsApplyUrl);
        Assert.Equal(Now, ambiguous.EmployerAtsCheckedUtc);
        Assert.Equal(1, summary.Ambiguous);
        Assert.Equal(1, summary.Recovered);
    }

    [Fact]
    public async Task A_posting_whose_own_board_hosts_the_application_is_never_asked_about()
    {
        await RunAsync(Vendors(), employers: 0);

        var hosted = await PostingAsync(BoardHosted);

        // OffsiteApply false is that board talking about this listing rather than one that
        // resembles it, so there is nothing for an employer's ATS to recover and asking spends a
        // request to arrive back where we started.
        Assert.Null(hosted.EmployerAtsCheckedUtc);
        Assert.Null(hosted.EmployerAtsApplyUrl);
    }

    [Fact]
    public async Task A_board_on_a_vendor_with_no_reader_costs_no_request()
    {
        var vendors = Vendors();

        var summary = await RunAsync(vendors, employers: 0);

        // Every Workable link in this corpus is apply.workable.com/j/{code}, which names no board,
        // so Workable employers are reachable only through a probed host that is not in the
        // verified record. AtsBoardReader.CanRead answers that for nothing, where asking would
        // spend a slot to be told nobody is listening.
        Assert.DoesNotContain(vendors.Requests, request => request.Vendor == AtsVendor.Workable);
        Assert.Equal(1, summary.Unreadable);
        Assert.Null((await PostingAsync(BehindWorkable)).EmployerAtsCheckedUtc);
    }

    // -----------------------------------------------------------------------
    // Probe: the weak half, and the half that has to confirm
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_probed_board_that_names_the_employer_is_confirmed_and_matched_in_one_request()
    {
        var vendors = Vendors();

        var summary = await RunAsync(vendors, boards: 0);

        var board = Assert.Single(await BoardsAsync(), row => row.CompanyId == Contoso);

        Assert.Equal(AtsBoardDiscovery.Probed, board.Discovery);
        Assert.Equal(Now, board.ConfirmedAtUtc);
        Assert.Equal(1, summary.Confirmed);

        // The listings that confirmed the board are the listings the posting is matched against, so
        // the confirmation and the recovery cost one request between them rather than one each.
        Assert.Equal(ContosoUrl, (await PostingAsync(Probed)).EmployerAtsApplyUrl);
        Assert.Equal(Now, board.LastFetchedUtc);
        Assert.Single(vendors.Requests, request => request is { Vendor: AtsVendor.SmartRecruiters, Token: "contosoholdings" });
    }

    [Fact]
    public async Task A_probed_board_that_will_not_name_itself_produces_no_link()
    {
        var vendors = Vendors();

        var summary = await RunAsync(vendors, boards: 0);

        var board = Assert.Single(await BoardsAsync(), row => row.CompanyId == Orbital);

        // The board answered, so somebody owns "orbitallabs" - and a 200 read as a confirmation is
        // how an application reaches a company the candidate never applied to. Greenhouse publishes
        // no company name, so there is nothing here to agree with and the honest answer is no link.
        Assert.Contains(vendors.Requests, request => request is { Vendor: AtsVendor.Greenhouse, Token: "orbitallabs" });
        Assert.Null(board.ConfirmedAtUtc);
        Assert.Null((await PostingAsync(Unconfirmable)).EmployerAtsApplyUrl);

        // Answered and Confirmed are separate figures because the distance between them is the
        // argument for a second name-bearing endpoint, and never for relaxing the confirmation.
        Assert.True(summary.Answered > summary.Confirmed);
    }

    [Fact]
    public async Task An_unconfirmed_board_is_stored_so_the_request_is_never_spent_twice()
    {
        var first = Vendors();
        await RunAsync(first, boards: 0);

        var second = Vendors();
        var summary = await RunAsync(second, boards: 0);

        // A token that answered and did not confirm cost a request to an API that is doing us a
        // favour by answering at all. The row is how the next pass knows not to spend it again -
        // ListEmployersToProbeAsync offers only employers with no board row of any kind.
        Assert.DoesNotContain(second.Requests, request => request.Token == "orbitallabs");
        Assert.DoesNotContain(second.Requests, request => request.Token == "contosoholdings");
        Assert.Equal(0, summary.Answered);
    }

    [Fact]
    public async Task An_employer_no_token_resolved_for_is_offered_again_and_the_ceiling_is_the_bound()
    {
        await RunAsync(Vendors(), boards: 0);

        var vendors = Vendors();

        var summary = await RunAsync(vendors, boards: 0);

        // A probe that answered 404 writes nothing, because this table holds boards that exist - so
        // an employer no token resolved for comes back on every pass. That is deliberate rather
        // than an oversight, and it is bounded rather than unbounded: AtsBoardCandidates.MaxCandidates
        // sizes one employer's campaign exactly and this pass's two ceilings size the rest. A row
        // per token that did not answer would be a second, larger table whose only reader is a
        // request budget.
        Assert.All(
            vendors.Requests,
            request => Assert.StartsWith("acme", request.Token, StringComparison.Ordinal));

        Assert.Equal(8, summary.Probed);
    }

    [Fact]
    public async Task An_employer_whose_name_yields_no_distinctive_token_costs_no_request()
    {
        var vendors = Vendors();

        var summary = await RunAsync(vendors, boards: 0);

        // "Dex" is three characters. There are a thousand three-letter companies and one board per
        // token, so probing one is not a weak signal - it is no signal, and it is exactly how the
        // four measured false positives were produced.
        Assert.DoesNotContain(vendors.Requests, request => request.Token.Contains("dex", StringComparison.OrdinalIgnoreCase));
        Assert.True(summary.Unprobeable >= 1);
        Assert.DoesNotContain(await BoardsAsync(), row => row.CompanyId == Dex);
    }

    [Fact]
    public async Task A_probe_tries_its_best_token_against_every_vendor_before_its_second_best_anywhere()
    {
        var vendors = Vendors();

        await RunAsync(vendors, boards: 0);

        // Orbital's candidates are "orbitallabs" then "orbital-labs", likeliest first, and the
        // per-employer cap will usually cut a campaign short - so a truncated campaign has to have
        // made the right requests. Four of the better guess everywhere, then four of the worse.
        var tokens = vendors.Requests
            .Where(request => request.Token.StartsWith("orbital", StringComparison.Ordinal))
            .Select(request => request.Token)
            .ToArray();

        Assert.Equal(8, tokens.Length);
        Assert.All(tokens.Take(4), token => Assert.Equal("orbitallabs", token));
        Assert.All(tokens.Skip(4), token => Assert.Equal("orbital-labs", token));
    }

    [Fact]
    public async Task The_probe_stops_at_a_confirmation_and_not_at_the_first_answer()
    {
        var vendors = Vendors();

        await RunAsync(vendors, boards: 0);

        var campaign = vendors.Requests
            .Where(request => request.Token == "contosoholdings")
            .ToArray();

        // Contoso's token answers only on SmartRecruiters, the last of the four readable vendors.
        // A campaign that stopped at the first board to answer would never reach it - which is the
        // case an employer whose name collides with a stranger's board is in - and a campaign that
        // stops at the first confirmation asks no more once it has one.
        Assert.Equal(4, campaign.Length);
        Assert.Equal(AtsVendor.SmartRecruiters, campaign[^1].Vendor);
    }

    // -----------------------------------------------------------------------
    // The bounds, which are somebody else's rate limiter
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_probe_request_ceiling_bounds_what_the_pass_costs_somebody_else()
    {
        var vendors = Vendors();

        var summary = await RunAsync(vendors, boards: 0, employers: 10, probeRequests: 2);

        // The employer count says how much of the backlog is chipped at; this says what a vendor
        // would actually notice, and it is the one that has to hold.
        Assert.Equal(2, vendors.Requests.Count);
        Assert.Equal(2, summary.Probed);
    }

    [Fact]
    public async Task Setting_the_probe_to_zero_switches_it_off_without_a_deploy()
    {
        var vendors = Vendors();

        var summary = await RunAsync(vendors, employers: 0);

        // A supported state rather than a broken one: the learned path needs no guess at all and
        // covers 39% of the gap on its own.
        Assert.Equal(0, summary.Probed);
        Assert.DoesNotContain(vendors.Requests, request => request.Token == "contosoholdings");
    }

    [Fact]
    public async Task The_fetch_bound_stops_the_pass_where_it_says_it_does()
    {
        var vendors = Vendors();

        var summary = await RunAsync(vendors, boards: 1, employers: 0);

        Assert.Equal(1, summary.Fetched);
        Assert.Single(vendors.Requests);
    }

    [Fact]
    public async Task A_second_pass_neither_relearns_nor_reasks_what_the_first_one_established()
    {
        var first = Vendors();
        var opening = await RunAsync(first);

        var second = Vendors();
        var repeat = await RunAsync(second);

        // Resumable from the database and from nothing else, in all three phases and by
        // construction rather than by a flag: a learned board leaves the learn query, an employer
        // with any board row leaves the probe list, and a fetched board leaves the fetch list until
        // its refetch window has passed. Nothing is flagged and no attempt is counted anywhere.
        Assert.NotEmpty(first.Requests);
        Assert.DoesNotContain(second.Requests, request => request.Token == "cloudflare");
        Assert.DoesNotContain(second.Requests, request => request.Token == "contosoholdings");

        Assert.Equal(0, repeat.Learned);
        Assert.Equal(0, repeat.Fetched);
        Assert.Equal(0, repeat.Recovered);
        Assert.Equal(opening.Blocked, repeat.Blocked);
    }

    [Fact]
    public async Task A_board_is_asked_again_once_its_answer_has_aged_out()
    {
        await RunAsync(Vendors());

        var vendors = Vendors();

        // Eight days on, with the refetch window at seven. Measured from the last attempt rather
        // than the last answer, so a board that is down is not hammered.
        var summary = await RunAsync(vendors, now: Now.AddDays(8));

        Assert.Contains(vendors.Requests, request => request.Token == "cloudflare");
        Assert.Equal(1, summary.Fetched);
    }

    // -----------------------------------------------------------------------
    // The summary, which is the point of the pass
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_summary_says_what_the_pass_cost_beside_what_it_bought()
    {
        var summary = await RunAsync(Vendors());

        Assert.Equal(2, summary.Learned);

        // Contoso resolved on its fourth request; Orbital and Acme Industries spent their whole
        // per-employer share and resolved nothing; Dex cost nothing at all.
        Assert.Equal(20, summary.Probed);
        Assert.Equal(2, summary.Answered);
        Assert.Equal(1, summary.Confirmed);
        Assert.Equal(1, summary.Unprobeable);

        // Cloudflare through the fetch phase and Contoso through the probe that confirmed it. One
        // request each, and the second is counted in both figures because one request did both
        // jobs.
        Assert.Equal(2, summary.Fetched);
        Assert.Equal(4, summary.Listings);
        Assert.Equal(1, summary.Unreadable);
        Assert.Equal(0, summary.Unavailable);

        Assert.Equal(4, summary.Checked);
        Assert.Equal(2, summary.Recovered);
        Assert.Equal(1, summary.Ambiguous);
    }

    [Fact]
    public async Task The_blocked_count_is_what_says_whether_the_feature_is_working()
    {
        var before = await RunAsync(Vendors(), boards: 0, employers: 0, learn: false);

        // Eight postings carry no employer link from any source: three at Cloudflare and one each
        // at Contoso, Orbital, Umbrella, Acme Industries and Dex. The posting whose own board hosts
        // the application and the one that already carries a recovered link are not among them.
        Assert.Equal(8, before.Blocked);

        var after = await RunAsync(Vendors());

        // "Two boards read" is the same line on a pass that cleared the backlog and on one that
        // took two off a backlog of four hundred. This is the figure that separates them.
        Assert.Equal(6, after.Blocked);
    }

    [Fact]
    public async Task The_blocked_predicate_matches_the_repository_that_orders_the_pass()
    {
        // Two spellings of one rule, and the duplication is forced: the repository's is private and
        // composes into the reads that order this pass's work, while the function's has to select
        // the postings to match and count what is still stuck. The shortlist's channel filter is
        // what happens when a pair like this drifts - and it had already drifted once before
        // anybody noticed. On a corpus with no boards yet, every blocked posting's employer is in
        // the probe list, so the two totals have to agree exactly.
        var summary = await RunAsync(Vendors(), boards: 0, employers: 0, learn: false);

        await using var db = CreateContext();

        var employers = await new EmployerAtsBoardRepository(db)
            .ListEmployersToProbeAsync(100, Now.AddDays(-45));

        Assert.Equal(summary.Blocked, employers.Sum(employer => employer.BlockedPostings));
    }

    [Fact]
    public async Task A_vendor_that_does_not_answer_claims_nothing_about_the_employer()
    {
        // Every board unavailable: a timeout, a 5xx, a rate limit, a body that would not parse.
        var vendors = Vendors(unavailable: true);

        var summary = await RunAsync(vendors, employers: 0);

        Assert.Equal(0, summary.Fetched);
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(0, summary.Recovered);

        // Nothing is claimed about the employer and nothing is written about the posting. Reading
        // one bad afternoon at a vendor as "this board is wrong" would cost a real board for good.
        Assert.Null((await PostingAsync(OnTheBoard)).EmployerAtsCheckedUtc);

        var board = Assert.Single(await BoardsAsync(), row => row.CompanyId == Cloudflare);

        // The attempt is stamped even so, which is what bounds the pass to one request per employer
        // whether or not the board answered - and the confirmation it earned is untouched.
        Assert.Equal(Now, board.LastFetchedUtc);
        Assert.Equal(Now, board.ConfirmedAtUtc);
    }

    // -----------------------------------------------------------------------
    // Careers page: the third source, one request each, and the token is published
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_board_named_by_the_employers_own_page_is_confirmed_and_recovers_a_link_the_same_night()
    {
        var vendors = Vendors();

        vendors.Boards[(AtsVendor.Greenhouse, "dex")] = AtsBoardRead.For(
            [new AtsListing("Junior Engineer", "London", DexJobUrl)]);

        await CompanyUrlAsync(Dex, DexCareersUrl);

        var pages = Pages((DexCareersUrl, DexCareersPage));

        var summary = await RunAsync(vendors, pages: pages);

        // The employer no other path can reach. AtsBoardCandidates.For refuses to guess at a
        // three-letter name outright - "Dex" is one of the four measured false positives that
        // MinimumSingleWordLength exists to keep out - and LinkedIn published no apply link for
        // their postings, so there is nothing to learn from either. Their own page names the board.
        Assert.Empty(AtsBoardCandidates.For("Dex"));

        Assert.Single(pages.Requests);
        Assert.Equal(1, summary.CareersPages);
        Assert.Equal(1, summary.CareersBoards);
        Assert.Equal(1, summary.CareersConfirmed);

        var board = Assert.Single(await BoardsAsync(), row => row.CompanyId == Dex);

        // Recorded as what it is: weaker than a token off a link the employer published, stronger
        // than a slug guessed from their name, and confirmed here because the token their page
        // carries is their own name - which is not the circular check Core refuses for a probe,
        // since nothing derived this token from that name.
        Assert.Equal(AtsBoardDiscovery.CareersPage, board.Discovery);
        Assert.Equal(Now, board.ConfirmedAtUtc);

        // And the fetch phase, later in the same pass, does what a confirmation is for.
        var recovered = await PostingAsync(AtDex);

        Assert.Equal(DexJobUrl, recovered.EmployerAtsApplyUrl);
        Assert.Null(recovered.JobUrlDirect);
    }

    [Fact]
    public async Task A_careers_page_that_names_no_board_is_recorded_as_asked_rather_than_read_again()
    {
        await CompanyUrlAsync(Orbital, OrbitalCareersUrl);

        var first = Pages((OrbitalCareersUrl, RecordedCareersPages.GreenhouseEmbedWithoutTheBoard));

        // The probe is off, so the second pass below can only be explained by the stamp: with it on,
        // this employer would leave the careers list because a probe wrote them a board row, and the
        // test would pass while asserting the wrong mechanism.
        var summary = await RunAsync(Vendors(), employers: 0, pages: first);

        // The page proves the employer runs Greenhouse and names no token - every gh_jid on it sits
        // under their own domain, where AtsBoardToken.FromUrl answers null by design. So nothing is
        // learned and nothing is guessed.
        Assert.Single(first.Requests);
        Assert.Equal(1, summary.CareersPages);
        Assert.Equal(0, summary.CareersBoards);
        Assert.DoesNotContain(await BoardsAsync(), row => row.Discovery == AtsBoardDiscovery.CareersPage);

        // The stamp is the only record a page that named nothing leaves, and without it "we read
        // their site and there was nothing" is indistinguishable from "nobody has looked" - so the
        // pass would fetch that employer's server every night for ever. The same
        // two-nulls-in-one-column fault OffsiteApply was added to undo.
        Assert.Equal(Now, (await CompanyAsync(Orbital)).AtsCareersPageReadUtc);

        var second = Pages((OrbitalCareersUrl, RecordedCareersPages.GreenhouseEmbedWithoutTheBoard));

        await RunAsync(Vendors(), employers: 0, pages: second);

        Assert.Empty(second.Requests);
    }

    [Fact]
    public async Task An_employer_whose_page_named_nothing_is_still_worth_probing()
    {
        await CompanyUrlAsync(Orbital, OrbitalCareersUrl);

        var vendors = Vendors();

        await RunAsync(vendors, pages: Pages((OrbitalCareersUrl, RecordedCareersPages.NoBoardAtAll)));

        // The page answered a question about their site and not about their employer. A page with
        // no board row behind it leaves them in the probe list, which is the difference between
        // "asked their site" and "given up on them" - and it is why a page that names nothing
        // writes no board row rather than a row saying so.
        Assert.Contains(vendors.Requests, request => request.Token == "orbitallabs");
    }

    [Fact]
    public async Task A_company_url_that_is_junk_or_an_aggregators_costs_no_request_and_throws_nothing()
    {
        // Three of the four shapes scraped company URLs actually arrive in. The aggregator is the
        // one worth naming: company_url is very often a job board's own profile page for the
        // employer, and a board link found on one of those belongs to whoever that page is listing.
        await CompanyUrlAsync(Contoso, "https://www.linkedin.com/company/contoso");
        await CompanyUrlAsync(Orbital, "   ");
        await CompanyUrlAsync(Shortened, "not a url");

        var pages = Pages();

        var summary = await RunAsync(Vendors(), pages: pages);

        Assert.Empty(pages.Requests);
        Assert.Equal(0, summary.CareersPages);
        Assert.Equal(3, summary.CareersUnusable);

        // Stamped even though nothing was fetched, because the employer occupies a slot in a
        // bounded work list whether or not a socket is opened - leaving them unstamped would starve
        // the employers behind them on every pass.
        Assert.Equal(Now, (await CompanyAsync(Orbital)).AtsCareersPageReadUtc);
    }

    [Fact]
    public async Task A_page_naming_a_board_the_employers_name_does_not_account_for_recovers_nothing()
    {
        await CompanyUrlAsync(Shortened, AcmeCareersUrl);

        var vendors = Vendors();

        vendors.Boards[(AtsVendor.Lever, "someoneelse")] = AtsBoardRead.For(
            [new AtsListing("Analyst", "Leeds", "https://jobs.lever.co/someoneelse/2f1c9d7e")]);

        var summary = await RunAsync(
            vendors, pages: Pages((AcmeCareersUrl, BorrowedBoardPage)));

        // The agency case, and the largest remaining backlog in this corpus: the employers holding
        // the most link-less applyable postings are agencies advertising a client's vacancy under
        // their own name. Their page names a real board and it is not theirs, so the token is
        // stored - the request was spent and the row is what stops it being spent again - and it is
        // stored unusable.
        var board = Assert.Single(await BoardsAsync(), row => row.CompanyId == Shortened);

        Assert.Equal(AtsBoardDiscovery.CareersPage, board.Discovery);
        Assert.Null(board.ConfirmedAtUtc);
        Assert.Equal(1, summary.CareersBoards);
        Assert.Equal(0, summary.CareersConfirmed);

        // ConfirmedAtUtc is the whole test: an unconfirmed row cannot reach the fetch list and
        // AtsBoardToFetch cannot represent one, so the board is never read and the posting is never
        // stamped as asked about. A guess has to get past two independent refusals to reach a link.
        Assert.DoesNotContain(vendors.Requests, request => request.Token == "someoneelse");

        var blocked = await PostingAsync(BehindShortener);

        Assert.Null(blocked.EmployerAtsApplyUrl);
        Assert.Null(blocked.EmployerAtsCheckedUtc);
    }

    [Fact]
    public async Task The_pass_reads_no_more_careers_pages_than_it_is_allowed()
    {
        foreach (var employer in new[] { Contoso, Dex, Orbital, Shortened })
        {
            await CompanyUrlAsync(employer, $"https://careers.employer{employer}.example/");
        }

        var pages = Pages();

        var summary = await RunAsync(Vendors(), pages: pages, pageLimit: 2);

        // The bound is on requests to employers' own servers rather than on rows read here, which
        // is what makes it a courtesy rather than a tuning knob - and it is the tightest of the
        // three request-making bounds because this is the only traffic aimed at hosts that never
        // offered to answer.
        Assert.Equal(2, pages.Requests.Count);
        Assert.Equal(2, summary.CareersPages);

        // The two the ordering chose, and the other two wait for another pass rather than being
        // dropped: every one of them keeps a null stamp, so the next pass offers them again.
        Assert.Null((await CompanyAsync(Shortened)).AtsCareersPageReadUtc);
    }

    // -----------------------------------------------------------------------
    // The HTTP nudge
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_route_clamps_what_it_is_asked_for_to_what_the_gateway_allows()
    {
        var vendors = Vendors();

        var result = await Create(vendors).RunRecoverApplyLinksFunction(
            Request("""{"boards":500,"employers":500}"""),
            CancellationToken.None);

        var summary = Assert.IsType<RecoverApplyLinksFunction.RecoverySummary>(
            Assert.IsType<OkObjectResult>(result).Value);

        // A caller asking for five hundred boards over HTTP is asking for a 504, and a 504 carries
        // nothing back - here it would lose board reads that had already cost somebody a request.
        // Two employers were probed rather than five hundred, so the third in the ordering was
        // never reached at all.
        Assert.Equal(4, summary.Probed);
        Assert.DoesNotContain(vendors.Requests, request => request.Token.StartsWith("orbital", StringComparison.Ordinal));
        Assert.Equal(1, vendors.Requests.Count(request => request.Token == "cloudflare"));
    }

    [Fact]
    public async Task A_malformed_body_falls_back_to_the_route_defaults()
    {
        var vendors = Vendors();

        var result = await Create(vendors).RunRecoverApplyLinksFunction(
            Request("not json"),
            CancellationToken.None);

        var summary = Assert.IsType<RecoverApplyLinksFunction.RecoverySummary>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, summary.Learned);
    }

    // -----------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------

    private JobsDbContext CreateContext() => new(_options);

    private Task<RecoverApplyLinksFunction.RecoverySummary> RunAsync(
        StubVendors vendors,
        int boards = 40,
        int employers = 10,
        int probeRequests = 40,
        bool learn = true,
        DateTimeOffset? now = null,
        StubHandler? pages = null,
        int pageLimit = 25)
    {
        var function = Create(vendors, boards, employers, probeRequests, now ?? Now, pages, pageLimit);

        return learn
            ? function.RunNightlyAsync()
            : Summary(function, boards, employers, pageLimit);
    }

    /// <summary>
    /// The nightly pass with the learn phase off, which only the route can express.
    /// </summary>
    /// <remarks>
    /// It exists so the two request-making phases can be exercised on a corpus that has learned
    /// nothing - which is the only way to assert what the probe does about an employer whose board
    /// a link would otherwise have named for free.
    /// </remarks>
    private static async Task<RecoverApplyLinksFunction.RecoverySummary> Summary(
        RecoverApplyLinksFunction function, int boards, int employers, int pages)
    {
        var result = await function.RunRecoverApplyLinksFunction(
            Request($$"""{"boards":{{boards}},"employers":{{employers}},"pages":{{pages}},"learn":false}"""),
            CancellationToken.None);

        return Assert.IsType<RecoverApplyLinksFunction.RecoverySummary>(
            Assert.IsType<OkObjectResult>(result).Value);
    }

    private RecoverApplyLinksFunction Create(
        StubVendors vendors,
        int boards = 40,
        int employers = 10,
        int probeRequests = 40,
        DateTimeOffset? now = null,
        StubHandler? pages = null,
        int pageLimit = 25)
    {
        var db = CreateContext();

        return new RecoverApplyLinksFunction(
            db,
            new EmployerAtsBoardRepository(db),
            new AtsBoardReader(
                vendors.Clients(),
                Options.Create(new AtsBoardOptions()),
                NullLogger<AtsBoardReader>.Instance),
            // Stubbed at HTTP rather than at an interface, unlike the four vendors, and the seam is
            // different because the subject is. A board client's job is a documented endpoint and a
            // JSON shape, so the interface is where a pass stops caring; a careers page has no
            // interface to stand behind - what this pass is judged on is that an arbitrary
            // employer's server was asked once, politely, or not at all - so the stub sits where
            // the requests are countable.
            new CareersPageReader(
                new StubHttpClientFactory(pages ?? Pages()),
                Options.Create(new AtsBoardOptions()),
                NullLogger<CareersPageReader>.Instance),
            Options.Create(new ApplyLinkRecoveryOptions
            {
                BoardsPerPass = boards,
                EmployersProbedPerPass = employers,
                ProbeRequestsPerPass = probeRequests,
                CareersPagesPerPass = pageLimit,
            }),
            new FakeTime(now ?? Now),
            NullLogger<RecoverApplyLinksFunction>.Instance);
    }

    /// <summary>Careers pages, by the address the employer published for themselves.</summary>
    /// <remarks>
    /// An address nobody registered answers 404, which is <c>Unavailable</c> rather than "this
    /// employer has no board" - unlike a probed board token, where a 404 is the answer. The handler
    /// records every request, and on most of these tests the assertion that matters is that it
    /// recorded none.
    /// </remarks>
    private static StubHandler Pages(params (string Url, string Body)[] pages)
    {
        var bodies = pages.ToDictionary(page => page.Url, page => page.Body, StringComparer.Ordinal);

        return new StubHandler(url => bodies.TryGetValue(url, out var body)
            ? RecordedCareersPages.Ok(body)
            : RecordedCareersPages.Status(HttpStatusCode.NotFound));
    }

    /// <summary>Publishes an address for an employer, the way an ingest folds one onto them.</summary>
    /// <remarks>
    /// <c>Companies.Url</c> is <c>JobPostings.CompanyUrl</c> after the ingest has folded it onto the
    /// employer - <c>company.Url = posting.CompanyUrl ?? company.Url</c> - so writing it directly is
    /// writing what a scrape would have written, and the string is deliberately whatever the test
    /// says rather than something validated on the way in.
    /// </remarks>
    private async Task CompanyUrlAsync(int companyId, string? url)
    {
        await using var db = CreateContext();

        await db.Companies
            .Where(company => company.Id == companyId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.Url, url));
    }

    private async Task<CompanyEntity> CompanyAsync(int companyId)
    {
        await using var db = CreateContext();

        return await db.Companies.AsNoTracking().SingleAsync(company => company.Id == companyId);
    }

    private async Task<JobPostingEntity> PostingAsync(long postingId)
    {
        await using var db = CreateContext();

        return await db.JobPostings.AsNoTracking().SingleAsync(posting => posting.Id == postingId);
    }

    private static void Board(
        EmployerAtsBoardEntity row, int companyId, AtsVendor vendor, string token)
    {
        Assert.Equal(companyId, row.CompanyId);
        Assert.Equal(vendor, row.Vendor);
        Assert.Equal(token, row.Token);
    }

    private async Task<IReadOnlyList<EmployerAtsBoardEntity>> BoardsAsync()
    {
        await using var db = CreateContext();

        return await db.EmployerAtsBoards.AsNoTracking().OrderBy(board => board.Id).ToListAsync();
    }

    private static void Employer(JobsDbContext db, int id, string displayName)
        => db.Companies.Add(new CompanyEntity
        {
            Id = id,
            CompanyKey = displayName.ToLowerInvariant(),
            DisplayName = displayName,
            // The column no read in this pass may drag across. A board hangs off Companies.Id, and
            // a navigation property here would be one Include away from pulling a paragraph per
            // employer into a pass that wanted a name and a token.
            Description = new string('d', 4096),
            FirstSeenUtc = Now.AddDays(-30),
            LastSeenUtc = Now,
        });

    private static void Add(
        JobsDbContext db,
        long id,
        int companyId,
        string title,
        string? direct = null,
        string? recovered = null,
        bool? offsiteApply = null,
        string city = "London")
        => db.JobPostings.Add(new JobPostingEntity
        {
            Id = id,
            SourceKey = $"linkedin:{id}",
            Site = "linkedin",
            ExternalId = id.ToString(),
            ContentHash = new string((char)('a' + (id % 20)), 64),
            Title = title,
            Company = $"Company {companyId}",
            CompanyId = companyId,
            LocationCity = city,
            LocationRaw = $"{city}, UK",
            JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
            JobUrlDirect = direct,
            EmployerAtsApplyUrl = recovered,
            EmployerAtsMatchConfidence = recovered is null ? null : (AtsMatchConfidence?)AtsMatchConfidence.TitleOnly,
            OffsiteApply = offsiteApply,
            Description = $"{title}. Kubernetes, Terraform, C#.",
            FirstSeenUtc = Now.AddDays(-3),
            LastSeenUtc = Now,
        });

    private static HttpRequest Request(string body)
    {
        var context = new DefaultHttpContext();

        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/json";

        return context.Request;
    }

    /// <summary>A clock that does not move, so a stored timestamp is known by construction.</summary>
    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// The four board endpoints, without the vendors.
    /// </summary>
    /// <remarks>
    /// <b>Stubbed at <c>IAtsBoardClient</c> rather than at HTTP, which is the seam the design
    /// draws.</b> Everything above that line is meant not to care that Greenhouse answers
    /// <c>{"jobs":[…]}</c> and Lever a bare array, and asserting the parse here would pin both
    /// halves at once and neither properly - <c>AtsBoardClientTests</c> owns the JSON. What this
    /// records is the thing this pass is actually judged on: which board was asked for, on which
    /// vendor, in what order, and how many times.
    ///
    /// A token nobody has an answer for is <c>NotABoard</c>, because a 404 is the ordinary answer
    /// for a token that is not a board and is how a probe is meant to fail.
    /// </remarks>
    private sealed class StubVendors(bool unavailable)
    {
        private readonly ConcurrentQueue<(AtsVendor Vendor, string Token)> _requests = new();

        public Dictionary<(AtsVendor Vendor, string Token), AtsBoardRead> Boards { get; } = [];

        /// <summary>Every board asked for, in order, across every vendor.</summary>
        public IReadOnlyList<(AtsVendor Vendor, string Token)> Requests => [.. _requests];

        /// <summary>One reader per vendor, matching how the host registers them.</summary>
        /// <remarks>
        /// Workable is absent, exactly as it is in <c>AtsBoardRegistration</c>: every Workable link
        /// in the corpus names no board, so the endpoint that would answer a probed host is not in
        /// the verified record and no reader was written for it.
        /// </remarks>
        public IEnumerable<IAtsBoardClient> Clients() =>
        [
            new StubClient(AtsVendor.Greenhouse, this),
            new StubClient(AtsVendor.Lever, this),
            new StubClient(AtsVendor.Ashby, this),
            new StubClient(AtsVendor.SmartRecruiters, this),
        ];

        private AtsBoardRead Read(AtsVendor vendor, AtsBoard board)
        {
            _requests.Enqueue((vendor, board.Token));

            return unavailable
                ? AtsBoardRead.Unavailable
                : Boards.GetValueOrDefault((vendor, board.Token), AtsBoardRead.NotABoard);
        }

        private sealed class StubClient(AtsVendor vendor, StubVendors vendors) : IAtsBoardClient
        {
            public AtsVendor Vendor => vendor;

            public Task<AtsBoardRead> ReadAsync(AtsBoard board, CancellationToken cancellationToken = default)
                => Task.FromResult(vendors.Read(vendor, board));
        }
    }

    /// <summary>The vendors as this corpus's employers actually publish themselves.</summary>
    /// <remarks>
    /// Cloudflare's board carries the posting the advert named, one it no longer carries, and the
    /// same title twice - which is the shape that makes an abstention testable. Contoso's names
    /// itself, which is the only evidence <c>AtsBoardCandidates.Confirm</c> takes. Orbital's does
    /// not, which is what a Greenhouse probe looks like however right the token is.
    /// </remarks>
    private static StubVendors Vendors(bool unavailable = false)
    {
        var vendors = new StubVendors(unavailable);

        vendors.Boards[(AtsVendor.Greenhouse, "cloudflare")] = AtsBoardRead.For(
        [
            new AtsListing("VoidZero Engineer", "London", VoidZeroUrl),
            new AtsListing("Data Engineer", "London", DataOneUrl),
            new AtsListing("Data Engineer", "London", DataTwoUrl),
        ]);

        vendors.Boards[(AtsVendor.SmartRecruiters, "contosoholdings")] = AtsBoardRead.For(
            [new AtsListing("Platform Engineer", "Manchester", ContosoUrl)],
            "Contoso Holdings Ltd");

        vendors.Boards[(AtsVendor.Greenhouse, "orbitallabs")] = AtsBoardRead.For(
            [new AtsListing("Rocket Engineer", "London", "https://boards.greenhouse.io/orbitallabs/jobs/5")]);

        return vendors;
    }
}
