using System.Threading.RateLimiting;
using JobPlatform.Api.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace JobPlatform.Api.Infrastructure;

public static class RateLimitSetup
{
    public const string ReadPolicy = "reads";
    public const string MatchPolicy = "matches";

    /// <summary>
    /// Per-caller rate limits.
    /// </summary>
    /// <remarks>
    /// Protecting two budgets, not the server: the SQL free grant, which a tight polling loop
    /// could drain in days, and the Anthropic bill, where a single caller could otherwise run
    /// up real money. Matching gets its own much smaller bucket for that reason.
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

            limiter.AddPolicy(MatchPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.MatchesPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                    }));
        });
    }

    private static string PartitionKey(HttpContext context)
        => context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name ?? "authenticated"
            : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}
