namespace JobPlatform.Data.Cosmos;

public sealed class CosmosOptions
{
    public const string SectionName = "Cosmos";

    /// <summary>Account endpoint, e.g. <c>https://cosmos-jobplatform.documents.azure.com:443/</c>.
    /// The account has local auth disabled, so there is no key to configure.</summary>
    public string AccountEndpoint { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "jobplatform";

    public string MetricsContainerName { get; set; } = "metrics";

    /// <summary>
    /// The AI call ledger. Separate from metrics because that container is partitioned by
    /// search term and a model call has no search term.
    /// </summary>
    public string AiCallsContainerName { get; set; } = "aiCalls";

    /// <summary>Client id of the user-assigned managed identity. Empty locally, where
    /// <c>DefaultAzureCredential</c> falls back to the signed-in developer.</summary>
    public string? ManagedIdentityClientId { get; set; }
}
