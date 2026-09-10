using System.Net;
using JobPlatform.Core.Applications;
using JobPlatform.Ingestion.Ats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Ingestion.Tests.Ats;

/// <summary>
/// The wiring, asserted because none of it fails loudly when it is wrong.
/// </summary>
/// <remarks>
/// <b>A missing registration here is a recovery that silently does not happen.</b> A vendor whose
/// reader was never registered answers <see cref="AtsBoardReadOutcome.Unavailable"/> for every
/// employer on it, for ever, and the only symptom is a count that never rises - which is precisely
/// the shape of bug this repository has already paid for more than once. So the four are asserted
/// by vendor rather than by count: a count in a test is a number somebody updates, where a name is
/// a claim about what works.
/// </remarks>
public sealed class AtsBoardRegistrationTests
{
    [Fact]
    public void Every_vendor_with_a_verified_endpoint_has_a_reader_after_registration()
    {
        using var provider = Provide();

        var reader = provider.GetRequiredService<AtsBoardReader>();

        Assert.True(reader.CanRead(AtsVendor.Greenhouse));
        Assert.True(reader.CanRead(AtsVendor.Ashby));
        Assert.True(reader.CanRead(AtsVendor.Lever));
        Assert.True(reader.CanRead(AtsVendor.SmartRecruiters));
    }

    [Fact]
    public void Workable_is_admitted_by_the_core_type_and_has_no_reader_here()
    {
        // Not a gap discovered later. Every Workable link in this corpus is
        // apply.workable.com/j/{code}, which names no board at all, so a Workable employer is
        // reachable only through a probed {token}.workable.com - and no endpoint for that is in the
        // verified record. This test is what makes adding one a deliberate diff.
        using var provider = Provide();

        Assert.False(provider.GetRequiredService<AtsBoardReader>().CanRead(AtsVendor.Workable));

        // The Core type still admits it, which is the difference between "this vendor publishes no
        // public board" and "nobody here has written the reader yet".
        Assert.True(AtsBoardToken.ServesPublicBoard(AtsVendor.Workable));
    }

    [Fact]
    public void The_shared_client_identifies_itself_and_will_not_wait_for_ever()
    {
        // A vendor noticing this traffic should be able to find out in one search what it is. The
        // timeout is the other half: a board request still running after ten seconds is holding an
        // invocation billed by the second and a slot another employer could have used.
        using var provider = Provide();

        var client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(AtsBoardRegistration.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        Assert.Contains(
            "JobPlatform",
            client.DefaultRequestHeaders.UserAgent.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            client.DefaultRequestHeaders.Accept,
            accept => accept.MediaType == "application/json");
    }

    [Fact]
    public void The_careers_page_reader_shares_the_client_that_carries_no_cookie_or_credential()
    {
        // The third discovery source is the only thing here that fetches a host nobody publishes an
        // API for, so it is the one place a header would be added to "help a page answer" and
        // nothing would fail. Wiring it onto the shared client is what makes that impossible rather
        // than discouraged: UseCookies off means a Set-Cookie cannot come back on the next request,
        // a null Credentials means the host's managed identity is never offered, and PreAuthenticate
        // off means nothing is volunteered ahead of a challenge that will never come.
        using var provider = Provide();

        Assert.NotNull(provider.GetService<CareersPageReader>());

        var handler = Assert.IsType<SocketsHttpHandler>(PrimaryHandler(provider));

        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);

        // And a redirect chain long enough to be a loop is not a careers page. There is nothing to
        // leak along one - no cookie is carried and no credential exists to offer - but an
        // unbounded chase is an arbitrary host deciding how long an invocation runs.
        Assert.Equal(3, handler.MaxAutomaticRedirections);
    }

    /// <summary>
    /// The handler the named client is actually built with, by running the registered builder.
    /// </summary>
    /// <remarks>
    /// Reached through <c>HttpClientFactoryOptions</c> rather than by constructing a client, because
    /// the primary handler is not exposed on <c>HttpClient</c> at all - and the properties above are
    /// the whole of "no credential, no cookie, no session" as a fact rather than as a promise, so
    /// they are worth reaching for. This is what the factory itself does when it creates the client.
    /// </remarks>
    private static HttpMessageHandler PrimaryHandler(IServiceProvider provider)
    {
        var options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(AtsBoardRegistration.HttpClientName);

        var builder = new CapturingHandlerBuilder(provider);

        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        return builder.PrimaryHandler;
    }

    /// <summary>A builder that keeps whatever the registration hands it.</summary>
    private sealed class CapturingHandlerBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override IServiceProvider Services { get; } = services;

        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    [Fact]
    public void A_deployment_that_configures_nothing_still_reads_boards()
    {
        // Unlike the AI provider and the realtime feed, which register nothing when unconfigured
        // because they need an endpoint and a role assignment. These endpoints are public and need
        // neither, so the settings exist to make a pass gentler and never to switch it on.
        using var provider = Provide();

        Assert.NotNull(provider.GetService<AtsBoardReader>());
    }

    [Fact]
    public void The_courtesies_are_configuration_rather_than_constants()
    {
        // The answer to "you are being noisy" has to be a settings change rather than a deploy.
        using var provider = Provide(new Dictionary<string, string?>
        {
            ["AtsBoards:RequestTimeout"] = "00:00:03",
            ["AtsBoards:UserAgent"] = "SomeoneElsesFork/2.0 (+https://example.invalid)",
        });

        var client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(AtsBoardRegistration.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(3), client.Timeout);
        Assert.Contains(
            "SomeoneElsesFork/2.0",
            client.DefaultRequestHeaders.UserAgent.ToString(),
            StringComparison.Ordinal);
    }

    private static ServiceProvider Provide(IDictionary<string, string?>? settings = null)
        => new ServiceCollection()
            .AddAtsBoardClients(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
                    .Build())
            .BuildServiceProvider();
}
