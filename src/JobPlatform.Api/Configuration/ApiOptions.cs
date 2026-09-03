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
    /// What one MCP client may spend a minute, sustained.
    /// </summary>
    /// <remarks>
    /// An order of magnitude below <see cref="ReadsPerMinute"/>, deliberately, and unchanged when
    /// the tool surface went from six to fourteen. A browser makes a burst of calls when a page
    /// opens and then stops; an agent can loop. The tools read Azure SQL, which is billed on
    /// wall-clock time online against a monthly grant one daily ingest half-consumes, so a client
    /// polling every few seconds would exhaust it and pause the database for everything else.
    ///
    /// <b>The arithmetic still fits, which is why the number did not move.</b> The real bound on
    /// this loop is <c>SubmissionLimits.MaxSubmittedPerDay</c>, and twenty-five applications at a
    /// few dozen tool calls each is under a thousand calls in a day - well under one a minute
    /// averaged over one. What grew is not the total but its <i>shape</i>: an application is a
    /// pack read, a field resolution per input on somebody's form and then a write, all inside a
    /// few seconds, followed by minutes of a browser doing something a database never hears
    /// about. <see cref="McpBurst"/> is what absorbs that, and <c>RateLimitSetup.McpPolicy</c> is
    /// a token bucket rather than a fixed window for the same reason.
    /// </remarks>
    public int McpRequestsPerMinute { get; set; } = 20;

    /// <summary>
    /// How many calls one MCP client may make back to back before the sustained rate binds.
    /// </summary>
    /// <remarks>
    /// <b>Sized to one application rather than to a minute.</b> Forty covers the handshake a
    /// stateless transport repeats per connection, the pack read, thirty-odd field resolutions -
    /// <c>SubmissionLimits.MaxSubmittedFieldCount</c> calls a hundred fields "well above the
    /// longest real application form" - and the submission and its event at the end. Past that a
    /// client is filling in a form larger than any this corpus has and can afford to wait a few
    /// seconds a field.
    ///
    /// <b>What this exists to prevent is a refusal landing between the send and the record.</b>
    /// The loop's writes come last: <c>create_submission</c> and <c>record_event</c> run
    /// <i>after</i> the browser has posted the form, so a limiter that refuses the twenty-first
    /// call of a form leaves an application that exists in the world and not in the log - the one
    /// state this pipeline cannot recover from, because every later decision reads the log rather
    /// than the world. A retry fixes it, and under a fixed window that retry waits for a window
    /// boundary it cannot see.
    /// </remarks>
    public int McpBurst { get; set; } = 40;

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
