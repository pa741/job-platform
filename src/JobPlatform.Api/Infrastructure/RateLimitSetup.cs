using System.Threading.RateLimiting;
using JobPlatform.Api.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace JobPlatform.Api.Infrastructure;

public static class RateLimitSetup
{
    public const string ReadPolicy = "reads";

    /// <summary>
    /// The agent surface's own budget, kept apart from the dashboard's.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="ReadPolicy"/>, and that is the point.</b> A client polls differently
    /// from a browser and must not be able to exhaust the budget the dashboard shares - the
    /// dashboard is what a person uses to find out that something is wrong.
    ///
    /// <b>A token bucket rather than a fixed window, which is the one thing the apply loop
    /// changed here.</b> The budget did not move - see <c>RateLimitOptions.McpRequestsPerMinute</c>
    /// for the arithmetic that says it still fits - but a surface of fourteen tools driven by a
    /// browser filling in forms spends that budget in bursts rather than evenly, and a fixed
    /// window turns a burst into a refusal at a boundary the client cannot see. Refusing the
    /// twenty-first call of one application is not a slowed-down client: the writes come last, so
    /// it leaves the application sent and unrecorded, and the wait before a retry can be almost a
    /// whole window. A bucket refuses the same number of calls over any minute and refills
    /// continuously, so the retry is seconds away instead of a boundary away.
    /// </remarks>
    public const string McpPolicy = "mcp";

    /// <summary>
    /// How often the MCP bucket refills: a tenth of a minute.
    /// </summary>
    /// <remarks>
    /// Short deliberately. The period is the worst wait a refused client faces, and the call most
    /// likely to be refused is the one recording that a form has already gone. A minute-long
    /// period would spend the same permits and make that wait sixty times longer for nothing.
    /// A configured rate that is not a multiple of ten rounds down to the nearest one - a permit
    /// or two a minute, and not worth a second setting to express exactly.
    /// </remarks>
    private static readonly TimeSpan McpReplenishment = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Per-caller rate limits.
    /// </summary>
    /// <remarks>
    /// Protecting a budget, not the server: the SQL free grant, which a tight polling loop
    /// could drain in days.
    ///
    /// Partitioned by authenticated principal where there is one, falling back to remote IP.
    /// Behind Container Apps' ingress that IP is the forwarded client address, which is why
    /// ForwardedHeaders is configured in Program.cs - without it every caller would share one
    /// partition and the limiter would throttle the whole world together.
    /// </remarks>
    public static IServiceCollection AddApiRateLimiting(
        this IServiceCollection services, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(ReadPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.ReadsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                    }));

            // A separate partition as well as a separate limit: keyed on the same principal, so
            // one person's agent and one person's browser each get their own budget rather than
            // competing for one.
            limiter.AddPolicy(McpPolicy, context =>
                RateLimitPartition.GetTokenBucketLimiter(
                    $"mcp:{PartitionKey(context)}",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        // The burst, not the rate. A full bucket is one application's worth of
                        // calls; what refills it is the line below, which is the sustained
                        // budget and the number the SQL grant is actually protected by.
                        TokenLimit = Math.Max(options.McpBurst, options.McpRequestsPerMinute),
                        TokensPerPeriod = Math.Max(1, options.McpRequestsPerMinute / 10),
                        ReplenishmentPeriod = McpReplenishment,

                        // Refused rather than queued, as the fixed window this replaced was. A
                        // queued tool call is a client that has stopped and cannot say why; a 429
                        // is something an agent can read, wait on and retry with the same
                        // idempotency key.
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
        });
    }

    private static string PartitionKey(HttpContext context)
        => context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name ?? "authenticated"
            : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}
