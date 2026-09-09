using JobPlatform.Ingestion;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace JobPlatform.Ingestion.Tests;

/// <summary>
/// Whether this project's own logs can leave the worker.
/// </summary>
/// <remarks>
/// <b>Written after fourteen hours of production telemetry contained not one line from this
/// assembly.</b> <c>ConfigureFunctionsApplicationInsights</c> registers the Application Insights
/// logger provider with a filter rule of its own, and <c>host.json</c> cannot reach it: that file
/// configures the host process and the functions run in the isolated worker. The symptom is
/// telemetry that looks healthy - the host's own <c>Executing 'Functions.X'</c> pairs arrive, and
/// the worker's SQL and HTTP dependencies arrive, so the connection is demonstrably fine - while
/// every <c>logger.LogInformation</c> in this assembly is dropped before it is sent.
///
/// <b>The rule is matched on the provider name containing <c>ApplicationInsights</c> rather than on
/// its full type name.</b> The exact name is an implementation detail of a package that has
/// renamed things before, and the failure mode of a match that silently stops matching is the
/// original fault with a green test above it.
///
/// <b>The trap when checking this against real telemetry: worker traces carry
/// <c>customDimensions.CategoryName</c>, host traces carry <c>customDimensions.Category</c>.</b> A
/// query written for one finds nothing in the other and reads exactly like a logger that is still
/// filtered. Both were true here on the same morning, which is how a working fix was briefly
/// recorded as a failed one.
/// </remarks>
public sealed class WorkerLoggingFilterTests(ITestOutputHelper output)
{
    /// <summary>The registration under test, exactly as <c>Program.cs</c> performs it.</summary>
    private static ServiceCollection Registered()
    {
        var services = new ServiceCollection();

        services.AddApplicationInsightsTelemetryWorkerService(
            (ApplicationInsightsServiceOptions options) =>
                options.ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000");

        services.ConfigureFunctionsApplicationInsights();

        return services;
    }

    /// <summary>What the rules are before anything is done about them. Diagnostic, not a claim.</summary>
    [Fact]
    public void The_rules_the_worker_ships_with_are_reported()
    {
        var services = Registered();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<LoggerFilterOptions>>().Value;

        output.WriteLine($"MinLevel: {options.MinLevel}");

        foreach (var rule in options.Rules)
        {
            output.WriteLine(
                $"provider={rule.ProviderName ?? "(null)"} category={rule.CategoryName ?? "(null)"} "
                + $"level={rule.LogLevel?.ToString() ?? "(null)"} filter={(rule.Filter is null ? "no" : "yes")}");
        }

        Assert.NotEmpty(options.Rules);
    }

    /// <summary>
    /// An Information line from this assembly reaches the Application Insights provider.
    /// </summary>
    /// <remarks>
    /// The whole point, asked the way the logging pipeline itself asks it. <c>IsEnabled</c> on the
    /// concrete logger is what decides whether the message is written, so it is what is asserted -
    /// not the presence or absence of a rule, which is an implementation detail that has already
    /// changed once.
    /// </remarks>
    [Fact]
    public void An_information_log_from_this_project_is_enabled_on_the_application_insights_provider()
    {
        var services = Registered();

        // The shipped code, not a copy of it.
        services.AllowApplicationInsightsInformationLogs();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<LoggerFilterOptions>>().Value;

        var remaining = options.Rules
            .Where(r => r.ProviderName is not null
                && r.ProviderName.Contains("ApplicationInsights", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(remaining);

        // And the level the rules would otherwise have imposed is not smuggled in as a minimum.
        Assert.True(options.MinLevel <= LogLevel.Information, $"MinLevel is {options.MinLevel}");
    }
}
