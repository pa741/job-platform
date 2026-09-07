using System.Linq.Expressions;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// A board the pass may fetch, and the only shape in which one leaves this repository.
/// </summary>
/// <remarks>
/// <b><see cref="ConfirmedAtUtc"/> is not nullable, and that is the guarantee rather than a
/// convenience.</b> A probed token that nobody confirmed must never produce an apply link -
/// "Dex", "Kernel", "Fin" and "Orbital" are all real boards belonging to <i>somebody</i>, not
/// necessarily to the employer on the advert - and the ordinary way that rule fails is a
/// <c>WHERE</c> clause somebody tidied. So the rule is made unrepresentable instead of documented:
/// there is no way to build one of these for a board that has not confirmed, and
/// <see cref="EmployerAtsBoardRepository.ListBoardsToFetchAsync"/> drops a row that fails the test
/// rather than widening the type. A caller cannot forget to check what it cannot be handed.
///
/// <b><see cref="Company"/> is here because the fetch is only half the job.</b> A probed board has
/// to be confirmed against the employer's name after it answers -
/// <c>AtsBoardCandidates.Confirm</c> takes that name - and a caller that had to go and get it
/// would go to <c>Companies</c>, which is where the unbounded employer blurb lives. It is read
/// here as one bounded column, deliberately, and it is the folded <c>DisplayName</c> rather than
/// the name printed on any one advert: the board hangs off <c>Companies.Id</c> precisely because
/// one employer's postings spell their name three ways.
/// </remarks>
/// <param name="CompanyId">The employer, as <c>Companies.Id</c>.</param>
/// <param name="Company">That employer's folded display name.</param>
/// <param name="Board">Vendor, token and region - everything the request needs and no URL.</param>
/// <param name="ConfirmedAtUtc">When this board was confirmed to be this employer's.</param>
/// <param name="LastFetchedUtc">When it was last asked, or null where nothing ever has.</param>
/// <param name="BlockedPostings">
/// How many of this employer's postings have no employer apply link from any source. What the
/// fetch is worth, and what the ordering is on.
/// </param>
public sealed record AtsBoardToFetch(
    int CompanyId,
    string Company,
    AtsBoard Board,
    DateTimeOffset ConfirmedAtUtc,
    DateTimeOffset? LastFetchedUtc,
    int BlockedPostings);

/// <summary>
/// An employer with no board on any vendor, and enough to build a candidate token from.
/// </summary>
/// <remarks>
/// <b><see cref="Company"/> is the input to <c>AtsBoardCandidates.For</c> and the reason this read
/// exists at all.</b> A probe is a slug built from a name, so a row with no name is a row with
/// nothing to try - and the name is the folded <c>Companies.DisplayName</c>, not the raw string
/// off an advert, because <c>CompanyNormalizer</c> has already reconciled "Contoso", "Contoso Ltd"
/// and "Contoso Limited" into the one employer this board would belong to.
///
/// <b>Nothing here says a probe is worth making.</b> That judgement is
/// <c>AtsBoardCandidates.For</c>'s, and it refuses most single-word names outright - Monzo, Stripe
/// and Revolut included - because a token that is a common noun belongs to whoever registered it
/// first. This read answers only "which employers are still unreachable, worst first"; an empty
/// candidate list for one of them is the expected answer and not a gap.
/// </remarks>
/// <param name="CompanyId">The employer, as <c>Companies.Id</c>.</param>
/// <param name="Company">That employer's folded display name.</param>
/// <param name="BlockedPostings">How many of their postings have no employer apply link.</param>
public sealed record AtsEmployerToProbe(int CompanyId, string Company, int BlockedPostings);

/// <summary>
/// A board that was asked and has shown nothing since. A token can be renamed and a company can
/// leave a vendor, and neither event announces itself.
/// </summary>
/// <remarks>
/// <b><see cref="LastFetchedUtc"/> is not nullable, which is what tells a dead board from one
/// nobody has asked about.</b> Those two want opposite work - one is a board to stop asking and
/// investigate, the other is an employer still owed a request - and this schema has already paid
/// once for letting two nulls share a representation, which is the fault
/// <c>JobPostings.OffsiteApply</c> exists to undo. A board that has never been fetched cannot be
/// expressed by this type at all, so it cannot arrive in a report of dead ones.
///
/// <b>The evidence of silence is the absence of checked postings, not a column.</b> The schema has
/// three timestamps and none of them means "it answered": <c>LastFetchedUtc</c> is stamped on
/// every attempt, because that is what bounds the pass to one request per employer whether or not
/// the request succeeded, and <c>ConfirmedAtUtc</c> may not be reused as an aliveness clock -
/// Core's own <c>AtsBoardCandidates.Confirm</c> cannot re-confirm a Lever board at all, so every
/// learned board would read as silent the moment it was first fetched. What a live board leaves
/// behind instead is <c>JobPostings.EmployerAtsCheckedUtc</c> across the employer's postings: a
/// board that answered was matched against them, and a board that answered nothing was not.
///
/// <b>What that reading misses, stated rather than hidden.</b> An employer holding two boards -
/// ordinary after an acquisition, and the reason the token is in the identity key - has one trail
/// of checked postings between them, so a dead board beside a live one is not reported. It
/// under-reports rather than over-reports, which is the direction to be wrong in for a report: the
/// cost is one wasted request per pass, where a false alarm would be somebody deleting a board
/// that works.
/// </remarks>
/// <param name="CompanyId">The employer, as <c>Companies.Id</c>.</param>
/// <param name="Board">The board that has gone quiet.</param>
/// <param name="Discovery">Whether the token was learned from a link or probed from a name.</param>
/// <param name="DiscoveredAtUtc">When this board was first attached to this employer.</param>
/// <param name="ConfirmedAtUtc">When it was confirmed, or null where it never was.</param>
/// <param name="LastFetchedUtc">When it was last asked. Never null - see the remarks.</param>
/// <param name="BlockedPostings">What the silence is costing, in postings with no link.</param>
public sealed record AtsSilentBoard(
    int CompanyId,
    AtsBoard Board,
    AtsBoardDiscovery Discovery,
    DateTimeOffset DiscoveredAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    DateTimeOffset LastFetchedUtc,
    int BlockedPostings);

