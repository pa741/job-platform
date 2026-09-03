using Azure.Identity;
using Azure.Storage.Blobs;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JobPlatform.Data.Applications;

/// <summary>
/// Registers the pack store, or registers nothing at all.
/// </summary>
/// <remarks>
/// <b>In <c>JobPlatform.Data</c> rather than beside the endpoints that read it, because two hosts
/// register this and only one of them is the API.</b> The other is the Functions host, which
/// renders the nightly pass's documents and references Core, Data, Ai and Documents - a worker
/// cannot take a reference on a web project, so for as long as this lived in
/// <c>JobPlatform.Api</c> there was no arrangement of <c>src/JobPlatform.Ingestion/Program.cs</c>
/// that could register it. The failure that caused was silent and total:
/// <c>GenerateApplicationsFunction</c> resolves <see cref="IApplicationPackStore"/> as nullable,
/// so every unattended run took the "no pack store" path, stored its markdown with null paths and
/// reported success - and putting the rendered file where a browser can upload it is the entire
/// purpose of that pass. Data is also where it belongs on its own terms: it is where the
/// identity-based clients both hosts need already live, beside <c>CosmosClientFactory</c> and the
/// realtime feed's registration.
///
/// <b>Nothing is registered where no service URI is configured, and that is the feature.</b> A
/// deployment with no storage account still generates documents, still serves the pack, and says
/// in its <c>note</c> that no file is available - the same answer the pack already gives for a
/// posting whose documents were never written. Every consumer resolves
/// <see cref="IApplicationPackStore"/> as nullable, so an absent registration is a capability the
/// deployment does not have rather than a dependency it is missing. This is the shape
/// <c>AddAiProvider</c>, <c>AddRealtimeFeed</c> and the scraper configuration publisher all take,
/// and it is what lets the API test host and a fresh clone boot with no Azure account, no
/// credential and no container.
///
/// <b>Identity-based, exactly like every other storage caller here.</b>
/// <c>DefaultAzureCredential</c> with the configured <c>ManagedIdentityClientId</c>, which is
/// empty locally and falls back to the signed-in developer. There is no connection string and no
/// account key anywhere in this path - not for writing, and not for signing, because the
/// account-wide Blob Data Reader assignment already carries <c>generateUserDelegationKey</c>.
///
/// <b>A singleton, unlike the scraper configuration publisher.</b> That one is scoped because it
/// depends on a scoped repository; this depends on nothing scoped, owns a connection pool that one
/// instance per request would exhaust, and caches a user delegation key that a per-request
/// instance would re-fetch on every call. A singleton also survives the Functions worker's
/// per-invocation scope, where a scoped store would fetch a delegation key per document.
/// </remarks>
public static class ApplicationPackSetup
{
    /// <summary>
    /// Wires the store when storage is configured. Call once from each host's <c>Program</c>.
    /// </summary>
    /// <remarks>
    /// <b>Both spellings of the key are read.</b> The container app sets
    /// <c>ApplicationPacks__serviceUri</c> and <c>ApplicationPacks__ContainerName</c>; a host that
    /// maps environment variables in the usual way turns those into the <c>ApplicationPacks</c>
    /// section, which is what the options class binds - and that mapping is the whole of how the
    /// Functions host, whose app settings reach the worker as environment variables and nothing
    /// else, resolves the same two names. The literal double-underscore lookup is the same
    /// belt-and-braces both <c>Program</c> files already apply to <c>ScraperConfig__serviceUri</c>
    /// and <c>LandingStorage__serviceUri</c>, and it is cheap insurance against a configuration
    /// source that does not do the mapping - the failure it prevents is a deployment that has
    /// storage, is configured for it, and silently reports that no documents are available.
    ///
    /// <b>A URI that will not parse is treated as no URI.</b> Failing fast would be defensible for
    /// a database, and is what <c>Program</c> does for the SQL connection string and the Cosmos
    /// endpoint: without those there is no product. Without this there is a product missing one
    /// convenience, so a typo must not be the difference between a running API and a container
    /// that will not start.
    /// </remarks>
    public static IServiceCollection AddApplicationPacks(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ApplicationPackOptions.SectionName);

        // Case-insensitive, so the template's lower-case 'serviceUri' reaches the property.
        var serviceUri = Coalesce(
            section[nameof(ApplicationPackOptions.ServiceUri)],
            configuration[$"{ApplicationPackOptions.SectionName}__serviceUri"]);

        if (string.IsNullOrWhiteSpace(serviceUri)
            || !Uri.TryCreate(serviceUri.Trim(), UriKind.Absolute, out var endpoint))
        {
            return services;
        }

        var containerName = Coalesce(
            section[nameof(ApplicationPackOptions.ContainerName)],
            configuration[$"{ApplicationPackOptions.SectionName}__ContainerName"])
            ?? new ApplicationPackOptions().ContainerName;

        services.Configure<ApplicationPackOptions>(section);

        // A no-op wherever the section bound normally, which is every real deployment. It exists
        // for the case above where only the literal key was present: without it the options would
        // carry a blank service URI and the default container while the client below used the
        // right ones, and the two would disagree about which container a stored path names.
        services.PostConfigure<ApplicationPackOptions>(options =>
        {
            options.ServiceUri = serviceUri;
            options.ContainerName = containerName;
        });

        var managedIdentityClientId = configuration["ManagedIdentityClientId"];

        services.AddSingleton(_ =>
        {
            var credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
                ? new DefaultAzureCredential()
                : new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = managedIdentityClientId,
                });

            // The account client is kept as well as the container's: a user delegation key is
            // requested at account scope, and reaching for it later would put the endpoint back
            // into the code that signs.
            var service = new BlobServiceClient(endpoint, credential);

            return new ApplicationPackContainer(service, service.GetBlobContainerClient(containerName));
        });

        services.AddSingleton<IApplicationPackStore, ApplicationPackStore>();

        return services;
    }

    /// <summary>The first of the two that says something. Blank is nothing, not a value.</summary>
    private static string? Coalesce(string? first, string? second)
        => string.IsNullOrWhiteSpace(first) ? (string.IsNullOrWhiteSpace(second) ? null : second) : first;
}
