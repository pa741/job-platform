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

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>
    /// Application principals that act for a candidate, keyed by directory object id.
    /// </summary>
    /// <remarks>
    /// <b>An app-only token names software, never a person.</b> A client running unattended
    /// authenticates with its own credential, so the <c>oid</c> on its token is a service
    /// principal's and matches no profile - every tool would answer "this candidate has no
    /// profile yet" against a pipeline that is in fact full. This is what says whose pipeline
    /// such a principal acts on.
    ///
    /// <b>Configuration rather than a tool argument, and that is the whole point.</b> The rule
    /// in <c>SubmissionTools</c> is that no tool takes a profile id, because a tool's arguments
    /// are named by a model and an unused <c>profileId</c> is exactly what a model would
    /// helpfully fill in. The mapping keeps that intact: identity still arrives with the token,
    /// through one indirection an operator wrote and no caller can name.
    ///
    /// <b>Scoped to this surface, not to <c>CallerIdentity</c>.</b> Resolving it there would let
    /// an app-only token act as the candidate across every route the API serves. Here it reaches
    /// the six tools and nothing else, so the app role's breadth - the API has one authenticated
    /// policy and no per-scope discrimination - buys access to a surface that still resolves to
    /// nobody everywhere else.
    ///
    /// Both halves are directory object ids. Neither is a secret, and both are tenant
    /// identifiers, so they are supplied by deployment configuration rather than committed.
    /// </remarks>
    public Dictionary<string, string> AppPrincipals { get; set; } = [];
}