/// <summary>
/// What one board read concluded about one posting, on its way into the row.
/// </summary>
/// <remarks>
/// <b>It carries <c>AtsListingMatch</c> whole rather than a URL and a confidence</b>, so that a
/// matched outcome with no listing, or a listing with no confidence, cannot be expressed by a
/// caller assembling this by hand - the rule Core already enforces by giving that type three
/// factories and no public constructor. An abstention and a no-match arrive here as themselves and
/// are recorded as themselves: both stamp the posting as asked and neither writes a link.
/// </remarks>
/// <param name="PostingId">The posting the board was asked about.</param>
/// <param name="Match">What the board's listings said about it.</param>
public sealed record AtsPostingMatch(long PostingId, AtsListingMatch Match);

/// <summary>
/// What has been learned about employers' own applicant tracking boards, and what a pass has
/// recovered with it.
/// </summary>
/// <remarks>
/// <b>Every read here is profile-agnostic, and that is worth saying out loud because every other
/// repository in this folder is not.</b> <c>FormAnswerRepository</c>, <c>CvVariantRepository</c>,
/// <c>JobMatchRepository</c> and <c>SubmissionRepository</c> all take a profile id and scope every
/// query to it, and a reader arriving here will assume the same and look for the clause that is
/// missing. It is missing because there is nothing to scope: a board token is a fact about an
/// employer, published by that employer to anybody who asks, and it is the same fact for every
/// candidate this system will ever hold. <b>Scoping it would make the feature worse rather than
/// merely redundant</b> - the bound this whole path runs under is one request per employer per
/// pass, and a per-candidate work list multiplies those requests by the number of candidates to
/// fetch bytes that were already fetched. So no method here takes a profile id, and
/// <c>The_reads_are_not_scoped_to_a_candidate</c> is what stops one being added by reflex.
///
/// <b>Nothing on this path takes a credential, a cookie or a session, and nothing added here
/// may.</b> The five vendors involved publish these boards to job seekers, unauthenticated and
/// documented; the authenticated LinkedIn route was researched and refused, and there is a legal
/// record behind that refusal rather than a preference. See <c>mcp_handoff.md</c> 3.2 and 3.2a.
/// There is no column, parameter or method here that could hold one.
///
/// <b>Two facts about a board and two columns, kept apart on purpose.</b>
/// <c>ConfirmedAtUtc</c> answers "may this board be used", and it is what
/// <see cref="ListBoardsToFetchAsync"/> filters on. <c>LastFetchedUtc</c> answers "when was it
/// last asked", and it is stamped by <see cref="RecordFetchAsync"/> on every attempt - answered or
/// not - because a dead board that recorded nothing would be re-asked every pass for ever, at
/// somebody else's expense. Neither may stand in for the other:
/// <see cref="AtsSilentBoard"/>'s remarks give the case where reusing one as the other silently
/// breaks Lever.
///
/// <b>Learned beats probed, and the direction is enforced rather than trusted.</b>
/// <see cref="LearnAsync"/> upgrades a probed row and <see cref="RecordProbeAsync"/> never touches
/// a row that already exists, so a probe cannot downgrade, re-date or un-confirm a token the
/// employer themselves published. Nothing here overwrites a confirmation either: a transient 500
/// from a vendor must not cost a board that was proved a year ago.
///
/// <b>The apply link is written beside what the board published and never over it.</b>
/// <c>JobUrlDirect</c> is what the board carrying the advert said; <c>EmployerAtsApplyUrl</c> is
/// what the employer's own system said when that board had gone quiet. Both open a form and
/// nothing a browser sees separates them, so the columns are the only thing that can - and a
/// caller that cannot tell an inference from a published fact has no way to notice when the match
/// was wrong. No method in this file assigns to <c>JobUrlDirect</c>, and
/// <c>A_recovered_link_never_touches_the_column_the_board_published</c> is what keeps it that way.
/// </remarks>
public sealed class EmployerAtsBoardRepository(JobsDbContext db)
{
    /// <summary>
    /// The width of <c>JobPostings.EmployerAtsApplyUrl</c>, restated rather than shared.
    /// </summary>
    /// <remarks>
    /// The column's own remarks refuse a shared constant on the grounds that three widths agreeing
    /// - this one, <c>JobUrlDirect</c> and <c>SubmissionLimits.MaxApplyUrlLength</c> - is a
    /// coincidence worth keeping rather than a fact to centralise, since widening one would then
    /// silently widen the others. So this is a local copy pinned by
    /// <c>A_recovered_link_too_long_for_its_column_is_refused_rather_than_truncated</c>, and it
    /// exists so the refusal happens here instead of as a truncation in the provider. <b>A
    /// truncated apply URL is worse than a refused one</b>: it is still a link, it still opens
    /// something, and what it opens is not the employer's form.
    /// </remarks>
    private const int MaxApplyUrlLength = 1000;

