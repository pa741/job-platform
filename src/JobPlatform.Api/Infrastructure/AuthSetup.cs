using JobPlatform.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

namespace JobPlatform.Api.Infrastructure;

public static class AuthSetup
{
    /// <summary>Policy for endpoints that may be opened up during iteration.</summary>
    public const string PublicReadPolicy = "PublicRead";

    /// <summary>Policy for endpoints that must always carry a principal.</summary>
    public const string AuthenticatedPolicy = "Authenticated";

    /// <summary>
    /// Entra ID bearer authentication, with a read policy that can be relaxed by config.
    /// </summary>
    /// <remarks>
    /// Two policies rather than one because the endpoints are not equivalent.
    /// <see cref="PublicReadPolicy"/> collapses to anonymous when
    /// <c>Api:AllowAnonymousReads</c> is set, so a frontend can be developed against real
    /// data before app registrations exist. <see cref="AuthenticatedPolicy"/> ignores that
    /// flag entirely and guards <c>/me</c>, which has no meaning without a principal.
    ///
    /// The Microsoft Identity Web scheme is only added when an <c>AzureAd</c> section with a
    /// client id exists; it throws at startup otherwise, which would make a local run without
    /// an Entra tenant impossible. A placeholder scheme takes its place so denied requests
    /// still answer 401.
    /// </remarks>
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services, IConfiguration configuration, ApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var azureAd = configuration.GetSection("AzureAd");
        var configured = azureAd.Exists() && !string.IsNullOrWhiteSpace(azureAd["ClientId"]);

        // AddAuthentication is called unconditionally: it registers the core authentication
        // services that UseAuthentication's middleware resolves. Calling it only when a
        // scheme exists means an API with no AzureAd section fails at startup with
        // "Unable to resolve service for type IAuthenticationSchemeProvider" - which is
        // precisely the local-development case the AllowAnonymousReads flag exists to serve.
        if (configured)
        {
            services.AddAuthentication().AddMicrosoftIdentityWebApi(azureAd);
        }
        else
        {
            // A scheme that authenticates nobody, registered as the default so a denied
            // request answers 401 rather than throwing for want of a challenge scheme.
            services.AddAuthentication(UnconfiguredAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, UnconfiguredAuthenticationHandler>(
                    UnconfiguredAuthenticationHandler.SchemeName, _ => { });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(PublicReadPolicy, policy =>
            {
                // Keyed on the explicit flag alone, never on whether a provider happens to be
                // configured. Opening up because AzureAd is missing would mean a deployment
                // with a mistyped section name silently serves the whole dataset publicly -
                // the failure would look like success. Requiring the flag makes a missing
                // provider fail loudly, as 401s, which is the safe direction to fail in.
                if (options.AllowAnonymousReads)
                {
                    // No requirements at all. An empty policy with no assertion is invalid,
                    // so an always-true assertion is what "no requirement" has to look like.
                    policy.RequireAssertion(_ => true);
                }
                else
                {
                    policy.RequireAuthenticatedUser();
                }
            })
            .AddPolicy(AuthenticatedPolicy, policy => policy.RequireAuthenticatedUser());

        return services;
    }
}
