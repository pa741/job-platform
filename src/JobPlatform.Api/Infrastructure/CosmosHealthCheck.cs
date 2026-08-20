using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using JobPlatform.Data.Cosmos;

namespace JobPlatform.Api.Infrastructure;

/// <summary>
/// Readiness: can we reach the metrics container.
/// </summary>
/// <remarks>
/// Cosmos is checked and Azure SQL deliberately is not. SQL here is serverless, billed on
/// wall-clock time online, and auto-pauses when idle; a readiness probe polling it would keep
/// it awake around the clock and spend the whole monthly free grant on health checks, at
/// which point the platform pauses the database until the next month. The API also does not
/// need SQL to serve the dashboard - every metric comes from Cosmos - so Cosmos is the honest
/// readiness dependency.
///
/// Reads container properties rather than issuing a query: it is a metadata call, so it
/// verifies endpoint, credential and authorisation without consuming request units against
/// the container's throughput.
/// </remarks>
public sealed class CosmosHealthCheck(CosmosClient client, IOptions<CosmosOptions> options)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        try
        {
            var container = client.GetContainer(settings.DatabaseName, settings.MetricsContainerName);
            await container.ReadContainerAsync(cancellationToken: cancellationToken);

            return HealthCheckResult.Healthy("Cosmos reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cosmos unreachable.", ex);
        }
    }
}
