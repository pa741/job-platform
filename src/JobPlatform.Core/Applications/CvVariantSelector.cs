using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;

namespace JobPlatform.Core.Applications;

/// <summary>
/// What the arithmetic concluded about which CV to send.
/// </summary>
/// <remarks>
/// <b>Three answers, and the middle one is not a failure.</b> The obvious shape here is a
/// nullable variant - one is chosen or none is - and it collapses two situations that want
/// opposite handling. <see cref="Ambiguous"/> says two documents fit this posting equally well,
/// which is a question worth asking a model that has the advert in front of it;
/// <see cref="NoFit"/> says none of them fits, which is not a question at all but a brief for
/// the CV the candidate has not written yet. Merging them would hand the second case to a model
/// that can only pick the least wrong answer, which is exactly the failure this whole feature
/// removes.
///
/// <b>Numbered from one</b>, like every other classification in this codebase: a zero member
/// acquires <c>default</c>, and a stored zero would then read as a real decision about which
/// document went out under somebody's name rather than as "nothing was recorded".
/// </remarks>
public enum CvSelectionOutcome
{
    /// <summary>One variant cleared the floor and beat the rest by the margin. Send it.</summary>
    Chosen = 1,

    /// <summary>
    /// Two or more variants are too close to separate. A decision, not an error.
    /// </summary>
    /// <remarks>
    /// The point at which this pipeline hands over rather than guesses. Settling it on the lower
    /// id would be deterministic and meaningless - id order is authoring order - and settling it
    /// on a hundredth of a point would dress a coin toss as arithmetic. The contenders are in
    /// <see cref="CvSelection.Tied"/>, and what a model is asked is which of <i>these</i> to
    /// send: a closed question over a fixed set, never a request to write.
    /// </remarks>
    Ambiguous = 2,

    /// <summary>
    /// Nothing in the library fits. <see cref="CvSelection.Missing"/> says what would.
    /// </summary>
    /// <remarks>
    /// Deliberately not "send the nearest one". The nearest CV to a job it does not fit is the
    /// failure this design replaces, and it is invisible - the application simply never comes
    /// back and nothing in the system ever learns why. An abstention is visible, and it is the
    /// only outcome here that leaves behind something the candidate can act on.
    /// </remarks>
    NoFit = 3,
}

/// <summary>
/// One CV variant, flattened to exactly what the selector may read.
/// </summary>
/// <remarks>
/// <b>Not the variant row, and the narrowing is the guarantee rather than a convenience.</b>
/// This carries no markdown, no blob path and no hash: a selector that cannot reach the document
/// cannot be tempted to read it, so the choice rests on concepts that are stored, auditable and
/// re-derivable rather than on prose nobody diffed. The same argument that keeps
/// <c>CandidateFacts</c> away from a candidate's name.
///
/// <b>Keys, not assertions, which is the spec's selection-only guard read from the other
/// end.</b> A variant's concepts feed selection and nothing else - they must never reach
/// <c>ProfileConcepts</c>, never move a match score, never widen what the candidate is judged to
/// have. Carrying <see cref="AssertionPolarity"/> here would additionally let a document that
/// describes itself emphatically outscore one that mentions the same work plainly, which is a CV
/// inflating the record it was written from through a side door. Presence or absence is all this
/// is entitled to read.
///
/// <b>No <c>IsArchived</c>, on purpose.</b> Archiving removes a variant from selection, and the
/// caller does that by not passing it - which leaves the spec's third open question (may an
/// archived variant still be sent for a re-application to the same employer?) open, where a
/// filter in here would have quietly answered it "never".
/// </remarks>
public sealed record CvVariantFacts
{
    /// <summary>The variant's row id. Recorded on the submission, which is what lets C4 correlate outcomes.</summary>
    public required long VariantId { get; init; }

