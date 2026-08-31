using JobPlatform.Core.Ai;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
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
/// </remarks>
public sealed class MatchSweepFunction(
    JobsDbContext db,
    CandidateProfileRepository profiles,
    JobMatchRepository matches,
    EmbeddingRepository embeddings,
    TimeProvider time,
    ILogger<MatchSweepFunction> logger,
    ICandidacyAssessor? assessor = null,
    ITextEmbedder? embedder = null)
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
    /// The score a match must clear before the model is asked about it.
    /// </summary>
    /// <remarks>
    /// The single most important number in this file, because it is the one that decides what
    /// gets paid for. Deliberately not high: the arithmetic under-scores a candidate whose
    /// relevant experience is in prose the extractor read cautiously, and the model exists
    /// precisely to catch that. A threshold tuned to look efficient would filter out the cases
    /// worth judging.
    /// </remarks>
    /// <remarks>
    /// <b>Not <c>MatchRanker.FusionFloor</c>, and tying it to that was a mistake.</b> The two
    /// were briefly one constant on the reasoning that "the band the model is spent on and the
    /// band the embedding re-orders are the same band". They are not the same question. This one
    /// asks where a judgement is worth buying, and the answer is "wherever the arithmetic might
    /// be wrong", which is low by design. The other asks where the embedding carries signal, and
    /// the holdout put that at 80 - so coupling them would have silently stopped the model from
    /// ever looking at anything below 80, and with it the only source of labels that can show
    /// whether the score works down there.
    ///
    /// Two constants that happened to share a value are not one constant.
    /// </remarks>
    private const int AssessmentThreshold = 45;

    /// <summary>Ceiling on how many pairs one sweep sends to the model, per profile.</summary>
    /// <remarks>
    /// Briefly 90, for the run on 2026-08-28, to build an assessed set worth measuring a
    /// verdict-aware ranking against. Back to 40, and the run is worth recording: 90 pairs went
    /// out in nine batches of ten, four came back usable and five were discarded whole. 40 of 90
    /// written, and nothing failed - see HANDOFF.md 4.2.
    ///
    /// <see cref="MeasurementAssessments"/> of these are spent on the sample rather than on the
    /// shortlist, so the total cost of a night has not moved.
    /// </remarks>
    private const int MaxAssessments = 40;

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
    /// The cost that is real is the shortlist losing ten of its forty. That is affordable because
    /// the shortlist is not a queue that empties - it is the top of a ranking that is re-drawn
    /// nightly, so a row not judged tonight is judged tomorrow unless something better arrives,
    /// in which case judging the better one first was correct.
    /// </remarks>
    private const int MeasurementAssessments = 10;

    /// <summary>
    /// The bands the measurement sample is drawn from, below the shortlist's usual reach.
    /// </summary>
    /// <remarks>
    /// Stops at 89 deliberately: the top band is what the shortlist already covers every night, so
    /// spending measurement budget there buys a fourth copy of the only evidence the system has.
    /// The floor matches <see cref="AssessmentThreshold"/> - below it no pair is a candidate for
    /// judgement at all, so a band down there would return nothing and quietly waste its slot.
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
    /// </remarks>
    private const int MaxAssessmentsPerRequest = 10;

    [Function(nameof(MatchSweepFunction))]
    public async Task RunAsync(
        // 03:30 UTC: after the NAS scrape has uploaded and the ingest and extraction queues
        // have drained, and before anybody in the UK opens the dashboard.
        [TimerTrigger("0 30 3 * * *")] TimerInfo timer,
        CancellationToken ct)
        => await SweepAsync(profileId: null, MaxAssessments, AssessmentThreshold, maxScore: null, ct);

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
            MaxAssessmentsPerRequest,
            Math.Max(body?.MinScore ?? AssessmentThreshold, 0),
            body?.MaxScore,
            ct);

        return new OkObjectResult(summary);
    }

    /// <param name="ProfileId">Restrict to one profile. Null sweeps every profile.</param>
    /// <param name="MinScore">
    /// Floor on which pairs the model may be spent on. Defaults to the standing threshold.
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

    private async Task<SweepSummary> SweepAsync(
        long? profileId, int assessmentLimit, int minScore, int? maxScore, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now.AddDays(-LookbackDays);

        var profileIds = profileId is { } single
            ? [single]
            : await matches.GetProfileIdsAsync(ct);

        if (profileIds.Count == 0)
        {
            logger.LogInformation("Match sweep: no profiles to score.");
            return new SweepSummary(0, 0, 0, 0, 0);
        }

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
            scored += await ScoreAsync(id, postings, vectors, now, ct);
            assessed += await AssessAsync(id, assessmentLimit, minScore, maxScore, ct);
        }

        // Requested is reported beside written, always, because the two diverging is the whole
        // signal and a written count on its own cannot show it.
        logger.LogInformation(
            "Match sweep complete: {Scored} score(s) written, {Assessed} assessment(s) written "
            + "of {Requested} requested ({Discarded} discarded).",
            scored, assessed.Persisted, assessed.Requested, assessed.Discarded);

        return new SweepSummary(
            profileIds.Count, scored, assessed.Persisted, assessed.Requested, assessed.Discarded);
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
    /// The pairs this pass will spend the model on: the shortlist, plus a stratified sample.
    /// </summary>
    /// <remarks>
    /// <b>An explicit ceiling means the caller is already drawing a sample, so nothing is added.</b>
    /// That is the band-bounded HTTP route, which exists precisely to draw one by hand; stratifying
    /// a stratified draw would silently return rows from outside the band that was asked for.
    ///
    /// Otherwise the budget splits. The merge is in <see cref="StratifiedShortlist"/> rather than
    /// here, because interleaving with a deduplication is the part that is easy to get subtly
    /// wrong and it is only assertable exactly while it needs nothing but lists.
    /// </remarks>
    private async Task<IReadOnlyList<CandidacyRequest>> BuildShortlistAsync(
        long profileId, int limit, int minScore, int? maxScore, CancellationToken ct)
    {
        if (maxScore is not null)
        {
            return await matches.GetUnassessedAsync(profileId, minScore, limit, maxScore, ct);
        }

        // Never more than a quarter of the budget. The nightly forty is unaffected - a quarter of
        // it is exactly the ten this wants - but the HTTP route's ten drops to two, and that
        // matters: that route exists for somebody who has just filled in their profile and has
        // nothing to look at until tomorrow morning. Spending half of their one call on a
        // measurement sample would be taking the shortlist away from the only person it was for.
        var measurement = Math.Min(MeasurementAssessments, limit / 4);

        var topDown = await matches.GetUnassessedAsync(
            profileId, minScore, limit - measurement, null, ct);

        if (measurement <= 0)
        {
            return topDown;
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
                profileId, Math.Max(low, minScore), MeasurementPerBand, high, ct));
        }

        return StratifiedShortlist.Combine(topDown, bands, limit, r => r.PostingId);
    }

    private async Task<AssessmentTally> AssessAsync(
        long profileId, int assessmentLimit, int minScore, int? maxScore, CancellationToken ct)
    {
        if (assessor is null)
        {
            return AssessmentTally.Empty;
        }

        // Bounded by the caller's budget rather than by the nightly ceiling. Anything left
        // over stays unassessed and is picked up next time - the shortlist query selects on
        // exactly that, so a partial pass resumes rather than restarting.
        var shortlist = await BuildShortlistAsync(profileId, assessmentLimit, minScore, maxScore, ct);

        if (shortlist.Count == 0)
        {
            return AssessmentTally.Empty;
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
            return AssessmentTally.Empty;
        }

        var view = await profiles.GetAsync(subjectId, ct);

        if (view is null)
        {
            return AssessmentTally.Empty;
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

        return new AssessmentTally(shortlist.Count, written.Count, persisted);
    }

    /// <summary>
    /// What one profile's assessment pass asked for against what survived it.
    /// </summary>
    /// <param name="Requested">Pairs sent to the model. This is what the run cost.</param>
    /// <param name="Returned">Answers the assessor could correlate back to a posting.</param>
    /// <param name="Persisted">Rows actually written, which a no-op update can make smaller.</param>
    private readonly record struct AssessmentTally(int Requested, int Returned, int Persisted)
    {
        public int Discarded => Requested - Returned;

        public static AssessmentTally Empty => new(0, 0, 0);

        public static AssessmentTally operator +(AssessmentTally left, AssessmentTally right)
            => new(
                left.Requested + right.Requested,
                left.Returned + right.Returned,
                left.Persisted + right.Persisted);
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
    /// <remarks>
    /// <see cref="Requested"/> and <see cref="Discarded"/> are reported rather than inferred
    /// because a caller cannot derive them: a sweep that assessed forty looks identical whether
    /// it asked for forty or for ninety. On 2026-08-28 it was ninety.
    /// </remarks>
    public sealed record SweepSummary(
        int Profiles, int Scored, int Assessed, int Requested, int Discarded);
}
