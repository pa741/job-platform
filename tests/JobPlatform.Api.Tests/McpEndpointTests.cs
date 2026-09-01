using System.Net;
using System.Net.Http.Json;
using JobPlatform.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The agent surface: what it exposes, and what it refuses to expose.
/// </summary>
/// <remarks>
/// The tool <i>behaviour</i> is not tested here - it is a thin projection over repositories that
/// have their own tests, and reaching it would need an authenticated principal the test host has
/// no way to mint. What is tested is the part that is specific to this surface and would be
/// catastrophic to get wrong: the route is closed, the surface is the four tools somebody
/// reviewed, and there is no tool that returns the profile or sends anything.
/// </remarks>
public sealed class McpEndpointTests
{
    /// <summary>The whole read-only surface, by name. Adding to this list is a deliberate act.</summary>
    private static readonly string[] Expected =
    [
        "list_applyable",
        "get_submission_pack",
        "get_form_field",
        "list_submissions",
    ];

    /// <summary>
    /// The tools registered are exactly the ones intended.
    /// </summary>
    /// <remarks>
    /// <b>An equality, not a superset.</b> The point is the tools that are <i>absent</i>: there is
    /// no <c>submit_application</c>, because applying is irreversible and outward-facing and
    /// nothing in this repository may reach an employer; there is no <c>get_profile</c>, because
    /// a tool result is transcript content wherever the client runs; and there is no write tool
    /// yet, because those are added only once this surface has been exercised. A superset
    /// assertion would pass while any of those was quietly added.
    ///
    /// It also pins the registration style. <c>WithTools&lt;T&gt;</c> is explicit; had it been
    /// <c>WithToolsFromAssembly</c>, a class gaining an attribute would become a public tool with
    /// nothing failing, and this test is what turns that into a red build.
    /// </remarks>
    [Fact]
    public void The_tool_surface_is_exactly_the_four_read_tools()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var tools = factory.Services
            .GetRequiredService<IOptions<McpServerOptions>>()
            .Value
            .ToolCollection;

        Assert.NotNull(tools);

        var names = tools.Select(tool => tool.ProtocolTool.Name).OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(Expected.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    /// <summary>
    /// Every tool describes itself, because the description is the interface a model reads.
    /// </summary>
    /// <remarks>
    /// A tool with no description is one a model has to guess the purpose of from its name, and
    /// guessing wrong here means calling <c>get_form_field</c> when it meant
    /// <c>get_submission_pack</c> - which is a disclosure, not a mistake to shrug at.
    /// </remarks>
    [Fact]
    public void Every_tool_carries_a_description()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var tools = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;

        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description)));
    }

    /// <summary>
    /// The route requires an authenticated principal, asserted as metadata.
    /// </summary>
    /// <remarks>
    /// As metadata rather than through a response, for the reason
    /// <c>AuthorizationTests.Every_submission_route_requires_the_authenticated_policy</c> gives:
    /// an anonymous request answers 401 for several reasons and a behavioural test alone cannot
    /// tell which one it caught. This surface reads a person's shortlist, their generated CV and
    /// their contact details, so <c>Api:AllowAnonymousReads</c> must never reach it - and that
    /// flag is set to true here precisely so this test would notice if it did.
    /// </remarks>
    [Fact]
    public void The_mcp_route_requires_the_authenticated_policy()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        var routes = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/mcp", StringComparison.Ordinal) == true)
            .ToList();

        // The transport maps more than one endpoint under the prefix. Asserting it found some
        // stops this passing vacuously if the path ever moves.
        Assert.NotEmpty(routes);

        Assert.All(routes, route =>
        {
            var policies = route.Metadata.OfType<IAuthorizeData>().Select(data => data.Policy).ToList();

            Assert.Contains(AuthSetup.AuthenticatedPolicy, policies);
            Assert.DoesNotContain(AuthSetup.PublicReadPolicy, policies);
        });
    }

    /// <summary>And it answers 401 end to end, with the anonymous-reads switch on.</summary>
    [Fact]
    public async Task An_unauthenticated_client_cannot_list_the_tools()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