    /// <summary>The candidate's own name for it - "Backend .NET". Used in the rationale, never matched on.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The concept keys extracted from this variant's markdown.
    /// </summary>
    /// <remarks>
    /// Keys rather than labels, for the reason the whole vocabulary is keyed:
    /// <c>skill.kubernetes</c> is the identity and "Kubernetes" is an attribute of it. A selector
    /// matching on labels would stop agreeing with the matcher the first time one was renamed.
    /// </remarks>
    public IReadOnlyCollection<string> ConceptKeys { get; init; } = [];
}

/// <summary>What one variant scored against this posting.</summary>
/// <param name="VariantId">The row id, so a caller can fetch the document without a second lookup.</param>
/// <param name="Label">The candidate's name for it, as it appears in <see cref="CvSelection.Rationale"/>.</param>
/// <param name="Score">
/// 0-100: the weighted share of the posting's discriminating requirements this variant answers,
/// with partial credit for the adjacencies <c>MatchScorer</c> already prices. Rounded once, here.
/// </param>
/// <param name="Answered">
/// How many of those requirements it answers <i>outright</i> - exactly, or with something the
/// vocabulary says entails them. Deliberately stricter than <paramref name="Score"/>: the score
/// ranks documents against each other, where this is the count a person reading an audit line
/// wants, and the two questions have different right answers about a near miss.
/// </param>
public readonly record struct CvVariantScore(long VariantId, string Label, int Score, int Answered);

/// <summary>
/// Which CV this posting gets, or why it gets none.
/// </summary>
/// <remarks>
/// <b>All of this travels through the pack, because a person may later ask why this CV was
/// sent.</b> An answer to that question assembled afterwards from a variant id and a memory of
/// what the constants were is not an answer, it is a reconstruction - so the numbers, the losers
/// and the prose are returned together. Everything here is derived, so a caller may store it,
/// recompute it, or both, and <see cref="Version"/> says which rule produced it.
///
/// <b>Not one concept a variant asserted leaves this type.</b> The scores are numbers, the
/// missing set is what the <i>posting</i> asked for, and the rationale names posting concepts and
/// variant labels. A caller therefore cannot accidentally feed a document's own vocabulary back
/// into the profile it was written from: the guard is structural rather than a rule somebody has
/// to remember, and <c>CvVariantSelectorTests</c> asserts it.
/// </remarks>
public sealed record CvSelection
{
    /// <summary>
    /// Bumped whenever the selector would choose differently for the same library and posting.
    /// </summary>
    /// <remarks>
    /// Recorded alongside the chosen variant for the reason <c>MatchResult</c> and
    /// <c>MatchRanker</c> carry their own: C4 correlates replies against the CV that was sent,
    /// and a constant moved halfway through that window silently averages two experiments
    /// together. Separate from both of theirs, because a scorer change and a floor change here
    /// are stale for different reasons and cost different things to redo.
    /// </remarks>
    public const int CurrentVersion = 1;

    /// <summary>Chosen, ambiguous, or nothing fits.</summary>
    public required CvSelectionOutcome Outcome { get; init; }

    /// <summary>
    /// The variant to send. Non-null <i>only</i> when <see cref="Outcome"/> is
    /// <see cref="CvSelectionOutcome.Chosen"/>.
    /// </summary>
    /// <remarks>
    /// Null rather than "the leader anyway" on the other two outcomes, and that is the type doing
    /// the arguing: a field holding the best of a bad set is a field somebody eventually sends.
    /// The leader is still visible as the head of <see cref="Scores"/> for the audit trail, where
    /// reading it is a deliberate act rather than the path of least resistance.
    /// </remarks>
    public CvVariantScore? Chosen { get; init; }

    /// <summary>
    /// The second-best variant, whatever the outcome, or null where only one was offered.
    /// </summary>
    /// <remarks>
    /// Present on every outcome because it is the number the margin was measured against, and an
    /// audit line saying "a lead of 32" is unreadable without the thing it was 32 ahead of.
    /// </remarks>
    public CvVariantScore? RunnerUp { get; init; }

