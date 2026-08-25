using JobPlatform.Api.Features.Applications;
using JobPlatform.Api.Features.Matches;
using JobPlatform.Api.Features.Meta;
using JobPlatform.Api.Features.Metrics;
using JobPlatform.Api.Features.Postings;
using JobPlatform.Api.Features.Profiles;
using JobPlatform.Api.Features.Runs;

namespace JobPlatform.Api.Endpoints;

public static class EndpointGroupExtensions
{
    /// <summary>Every feature in the API. Add new groups here.</summary>
    private static readonly IEndpointGroup[] Groups =
    [
        new PostingEndpoints(),
        new RunEndpoints(),
        new MetricEndpoints(),
        new ProfileEndpoints(),
        new MatchEndpoints(),
        new ApplicationEndpoints(),
    ];

    /// <summary>
    /// Maps the versioned API surface under <c>/api/v1</c>.
    /// </summary>
    /// <remarks>
    /// A route-group prefix rather than the Asp.Versioning package. The prefix costs nothing
    /// now and leaves the door open: a v2 is a second group, and the package can be adopted
    /// later without any route moving.
    /// </remarks>
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1").WithTags("v1");

        foreach (var group in Groups)
        {
            group.Map(v1);
        }

        // Health and identity sit outside the versioned surface: probes and the platform
        // address them by fixed path, and a version bump must not move them.
        new MetaEndpoints().Map(app);

        return app;
    }
}
