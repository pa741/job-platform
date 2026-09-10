using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Metrics;
using JobPlatform.Core.Parsing;
using JobPlatform.Data.Applications;
using JobPlatform.Data.Cosmos;
using JobPlatform.Data.Realtime;
using JobPlatform.Data.Sql;
using JobPlatform.Ai;
using JobPlatform.Core.Enrichment;
using JobPlatform.Ingestion;
using JobPlatform.Ingestion.Ats;
using JobPlatform.Ingestion.Curated;
using JobPlatform.Ingestion.Functions;
using JobPlatform.Ingestion.Extraction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Every log this project writes was being filtered out before it left the worker, and nothing
// said so. See WorkerLogging for the whole account: host.json configures the host process, the
// functions run in the isolated worker, and the Application Insights provider carries a filter
// rule there that no setting reaches.
builder.Services.AllowApplicationInsightsInformationLogs();

// What the host.json line says, said again where the worker can hear it. Without this the
// removal above leaves the worker on the framework default, which is Information for everything
// - including the EF Core command logger, which would put every SQL statement the sweep runs
// into telemetry billed by the gigabyte.
builder.Logging
    .SetMinimumLevel(LogLevel.Information)
    .AddFilter("Microsoft", LogLevel.Warning)
    .AddFilter("Azure", LogLevel.Warning)
    .AddFilter("System", LogLevel.Warning)
    .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning)
    .AddFilter("JobPlatform", LogLevel.Information);

var configuration = builder.Configuration;

// Empty locally, where DefaultAzureCredential falls back to the signed-in developer.
var managedIdentityClientId = configuration["ManagedIdentityClientId"];

// Off unless a deployment asks. Turning it on stores the prompt of a failed call so the
// failure can be replayed, and an assessment or profile prompt is somebody's employment
// history - a decision to make deliberately, per deployment. The sink still keeps one only
// for a call that lost something, and no list endpoint returns it.
builder.Services.Configure<AiLedgerOptions>(options =>
    options.RecordPrompts =
        bool.TryParse(configuration[$"{AiLedgerOptions.SectionName}:RecordPrompts"], out var record)
        && record);

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

// Registers a Kernel, an IDocumentExtractor and an ICandidacyAssessor only when
// Ai:Provider is azureopenai and an endpoint is configured. Anything else registers nothing
// and does not throw - a missing environment variable must not take down an ingest that has
// nothing to do with AI. There is no key to check for: Azure OpenAI authenticates with the
// same managed identity everything else here uses.
builder.Services.AddAiProvider(configuration);

// The batch path, registered independently of the interactive one. A deployment can have
// either, both or neither; both is the intended arrangement - Azure for profiles, where a
// person is waiting and personal data stays in the tenant, and OpenAI's batch endpoint for job
// adverts, where nobody is waiting and the rate pool is separate.
builder.Services.AddOpenAiBatchProvider(configuration);