    /// <summary>Every variant offered, best first, ties broken on id so the order is stable.</summary>
    /// <remarks>
    /// The id tie-break orders the list and never decides the outcome: the margin is applied
    /// first, so two variants a point apart are <see cref="CvSelectionOutcome.Ambiguous"/> before
    /// this comparison is ever consulted. It exists so the same library and the same posting
    /// render the same page twice, which is a different requirement from picking a winner and
    /// must not be allowed to become one.
    /// </remarks>
    public IReadOnlyList<CvVariantScore> Scores { get; init; } = [];

    /// <summary>
    /// The variants the arithmetic could not separate. Populated only for
    /// <see cref="CvSelectionOutcome.Ambiguous"/>.
    /// </summary>
    /// <remarks>
    /// <b>The model's ballot, computed here so nobody spells the rule twice.</b> Whoever wires the
    /// tie-break would otherwise re-derive "within the margin of the leader" at the call site, and
    /// a second spelling of a constant is the drift <c>ParkReasonPolicy</c> derives its own lists
    /// to avoid. Empty on a decided selection, deliberately: offering a ballot for a choice
    /// already made is an invitation to re-open it.
    ///
    /// It can be wider than the two or three a tie-break pictures - a posting stating nothing that
    /// discriminates ties the whole library - so a caller sending it to a model bounds it. The
    /// selector will not do that bounding, because dropping contenders by id is the tie-break on
    /// id this outcome exists to refuse.
    /// </remarks>
    public IReadOnlyList<CvVariantScore> Tied { get; init; } = [];

    /// <summary>
    /// The posting's requirements that <i>no</i> variant answers outright, hardest demand first.
    /// </summary>
    /// <remarks>
    /// <b>Data, not prose, because it is the input to the gap brief rather than a message.</b> The
    /// brief aggregates these across every posting parked for want of a CV and ranks the gaps by
    /// how many applications each one blocks; that is a group-by over concept keys, and a sentence
    /// cannot be grouped. <c>ConceptGap</c> is reused rather than redeclared, so the brief and the
    /// match breakdown are demonstrably talking about the same rows.
    ///
    /// <b>Answered means answered outright</b> - exactly, by a specialisation, or by a curated
    /// implication, the three relations <c>MatchScorer</c> gives full credit. Adjacency earns
    /// score and does not close a gap: "you hold Vue and they asked for React" is a reason the
    /// document scores something, and it is not a reason to leave React out of the next CV.
    ///
    /// Populated on every outcome, because what the library does not cover is a fact about the
    /// library. It is only <i>acted on</i> for <see cref="CvSelectionOutcome.NoFit"/>, which is
    /// where a posting is actually blocked.
    /// </remarks>
    public IReadOnlyList<ConceptGap> Missing { get; init; } = [];

    /// <summary>
    /// Why this happened, in a sentence or two, for a person reading an audit trail later.
    /// </summary>
    /// <remarks>
    /// <b>Written for the question "why did you send them that CV?", which is asked months
    /// afterwards by somebody who does not have the constants to hand.</b> So it names the
    /// numbers, the floor and the margin in the same breath as the decision rather than assuming
    /// a reader who can look them up. It is derived and carries nothing the rest of this record
    /// does not: anything a caller needs to branch on is a field, never a string to be parsed.
    ///
    /// Concept <i>labels</i> here where the rest of the type carries keys, because this is the one
    /// half meant for human eyes; a key the vocabulary no longer knows still prints as itself,
    /// since an audit line that quietly omits a requirement is worse than one showing a raw key.
    /// Bounded at <see cref="CvVariantSelector"/>'s rationale limit - a posting stating forty
    /// requirements would otherwise write a paragraph nobody finishes into a column nobody sized
    /// for it.
    /// </remarks>
    public required string Rationale { get; init; }

    /// <summary>Which selection rule produced this. See <see cref="CurrentVersion"/>.</summary>
    public int Version { get; init; } = CurrentVersion;
}

