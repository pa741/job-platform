using Azure.Storage.Blobs;
using JobPlatform.Api.Features.Applications;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The pack store's registration, and the deployment that has no storage at all.
/// </summary>
/// <remarks>
/// <b>The degraded path is the one that matters here</b>, which is why it is tested from both
/// ends: against the service collection, where nothing may be registered, and against the real
/// host, where nothing may be resolvable. A deployment without a storage account still generates
/// documents and still serves the pack - it simply has no file to offer - and a change that turned
/// that into a startup failure would take the whole API down for a feature it does not have. That
/// is the same shape <c>AddAiProvider</c> and the realtime feed both have, and it is what lets
/// this suite run with no Azure account and no credentials.
///
/// Nothing here reaches the network. The refusal paths answer before any client call is made,
/// which is exactly what makes them worth pinning: a signature is never minted for a reference
/// this system did not write.
/// </remarks>
public sealed class ApplicationPackStoreTests
{
    private const string ServiceUri = "https://unused.blob.core.windows.net";

    [Fact]
    public void No_service_uri_registers_no_pack_store()
    {
        // The rule, stated once: without storage the store is absent rather than stubbed. Every
        // consumer resolves it as nullable and says so in its note.
        var provider = Provider(Configuration());

        Assert.Null(provider.GetService<IApplicationPackStore>());
        Assert.Null(provider.GetService<ApplicationPackContainer>());
    }

    [Fact]
    public void A_service_uri_that_will_not_parse_is_treated_as_no_service_uri()
    {
        // Failing fast is defensible for the SQL connection string, where there is no product
        // without one. Here a typo would be the difference between a running API and a container
        // that will not start, for a convenience over a record that lives in SQL.
        var provider = Provider(Configuration(("ApplicationPacks:serviceUri", "blob.core.windows.net")));

        Assert.Null(provider.GetService<IApplicationPackStore>());
    }

    [Fact]
    public void A_configured_service_uri_registers_a_store_over_the_named_container()
    {
        var provider = Provider(Configuration(
            ("ApplicationPacks:serviceUri", ServiceUri),
            ("ApplicationPacks:ContainerName", "application-packs")));

        var store = provider.GetService<IApplicationPackStore>();

        Assert.NotNull(store);
        Assert.Equal("application-packs", provider.GetRequiredService<ApplicationPackContainer>().Client.Name);

        // The account client is kept as well as the container's: a user delegation key is
        // requested at account scope, and reaching for it later would put the endpoint back into
        // the code that signs.
        Assert.Equal(
            new Uri(ServiceUri),
            provider.GetRequiredService<ApplicationPackContainer>().Service.Uri);
    }

    [Fact]
    public void The_container_apps_own_key_spellings_reach_the_options()
    {
        // These are the literal names infra/modules/containerapp.bicep sets. A host that maps
        // environment variables in the usual way turns them into the ApplicationPacks section;
        // this asserts the belt-and-braces lookup for one that does not, because the failure it
        // prevents is a deployment that has storage, is configured for it, and reports that no
        // documents are available.
        var provider = Provider(Configuration(
            ("ApplicationPacks__serviceUri", ServiceUri),
            ("ApplicationPacks__ContainerName", "application-packs")));

        Assert.NotNull(provider.GetService<IApplicationPackStore>());

        var options = provider.GetRequiredService<IOptions<ApplicationPackOptions>>().Value;

        Assert.Equal(ServiceUri, options.ServiceUri);
        Assert.Equal("application-packs", options.ContainerName);
        Assert.Equal("application-packs", provider.GetRequiredService<ApplicationPackContainer>().Client.Name);
    }

    [Fact]
    public void The_section_is_bound_so_a_deployment_can_move_the_container()
    {
        var provider = Provider(Configuration(
            ("ApplicationPacks:serviceUri", ServiceUri),
            ("ApplicationPacks:ContainerName", "other-packs"),
            ("ApplicationPacks:LinkLifetimeMinutes", "5")));

        var options = provider.GetRequiredService<IOptions<ApplicationPackOptions>>().Value;

        Assert.Equal("other-packs", options.ContainerName);
        Assert.Equal(TimeSpan.FromMinutes(5), options.LinkLifetime);
        Assert.Equal("other-packs", provider.GetRequiredService<ApplicationPackContainer>().Client.Name);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-30, 1)]
    [InlineData(15, 15)]
    [InlineData(600, 60)]
    public void A_links_lifetime_is_clamped_at_both_ends(int configured, int expected)
    {
        // Configurable must not become permanent. The upper clamp is the whole property of a
        // short-lived link - the URL is a bearer credential that will sit in a transcript - and
        // the lower one stops a zero producing a signature that has expired before it is handed
        // over.
        var options = new ApplicationPackOptions { LinkLifetimeMinutes = configured };

        Assert.Equal(TimeSpan.FromMinutes(expected), options.LinkLifetime);
        Assert.Equal(TimeSpan.FromMinutes(expected), Store(options).LinkLifetime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://account.blob.core.windows.net/application-packs/1/2/CV.pdf")]
    [InlineData("1/../2/CV.pdf")]
    [InlineData("application-packs/")]
    public async Task A_reference_that_names_no_blob_is_never_signed(string? stored)
    {
        // Answered before any client call, so this reaches no network. A row carrying a reference
        // from an older build, or from the wrong column, means the pack says no file is available
        // - the same answer it gives when nothing was ever rendered.
        Assert.Null(await Store().LinkAsync(stored));
    }

    [Fact]
    public async Task An_empty_render_stores_nothing()
    {
        // A renderer that produced no bytes has already failed. Storing an empty blob would leave
        // a path on the document row promising a file that is not there, which is worse than the
        // null the caller already handles.
        var stored = await Store().StoreAsync(
            new PackFileRequest
            {
                ProfileId = 1,
                DocumentId = 2,
                Document = PackDocument.CurriculumVitae,
                Format = PackFormat.Pdf,
                Content = [],
                CandidateName = "Pablo De Groot",
            });

        Assert.Null(stored);
    }

    [Fact]
    public void The_api_serves_no_pack_store_where_no_storage_is_configured()
    {
        // The same rule as the first test, proved against the real composition root rather than a
        // hand-built collection: a host with no storage account boots, serves, and simply has no
        // documents to hand over. This suite runs with no Azure account at all, which is what
        // makes that a fact rather than an intention.
        using var factory = new ApiFactory();

        Assert.Null(factory.Services.GetService<IApplicationPackStore>());
    }

    private static ApplicationPackStore Store(ApplicationPackOptions? options = null)
    {
        var settings = options ?? new ApplicationPackOptions();

        // No credential, deliberately. Every path exercised here answers before a request would
        // be made, so a test that needed one would be testing something else.
        var service = new BlobServiceClient(new Uri(ServiceUri));

        return new ApplicationPackStore(
            new ApplicationPackContainer(service, service.GetBlobContainerClient(settings.ContainerName)),
            Options.Create(settings),
            TimeProvider.System,
            NullLogger<ApplicationPackStore>.Instance);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    private static ServiceProvider Provider(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddApplicationPacks(configuration);

        return services.BuildServiceProvider();
    }
}