    /// <summary>
    /// A posting with no employer apply link from any source, on a board that does not host the
    /// application itself.
    /// </summary>
    /// <remarks>
    /// <b>One definition and three readers, because the alternative is measured.</b> This rule
    /// orders the fetch list, orders the probe list and decides whether either is worth making at
    /// all; written out at each of those it would be three spellings held together by nothing. The
    /// shortlist's channel filter is what that costs when it happens - two copies, a test to keep
    /// them honest, and one occasion on which they had already drifted - and
    /// <c>ParkReasonPolicy</c> is the arrangement that avoids it. An <see cref="Expression"/>
    /// composes into a query, where a static predicate over a column would have no SQL at all.
    ///
    /// <b><c>OffsiteApply != false</c> and not <c>== true</c>, and the third state is the reason.</b>
    /// False means that board says it hosts the application, and it is talking about <i>this</i>
    /// listing rather than one that resembles it - so there is nothing for an employer's ATS to
    /// recover and asking about it spends a request to arrive back where we started. Null means
    /// nothing was established, which is not the same claim and is the ordinary state of a posting
    /// whose detail page nobody read. Collapsing the two is the fault that column was added to
    /// undo, and the queue's own projection reads it exactly this way.
    ///
    /// <b>What is deliberately not in it.</b> No verdict, no score, no submission and no profile:
    /// this counts postings that are unreachable rather than postings one particular candidate
    /// wants, for the reason the class remarks give. And no freshness bound, because
    /// <c>LastSeenUtc</c> is a caller's policy about somebody else's request budget rather than a
    /// property of the link - <see cref="ListBoardsToFetchAsync"/> takes it as a parameter instead.
    /// </remarks>
    private static readonly Expression<Func<JobPostingEntity, bool>> WithoutEmployerLink =
        posting => posting.JobUrlDirect == null
            && posting.EmployerAtsApplyUrl == null
            && posting.OffsiteApply != false;

    /// <summary>
    /// Records a board read off a link the employer published, upgrading a probe of the same token.
    /// </summary>
    /// <remarks>
    /// <b>Confirmed the moment it is learned, and that is not a loophole.</b> The token was not
    /// guessed: it was lifted out of a URL the board carrying the advert published as this
    /// employer's apply link, so the evidence exists and its date is the date it was read. That is
    /// what makes the usable-board test one clause - <c>ConfirmedAtUtc != null</c> - rather than
    /// one clause per discovery path, and it is the only route by which a Lever board is ever
    /// usable at all, because Lever's public feed carries no company name for
    /// <c>AtsBoardCandidates.Confirm</c> to agree with.
    ///
    /// <b>An upgrade keeps the original discovery date, and that is the point of having two
    /// dates.</b> <c>DiscoveredAtUtc</c> answers "when did we start believing this", which is the
    /// first question asked about a board that turns out to belong to somebody else, and a probe
    /// that guessed the token in March is when the believing started even if a link proved it in
    /// September. The confirmation is stamped only where there was none: a probe already confirmed
    /// against the employer's name keeps the date it earned.
    ///
    /// <b>Re-learning an already-learned board writes nothing at all</b>, which matters more than
    /// it looks. The learned path runs over links already held, so it revisits the same board every
    /// pass; a version of this that re-stamped a timestamp each time would make the table's dates
    /// say "this was believed today" for ever, and would erase the only evidence that a board has
    /// been quiet since March.
    ///
    /// <b>A duplicate is refused by the unique index rather than by this method.</b> The read and
    /// the insert are two statements, and the learned path and the probe path are different code
    /// reaching the same employer in the same pass, so both can find the gap between them empty.
    /// The resulting <see cref="DbUpdateException"/> is deliberately not caught: it is
    /// indistinguishable at this level from an employer that is not in <c>Companies</c> at all,
    /// and swallowing that one would turn "this board could never be stored" into "already known".
    /// </remarks>
    /// <returns><c>true</c> where a row was written or upgraded; <c>false</c> where it was already known.</returns>
    public async Task<bool> LearnAsync(
        int companyId, AtsBoard board, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);

        var existing = await TrackedBoardAsync(companyId, board, ct);

        if (existing is null)
        {
            db.EmployerAtsBoards.Add(Row(companyId, board, AtsBoardDiscovery.Learned, now, confirmedAtUtc: now));

            await db.SaveChangesAsync(ct);

            return true;
        }

        if (existing.Discovery == AtsBoardDiscovery.Learned)
        {
            return false;
        }

        existing.Discovery = AtsBoardDiscovery.Learned;
        existing.ConfirmedAtUtc ??= now;