/// <summary>
/// Chooses which of the candidate's CVs to send with one application, or declines to.
/// </summary>
/// <remarks>
/// <b>Written because a model put "I am an AI and they should have seen this" into an application
/// destined for a real employer.</b> A guard now drops that class of sentence, and a guard is a
/// net under a trapeze. This is the change that takes the model out of the document that matters
/// most: the candidate writes the CVs, the arithmetic picks one, and a model is asked at most
/// which of two finished documents to send. It cannot invent a claim about work it is not writing
/// about.
///
/// <b>The house idiom, unchanged: the arithmetic runs on everything and the model runs on what
/// survives it.</b> <c>MatchScorer</c> eliminates postings so the model is spent only on a
/// shortlist; this eliminates documents so the model is asked only about a tie. Pure and
/// Azure-free for the reason both of those are - a rule deciding which document went to which
/// employer is worth pinning exactly, and it is only pinnable while running it needs nothing but
/// a graph, a list of requirements and a list of concept sets.
///
/// <b>It never invents evidence and it never writes.</b> Every point traces to a requirement on
/// the posting and a concept extracted from a document the candidate authored, related through
/// the same curated graph the matcher uses: <c>MatchScorer.BestRelation</c> is called rather than
/// reimplemented, so the selector and the match breakdown cannot reach different conclusions
/// about the same pair of concepts.
///
/// <b>A concept that cannot discriminate must not carry a selection</b>, which this repository
/// learned expensively on the other side of the same join. "Home Delivery Driver" scored 94 on
/// the single word "containers", and a posting whose every stated requirement was "agile",
/// "cloud" or a board's <c>area.*</c> tag was scoring 100 against a profile it shared nothing
/// with. <c>Concept.IsDiscriminating</c> is the vocabulary's own judgement, made once for
/// resolution and read here one layer later so the two cannot drift; a variant therefore cannot
/// win a posting on "agile" and "cloud" alone, because those demands are not scored at all.
/// </remarks>
public static class CvVariantSelector
{
    /// <summary>
    /// The score a variant must reach before any CV is sent at all.
    /// </summary>
    /// <remarks>
    /// <b>50, and it is the boundary at which adjacency alone stops being enough.</b> Every
    /// partial-credit relation <c>MatchScorer</c> prices is worth less than half - a
    /// generalisation 0.45 at one step and less further up, a related edge 0.30, a superseded one
    /// 0.25 - so a variant answering <i>every</i> requirement by transferable ground and none of
    /// them outright tops out at 45 and fails, while one answering half of them exactly passes.
    /// That is the property worth having: <b>a CV cannot be sent on resemblance, it has to contain
    /// some of the things the advert actually asked for.</b>
    ///
    /// <b>It is reasoned rather than measured, and saying so matters here.</b> There is nothing to
    /// fit it against yet - the library it would be tuned on does not exist, which is itself the
    /// finding that motivated the feature: <c>ApplicationDraft.WriterVersion</c> is a constant and
    /// revisions differ only by free-text instructions, so there have never been variants to
    /// compare. C4 is what will eventually move this number, by correlating replies against the
    /// variant recorded on the submission. Until then it is derived from the credit table rather
    /// than from a preference, which is the strongest thing available and weaker than a
    /// measurement.
    ///
    /// <b>Set high rather than low, because the two mistakes are not the same size.</b> Sending
    /// the wrong CV fails silently - the application does not come back and nothing learns why -
    /// where abstaining produces a brief naming the CV that would unblock this posting and the
    /// others like it. Being told is worth more than being applied for.
    /// </remarks>
    public const int SelectionFloor = 50;