// The producer is registered under exactly the same condition as the consumer it feeds.
// Without it the pipeline receives a null queue and writes nothing, so an unconfigured
// deployment never accumulates work for a model that will never run.
if (builder.Services.Any(d => d.ServiceType == typeof(IDocumentExtractor)))
{
    builder.Services.AddSingleton(provider =>
    {
        // Identity-based host connections come in two shapes and the Functions host accepts
        // both: an explicit `__queueServiceUri`, or an `__accountName` the host expands per
        // service. infra/modules/functionapp.bicep sets the account name, so reading only the
        // URI form meant this factory threw the moment an AI provider was configured - which
        // is the first time it had ever run, because the queue is registered only alongside an
        // extractor and there had never been one. The backfill endpoint answered 500 rather
        // than the "no provider configured" it was written to answer.
        var serviceUri = configuration["AzureWebJobsStorage:queueServiceUri"]
            ?? configuration["AzureWebJobsStorage__queueServiceUri"];

        if (string.IsNullOrWhiteSpace(serviceUri))
        {
            var accountName = configuration["AzureWebJobsStorage:accountName"]
                ?? configuration["AzureWebJobsStorage__accountName"];

            serviceUri = string.IsNullOrWhiteSpace(accountName)
                ? throw new InvalidOperationException(
                    "AzureWebJobsStorage is configured with neither queueServiceUri nor accountName, "
                    + "but an AI provider is. The extraction queue lives on the host storage account.")
                : $"https://{accountName}.queue.core.windows.net";
        }

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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MetricsCalculator>();
builder.Services.AddScoped<MetricsRepository>();

// The AI call ledger. Registered here rather than inside AddAiProvider, because it is the
// consumers of AI that report to it and they resolve it as nullable: a deployment with no
// provider registers no assessor and simply never calls this, while one with a provider and no
// Cosmos still makes its model calls. Diagnostics that can take down what they observe are
// worse than none.
builder.Services.AddScoped<IAiCallLog, AiCallLogRepository>();
builder.Services.AddScoped<JobPostingRepository>();
builder.Services.AddScoped<IngestionPipeline>();

// The match sweep. Registered unconditionally, unlike the extraction queue above: its scoring
// pass is pure arithmetic over the concept graph and needs no model at all, so a deployment
// with no AI provider still produces ranked matches - just without the judgement layer that
// ICandidacyAssessor would add.
builder.Services.AddScoped<CandidateProfileRepository>();
builder.Services.AddScoped<JobMatchRepository>();
builder.Services.AddScoped<EmbeddingRepository>();

// What each candidate's nights are allowed to buy. Both unattended passes read it - the sweep for
// its judgement budget, threshold and recent reservation, the generation pass for its drafting
// bounds - and both resolve it as nullable, so this registration is what turns configuration on
// rather than what makes the functions activate. Without it they run every profile on
// PipelineSettings.Default, which is the behaviour that shipped before the table existed.
//
// Registered unconditionally, like the match sweep above it: there is nothing to check for. It is
// the same DbContext and the same managed identity, and a deployment whose migration has not
// reached the table yet is handled where it is read, not by leaving it out here.
builder.Services.AddScoped<PipelineSettingsRepository>();

// The realtime feed. Registers nothing when no endpoint is configured, so every consumer
// resolves IRealtimeFeed as nullable and the dashboard falls back to polling.
builder.Services.AddRealtimeFeed(builder.Configuration);
builder.Services.AddScoped<ExtractionBatchRepository>();
builder.Services.AddScoped<PostingExtractionWriter>();

// The nightly generation pass, which is what puts documents in front of the apply loop. The
// repository is registered here rather than only in the API because this host now writes drafts
// too - without it the function is discovered and then fails to activate, which is a runtime
// fault no test sees, because the tests construct the function directly.
//
// Bound unconditionally, like the match sweep and for the same kind of reason: the options carry
// the batch cap, and a deployment that has not configured them should get the defaults rather
// than an unbounded pass. Setting DocumentsPerNight to 0 switches the pass off without a deploy,
// which is the control worth having when the number is a bill.
builder.Services.AddScoped<ApplicationDocumentRepository>();

// Apply-link recovery. The board clients bring their own HttpClient registrations and their own
// options; the repository and the pass's bounds are registered here beside the other scoped
// repositories, because a function that is discovered and then cannot be activated fails only when
// the timer fires - and this one fires at night, which is the worst place for a fault to be found.
builder.Services.AddAtsBoardClients(builder.Configuration);
builder.Services.AddScoped<EmployerAtsBoardRepository>();
builder.Services.Configure<ApplyLinkRecoveryOptions>(
    builder.Configuration.GetSection(ApplyLinkRecoveryOptions.SectionName));
builder.Services.Configure<ApplicationGenerationOptions>(
    builder.Configuration.GetSection(ApplicationGenerationOptions.SectionName));

// Where those rendered files go. The same call the API makes, from JobPlatform.Data, because a
// Functions worker cannot reference a web project: while the store lived in JobPlatform.Api this
// host could not register it at all, IApplicationPackStore resolved null on every invocation, and
// the pass took its "no pack store" path - storing markdown with null paths and reporting success
// while never producing a PDF or a DOCX. Nothing said so, because this pass runs unattended.
//
// Registers nothing where ApplicationPacks__serviceUri is absent or will not parse, exactly like
// the realtime feed and the AI provider above: a deployment with no storage account still writes
// the markdown, which is the record. The keys are read from the section and from their literal
// double-underscore spelling, so the app settings infra sets reach it however the host maps them.
builder.Services.AddApplicationPacks(configuration);

builder.Build().Run();