        await db.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>
    /// Records a token probed from the employer's name, unconfirmed and unusable until it is not.
    /// </summary>
    /// <remarks>
    /// <b>The row is written with no confirmation and cannot be fetched through until
    /// <see cref="ConfirmAsync"/> stamps one.</b> A 200 from a vendor proves that <i>somebody</i>
    /// owns the token and nothing more; four of the tokens this corpus produced - "Dex", "Kernel",
    /// "Fin" and "Orbital" - are real boards belonging to a company that is not the one on the
    /// advert. <see cref="ListBoardsToFetchAsync"/> cannot return this row, and the type it returns
    /// cannot represent it.
    ///
    /// <b>Storing an unconfirmed probe is the whole point rather than a side effect.</b> A token
    /// that answered and did not confirm is a request already spent on an API doing us a favour by
    /// answering at all, and the row is how the next pass knows not to spend it again -
    /// <see cref="ListEmployersToProbeAsync"/> offers only employers with no board row of any kind.
    ///
    /// <b>An existing row is left exactly as it is, whatever it says.</b> A probe may not overwrite
    /// a learned board, may not re-date one, and may not clear a confirmation somebody earned:
    /// learned beats probed, and the guess must never be able to displace the fact. It may not
    /// re-write a probe either, because <c>DiscoveredAtUtc</c> would then always be today.
    /// </remarks>
    /// <returns><c>true</c> where a row was written; <c>false</c> where this board was already known.</returns>
    public async Task<bool> RecordProbeAsync(
        int companyId, AtsBoard board, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);

        if (await TrackedBoardAsync(companyId, board, ct) is not null)
        {
            return false;
        }

        db.EmployerAtsBoards.Add(Row(companyId, board, AtsBoardDiscovery.Probed, now, confirmedAtUtc: null));

        await db.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>
    /// Records that a probed board proved itself this employer's.
    /// </summary>
    /// <remarks>
    /// <b>The evidence is a parameter and <c>Unconfirmed</c> writes nothing.</b> Confirmation is
    /// the single thing standing between a slug collision and an application sent to a company the
    /// candidate never applied to, so a caller has to say what it saw rather than assert that it
    /// saw something. <c>AtsBoardConfidence</c> numbers upwards from a zero that refuses, which is
    /// what makes the test below a comparison rather than a list: a value nobody set, a struct
    /// default or a member deserialised from nothing all read as "no evidence" and are declined.
    ///
    /// <b><c>NameAgrees</c> is the bar and <c>NameAndPostingAgree</c> is not required</b>, for the
    /// reason Core gives: a board listing no job that matches the advert is usually a board that
    /// closed the vacancy, not a board belonging to somebody else. A caller wanting the stronger
    /// claim raises its own bar before calling; nothing here can lower one.
    ///
    /// <b>The first confirmation stands and a later one does not move it.</b> The column answers
    /// "when was this proved", which is a fact about an event rather than a freshness stamp, and a
    /// date that crept forward on every pass would quietly become "today" and stop being able to
    /// answer anything. Freshness is <c>LastFetchedUtc</c>, and it is a different column for
    /// exactly this reason.
    /// </remarks>
    /// <returns><c>true</c> where this call confirmed the board; <c>false</c> otherwise.</returns>
    public async Task<bool> ConfirmAsync(
        int companyId,
        AtsBoard board,
        AtsBoardConfidence proved,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);

        // Before the read, so a caller with no evidence cannot even look. There is nothing to
        // learn from the row in that case and every reason not to have fetched it.
        if (proved < AtsBoardConfidence.NameAgrees)
        {
            return false;
        }

        var existing = await TrackedBoardAsync(companyId, board, ct);

        if (existing is null || existing.ConfirmedAtUtc is not null)
        {
            return false;
        }

        existing.ConfirmedAtUtc = now;

        await db.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>
    /// Records that the board was asked, whether or not it answered.
    /// </summary>
    /// <remarks>
    /// <b>Call it before the request, not after.</b> The stamp is what bounds the pass to one
    /// request per employer, and a pass that recorded the attempt only on success would re-ask a
    /// board that times out for as many postings as that employer has - against an API that exists
    /// to serve that vendor's own customers. Recording first also makes
    /// <see cref="ListSilentBoardsAsync"/> correct by construction: everything a live board leaves
    /// behind is stamped after this, so "nothing checked since the board was asked" cannot be
    /// produced by the order the two writes happen to land in.
    ///
    /// <b>It does not confirm and it does not un-confirm.</b> Answering proves the token resolves,
    /// which is not the same claim as the board belonging to this employer -
    /// <see cref="ConfirmAsync"/> is where that claim is made, from evidence. And a board that says
    /// nothing today keeps the confirmation it earned, because a vendor having a bad afternoon is
    /// not evidence that a token changed hands.
    /// </remarks>
    /// <returns><c>true</c> where the board was found and stamped.</returns>
    public async Task<bool> RecordFetchAsync(
        int companyId, AtsBoard board, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);

        var existing = await TrackedBoardAsync(companyId, board, ct);

        if (existing is null)
        {
            return false;
        }

        existing.LastFetchedUtc = now;