    /// <summary>
    /// How far ahead the winner must be before the arithmetic is trusted to have chosen.
    /// </summary>
    /// <remarks>
    /// <b>10 points, and it is calibrated to be one requirement wide.</b> With <c>n</c>
    /// requirements stated, a difference of one whole requirement answered is worth
    /// <c>100/n</c>, and a difference of one adjacency - the same requirement met exactly by one
    /// document and by transferable ground in the other - is worth about <c>55/n</c>:
    ///
    /// <code>
    /// n stated   one requirement apart   one adjacency apart
    /// 5          20.0                    11.0
    /// 8          12.5                     6.9
    /// 10         10.0                     5.5
    /// 14          7.1                     3.9
    /// </code>
    ///
    /// So between six and ten stated requirements - where an advert that states any at all
    /// usually sits - a margin of 10 separates two CVs differing by a whole requirement and
    /// refuses to separate two differing by which edge the graph happened to credit. The second
    /// half is the point: a gap that small is a fact about the vocabulary rather than about the
    /// documents, and resolving it by arithmetic would be dressing a coin toss as a decision.
    ///
    /// Outside that band it degrades in the honest direction. A terse posting separates almost
    /// anything, and the floor is what stops a thin lead from mattering; a long one ties almost
    /// everything, and a tie goes to something that can read the advert. Neither failure sends a
    /// document nobody chose.
    ///
    /// <b>The lead must reach the margin, not exceed it</b>, so a difference of exactly one
    /// requirement in a ten-requirement posting decides rather than ties. A boundary has to fall
    /// on one side, and this is the side where the arithmetic has genuinely said something.
    /// </remarks>
    public const int SelectionMargin = 10;

    /// <summary>
    /// What a required demand weighs against a softer one.
    /// </summary>
    /// <remarks>
    /// 0.375, which is <c>MatchScorer</c>'s own 0.15 against 0.40 carried over unchanged, so the
    /// two files cannot come to disagree about what "preferred" is worth. The <i>shape</i>
    /// deliberately differs: the scorer averages within two axes because they have to be combined
    /// with five non-concept ones on a single scale, and a variant has no salary, no location and
    /// no seniority to combine with. Weighting per demand avoids the artefact that structure would
    /// import here - one required demand against nine preferred ones would put 73% of a CV's score
    /// on the single required one, so a document answering it and nothing else would read as
    /// covering three quarters of the advert.
    ///
    /// <see cref="AssertionPolarity.Unspecified"/> lands with the softer set, exactly as it does
    /// in the scorer and for the same reason: it is by far the most common polarity, only the
    /// model pass can tell essential from desirable, and it has not necessarily run.
    /// </remarks>
    private const double SofterDemandWeight = 0.375;

    private const double RequiredDemandWeight = 1.00;

    /// <summary>
    /// How many concept names the rationale spells out before it counts the rest.
    /// </summary>
    /// <remarks>
    /// Five. The rationale is stored and read by a person, and a posting listing forty
    /// requirements would otherwise write a paragraph nobody finishes into a column nobody sized
    /// for it. Nothing is lost to the truncation: <see cref="CvSelection.Missing"/> carries every
    /// one of them as data, which is what the gap brief reads anyway.
    /// </remarks>
    private const int RationaleConceptLimit = 5;

