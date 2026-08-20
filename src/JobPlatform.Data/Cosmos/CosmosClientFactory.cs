using System.Text.Json;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace JobPlatform.Data.Cosmos;

public static class CosmosClientFactory
{
    /// <summary>
    /// Builds the singleton client. Registering it once matters: the client owns the
    /// connection pool, and creating one per invocation exhausts sockets under load.
    /// </summary>
    /// <remarks>
    /// Serialization is pinned to System.Text.Json with a camelCase policy. Cosmos
    /// requires the document key to be literally <c>id</c>, and the container is
    /// partitioned on <c>/searchTerm</c> — the SDK's default PascalCase output would
    /// satisfy neither.
    /// </remarks>
    /// <param name="applicationName">
    /// Reported to Cosmos and shown in its metrics, so ingestion writes and API reads can be
    /// told apart when diagnosing RU consumption. Defaults to the ingestion name for callers
    /// that predate the API.
    /// </param>
    public static CosmosClient Create(CosmosOptions options, string applicationName = "job-platform-ingestion")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AccountEndpoint);

        var clientOptions = new CosmosClientOptions
        {
            ApplicationName = applicationName,
            // The ingest is a short burst of writes from a serverless host; Gateway mode
            // avoids the direct-mode port range and connection warm-up cost.
            ConnectionMode = ConnectionMode.Gateway,
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            },
        };

        // The account has local auth disabled, so this is the only way in.
        var credential = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = options.ManagedIdentityClientId,
            });

        return new CosmosClient(options.AccountEndpoint, credential, clientOptions);
    }
}
