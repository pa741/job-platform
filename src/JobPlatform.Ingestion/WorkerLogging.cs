using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion;

/// <summary>
/// Lets this project's own logs leave the isolated worker.
/// </summary>
/// <remarks>
/// <b>Written after fourteen hours of production telemetry contained not one line from this
/// assembly.</b> <c>ConfigureFunctionsApplicationInsights</c> registers the Application Insights
/// logger provider with a filter rule of its own - provider-scoped, no category, level Warning -
/// and <c>host.json</c> cannot reach it: that file configures the host process, and the functions
/// run in the worker. So <c>"JobPlatform": "Information"</c> there governs the host's copy of a log
/// and every <c>logger.LogInformation</c> in this assembly is dropped before it is sent.
///
/// <b>The symptom is telemetry that looks healthy.</b> The host's own
/// <c>Executing 'Functions.MatchSweepFunction'</c> pairs arrive under <c>Function.*</c>, and the
/// worker's SQL and HTTP dependencies arrive too - so a query returns rows, the connection is
/// demonstrably fine, and the only thing missing is the half a person actually wants: what the
/// sweep decided, how many judgements it asked for, and how many it discarded.
///
/// <b>A method rather than five lines in <c>Program.cs</c>, so the test exercises the shipped
/// code.</b> The first attempt at this was written inline and the test asserted a copy of it,
/// which is a test that passes on the day the two drift apart.
/// </remarks>
public static class WorkerLogging
{
    /// <summary>
    /// Removes the Application Insights provider's own filter rule, so ordinary logging
    /// configuration decides what is sent.
    /// </summary>
    /// <remarks>
    /// Matched on the provider name containing <c>ApplicationInsights</c> rather than on its full
    /// type name. The exact name is an implementation detail of a package that has renamed things
    /// before, and a match that silently stops matching restores the original fault with a test
    /// still passing - <c>WorkerLoggingFilterTests</c> asserts the outcome for that reason.
    ///
    /// <b>The caller must set the levels afterwards.</b> Removing the rule leaves the framework
    /// default, which is Information for everything - including
    /// <c>Microsoft.EntityFrameworkCore.Database.Command</c>, which would put every SQL statement
    /// the sweep runs into telemetry billed by the gigabyte.
    /// </remarks>
    public static IServiceCollection AllowApplicationInsightsInformationLogs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.Configure<LoggerFilterOptions>(options =>
        {
            var providerRules = options.Rules
                .Where(rule => rule.ProviderName is not null
                    && rule.ProviderName.Contains("ApplicationInsights", StringComparison.Ordinal))
                .ToList();

            foreach (var rule in providerRules)
            {
                options.Rules.Remove(rule);
            }
        });
    }
}
