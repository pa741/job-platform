using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Matches;

/// <summary>
/// The caller's own matches.
/// </summary>
/// <remarks>
/// Read-only. Nothing here scores anything or calls a model: the arithmetic runs in the nightly
/// sweep and the judgement runs behind it, so by the time a candidate opens this page the work
/// is done and this is a query. That is the whole reason the sweep exists on a timer rather
/// than being triggered by the page - a shortlist that costs model calls to look at is one
/// nobody can afford to browse.
///
/// Authenticated unconditionally, like the profile, and scoped to the caller's own profile id
/// resolved from their token. The posting data returned is public, but which postings a
/// particular person matches, and by how much, is not.
/// </remarks>
public sealed class MatchEndpoints : IEndpointGroup
{
    /// <summary>Hard ceiling regardless of what a caller asks for. Mirrors the posting search.</summary>
    private const int MaxLimit = 100;

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/matches")
            .WithTags("Matches")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListMatches")
            .WithSummary("The calling principal's scored matches, best first.");

        group.MapGet("/{postingId:long}", GetAsync)
            .WithName("GetMatch")
            .WithSummary("One match in full, including the breakdown behind the score.");

        group.MapPut("/{postingId:long}/dismissed", SetDismissedAsync)
            .WithName("SetMatchDismissed")
            .WithSummary("Marks a match as not interesting, or takes that back.");

