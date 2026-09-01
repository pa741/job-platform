using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Cosmos;
using ModelContextProtocol.AspNetCore;

namespace JobPlatform.Api.Features.Mcp;

/// <summary>
/// Registers the MCP server and its tools.
/// </summary>
/// <remarks>
/// A single extension called from <c>Program.cs</c>, beside <c>AddRealtimeFeed</c> and
/// <c>AddAiProvider</c>. Adding a feature here is a folder plus one line in
/// <c>EndpointGroupExtensions</c>; a feature that also needs services adds one line of
/// composition, and this follows the two that already do rather than inventing a third shape.
/// </remarks>
public static class McpRegistration
{
    /// <summary>
    /// The agent surface: an MCP server over the existing repositories.
    /// </summary>
    /// <remarks>
    /// <b>In <c>JobPlatform.Api</c> rather than as its own service.</b> A separate deployment
    /// would need its own SQL user and its own role assignments - a second identity to grant, in
    /// an architecture whose whole claim is that service-to-service hops are already solved. It
    /// reuses this API's Entra validation, its repositories and its authorisation boundary
    /// unchanged.
    ///
    /// <b>Stateless sessions.</b> The 2.x default, and the right one here: the container scales
    /// to zero, so there are no sticky sessions worth keeping and nothing to synchronise between
    /// revisions. Set explicitly rather than left to the default, because it is the setting that
    /// decides whether this survives a scale event and a default can move between versions.
    ///
    /// <b><c>WithTools&lt;T&gt;</c> rather than <c>WithToolsFromAssembly</c>.</b> The same reason
    /// <c>IEndpointGroup</c> gives for registering route groups by hand: the surface stays
    /// greppable and startup stays debuggable by reading it. Assembly scanning would mean a class
    /// gaining an attribute is a new public tool nobody reviewed.
    /// </remarks>
    public static IServiceCollection AddMcpFeature(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Unconditional, exactly like IAiCallLog beside it, and resolved as nullable by the
        // tools so a host without it still serves them. A host that genuinely has no Cosmos -
        // the test host - removes the registration rather than faking one, which is how the
        // ledger is handled and is the honest shape: absent, not stubbed.
        services.AddScoped<IDisclosureLog, DisclosureLogRepository>();

        services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<SubmissionTools>();

        return services;
    }
}

/// <summary>
/// Where an MCP client connects.
/// </summary>
/// <remarks>
/// <b>Behind <see cref="AuthSetup.AuthenticatedPolicy"/>, never the public read policy.</b>
/// <c>Api:AllowAnonymousReads</c> exists to open the posting corpus, which is public text. This
/// surface reads a person's shortlist, their generated CV and their contact details, and one
/// mistyped configuration section must not be the difference between a dashboard and a published
/// profile. <c>AuthorizationTests</c> pins it as metadata, because an anonymous request answers
/// 401 for other reasons too and a behavioural test alone would pass either way.
///
/// <b>Its own rate-limiting policy.</b> A client polls differently from a browser and must not
/// be able to exhaust the budget the dashboard shares - and these tools read Azure SQL, which is
/// billed on wall-clock time against a monthly grant one daily ingest half-consumes.
///
/// <b>Inside <c>/api/v1</c>, so the address is <c>/api/v1/mcp</c>.</b> Health and identity sit
/// outside the versioned group because probes address them by fixed path and a version bump must
/// not move them. A tool surface is not that: it is addressed by a person pasting a URL into a
/// client's configuration once, and it should version with everything else it reads.
///
/// <b>No output cache.</b> Every tool here is per-principal, and a shared cache keyed on a URL
/// with no user in it is how one person is served another's pipeline.
/// </remarks>
public sealed class McpEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapMcp("/mcp")
            .WithTags("MCP")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.McpPolicy);
    }
}
