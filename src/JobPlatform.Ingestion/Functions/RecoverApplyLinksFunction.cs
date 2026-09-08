using System.Linq.Expressions;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using JobPlatform.Ingestion.Ats;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// What one pass over employers' own applicant tracking boards is allowed to cost.
/// </summary>
/// <remarks>
/// <b>Configurable because every number here is somebody else's rate limiter rather than our
/// bill.</b> <c>ApplicationGenerationOptions</c> is settings because its number is money;
/// <c>AtsBoardOptions</c> is settings because its numbers are courtesies inside one request. These
/// are the courtesies of a whole pass - how many boards it asks, how many tokens it guesses at, how
/// soon it comes back - and the answer to "you are being noisy" has to be a configuration change
/// rather than a deploy. A constant would make the answer to "the recovery has stalled" a deploy
/// too.
///
/// <b>Every member defaults to something a clone can run unattended</b>, so a deployment that binds
/// nothing gets a bounded, polite pass rather than an unbounded one or a dead one. Two of them are
/// switches as well as bounds: <see cref="BoardsPerPass"/> at zero stops the fetch and
/// <see cref="EmployersProbedPerPass"/> at zero stops the probe, which is the control worth having
/// when the traffic is aimed at an API that is doing us a favour by answering at all.
/// </remarks>
public sealed class ApplyLinkRecoveryOptions
{
    /// <summary>Configuration section: <c>Applications:LinkRecovery</c>.</summary>
    public const string SectionName = "Applications:LinkRecovery";

    /// <summary>
    /// How many employers' boards one pass reads.
    /// </summary>
    /// <remarks>
    /// <b>Forty, and it is a count of employers rather than of postings, which is the whole shape
    /// of this feature.</b> A board answers its entire catalogue in one request - Cloudflare's
    /// Greenhouse returned 333 jobs on 2026-09-07 - so forty boards is forty requests and can
    /// unblock many hundreds of postings. Asking per posting would have been 333 requests for the
    /// same bytes, which is the arrangement <c>EmployerAtsBoards</c> exists to prevent.
    ///
    /// Sized against the measurement rather than guessed: 122 of the 309 link-less postings sit at
    /// an employer whose board is already knowable, and those cluster into far fewer than forty
    /// employers - so a nightly pass at this bound reaches the whole known set and then spends its
    /// budget re-reading the boards whose answers have aged out.
    /// </remarks>
    public int BoardsPerPass { get; set; } = 40;

    /// <summary>
    /// How many employers with no board at all one pass guesses a token for.
    /// </summary>
    /// <remarks>
    /// <b>Ten, an order of magnitude below the fetch bound, because probing is the weak half of
    /// this feature and it is the half that spends requests on nothing.</b> A slug built from a
    /// name resolved 21 of 120 employers; the other 99 were silent 404s. Each employer costs
    /// several requests rather than one - see <see cref="ProbeRequestsPerPass"/> - so ten
    /// employers is already the larger share of the pass's traffic.
    ///
    /// <b>Zero switches the probe off without a deploy</b>, and that is a supported state rather
    /// than a broken one: the learned path needs no guess at all and covers 39% of the gap on its
    /// own.
    /// </remarks>
    public int EmployersProbedPerPass { get; set; } = 10;

    /// <summary>
    /// The hard ceiling on how many probe requests one pass makes, across every employer.
    /// </summary>
    /// <remarks>
    /// <b>A second bound on the same activity, because the first one does not bound the traffic.</b>
    /// <see cref="EmployersProbedPerPass"/> says how much of the backlog is chipped at;
    /// <c>AtsBoardCandidates.MaxCandidates</c> is four and there are four readable vendors, so ten
    /// employers is up to a hundred and sixty requests if nothing stops it earlier. This is the
    /// number a vendor would actually notice, so it is the number that is stated.
    ///
    /// Forty is deliberately below the product of the other two: an employer resolved on its first
    /// candidate costs one request and an employer that resolves on none costs its whole per-employer
    /// share, so the pass reaches fewer employers on a bad night rather than making more requests.
    /// </remarks>
    public int ProbeRequestsPerPass { get; set; } = 40;

    /// <summary>
    /// How long a board's answer stands before the board is worth asking again.
    /// </summary>
    /// <remarks>
    /// Seven days, measured from the last <i>attempt</i> rather than the last answer, which is the
    /// repository's own rule and is what stops a board that is down being hammered. A vacancy list
    /// does not turn over faster than this in a way that matters here: a posting whose link was
    /// recovered keeps it, and a posting that matched nothing is one whose vacancy the board had
    /// already closed.
    /// </remarks>
    public int RefetchAfterDays { get; set; } = 7;

    /// <summary>
    /// How far back a posting counts as still worth recovering a link for.
    /// </summary>
    /// <remarks>
    /// Forty-five days, the same window <c>MatchSweepFunction.LookbackDays</c> scores over, because
    /// a posting outside it is not in anybody's queue and a board is not worth a request for
    /// vacancies nobody has seen advertised in months. <b>It bounds the fetch, the probe and the
    /// blocked count and deliberately does not bound the learn</b> - see
    /// <see cref="RecoverApplyLinksFunction"/>, where learning costs no request at all and a link an
    /// employer published a year ago still names the board their current vacancies are on.
    /// </remarks>
    public int LookbackDays { get; set; } = 45;

    /// <summary>
    /// How many already-held apply links the learning pass reads through.
    /// </summary>
    /// <remarks>
    /// <b>A bound on rows read rather than on requests made, because learning makes none.</b> It
    /// is a pass over links this system already holds, so the only cost is the read and one write
    /// per board learned; five thousand is comfortably above the whole corpus's direct links, which
    /// is what makes the phase complete rather than merely bounded.
    ///
    /// <b>What it costs if the corpus ever outgrows it is stated rather than discovered.</b> The
    /// read is ordered by employer, so a corpus with more direct links than this starves the
    /// employers with the highest ids until those ahead of them have been learned - and since a
    /// learned employer leaves the query for good, the head does drain. A pass reporting
    /// <c>Learned</c> at zero with the backlog unmoved is what that looks like, and raising this
    /// number is the fix.
    /// </remarks>
    public int LearnFromLinks { get; set; } = 5_000;
}