        group.MapGet("/skill-gap", SkillGapAsync)
            .WithName("GetSkillGap")
            .WithSummary("What the candidate's matched band asks for that their profile lacks.");
    }

    /// <summary>Default score floor for the gap. The band worth taking advice from.</summary>
    private const int GapMinimumScore = 40;

    /// <summary>How many gaps to return. A list nobody scrolls is a list nobody acts on.</summary>
    private const int GapLimit = 12;

    /// <summary>
    /// The join, run backwards: what this candidate's matched band asks for and their profile
    /// does not hold.
    /// </summary>
    /// <remarks>
    /// <b>Reads Azure SQL to answer an aggregate question</b>, which the architecture otherwise
    /// reserves for Cosmos. Allowed on the terms <c>GetSourceCompositionAsync</c> set, with one
    /// difference that matters: this is per-principal, so it carries no output cache and the
    /// usual mitigation is unavailable. It is bounded instead - the expensive half is scoped to
    /// one profile and one score floor so it lands on the <c>(ProfileId, Score)</c> index, and
    /// the corpus figures are looked up only for the concepts that band already names rather
    /// than aggregated over the whole vocabulary.
    ///
    /// <para>
    /// It must therefore never be on a bootstrap or polling path. It is loaded when the market
    /// page renders and not before.
    /// </para>
    /// </remarks>
    private static async Task<IResult> SkillGapAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        [FromServices] JobPostingQueryRepository postings,
        CancellationToken ct,
        string? searchTerm = null,
        int minScore = GapMinimumScore)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.Problem(
                detail: "No profile exists for this principal, so nothing has been matched yet.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var inBand = await matches.GetInBandConceptDemandAsync(
            profileId.Value, Math.Clamp(minScore, 0, 100), ct);

        // The keys the band names bound the corpus query. Without that this is the 222-row
        // aggregate over the whole assertion table that GetConceptDemandAsync exists to refuse.
        var corpus = inBand.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await postings.GetConceptDemandAsync([.. inBand.Keys], searchTerm, ct);

        var held = await profiles.GetAssertionsAsync(profileId.Value, ct);

        var gaps = SkillGapAnalysis.Compute(
            inBand,
            corpus,
            [.. held.Select(a => a.ConceptKey).Distinct(StringComparer.Ordinal)],
            ConceptGraph.Default,
            GapLimit);

        return TypedResults.Ok(new SkillGapResponse
        {
            MinScore = Math.Clamp(minScore, 0, 100),
            SearchTerm = searchTerm,
            Items = [.. gaps.Select(ToGapResponse)],
        });
    }

    private static SkillGapItem ToGapResponse(SkillGap gap)
    {
        var label = ConceptGraph.Default.TryGet(gap.ConceptKey, out var concept)
            ? concept.Label
            : gap.ConceptKey;

        string? heldLabel = null;

        if (gap.HeldKey is { } heldKey)
        {
            heldLabel = ConceptGraph.Default.TryGet(heldKey, out var heldConcept)
                ? heldConcept.Label
                : heldKey;
        }

        return new SkillGapItem
        {
            Concept = gap.ConceptKey,
            Label = label,
            Kind = concept.Kind.ToString(),
            MatchPostings = gap.MatchPostings,
            CorpusPostings = gap.CorpusPostings,
            Held = gap.HeldKey,
            HeldLabel = heldLabel,
            Relation = gap.Relation?.ToString(),
            Credit = gap.Credit,
        };
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        CancellationToken ct,
        int minScore = 0,
        bool assessedOnly = false,
        int limit = 25,
        int offset = 0,
        bool dismissed = false)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        if (offset < 0)
        {
            return TypedResults.Problem(
                detail: "offset must not be negative.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        // No profile means no matches, and saying so plainly beats an empty list: the client
        // needs to send the person to the form rather than tell them nothing matched.
        if (profileId is null)
        {
            return TypedResults.Problem(
                detail: "No profile exists for this principal, so nothing has been matched yet.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var rows = await matches.ListAsync(
            profileId.Value,
            Math.Clamp(minScore, 0, 100),
            assessedOnly,
            Math.Clamp(limit, 1, MaxLimit),
            offset,
            dismissed,
            ct);

        return TypedResults.Ok(new { items = rows.Select(ToSummary).ToList(), offset });
    }

    /// <summary>
    /// Records that this candidate is not interested in a posting, or takes it back.
    /// </summary>
    /// <remarks>
    /// The only write on this group, and the one that keeps the shortlist a worklist. Without
    /// it every role the candidate has already rejected is back at the top tomorrow, and the
    /// nightly budget keeps spending judgements on postings they have said no to.
    ///
    /// <para>
    /// A PUT rather than a POST because it sets a state rather than appending to a log, and
    /// because it has to be safe to repeat: a client retrying a dismissal it is unsure landed
    /// must not get a different answer the second time.
    /// </para>
    /// </remarks>
    private static async Task<IResult> SetDismissedAsync(
        ClaimsPrincipal user,
        long postingId,
        [FromBody] SetDismissedRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var when = request.Dismissed ? clock.GetUtcNow() : (DateTimeOffset?)null;
        var found = await matches.SetDismissedAsync(profileId.Value, postingId, when, ct);

        return found
            ? TypedResults.Ok(new SetDismissedResponse { PostingId = postingId, DismissedAtUtc = when })
            : TypedResults.NotFound();
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user,
        long postingId,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var row = await matches.GetDetailAsync(profileId.Value, postingId, ct);

        return row is null ? TypedResults.NotFound() : TypedResults.Ok(ToDetail(row));
    }

    private static MatchSummary ToSummary(MatchRow row)
        => Fill(new MatchSummary
        {
            PostingId = row.PostingId,
            Title = row.Title,
            Score = row.Score,
        }, row);

    private static MatchDetail ToDetail(MatchRow row)
    {
        var graph = ConceptGraph.Default;

        var detail = new MatchDetail
        {
            PostingId = row.PostingId,
            Title = row.Title,
            Score = row.Score,
            HasApplication = row.HasApplication,
            Components = row.Read<MatchComponent>(row.ComponentsJson)
                .Select(c => new MatchComponentResponse(c.Name, c.Score, c.Weight))
                .ToList(),
            Matched = row.Read<ConceptMatch>(row.MatchedJson)
                .Select(m => new ConceptMatchResponse(
                    m.RequiredKey,
                    Label(graph, m.RequiredKey),
                    m.HeldKey,
                    Label(graph, m.HeldKey),
                    m.Relation.ToString(),
                    m.Credit,
                    m.Demand.ToString()))
                .ToList(),
            Gaps = row.Read<ConceptGap>(row.GapsJson)
                .Select(g => new ConceptGapResponse(
                    g.RequiredKey,
                    Label(graph, g.RequiredKey),
                    g.Demand.ToString(),
                    g.YearsMin))
                .ToList(),
            Strengths = row.Read<string>(row.StrengthsJson),
            AssessmentGaps = row.Read<string>(row.AssessmentGapsJson),
            Emphasise = row.Read<string>(row.EmphasiseJson),
        };

        return (MatchDetail)Fill(detail, row);
    }

    /// <summary>
    /// The fields the summary and the detail share.
    /// </summary>
    /// <remarks>
    /// A <c>with</c> expression on the base record, so <see cref="MatchDetail"/> keeps the
    /// properties it set before this runs. Writing the shared fields twice is how the two
    /// responses drift apart.
    /// </remarks>
    private static MatchSummary Fill(MatchSummary summary, MatchRow row)
        => summary with
        {
            // Recomputed from the components rather than stored: it is a pure function of
            // them, so a column would be a second copy that could drift from the first.
            Coverage = Math.Clamp(row.Read<MatchComponent>(row.ComponentsJson).Sum(c => c.Weight), 0, 1),
            Company = row.Company,
            Location = row.Location,
            AnnualSalaryMin = row.AnnualSalaryMin,
            AnnualSalaryMax = row.AnnualSalaryMax,
            AnnualSalaryCurrency = row.AnnualSalaryCurrency,
            WorkArrangement = row.WorkArrangement.ToString(),
            Seniority = row.Seniority.ToString(),
            DatePosted = row.DatePosted,
            RequiredGapCount = row.RequiredGapCount,

            // Null rather than "Unknown" where the sweep has not been here. A client has to be
            // able to tell "the model has not looked at this yet" from "the model looked and
            // could not say", and a default enum name collapses the two.
            Verdict = row.Verdict?.ToString(),
            AssessmentScore = row.AssessmentScore,
            Rationale = row.Rationale,
            Similarity = row.Similarity,
            RankScore = row.RankScore,
            ScoredAtUtc = row.ScoredAtUtc,
            AssessedAtUtc = row.AssessedAtUtc,
            DismissedAtUtc = row.DismissedAtUtc,
        };

    private static string Label(ConceptGraph graph, string key)
        => graph.TryGet(key, out var concept) ? concept.Label : key;
}