    /// <summary>
    /// Picks the variant to send with an application to this posting, or declines to pick one.
    /// </summary>
    /// <param name="demands">
    /// What the posting asks for, as the matcher already models it - <c>PostingFacts.Concepts</c>
    /// passed straight through. Duplicates are folded here: the posting side stores a row per
    /// source, so a concept the board tagged and the description also named arrives twice by
    /// design and would otherwise weigh double for no reason but how thoroughly it was recorded.
    /// </param>
    /// <param name="variants">
    /// The library to choose from, already filtered to what may be sent. Archived variants are
    /// excluded by not being here; see <see cref="CvVariantFacts"/> on why that stays the
    /// caller's decision.
    /// </param>
    /// <param name="graph">The vocabulary. Defaults to the shipped one, as the scorer does.</param>
    public static CvSelection Select(
        IReadOnlyList<ConceptAssertion> demands,
        IReadOnlyList<CvVariantFacts> variants,
        ConceptGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(demands);
        ArgumentNullException.ThrowIfNull(variants);

        graph ??= ConceptGraph.Default;

        var stated = Deduplicate(demands);
        var judged = stated.Where(demand => Discriminates(demand, graph)).ToList();

        // Accumulated across the whole library rather than per variant: the question the gap
        // brief asks is what nothing covers, and a requirement one CV answers is not a gap merely
        // because the CV that won this posting did not answer it.
        var answeredByAny = new HashSet<string>(StringComparer.Ordinal);

        var scores = ScoreVariants(judged, variants, graph, answeredByAny);

        var missing = judged
            .Where(demand => !answeredByAny.Contains(demand.ConceptKey))
            .Select(demand => new ConceptGap(demand.ConceptKey, demand.Polarity, demand.YearsMin))
            .OrderByDescending(gap => gap.Demand)
            .ThenBy(gap => gap.RequiredKey, StringComparer.Ordinal)
            .ToList();

        // An empty library is answered before anything else, and it is NoFit rather than a tie
        // between nothing: writing the first CV is the action, and it is the action the gap brief
        // would name. What keeps this out of the park loop described below is that no variant
        // exists to satisfy the queue clause's existential - see there.
        if (scores.Count == 0)
        {
            return new CvSelection
            {
                Outcome = CvSelectionOutcome.NoFit,
                Missing = missing,
                Rationale = EmptyLibraryRationale(stated.Count, missing, graph),
            };
        }

        // Nothing this posting says distinguishes one kind of job from another: it stated no
        // requirements at all, or only "agile", "cloud" and a board tag naming a whole field.
        //
        // Ambiguous rather than NoFit, and the reason is mechanical rather than aesthetic. NoFit
        // parks the posting, and a posting parked for want of a CV must return "when a covering
        // variant is written" - a set difference against Missing, which here is empty. An empty
        // gap is covered by any variant that exists, so with a library in hand the posting would
        // return on the very next run, compute the same empty gap and park again: a loop rather
        // than a retry, which is precisely what ParkReason.MissingAnswer was given a third retry
        // class to avoid. Ambiguous has no such edge - it hands a real question to something that
        // can read the advert, which is where whatever this posting does say actually is.
        //
        // The queue clause must therefore ask whether *some* variant covers every missing
        // concept, never merely whether nothing is missing. That phrasing is also what keeps the
        // empty-library branch above out of the same loop.
        //
        // It is close to unreachable in production, which bounds what getting it slightly wrong
        // can cost: MatchScorer floors such a posting at zero on the identical test, so it never
        // clears the assessment threshold and never reaches the apply queue.
        if (judged.Count == 0)
        {
            return new CvSelection
            {
                Outcome = CvSelectionOutcome.Ambiguous,
                RunnerUp = scores.Count > 1 ? scores[1] : null,
                Scores = scores,
                Tied = scores,
                Missing = missing,
                Rationale = NothingToJudgeOnRationale(stated.Count, scores.Count),
            };
        }

        var leader = scores[0];
        var runnerUp = scores.Count > 1 ? (CvVariantScore?)scores[1] : null;

        if (leader.Score < SelectionFloor)
        {
            return new CvSelection
            {
                Outcome = CvSelectionOutcome.NoFit,
                RunnerUp = runnerUp,
                Scores = scores,
                Missing = missing,
                Rationale = NoFitRationale(leader, scores.Count, missing, graph),
            };
        }

        // The margin is vacuous with one variant, and that is right rather than a loophole: there
        // is nothing to be ambiguous between. The floor is what still has to be cleared, so a
        // single unsuitable CV is declined rather than sent by default.
        if (runnerUp is { } second && leader.Score - second.Score < SelectionMargin)
        {
            return new CvSelection
            {
                Outcome = CvSelectionOutcome.Ambiguous,
                RunnerUp = runnerUp,
                Scores = scores,
                Tied = [.. scores.Where(score => leader.Score - score.Score < SelectionMargin)],
                Missing = missing,
                Rationale = AmbiguousRationale(leader, second, scores),
            };
        }

        return new CvSelection
        {
            Outcome = CvSelectionOutcome.Chosen,
            Chosen = leader,
            RunnerUp = runnerUp,
            Scores = scores,
            Missing = missing,
            Rationale = ChosenRationale(leader, runnerUp, judged.Count, missing, graph),
        };
    }

