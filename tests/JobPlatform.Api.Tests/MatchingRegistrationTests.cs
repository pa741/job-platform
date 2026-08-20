using JobPlatform.Ai;
using JobPlatform.Core.Matching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Which ranker the container actually resolves for a given configuration.
/// </summary>
/// <remarks>
/// Worth pinning because every other test in this suite runs on the keyword default, so the
/// Semantic Kernel composition would otherwise be exercised for the first time in production.
/// Resolving the service is the point: registration is lazy, so a Kernel that cannot be built
/// stays silent until the first match request.
/// </remarks>
public sealed class MatchingRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvMatching(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_default_is_the_keyword_ranker()
    {
        using var provider = Build();

        Assert.IsType<KeywordCvRanker>(provider.GetRequiredService<ICvRanker>());
    }

    [Fact]
    public void An_unrecognised_provider_falls_back_to_keyword_rather_than_throwing()
    {
        using var provider = Build(("Matching:Provider", "gpt-9"));

        Assert.IsType<KeywordCvRanker>(provider.GetRequiredService<ICvRanker>());
    }

    /// <summary>
    /// The whole Semantic Kernel graph: AnthropicClient to IChatClient to
    /// IChatCompletionService to Kernel to ranker. If any link is wrong this throws here
    /// rather than on a user's first match request.
    /// </summary>
    [Fact]
    public void Selecting_anthropic_with_a_key_resolves_the_semantic_kernel_ranker()
    {
        using var provider = Build(
            ("Matching:Provider", "anthropic"),
            ("Matching:Anthropic:ApiKey", "sk-ant-not-a-real-key-used-only-for-wiring"));

        var ranker = provider.GetRequiredService<ICvRanker>();

        Assert.IsType<SemanticKernelCvRanker>(ranker);
        Assert.Equal("semantic-kernel/anthropic", ranker.Name);
    }

    /// <summary>
    /// A missing key must not take down endpoints that have nothing to do with matching.
    /// </summary>
    [Fact]
    public void Selecting_anthropic_without_a_key_degrades_to_keyword_instead_of_failing_startup()
    {
        using var provider = Build(("Matching:Provider", "anthropic"));

        Assert.IsType<KeywordCvRanker>(provider.GetRequiredService<ICvRanker>());
    }
}
