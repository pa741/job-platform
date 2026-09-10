using System.Linq.Expressions;
using JobPlatform.Data.Sql.Entities;

namespace JobPlatform.Data.Sql;

/// <summary>
/// Whether anything unattended could ever apply to a posting, as a predicate the database runs.
/// </summary>
/// <remarks>
/// <b>This decides who gets judged and nothing else, and that boundary is the whole design.</b>
/// It is the same rule the age reservation already lives under: a posting's reachability is a
/// fact about how a board publishes its adverts, not about whether the candidate fits, so it is
/// a claim on the judgement budget and never a claim on the match. Nothing here may reach
/// <c>MatchScorer</c>, <c>MatchRanker</c>, a stored score, a verdict, or any threshold a score is
/// compared against - a posting excluded from tonight's shortlist keeps the score it already had,
/// keeps its place on the matches page, and is judged the moment its link arrives. There are two
/// readers and they are the whole permitted surface:
/// <see cref="JobMatchRepository.GetUnassessedAsync"/>, which is the judgement budget, and
/// <see cref="JobMatchRepository.CountUnreachableAsync"/>, which only reports on what it removed.
///
/// <b>Permanent and temporary are different states and conflating them silently drops jobs
/// forever.</b> <c>OffsiteApply == false</c> is the board saying it hosts the application itself,
/// about <i>this</i> listing rather than one that resembles it - LinkedIn "Easy Apply" - and
/// nothing unattended can drive that form. Everything else with no link is <i>temporarily</i>
/// unreachable: <c>EmployerAtsBoardRepository</c>'s recovery pass targets exactly the postings
/// where <c>OffsiteApply != false</c>, and it succeeds on a material share of them, so those keep
/// their place in the budget. Skipping one of those would mean a posting whose link arrives
/// tomorrow was never judged and never comes back - the shortlist is drawn from unassessed pairs,
/// so a pair that is never drawn is never assessed and is never eligible again for any other
/// reason. That is strictly worse than spending one judgement on it.
///
/// <b>So the error falls towards judging, everywhere it can.</b> Every clause here has to hold
/// before a posting is held back, and the doubtful states - <c>OffsiteApply</c> null, a link from
/// either source, an employer whose own board is confirmed - all keep the posting in the draw.
/// The cost of being wrong in that direction is one judgement out of a nightly forty; the cost of
/// being wrong the other way is a vacancy the candidate never hears about, with nothing anywhere
/// reporting that it happened, because <b>a row that should be absent from a list is not
/// something anybody notices</b>.
///
/// <b>What it is worth, measured rather than assumed.</b> Against the live corpus on 2026-09-09
/// (8,411 postings; LinkedIn 6,056, freehire 1,281, Indeed 1,074): 691 postings across 336
/// companies are board-hosted with no link, and exactly 10 of them, at 7 companies, sit at an
/// employer with a confirmed ATS board. So <b>this bites on 681 postings today</b>, not on the
/// ~2,600 it will eventually reach. 4,364 LinkedIn postings - 72% of that board, and 52% of the
/// corpus - read <c>OffsiteApply == null</c> because they were last seen before the scraper's
/// <c>offsite_apply</c> classifier shipped, and a route-unknown posting reclassifies only when a
/// later search turns it up again. Under the rule above every one of those is temporarily
/// unreachable and keeps its budget place, which is correct and which is also why the saving today
/// is small. Do not quote the projection as the saving: it is what the same rule will be worth
/// once the corpus reclassifies, and it is a projection.
///
/// <b>Recovery will not rescue the ones that are held back, and that is the measurement that
/// makes "permanent" honest.</b> 10 of 691 is 1.4%: an employer running Easy Apply on LinkedIn
/// does not, in this corpus, also publish that vacancy on their own ATS board. Those 10 are kept
/// anyway - see <see cref="PermanentlyUnreachable"/> - because they are the only rows where the
/// word "permanent" is arguable, and 1.4% of a saving is a cheap price for not having to argue it.
/// </remarks>
public static class PostingReachability
{
    /// <summary>
    /// Pairs whose posting no unattended run could ever apply to, however good the match is.
    /// </summary>
    /// <remarks>
    /// <b>Four clauses, and each one is holding a different failure shut.</b>
    ///
    /// <c>OffsiteApply == false</c> and never <c>!= true</c>: the column is three-state and the
    /// third state is load-bearing. Null means nothing was established - the detail page was not
    /// read, the board does not say, or the posting predates the column - which is the ordinary
    /// state of two thirds of LinkedIn, and reading it as "board-hosted" is exactly the fault the
    /// column was added to undo.
    ///
    /// <b>The coalesce on that clause is not decoration, and the first version without it was
    /// wrong for 52% of the corpus.</b> <c>OffsiteApply == false</c> compiles to
    /// <c>OffsiteApply = 0</c>, which in SQL is NULL rather than false wherever the column is
    /// null - and <see cref="WorthJudging"/> wraps this whole tree in a <c>NOT</c>, where
    /// <c>NOT NULL</c> is still NULL and a <c>WHERE</c> drops the row. So the exclusion silently
    /// swallowed every route-unknown posting: 4,364 LinkedIn rows, the exact population this rule
    /// promises to keep. C# and SQL disagree about <c>null == false</c> and the disagreement only
    /// shows up under negation, which is the worst possible place for it to hide. Reading the
    /// absent value as <c>true</c> - "assume the application is offsite until something says
    /// otherwise" - makes the predicate two-valued, makes the negation exact, and states the safe
    /// default where a reader will see it. <c>MatchSweepReachabilityTests</c> caught this, and it
    /// is why the null case is asserted as its own theory row rather than alongside the others.
    ///
    /// The two link columns because a posting that carries either is reachable by definition and
    /// there is nothing to decide. They are read separately rather than through the ladder in
    /// <c>ApplyableRow</c> for the reason that ladder exists: <c>JobUrlDirect</c> is what the board
    /// published and <c>EmployerAtsApplyUrl</c> is what the employer's own system answered, and
    /// this predicate wants both to be absent rather than wanting to know which one won.
    ///
    /// <b>The confirmed-board clause is the carve-out, and it costs 10 postings to buy the
    /// argument.</b> An employer with a confirmed board is the one case where a link could still
    /// turn up for a board-hosted posting - the recovery pass declines to ask about such a posting
    /// today, on <c>WithoutEmployerLink</c>'s <c>OffsiteApply != false</c> clause, but that is a
    /// policy in another file that could reasonably change, and a rule whose safety depends on a
    /// second file not changing is not safe. Keeping them means the exclusion never has to defend
    /// the hardest 1.4% of its own claim.
    ///
    /// <b>A posting with no <c>CompanyId</c> matches no board and is therefore held back, which is
    /// the right way round.</b> That column is null where <c>CompanyNormalizer.Key</c> produced no
    /// key, and every route by which a link could arrive for such a posting - the fetch list, the
    /// probe list, the learned board - is keyed on the employer id. A posting the recovery pass
    /// cannot even address is more certainly unreachable than one it can, not less.
    /// </remarks>
    /// <param name="boards">
    /// The board table, passed rather than reached for, so this stays a predicate factory with no
    /// context of its own. EF composes it into the outer query as a <c>NOT EXISTS</c>.
    /// </param>
    public static Expression<Func<JobMatchEntity, bool>> PermanentlyUnreachable(
        IQueryable<EmployerAtsBoardEntity> boards)
    {
        ArgumentNullException.ThrowIfNull(boards);

        // Every clause has to be two-valued in SQL, and the coalesce is what makes the first one
        // so. See the remarks: `OffsiteApply == false` alone compiles to `OffsiteApply = 0`, which
        // is NULL rather than false for a route-unknown posting, and the NOT in WorthJudging then
        // drops it. Written this way the default for "nothing was established" is stated in the
        // predicate itself, which is also the clearer reading of the rule.
        return m => (m.Posting!.OffsiteApply ?? true) == false
            && m.Posting.JobUrlDirect == null
            && m.Posting.EmployerAtsApplyUrl == null
            && !boards.Any(b =>
                b.CompanyId == m.Posting!.CompanyId && b.ConfirmedAtUtc != null);
    }

    /// <summary>
    /// The same rule inverted: the pairs a judgement may be spent on.
    /// </summary>
    /// <remarks>
    /// <b>Derived from <see cref="PermanentlyUnreachable"/> rather than written out again</b>,
    /// which is the arrangement <c>ParkReasonPolicy</c> settled on for the same problem: one
    /// definition, two readers, nothing to drift. The alternative is the one the shortlist's
    /// channel filter is stuck with - two spellings of one rule held together by a test, which has
    /// already caught them diverging once - and it is stuck with it only because there was no way
    /// to derive one from the other. Here there is, so negating the tree is worth the four lines
    /// of expression surgery: a clause added above and forgotten below is not a failure this
    /// arrangement can have.
    ///
    /// The parameters are reused rather than rebound, so the negated tree is the same tree with a
    /// <c>NOT</c> on it and EF sees the query it would have seen.
    /// </remarks>
    public static Expression<Func<JobMatchEntity, bool>> WorthJudging(
        IQueryable<EmployerAtsBoardEntity> boards)
    {
        var unreachable = PermanentlyUnreachable(boards);

        return Expression.Lambda<Func<JobMatchEntity, bool>>(
            Expression.Not(unreachable.Body), unreachable.Parameters);
    }
}
