namespace JobPlatform.Core.Dedup;

/// <summary>Where an apply URL came from, and therefore how much to trust it.</summary>
/// <remarks>
/// <b>The numbering is not the strength order, and that is the trap.</b>
/// <see cref="BoardPosting"/> is zero because it is the <i>absence</i> of a link and an unset
/// value should read as "nothing known"; <see cref="MatchedOnAnotherBoard"/> is the weakest of
/// the four and yet outranks two of them numerically, because it is an inference from a title,
/// an employer and a city rather than something a board published. So anything sorting on the
/// value gets the ordering inverted exactly at the top, where it decides whether an agent is
/// handed the employer's own form or a guess. <see cref="PostingCluster"/> asks a function
/// instead, and that is what keeps the numbers free to mean nothing.
///
/// <b>Nobody may renumber these, and the paragraph below is exactly what will convince somebody
/// they may.</b> The numbers are persisted nowhere, so a renumber breaks no migration, fails no
/// test and produces no compiler error, and sorting the members into strength order looks like
/// tidying up after the first paragraph. What it moves is the meaning of a digit on the wire.
/// <c>list_applyable</c> takes this provenance as a string a model chose, and the parse rejects a
/// digit naming no member while accepting one that does - so <c>"2"</c> is
/// <see cref="MatchedOnAnotherBoard"/> today and would be whatever inherited that number
/// tomorrow. The <i>names</i> are what cross the MCP surface and the names would be unchanged,
/// which is the whole problem: neither side of the call has anything left to compare. And the
/// answer is acted on rather than displayed - a client carries it into <c>create_submission</c>
/// as the channel it applied through, so a filter quietly re-pointed writes a row that
/// misdescribes where a real application went, in a log whose entire purpose is that later
/// decisions read it instead of the world. A new member takes the next free number, exactly as
/// <c>AtsVendor</c> does.
///
/// <b>This lives in Core rather than beside the query that projects it.</b> It was declared in
/// <c>JobPlatform.Data.Sql</c> first, which put the values a posting's applicability turns on
/// behind a database. Deciding which of two rows for one job an agent can actually apply
/// through is arithmetic over those values and belongs beside <c>MatchScorer</c> and
/// <see cref="JobFingerprint"/>, where it is assertable without one - so the declaration moved
/// here and the repository imports it. The stored numbers did not change, because they are
/// persisted nowhere: the column is derived on read.
/// </remarks>
public enum ApplyUrlSource
{
    /// <summary>No direct link known. This is the board's own posting page.</summary>
    BoardPosting = 0,

    /// <summary>The posting itself published the employer's apply URL.</summary>
    Posting = 1,

    /// <summary>
    /// Taken from the same job on another board, matched on title, employer and city.
    /// </summary>
    /// <remarks>
    /// An inference, and the provenance exists so a caller can tell it from a fact. It recovers
    /// roughly 5% of the links LinkedIn stopped publishing, at no request and no risk, and the
    /// city is part of the match because without it better than a quarter of the candidates were
    /// one employer advertising one title in several cities.
    /// </remarks>
    MatchedOnAnotherBoard = 2,

    /// <summary>
    /// Read off the employer's own applicant tracking system, matched to this posting by title
    /// and place.
    /// </summary>
    /// <remarks>
    /// <b>A fourth value rather than a shade of one of the other three, because the two ways it
    /// can be wrong belong to neither.</b> The recovered link is real and the form opens either
    /// way, so nothing a browser sees separates a good recovery from a bad one - only the
    /// provenance can. The board token may belong to somebody else: "Dex", "Kernel", "Fin" and
    /// "Orbital" are all real boards owned by <i>a</i> company, not necessarily the one on the
    /// advert. And the title match may land on the vacancy next to the right one, because an
    /// employer's board is a catalogue rather than a page - Cloudflare's answers with 333 jobs.
    /// Folded into <see cref="Posting"/> both failures become invisible; folded into
    /// <see cref="MatchedOnAnotherBoard"/> the queue would rank a link the employer itself
    /// publishes below one borrowed off a stranger's listing.
    ///
    /// <b>Stronger than <see cref="MatchedOnAnotherBoard"/> because of who was asked.</b> That
    /// one borrows a link from a listing that merely <i>resembles</i> this one - three normalised
    /// strings agreeing, with nothing confirming the pair afterwards. This asks the employer's
    /// own system, which is the register of its own vacancies rather than a resemblance to it,
    /// and a token guessed from a company name is confirmed against a posting before it is
    /// trusted. <b>Weaker than <see cref="Posting"/></b> because that is the board carrying the
    /// advert naming the destination outright: no token to be right about, no title to match, and
    /// nothing between the advert and the link.
    ///
    /// <b>No account is used to obtain it, and that is a decision with a record.</b> Greenhouse,
    /// Ashby, Lever, Workable and SmartRecruiters serve public, documented, unauthenticated board
    /// listings that exist to be read by job seekers, so nothing on this path takes a credential,
    /// a cookie or a session. See <c>mcp_handoff.md</c> 3.2 and 3.2a: the authenticated LinkedIn
    /// route is closed rather than merely unbuilt, and this exists because that one is.
    ///
    /// It is worth an order of magnitude more than the cross-board recovery, which is why it was
    /// built at all. Measured 2026-09-07: of 382 applyable postings, 309 carried no employer link
    /// and every one of those was LinkedIn; 122 of them - 39% - sit at an employer whose board
    /// token is already known from a link held for another posting, and probing a slug from the
    /// name resolved 21 of a further 120. Against roughly 5% for
    /// <see cref="MatchedOnAnotherBoard"/>, on data already held.
    /// </remarks>
    MatchedOnEmployerAts = 3,
}

