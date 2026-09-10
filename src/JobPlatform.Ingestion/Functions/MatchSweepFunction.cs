using JobPlatform.Core.Ai;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Model;
using JobPlatform.Core.Settings;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Scores every profile against the recent corpus, then spends the model budget on what clears
/// the threshold.
/// </summary>
/// <remarks>
/// <b>Two passes, and the split is the point.</b> The first is arithmetic over every candidate
/// pair - tens of thousands of them, costing nothing but a query and some in-memory work. The
/// second reads adverts with a language model and is capped hard. Running the model over
/// everything would cost real money to be told what a join already knew; running only the
/// arithmetic would give a number with no judgement behind it. Doing the cheap one first is
/// what makes the expensive one affordable.
///
/// <b>Nightly, after the ingest, rather than when somebody opens the page.</b> A shortlist that
/// costs model calls to look at is one nobody can afford to browse, and the scraper runs once a
/// day anyway - so anything computed on demand would usually be recomputing an unchanged
/// answer. By the time a candidate opens their matches the work is done and the page is a query.
///
/// The whole function is inert without an AI provider for its second pass only: scoring still
/// runs and still writes, because the arithmetic needs no model. That is a genuinely useful
/// degraded mode rather than a token one - a deployment with no provider configured still
/// produces ranked matches, just without the judgement layer.
///
/// <b>Two facts about the world make a claim on the second pass's budget and neither makes one on
/// the match.</b> A posting's age decides a reserved share of the shortlist; a posting's
/// reachability decides whether it is drawn at all. Both are facts about how the market publishes
/// adverts rather than about whether this candidate fits one, so no score, ranking, threshold or
/// verdict reads either - and the symmetry is worth keeping in mind whenever a third such fact is
/// proposed, because the pressure is always to let it "just nudge" the score. The reachability
/// rule and the measurement behind it are in <c>PostingReachability</c>; it is applied by the
/// shortlist query rather than here, because a selection applied after the draw would be a silent
/// reduction of the budget rather than a filter.
///
/// <b>Four of this pass's numbers are per-candidate settings now, and this file holds none of
/// them.</b> <c>MaxAssessments</c> (40) is <see cref="PipelineSettings.AssessmentsPerNight"/>,
/// <c>AssessmentThreshold</c> (45) is <see cref="PipelineSettings.AssessmentThreshold"/>,
/// <c>RecentShareNumerator</c> over <c>RecentShareDenominator</c> (two thirds) is
/// <see cref="PipelineSettings.RecentSharePercent"/> over a hundred, and the reservation window
/// that read <see cref="PostingAge.DailyWindowDays"/> straight is
/// <see cref="PipelineSettings.RecentWindowDays"/> - which merely <i>defaults</i> to it, because
/// that constant remains the system-wide definition of a recent posting that every other age
/// filter answers to. The old names are written out here so that a search for one still lands
/// somewhere; what is deliberately not written out is a second copy of any of the numbers. Each
/// default lives on the record once and the argument for it moved there with it, since a constant
/// kept alongside "for reference" is how two spellings of one decision start to drift.
///
/// <b>Every one of those four is a claim on the budget, which is why they were the four that could
/// be made settings at all.</b> They decide how many pairs are drawn, what a pair must score to be
/// eligible for the draw, and how much of the draw is held for recent postings. Nothing
/// configurable is handed to <c>MatchScorer</c> or <c>MatchRanker</c>, and nothing here could be
/// without changing what a match <i>means</i> rather than what a night costs: a candidate must not
/// be able to type their way to a better score. <c>MatchRanker.FusionFloor</c> in particular is
/// not a setting, is not read here, and is a different question from
/// <see cref="PipelineSettings.AssessmentThreshold"/> - the two were briefly one constant on the
/// grounds that they shared a value and that was a mistake.
/// </remarks>
public sealed class MatchSweepFunction(
    JobsDbContext db,
    CandidateProfileRepository profiles,
    JobMatchRepository matches,
    EmbeddingRepository embeddings,
    TimeProvider time,
    ILogger<MatchSweepFunction> logger,
    ICandidacyAssessor? assessor = null,
    ITextEmbedder? embedder = null,
    // Optional and trailing, like the assessor and the embedder above it and for the same reason:
    // a host that has not registered it - or a database whose settings table the migration has
    // not reached yet - still sweeps, on the defaults, which is the behaviour that shipped before
    // the table existed. See SettingsForAsync.
    PipelineSettingsRepository? pipelineSettings = null)
{
    /// <summary>
    /// How far back the scoring pass looks.
    /// </summary>
    /// <remarks>
    /// Bounded by recency rather than by relevance. A cheaper pre-filter - say, postings sharing
    /// one required skill - would need the score this pass exists to compute, and would drop
    /// exactly the roles a candidate is qualified for by a route the filter cannot see.
    /// Recency biases nothing about the match itself.
    ///
    /// Public because <c>EmbedCorpusFunction</c> reads it. That pass exists to serve this one,
    /// and a shorter window there would leave the oldest slice of every ranking silently unfused.
    /// </remarks>
    public const int LookbackDays = 45;

    /// <summary>Ceiling on how many postings one sweep scores per profile.</summary>
    private const int MaxPostings = 20_000;

    /// <summary>
    /// How many of the budget go to the measurement sample instead of to the shortlist.
    /// </summary>
    /// <remarks>
    /// <b>A quarter, and it buys the only thing that can settle any remaining question about the
    /// ranking.</b> Selecting top-down is right for the product and produces a labelled set that
    /// describes the top of the score range and nothing else: three consecutive nights returned
    /// 92-100, then 89-100, and every correlation computed from labels like those is
    /// range-restricted in exactly the way that made the score look anti-correlated at -0.198 when
    /// it is really +0.31. No quantity of further top-down labels fixes that; a different shape
    /// does.
    ///
    /// <b>It is free, which is the reason it is ten and not two.</b> These rows are merged into
    /// the same batches as the shortlist, and the assessor sends the candidate's profile once per
    /// batch - the profile being the larger half of the prompt. So ten rows riding along cost ten
    /// adverts' worth of tokens; a separate stratified pass would have paid for the profile again.
    /// Measured against the last three nights, a night is four batches of ten either way.
    ///
    /// The cost that is real is the shortlist losing ten of its forty - forty being what
    /// <see cref="PipelineSettings.AssessmentsPerNight"/> defaults to, and a quarter of whatever
    /// it is set to otherwise. That is affordable because the shortlist is not a queue that
    /// empties - it is the top of a ranking that is re-drawn nightly, so a row not judged tonight
    /// is judged tomorrow unless something better arrives, in which case judging the better one
    /// first was correct.
    ///
    /// <b>Not a setting, unlike the budget it is taken out of.</b> It is a research sample rather
    /// than a preference: it exists to answer whether the score works below the band the shortlist
    /// ever reaches, and a candidate turning it off would remove the only evidence that could
    /// settle that - while paying nothing for it, because these rows ride along in batches that
    /// are being sent anyway.
    /// </remarks>
    private const int MeasurementAssessments = 10;

    /// <summary>
    /// The bands the measurement sample is drawn from, below the shortlist's usual reach.
    /// </summary>
    /// <remarks>
    /// Stops at 89 deliberately: the top band is what the shortlist already covers every night, so
    /// spending measurement budget there buys a fourth copy of the only evidence the system has.
    /// The floor is 45 because that is what
    /// <see cref="PipelineSettings.AssessmentThreshold"/> defaults to, and below a candidate's
    /// threshold no pair is a candidate for judgement at all - so a band under it returns nothing
    /// and quietly wastes its slot, which is why a band entirely below the floor in force is
    /// skipped rather than queried.
    ///
    /// <b>The bands are fixed while the threshold is not, and that is a known consequence rather
    /// than an oversight.</b> A candidate who lowers their threshold below 45 opens a band this
    /// sample does not cover: the shortlist will judge down there and the stratified sample will
    /// not, so the labels stay a statement about 45 and up. That is a gap in the evidence rather
    /// than a fault in the run, and it is worth knowing before somebody reads those labels as
    /// describing the whole range. Widening the bands to follow a setting would make the sample
    /// mean something different for every candidate, which is the one thing a measurement series
    /// cannot survive.
    /// </remarks>
    private static readonly (int Min, int Max)[] MeasurementBands =
        [(45, 59), (60, 69), (70, 79), (80, 89)];

    /// <summary>
    /// How many rows to ask each band for before the merge trims to the budget.
    /// </summary>
    /// <remarks>
    /// More than <see cref="MeasurementAssessments"/> divided by the band count, so that a band
    /// which is exhausted or whose rows all lack a description does not silently shrink the
    /// sample - the merge takes what it can from the bands that do answer.
    /// </remarks>
    private const int MeasurementPerBand = 5;

    /// <summary>
    /// How many pairs one HTTP invocation may send to the model.
    /// </summary>
    /// <remarks>
    /// The platform gives an HTTP trigger roughly 230 seconds and the timer minutes, so the
    /// two cannot share a budget. Assessing forty pairs at raised reasoning effort does not
    /// fit in the shorter one - the first real sweep after the corpus was extracted was cut
    /// off before the model ran at all, leaving every verdict null and nothing saying why.
    ///
    /// The scoring pass is not bounded the same way. It is arithmetic over rows already in
    /// memory, it finishes in seconds for the whole corpus, and stopping it half way would
    /// leave a profile ranked against an arbitrary subset - which is worse than not ranking
    /// it at all.
    ///
    /// <b>A ceiling over <see cref="PipelineSettings.AssessmentsPerNight"/> and never a budget of
    /// its own</b> - see <see cref="BudgetFor"/>. It is deliberately not a setting: the number is
    /// the gateway's timeout, so it is a fact about the platform rather than a preference, and
    /// nobody should be able to type their way past it into a 504 that carries no answer back.
    /// </remarks>
    private const int MaxAssessmentsPerRequest = 10;

    [Function(nameof(MatchSweepFunction))]
    public async Task RunAsync(
        // 03:30 UTC: after the NAS scrape has uploaded and the ingest and extraction queues
        // have drained, and before anybody in the UK opens the dashboard.
        [TimerTrigger("0 30 3 * * *")] TimerInfo timer,
        CancellationToken ct)
        // No ceiling and no floor of its own. The timer has minutes rather than the HTTP
        // trigger's ~230 seconds, so what a night buys is entirely what each candidate asked for:
        // their AssessmentsPerNight, their AssessmentThreshold, their reservation and their
        // window, resolved per profile inside the loop.
        => await SweepAsync(
            profileId: null,
            assessmentCeiling: null,
            minScore: null,
            maxScore: null,
            ct);

    /// <summary>
    /// The same sweep, on demand.
    /// </summary>
    /// <remarks>
    /// Exists for the case the timer cannot serve: somebody has just filled in their profile for
    /// the first time and has nothing to look at until tomorrow morning. It is an admin endpoint
    /// rather than a user-facing one, because it is the expensive path and a route a client can
    /// call is a route a client can call repeatedly.
    ///
    /// Follows <c>ReprocessBlobFunction</c>: ASP.NET Core integration types because the host is
    /// built with <c>ConfigureFunctionsWebApplication</c>, and no <c>admin/</c> route prefix
    /// because the host reserves it and claiming it fails as a 404 rather than as an error.
    /// </remarks>
    [Function(nameof(RunMatchSweepFunction))]
    public async Task<IActionResult> RunMatchSweepFunction(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "run-match-sweep")]
        HttpRequest request,
        CancellationToken ct)
    {
        var body = await RequestBody.ReadAsync<SweepRequest>(request, ct);

        var summary = await SweepAsync(
            body?.ProfileId,
            // A ceiling over what each candidate configured rather than a budget of its own, so a
            // profile whose AssessmentsPerNight is lower than this still buys only what it asked
            // for - and one that has switched the judgement pass off stays off. The number is the
            // gateway's, not a preference.
            assessmentCeiling: MaxAssessmentsPerRequest,
            // Passed through unresolved, so that "the caller said nothing" reaches the place that
            // knows this candidate's own threshold. Substituting a floor here would sweep at a
            // number nobody configured.
            minScore: body?.MinScore,
            maxScore: body?.MaxScore,
            ct);

        return new OkObjectResult(summary);
    }

    /// <param name="ProfileId">Restrict to one profile. Null sweeps every profile.</param>
    /// <param name="MinScore">
    /// Floor on which pairs the model may be spent on. Defaults to this candidate's own
    /// <see cref="PipelineSettings.AssessmentThreshold"/>, which is what the timer sweeps at.
    /// </param>
    /// <param name="MaxScore">
    /// Ceiling, for drawing a sample from one score band instead of off the top.
    /// </param>
    /// <remarks>
    /// The band exists to fix a measurement problem rather than a matching one. Every assessment
    /// so far was selected by score, so every correlation computed from them describes only the
    /// top decile - pooling bias, and no amount of extra top-down assessing cures it. Sweeping a
    /// band at a time is how a stratified sample gets built, and a stratified sample is what makes
    /// those numbers statements about the corpus.
    ///
    /// Scoring is unaffected: the band bounds only which pairs the model is spent on.
    /// </remarks>
    public sealed record SweepRequest(long? ProfileId, int? MinScore = null, int? MaxScore = null);

    /// <param name="assessmentCeiling">
    /// An upper bound the caller imposes on every profile's own budget, or null for none. The HTTP
    /// route passes one because the gateway does; the timer passes none.
    /// </param>
    /// <param name="minScore">
    /// A floor the caller asked for explicitly, or null to sweep at each candidate's own
    /// threshold.
    /// </param>
    private async Task<SweepSummary> SweepAsync(
        long? profileId,
        int? assessmentCeiling,
        int? minScore,
        int? maxScore,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now.AddDays(-LookbackDays);

        var profileIds = profileId is { } single
            ? [single]
            : await matches.GetProfileIdsAsync(ct);

        if (profileIds.Count == 0)
        {
            logger.LogInformation("Match sweep: no profiles to score.");
            return new SweepSummary(0, 0, 0, 0, 0, 0);
        }

        // Once for the whole sweep, like the two reads below it and for the same reason with a
        // sharper edge: this one would otherwise sit at the very top of the per-profile loop,
        // spending a wakeup on a database billed by wall-clock time before any of the work that
        // would have justified being awake. One query for every candidate costs what one query
        // for the first candidate costs.
        var configured = await SettingsForAsync(profileIds, ct);

        // Fetched once for the whole sweep. Every profile is scored against the same slice, and
        // re-reading tens of thousands of rows per profile would turn a nightly job into the
        // thing that exhausts the database's monthly grant.
        var postings = await matches.GetPostingFactsAsync(since, MaxPostings, ct);

        // Once for the whole sweep too, and for the same reason with more force behind it: these
        // are two-kilobyte blobs, so re-reading them per profile is megabytes of transfer to
        // recompute an answer that does not depend on which candidate is being scored.
        var vectors = await embeddings.GetPostingVectorsAsync(since, MaxPostings, ct);

        logger.LogInformation(
            "Match sweep: {Profiles} profile(s) against {Postings} posting(s) seen since "
            + "{Since:yyyy-MM-dd}; {Vectors} carry an embedding.",
            profileIds.Count, postings.Count, since, vectors.Count);

        var scored = 0;
        var assessed = AssessmentTally.Empty;

        foreach (var id in profileIds)
        {
            // Scoring is unaffected by any of this, and that is the invariant the whole file is
            // built around: it runs for every profile at the same cost whatever anybody
            // configured, because a setting may decide what is judged and never what a match is.
            scored += await ScoreAsync(id, postings, vectors, now, ct);

            // Resolved per profile rather than per run. The budget is per candidate on purpose -
            // a second profile must not go unjudged because the first filled the batch - and
            // nothing bounds the sum across candidates, which is the same hole
            // ScraperSearchValidation writes down for searches.
            var budget = BudgetFor(configured[id], assessmentCeiling, minScore);

            assessed += await AssessAsync(id, budget, maxScore, now, ct);
        }

        // Requested is reported beside written, always, because the two diverging is the whole
        // signal and a written count on its own cannot show it.
        logger.LogInformation(
            "Match sweep complete: {Scored} score(s) written, {Assessed} assessment(s) written "
            + "of {Requested} requested ({Discarded} discarded).",
            scored, assessed.Persisted, assessed.Requested, assessed.Discarded);

        // Logged even at zero, and deliberately: the number this rule was argued from is 681
        // postings today against a projected ~2,600 once the corpus reclassifies, and a claim
        // that size cannot rest on a line that only appears when it is already interesting.
        // "Held out of the draw" rather than "saved": the draw is bounded, so an excluded pair
        // is usually replaced by the next-best one and the night costs the same either way.
        // CountUnreachableAsync says why, and this wording is the only thing stopping the two
        // being read as one number.
        logger.LogInformation(
            "Match sweep: {Unreachable} pair(s) held out of the judgement draw as permanently "
            + "unreachable - the board hosts the application and the employer publishes no "
            + "confirmed board. This is the pool the rule reached, not the judgements it saved.",
            assessed.Unreachable);

        return new SweepSummary(
            profileIds.Count,
            scored,
            assessed.Persisted,
            assessed.Requested,
            assessed.Discarded,
            assessed.Unreachable);
    }

    /// <summary>
    /// What one profile's judgement pass may draw, once this candidate's settings and the caller's
    /// own bounds have been resolved against each other.
    /// </summary>
    /// <param name="Assessments">Pairs this profile may send to the model on this run.</param>
    /// <param name="MinScore">The score a pair must clear to be a candidate for the draw.</param>
    /// <param name="RecentSharePercent">
    /// The percentage of the shortlist held for postings inside
    /// <see cref="RecentWindowDays"/>.
    /// </param>
    /// <param name="RecentWindowDays">
    /// How far back that reservation counts as recent. <b>Not
    /// <see cref="PostingAge.DailyWindowDays"/></b>, which stays the system-wide age definition
    /// every other filter answers to; this one merely defaults to it.
    /// </param>
    /// <remarks>
    /// <b>Four ints travelling as one named value rather than as four positional arguments.</b>
    /// <see cref="PipelineSettings"/> makes that argument about nine of them and it holds at four:
    /// these pass through two signatures that already carry a limit, a floor and a ceiling, every
    /// one of them a small non-negative int, so a transposed pair would compile, pass review and
    /// quietly reconfigure somebody's night. It is constructed in exactly one place, with every
    /// argument named.
    ///
    /// <b>Every member is a claim on the budget and no member could be a claim on the match.</b>
    /// Nothing on this type is passed to <c>MatchScorer</c> or <c>MatchRanker</c> - it says how
    /// many pairs are drawn, what a pair must score to be eligible for the draw, and how much of
    /// the draw is held for recent postings, and there is nowhere in it to put a number that would
    /// move a score. If a fifth member ever seems to belong here, that is the test it has to pass.
    /// </remarks>
    private readonly record struct SweepBudget(
        int Assessments, int MinScore, int RecentSharePercent, int RecentWindowDays);

    /// <summary>
    /// One candidate's settings, bounded by whatever the caller is entitled to impose on top of
    /// them.
    /// </summary>
    /// <remarks>
    /// <b>The caller's ceiling is a ceiling and never a floor.</b>
    /// <see cref="MaxAssessmentsPerRequest"/> is the gateway's ~230 seconds expressed as a row
    /// count, so it can only ever lower what the candidate configured. The consequence is
    /// deliberate: a candidate whose <see cref="PipelineSettings.AssessmentsPerNight"/> is zero
    /// has switched the judgement pass off, and the on-demand route must not be a way round
    /// somebody's own off switch - the same reasoning that keeps the daily send cap enforced in
    /// one place rather than at each call site.
    ///
    /// <b>An explicit floor from the caller replaces the setting rather than bounding it.</b> That
    /// route exists to draw a score band by hand for a stratified sample, so its
    /// <c>MinScore</c> is somebody deliberately asking about pairs the standing threshold refuses;
    /// taking the larger of the two would silently answer about a different band from the one
    /// requested, which is the one thing a sample cannot survive. Absent, this candidate's own
    /// <see cref="PipelineSettings.AssessmentThreshold"/> stands, so the route sweeps at the same
    /// floor the timer would.
    ///
    /// <b>Floored at zero here rather than validated, and the two are different jobs.</b>
    /// <see cref="PipelineSettingsValidation"/> owns the bounds and refuses a negative before it
    /// can be stored, with a message reaching the person who typed it; nothing revalidates a row
    /// on the way out, and this is an unattended pass at half past three in the morning, so a
    /// value that got past validation somehow has to read as "buy nothing" rather than throw. The
    /// other two members are left exactly as stored on purpose: a share above a hundred cannot
    /// reserve more than the shortlist, because the merge trims to the budget, and a window at or
    /// below zero goes through <see cref="PostingAge.Cutoff"/>, which clamps it and reads it as
    /// "since now" - so both degrade into the top-down draw rather than into an exception.
    /// </remarks>
    private static SweepBudget BudgetFor(
        PipelineSettings settings, int? assessmentCeiling, int? minScore)
    {
        var assessments = Math.Max(settings.AssessmentsPerNight, 0);

        return new SweepBudget(
            Assessments: assessmentCeiling is { } ceiling
                ? Math.Min(assessments, Math.Max(ceiling, 0))
                : assessments,
            MinScore: Math.Max(minScore ?? settings.AssessmentThreshold, 0),
            RecentSharePercent: settings.RecentSharePercent,
            RecentWindowDays: settings.RecentWindowDays);
    }

    /// <summary>
    /// Every profile's settings in one query, and <see cref="PipelineSettings.Default"/> for all of
    /// them where that query cannot be made at all.
    /// </summary>
    /// <remarks>
    /// <b>Missing settings degrade rather than fail, which is how every other dependency in this
    /// host behaves.</b> The sweep already has three degraded modes - no AI provider still scores,
    /// no embedder still ranks, no profile vector still writes matches - and this is the weakest
    /// dependency of the four, because the answer when it is absent is precisely the behaviour
    /// that shipped before the table existed. A candidate running on
    /// <see cref="PipelineSettings.Default"/> for one night is a candidate running on the
    /// constants this file used to hold.
    ///
    /// <b>The catch is not defensive programming; it is a state this repository is documented to
    /// deploy in.</b> <c>deploy.yml</c> skips migrations on an ordinary push, so a build that
    /// knows about a table reaches production before the table does - the same ordering hazard
    /// written down under "seed before reparsing", and one that has already cost a run here. A
    /// sweep that threw on it would take the whole night's scoring down to avoid running on the
    /// numbers it would have run on anyway, and it would do so at 03:30 with nobody watching.
    ///
    /// <b>Warning rather than error, and once per sweep rather than once per profile.</b> What is
    /// lost is configuration, not data: every pair is still scored, ranked and written, the
    /// judgement budget is still spent, and tomorrow's sweep reads the settings again from
    /// scratch.
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, PipelineSettings>> SettingsForAsync(
        IReadOnlyList<long> profileIds, CancellationToken ct)
    {
        if (pipelineSettings is null)
        {
            return Defaults(profileIds);
        }

        try
        {
            // Every requested id is present in the answer, mapped to the defaults where no row
            // exists - so the loop below never has to decide what a miss means, which is the
            // decision the repository exists to make once.
            return await pipelineSettings.GetForProfilesAsync(profileIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Match sweep: pipeline settings could not be read, so all {Profiles} profile(s) "
                + "run this sweep on the shipped defaults - which is what a candidate who has "
                + "configured nothing runs. Scoring and ranking are unaffected.",
                profileIds.Count);

            return Defaults(profileIds);
        }

        static Dictionary<long, PipelineSettings> Defaults(IReadOnlyList<long> ids)
            => ids.Distinct().ToDictionary(id => id, _ => PipelineSettings.Default);
    }

    private async Task<int> ScoreAsync(
        long profileId,
        IReadOnlyList<PostingFacts> postings,
        IReadOnlyDictionary<long, float[]> vectors,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var candidate = await BuildCandidateAsync(profileId, ct);

        if (candidate is null)
        {
            return 0;
        }

        var graph = ConceptGraph.Default;
        var scores = new List<(PostingFacts Posting, MatchResult Result)>(postings.Count);

        foreach (var posting in postings)
        {
            scores.Add((posting, MatchScorer.Score(candidate, posting, graph)));
        }

        // The score is the whole of the match; the rank is only the order it is read in. So the
        // ranking is computed after every pair has a score, over the whole pool at once - which
        // is what MatchRanker needs and what a per-pair call could never give it.
        var profileVector = await EnsureProfileVectorAsync(profileId, ct);

        var ranking = MatchRanker.Rank(
        [
            .. scores.Select(s => new RankInput(
                s.Posting.PostingId,
                s.Result.Score,
                Similarity(profileVector, s.Posting.PostingId))),
        ]);

        return await matches.UpsertScoresAsync(profileId, scores, ranking, now, ct);

        double? Similarity(float[]? profile, long postingId)
            => profile is not null && vectors.TryGetValue(postingId, out var posting)
                ? EmbeddingVector.Similarity(profile, posting)
                : null;
    }

    /// <summary>
    /// The profile's vector, embedding it first if the document has changed since the last one.
    /// </summary>
    /// <remarks>
    /// <b>Here rather than in the corpus pass, and it is one call.</b> The posting side is
    /// thousands of adverts and belongs to a bounded pass of its own; the profile side is a
    /// single document per candidate, needed by the ranking that is about to run, and cheap
    /// enough that making somebody wait a night for it would be the only cost worth mentioning.
    ///
    /// The staleness test is the profile's own <c>ExtractionInputHash</c>, which is a hash of
    /// <c>ToDocument()</c> - the exact text embedded here. So a save that edited a phone number
    /// costs nothing and one that rewrote a job description costs one call, which is the same
    /// bargain the extraction path already strikes.
    ///
    /// Null on every failure path, and the ranker drops the axis for it: no provider, no
    /// document, a profile never saved, or a call that did not come back. None of those is a
    /// reason to stop scoring.
    /// </remarks>
    private async Task<float[]?> EnsureProfileVectorAsync(long profileId, CancellationToken ct)
    {
        var identity = await db.CandidateProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => new { p.SubjectId, p.ExtractionInputHash })
            .FirstOrDefaultAsync(ct);

        if (identity?.ExtractionInputHash is not { } hash)
        {
            return null;
        }

        if (await embeddings.GetProfileVectorAsync(profileId, hash, ct) is { } current)
        {
            return current;
        }

        if (embedder is null || string.IsNullOrWhiteSpace(identity.SubjectId))
        {
            return null;
        }

        // Only reached when the vector is genuinely stale, so the profile graph is loaded on the
        // nights it is needed rather than on every night.
        var view = await profiles.GetAsync(identity.SubjectId, ct);

        if (view is null)
        {
            return null;
        }

        var text = EmbeddingText.ForProfile(view.Profile.ToDocument());

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var embedded = await embedder.EmbedAsync([text], ct);

        if (embedded.Count == 0 || embedded[0] is not { } vector)
        {
            logger.LogWarning(
                "Match sweep: could not embed profile {ProfileId}; its matches will rank on the "
                + "score alone. The AI ledger carries why.", profileId);
            return null;
        }

        await embeddings.UpsertProfileEmbeddingAsync(
            profileId, vector, hash, embedder.Deployment, time.GetUtcNow(), ct);

        return vector;
    }

    /// <summary>
    /// The candidate side of the match, or null where the profile holds no concepts at all.
    /// </summary>
    /// <remarks>
    /// A profile with no concepts scores zero against everything, so scoring it writes tens of
    /// thousands of rows saying nothing. Skipping it is not an optimisation - it is the
    /// difference between an empty matches page and an empty matches page that cost a full
    /// sweep to produce.
    /// </remarks>
    private async Task<CandidateFacts?> BuildCandidateAsync(long profileId, CancellationToken ct)
    {
        var assertions = await profiles.GetAssertionsAsync(profileId, ct);

        if (assertions.Count == 0)
        {
            logger.LogInformation("Match sweep: profile {ProfileId} holds no concepts; skipping.", profileId);
            return null;
        }

        var facts = await db.CandidateProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => new
            {
                p.Seniority,
                p.YearsExperience,
                p.PreferredArrangement,
                p.MaxDaysInOffice,
                p.MinimumSalary,
                p.SalaryCurrency,
                p.LocationCity,
                p.LocationCountry,
                p.WillingToRelocate,
            })
            .FirstOrDefaultAsync(ct);

        if (facts is null)
        {
            return null;
        }

        return new CandidateFacts
        {
            Concepts = assertions,
            Seniority = facts.Seniority,
            YearsExperience = facts.YearsExperience,
            PreferredArrangement = facts.PreferredArrangement,
            MaxDaysInOffice = facts.MaxDaysInOffice,
            MinimumSalary = facts.MinimumSalary,
            SalaryCurrency = facts.SalaryCurrency,
            LocationCity = facts.LocationCity,
            LocationCountry = facts.LocationCountry,
            WillingToRelocate = facts.WillingToRelocate,
        };
    }

    /// <summary>How many discarded posting ids to name in the warning before truncating.</summary>
    private const int DiscardedPostingsLogged = 20;

    /// <summary>
    /// The pairs this pass will spend the model on: the recent ones, the shortlist behind them,
    /// and a stratified sample.
    /// </summary>
    /// <remarks>
    /// <b>An explicit ceiling means the caller is already drawing a sample, so nothing is added.</b>
    /// That is the band-bounded HTTP route, which exists precisely to draw one by hand; stratifying
    /// a stratified draw would silently return rows from outside the band that was asked for, and
    /// reserving part of it for recent postings would do the same thing one dimension over.
    ///
    /// Otherwise the budget splits twice, and the two splits are independent: a reserved share for
    /// postings inside <see cref="SweepBudget.RecentWindowDays"/>, so a day's arrivals are judged
    /// on the day they arrive rather than behind a backlog, and a reserved share for the
    /// measurement sample. Both merges are in <see cref="StratifiedShortlist"/> rather than here,
    /// because interleaving with a deduplication is the part that is easy to get subtly wrong and
    /// it is only assertable exactly while it needs nothing but lists.
    ///
    /// <b>The reservation is a reservation and not an ordering, which is the whole design and the
    /// part a setting must not be allowed to erode.</b> The system exists to answer a day's
    /// postings on the day they appear: an application sent a week after the advert went up
    /// competes against a shortlist the employer has already drawn. Selecting top-down by score
    /// alone does not deliver that - pairs above the threshold accumulate, the highest are judged
    /// first whatever their age, and a corpus with a backlog spends every night on the backlog,
    /// which is exactly the state a first sweep over forty-five days of postings starts in.
    /// Ordering by age instead is the obvious thing to reach for and is worse: age says nothing
    /// about whether the candidate fits, and it is absorbing - once daily arrivals exceed the
    /// budget, nothing older is judged again. So the budget splits, both draws are ordered by
    /// score, and whatever the reservation cannot fill returns to the top-down draw over the whole
    /// corpus: a quiet day costs nothing and a backlog still drains at the remaining share a
    /// night. The window decides which rows are <i>eligible</i> for the reserved share and never
    /// which of them is best, so widening it promotes nothing and is not a way past the threshold.
    /// </remarks>
    private async Task<IReadOnlyList<CandidacyRequest>> BuildShortlistAsync(
        long profileId, SweepBudget budget, int? maxScore, DateTimeOffset now, CancellationToken ct)
    {
        var limit = budget.Assessments;
        var minScore = budget.MinScore;

        if (maxScore is not null)
        {
            return await matches.GetUnassessedAsync(profileId, minScore, limit, maxScore, ct: ct);
        }

        // Never more than a quarter of the budget, whatever the budget now is. At the default
        // forty the sample is unaffected - a quarter of it is exactly the ten this wants - and a
        // candidate who lowers their budget lowers both halves of it, which is the honest
        // arithmetic: the sample is a share of what a night buys and not a fixed tax on it. The
        // HTTP route's ten drops to two by the same rule, and that
        // matters: that route exists for somebody who has just filled in their profile and has
        // nothing to look at until tomorrow morning. Spending half of their one call on a
        // measurement sample would be taking the shortlist away from the only person it was for.
        var measurement = Math.Min(MeasurementAssessments, limit / 4);

        var shortlistBudget = limit - measurement;

        // The percent spelling of the two thirds this file used to hold as a numerator over a
        // denominator, and the arithmetic keeps the same shape: integer division, so it floors.
        // Behaviour-preserving rather than identical - `budget * 67 / 100` and `budget * 2 / 3`
        // agree for every shortlist budget from 0 to 99, and first disagree by a single row at a
        // hundred, which needs AssessmentsPerNight at 110 against the shipped forty. Quote it that
        // way round rather than claiming the two spellings are the same.
        var recentBudget = shortlistBudget * budget.RecentSharePercent / 100;

        // Ordered by score inside the window, exactly like the draw below it. The window decides
        // which rows are eligible for the reserved share; it never decides which of them is best.
        //
        // The window is this candidate's own and not PostingAge.DailyWindowDays, which stays
        // exactly where it is as the system-wide definition of a recent posting - the shortlist,
        // the corpus search and the apply queue all still answer to it. This one defaults to it
        // and is free to diverge afterwards, because "how far back may a posting be and still
        // have a claim on tonight's reserved share" is a different question from "what counts as
        // posted about twenty-four hours ago".
        var recent = recentBudget <= 0
            ? []
            : await matches.GetUnassessedAsync(
                profileId,
                minScore,
                recentBudget,
                maximumScore: null,
                PostingAge.Cutoff(now, budget.RecentWindowDays),
                ct);

        // The whole corpus, unbounded by date, and asked for the whole shortlist rather than for
        // what the recent draw left. It has to overlap: a recent posting is also one of the
        // highest-scoring unassessed pairs, and this draw not knowing that is what lets the merge
        // fill the shortlist to its budget on a day when nothing new arrived.
        var topDown = await matches.GetUnassessedAsync(
            profileId, minScore, shortlistBudget, maximumScore: null, postedSince: null, ct);

        // Recent first and the rest behind it, deduplicated. The merge is the same one the
        // measurement sample goes through, for the same reason: an interleave with a
        // deduplication in it is the part that is quietly wrong, and it is only assertable
        // exactly while it needs nothing but lists.
        var shortlist = StratifiedShortlist.Combine(
            recent, [topDown], shortlistBudget, r => r.PostingId);

        if (measurement <= 0)
        {
            return shortlist;
        }

        var bands = new List<IReadOnlyList<CandidacyRequest>>(MeasurementBands.Length);

        foreach (var (low, high) in MeasurementBands)
        {
            // A band entirely below the caller's floor has nothing to offer and is skipped
            // rather than queried, so the pass does not wake the database for an empty answer.
            if (high < minScore)
            {
                continue;
            }

            bands.Add(await matches.GetUnassessedAsync(
                profileId, Math.Max(low, minScore), MeasurementPerBand, high, ct: ct));
        }

        return StratifiedShortlist.Combine(shortlist, bands, limit, r => r.PostingId);
    }

    private async Task<AssessmentTally> AssessAsync(
        long profileId,
        SweepBudget budget,
        int? maxScore,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (assessor is null)
        {
            return AssessmentTally.Empty;
        }

        // Zero is a real setting and it is the off switch, so this profile is answered before any
        // query is issued for it. The scoring pass above has already run and written its matches -
        // what is switched off is the buying of verdicts, and nothing else.
        //
        // Answered before the unreachable count rather than after it, unlike the empty shortlist
        // below. That count exists to explain a night that drew nothing it wanted to judge; a
        // night that was told to judge nothing is already explained, and counting anyway would
        // spend a COUNT per profile on a database billed by wall-clock time to describe a draw
        // that was never going to happen.
        if (budget.Assessments <= 0)
        {
            return AssessmentTally.Empty;
        }

        // Bounded by this candidate's own budget, and by the caller's ceiling where there is one.
        // Anything left over stays unassessed and is picked up next time - the shortlist query
        // selects on exactly that, so a partial pass resumes rather than restarting.
        var shortlist = await BuildShortlistAsync(profileId, budget, maxScore, now, ct);

        // One count per profile per sweep, taken over the same eligible set the top-down draw
        // runs over, and taken here rather than inside the draw because it is a measurement of
        // the draw rather than a part of it. It costs one COUNT against a pass that has already
        // read tens of thousands of rows, and it is what makes the reachability rule's claim
        // checkable instead of remembered. Unbounded by the recent window on purpose: the window
        // is a reservation inside the budget, and this is asking how far the rule reaches.
        var unreachable = new AssessmentTally(
            0, 0, 0, await matches.CountUnreachableAsync(profileId, budget.MinScore, maxScore, ct: ct));

        // Before the empty check rather than after it, because the empty shortlist is the case
        // the count exists to explain: a night that drew nothing because everything left was
        // board-hosted looks exactly like a night with nothing left to judge, and those want
        // opposite responses.
        if (shortlist.Count == 0)
        {
            return unreachable;
        }

        // The assessor needs the whole profile, not the flattened facts: it reads the
        // candidate's prose, which is the half the scorer deliberately cannot see.
        var subjectId = await db.CandidateProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => p.SubjectId)
            .FirstOrDefaultAsync(ct);

        if (subjectId is null)
        {
            return unreachable;
        }

        var view = await profiles.GetAsync(subjectId, ct);

        if (view is null)
        {
            return unreachable;
        }

        var assessments = await assessor.AssessAsync(view.Profile, shortlist, ct);

        var written = new List<(long, CandidacyAssessment)>(shortlist.Count);
        var discarded = new List<long>();

        for (var i = 0; i < shortlist.Count; i++)
        {
            if (i < assessments.Count && assessments[i] is { } assessment)
            {
                written.Add((shortlist[i].PostingId, assessment));
            }
            else
            {
                discarded.Add(shortlist[i].PostingId);
            }
        }

        // What was paid for against what came back. The assessor drops an answer it cannot
        // correlate rather than guessing which posting it belongs to, which is right - but it
        // throws nothing, so without this the loss is indistinguishable from a quiet night.
        // Measured on 2026-08-28: 90 pairs sent, 50 discarded, and the sweep reported success.
        //
        // Warning rather than error: the pairs stay unassessed and the next sweep picks them up,
        // so this is money and latency lost, not data. The ids are named because "some calls
        // failed" is not something anybody can act on.
        if (discarded.Count > 0)
        {
            logger.LogWarning(
                "Candidacy assessment for profile {ProfileId}: {Requested} requested, {Returned} "
                + "usable, {Discarded} discarded. Affected postings: {Postings}.",
                profileId,
                shortlist.Count,
                written.Count,
                discarded.Count,
                string.Join(", ", discarded.Take(DiscardedPostingsLogged))
                    + (discarded.Count > DiscardedPostingsLogged ? ", ..." : string.Empty));
        }

        var persisted = await matches.ApplyAssessmentsAsync(profileId, written, time.GetUtcNow(), ct);

        return new AssessmentTally(
            shortlist.Count, written.Count, persisted, unreachable.Unreachable);
    }

    /// <summary>
    /// What one profile's assessment pass asked for against what survived it.
    /// </summary>
    /// <param name="Requested">Pairs sent to the model. This is what the run cost.</param>
    /// <param name="Returned">Answers the assessor could correlate back to a posting.</param>
    /// <param name="Persisted">Rows actually written, which a no-op update can make smaller.</param>
    /// <param name="Unreachable">
    /// Pairs above the threshold and unassessed that the reachability rule kept out of the draw.
    /// </param>
    /// <remarks>
    /// <b><see cref="Unreachable"/> is not part of the same arithmetic as the other three and must
    /// not be folded into them.</b> Those three describe one call and each is a subset of the one
    /// before it; this one counts rows that were never in the call at all, so
    /// <c>Requested + Unreachable</c> is not a number that means anything - the draw is bounded,
    /// and an excluded pair is usually replaced by the next-best one rather than leaving a gap.
    /// It rides along here only because it is measured per profile and reported per sweep, which
    /// is exactly what this type is for.
    /// </remarks>
    private readonly record struct AssessmentTally(
        int Requested, int Returned, int Persisted, int Unreachable)
    {
        public int Discarded => Requested - Returned;

        public static AssessmentTally Empty => new(0, 0, 0, 0);

        public static AssessmentTally operator +(AssessmentTally left, AssessmentTally right)
            => new(
                left.Requested + right.Requested,
                left.Returned + right.Returned,
                left.Persisted + right.Persisted,
                left.Unreachable + right.Unreachable);
    }

    /// <param name="Profiles">How many profiles the sweep considered.</param>
    /// <param name="Scored">Rows whose score actually moved. Unchanged pairs are not rewritten.</param>
    /// <param name="Assessed">Pairs the model judged this run.</param>
    /// <param name="Requested">
    /// Pairs sent to the model, which is what the run cost.
    /// </param>
    /// <param name="Discarded">
    /// Pairs paid for whose answer could not be correlated back to a posting.
    /// </param>
    /// <param name="Unreachable">
    /// Pairs the reachability rule held out of the draw: above the threshold, unassessed, and on a
    /// posting whose board hosts the application with no employer board to recover a link from.
    /// </param>
    /// <remarks>
    /// <see cref="Requested"/> and <see cref="Discarded"/> are reported rather than inferred
    /// because a caller cannot derive them: a sweep that assessed forty looks identical whether
    /// it asked for forty or for ninety. On 2026-08-28 it was ninety.
    ///
    /// <see cref="Unreachable"/> is here for the same reason one step further out. It is a claim
    /// on the budget and never on the match, so it moves no score, no rank and no verdict and
    /// therefore shows up nowhere else - which is exactly what makes an unmeasured version of it
    /// so easy to believe. <b>It is the pool the rule reached and not the judgements it saved</b>,
    /// and those differ whenever the eligible pool is larger than the budget, which is most
    /// nights. Measured on 2026-09-09 the pool is 681 postings corpus-wide; the ~2,600 the same
    /// rule will reach once LinkedIn's route-unknown backlog reclassifies is a projection and must
    /// not be quoted as a saving.
    /// </remarks>
    public sealed record SweepSummary(
        int Profiles, int Scored, int Assessed, int Requested, int Discarded, int Unreachable);
}
