using JobPlatform.Core.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JobPlatform.Data.Realtime;

public static class RealtimeRegistration
{
    /// <summary>
    /// Registers the realtime feed when an endpoint is configured, and nothing otherwise.
    /// </summary>
    /// <remarks>
    /// <b>The same shape as <c>AddAiProvider</c>, and for the same reason.</b> A missing endpoint
    /// registers nothing rather than throwing, so an absent environment variable cannot take down
    /// endpoints with nothing to do with realtime - and every consumer resolves
    /// <see cref="IRealtimeFeed"/> as nullable and skips its step. The dashboard polls whether or
    /// not this exists; the feed only ever makes it quicker.
    ///
    /// Note what is <i>not</i> checked: a credential. The service runs with
    /// <c>disableLocalAuth</c>, so there is no key to be present or absent, and whether the
    /// identity actually holds <c>SignalR App Server</c> is answered by the first call rather than
    /// by registration - the contract every identity-based connection here runs under.
    ///
    /// A singleton, deliberately. The feed holds one hub context and the connection pool behind
    /// it, which is the same reason <c>CosmosClient</c> is one.
    /// </remarks>
    public static IServiceCollection AddRealtimeFeed(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<RealtimeOptions>(configuration.GetSection(RealtimeOptions.SectionName));

        var options = configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>()
            ?? new RealtimeOptions();

        if (string.IsNullOrWhiteSpace(options.ServiceUri))
        {
            return services;
        }

        // Both hosts resolve their identity through a top-level key, because SQL and Cosmos need
        // the same value. Falling back to it means this section does not have to repeat it.
        services.PostConfigure<RealtimeOptions>(o =>
            o.ManagedIdentityClientId ??= configuration["ManagedIdentityClientId"]);

        services.AddSingleton<IRealtimeFeed, SignalRFeed>();

        return services;
    }
}
