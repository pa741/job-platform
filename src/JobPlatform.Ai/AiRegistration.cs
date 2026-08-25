using System.ClientModel;
using Azure.Core;
using Azure.Identity;
using JobPlatform.Ai.Applications;
using JobPlatform.Ai.Extraction;
using JobPlatform.Ai.Matching;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using OpenAI;

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

        // Inside the same `if`, deliberately. No Kernel means no extractor, no assessor and no
        // writer, so a consumer resolving one of these as nullable gets null and skips the
        // step — the pipeline still runs and nothing is enqueued for a model that does not
        // exist.
        services.AddSingleton<IDocumentExtractor, KernelDocumentExtractor>();
        services.AddSingleton<ICandidacyAssessor, KernelCandidacyAssessor>();
        services.AddSingleton<IApplicationWriter, KernelApplicationWriter>();

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

        // A single credential instance, shared. Each one maintains its own token cache, so
        // constructing them per service would mean two identical round trips to IMDS.
        TokenCredential credential = new DefaultAzureCredential(
            new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                    ? null
                    : options.ManagedIdentityClientId,
            });

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
}
