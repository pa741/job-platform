namespace JobPlatform.Api.Configuration;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// Lets read endpoints be called without a token.
    /// </summary>
    /// <remarks>
    /// An iteration convenience, not a deployment mode: it exists so the React dashboard can
    /// be built against a running API before Entra app registrations are sorted out. It never
    /// opens <c>/me</c>, which is meaningless without a principal.
    /// </remarks>
    public bool AllowAnonymousReads { get; set; }

    /// <summary>Origins allowed to call the API from a browser, e.g. the Static Web App.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Serves the OpenAPI document and the Scalar UI. Off in production by default.</summary>
    public bool EnableApiExplorer { get; set; }
}

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Cache lifetimes, in seconds.
    /// </summary>
    /// <remarks>
    /// These are a cost control before they are a performance one. Every uncached posting
    /// request wakes a serverless database billed by wall-clock second against a monthly
    /// grant, so the TTL is what stands between an open dashboard tab and an exhausted
    /// grant. They are configuration rather than constants so they can be tuned against real
    /// traffic without a code deploy.
    /// </remarks>
    public int PostingsSeconds { get; set; } = 30;

    public int MetricsSeconds { get; set; } = 60;

    /// <summary>Facets change at most once a day, when the scraper runs.</summary>
    public int FacetsSeconds { get; set; } = 300;

    public bool Enabled { get; set; } = true;
}

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public int ReadsPerMinute { get; set; } = 120;

    /// <summary>
    /// What one MCP client may spend a minute.
    /// </summary>
    /// <remarks>
    /// An order of magnitude below <see cref="ReadsPerMinute"/>, deliberately. A browser makes a
    /// burst of calls when a page opens and then stops; an agent can loop. The tools read Azure
    /// SQL, which is billed on wall-clock time online against a monthly grant one daily ingest
    /// half-consumes, so a client polling every few seconds would exhaust it and pause the
    /// database for everything else. Asking "what changed" once a day is what this is sized for.
    /// </remarks>
    public int McpRequestsPerMinute { get; set; } = 20;

    public bool Enabled { get; set; } = true;
}
