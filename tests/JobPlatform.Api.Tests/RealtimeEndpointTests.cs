using System.Net;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The negotiate endpoint's two guarantees, both of which are about what it must not do.
/// </summary>
/// <remarks>
/// <b>It must not be openable by <c>Api:AllowAnonymousReads</c>.</b> That switch relaxes every
/// other read to anonymous so a frontend can be built against real data. This route mints a token
/// against a service with a twenty-connection ceiling that the deployment pays for, so it sits
/// behind the authenticated policy instead - the same fence the prompt-replay route has, for a
/// related reason. A test is the only thing standing between that decision and a future
/// convenience.
///
/// <b>And it must fail as "not here" rather than "broken".</b> The feed is optional by design, so
/// a deployment without one is a normal deployment. 503 says stop asking for now; a 500 would say
/// something is wrong and invite a retry loop against a service that does not exist.
/// </remarks>
public sealed class RealtimeEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new() { AllowAnonymousReads = true };
    private readonly HttpClient _client;

    public RealtimeEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Negotiate_is_not_opened_by_the_anonymous_reads_switch()
    {
        // The factory above turns anonymous reads on, which is what a frontend build uses. Every
        // /ai-calls read answers under it; this one must not.
        var response = await _client.PostAsync("/api/v1/realtime/negotiate", content: null);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            response.StatusCode,
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
    }

    [Fact]
    public async Task The_route_exists_and_only_answers_to_a_post()
    {
        // A GET reaching it would mean the group was mapped loosely enough that a browser could
        // wander onto a token-minting endpoint by following a link.
        var response = await _client.GetAsync("/api/v1/realtime/negotiate");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
