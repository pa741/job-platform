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
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        CancellationToken ct,
        int minScore = 0,
        bool assessedOnly = false,
        int limit = 25,
        int offset = 0)
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
            ct);

        return TypedResults.Ok(new { items = rows.Select(ToSummary).ToList(), offset });
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
            ScoredAtUtc = row.ScoredAtUtc,
            AssessedAtUtc = row.AssessedAtUtc,
        };

    private static string Label(ConceptGraph graph, string key)
        => graph.TryGet(key, out var concept) ? concept.Label : key;
}
