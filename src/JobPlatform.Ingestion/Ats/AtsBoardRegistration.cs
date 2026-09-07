using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Wires the board readers and the one outbound client they share.
/// </summary>
/// <remarks>
/// <b>One named client for all four vendors, built through <c>IHttpClientFactory</c>.</b> The
/// factory is what owns the handler and its connection pool: a reader newing up an
/// <c>HttpClient</c> per board would exhaust sockets across a pass of several hundred, and one held
/// as a static would never notice DNS moving under it. It is the same instinct as
/// <c>CosmosClient</c> being a singleton, expressed the way outbound HTTP expresses it.
///
/// <b>One client rather than four also means the courtesies are set once.</b> The timeout, the
/// user agent and - the part that matters - the handler's refusal to keep cookies are properties of
/// the transport every vendor reader is handed, not of anybody remembering to configure their own.
/// A fifth reader added later inherits all of it by construction.
///
/// <b>The handler is where "no credential, no cookie, no session" stops being a promise and becomes
/// a fact.</b> <c>UseCookies</c> is off, so a <c>Set-Cookie</c> from a vendor is not stored and
/// cannot come back on the next request; <c>Credentials</c> is null, so the host's own identity is
/// never offered; and <c>PreAuthenticate</c> is off, so nothing is volunteered ahead of a
/// challenge. That is a recorded decision with a legal record behind it - <c>mcp_handoff.md</c> 3.2
/// and 3.2a - and it is enforced here rather than reviewed for, because a reviewed rule is one
/// somebody eventually adds a header past.
///
/// <b>Registering nothing is not an option here, unlike the AI provider or the realtime feed.</b>
/// Those two register nothing when unconfigured because they need an endpoint and a role assignment
/// that a clone may not have; these endpoints are public, need no configuration to reach and no
/// identity to authenticate, so a deployment that binds no settings gets four working readers with
/// the polite defaults. The options exist to make a pass gentler, never to switch it on.
/// </remarks>
public static class AtsBoardRegistration
{
    /// <summary>
    /// The named client every board reader resolves.
    /// </summary>
    /// <remarks>
    /// Named rather than typed, because four readers share one configuration and a typed client per
    /// reader would be four handlers, four pools and four places for the cookie rule to be dropped
    /// from.
    /// </remarks>
    public const string HttpClientName = "ats-boards";

    /// <summary>Registers the four vendor readers, their shared client and the pass-level reader.</summary>
    public static IServiceCollection AddAtsBoardClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AtsBoardOptions>(configuration.GetSection(AtsBoardOptions.SectionName));

        services
            .AddHttpClient(HttpClientName, (provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<AtsBoardOptions>>().Value;

                // Clamped rather than trusted. Both of these throw on a non-positive value, and
                // they are read while a client is being created - inside the reader's own
                // never-throw net, which would turn one mistyped setting into every board on every
                // vendor answering "unavailable" with a log blaming the vendor. A floor is the
                // legible failure: a request that is too impatient or a board that is too small to
                // read is visible in one pass, where a whole feature quietly off is not.
                client.Timeout = options.RequestTimeout > TimeSpan.Zero
                    ? options.RequestTimeout
                    : TimeSpan.FromSeconds(1);

                client.MaxResponseContentBufferSize = Math.Max(1024, options.MaxResponseBytes);

                // Descriptive, and it names the software rather than pretending to be a browser.
                // Nothing here is circumventing anything, so nothing here needs to look like
                // something else - and a vendor who wants to know what this traffic is should be
                // able to find out in one search.
                client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The whole of "no cookie, no session" in one line. Without it a vendor's
                // Set-Cookie would be stored on the pooled handler and returned on every later
                // request, which is a session - however unintentionally acquired.
                UseCookies = false,

                // And the whole of "no credential". Null is already the default, and it is stated
                // because the default is what a later edit would change without noticing: the host
                // runs under a managed identity that has business with Azure and none whatsoever
                // with a job board. PreAuthenticate is off with it, so nothing is volunteered
                // ahead of a challenge that will never come.
                Credentials = null,
                PreAuthenticate = false,

                // Boards are JSON and often megabytes of it. Asking for it compressed is a
                // courtesy to the vendor's bandwidth as much as to ours.
                AutomaticDecompression = DecompressionMethods.All,

                // Redirects are followed, because a vendor moving its own board host is their
                // business and not a failure - but a chain long enough to be a loop is not a
                // board, and there is nothing to leak along one: no cookie is carried and no
                // credential exists to offer.
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 3,
            });

        services.AddSingleton<IAtsBoardClient, GreenhouseBoardClient>();
        services.AddSingleton<IAtsBoardClient, AshbyBoardClient>();
        services.AddSingleton<IAtsBoardClient, LeverBoardClient>();
        services.AddSingleton<IAtsBoardClient, SmartRecruitersBoardClient>();

        // Workable is the fifth vendor AtsBoard admits and the one with no reader. Every Workable
        // link in this corpus is apply.workable.com/j/{code}, which names no board at all, so a
        // Workable employer is reachable only through a probed {token}.workable.com - and the
        // endpoint that would answer it is not in the verified record. AtsBoardReader.CanRead is
        // how a caller finds that out without spending a request on it.

        services.AddSingleton<AtsBoardReader>();

        return services;
    }
}
