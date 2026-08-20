using System.ComponentModel.DataAnnotations;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Matching;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Matching;

/// <summary>
/// CV-to-posting matching.
/// </summary>
/// <remarks>
/// The only write-shaped endpoints in the API, and the only ones that can cost money per
/// call. Both consequences are visible in the wiring: they require a real principal
/// regardless of <c>Api:AllowAnonymousReads</c>, they sit in their own small rate-limit
/// bucket, and they are never output-cached - a cached match would be served to a second
/// caller who submitted a different CV.
/// </remarks>
public sealed class MatchEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/match")
            .WithTags("Matching")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.MatchPolicy);

        group.MapPost("/", MatchAsync)
            .WithName("MatchCv")
            .WithSummary("Rank stored postings against a CV.");

        group.MapPost("/profile", ProfileAsync)
            .WithName("ExtractCvProfile")
            .WithSummary("Extract the structured profile a match would be run from.");
    }

    private static async Task<IResult> MatchAsync(
        [FromBody] MatchRequest request,
        [FromServices] CvMatchingService matching,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CvText))
        {
            return TypedResults.Problem(
                detail: "cvText is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await matching.MatchAsync(
            request.CvText,
            new MatchCandidateQuery
            {
                SearchTerm = request.SearchTerm,
                IsRemote = request.Remote,
                Site = request.Site,
                Country = request.Country,
                PostedFrom = request.PostedFrom,
            },
            request.TopN,
            ct);

        return TypedResults.Ok(new MatchResponse
        {
            Matches = outcome.Matches,
            Profile = ToResponse(outcome.Profile),
            Provider = outcome.Provenance.Provider,
            CandidatesConsidered = outcome.Provenance.CandidatesConsidered,
            DegradedToFallback = outcome.Provenance.DegradedToFallback,
            DegradationReason = outcome.Provenance.DegradationReason,
        });
    }

    private static IResult ProfileAsync(
        [FromBody] ProfileRequest request,
        [FromServices] CvMatchingService matching)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CvText))
        {
            return TypedResults.Problem(
                detail: "cvText is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return TypedResults.Ok(ToResponse(matching.ExtractProfile(request.CvText)));
    }

    /// <summary>
    /// Maps the profile without its raw text.
    /// </summary>
    /// <remarks>
    /// A CV is personal data the caller already has; echoing it back adds nothing and would
    /// put it into logs, traces and any client-side cache the response passes through.
    /// </remarks>
    private static CvProfileResponse ToResponse(CvProfile profile) => new()
    {
        Skills = profile.Skills,
        Titles = profile.Titles,
        Locations = profile.Locations,
        YearsExperience = profile.YearsExperience,
        PrefersRemote = profile.PrefersRemote,
        TokenCount = profile.Tokens.Count,
    };
}

public sealed record MatchRequest
{
    [Required]
    public string CvText { get; init; } = string.Empty;

    public string? SearchTerm { get; init; }
    public bool? Remote { get; init; }
    public string? Site { get; init; }
    public string? Country { get; init; }
    public DateOnly? PostedFrom { get; init; }

    /// <summary>How many matches to return. Clamped by <c>Matching:MaxTopN</c>.</summary>
    public int? TopN { get; init; }
}

public sealed record ProfileRequest
{
    [Required]
    public string CvText { get; init; } = string.Empty;
}

public sealed record CvProfileResponse
{
    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<string> Titles { get; init; } = [];
    public IReadOnlyList<string> Locations { get; init; } = [];
    public double? YearsExperience { get; init; }
    public bool? PrefersRemote { get; init; }
    public int TokenCount { get; init; }
}

public sealed record MatchResponse
{
    public required IReadOnlyList<PostingMatch> Matches { get; init; }
    public required CvProfileResponse Profile { get; init; }

    /// <summary>Which ranker produced this, so a caller never has to guess.</summary>
    public required string Provider { get; init; }

    public required int CandidatesConsidered { get; init; }

    /// <summary>True when the configured ranker failed and keyword order was returned.</summary>
    public required bool DegradedToFallback { get; init; }

    public string? DegradationReason { get; init; }
}
