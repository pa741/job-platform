using JobPlatform.Api.Configuration;

namespace JobPlatform.Api.Infrastructure;

public static class CacheSetup
{
    public const string PostingsPolicy = "postings";
    public const string MetricsPolicy = "metrics";
    public const string FacetsPolicy = "facets";

    /// <summary>
    /// Output caching, sized by what each family of data actually costs to produce.
    /// </summary>
    /// <remarks>
    /// Responses are cached by full query string, so different filters do not share an entry.
    /// Nothing user-specific is ever cached: no endpoint under these policies varies by
    /// principal, and <c>/me</c> deliberately carries no policy at all.
    /// </remarks>
    public static IServiceCollection AddApiOutputCache(
        this IServiceCollection services, CacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return services.AddOutputCache(cache =>
        {
            cache.AddPolicy(PostingsPolicy, builder => builder
                .Expire(TimeSpan.FromSeconds(options.PostingsSeconds))
                .SetVaryByQuery("*"));

            cache.AddPolicy(MetricsPolicy, builder => builder
                .Expire(TimeSpan.FromSeconds(options.MetricsSeconds))
                .SetVaryByQuery("*"));

            cache.AddPolicy(FacetsPolicy, builder => builder
                .Expire(TimeSpan.FromSeconds(options.FacetsSeconds))
                .SetVaryByQuery("*"));
        });
    }
}
