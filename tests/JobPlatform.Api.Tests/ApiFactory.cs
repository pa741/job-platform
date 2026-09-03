using JobPlatform.Core.Ai;
using JobPlatform.Core.Model;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Cosmos;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Boots the real API against SQLite and a stubbed Cosmos reader.
/// </summary>
/// <remarks>
/// SQLite rather than a mock repository, so the LINQ in
/// <see cref="JobPostingQueryRepository"/> actually has to translate - the same reasoning as
/// the existing <c>JobPostingRepositoryTests</c>. Cosmos is stubbed instead, because the
/// emulator is not a reasonable CI dependency and this suite's whole point is that it runs
/// with no Azure account and no credentials, exactly like the rest of the repository's tests.
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeMetricsSource Metrics { get; } = new();

    public FakeAiCallSource AiCalls { get; } = new();

    /// <summary>Set before the first request to open read endpoints without a token.</summary>
    public bool AllowAnonymousReads { get; init; } = true;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        _connection.Open();

        // Every suite in this project builds its host through here, so turning the container's own
        // checks on once covers all of them.
        //
        // Neither ordinary defence sees a captured dependency. Production leaves ValidateScopes
        // off, so a singleton holding a scoped service does not fail - it quietly keeps one
        // request's object for the life of the process. And WebApplicationFactory does not turn
        // the checks on by default either: registering the form-field resolver as a singleton over
        // a scoped IAiCallLog left all 313 tests here green, which is how that fault was found by
        // experiment rather than by the suite.
        //
        // ValidateOnBuild is the half that matters. ValidateScopes alone only catches a service
        // some test actually resolves; ValidateOnBuild walks every registration, so the next one
        // is caught in whichever service nothing happens to exercise.
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Program.cs fails fast without these; the registrations below replace whatever
            // they produce, so the values only have to be well-formed.
            ["SqlConnectionString"] = "Server=unused;Database=unused;",
            ["Cosmos:AccountEndpoint"] = "https://unused.documents.azure.com:443/",
            ["Api:AllowAnonymousReads"] = AllowAnonymousReads ? "true" : "false",
            ["RateLimit:Enabled"] = "false",
            // Output caching would make a test's second request serve the first one's body,
            // which silently hides whatever the second request was meant to prove.
            ["Cache:Enabled"] = "false",
        }));

        var host = base.CreateHost(builder);

        // After the host exists, not inside ConfigureServices: building a service provider
        // mid-registration creates a second container whose singletons are not the ones the
        // application ends up using.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
        db.Database.EnsureCreated();

        // EnsureCreated leaves the concept tables empty; the repository needs the vocabulary
        // projected into them before it can resolve anything.
        ConceptSeeder.SeedAsync(db).GetAwaiter().GetResult();

        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            RemoveDbContextRegistrations(services);

            // Tracking left at the default, matching Program.cs. It was NoTracking here for as
            // long as it was there, which is exactly why this suite could not see that the API's
            // four read-then-mutate repositories were saving nothing: the fixture reproduced the
            // fault rather than catching it.
            services.AddDbContext<JobsDbContext>(options => options.UseSqlite(_connection));

            // The Cosmos client would try to authenticate on construction, so both it and the
            // repository that wraps it are replaced outright.
            services.RemoveAll<Microsoft.Azure.Cosmos.CosmosClient>();
            services.RemoveAll<MetricsQueryRepository>();
            services.RemoveAll<IMetricsSource>();
            services.AddSingleton<IMetricsSource>(Metrics);

            services.RemoveAll<AiCallQueryRepository>();
            services.RemoveAll<IAiCallSource>();
            services.AddSingleton<IAiCallSource>(AiCalls);

            // Left absent rather than faked: every consumer resolves it as nullable and
            // skips, which is the degraded mode a host without Cosmos is supposed to have.
            services.RemoveAll<IAiCallLog>();

            // The same for the disclosure log the MCP tools write to. Both are Cosmos-backed and
            // both are optional to their callers, so a host with no Cosmos has neither - and
            // leaving either registered fails DI validation at startup rather than at first use,
            // which takes down every test in this suite at once.
            services.RemoveAll<IDisclosureLog>();

            services.RemoveAll<Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck>();

        });
    }

    /// <summary>
    /// Strips every trace of the production DbContext registration.
    /// </summary>
    /// <remarks>
    /// Removing <c>DbContextOptions</c> alone is not enough. EF also registers an
    /// <c>IDbContextOptionsConfiguration</c> per <c>AddDbContext</c> call, and those
    /// accumulate rather than replace - so the production UseSqlServer callback would still
    /// run alongside the test's UseSqlite one, producing options with two providers and the
    /// error "Only a single database provider can be registered in a service provider".
    /// </remarks>
    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var doomed = services
            .Where(descriptor =>
            {
                var type = descriptor.ServiceType;

                if (type == typeof(JobsDbContext) || type == typeof(DbContextOptions<JobsDbContext>))
                {
                    return true;
                }

                return type.IsGenericType
                    && type.GetGenericArguments().Contains(typeof(JobsDbContext))
                    && type.Name.Contains("DbContextOptionsConfiguration", StringComparison.Ordinal);
            })
            .ToList();

        foreach (var descriptor in doomed)
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>Seeds postings through the real ingestion path, so rows are shaped exactly
    /// as production writes them.</summary>
    public async Task SeedAsync(
        string searchTerm, DateOnly scrapeDate, params JobPosting[] postings)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
        var repository = new JobPostingRepository(db, NullLogger<JobPostingRepository>.Instance);

        await repository.IngestAsync(
            new ScrapeRunContext
            {
                BlobPath = $"jobs/{searchTerm}_{scrapeDate:yyyy-MM-dd}T09-00-00Z.csv",
                SearchTerm = searchTerm,
                ScrapedAtUtc = new DateTimeOffset(scrapeDate.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero),
            },
            postings,
            postings.Length,
            invalidRows: 0);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
