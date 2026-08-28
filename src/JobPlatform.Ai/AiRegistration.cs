using System.ClientModel;
using Azure.Core;
using Azure.Identity;
using JobPlatform.Ai.Applications;
using JobPlatform.Ai.Extraction;
using JobPlatform.Ai.Matching;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using OpenAI;

// Two classes of this name are in scope - Semantic Kernel declares one in its own namespace and
// another in Microsoft.Extensions.DependencyInjection - so an unqualified reference is ambiguous
// and the extension-method form silently resolves to the IKernelBuilder overload instead. Named
// once here rather than fully qualified at the call.
using AzureOpenAiServices = Microsoft.Extensions.DependencyInjection.AzureOpenAIServiceCollectionExtensions;

namespace JobPlatform.Ai;

public static class AiRegistration
{
    /// <summary>
    /// Registers the LLM provider selected by <c>Ai:Provider</c>.
    /// </summary>
    /// <remarks>
    /// A provider of <c>none</c>, an unrecognised provider, or a missing endpoint all register
    /// no Kernel and do not throw. Failing startup would mean an absent environment variable
    /// takes down every endpoint, including those with nothing to do with AI — and the three
    /// consumers below are all resolved as nullable for the same reason.
    ///
    /// Note what is <i>not</i> checked: a credential. Azure OpenAI authenticates with Entra, so
    /// there is no key to be present or absent, and no code path here that can fail for want of
    /// a secret. Whether the identity actually holds <c>Cognitive Services OpenAI User</c> on
    /// the resource is answered by the first call, not by registration — the same contract
    /// every other identity-based connection in this system runs under.
    /// </remarks>
    public static IServiceCollection AddAiProvider(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AzureOpenAiOptions>(
            configuration.GetSection(AzureOpenAiOptions.SectionName));

        var provider = configuration[AzureOpenAiOptions.ProviderKey] ?? "none";

        var options = configuration.GetSection(AzureOpenAiOptions.SectionName).Get<AzureOpenAiOptions>()
            ?? new AzureOpenAiOptions();

        // The API resolves its identity through a top-level key, because SQL and Cosmos need
        // the same value. Falling back to it means the AI section does not have to repeat it.
        options.ManagedIdentityClientId ??= configuration["ManagedIdentityClientId"];

        if (!string.Equals(provider, "azureopenai", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return services;
        }

        services.AddSingleton(BuildKernel(options));

        // The embeddings deployment, which is not on the Kernel and cannot be. A Kernel holds
        // chat completion services and invokes prompts against them; an embedding is not a
        // prompt and returns no completion, so it is registered on the container directly as
        // the Microsoft.Extensions.AI abstraction Semantic Kernel itself sits on. No new
        // package, no new credential, and the same TokenCredential the chat services use.
        // Marked experimental by Semantic Kernel, and suppressed at exactly this call rather than
        // through a project-wide NoWarn - the same treatment, and for the same reason, as the one
        // in AiPrompt: a blanket suppression would silently accept the next experimental API
        // somebody reaches for.
#pragma warning disable SKEXP0010
        AzureOpenAiServices.AddAzureOpenAIEmbeddingGenerator(
            services,
            deploymentName: options.EmbeddingDeployment,
            endpoint: options.Endpoint!,
            credentials: Credential(options),
            // Ask the deployment for a truncated vector rather than truncating one here.
            // Matryoshka representation learning is what makes the first 512 of 1,536 dimensions
            // a real embedding rather than a lossy prefix, and 512 is the width MatchRanker's
            // weight was measured at.
            dimensions: EmbeddingVector.Dimensions);
#pragma warning restore SKEXP0010

        // Inside the same `if`, deliberately. No Kernel means no extractor, no assessor and no
        // writer, so a consumer resolving one of these as nullable gets null and skips the
        // step — the pipeline still runs and nothing is enqueued for a model that does not
        // exist.
        services.AddSingleton<IDocumentExtractor, KernelDocumentExtractor>();
        services.AddSingleton<ICandidacyAssessor, KernelCandidacyAssessor>();
        services.AddSingleton<IApplicationWriter, KernelApplicationWriter>();
        services.AddSingleton<ITextEmbedder, KernelTextEmbedder>();

        return services;
    }

    /// <summary>
    /// Registers the OpenAI batch extraction path, when a key is configured.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="AddAiProvider"/> on purpose, and independent of it.</b> The
    /// two answer different questions: that one is "which provider serves interactive work",
    /// this one is "is there somewhere to send a corpus overnight". A deployment can have
    /// either, both, or neither, and the combination that matters most is both - Azure for
    /// profiles, where a person is waiting and personal data should not leave the tenant, and
    /// OpenAI's batch endpoint for job adverts, where nobody is waiting and the rate pool is
    /// separate from the interactive deployment's.
    ///
    /// <b>This is the one credential in the system.</b> Everything else authenticates with
    /// Entra; reaching api.openai.com cannot. Absent the key nothing here registers, the
    /// backfill falls back to the queue, and the property that a fresh clone deploys with
    /// nothing to leak is preserved.
    /// </remarks>
    public static IServiceCollection AddOpenAiBatchProvider(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OpenAiBatchOptions>(
            configuration.GetSection(OpenAiBatchOptions.SectionName));

        var apiKey = configuration[$"{OpenAiBatchOptions.SectionName}:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return services;
        }

        // One client, shared. It owns the connection pool, the same reason CosmosClient is a
        // singleton and the chat client above is.
        var client = new OpenAIClient(new ApiKeyCredential(apiKey));

#pragma warning disable OPENAI001
        services.AddSingleton(client.GetBatchClient());
#pragma warning restore OPENAI001
        services.AddSingleton(client.GetOpenAIFileClient());
        services.AddSingleton<IBatchDocumentExtractor, OpenAiBatchExtractor>();

        return services;
    }

    /// <summary>
    /// Builds the Kernel every prompt is invoked through.
    /// </summary>
    /// <remarks>
    /// Two chat completion services on one Kernel, distinguished by service id, so a caller
    /// picks the model by naming which job it is doing rather than by holding a different
    /// Kernel. <see cref="AzureOpenAiOptions.BulkServiceId"/> is the cheap high-volume
    /// deployment; <see cref="AzureOpenAiOptions.WritingServiceId"/> is the expensive one. A
    /// prompt selects between them through <c>ServiceId</c> on its execution settings.
    ///
    /// The credential is a <see cref="TokenCredential"/>, not a key. Semantic Kernel's Azure
    /// OpenAI connector takes one directly and refreshes the token itself, which is what makes
    /// the whole no-secret property hold end to end rather than only at the vault boundary.
    ///
    /// This method replaced a hand-composed Anthropic transport — <c>AsIChatClient()</c> handed
    /// to <c>AsChatCompletionService()</c> — that existed only because Semantic Kernel has no
    /// official Anthropic connector. Moving to Azure OpenAI made that composition unnecessary:
    /// the connector here ships in the Semantic Kernel metapackage and is GA, so nothing in
    /// this file is experimental and nothing reaches past the Kernel to an SDK.
    /// </remarks>
    internal static Kernel BuildKernel(AzureOpenAiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = Kernel.CreateBuilder();

        var credential = Credential(options);

        builder.AddAzureOpenAIChatCompletion(
            deploymentName: options.BulkDeployment,
            endpoint: options.Endpoint!,
            credentials: credential,
            serviceId: AzureOpenAiOptions.BulkServiceId);

        builder.AddAzureOpenAIChatCompletion(
            deploymentName: options.WritingDeployment,
            endpoint: options.Endpoint!,
            credentials: credential,
            serviceId: AzureOpenAiOptions.WritingServiceId);

        return builder.Build();
    }

    /// <summary>
    /// The identity every deployment on this resource is reached with.
    /// </summary>
    /// <remarks>
    /// A <see cref="TokenCredential"/>, not a key, which is what makes the no-secret property
    /// hold end to end rather than only at a vault boundary. Constructed per call site rather
    /// than cached in a static: each instance maintains its own token cache, so the two callers
    /// here cost one extra round trip to IMDS at startup and nothing afterwards - and a static
    /// would outlive the options it was built from.
    /// </remarks>
    private static TokenCredential Credential(AzureOpenAiOptions options)
        => new DefaultAzureCredential(
            new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                    ? null
                    : options.ManagedIdentityClientId,
            });
}