    /// <summary>
    /// Every variant's score, best first, ties broken on id.
    /// </summary>
    /// <remarks>
    /// <see cref="MatchScorer.BestRelation"/> does the graph walk, deliberately, rather than a
    /// second implementation of it living here. Two definitions of what relates to what would
    /// surface as a pack claiming a CV covers ground the match breakdown says it does not, about
    /// the same two concept keys, on the same page.
    /// </remarks>
    private static List<CvVariantScore> ScoreVariants(
        List<ConceptAssertion> demands,
        IReadOnlyList<CvVariantFacts> variants,
        ConceptGraph graph,
        HashSet<string> answeredByAny)
    {
        var weights = demands.Select(Weight).ToList();
        var totalWeight = weights.Sum();

        var scores = new List<CvVariantScore>(variants.Count);

        foreach (var variant in variants)
        {
            var earned = 0.0;
            var answered = 0;

            for (var i = 0; i < demands.Count; i++)
            {
                var best = MatchScorer.BestRelation(variant.ConceptKeys, demands[i].ConceptKey, graph);

                if (best is not { } relation)
                {
                    continue;
                }

                earned += weights[i] * Math.Clamp(relation.Credit, 0, 1);

                if (!Entails(relation.Relation))
                {
                    continue;
                }

                answered++;
                answeredByAny.Add(demands[i].ConceptKey);
            }

            // Rounded once, here, so every consumer shows the number the decision was made on -
            // the reason MatchResult.Score is rounded in the scorer rather than in a view.
            var score = totalWeight <= 0
                ? 0
                : (int)Math.Round(Math.Clamp(earned / totalWeight, 0, 1) * 100, MidpointRounding.AwayFromZero);

            scores.Add(new CvVariantScore(variant.VariantId, Name(variant), score, answered));
        }

        scores.Sort(static (left, right) => right.Score != left.Score
            ? right.Score.CompareTo(left.Score)
            : left.VariantId.CompareTo(right.VariantId));

        return scores;
    }

    /// <summary>
    /// Whether meeting this demand says anything about what the job is.
    /// </summary>
    /// <remarks>
    /// A key the graph does not know counts as discriminating, exactly as it does in the scorer's
    /// concept floor, and the asymmetry is the argument: unknown is not the same as generic, and
    /// reading it as generic would let a vocabulary edit silently stop every posting still
    /// carrying the old key from being able to pull a CV at all.
    /// </remarks>
    private static bool Discriminates(ConceptAssertion demand, ConceptGraph graph)
        => !graph.TryGet(demand.ConceptKey, out var concept) || concept.IsDiscriminating;

    /// <summary>
    /// Whether this relation closes a gap, as opposed to merely earning credit against one.
    /// </summary>
    /// <remarks>
    /// The three relations <c>MatchScorer</c> gives full credit, named rather than compared
    /// against a credit threshold: the relation is what the vocabulary asserted, where the number
    /// is a weight somebody may tune, and a gap brief that changed its mind because a constant
    /// moved would be reporting on the wrong thing.
    /// </remarks>
    private static bool Entails(MatchRelation relation)
        => relation is MatchRelation.Exact or MatchRelation.Specialisation or MatchRelation.Implied;

    private static double Weight(ConceptAssertion demand)
        => demand.Polarity == AssertionPolarity.Required ? RequiredDemandWeight : SofterDemandWeight;

    /// <summary>One demand per concept, keeping the hardest. See <see cref="Select"/> on why.</summary>
    private static List<ConceptAssertion> Deduplicate(IReadOnlyList<ConceptAssertion> demands)
        => [.. demands
            .Where(demand => !string.IsNullOrWhiteSpace(demand.ConceptKey))
            .GroupBy(demand => demand.ConceptKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(demand => (int)demand.Polarity).First())];

