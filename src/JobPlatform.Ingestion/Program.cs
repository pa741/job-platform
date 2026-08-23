using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using JobPlatform.Core.Metrics;
using JobPlatform.Core.Parsing;
using JobPlatform.Data.Cosmos;
using JobPlatform.Data.Sql;
using JobPlatform.Ai;
using JobPlatform.Core.Enrichment;
using JobPlatform.Ingestion;
using JobPlatform.Ingestion.Curated;
using JobPlatform.Ingestion.Extraction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

var configuration = builder.Configuration;

// Empty locally, where DefaultAzureCredential falls back to the signed-in developer.
var managedIdentityClientId = configuration["ManagedIdentityClientId"];

builder.Services.Configure<CosmosOptions>(options =>
{
    options.AccountEndpoint = configuration["Cosmos:AccountEndpoint"]
        ?? throw new InvalidOperationException("Cosmos:AccountEndpoint is not configured.");
    options.DatabaseName = configuration["Cosmos:DatabaseName"] ?? "jobplatform";
    options.MetricsContainerName = configuration["Cosmos:MetricsContainerName"] ?? "metrics";
    options.ManagedIdentityClientId = managedIdentityClientId;
});

// Singleton: the client owns the connection pool. One per invocation exhausts sockets.
builder.Services.AddSingleton(provider =>
{
    var options = provider.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<CosmosOptions>>().Value;
    return CosmosClientFactory.Create(options);
});

builder.Services.AddDbContext<JobsDbContext>(options =>
{
    var connectionString = configuration["SqlConnectionString"]
        ?? throw new InvalidOperationException("SqlConnectionString is not configured.");

    options.UseSqlServer(connectionString, sql =>
    {
        // The database is serverless and auto-pauses. Waking it takes 30-60s, which is a
        // transient condition to ride out, not a failure to surface.
        sql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(20), errorNumbersToAdd: null);
        sql.CommandTimeout(120);
    });
});

builder.Services.AddSingleton(provider =>
{
    var serviceUri = configuration["LandingStorage:serviceUri"]
        ?? configuration["LandingStorage__serviceUri"]
        ?? throw new InvalidOperationException("LandingStorage:serviceUri is not configured.");

    var containerName = configuration["LandingContainerName"] ?? "jobs-landing";

    var credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
        ? new DefaultAzureCredential()
        : new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId,
        });

    return new BlobServiceClient(new Uri(serviceUri), credential)
        .GetBlobContainerClient(containerName);
});

// Registers a Kernel and an IDocumentExtractor only when Ai:Provider is anthropic and a
// key is present. Anything else registers nothing and does not throw - a missing environment
// variable must not take down an ingest that has nothing to do with AI.
builder.Services.AddAiProvider(configuration);

// The producer is registered under exactly the same condition as the consumer it feeds.
// Without it the pipeline receives a null queue and writes nothing, so an unconfigured
// deployment never accumulates work for a model that will never run.
if (builder.Services.Any(d => d.ServiceType == typeof(IDocumentExtractor)))
{
    builder.Services.AddSingleton(provider =>
    {
        var serviceUri = configuration["AzureWebJobsStorage:queueServiceUri"]
            ?? configuration["AzureWebJobsStorage__queueServiceUri"]
            ?? throw new InvalidOperationException(
                "AzureWebJobsStorage:queueServiceUri is not configured, but an AI provider is. "
                + "The extraction queue lives on the host storage account.");

        var credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = managedIdentityClientId,
            });

        return new QueueServiceClient(new Uri(serviceUri), credential)
            .GetQueueClient(ExtractionQueue.Name);
    });

    builder.Services.AddSingleton<IExtractionQueue, StorageExtractionQueue>();
}

// A second container on the same account. Wrapped rather than registered as another
// BlobContainerClient, because two registrations of one type resolve by whichever was last
// and the failure - the export writing into the landing container - would be silent, and
// would be exactly the write the scoped RBAC grant exists to prevent.
builder.Services.AddSingleton(provider =>
{
    var serviceUri = configuration["LandingStorage:serviceUri"]
        ?? configuration["LandingStorage__serviceUri"]
        ?? throw new InvalidOperationException("LandingStorage:serviceUri is not configured.");

    var containerName = configuration["CuratedContainerName"] ?? "jobs-curated";

    var credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
        ? new DefaultAzureCredential()
        : new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId,
        });

    return new CuratedContainer(
        new BlobServiceClient(new Uri(serviceUri), credential).GetBlobContainerClient(containerName));
});

builder.Services.AddScoped<CuratedExporter>();

builder.Services.AddSingleton<JobCsvParser>();
builder.Services.AddSingleton<MetricsCalculator>();
builder.Services.AddScoped<MetricsRepository>();
builder.Services.AddScoped<JobPostingRepository>();
builder.Services.AddScoped<IngestionPipeline>();

builder.Build().Run();