        await db.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>
    /// The boards worth asking this pass, the ones blocking the most postings first.
    /// </summary>
    /// <remarks>
    /// <b>Only confirmed boards, structurally.</b> The <c>WHERE</c> clause excludes an unconfirmed
    /// probe and <see cref="AtsBoardToFetch"/> cannot represent one, so the two have to fail
    /// together for a guess to reach a caller - and if the clause were ever tidied away, the row
    /// would be dropped rather than returned. A rule enforced by there being no way to express the
    /// alternative outlives one enforced by everybody remembering it.
    ///
    /// <b>Ordered by what the board is blocking, because that is what the feature is for.</b> This
    /// exists to unblock applications rather than to fill a table, and the corpus is lopsided:
    /// Cloudflare's board answers 333 jobs in one request and several employers hold a single
    /// posting each. A first-come ordering would spend a bounded budget on the tail. The count is
    /// the same rule the probe list orders on and the same rule that decides whether either is
    /// worth making - see <see cref="WithoutEmployerLink"/>.
    ///
    /// <b>Never-fetched boards lead the boards that have been fetched</b>, and the coalesced sort
    /// key is deliberate rather than trusting an engine's idea of where a null goes: SQL Server and
    /// SQLite agree today and nothing here should depend on that. The employer id is the final
    /// tie-break so the page is a total order - a bounded list whose contents changed between two
    /// identical calls would make a pass's own logs unreadable.
    ///
    /// <b>A board on a vendor with no public listing is dropped rather than thrown over.</b>
    /// Nothing at the database refuses a Workday row - the DbContext says why a check constraint
    /// would rot - so <c>new AtsBoard</c> would refuse it here, and one exception inside a
    /// projection loses every other row of the page with it. Dropping is also the right answer:
    /// a board that cannot be read is not a board worth a request.
    /// </remarks>
    /// <param name="asOf">The clock. The refetch window is measured back from here.</param>
    /// <param name="refetchAfter">
    /// How long a board's answer stands before it is worth asking again. Measured from the last
    /// attempt rather than from the last answer, so a board that is down is not hammered.
    /// </param>
    /// <param name="limit">The most boards to return. A budget for somebody else's API.</param>
    /// <param name="seenSince">
    /// Optional: count only postings seen at or after this. A board is not worth a request for
    /// vacancies nobody has seen advertised in months.
    /// </param>
    public async Task<IReadOnlyList<AtsBoardToFetch>> ListBoardsToFetchAsync(
        DateTimeOffset asOf,
        TimeSpan refetchAfter,
        int limit,
        DateTimeOffset? seenSince = null,
        CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var blocked = Blocked(seenSince);
        var cutoff = asOf - refetchAfter;

        var rows = await db.EmployerAtsBoards
            .AsNoTracking()
            .Where(b => b.ConfirmedAtUtc != null
                && (b.LastFetchedUtc == null || b.LastFetchedUtc <= cutoff)
                && blocked.Any(p => p.CompanyId == b.CompanyId))
            // Ordered before it is projected, and that ordering is not a style choice: EF cannot
            // translate an ORDER BY over a member of a projected record - it compiles and throws
            // "could not be translated" on the first run - while this form is one SELECT carrying
            // its own ORDER BY, so the page is chosen and returned in the same order.
            .OrderByDescending(b => blocked.Count(p => p.CompanyId == b.CompanyId))
            .ThenBy(b => b.LastFetchedUtc == null ? 0 : 1)
            .ThenBy(b => b.LastFetchedUtc)
            .ThenBy(b => b.CompanyId)
            .Take(limit)
            .Select(b => new BoardRow(
                b.CompanyId,
                // A correlated read of one bounded column rather than a navigation and an
                // Include. There is no navigation to Companies on purpose: Companies.Description
                // is the unbounded employer blurb, and an Include here would drag a paragraph per
                // employer into a pass that wanted a name and a token.
                db.Companies.Where(c => c.Id == b.CompanyId).Select(c => c.DisplayName).FirstOrDefault(),
                b.Vendor,
                b.Token,
                b.Region,
                b.ConfirmedAtUtc,
                b.LastFetchedUtc,
                blocked.Count(p => p.CompanyId == b.CompanyId)))
            .ToListAsync(ct);

        var boards = new List<AtsBoardToFetch>(rows.Count);

        foreach (var row in rows)
        {
            // Both halves have to hold, and the row is dropped rather than mended where either
            // does not. A confirmation is the whole permission to use this board; a vendor or a
            // token AtsBoard would refuse is a row nothing can fetch.
            if (row.ConfirmedAtUtc is not { } confirmed || !Fetchable(row.Vendor, row.Token))
            {
                continue;
            }

            boards.Add(new AtsBoardToFetch(
                row.CompanyId,
                // The foreign key makes a board with no employer row impossible, so this is the
                // impossible case rather than a real one - and the empty string is the harmless
                // reading of it: AtsBoardCandidates.Confirm agrees with no name at all, so a
                // nameless employer confirms nothing rather than confirming everything.
                row.Company ?? string.Empty,
                new AtsBoard(row.Vendor, row.Token, row.Region),
                confirmed,
                row.LastFetchedUtc,
                row.BlockedPostings));
        }

        return boards;
    }

