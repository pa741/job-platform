using Azure.Storage.Blobs;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Applications;
using JobPlatform.Data.Sql;
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
/// <b>Two hosts register this store and only one of them is the API</b>, which is why a suite
/// under <c>JobPlatform.Api.Tests</c> asserts where the registration ships as well as what it
/// does. The Functions host renders the nightly pass's documents and cannot reference a web
/// project; a store it could not reach was a pass that never produced a file and never said so.
///
/// <b>There are two containers now, and most of what is asserted here is about keeping them
/// apart.</b> Generated documents go to <c>application-packs</c> and the candidate's own CV
/// variants to <c>profile-cvs</c>, because a document id and a variant id are independent and both
/// start at one - so one container would have document 34 and variant 34 of a candidate addressing
/// the same directory, and since a chosen CV and a generated one spell the filename identically,
/// the same blob. That failure has no symptom on this side: the write succeeds, the row looks
/// right, and an employer receives a document nobody chose to send them. So the default, both key
/// spellings, the resolution of a stored path and the refusal to write a variant into the pack
/// container are each pinned separately.
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
        Assert.Equal("application-packs", provider.GetRequiredService<ApplicationPackContainer>().Packs.Name);

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
        Assert.Equal("application-packs", provider.GetRequiredService<ApplicationPackContainer>().Packs.Name);
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
        Assert.Equal("other-packs", provider.GetRequiredService<ApplicationPackContainer>().Packs.Name);
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
    public void Variants_get_a_container_of_their_own_without_a_host_asking_for_one()
    {
        // The default matters more than the setting does. A document id and a variant id are
        // independent and both start at one, so under a single container document 34 and variant
        // 34 of one candidate address the same directory - and since a chosen CV and a generated
        // one now spell the filename identically, the same blob. A deployment that has not been
        // redeployed since this shipped still has to keep them apart, so the default here is the
        // template's and not the pack container's.
        var container = Provider(Configuration(("ApplicationPacks:serviceUri", ServiceUri)))
            .GetRequiredService<ApplicationPackContainer>();

        Assert.Equal("profile-cvs", container.Variants.Name);
        Assert.NotEqual(container.Packs.Name, container.Variants.Name);
    }

    [Fact]
    public void The_container_apps_variant_key_spelling_reaches_the_options()
    {
        // The literal name infra/modules/containerapp.bicep and functionapp.bicep both set. Read
        // the same belt-and-braces way as the other two, because a host whose configuration source
        // does not map '__' would otherwise fall back to the default while the template said
        // otherwise - and a container name that disagrees with the deployment is a variant written
        // where nothing will look for it.
        var provider = Provider(Configuration(
            ("ApplicationPacks__serviceUri", ServiceUri),
            ("ApplicationPacks__ContainerName", "application-packs"),
            ("ApplicationPacks__VariantContainerName", "profile-cvs")));

        var options = provider.GetRequiredService<IOptions<ApplicationPackOptions>>().Value;

        Assert.Equal("profile-cvs", options.VariantContainerName);
        Assert.Equal("profile-cvs", provider.GetRequiredService<ApplicationPackContainer>().Variants.Name);
    }

    [Fact]
    public void The_section_is_bound_so_a_deployment_can_move_the_variant_container()
    {
        var provider = Provider(Configuration(
            ("ApplicationPacks:serviceUri", ServiceUri),
            ("ApplicationPacks:VariantContainerName", "other-cvs")));

        Assert.Equal(
            "other-cvs",
            provider.GetRequiredService<IOptions<ApplicationPackOptions>>().Value.VariantContainerName);

        Assert.Equal(
            "other-cvs",
            provider.GetRequiredService<ApplicationPackContainer>().Variants.Name);
    }

    [Theory]
    [InlineData("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", "profile-cvs", "12/34/Pablo_De_Groot_Curriculum_Vitae.pdf")]
    [InlineData("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.docx", "profile-cvs", "12/34/Pablo_De_Groot_Curriculum_Vitae.docx")]
    [InlineData("PROFILE-CVS/12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", "profile-cvs", "12/34/Pablo_De_Groot_Curriculum_Vitae.pdf")]
    [InlineData("application-packs/12/34/Pablo_De_Groot_Cover_Letter.pdf", "application-packs", "12/34/Pablo_De_Groot_Cover_Letter.pdf")]
    [InlineData("12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", "application-packs", "12/34/Pablo_De_Groot_Curriculum_Vitae.pdf")]
    [InlineData("other-container/12/34/CV.pdf", "application-packs", "other-container/12/34/CV.pdf")]
    public void A_stored_reference_is_resolved_in_the_container_that_wrote_it(
        string stored, string expectedContainer, string expectedBlobName)
    {
        // The pack holds both kinds at once - a chosen variant's path out of CvVariants and a
        // generated document's out of ApplicationDocuments - so the reference decides rather than
        // the caller. Signing a variant against the pack container is not an error anybody sees:
        // it is a valid URL for a blob that is not there, discovered at the upload box.
        //
        // The last case is the one worth stating plainly. A path naming some third container is
        // read as a blob name inside the pack container that happens to contain slashes, so it
        // yields a dead link and never a signature over anything outside these two - which is the
        // property ApplicationPackFile.TryBlobName is written for.
        Assert.True(Container(new ApplicationPackOptions()).Resolve(stored, out var client, out var blobName));

        Assert.Equal(expectedContainer, client.Name);
        Assert.Equal(expectedBlobName, blobName);
    }

    [Fact]
    public async Task A_variant_is_never_written_into_the_pack_container()
    {
        // The one misconfiguration the second container exists to prevent, refused at the write
        // rather than at the registration so that it holds however the store was built. Both
        // settings spelled the same is a deployment in which one candidate's CV overwrites the
        // generated document sharing its id, under a filename that no longer distinguishes them,
        // and the hash stored beside the survivor describes bytes nobody sent.
        //
        // Refusing costs a variant that stays unrendered and says so - CvVariant.IsSendable keeps
        // it out of selection - which is the recoverable half of a bad trade.
        var stored = await Store(new ApplicationPackOptions { VariantContainerName = "application-packs" })
            .StoreVariantAsync(
                new VariantFileRequest
                {
                    ProfileId = 1,
                    VariantId = 2,
                    Format = PackFormat.Pdf,
                    Content = [1, 2, 3],
                    CandidateName = "Pablo De Groot",
                });

        Assert.Null(stored);
    }

    [Fact]
    public async Task An_empty_variant_render_stores_nothing()
    {
        // The same rule as the pack half, and it has to be stated on both: a path recorded against
        // an empty blob is a row promising a CV that is not there, and CvVariant.IsRenderCurrent
        // would then let it be chosen.
        var stored = await Store().StoreVariantAsync(
            new VariantFileRequest
            {
                ProfileId = 1,
                VariantId = 2,
                Format = PackFormat.Pdf,
                Content = [],
                CandidateName = "Pablo De Groot",
            });

        Assert.Null(stored);
    }

    [Fact]
    public void One_store_serves_both_contracts_so_one_delegation_key_is_fetched()
    {
        // Two AddSingleton<TInterface, TImpl> registrations build two stores, each caching its own
        // user delegation key behind its own semaphore - twice the round trips to the account, and
        // a key retired in one still warm in the other. Both interfaces forward to the concrete
        // registration instead, and this is what says so.
        var provider = Provider(Configuration(("ApplicationPacks:serviceUri", ServiceUri)));

        var packs = provider.GetRequiredService<IApplicationPackStore>();

        Assert.Same(packs, provider.GetRequiredService<ICvVariantFileStore>());
        Assert.Same(packs, provider.GetRequiredService<ApplicationPackStore>());
    }

    [Fact]
    public void No_service_uri_registers_no_variant_renderer_either()
    {
        // The CV library stays usable as text where there is no storage: variants save, they
        // simply never become sendable, which is what CvVariant.IsSendable already says. A
        // required registration here would make "no storage account" a startup failure for a
        // feature the deployment does not have.
        var provider = Provider(Configuration());

        Assert.Null(provider.GetService<CvVariantRenderer>());
        Assert.Null(provider.GetService<ICvVariantFileStore>());
    }

    [Fact]
    public void The_variant_renderer_ships_in_the_assembly_the_functions_host_can_reference()
    {
        // The same failure as the pack store's, one feature later and not yet made: the API
        // renders a variant when somebody saves one, a pass will render them unattended, and a
        // worker cannot reference a web project. A renderer in JobPlatform.Api would be a pass
        // that stores markdown, reports success and produces no file - which is the entire output.
        var renderer = Provider(Configuration(("ApplicationPacks:serviceUri", ServiceUri)))
            .GetRequiredService<CvVariantRenderer>();

        Assert.Equal(typeof(JobsDbContext).Assembly, renderer.GetType().Assembly);
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

    [Fact]
    public void The_pack_store_is_registered_from_an_assembly_the_functions_host_can_reference()
    {
        // The failure this pins is the one nobody could see. The store used to live in
        // JobPlatform.Api, and src/JobPlatform.Ingestion references Core, Data, Ai and Documents -
        // a Functions worker cannot take a reference on a web project - so no arrangement of that
        // host's Program could register it. GenerateApplicationsFunction resolves
        // IApplicationPackStore as nullable, so the nightly pass took its "no pack store" path on
        // every unattended run: markdown stored with null paths, a success reported, and never a
        // PDF or a DOCX, which is the entire output that pass exists to produce.
        //
        // JobsDbContext stands for JobPlatform.Data rather than an assembly name in a string: the
        // compiler checks a type reference, and it is Data the Functions host already references.
        var store = Provider(Configuration(("ApplicationPacks:serviceUri", ServiceUri)))
            .GetRequiredService<IApplicationPackStore>();

        Assert.Equal(typeof(JobsDbContext).Assembly, store.GetType().Assembly);
        Assert.Equal(typeof(JobsDbContext).Assembly, typeof(ApplicationPackSetup).Assembly);
    }

    [Fact]
    public void The_functions_hosts_environment_variables_register_the_pack_store()
    {
        // The Functions worker gets its app settings as environment variables and nothing else,
        // where '__' is the section separator - so this, not an appsettings section, is the shape
        // the nightly pass's configuration actually arrives in. Read through a real environment
        // variable source rather than an in-memory dictionary spelled the same way, because the
        // mapping is the part that has to hold: infra sets one pair of names for both hosts, and
        // a host that could not read them would be storage that is configured and never written.
        var previousUri = Environment.GetEnvironmentVariable("ApplicationPacks__serviceUri");
        var previousContainer = Environment.GetEnvironmentVariable("ApplicationPacks__ContainerName");
        var previousVariants = Environment.GetEnvironmentVariable("ApplicationPacks__VariantContainerName");

        try
        {
            Environment.SetEnvironmentVariable("ApplicationPacks__serviceUri", ServiceUri);
            Environment.SetEnvironmentVariable("ApplicationPacks__ContainerName", "application-packs");
            Environment.SetEnvironmentVariable("ApplicationPacks__VariantContainerName", "profile-cvs");

            var provider = Provider(new ConfigurationBuilder().AddEnvironmentVariables().Build());

            Assert.NotNull(provider.GetService<IApplicationPackStore>());

            var options = provider.GetRequiredService<IOptions<ApplicationPackOptions>>().Value;

            Assert.Equal(ServiceUri, options.ServiceUri);
            Assert.Equal("application-packs", options.ContainerName);
            Assert.Equal("profile-cvs", options.VariantContainerName);

            var container = provider.GetRequiredService<ApplicationPackContainer>();

            Assert.Equal("application-packs", container.Packs.Name);
            Assert.Equal("profile-cvs", container.Variants.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ApplicationPacks__serviceUri", previousUri);
            Environment.SetEnvironmentVariable("ApplicationPacks__ContainerName", previousContainer);
            Environment.SetEnvironmentVariable("ApplicationPacks__VariantContainerName", previousVariants);
        }
    }

    private static ApplicationPackStore Store(ApplicationPackOptions? options = null)
    {
        var settings = options ?? new ApplicationPackOptions();

        return new ApplicationPackStore(
            Container(settings),
            Options.Create(settings),
            TimeProvider.System,
            NullLogger<ApplicationPackStore>.Instance);
    }

    private static ApplicationPackContainer Container(ApplicationPackOptions settings)
    {
        // No credential, deliberately. Every path exercised here answers before a request would
        // be made, so a test that needed one would be testing something else.
        var service = new BlobServiceClient(new Uri(ServiceUri));

        return new ApplicationPackContainer
        {
            Service = service,
            Packs = service.GetBlobContainerClient(settings.ContainerName),
            Variants = service.GetBlobContainerClient(settings.VariantContainerName),
        };
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
