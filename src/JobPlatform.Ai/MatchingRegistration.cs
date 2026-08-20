using Anthropic;
using JobPlatform.Core.Matching;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace JobPlatform.Ai;

public static class MatchingRegistration
{
    /// <summary>
    /// Registers the matching pipeline and resolves <see cref="ICvRanker"/> from
    /// <c>Matching:Provider</c>.
    /// </summary>
    /// <remarks>
    /// The keyword ranker is always registered, not merely as a default: the pipeline uses it
    /// as its retrieval prefilter and as its fallback whichever provider is selected, so it is
    /// a component of every configuration rather than an alternative to them.
    ///
    /// Selecting <c>anthropic</c> without a key falls back to keyword with a warning rather
    /// than throwing. Failing startup would mean a missing environment variable takes down
    /// every endpoint, including those with nothing to do with matching - a bad trade for a
    /// service whose degraded mode is genuinely useful.
    /// </remarks>
    public static IServiceCollection AddCvMatching(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CvMatchingOptions>(configuration.GetSection(CvMatchingOptions.SectionName));
        services.Configure<SemanticKernelOptions>(configuration.GetSection(SemanticKernelOptions.SectionName));

        services.AddSingleton<ICvProfileExtractor, KeywordCvProfileExtractor>();
        services.AddSingleton<KeywordCvRanker>();
        services.AddScoped<CvMatchingService>();

        var provider = configuration[$"{CvMatchingOptions.SectionName}:Provider"] ?? "keyword";
        var apiKey = configuration[$"{SemanticKernelOptions.SectionName}:ApiKey"];
        var model = configuration[$"{SemanticKernelOptions.SectionName}:Model"] ?? "claude-opus-5";

        if (!string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ICvRanker>(sp => sp.GetRequiredService<KeywordCvRanker>());
            return services;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddSingleton<ICvRanker>(sp =>
            {
                sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(MatchingRegistration))
                    .LogWarning(
                        "Matching:Provider is 'anthropic' but no API key is configured; " +
                        "using the keyword ranker. Set Matching__Anthropic__ApiKey to enable it.");

                return sp.GetRequiredService<KeywordCvRanker>();
            });

            return services;
        }

        services.AddSingleton(BuildKernel(apiKey, model));
        services.AddSingleton<ICvRanker, SemanticKernelCvRanker>();

        return services;
    }

    /// <summary>
    /// Builds the Kernel the ranker invokes prompts through.
    /// </summary>
    /// <remarks>
    /// There is no official Microsoft Semantic Kernel connector for Anthropic - the only
    /// packages on NuGet are third-party alphas, which is not a dependency this repository
    /// should carry. So the chat service is composed instead: the official Anthropic SDK
    /// exposes an <c>IChatClient</c> through Microsoft.Extensions.AI, and Semantic Kernel
    /// consumes any <c>IChatClient</c> as an <see cref="IChatCompletionService"/>.
    ///
    /// The result is the arrangement Semantic Kernel is actually for - prompt templates,
    /// Kernel arguments, a provider-neutral chat abstraction - with a supported, GA transport
    /// underneath. Swapping to Azure OpenAI or a future first-party Anthropic connector is a
    /// change to this method alone.
    ///
    /// <c>AsChatCompletionService</c> is marked experimental (SKEXP0001); that is why
    /// TreatWarningsAsErrors is off in Directory.Build.props.
    /// </remarks>
    private static Kernel BuildKernel(string apiKey, string model)
    {
        var builder = Kernel.CreateBuilder();

        // Singleton client: it owns the connection pool.
        IChatClient chatClient = new AnthropicClient { ApiKey = apiKey }
            .AsIChatClient(model);

        builder.Services.AddSingleton(chatClient.AsChatCompletionService());

        return builder.Build();
    }
}
