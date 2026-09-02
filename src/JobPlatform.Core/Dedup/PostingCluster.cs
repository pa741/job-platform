namespace JobPlatform.Core.Dedup;

/// <summary>Where an apply URL came from, and therefore how much to trust it.</summary>
/// <remarks>
/// <b>The numbering is not the strength order, and that is the trap.</b>
/// <see cref="BoardPosting"/> is zero because it is the <i>absence</i> of a link and an unset
/// value should read as "nothing known"; <see cref="MatchedOnAnotherBoard"/> carries the highest
/// number and is the weakest of the three, because it is an inference from a title, an employer
/// and a city rather than something a board published. So anything sorting on the value gets the
/// ordering inverted exactly at the top, where it decides whether an agent is handed the
/// employer's own form or a guess. <see cref="PostingCluster"/> asks a function instead.
///
/// <b>This lives in Core rather than beside the query that projects it.</b> It was declared in
/// <c>JobPlatform.Data.Sql</c> first, which put the three values a posting's applicability turns
/// on behind a database. Deciding which of two rows for one job an agent can actually apply
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
    /// different order: comparing the values would rank
    /// <see cref="ApplyUrlSource.MatchedOnAnotherBoard"/>, an inference, above
    /// <see cref="ApplyUrlSource.Posting"/>, a link the board published.
    /// </remarks>
    private static int Strength(ApplyUrlSource source) => source switch
    {
        ApplyUrlSource.Posting => 2,
        ApplyUrlSource.MatchedOnAnotherBoard => 1,
        _ => 0,
    };
}