/// <summary>
/// One posting inside a cluster, as the choice between them reads it.
/// </summary>
/// <remarks>
/// <b>What this record does not carry is the design.</b> There is no site and no city, because
/// those two fields are the entire reason the existing cross-board apply-link recovery cannot be
/// reused to deduplicate. That query requires <c>Site != Site</c> and an exactly equal
/// <c>LocationCity</c>, and the live corpus breaks both requirements: two of its three duplicate
/// pairs are one job listed twice on a single board - Dex 551 against 4961, Harnham 968 against
/// 379, all four LinkedIn - and the third, Cloudflare 3020 against 3030, is filed under "London"
/// on one row and "Greater London" on the other. A rule with no field for a site and no field
/// for a city cannot rediscover either restriction by accident.
///
/// Membership is settled before any of this runs: the caller groups on the persisted
/// <c>CrossBoardKey</c> and hands one group over. This type only ever answers "which of these".
/// </remarks>
/// <param name="PostingId">The row, and the final tie-break - see <see cref="PostingCluster"/>.</param>
/// <param name="ApplyUrlSource">How the apply URL for this row was arrived at.</param>
/// <param name="AssessmentScore">
/// The model's score where it has judged this pair, null where it has not. Null loses to any real
/// score: the members are the same job, so a row nothing has judged is the row less is known
/// about rather than the row that scored badly.
/// </param>
/// <param name="RankScore">The queue's ordering key for this pair. Comparable only within one profile's pool.</param>
/// <param name="HasDocuments">
/// Whether generated documents exist for this posting. Reported to the caller and deliberately
/// not ranked on - see <see cref="PostingCluster"/>.
/// </param>
public readonly record struct ClusterMember(
    long PostingId,
    ApplyUrlSource ApplyUrlSource,
    int? AssessmentScore,
    double RankScore,
    bool HasDocuments);

