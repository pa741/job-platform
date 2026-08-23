using Anthropic;
using JobPlatform.Ai.Extraction;
using JobPlatform.Core.Enrichment;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace JobPlatform.Ai;

public static class AiRegistration
{
    /// <summary>
    /// Registers the LLM provider selected by <c>Ai:Provider</c>.
    /// </summary>
    /// <remarks>
    /// Nothing consumes the <see cref="Kernel"/> yet - the CV matching that used to is being
    /// rebuilt with a different structure. The registration stays wired anyway, so the path
    /// from configuration through the Key Vault-backed secret to a working chat service is
    /// exercised by a test rather than discovered for the first time by whatever adopts it.
    ///
    /// A provider of <c>none</c>, an unrecognised provider, or a missing key all register no
    /// Kernel and do not throw. Failing startup would mean an absent environment variable
    /// takes down every endpoint, including those with nothing to do with AI.
    /// </remarks>
    public static IServiceCollection AddAiProvider(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AiProviderOptions>(
            configuration.GetSection(AiProviderOptions.SectionName));

        var provider = configuration[AiProviderOptions.ProviderKey] ?? "none";
        var apiKey = configuration[$"{AiProviderOptions.SectionName}:ApiKey"];
        var model = configuration[$"{AiProviderOptions.SectionName}:Model"] ?? "claude-opus-5";

        if (!string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            return services;
        }

        services.AddSingleton(BuildKernel(apiKey, model));

        // Inside the same `if`, deliberately. No Kernel means no extractor, so a consumer
        // resolving IDocumentExtractor? gets null and skips the step - the pipeline still
        // runs and nothing is enqueued for a model that does not exist.
        services.AddSingleton<IDocumentExtractor, KernelDocumentExtractor>();

        return services;
    }

    /// <summary>
    /// Builds the Kernel prompts are invoked through.
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