    /// <summary>
    /// The employers with no board on any vendor, the ones blocking the most postings first.
    /// </summary>
    /// <remarks>
    /// <b>"No board at all" includes an unconfirmed probe, and that is the bound rather than an
    /// oversight.</b> A probe that answered and did not confirm cost a request; a probe that
    /// answered nothing wrote no row at all, which is the one case this read offers again. Treating
    /// an unconfirmed row as "still unreachable" would re-probe the same names every pass for ever
    /// - four tokens across five vendors is twenty requests per employer, and the measurement had
    /// 120 employers left to try.
    ///
    /// <b>Ordered by what the employer is blocking, on the same rule as the fetch list.</b> Probing
    /// is the weaker half of this feature - a slug from a name resolved 21 of 120, against 122 of
    /// 309 reachable from links already held - so the budget it spends is best spent where the most
    /// postings are stuck. The employer id is the final tie-break, for the stability reason the
    /// fetch list gives.
    ///
    /// <b>It reads two bounded columns of <c>Companies</c> and never the third.</b> The blurb is
    /// unbounded and deduplicating it out of every posting row is most of why that table exists.
    /// </remarks>
    /// <param name="limit">The most employers to return. A budget for somebody else's API.</param>
    /// <param name="seenSince">Optional: count only postings seen at or after this.</param>
    public async Task<IReadOnlyList<AtsEmployerToProbe>> ListEmployersToProbeAsync(
        int limit, DateTimeOffset? seenSince = null, CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var blocked = Blocked(seenSince);

        return await db.Companies
            .AsNoTracking()
            .Where(c => !db.EmployerAtsBoards.Any(b => b.CompanyId == c.Id)
                && blocked.Any(p => p.CompanyId == c.Id))
            // Ordered before it is projected, for the reason ListBoardsToFetchAsync gives.
            .OrderByDescending(c => blocked.Count(p => p.CompanyId == c.Id))
            .ThenBy(c => c.Id)
            .Take(limit)
            .Select(c => new AtsEmployerToProbe(
                c.Id,
                c.DisplayName,
                blocked.Count(p => p.CompanyId == c.Id)))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The boards that have been asked and have shown nothing since.
    /// </summary>
    /// <remarks>
    /// <b>A board is not removed when it goes quiet, it is reported.</b> A token can be renamed and
    /// a company can leave a vendor, and neither event announces itself - the endpoint simply
    /// starts answering 404, which is also the ordinary answer for a token that was never a board.
    /// Deleting on that evidence would throw away a confirmation that cost a request, on the
    /// strength of an answer a vendor gives for several different reasons.
    ///
    /// <b>The silence is measured from the attempt and the evidence is the postings.</b> See
    /// <see cref="AtsSilentBoard"/> for why none of the three columns on the board row can say
    /// "it answered", what stands in for it, and the one case that reading under-reports.
    ///
    /// <b>Oldest attempt first, because that is how long the silence has run.</b> Nothing here
    /// filters on the blocked count: a dead board is worth seeing even at an employer with nothing
    /// outstanding today, since the postings that arrive tomorrow will be stuck behind it. The
    /// count comes back so a report can say what the silence is costing.
    /// </remarks>
    /// <param name="asOf">The clock. The silence window is measured back from here.</param>
    /// <param name="silentFor">
    /// How long a board must have been quiet before it is worth reporting. A board asked minutes
    /// ago has not gone quiet, it is mid-pass.
    /// </param>
    /// <param name="limit">The most boards to report.</param>
    public async Task<IReadOnlyList<AtsSilentBoard>> ListSilentBoardsAsync(
        DateTimeOffset asOf, TimeSpan silentFor, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var blocked = Blocked(seenSince: null);
        var cutoff = asOf - silentFor;

        var rows = await db.EmployerAtsBoards
            .AsNoTracking()
            .Where(b => b.LastFetchedUtc != null
                && b.LastFetchedUtc <= cutoff
                // Nothing of this employer's has been matched against a board since this one was
                // asked. A posting never checked at all compares to null and drops out, which is
                // the right way round: never checked is not evidence that anything answered.
                && !db.JobPostings.Any(p => p.CompanyId == b.CompanyId
                    && p.EmployerAtsCheckedUtc >= b.LastFetchedUtc))
            // Ordered before it is projected, for the reason ListBoardsToFetchAsync gives.
            .OrderBy(b => b.LastFetchedUtc)
            .ThenBy(b => b.CompanyId)
            .Take(limit)
            .Select(b => new SilentRow(
                b.CompanyId,
                b.Vendor,
                b.Token,
                b.Region,
                b.Discovery,
                b.DiscoveredAtUtc,
                b.ConfirmedAtUtc,
                b.LastFetchedUtc,
                blocked.Count(p => p.CompanyId == b.CompanyId)))
            .ToListAsync(ct);

        var silent = new List<AtsSilentBoard>(rows.Count);

        foreach (var row in rows)
        {
            // Dropped rather than thrown over, for the reason the fetch list gives. A board this
            // repository cannot express is one it cannot have asked either.
            if (row.LastFetchedUtc is not { } fetched || !Fetchable(row.Vendor, row.Token))
            {
                continue;
            }

            silent.Add(new AtsSilentBoard(
                row.CompanyId,
                new AtsBoard(row.Vendor, row.Token, row.Region),
                row.Discovery,
                row.DiscoveredAtUtc,
                row.ConfirmedAtUtc,
                fetched,
                row.BlockedPostings));
        }

        return silent;
    }

    /// <summary>Records what one board read concluded about one posting.</summary>
    /// <remarks>
    /// The single-posting spelling of <see cref="RecordMatchesAsync"/>, whose remarks carry the
    /// argument. One board read covers every posting that employer has, so the batch is the shape
    /// the pass actually calls in; this exists for the caller with one posting in hand and does not
    /// duplicate a line of it.
    /// </remarks>
    /// <returns><c>true</c> where the posting was found and stamped.</returns>
    public async Task<bool> RecordMatchAsync(
        long postingId, AtsListingMatch match, DateTimeOffset now, CancellationToken ct = default)
        => await RecordMatchesAsync([new AtsPostingMatch(postingId, match)], now, ct) == 1;

    /// <summary>
    /// Records what one board read concluded about each of an employer's postings.
    /// </summary>
    /// <remarks>
    /// <b>Every posting is stamped as asked and only a match writes a link.</b> The stamp is the
    /// third state that keeps the pass from re-asking the same employer about the same postings
    /// every run: without it a null <c>EmployerAtsApplyUrl</c> means either "the board was read and
    /// had nothing" or "nobody has asked yet", and those want opposite work. It is also the only
    /// trace an abstention leaves - <c>AtsListingMatch</c> distinguishes "nothing matched" from
    /// "several listings matched and the rule declined to choose", and both leave the link null.
    ///
    /// <b>A pass that found nothing this time does not erase what it found last time.</b> The link
    /// columns are left alone on a no-match, because a vacancy that closed and a title match that
    /// went ambiguous look identical from here, and clearing a good link on the second would cost
    /// an application to save a stale link that costs a click.
    ///
    /// <b><c>JobUrlDirect</c> is not assignable anywhere in this file.</b> A recovered link sits
    /// beside what the advert's own board published rather than on top of it, because both open a
    /// form and the column is the only thing that can say afterwards which of them was the
    /// inference.
    ///
    /// <b>Written as an update rather than as a read and a mutation, and the tracking rule is not
    /// being skirted.</b> That rule - say <c>AsTracking()</c> out loud on every read you then
    /// mutate - exists because this host once ran a global <c>NoTracking</c> under which four
    /// repositories silently saved nothing. There is no read here to track: the whole write is
    /// expressed as SQL over the three columns, which is also what keeps an unbounded description
    /// out of a pass that wanted to stamp a URL. It is correct under either tracking default and
    /// <c>Writes_land_under_a_host_that_tracks_nothing</c> pins that it stays so.
    ///
    /// <b>One transaction, because a board read is one fact about one employer.</b> Half a stamped
    /// employer is worse than an unstamped one: the unstamped postings would be re-asked and the
    /// stamped ones would not, so the next pass would spend the request and act on part of the
    /// answer. An ambient transaction is joined rather than nested where a caller already has one.
    ///
    /// <b>It refuses a link too long for its column rather than truncating one.</b>
    /// <c>FormAnswerRepository</c> makes the same choice for the same reason and
    /// <c>SubmissionRepository</c> makes the opposite one, correctly: a shortened audit line is
    /// still readable, where a shortened apply URL is a working link to the wrong place.
    /// </remarks>
    /// <param name="matches">
    /// One entry per posting the board was asked about. A posting named twice is refused - a batch
    /// that says two things about one row has no correct order to apply them in.
    /// </param>
    /// <param name="now">The moment the board was read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many posting rows were stamped.</returns>
    public async Task<int> RecordMatchesAsync(
        IReadOnlyCollection<AtsPostingMatch> matches, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (matches.Count == 0)
        {
            return 0;
        }

        var seen = new HashSet<long>(matches.Count);
        var matched = new List<(long PostingId, string ApplyUrl, AtsMatchConfidence Confidence)>();
        var asked = new List<long>();

        foreach (var entry in matches)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Match);

            if (!seen.Add(entry.PostingId))
            {
                throw new ArgumentException(
                    $"Posting {entry.PostingId} appears twice in one board read.",
                    nameof(matches));
            }

            // Both halves are read from the match rather than trusted separately: Core's factories
            // make a matched outcome without a listing or a confidence unrepresentable, and this is
            // where that guarantee is cashed in.
            if (entry.Match is { Outcome: AtsListingMatchOutcome.Matched, Listing: { } listing, Confidence: { } confidence })
            {
                matched.Add((entry.PostingId, ApplyUrl(listing, nameof(matches)), confidence));
            }
            else
            {
                asked.Add(entry.PostingId);
            }
        }

