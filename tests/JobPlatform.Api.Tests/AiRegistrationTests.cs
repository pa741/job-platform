using JobPlatform.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Which AI provider the container actually resolves for a given configuration.
/// </summary>
/// <remarks>
/// Nothing consumes the Kernel yet, which is exactly why this is pinned here: the Semantic
/// Kernel composition would otherwise be exercised for the first time by whatever feature
/// adopts it. Resolving the service is the point - registration is lazy, so a Kernel that
/// cannot be built stays silent until something asks for one.
/// </remarks>
public sealed class AiRegistrationTests
{
    private const string WiringKey = "sk-ant-not-a-real-key-used-only-for-wiring";

    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiProvider(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void No_provider_is_configured_by_default()
    {
        using var provider = Build();

        Assert.Null(provider.GetService<Kernel>());
    }

    [Fact]
    public void An_unrecognised_provider_registers_nothing_rather_than_throwing()
    {
        using var provider = Build(
            ("Ai:Provider", "gpt-9"),
            ("Ai:Anthropic:ApiKey", WiringKey));

        Assert.Null(provider.GetService<Kernel>());
    }

    /// <summary>
    /// The whole Semantic Kernel graph: AnthropicClient to IChatClient to
    /// IChatCompletionService to Kernel. If any link is wrong this throws here rather than on
    /// a future feature's first model call.
    /// </summary>
    [Fact]
    public void Selecting_anthropic_with_a_key_resolves_a_kernel()
    {
        using var provider = Build(
            ("Ai:Provider", "anthropic"),
            ("Ai:Anthropic:ApiKey", WiringKey));

        Assert.NotNull(provider.GetService<Kernel>());
    }

    /// <summary>
    /// A missing key must not take down endpoints that have nothing to do with AI.
    /// </summary>
    [Fact]
    public void Selecting_anthropic_without_a_key_registers_nothing_instead_of_failing_startup()
    {
        using var provider = Build(("Ai:Provider", "anthropic"));

        Assert.Null(provider.GetService<Kernel>());
    }
}
