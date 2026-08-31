using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Realtime;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Realtime;

/// <summary>What a browser needs to open the feed. Public URL, private token.</summary>
public sealed record RealtimeNegotiation(string Url, string AccessToken);

/// <summary>
/// Where a dashboard asks for permission to listen.
/// </summary>
/// <remarks>
/// <b>On the API rather than the Function app, and that is the whole design decision here.</b>
/// The obvious route is the Functions SignalR input binding, which is what every serverless
/// sample uses - but a Function endpoint is protected by a function key, and a browser holding a
/// function key holds a credential that also opens the reprocess, backfill and sweep routes. The
/// dashboard already carries an Entra token for this API, so negotiating here costs nothing and
/// reuses an authorisation boundary that exists.
///
/// <b>Behind <c>AuthenticatedPolicy</c>, not <c>PublicReadPolicy</c>.</b> Every other read here
/// can be opened to anonymous by <c>Api:AllowAnonymousReads</c> so a frontend can be built
/// against real data. This one must not be: it mints a token against a service the deployment
/// pays for, and an anonymous negotiate is an open invitation to exhaust a free tier capped at
/// twenty connections. The prompt-replay route is fenced the same way and for a related reason.
/// </remarks>
public sealed class RealtimeEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGroup("/realtime")
            .WithTags("Realtime")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy)
            .MapPost("/negotiate", NegotiateAsync)
            .WithName("NegotiateRealtime")
            .WithSummary("Mints this client's access to the live feed. Short-lived, per user.");
    }

    private static async Task<IResult> NegotiateAsync(
        ClaimsPrincipal user,
        [FromServices] IRealtimeFeed? feed,
        CancellationToken ct)
    {
        // 503 rather than 500 or an empty 200. The deployment may genuinely have no realtime
        // service - the feed is optional by design - and a client has to be able to tell "not
        // configured here" from "broken just now", because the first means stop asking and the
        // second means try later. Both fall back to polling; only one should retry.
        if (feed is null)
        {
            return Results.Problem(
                title: "The realtime feed is not configured for this deployment.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // The stable directory object id, never ClaimTypes.NameIdentifier - that resolves to
        // `sub`, which is pairwise per application, so a second app registration would hand the
        // same person a different identity on the feed than on their profile.
        var subjectId = user.SubjectId();

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            return Results.Unauthorized();
        }

        var access = await feed.NegotiateAsync(subjectId, ct);

        return access is null
            ? Results.Problem(
                title: "The realtime feed could not be reached.",
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new RealtimeNegotiation(access.Url, access.AccessToken));
    }
}
