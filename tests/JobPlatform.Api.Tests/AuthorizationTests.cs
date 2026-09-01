using System.Net;
using JobPlatform.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// What the AllowAnonymousReads flag does and, more importantly, what it does not do.
/// </summary>
public sealed class AuthorizationTests
{
    [Fact]
    public async Task Reads_are_open_when_anonymous_reads_are_allowed()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/postings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Reads_are_closed_when_anonymous_reads_are_not_allowed()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = false };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/postings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The guarantee worth asserting: opening reads for convenience must never open an
    /// endpoint that is not a read.
    /// </summary>
    [Fact]
    public async Task Me_stays_closed_even_when_anonymous_reads_are_allowed()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The same guarantee for the searches, which decide what the scraper spends money on.
    /// </summary>
    /// <remarks>
    /// A read that is opened for convenience must never open a route that configures a scheduled
    /// job on somebody's NAS. Every verb, because the flag opens reads and it is the write verbs
    /// that would be catastrophic - and nothing else in the suite fails if this regresses.
    /// </remarks>
    [Theory]
    [InlineData("GET", "/api/v1/searches")]
    [InlineData("GET", "/api/v1/searches/options")]
    [InlineData("POST", "/api/v1/searches")]
    [InlineData("PUT", "/api/v1/searches/software-engineer")]
    [InlineData("DELETE", "/api/v1/searches/software-engineer")]
    [InlineData("POST", "/api/v1/searches/publish")]
    public async Task Searches_stay_closed_even_when_anonymous_reads_are_allowed(string method, string path)
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The same guarantee for the submission pipeline, which is somebody's job search.
    /// </summary>
    /// <remarks>
    /// The posting corpus is public text and the anonymous-reads flag exists to open it. Which
    /// jobs a particular person applied to, where each one stands, and what they were told is the
    /// opposite of public - it is closer to the profile than to the corpus. Every verb, because
    /// the flag opens reads and it is the writes that would be worst, and because nothing else in
    /// this suite fails if the policy on this group regresses to PublicReadPolicy.
    /// </remarks>

    /// <summary>
    /// The policy on the submission group, asserted as metadata rather than through a response.
    /// </summary>
    /// <remarks>
    /// <b>Written after the behavioural version below turned out not to pin what it claimed.</b>
    /// Every handler under <c>/submissions</c> also calls <c>CallerIdentity.TryGetSubjectId</c>,
    /// which answers 401 when the token carries no <c>oid</c> - so an anonymous request answers
    /// 401 whichever policy is on the group, and swapping <c>AuthenticatedPolicy</c> for
    /// <c>PublicReadPolicy</c> left the GET cases green. Defence in depth working, and a test
    /// measuring the second layer while describing the first.
    ///
    /// Reading the endpoint metadata cannot be fooled that way: it asserts the thing the
    /// remarks on <c>SubmissionEndpoints</c> promise, and it fails the moment somebody relaxes
    /// the group - which is the failure that would publish a person's job search.
    /// </remarks>
    [Fact]
    public void Every_submission_route_requires_the_authenticated_policy()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };

        // Forces the host to build; the endpoint data source is not populated before it does.
        using var client = factory.CreateClient();

        var routes = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/submissions", StringComparison.Ordinal) == true)
            .ToList();

        // The group has four routes. Asserting the count stops this passing vacuously if the
        // prefix ever changes and the filter matches nothing at all.
        Assert.Equal(4, routes.Count);

        Assert.All(routes, route =>
        {
            var policies = route.Metadata
                .OfType<IAuthorizeData>()
                .Select(data => data.Policy)
                .ToList();

            Assert.Contains(AuthSetup.AuthenticatedPolicy, policies);
            Assert.DoesNotContain(AuthSetup.PublicReadPolicy, policies);
        });
    }

    /// <summary>
    /// And the same routes answer 401 anonymously, whatever the reason.
    /// </summary>
    /// <remarks>
    /// Kept alongside the metadata assertion rather than replaced by it. This one is weaker than
    /// it looks - see above - but it is the end-to-end statement, and the two failing for
    /// different reasons is the point of having both.
    /// </remarks>
    [Theory]
    [InlineData("GET", "/api/v1/submissions")]
    [InlineData("POST", "/api/v1/submissions")]
    [InlineData("GET", "/api/v1/submissions/1/events")]
    [InlineData("POST", "/api/v1/submissions/1/events")]
    public async Task Submissions_stay_closed_even_when_anonymous_reads_are_allowed(string method, string path)
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Liveness must answer without a token: the platform's probe does not carry one, and an
    /// authenticated probe would restart every healthy container.
    /// </summary>
    [Fact]
    public async Task Health_answers_without_a_token_regardless_of_configuration()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = false };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