/// <summary>
/// Recovers the employer apply link LinkedIn stopped publishing, by asking the employer's own
/// applicant tracking system for it.
/// </summary>
/// <remarks>
/// <b>Why this exists, in one measurement.</b> Of 382 applyable postings on 2026-09-07, 73 carry an
/// employer apply link and all 309 that do not are LinkedIn, which stopped publishing apply URLs to
/// signed-out clients entirely. The authenticated route back is closed rather than merely unbuilt -
/// <c>mcp_handoff.md</c> 3.2 and 3.2a carry the decision and the legal record behind it - and the
/// third option is that <i>the employer will tell you</i>. Greenhouse, Ashby, Lever, Workable and
/// SmartRecruiters each publish a documented, unauthenticated board listing that exists to be read
/// by job seekers. <b>Nothing on this path sends a credential, a cookie or a session, on any
/// host</b>, and nothing added to it may.
///
/// <b>Three phases, cheapest first, and the ordering is the design rather than a convenience.</b>
/// <list type="number">
/// <item>
/// <b>Learn.</b> Every employer whose board token can be read off a link they have already
/// published. It makes no request at all - it is a join over data already held - and it is where
/// 122 of the 309, 39% of the gap, is. It also produces the only boards a Greenhouse, Ashby or
/// Lever employer can ever have, because those three endpoints do not name the employer and so
/// cannot confirm a guess.
/// </item>
/// <item>
/// <b>Probe.</b> Only for employers with no board of any kind and with postings actually blocked.
/// The candidates are <c>AtsBoardCandidates.For</c>'s, and <b>a probed token is confirmed before
/// it is trusted</b>: "Dex", "Kernel", "Fin" and "Orbital" are all real boards belonging to
/// somebody, not necessarily to the employer on the advert. Confirmation is Core's
/// <c>AtsBoardCandidates.Confirm</c> and nothing here may relax it.
/// </item>
/// <item>
/// <b>Fetch.</b> Each known board once, matched against that employer's link-less postings with
/// <c>AtsListingMatcher</c>. One request per employer and never one per posting.
/// </item>
/// </list>
///
/// <b>Every link it writes is <c>ApplyUrlSource.MatchedOnEmployerAts</c> and never
/// <c>Posting</c>.</b> That is not decoration: a recovered link opens a form exactly like a
/// published one, so the column it lands in is the only thing that can afterwards say which of them
/// was an inference - and this one has two ways to be wrong that nothing downstream can see. The
/// token may belong to another company, and the title match may have landed on the vacancy next to
/// the right one. The separation is enforced by the repository, which has no way to assign to
/// <c>JobUrlDirect</c> at all; this pass simply never asks it to.
///
/// <b>Bounded hard, and every bound is somebody else's rate limiter rather than our bill.</b> There
/// is a cap on boards fetched, a cap on employers probed, a hard ceiling on probe requests, a
/// per-employer probe cap, a refetch window and a wall-clock budget. A runaway loop here is not a
/// slow night; it is rude.
///
/// <b>Resumable from the database and from nothing else</b>, in all three phases and by
/// construction rather than by a flag: a learned board leaves the learn query, an employer with any
/// board row leaves the probe list, and a fetched board leaves the fetch list until its refetch
/// window has passed. A pass cut short by its budget resumes where it stopped; a pass that crashes
/// loses only the work in flight.
///
/// <b>Degraded rather than broken wherever a reader is absent.</b> Workable is the fifth vendor
/// <c>AtsBoard</c> admits and the one with no reader, so its boards are counted and stepped over
/// instead of costing a request that nobody is listening for - which is exactly what
/// <c>AtsBoardReader.CanRead</c> exists to answer before anything is spent.
/// </remarks>
public sealed class RecoverApplyLinksFunction(
    JobsDbContext db,
    EmployerAtsBoardRepository boards,
    AtsBoardReader reader,
    IOptions<ApplyLinkRecoveryOptions> options,
    TimeProvider time,
    ILogger<RecoverApplyLinksFunction> logger)
{
    /// <summary>
    /// A posting with no employer apply link from any source, on a board that does not host the
    /// application itself.
    /// </summary>
    /// <remarks>
    /// <b>This is the second spelling of <c>EmployerAtsBoardRepository</c>'s own
    /// <c>WithoutEmployerLink</c>, and the duplication is forced rather than chosen.</b> That one
    /// is private, it composes into the two reads that order this pass's work, and this one has to
    /// select the actual postings to match and to count what is still stuck - so the two must
    /// agree, and there is nothing to share them through. The shortlist's channel filter is what
    /// this costs when it goes wrong: two spellings, a test to hold them together, and one occasion
    /// on which they had already drifted. <b>A change to either is a change to both</b>, and
    /// <c>The_blocked_predicate_matches_the_repository_that_orders_the_pass</c> is what turns
    /// forgetting into a red build rather than into a board fetched for postings it can never
    /// unblock.
    ///
    /// <c>OffsiteApply != false</c> and not <c>== true</c>, because the third state is the point:
    /// false means that board says it hosts the application, and it is talking about <i>this</i>
    /// listing rather than one that resembles it, so there is nothing for an employer's ATS to
    /// recover. Null means nothing was established, which is the ordinary state of a posting whose
    /// detail page nobody read.
    /// </remarks>
    private static readonly Expression<Func<JobPostingEntity, bool>> WithoutEmployerLink =
        posting => posting.JobUrlDirect == null
            && posting.EmployerAtsApplyUrl == null
            && posting.OffsiteApply != false;

    /// <summary>
    /// The vendors a probe may guess a token for, derived from Core's own list.
    /// </summary>
    /// <remarks>
    /// <b>Enumerated from <c>AtsBoardToken.ServesPublicBoard</c> rather than written out</b>, so a
    /// sixth vendor joining that list is reachable here without this file changing - which is the
    /// arrangement <c>ParkReasonPolicy</c> settled on and the one the shortlist's channel filter
    /// did not. Written out, the entry missing from the copy would be the one that mattered.
    ///
    /// The order is the enum's own and carries no claim: <c>AtsVendor</c>'s numbering is identity
    /// rather than strength, and a probe campaign tries its best token against every vendor before
    /// its second-best token against any of them, so the vendor order decides only which of two
    /// equally-likely guesses is made first.
    /// </remarks>
    private static readonly AtsVendor[] ProbeVendors =
        [.. Enum.GetValues<AtsVendor>().Where(AtsBoardToken.ServesPublicBoard)];

    /// <summary>
    /// How many probe requests one employer may cost before the pass moves on.
    /// </summary>
    /// <remarks>
    /// <b>Eight: the two likeliest tokens against all four readable vendors.</b> The worst case
    /// without it is <c>AtsBoardCandidates.MaxCandidates</c> times the vendors, which is sixteen
    /// requests to establish that one employer is not reachable - and the measurement had 99 of 120
    /// employers ending exactly there. Spending the pass's whole ceiling on five hopeless employers
    /// is worse than reaching ten.
    ///
    /// <b>The campaign is token-major rather than vendor-major</b>, so this bound spends itself on
    /// the guesses most likely to be right: <c>AtsBoardCandidates.For</c> returns its candidates
    /// likeliest first, and testing the best guess everywhere before testing a worse guess anywhere
    /// is what makes a truncated campaign still the right eight requests.
    /// </remarks>
    private const int MaxProbesPerEmployer = 8;

    /// <summary>
    /// How many boards are read for each one the fetch budget allows.
    /// </summary>
    /// <remarks>
    /// <b>An over-read, for the reason <c>GenerateApplicationsFunction.CandidateWindow</c> is one.</b>
    /// A board on a vendor with no reader in this build cannot be excluded by the query - the
    /// repository ranks boards by how many postings they block and knows nothing about which
    /// clients happen to be registered - so it is stepped over after materialisation, which is
    /// exactly where a filter silently shrinks a bound. Reading three times the budget means a head
    /// full of Workable boards does not leave the night's fetch cap unspent.
    ///
    /// <b>It is a window and not a guarantee.</b> An employer with a Workable board and many
    /// blocked postings sits at the head of that ordering on every pass, because nothing here marks
    /// it as tried - and marking it would be stamping <c>LastFetchedUtc</c> on a board that was
    /// never asked, which would then report as silent in a list of boards that have gone quiet. A
    /// truthful column and a wasted window slot is the better of the two, and the count is reported
    /// so the slot is visible.
    /// </remarks>
    private const int FetchWindow = 3;

    /// <summary>
    /// How many blocked postings one pass will match against boards.
    /// </summary>
    /// <remarks>
    /// A ceiling on a read rather than on requests: the board's bytes are already paid for, so
    /// matching is arithmetic, and the only cost is pulling four bounded columns out of a database
    /// billed on wall-clock time. Comfortably above the 309 the measurement found and above any
    /// plausible growth of it; a pass that reached it would match the employers with the lowest ids
    /// and leave the rest to the next one, which is the same resumption every other bound here has.
    /// </remarks>
    private const int MaxPostingsMatched = 5_000;

    /// <summary>Wall-clock budget for the nightly pass.</summary>
    /// <remarks>
    /// Ten minutes, comfortably inside Flex Consumption's 30-minute default and sized against the
    /// traffic rather than the arithmetic: at <c>AtsBoardOptions.RequestTimeout</c> of ten seconds
    /// and four requests in flight, forty boards and forty probes cannot fill it unless most of the
    /// vendors are timing out - which is the case the budget is for, since a pass that spent half
    /// an hour waiting on a vendor having a bad afternoon would still be waiting when the sweep
    /// wanted the instance.
    /// </remarks>
    private static readonly TimeSpan TimerBudget = TimeSpan.FromMinutes(10);

    /// <summary>Wall-clock budget for the HTTP route, with margin under the gateway's ~230s.</summary>
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(200);

    /// <summary>How many boards one HTTP invocation may read.</summary>
    /// <remarks>
    /// <b>Five, because the route is a nudge rather than a batch</b> - the same shape as
    /// <c>run-embed-corpus</c> and <c>run-generate-applications</c>. Calling it repeatedly is how a
    /// first pass gets finished by hand before 02:30 has ever run, and each call answers with what
    /// it did rather than committing to work the gateway will cut off at around 230 seconds. A 504
    /// carries nothing back, and here what it would lose is a board read that had already cost
    /// somebody else a request.
    /// </remarks>
    private const int MaxBoardsPerRequest = 5;

    /// <summary>How many employers one HTTP invocation may probe.</summary>
    /// <remarks>
    /// Two, which at <see cref="MaxProbesPerEmployer"/> is up to sixteen requests - already the
    /// larger half of what a 200-second budget should aim at somebody else's API by hand.
    /// </remarks>
    private const int MaxEmployersProbedPerRequest = 2;

    /// <summary>How many employer names to list in the log line before truncating.</summary>
    private const int NamesLogged = 10;

    [Function(nameof(RecoverApplyLinksFunction))]
    public Task RunAsync(
        // 02:30 UTC: after the NAS scrape has uploaded and the ingest has drained, and before
        // everything that reads the apply queue - the embedding pass at 03:00, the sweep at 03:30
        // and the generation pass at 04:30. A link recovered here changes what a run can apply to,
        // and it costs no model budget, so it goes first.
        //
        // The ordering is not enforced by anything and does not need to be: a link recovered after
        // a queue has been read is simply read by the next one.
        [TimerTrigger("0 30 2 * * *")] TimerInfo timer,
        CancellationToken ct)
        => RunNightlyAsync(ct);

    /// <summary>
    /// The nightly pass, separated from the trigger that fires it.
    /// </summary>
    /// <remarks>
    /// <b>The trigger returns nothing because a Functions return value is an output binding</b> and
    /// this pass has no binding to write to - it writes rows. The summary is still the only honest
    /// account of what a pass cost and what it bought, so the work is a method the trigger calls
    /// and what the log line reports is the same object a test can read.
    /// </remarks>
    public Task<RecoverySummary> RunNightlyAsync(CancellationToken ct = default)
    {
        var settings = options.Value;

        return RecoverAsync(
            settings.BoardsPerPass,
            settings.EmployersProbedPerPass,
            learn: true,
            TimerBudget,
            ct);
    }

    /// <summary>
    /// The same pass, on demand.
    /// </summary>
    /// <remarks>
    /// Exists for the case the timer cannot serve, exactly as <c>run-match-sweep</c> and
    /// <c>run-generate-applications</c> do: a corpus in which nothing has ever been recovered, and
    /// somebody establishing whether the pass works before waiting a night to find out. An admin
    /// endpoint rather than a user-facing one, because it is the path that makes requests to
    /// somebody else's API and a route a client can call is a route a client can call repeatedly.
    ///
    /// Follows <c>ReprocessBlobFunction</c>: ASP.NET Core integration types because the host is
    /// built with <c>ConfigureFunctionsWebApplication</c>, and no <c>admin/</c> route prefix
    /// because the host reserves it and claiming it fails as a 404 rather than as an error.
    /// </remarks>
    [Function(nameof(RunRecoverApplyLinksFunction))]
    public async Task<IActionResult> RunRecoverApplyLinksFunction(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "run-recover-apply-links")]
        HttpRequest request,
        CancellationToken ct)
    {
        var body = await RequestBody.ReadAsync<RecoverRequest>(request, ct);

        var summary = await RecoverAsync(
            // Clamped rather than defaulted from the options, because the bound here is the
            // gateway's rather than the night's: a caller asking for forty boards over HTTP is
            // asking for a 504, and answering with five boards read is more use than answering
            // with nothing after having already spent forty requests.
            Math.Clamp(body?.Boards ?? MaxBoardsPerRequest, 0, MaxBoardsPerRequest),
            Math.Clamp(body?.Employers ?? MaxEmployersProbedPerRequest, 0, MaxEmployersProbedPerRequest),
            body?.Learn ?? true,
            RequestBudget,
            ct);

        return new OkObjectResult(summary);
    }

    /// <param name="Boards">Boards to read, bounded by <see cref="MaxBoardsPerRequest"/> whatever is asked for.</param>
    /// <param name="Employers">Employers to probe, bounded by <see cref="MaxEmployersProbedPerRequest"/>.</param>
    /// <param name="Learn">
    /// Whether to run the free phase. Defaults to true and is exposed only so the two request-making
    /// phases can be exercised on their own - there is no reason to switch off the half that costs
    /// nobody anything.
    /// </param>
    public sealed record RecoverRequest(int? Boards = null, int? Employers = null, bool? Learn = null);

    private async Task<RecoverySummary> RecoverAsync(
        int boardLimit, int employerLimit, bool learn, TimeSpan budget, CancellationToken ct)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var since = now.AddDays(-Math.Max(settings.LookbackDays, 0));
        var started = time.GetTimestamp();
        var tally = new RecoveryTally();

        try
        {
            if (learn)
            {
                await LearnAsync(settings.LearnFromLinks, now, tally, ct);
            }

            if (time.GetElapsedTime(started) < budget)
            {
                await ProbeAsync(employerLimit, settings.ProbeRequestsPerPass, since, now, tally, started, budget, ct);
            }

            if (time.GetElapsedTime(started) < budget)
            {
                await FetchAsync(boardLimit, settings, since, now, tally, started, budget, ct);
            }
        }
        catch (DbUpdateException exception)
        {
            // The pass stops here rather than carrying on, and that is about the change tracker
            // rather than about tidiness: every phase writes through the one scoped DbContext, and
            // a failed SaveChanges leaves the entity that failed sitting in it - so the next write
            // of any kind would re-attempt the insert that has just been refused. Reported as an
            // error naming the phase's own counts, because a partial pass with a reason is worth
            // more than a pass that reports success having stopped writing half way through.
            logger.LogError(
                exception,
                "Apply-link recovery: a write was refused, so the pass stopped. {Learned} board(s) "
                + "learned, {Confirmed} probed board(s) confirmed and {Recovered} link(s) recovered "
                + "before it did. The likeliest cause is an employer that is not in Companies.",
                tally.Learned, tally.Confirmed, tally.Recovered);
        }

        tally.Blocked = await BlockedCountAsync(since, ct);

        // What was asked of somebody else's API, what came back, and what is still stuck. The last
        // number is the one that says whether this feature is working: "forty boards read" is the
        // same log line on a pass that cleared the backlog and on one that took forty off a
        // backlog of four hundred, and only the third figure separates them.
        logger.LogInformation(
            "Apply-link recovery complete: {Learned} board(s) learned from links already held; "
            + "{Probed} probe request(s) made for {Answered} answering board(s), {Confirmed} "
            + "confirmed; {Fetched} board(s) read carrying {Listings} vacancy(ies), {Unreadable} "
            + "skipped for want of a reader and {Unavailable} unanswered; {Checked} posting(s) "
            + "asked about, {Recovered} link(s) recovered and {Ambiguous} abstained on. "
            + "{Blocked} posting(s) still have no employer link.",
            tally.Learned, tally.Probed, tally.Answered, tally.Confirmed, tally.Fetched,
            tally.Listings, tally.Unreadable, tally.Unavailable, tally.Checked, tally.Recovered,
            tally.Ambiguous, tally.Blocked);

        return tally.ToSummary();
    }

    // -----------------------------------------------------------------------
    // Learn: 39% of the gap, and it makes no request at all
    // -----------------------------------------------------------------------

    /// <summary>
    /// Learns every board whose token can be read off a link the employer already published.
    /// </summary>
    /// <remarks>
    /// <b>The free phase, and the one that runs first for that reason.</b> It is a pass over links
    /// this system already holds: <c>AtsBoardToken.FromUrl</c> is pure and network-free, so nothing
    /// here is fetched, no redirect is followed and no shortener is resolved. 122 of the 309
    /// link-less postings measured on 2026-09-07 are at an employer reachable exactly this way.
    ///
    /// <b>It is not bounded by recency, unlike everything else in this pass.</b> A link an employer
    /// published a year ago names the board their <i>current</i> vacancies are on, and learning it
    /// costs a read and one write rather than a request - so the lookback that keeps the fetch and
    /// the probe pointed at the live corpus would only lose boards here for nothing.
    ///
    /// <b>The query excludes employers that already have a learned board, and deliberately not
    /// employers that have any board at all.</b> A probed row is a guess this pass may have stored
    /// earlier, unconfirmed and unusable, and <c>LearnAsync</c> upgrades exactly that row - the
    /// employer's own link is the evidence the guess never had. Excluding them would leave a
    /// correct token permanently unusable because a weaker version of it was written first.
    ///
    /// <b>Resumable by construction:</b> a learned board leaves this query, so a pass cut short
    /// resumes rather than restarting, and a pass that has caught up reads a short answer instead
    /// of doing the work again.
    ///
    /// <b>One board per employer per pass, and never more.</b> An employer that genuinely holds two
    /// boards - ordinary after an acquisition, and the reason the token is in the identity key -
    /// learns the second one on a later pass, once nothing else about them has changed. Learning
    /// every readable link at once would be one write per link for an employer whose postings all
    /// carry the same URL.
    /// </remarks>
    private async Task LearnAsync(int limit, DateTimeOffset now, RecoveryTally tally, CancellationToken ct)
    {
        if (limit <= 0)
        {
            return;
        }

        var links = await db.JobPostings
            .AsNoTracking()
            .Where(posting => posting.CompanyId != null
                && posting.JobUrlDirect != null
                && !db.EmployerAtsBoards.Any(board =>
                    board.CompanyId == posting.CompanyId
                    && board.Discovery == AtsBoardDiscovery.Learned))
            // By employer so the grouping below is a walk rather than a dictionary, and by
            // freshness within an employer so the token comes off their most recent link - a board
            // an employer has moved off is still published on the postings that predate the move.
            .OrderBy(posting => posting.CompanyId)
            .ThenByDescending(posting => posting.LastSeenUtc)
            .Select(posting => new LinkRow(posting.CompanyId!.Value, posting.JobUrlDirect!))
            .Take(limit)
            .ToListAsync(ct);

        tally.LinksRead = links.Count;

        var learned = new HashSet<int>();

        foreach (var link in links)
        {
            // One board per employer per pass. The rows are ordered by employer, so this also
            // skips the rest of an employer's links the moment one of them has answered.
            if (learned.Contains(link.CompanyId) || AtsBoardToken.FromUrl(link.JobUrlDirect) is not { } board)
            {
                continue;
            }

            learned.Add(link.CompanyId);

            if (await boards.LearnAsync(link.CompanyId, board, now, ct))
            {
                tally.Learned++;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Probe: the weak half, and the half that has to confirm
    // -----------------------------------------------------------------------

    /// <summary>
    /// Guesses a board token from the employer's name, and confirms it before it is stored as
    /// usable.
    /// </summary>
    /// <remarks>
    /// <b>A 200 confirms nothing, and that is the whole of this method's difficulty.</b> A vendor's
    /// board endpoint answers for whoever owns the token: "Dex", "Kernel", "Fin" and "Orbital" are
    /// all real boards belonging to a company, not necessarily the one on the advert, and a 200
    /// read as a confirmation is how an application reaches a company the candidate never applied
    /// to. So the answer is put to <c>AtsBoardCandidates.Confirm</c>, and only a board that
    /// confirms is stamped - <c>EmployerAtsBoardRepository.ListBoardsToFetchAsync</c> cannot return
    /// an unconfirmed row and <c>AtsBoardToFetch</c> cannot represent one, so a guess has to get
    /// past two independent refusals to reach a link.
    ///
    /// <b>Nothing here raises or lowers Core's bar.</b> <c>NameAgrees</c> is what
    /// <c>ConfirmAsync</c> accepts and what this passes it; the posting title is handed over as
    /// well, because it can only raise the answer to <c>NameAndPostingAgree</c> and never rescue a
    /// name that disagrees. Requiring the stronger value was considered and refused for Core's own
    /// reason: a board listing no job that matches the advert is usually a board that closed the
    /// vacancy, not a board belonging to somebody else.
    ///
    /// <b>An answering board is stored even when it does not confirm, and that is the bound rather
    /// than an oversight.</b> The request has been spent; the row is how the next pass knows not to
    /// spend it again, since <c>ListEmployersToProbeAsync</c> offers only employers with no board
    /// row of any kind. It is also upgradeable - the day that employer publishes a direct link, the
    /// learn phase turns the guess into a fact.
    ///
    /// <b>Only one of the four readable vendors can confirm today, and the gap is reported rather
    /// than closed here.</b> Greenhouse, Ashby and Lever publish no company name on their listing
    /// endpoints, so a token guessed for one of them comes back <c>Unconfirmed</c> however right it
    /// is - which is why <c>Answered</c> and <c>Confirmed</c> are separate figures in the summary.
    /// The distance between them is the argument for a second verified, name-bearing endpoint in
    /// <c>ATS-ENDPOINTS.md</c>. It is emphatically not an argument for confirming on the token, on
    /// the title, or on the fact that something answered: the token was built from the name, so
    /// comparing it back would confirm every probe including every wrong one while looking like a
    /// check.
    ///
    /// <b>The campaign stops at the first confirmation and never at the first answer</b>, so an
    /// employer whose name collides with a stranger's Greenhouse board can still be found on the
    /// vendor they are actually on.
    /// </remarks>
    private async Task ProbeAsync(
        int employerLimit,
        int requestLimit,
        DateTimeOffset since,
        DateTimeOffset now,
        RecoveryTally tally,
        long started,
        TimeSpan budget,
        CancellationToken ct)
    {
        var vendors = ProbeVendors.Where(reader.CanRead).ToArray();

        if (employerLimit <= 0 || requestLimit <= 0 || vendors.Length == 0)
        {
            return;
        }

        var employers = await boards.ListEmployersToProbeAsync(employerLimit, since, ct);

        if (employers.Count == 0)
        {
            return;
        }

        // Read once for the whole phase rather than per employer, so an employer that turns out to
        // have no candidate token at all costs no query. It is the same read the fetch phase makes
        // and it serves two purposes here: a title for the confirmation to corroborate against, and
        // the postings to match the moment a board does confirm.
        var blocked = await BlockedPostingsAsync([.. employers.Select(employer => employer.CompanyId)], since, ct);
        var unprobeable = new List<string>();

        foreach (var employer in employers)
        {
            if (tally.Probed >= requestLimit || time.GetElapsedTime(started) >= budget)
            {
                break;
            }

            var candidates = AtsBoardCandidates.For(employer.Company);

            if (candidates.Count == 0)
            {
                // Not a failure and the commonest answer worth having: a name too short or too
                // generic to distinguish from a common noun, which is exactly where a guess is
                // least worth making. Monzo, Stripe and Revolut are all refused here deliberately.
                tally.Unprobeable++;
                unprobeable.Add(employer.Company);
                continue;
            }

            var postings = blocked.GetValueOrDefault(employer.CompanyId, []);

            await ProbeEmployerAsync(employer, candidates, vendors, postings, requestLimit, now, tally, ct);
        }

        if (unprobeable.Count > 0)
        {
            // Information rather than a warning: this is the rule working. It names the employers
            // because "some names could not be probed" is not something anybody can act on, where
            // a list of them is the evidence for finding a second signal - which is the only way
            // the recall here goes up. Lowering AtsBoardCandidates.MinimumSingleWordLength is not.
            logger.LogInformation(
                "Apply-link recovery: {Count} employer(s) have no token distinctive enough to "
                + "probe, so no request was made for them: {Employers}.",
                unprobeable.Count,
                string.Join(", ", unprobeable.Take(NamesLogged))
                    + (unprobeable.Count > NamesLogged ? ", ..." : string.Empty));
        }
    }

    /// <summary>One employer's probe campaign: best token everywhere, then the next.</summary>
    /// <remarks>
    /// <b>Token-major rather than vendor-major</b>, because <c>AtsBoardCandidates.For</c> orders its
    /// candidates likeliest first and <see cref="MaxProbesPerEmployer"/> will usually cut the
    /// campaign short. Testing the best guess against every vendor before testing a worse guess
    /// against any of them means a truncated campaign has still made the right requests.
    ///
    /// <b>A board that confirms is fetched and matched here rather than left for the next pass.</b>
    /// Its listings are already in hand - the probe read them to find the name - so matching them
    /// now costs nothing, where deferring would ask the same vendor for the same bytes again
    /// tomorrow. That is the same rule the fetch phase runs under, applied to a request that has
    /// already happened.
    /// </remarks>
    private async Task ProbeEmployerAsync(
        AtsEmployerToProbe employer,
        IReadOnlyList<string> candidates,
        IReadOnlyList<AtsVendor> vendors,
        IReadOnlyList<BlockedPosting> postings,
        int requestLimit,
        DateTimeOffset now,
        RecoveryTally tally,
        CancellationToken ct)
    {
        var spent = 0;

        foreach (var token in candidates)
        {
            // Core builds these out of letters, digits and hyphens, so this cannot fail today. It
            // is asked anyway because AtsBoard's constructor throws on a token it will not accept,
            // and one exception here would lose the rest of the pass to a rule change in another
            // file.
            if (!AtsBoardToken.IsToken(token))
            {
                continue;
            }

            foreach (var vendor in vendors)
            {
                if (spent >= MaxProbesPerEmployer || tally.Probed >= requestLimit)
                {
                    return;
                }

                var board = new AtsBoard(vendor, token);

                spent++;
                tally.Probed++;

                var read = await reader.ReadAsync(board, ct);

                if (read.Outcome == AtsBoardReadOutcome.Unavailable)
                {
                    // Nothing is claimed about the employer. Reading a timeout or a 5xx as "the
                    // token is wrong" would take a real board out of the probe path for good over
                    // one bad afternoon at a vendor.
                    tally.Unavailable++;
                    continue;
                }

                if (read.Outcome == AtsBoardReadOutcome.NotABoard)
                {
                    // The ordinary answer, and how a guess is meant to fail: 99 of 120 employers
                    // landed here in the measurement. Counted through Probed and never logged as a
                    // fault, or the pass becomes a log nobody reads.
                    continue;
                }

                // An answer carrying no listings proves nothing, and one vendor proves it for
                // every token there is. Verified live on 2026-09-08: SmartRecruiters answers
                // 200 with {"totalFound":0,"content":[]} for `jackandjill`, for `hunterbond`
                // and for `definitely-not-a-real-company-xyz99` alike - it has no 404 for a
                // company that does not exist, so AtsBoardReadOutcome.NotABoard is unreachable
                // there and every guess "answers". All twelve probed rows in the corpus were
                // SmartRecruiters for exactly this reason.
                //
                // It is still recorded, because the request was spent and the row is how the
                // next pass knows not to spend it again - see the campaign's remarks. What it
                // must not do is count as evidence: confirmation reads the company name off a
                // listing, so an empty board cannot confirm however long it is asked, and
                // counting it in Answered puts the distance between Answered and Confirmed -
                // the figure that argues for a name-bearing endpoint - permanently wrong.
                if (read.Listings.Count == 0)
                {
                    await boards.RecordProbeAsync(employer.CompanyId, board, now, ct);
                    continue;
                }

                tally.Answered++;

                await boards.RecordProbeAsync(employer.CompanyId, board, now, ct);

                var proved = AtsBoardCandidates.Confirm(
                    employer.Company,
                    read.BoardName,
                    postings.Count > 0 ? postings[0].Title : null,
                    read.Listings.Select(listing => listing.Title));

                if (!await boards.ConfirmAsync(employer.CompanyId, board, proved, now, ct))
                {
                    logger.LogDebug(
                        "Apply-link recovery: {Vendor} board {Token} answered for {Employer} and "
                        + "did not confirm ({Confidence}). The row is kept so the request is not "
                        + "spent again, and a link the employer publishes later will upgrade it.",
                        vendor, token, employer.Company, proved);

                    continue;
                }

                tally.Confirmed++;

                logger.LogInformation(
                    "Apply-link recovery: probed {Vendor} board {Token} confirmed as {Employer} "
                    + "({Confidence}), unblocking up to {Blocked} posting(s).",
                    vendor, token, employer.Company, proved, employer.BlockedPostings);

                // The bytes are already here, so the confirmation and the match are one request
                // rather than two. RecordFetchAsync is what stops the fetch phase asking the same
                // board again tonight.
                //
                // It counts as a fetch as well as a probe, and that is not double-counting: one
                // request was made, Probed says what it cost and Fetched says what it bought, and
                // the two answer different questions. Reporting it only as a probe would make the
                // listings it returned invisible.
                tally.Fetched++;
                tally.Listings += read.Listings.Count;

                await boards.RecordFetchAsync(employer.CompanyId, board, now, ct);
                await MatchAsync(employer.CompanyId, postings, read, now, tally, ct);

                return;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Fetch: one request per employer, matched against every posting they block
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads each known board once and matches its listings against that employer's link-less
    /// postings.
    /// </summary>
    /// <remarks>
    /// <b>One request per employer and never one per posting.</b> Cloudflare's Greenhouse board
    /// answered 333 vacancies in a single request on 2026-09-07; asking it per posting would be 333
    /// requests for the same bytes from an API that exists to serve that vendor's paying customers
    /// and their applicants. The whole shape of <c>EmployerAtsBoards</c> - a row per employer rather
    /// than per posting - exists to make that true, and <c>AtsBoardReader</c> deduplicates whatever
    /// this hands it on top.
    ///
    /// <b>The fetch is recorded before the request and not after it.</b> That is what bounds the
    /// pass to one attempt per board whether or not the board answers: a version that stamped on
    /// success would re-ask a board that times out on every pass for ever. It also makes
    /// <c>ListSilentBoardsAsync</c> correct by construction rather than dependent on which of two
    /// writes lands first, since everything a live board leaves behind is stamped after this.
    ///
    /// <b>Unconfirmed boards cannot reach here</b>, and not because this method checks: the
    /// repository's <c>WHERE</c> clause excludes them and <c>AtsBoardToFetch</c> has no way to
    /// represent one. A caller cannot forget to check what it cannot be handed.
    ///
    /// <b>A board on a vendor with no reader is stepped over rather than asked</b> -
    /// <c>AtsBoardReader.CanRead</c> answers that for nothing, where asking would spend a slot to
    /// be told nobody is listening. Workable is the case: every Workable link in this corpus is the
    /// <c>apply.workable.com/j/{code}</c> form, which names no board, so those employers are
    /// reachable only through a probed host that is not in the verified record.
    /// </remarks>
    private async Task FetchAsync(
        int limit,
        ApplyLinkRecoveryOptions settings,
        DateTimeOffset since,
        DateTimeOffset now,
        RecoveryTally tally,
        long started,
        TimeSpan budget,
        CancellationToken ct)
    {
        if (limit <= 0)
        {
            return;
        }

        var window = await boards.ListBoardsToFetchAsync(
            now,
            TimeSpan.FromDays(Math.Max(settings.RefetchAfterDays, 0)),
            limit * FetchWindow,
            since,
            ct);

        var readable = new List<AtsBoardToFetch>(limit);

        foreach (var board in window)
        {
            if (readable.Count >= limit)
            {
                break;
            }

            if (reader.CanRead(board.Board.Vendor))
            {
                readable.Add(board);
            }
            else
            {
                tally.Unreadable++;
            }
        }

        if (readable.Count == 0)
        {
            return;
        }

        // Stamped before a single request goes out. Two employers holding the same board is
        // ordinary - one company advertising under two names in Companies - and each gets its own
        // stamp, while the reader below still makes one request for the pair.
        foreach (var board in readable)
        {
            await boards.RecordFetchAsync(board.CompanyId, board.Board, now, ct);
        }

        var blocked = await BlockedPostingsAsync([.. readable.Select(board => board.CompanyId)], since, ct);
        var reads = await reader.ReadAsync(readable.Select(board => board.Board), ct);

        foreach (var board in readable)
        {
            if (time.GetElapsedTime(started) >= budget)
            {
                // The requests are already made and the answers already in hand, so stopping here
                // loses matching rather than traffic. The postings keep their unstamped
                // EmployerAtsCheckedUtc, so the next pass asks about them again - which is the one
                // case where a board's bytes are paid for twice, and the alternative is an
                // invocation the host kills mid-write.
                break;
            }

            if (!reads.TryGetValue(board.Board, out var read))
            {
                continue;
            }

            if (read.Outcome != AtsBoardReadOutcome.Read)
            {
                tally.Unavailable++;
                continue;
            }

            tally.Fetched++;
            tally.Listings += read.Listings.Count;

            await MatchAsync(
                board.CompanyId,
                blocked.GetValueOrDefault(board.CompanyId, []),
                read,
                now,
                tally,
                ct);
        }
    }

    /// <summary>
    /// Matches one board read against one employer's blocked postings and records the outcome for
    /// every one of them.
    /// </summary>
    /// <remarks>
    /// <b>Every posting is stamped as asked and only a match writes a link.</b> The stamp is what
    /// stops the next pass re-asking the same employer about the same postings: without it a null
    /// <c>EmployerAtsApplyUrl</c> means either "the board was read and had nothing" or "nobody has
    /// asked yet", and those want opposite work. It is also the only trace an abstention leaves.
    ///
    /// <b>An abstention is counted rather than resolved.</b> <c>AtsListingMatcher</c> declines to
    /// choose when an employer advertises one title more than once and the city cannot separate
    /// them - 74 of 285 cross-board candidates were exactly that shape - and picking the first, the
    /// nearest or the shortest would dress a coin toss as arithmetic. A posting it declines is
    /// exactly where it was; a posting it gets wrong is an application sent to a job nobody chose.
    ///
    /// The whole board is handed to the matcher rather than a filtered subset, because two listings
    /// excluded before they arrive are two listings it cannot abstain between - and the abstention
    /// is the thing standing between a recovered link and the vacancy next to the right one.
    /// </remarks>
    private async Task MatchAsync(
        int companyId,
        IReadOnlyList<BlockedPosting> postings,
        AtsBoardRead read,
        DateTimeOffset now,
        RecoveryTally tally,
        CancellationToken ct)
    {
        if (postings.Count == 0)
        {
            return;
        }

        var matches = new List<AtsPostingMatch>(postings.Count);

        foreach (var posting in postings)
        {
            var match = AtsListingMatcher.Match(posting.Title, posting.LocationCity, read.Listings);

            switch (match.Outcome)
            {
                case AtsListingMatchOutcome.Matched:
                    tally.Recovered++;
                    break;

                case AtsListingMatchOutcome.Ambiguous:
                    tally.Ambiguous++;
                    break;
            }

            matches.Add(new AtsPostingMatch(posting.PostingId, match));
        }

        // One call, which is one transaction: half a stamped employer is worse than an unstamped
        // one, because the next pass would spend the request and act on part of the answer.
        tally.Checked += await boards.RecordMatchesAsync(matches, now, ct);

        logger.LogDebug(
            "Apply-link recovery: employer {CompanyId}'s board carried {Listings} vacancy(ies) "
            + "against {Postings} blocked posting(s).",
            companyId, read.Listings.Count, postings.Count);
    }

    // -----------------------------------------------------------------------
    // The postings, and the number that says whether this is working
    // -----------------------------------------------------------------------

    /// <summary>
    /// The link-less postings of a set of employers, in one read.
    /// </summary>
    /// <remarks>
    /// <b>One query for the page rather than one per employer</b>, because a round trip each
    /// against a database billed on wall-clock time is the cost every read in this codebase is
    /// written to avoid - and this pass would make one per board on a night that read forty.
    ///
    /// <b>Four bounded columns and never the description.</b> The matcher wants a title and a city;
    /// the advert body is unbounded <c>nvarchar(max)</c> and pulling it for several hundred
    /// postings would be megabytes to answer a question about two short strings.
    ///
    /// The recency bound is the caller's: a board is not worth a request for vacancies nobody has
    /// seen advertised in months, and matching against a posting that is no longer listed would
    /// write a link to a vacancy that has closed.
    /// </remarks>
    private async Task<Dictionary<int, IReadOnlyList<BlockedPosting>>> BlockedPostingsAsync(
        int[] companyIds, DateTimeOffset since, CancellationToken ct)
    {
        if (companyIds.Length == 0)
        {
            return [];
        }

        var rows = await db.JobPostings
            .AsNoTracking()
            .Where(WithoutEmployerLink)
            .Where(posting => posting.CompanyId != null
                && companyIds.Contains(posting.CompanyId.Value)
                && posting.LastSeenUtc >= since)
            // Ordered so the ceiling below cuts the same place on two identical calls. A bounded
            // page whose contents changed between them would make the pass's own logs unreadable.
            .OrderBy(posting => posting.CompanyId)
            .ThenBy(posting => posting.Id)
            .Select(posting => new BlockedPosting(
                posting.CompanyId!.Value, posting.Id, posting.Title, posting.LocationCity))
            .Take(MaxPostingsMatched)
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<BlockedPosting>)[.. group]);
    }

    /// <summary>
    /// How many postings still have no employer apply link from any source.
    /// </summary>
    /// <remarks>
    /// <b>The number this pass exists to move, and the only one in the summary that is about the
    /// corpus rather than about the run.</b> "Forty boards read" is the same line on a pass that
    /// cleared the backlog and on one that took forty off a backlog of four hundred; this is what
    /// separates them, and it is what says whether the feature is working at all.
    ///
    /// <b>Counted rather than estimated, and bounded only by the lookback.</b> It is one aggregate
    /// over short columns of a table with a few thousand live rows, which is affordable once a
    /// night - and a ceiling would make the figure stop moving exactly when the backlog was still
    /// above it, which is the interesting case.
    ///
    /// It counts the whole corpus and not one candidate's queue, deliberately: a board token is a
    /// fact about an employer and the same fact for every candidate, so a per-profile figure here
    /// would report on a different thing than the pass acts on.
    /// </remarks>
    private Task<int> BlockedCountAsync(DateTimeOffset since, CancellationToken ct)
        => db.JobPostings
            .AsNoTracking()
            .Where(WithoutEmployerLink)
            .CountAsync(posting => posting.LastSeenUtc >= since, ct);

    /// <summary>An employer's id beside one link they published, as SQL can answer it.</summary>
    private sealed record LinkRow(int CompanyId, string JobUrlDirect);

    /// <summary>One posting a board might unblock: everything the matcher reads and nothing else.</summary>
    private sealed record BlockedPosting(
        int CompanyId, long PostingId, string Title, string? LocationCity);

    /// <summary>
    /// The run's counts while it is still running.
    /// </summary>
    /// <remarks>
    /// Mutable and private, unlike <see cref="RecoverySummary"/>, and that is what lets a pass
    /// stopped by a refused write still report what it had done - the three phases accumulate into
    /// one object rather than folding three returned records, so there is no arm on which a partial
    /// pass reports zero.
    /// </remarks>
    private sealed class RecoveryTally
    {
        public int Learned { get; set; }

        public int LinksRead { get; set; }

        public int Probed { get; set; }

        public int Answered { get; set; }

        public int Confirmed { get; set; }

        public int Unprobeable { get; set; }

        public int Fetched { get; set; }

        public int Unreadable { get; set; }

        public int Unavailable { get; set; }

        public int Listings { get; set; }

        public int Checked { get; set; }

        public int Recovered { get; set; }

        public int Ambiguous { get; set; }

        public int Blocked { get; set; }

        public RecoverySummary ToSummary() => new(
            Learned, LinksRead, Probed, Answered, Confirmed, Unprobeable, Fetched, Unreadable,
            Unavailable, Listings, Checked, Recovered, Ambiguous, Blocked);
    }

    /// <param name="Learned">
    /// Boards attached to an employer from a link they had already published. Free: no request was
    /// made for any of them.
    /// </param>
    /// <param name="LinksRead">
    /// Already-held apply links the learn phase read through. At
    /// <see cref="ApplyLinkRecoveryOptions.LearnFromLinks"/> the phase is at its ceiling and the
    /// employers behind it wait for another pass.
    /// </param>
    /// <param name="Probed">
    /// Board tokens tried against a vendor. <b>This is what the pass cost somebody else</b>, and it
    /// is reported before anything it bought.
    /// </param>
    /// <param name="Answered">Probes that found a board. Somebody owns the token; not yet this employer.</param>
    /// <param name="Confirmed">
    /// Probed boards that proved themselves this employer's, and so may produce a link. <b>The
    /// distance from <paramref name="Answered"/> is the cost of three of the four verified
    /// endpoints not naming the company</b> - it is a gap to close with a fourth endpoint in
    /// <c>ATS-ENDPOINTS.md</c>, never by relaxing the confirmation.
    /// </param>
    /// <param name="Unprobeable">
    /// Employers whose name yields no token distinctive enough to guess at. No request was made for
    /// any of them, and that is the rule working rather than a shortfall.
    /// </param>
    /// <param name="Fetched">
    /// Boards whose listings this pass read and matched against. A probed board that confirms
    /// appears here <i>and</i> in <paramref name="Probed"/>: one request did both jobs, and the two
    /// figures answer different questions - what the pass cost, and what it read.
    /// </param>
    /// <param name="Unreadable">
    /// Known boards stepped over because this build has no reader for the vendor. It costs no
    /// request; it does occupy a slot in the fetch window on every pass, which is why it is counted.
    /// </param>
    /// <param name="Unavailable">
    /// Board reads that established nothing - a timeout, a 5xx, a rate limit, a body that would not
    /// parse. Nothing is claimed about those employers, and a recovery count that fell has a reason.
    /// </param>
    /// <param name="Listings">Vacancies read across every board answered. What the requests bought.</param>
    /// <param name="Checked">
    /// Postings a board was actually asked about and stamped. Above
    /// <paramref name="Recovered"/> by exactly the postings whose vacancy the board no longer
    /// carries.
    /// </param>
    /// <param name="Recovered">
    /// Apply links written. Every one is <c>ApplyUrlSource.MatchedOnEmployerAts</c> - an inference
    /// with a name on it, never folded in beside a link the advert's own board published.
    /// </param>
    /// <param name="Ambiguous">
    /// Postings whose employer advertises that title more than once, where the rule declined to
    /// choose. <b>An abstention rather than a failure</b>, and reported separately because it wants
    /// opposite work from a no-match: this is evidence about the posting, where a no-match is
    /// evidence about the board.
    /// </param>
    /// <param name="Blocked">
    /// Postings still carrying no employer apply link from any source, across the corpus, after
    /// this pass. <b>The number that says whether the feature is working</b>, and the only one here
    /// that is about the corpus rather than the run.
    /// </param>
    /// <remarks>
    /// <b>What the pass cost is reported beside what it bought, always.</b> A summary carrying only
    /// what was recovered cannot show a pass that spent forty requests and wrote nothing, and it is
    /// this feature's whole discipline that those requests go to an API doing us a favour by
    /// answering at all.
    /// </remarks>
    public sealed record RecoverySummary(
        int Learned,
        int LinksRead,
        int Probed,
        int Answered,
        int Confirmed,
        int Unprobeable,
        int Fetched,
        int Unreadable,
        int Unavailable,
        int Listings,
        int Checked,
        int Recovered,
        int Ambiguous,
        int Blocked);
}
