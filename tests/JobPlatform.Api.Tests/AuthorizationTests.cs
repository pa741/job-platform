using System.Net;
using System.Net.Http.Json;
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
    /// endpoint that costs money per call.
    /// </summary>
    [Fact]
    public async Task Matching_stays_closed_even_when_anonymous_reads_are_allowed()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/match", new { cvText = "C# and Azure engineer." });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_stays_closed_even_when_anonymous_reads_are_allowed()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/me");

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