/// <summary>
/// The same job, listed more than once, reduced to the one row an agent should act on.
/// </summary>
/// <remarks>
/// <b>Apply-URL strength outranks the assessment, and that ordering is measured rather than
/// preferred.</b> The Cloudflare pair in the live top-20 is the case that settles it: posting
/// 3020 carries the only direct ATS URL of the two and is assessed at <b>85</b>, against its twin
/// 3030 at <b>92</b>. Order by the score and the queue hands over 3030 - the better-judged row,
/// and the one an agent <i>cannot apply through</i>, because all 3030 has is a board posting
/// page. The verdict is a judgement about the job and both rows are the same job, so it cannot
/// separate them on anything that matters; the apply URL is the one difference between them that
/// changes what can be done next. The score is not ignored, it is second.
///
/// <b>The strength ladder has four rungs and the argument is entirely about the middle two.</b>
/// A link the advert's own board published outranks one read off the employer's applicant
/// tracking system, which outranks one borrowed from a listing that resembles this one, which
/// outranks having no link at all. The ends are obvious; the pair in the middle are both
/// inferences and look interchangeable. What separates them is who was asked and whether anybody
/// checked: <see cref="ApplyUrlSource.MatchedOnEmployerAts"/> comes from the employer's own
/// register of its own vacancies, through a board token confirmed against a posting before it is
/// used, while <see cref="ApplyUrlSource.MatchedOnAnotherBoard"/> is a third party's row that
/// agreed on three normalised strings and was never confirmed at all. Both can be wrong; only one
/// of them had a chance to be caught.
///
/// <b>Generated documents do not decide.</b> They sit on <see cref="ClusterMember"/> because the
/// queue reports them per row, and they are absent from the ordering because letting them in
/// makes the choice justify itself: documents are written <i>for</i> whichever row was primary
/// last time, so the first member to receive them would stay primary for good, even once a
/// sibling turned up carrying the employer's own apply URL. Exactly one posting in the whole
/// database has documents today, so the rule would decide nothing now and everything later,
/// which is the worst possible moment to discover it was wrong.
///
/// <b>The final tie-break is the lowest posting id, and its job is stability rather than
/// quality.</b> It makes the comparison a total order, so the primary does not depend on the
/// order rows came back in - and the primary is the cluster's identity in the queue, since the
/// alternates are derived from it and a submission against any member suppresses the whole
/// cluster. A primary that moved between two calls would hand a client a different row, and a
/// different alternates list, for a key it had already acted on. Ids are assigned on insertion,
/// so the lowest is the row seen first: the one anything earlier is already attached to.
///
/// <b>The dedupe key is required and must never be null.</b> <c>CrossBoardKey</c> answers null
/// where the city or the employer is unknown, and that null means "this posting has no
/// cross-board identity", not "it has the empty one". Grouping nulls together would merge every
/// unlocated posting in the corpus into one cluster - the collision the key returns null to
/// prevent - so a null key is a caller that has not filtered, and it is refused here.
/// </remarks>
/// <param name="DedupeKey">The <c>CrossBoardKey</c> every member shares.</param>
/// <param name="Primary">The row to act on.</param>
/// <param name="AlternatePostings">
/// The rest, best first by the same comparison that chose the primary. One comparison rather than
/// two, so the head of this list is always what <see cref="Choose"/> would return if the primary
/// were withdrawn - a second ordering would be free to disagree with the first.
/// </param>
public sealed record PostingCluster(
    string DedupeKey,
    ClusterMember Primary,
    IReadOnlyList<ClusterMember> AlternatePostings)
{
    private static readonly Comparer<ClusterMember> ByPreference = Comparer<ClusterMember>.Create(Preference);

    /// <summary>Picks the row to act on from one group of duplicates.</summary>
    /// <remarks>
    /// Pure, and kept separate from <see cref="From"/> so the rule can be asserted on its own:
    /// the evidence for it is four values on a handful of live rows, and a test that had to
    /// assemble a cluster to check an ordering would be testing the assembly instead.
    /// </remarks>
    public static ClusterMember Choose(IReadOnlyList<ClusterMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        if (members.Count == 0)
        {
            throw new ArgumentException("A cluster has at least one member.", nameof(members));
        }

        var best = members[0];

        for (var index = 1; index < members.Count; index++)
        {
            // Strictly better, never merely equal. The posting id makes Preference total, so
            // this cannot turn on arrival order - but writing it as `<= 0` would make it turn on
            // arrival order anyway the first time a caller hands over two rows for one id.
            if (Preference(members[index], best) < 0)
            {
                best = members[index];
            }
        }

        return best;
    }

    /// <summary>Assembles one group of duplicates into the shape the queue exposes.</summary>
    public static PostingCluster From(string dedupeKey, IReadOnlyList<ClusterMember> members)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);

        var primary = Choose(members);

        // Excluded by posting id rather than by value, which also collapses a row that arrived
        // twice. That is correct: one posting listed twice in a group is one posting.
        var alternates = members
            .Where(member => member.PostingId != primary.PostingId)
            .OrderBy(member => member, ByPreference)
            .ToArray();

        return new PostingCluster(dedupeKey, primary, alternates);
    }

    /// <summary>Negative where <paramref name="left"/> is the row to act on.</summary>
    private static int Preference(ClusterMember left, ClusterMember right)
    {
        var byStrength = Strength(right.ApplyUrlSource).CompareTo(Strength(left.ApplyUrlSource));

        if (byStrength != 0)
        {
            return byStrength;
        }

        // -1 for an unjudged row, not 0: a genuine assessment of zero is a judgement and sorts
        // above one that was never made, which is the opposite of what a plain default would do.
        var byAssessment = right.AssessmentScore.GetValueOrDefault(-1)
            .CompareTo(left.AssessmentScore.GetValueOrDefault(-1));

        if (byAssessment != 0)
        {
            return byAssessment;
        }

        var byRankScore = right.RankScore.CompareTo(left.RankScore);

        return byRankScore != 0 ? byRankScore : left.PostingId.CompareTo(right.PostingId);
    }

    /// <summary>How much an apply URL of this provenance is worth, largest first.</summary>
    /// <remarks>
    /// Written out rather than cast, because <see cref="ApplyUrlSource"/>'s own numbering is a
    /// different order and no reading of it is rescuable: comparing the values ascending puts
    /// <see cref="ApplyUrlSource.BoardPosting"/>, which is the absence of a link, at the top, and
    /// comparing them descending ranks <see cref="ApplyUrlSource.MatchedOnEmployerAts"/> and
    /// <see cref="ApplyUrlSource.MatchedOnAnotherBoard"/> - both inferences - above
    /// <see cref="ApplyUrlSource.Posting"/>, a link the board published outright.
    ///
    /// <b>The catch-all is the floor rather than a throw, and that is a trade with a cost worth
    /// naming.</b> An exhaustive switch would fail on a member nobody had ranked here, and this
    /// runs inside a projection over a whole queue, where one exception loses every other row
    /// with it. So an unranked member ranks as though no link were known - the safe direction,
    /// since it demotes a real link rather than promoting a bad one, but silent, and nothing else
    /// in the build would notice. Adding a member to <see cref="ApplyUrlSource"/> means editing
    /// this switch in the same commit;
    /// <c>Every_provenance_the_enum_declares_has_its_own_rung_in_the_ranking</c> is what makes
    /// forgetting a red test rather than a quiet demotion.
    /// </remarks>
    private static int Strength(ApplyUrlSource source) => source switch
    {
        ApplyUrlSource.Posting => 3,
        ApplyUrlSource.MatchedOnEmployerAts => 2,
        ApplyUrlSource.MatchedOnAnotherBoard => 1,
        _ => 0,
    };
}
