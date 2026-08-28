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
    TimeProvider time,
    ILogger<MatchSweepFunction> logger,
    ICandidacyAssessor? assessor = null)
{
    /// <summary>
    /// How far back the scoring pass looks.
    /// </summary>
    /// <remarks>
    /// Bounded by recency rather than by relevance. A cheaper pre-filter - say, postings sharing
    /// one required skill - would need the score this pass exists to compute, and would drop
    /// exactly the roles a candidate is qualified for by a route the filter cannot see.
    /// Recency biases nothing about the match itself.
    /// </remarks>
    private const int LookbackDays = 45;

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
    private const int AssessmentThreshold = 45;

    /// <summary>Ceiling on how many pairs one sweep sends to the model, per profile.</summary>
    /// <remarks>
    /// Briefly 90, for the run on 2026-08-28, to build an assessed set worth measuring a
    /// verdict-aware ranking against. Back to 40, and the run is worth recording: 90 pairs went
    /// out in nine batches of ten, four came back usable and five were discarded whole. 40 of 90
    /// written, and nothing failed - see HANDOFF.md 4.2.
    /// </remarks>
    private const int MaxAssessments = 40;

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
        => await SweepAsync(profileId: null, MaxAssessments, ct);

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
        var summary = await SweepAsync(body?.ProfileId, MaxAssessmentsPerRequest, ct);

        return new OkObjectResult(summary);
    }

    /// <param name="ProfileId">Restrict to one profile. Null sweeps every profile.</param>
    public sealed record SweepRequest(long? ProfileId);

    private async Task<SweepSummary> SweepAsync(long? profileId, int assessmentLimit, CancellationToken ct)
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

        logger.LogInformation(
            "Match sweep: {Profiles} profile(s) against {Postings} posting(s) seen since {Since:yyyy-MM-dd}.",
            profileIds.Count, postings.Count, since);

        var scored = 0;
        var assessed = AssessmentTally.Empty;

        foreach (var id in profileIds)
        {
            scored += await ScoreAsync(id, postings, now, ct);
            assessed += await AssessAsync(id, assessmentLimit, ct);
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
        long profileId, IReadOnlyList<PostingFacts> postings, DateTimeOffset now, CancellationToken ct)
    {
        var candidate = await BuildCandidateAsync(profileId, ct);

        if (candidate is null)
        {
            return 0;
        }

        var graph = ConceptGraph.Default;
        var scores = new List<(PostingFacts, MatchResult)>(postings.Count);

        foreach (var posting in postings)
        {
            scores.Add((posting, MatchScorer.Score(candidate, posting, graph)));
        }

        return await matches.UpsertScoresAsync(profileId, scores, now, ct);
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

    private async Task<AssessmentTally> AssessAsync(long profileId, int assessmentLimit, CancellationToken ct)
    {
        if (assessor is null)
        {
            return AssessmentTally.Empty;
        }

        // Bounded by the caller's budget rather than by the nightly ceiling. Anything left
        // over stays unassessed and is picked up next time - the shortlist query selects on
        // exactly that, so a partial pass resumes rather than restarting.
        var shortlist = await matches.GetUnassessedAsync(
            profileId, AssessmentThreshold, assessmentLimit, ct);

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
