using Azure.Identity;
using Azure.Storage.Blobs;
using JobPlatform.Core.Metrics;
using JobPlatform.Core.Parsing;
using JobPlatform.Data.Cosmos;
using JobPlatform.Data.Sql;
using JobPlatform.Ingestion;
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

builder.Services.AddSingleton<JobCsvParser>();
builder.Services.AddSingleton<MetricsCalculator>();
builder.Services.AddScoped<MetricsRepository>();
builder.Services.AddScoped<JobPostingRepository>();
builder.Services.AddScoped<IngestionPipeline>();

builder.Build().Run();
