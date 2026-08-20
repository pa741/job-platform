using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;

namespace JobPlatform.Api.Features.Meta;

/// <summary>
/// Health probes and the caller's own identity.
/// </summary>
/// <remarks>
/// Outside the versioned surface deliberately: Container Apps addresses the probe by fixed
/// path, and a version bump must not silently move it and start failing deployments.
/// </remarks>
public sealed class MetaEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Liveness. Dependency-free on purpose: this answers "is the process up", and a
        // probe that consults a database reports a dependency outage as an application
        // crash, which makes the platform restart a perfectly healthy container.
        routes.MapGet("/health", () => TypedResults.Ok(new { status = "ok" }))
            .WithTags("Meta")
            .WithName("Health")
            .WithSummary("Liveness. Touches nothing.")
            .AllowAnonymous()
            .ExcludeFromDescription();

        // Readiness checks Cosmos and *never* SQL. Azure SQL here is serverless, billed by
        // wall-clock time online, and auto-pauses after an hour idle; a probe polling it
        // every few seconds would hold it awake permanently and spend the entire monthly
        // grant on health checks alone, taking the database offline until the 1st of the
        // following month. Cosmos is always on, so pinging it costs a trivial number of RUs.
        routes.MapHealthChecks("/health/ready")
            .WithTags("Meta")
            .AllowAnonymous()
            .ExcludeFromDescription();

        // Claims are read through both their short names and their mapped URIs. The JWT
        // handler rewrites several short names to legacy SOAP-era URIs, so `scp` and `name`
        // are frequently not present under the names the token actually carried - reading
        // only the short form silently returns nulls and an empty scope list.
        routes.MapGet("/api/v1/me", (ClaimsPrincipal user) => TypedResults.Ok(new MeResponse
        {
            Name = user.Identity?.Name
                ?? First(user, "name", ClaimTypes.Name, "preferred_username", "upn"),
            IsAuthenticated = user.Identity?.IsAuthenticated ?? false,
            // Deliberately not ClaimTypes.NameIdentifier as a fallback: that resolves to
            // `sub`, which is pairwise per application, where `oid` is the stable directory
            // object id. Returning one labelled as the other would quietly break any caller
            // correlating a user across apps.
            ObjectId = First(user, "oid",
                "http://schemas.microsoft.com/identity/claims/objectidentifier"),
            TenantId = First(user, "tid",
                "http://schemas.microsoft.com/identity/claims/tenantidentifier",
                "http://schemas.microsoft.com/identity/claims/tenantid"),
            Scopes = (First(user, "scp", "scope",
                "http://schemas.microsoft.com/identity/claims/scope") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        }))
            .WithTags("Meta")
            .WithName("Me")
            .WithSummary("The calling principal, as the API resolved it.")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy);
    }

    /// <summary>First non-empty value among several claim names.</summary>
    private static string? First(ClaimsPrincipal user, params string[] claimTypes)
        => claimTypes
            .Select(user.FindFirstValue)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record MeResponse
{
    public string? Name { get; init; }
    public bool IsAuthenticated { get; init; }
    public string? ObjectId { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
}