    /// <summary>
    /// The variant's name for the audit trail, or something usable where it has none.
    /// </summary>
    /// <remarks>
    /// A label is candidate-authored text and may be blank; an audit line naming no document
    /// answers none of the question it exists for, so an unlabelled variant is identified by the
    /// id that will also be on the submission row.
    /// </remarks>
    private static string Name(CvVariantFacts variant)
        => string.IsNullOrWhiteSpace(variant.Label) ? $"variant {variant.VariantId}" : variant.Label.Trim();

    // -----------------------------------------------------------------------
    // The rationale. The only part of this file written for human eyes.
    // -----------------------------------------------------------------------

    private static string ChosenRationale(
        CvVariantScore leader,
        CvVariantScore? runnerUp,
        int demandCount,
        IReadOnlyList<ConceptGap> missing,
        ConceptGraph graph)
    {
        var lead = runnerUp is { } second
            ? $"over \"{second.Label}\" at {second.Score}, a lead of {leader.Score - second.Score} against a "
                + $"margin of {SelectionMargin}"
            : $"as the only variant offered, with nothing it had to beat by the margin of {SelectionMargin}";

        return $"Chose \"{leader.Label}\" at {leader.Score} of 100 {lead}, clearing the floor of {SelectionFloor}. "
            + $"It answers {leader.Answered} of {demandCount} {Requirements(demandCount)} this posting states"
            + (missing.Count == 0
                ? ", and nothing the posting asks for is missing from the library."
                : $". No variant in the library covers {Names(missing, graph)}.");
    }

    private static string AmbiguousRationale(
        CvVariantScore leader, CvVariantScore runnerUp, IReadOnlyList<CvVariantScore> scores)
    {
        var tied = scores.Count(score => leader.Score - score.Score < SelectionMargin);

        return $"Chose nothing: \"{leader.Label}\" at {leader.Score} of 100 and \"{runnerUp.Label}\" at "
            + $"{runnerUp.Score} are {leader.Score - runnerUp.Score} apart, inside the margin of "
            + $"{SelectionMargin}, so the arithmetic cannot separate them. {tied} variants are that close, and "
            + "the choice is handed on rather than settled on an id.";
    }

    private static string NothingToJudgeOnRationale(int statedCount, int variantCount)
        => "Chose nothing: this posting states "
            + (statedCount == 0
                ? "no requirements at all"
                : $"{statedCount} {Requirements(statedCount)} and not one of them says what the job is")
            + $", so there is nothing here that tells the {variantCount} variants apart.";

    private static string NoFitRationale(
        CvVariantScore leader, int variantCount, IReadOnlyList<ConceptGap> missing, ConceptGraph graph)
        => $"Sent nothing: the best of {variantCount} {(variantCount == 1 ? "variant" : "variants")}, "
            + $"\"{leader.Label}\" at {leader.Score} of 100, is below the floor of {SelectionFloor}. "
            + (missing.Count == 0
                ? "Every requirement is covered somewhere in the library, but not together in one CV."
                : $"No variant covers {Names(missing, graph)}.");

    private static string EmptyLibraryRationale(
        int statedCount, IReadOnlyList<ConceptGap> missing, ConceptGraph graph)
        => "Sent nothing: the library holds no variant to choose from. "
            + (missing.Count == 0
                ? $"This posting states {statedCount} {Requirements(statedCount)}."
                : $"A first CV would need to cover {Names(missing, graph)}.");

    private static string Requirements(int count) => count == 1 ? "requirement" : "requirements";

    /// <summary>Concept labels for a person, bounded, with the remainder counted rather than dropped.</summary>
    private static string Names(IReadOnlyList<ConceptGap> gaps, ConceptGraph graph)
    {
        var parts = gaps
            .Take(RationaleConceptLimit)
            .Select(gap => graph.TryGet(gap.RequiredKey, out var concept) ? concept.Label : gap.RequiredKey)
            .ToList();

        if (gaps.Count > RationaleConceptLimit)
        {
            parts.Add($"{gaps.Count - RationaleConceptLimit} more");
        }

        return parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }
}
