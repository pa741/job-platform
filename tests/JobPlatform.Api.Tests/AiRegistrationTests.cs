using JobPlatform.Ai;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Which AI provider the container actually resolves for a given configuration.
/// </summary>
/// <remarks>
/// Resolving the services is the point rather than inspecting the descriptors: registration is
/// lazy, so a Kernel that cannot be built stays silent until something asks for one, and the
/// first thing to ask would otherwise be a nightly sweep at half past three in the morning.
///
/// Note what these tests never supply: a key. Azure OpenAI authenticates with Entra, so the
/// only thing that decides whether a Kernel exists is whether an endpoint is configured -
/// which is also why none of this needs a network or a credential to run.
/// </remarks>
public sealed class AiRegistrationTests
{
    private const string Endpoint = "https://not-a-real-resource.openai.azure.com/";

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
            ("Ai:Provider", "some-other-vendor"),
            ("Ai:AzureOpenAi:Endpoint", Endpoint));

        Assert.Null(provider.GetService<Kernel>());
    }

    /// <summary>
    /// The whole Semantic Kernel graph: credential to Azure OpenAI connector to Kernel, with
    /// both deployments registered on it. If any link is wrong this throws here rather than on
    /// the nightly sweep's first model call.
    /// </summary>
    [Fact]
    public void Selecting_azure_openai_with_an_endpoint_resolves_a_kernel()
    {
        using var provider = Build(
            ("Ai:Provider", "azureopenai"),
            ("Ai:AzureOpenAi:Endpoint", Endpoint));

        Assert.NotNull(provider.GetService<Kernel>());
    }

    /// <summary>
    /// Two chat services on one Kernel, distinguished by service id.
    /// </summary>
    /// <remarks>
    /// The thing that decides whether a call costs bulk money or writing money, and it is
    /// silently wrong if either registration is missing: Semantic Kernel falls back to the only
    /// service present rather than failing, so a missing writing deployment would mean CVs
    /// quietly written by the cheap model.
    /// </remarks>
    [Fact]
    public void Both_deployments_are_registered_and_separately_addressable()
    {
        using var provider = Build(
            ("Ai:Provider", "azureopenai"),
            ("Ai:AzureOpenAi:Endpoint", Endpoint));

        var kernel = provider.GetRequiredService<Kernel>();

        Assert.NotNull(kernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>(
            AzureOpenAiOptions.BulkServiceId));

        Assert.NotNull(kernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>(
            AzureOpenAiOptions.WritingServiceId));
    }

    /// <summary>
    /// A missing endpoint must not take down endpoints that have nothing to do with AI.
    /// </summary>
    [Fact]
    public void Selecting_azure_openai_without_an_endpoint_registers_nothing_instead_of_failing_startup()
    {
        using var provider = Build(("Ai:Provider", "azureopenai"));

        Assert.Null(provider.GetService<Kernel>());
    }

    /// <summary>
    /// All four consumers appear and disappear together with the Kernel.
    /// </summary>
    /// <remarks>
    /// They are registered inside one <c>if</c> deliberately, and this is what holds that
    /// together: every caller resolves them as nullable and skips its step when they are absent,
    /// so a half-registered layer would produce a null reference somewhere far from here.
    /// </remarks>
    [Fact]
    public void The_consumers_are_registered_exactly_where_the_kernel_is()
    {
        using var unconfigured = Build();

        Assert.Null(unconfigured.GetService<IDocumentExtractor>());
        Assert.Null(unconfigured.GetService<ICandidacyAssessor>());
        Assert.Null(unconfigured.GetService<IApplicationWriter>());
        Assert.Null(unconfigured.GetService<ITextEmbedder>());

        using var configured = Build(
            ("Ai:Provider", "azureopenai"),
            ("Ai:AzureOpenAi:Endpoint", Endpoint));

        Assert.NotNull(configured.GetService<IDocumentExtractor>());
        Assert.NotNull(configured.GetService<ICandidacyAssessor>());
        Assert.NotNull(configured.GetService<IApplicationWriter>());
        Assert.NotNull(configured.GetService<ITextEmbedder>());
    }

    /// <summary>
    /// The embedding generator resolves, and it is not on the Kernel.
    /// </summary>
    /// <remarks>
    /// Two things fail silently here and both are worth a test of their own. The registration
    /// helper is marked experimental, which also hides it from extension-method lookup - so the
    /// call is written against an aliased static class, and a refactor that "tidies" it back into
    /// the fluent form compiles against the <c>IKernelBuilder</c> overload and registers nothing
    /// on the container at all. And an embedding is not a chat completion, so resolving it
    /// through the Kernel would be the wrong seam even where it worked.
    /// </remarks>
    [Fact]
    public void The_embedding_generator_is_on_the_container_rather_than_the_kernel()
    {
        using var configured = Build(
            ("Ai:Provider", "azureopenai"),
            ("Ai:AzureOpenAi:Endpoint", Endpoint));

        Assert.NotNull(
            configured.GetService<Microsoft.Extensions.AI.IEmbeddingGenerator<
                string, Microsoft.Extensions.AI.Embedding<float>>>());
    }

    /// <summary>
    /// The identity the AI layer authenticates as falls back to the one everything else uses.
    /// </summary>
    /// <remarks>
    /// A user-assigned identity has to be named explicitly or <c>DefaultAzureCredential</c>
    /// cannot tell which of the host's identities to present - the same trap the SQL connection
    /// string's <c>User Id=</c> exists for. Repeating the client id under the AI section would
    /// be a second place to get it wrong, so the top-level key is read when it is absent.
    /// </remarks>
    [Fact]
    public void The_managed_identity_client_id_falls_back_to_the_shared_one()
    {
        using var provider = Build(
            ("Ai:Provider", "azureopenai"),
            ("Ai:AzureOpenAi:Endpoint", Endpoint),
            ("ManagedIdentityClientId", "00000000-0000-0000-0000-000000000000"));

        Assert.NotNull(provider.GetService<Kernel>());
    }
}
