using Azure.Identity;
using Azure.Storage.Blobs;
using JobPlatform.Ai;
using JobPlatform.Ai.Applications;
using JobPlatform.Api.Configuration;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Submissions;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Features.Applications;
using JobPlatform.Api.Features.Mcp;
using JobPlatform.Api.Features.Searches;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Data.Cosmos;
using JobPlatform.Data.Realtime;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Local overrides, gitignored. Loaded explicitly rather than relying on the environment name
// so a developer gets the same file whether they run under Development or no environment at
// all. Real secrets belong in user secrets, not here - see appsettings.Local.example.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var configuration = builder.Configuration;

// Empty locally, where DefaultAzureCredential falls back to the signed-in developer.
var managedIdentityClientId = configuration["ManagedIdentityClientId"];

var apiOptions = configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
var cacheOptions = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
var rateLimitOptions = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.Configure<ApiOptions>(configuration.GetSection(ApiOptions.SectionName));
builder.Services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));
builder.Services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.Configure<McpOptions>(configuration.GetSection(McpOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddProblemDetails();

// ---------------------------------------------------------------------------
// Data. Both connections are identity-based; there is no secret in either path.
// ---------------------------------------------------------------------------

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

// Singleton: the client owns the connection pool. One per request exhausts sockets.
builder.Services.AddSingleton(provider =>
{
    var options = provider.GetRequiredService<IOptions<CosmosOptions>>().Value;
    return CosmosClientFactory.Create(options, applicationName: "job-platform-api");
});

builder.Services.AddDbContext<JobsDbContext>(options =>
{
    var connectionString = configuration["SqlConnectionString"]
        ?? throw new InvalidOperationException("SqlConnectionString is not configured.");

    options.UseSqlServer(connectionString, sql =>
    {
        // The database auto-pauses and takes 30-60s to wake, so the first request after an
        // idle period must ride that out rather than surface it as a failure.
        //
        // Tighter than the ingest function's settings, deliberately. That is a once-a-day
        // batch where waiting three minutes costs nothing; this is an interactive request
        // with a caller on the other end. The connection string already carries a 60s connect
        // timeout, which alone covers a cold start, so the retries here are for genuine
        // transient faults rather than for the wake - and are bounded so a caller cannot be
        // left hanging for minutes.
        sql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
        sql.CommandTimeout(60);
    });

    // The API never writes to SQL, so tracking would only cost memory and change detection.
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});

builder.Services.AddScoped<JobPostingQueryRepository>();
builder.Services.AddScoped<CandidateProfileRepository>();
builder.Services.AddScoped<ScraperSearchRepository>();
builder.Services.AddScoped<JobMatchRepository>();
builder.Services.AddScoped<SubmissionRepository>();

// The apply loop's three stores, scoped beside the submission one they are read with. All four
// are read in a single tool call - the queue, the answers a form is filled from, the questions
// waiting on a person, and the run doing it - so one DbContext per request serves all of them
// and a singleton anywhere here would be a DbContext shared across concurrent requests.
builder.Services.AddScoped<FormAnswerRepository>();
builder.Services.AddScoped<OpenQuestionRepository>();
builder.Services.AddScoped<RunRepository>();

// The realtime feed. Registers nothing when no endpoint is configured, so every consumer
// resolves IRealtimeFeed as nullable and the dashboard falls back to polling.
builder.Services.AddRealtimeFeed(builder.Configuration);
builder.Services.AddScoped<ApplicationDocumentRepository>();
builder.Services.AddScoped<MetricsQueryRepository>();
builder.Services.AddScoped<AiCallQueryRepository>();
builder.Services.AddScoped<IAiCallSource>(sp => sp.GetRequiredService<AiCallQueryRepository>());

// The write side too: profile extraction runs here, in the request, and is the one AI call
// somebody is waiting on. Consumers resolve it as nullable, so a host without it still
// makes the call - which is what the API test host relies on.
builder.Services.AddScoped<IAiCallLog, AiCallLogRepository>();
builder.Services.AddScoped<IMetricsSource>(sp => sp.GetRequiredService<MetricsQueryRepository>());

builder.Services.AddAiProvider(configuration);

// Registered whether or not that call found a provider, which is the one place in this file
// where an AI service is unconditional. Three of the resolver's four stages are lookups over the
// allowlist, over what the candidate has already typed and over what the same question resolved
// to before; folding it into AddAiProvider would take those down with the model, so a missing
// environment variable would stop a candidate's own stored answers from being found. The fourth
// stage abstains when no Kernel was registered - see FormFieldResolverRegistration.
//
// Registered scoped here rather than left to that call's singleton, which is the substitution its
// TryAdd exists to allow. The resolver takes an optional IAiCallLog so a resolution that consults
// the model leaves a record like every other call site, and this host registers that log scoped -
// so a singleton resolver is a singleton consuming a scoped service. Under Development's
// ValidateOnBuild the provider then refuses to build at all, and in production, where that
// validation is off, it would capture one request's log for the life of the process. Nothing in
// the resolver is worth keeping between requests: the Kernel it calls through is the singleton,
// and it is injected either way.
builder.Services.AddScoped<IFormFieldResolver, FormFieldResolver>();
builder.Services.AddFormFieldResolver();

// The agent surface. An MCP server over the repositories above, behind the same Entra
// validation and the same authorisation boundary - see Features/Mcp.
builder.Services.AddMcpFeature();

// ---------------------------------------------------------------------------
// Rendered application documents, in a container of their own.
// ---------------------------------------------------------------------------

// Registers nothing when ApplicationPacks:serviceUri is absent or will not parse, exactly as the
// scraper publisher below and the realtime feed above do: generation still writes the markdown,
// which is the record, and the pack says no file is available rather than the API refusing to
// start over a container it does not need. The section is bound inside that call rather than
// here, so a deployment with no storage carries no half-configured options either.
builder.Services.AddApplicationPacks(configuration);

// ---------------------------------------------------------------------------
// The scraper's configuration, published to a blob it reads.
// ---------------------------------------------------------------------------

// Registered only when a service uri is present. Nothing here is required for the API to work:
// with no storage configured the endpoints still store searches and simply say the scraper has
// not been told, and the scraper falls back to its own config.yaml. That is the same "not here
// invites a fallback" shape the AI provider and the realtime feed both take, and it is what
// lets the test host and a fresh clone boot with no storage account at all.
var scraperConfigServiceUri = configuration["ScraperConfig:serviceUri"]
    ?? configuration["ScraperConfig__serviceUri"];

if (!string.IsNullOrWhiteSpace(scraperConfigServiceUri))
{
    builder.Services.AddSingleton(_ =>
    {
        var containerName = configuration["ScraperConfigContainerName"] ?? "scraper-config";

        var credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = managedIdentityClientId,
            });

        return new ScraperConfigContainer(
            new BlobServiceClient(new Uri(scraperConfigServiceUri), credential)
                .GetBlobContainerClient(containerName));
    });

    builder.Services.AddScoped<ScraperConfigPublisher>();
}

