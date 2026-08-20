using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace JobPlatform.Api.Infrastructure;

/// <summary>
/// A scheme that authenticates nobody, used when no identity provider is configured.
/// </summary>
/// <remarks>
/// Without a registered scheme, a failed authorization has nothing to challenge and ASP.NET
/// throws "No authenticationScheme was specified, and there was no DefaultChallengeScheme
/// found" - so an API started without an AzureAd section answers every protected route with
/// 500 instead of 401. That is both wrong and actively misleading: it reads as a broken
/// server rather than as a missing credential.
///
/// This handler makes the honest answer the default one. It never produces a principal, so
/// nothing can accidentally be authorised by its presence.
/// </remarks>
public sealed class UnconfiguredAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Unconfigured";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());
}