        // Through the execution strategy, because this context is configured with
        // EnableRetryOnFailure and that strategy REFUSES a user-initiated transaction outright.
        //
        // <b>Without this the feature does nothing and says nothing.</b> The first board read of the
        // first nightly pass reaches the first ExecuteUpdateAsync inside the transaction below and
        // throws InvalidOperationException - not a DbUpdateException, which is the only thing the
        // pass catches - so it escapes the whole invocation before the summary is logged. No posting
        // is ever stamped, no link is ever recovered, and the only symptom is a function that failed
        // at night. CvVariantRepository states this same rule and avoids the transaction entirely by
        // needing only one SaveChanges; here there are two statements that must land together, so
        // the transaction is real and the strategy has to own it.
        //
        // The whole body is the retriable unit: a retry that replayed the commit without the updates
        // would report a success that wrote nothing, which is the failure this method exists to make
        // impossible.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
        var joined = db.Database.CurrentTransaction is not null;
        var transaction = joined ? null : await db.Database.BeginTransactionAsync(ct);

        try
        {
            var stamped = 0;

            if (asked.Count > 0)
            {
                // One statement for the whole no-match arm. It is the bulk of a board read - an
                // employer's board is a catalogue and only some of it is the advert in hand - and
                // every row of it takes the same value.
                stamped += await db.JobPostings
                    .Where(p => asked.Contains(p.Id))
                    .ExecuteUpdateAsync(p => p.SetProperty(x => x.EmployerAtsCheckedUtc, now), ct);
            }

            foreach (var (postingId, applyUrl, confidence) in matched)
            {
                stamped += await db.JobPostings
                    .Where(p => p.Id == postingId)
                    .ExecuteUpdateAsync(
                        p => p
                            .SetProperty(x => x.EmployerAtsApplyUrl, applyUrl)
                            .SetProperty(x => x.EmployerAtsMatchConfidence, confidence)
                            .SetProperty(x => x.EmployerAtsCheckedUtc, now),
                        ct);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return stamped;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
        });
    }

    /// <summary>The apply URL, refused rather than truncated where it will not fit its column.</summary>
    /// <remarks>
    /// The parameter name is passed in rather than taken from the local, so the exception names the
    /// argument the caller actually passed - a paramName naming a private variable is a paramName
    /// nobody can act on.
    /// </remarks>
    private static string ApplyUrl(AtsListing listing, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(listing.ApplyUrl))
        {
            throw new ArgumentException(
                "A matched listing with no apply URL is not a recovery.",
                parameterName);
        }

        var url = listing.ApplyUrl.Trim();

        return url.Length <= MaxApplyUrlLength
            ? url
            : throw new ArgumentException(
                $"An apply URL of {url.Length} characters does not fit the {MaxApplyUrlLength} its column holds.",
                parameterName);
    }

    /// <summary>
    /// Whether a stored row still names a board Core is willing to describe.
    /// </summary>
    /// <remarks>
    /// The two things <c>AtsBoard</c>'s constructor refuses, asked before it is called. Nothing at
    /// the database enforces either - the DbContext argues at length that a check constraint would
    /// rot the day a sixth vendor joins the list - so a row naming Workday, or a token written by
    /// something that skipped Core, is possible and must not take a page of results down with it.
    /// </remarks>
    private static bool Fetchable(AtsVendor vendor, string token)
        => AtsBoardToken.ServesPublicBoard(vendor) && AtsBoardToken.IsToken(token);

    /// <summary>The postings with no employer link, optionally bounded to what has been seen lately.</summary>
    /// <remarks>
    /// Composed rather than written as <c>seenSince == null || ...</c> in the predicate, so the
    /// bound is absent from the SQL where the caller did not ask for one instead of arriving as a
    /// parameter every row has to be compared against.
    /// </remarks>
    private IQueryable<JobPostingEntity> Blocked(DateTimeOffset? seenSince)
    {
        var postings = db.JobPostings.Where(WithoutEmployerLink);

        return seenSince is { } since ? postings.Where(p => p.LastSeenUtc >= since) : postings;
    }

    /// <summary>
    /// The stored row for one board, tracked because every caller of this is about to change it.
    /// </summary>
    /// <remarks>
    /// <b><c>AsTracking()</c> is stated here rather than assumed</b>, and it reads as a restatement
    /// of EF's own default because it is one. It is not redundant: the API host once set
    /// <c>NoTracking</c> globally on the argument that it never wrote to SQL, and under that a
    /// read-then-mutate saves nothing and throws nothing - four write paths had been doing exactly
    /// that before anybody noticed. Here it would mean a confirmation that never landed and a board
    /// re-fetched every pass, both silent.
    ///
    /// <b>One helper rather than the clause four times.</b> The identity is four columns and it has
    /// to be all four - the same key the unique index is on - so a copy that dropped the region
    /// would find the wrong row and the fifth caller would be the one that dropped it. The tracking
    /// is in the name so a reader at the call site can still see it.
    ///
    /// The token is compared the way the index compares it, which is case-sensitively on SQLite and
    /// case-insensitively on Azure SQL. That difference is known, deliberate and costs a duplicate
    /// fetch rather than a wrong answer; nothing may "fix" it by folding the token, because
    /// SmartRecruiters keys its listings on a case-sensitive company id and the fold would be a
    /// silent false negative.
    /// </remarks>
    private Task<EmployerAtsBoardEntity?> TrackedBoardAsync(
        int companyId, AtsBoard board, CancellationToken ct)
        => db.EmployerAtsBoards
            .AsTracking()
            .FirstOrDefaultAsync(
                b => b.CompanyId == companyId
                    && b.Vendor == board.Vendor
                    && b.Token == board.Token
                    && b.Region == board.Region,
                ct);

    /// <summary>A new row, with the two dates the two paths disagree about spelled by the caller.</summary>
    private static EmployerAtsBoardEntity Row(
        int companyId,
        AtsBoard board,
        AtsBoardDiscovery discovery,
        DateTimeOffset now,
        DateTimeOffset? confirmedAtUtc) => new()
        {
            CompanyId = companyId,
            Vendor = board.Vendor,
            Token = board.Token,
            Region = board.Region,
            Discovery = discovery,
            DiscoveredAtUtc = now,
            ConfirmedAtUtc = confirmedAtUtc,
        };

    /// <summary>
    /// One fetchable board as SQL can answer it, before Core's own type is rebuilt from it.
    /// </summary>
    /// <remarks>
    /// Private and flat for the reason <c>JobMatchRepository.QueueRow</c> is: <c>AtsBoard</c>
    /// validates in its constructor and EF cannot call it in a projection, and a projection that
    /// materialised the entity would read every column of a table this query wants five of.
    /// </remarks>
    private sealed record BoardRow(
        int CompanyId,
        string? Company,
        AtsVendor Vendor,
        string Token,
        AtsBoardRegion Region,
        DateTimeOffset? ConfirmedAtUtc,
        DateTimeOffset? LastFetchedUtc,
        int BlockedPostings);

    /// <summary>One quiet board as SQL can answer it. See <see cref="BoardRow"/>.</summary>
    private sealed record SilentRow(
        int CompanyId,
        AtsVendor Vendor,
        string Token,
        AtsBoardRegion Region,
        AtsBoardDiscovery Discovery,
        DateTimeOffset DiscoveredAtUtc,
        DateTimeOffset? ConfirmedAtUtc,
        DateTimeOffset? LastFetchedUtc,
        int BlockedPostings);
}