// ---------------------------------------------------------------------------
// Cross-cutting
// ---------------------------------------------------------------------------

builder.Services.AddApiAuthentication(configuration, apiOptions);
builder.Services.AddApiOutputCache(cacheOptions);
builder.Services.AddApiRateLimiting(rateLimitOptions);
builder.Services.AddApiOpenApi();

builder.Services.AddHealthChecks()
    .AddCheck<CosmosHealthCheck>("cosmos");

if (apiOptions.AllowedOrigins.Length > 0)
{
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
        .WithOrigins(apiOptions.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

// Container Apps terminates TLS at its ingress and forwards the caller's address. Without
// this the rate limiter would see the ingress address for every request and partition the
// entire internet into one bucket.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The ingress is not in a known network range we can enumerate, and it is the only thing
    // that can reach the container, so the default known-proxy restriction is cleared.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (apiOptions.AllowedOrigins.Length > 0)
{
    app.UseCors();
}

if (rateLimitOptions.Enabled)
{
    app.UseRateLimiter();
}

if (cacheOptions.Enabled)
{
    app.UseOutputCache();
}

app.UseAuthentication();
app.UseAuthorization();

if (apiOptions.EnableApiExplorer || app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapApi();

app.Run();

/// <summary>Named so the integration tests can reference it via WebApplicationFactory.</summary>
public partial class Program;
