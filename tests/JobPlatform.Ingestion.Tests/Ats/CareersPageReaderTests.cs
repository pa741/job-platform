using System.Net;
using System.Text;
using JobPlatform.Core.Applications;
using JobPlatform.Ingestion.Ats;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Ingestion.Tests.Ats;

/// <summary>
/// The third discovery source: reading an employer's own careers page for the board it embeds.
/// </summary>
/// <remarks>
/// <b>This is the only thing in the system that fetches a host nobody publishes an API for, so the
/// assertions here are about restraint as much as about extraction.</b> Every other request goes to
/// one of five documented board endpoints written to be read by job seekers; a careers page is
/// somebody's web server that has agreed to nothing. So what is pinned is: a URL that is not worth
/// a request costs none, an aggregator's page about the company costs none, a page too large to
/// read is not reported as a page with no board on it, a host that does not answer inside the
/// deadline is abandoned, and nothing but a GET, an Accept and a User-Agent ever leaves this
/// process.
///
/// <b>The extraction half is asserted through Core rather than around it.</b> Nothing in
/// <c>CareersPageReader</c> decides what a board host is, which query parameters name a vendor, or
/// which path segments are furniture - <c>AtsBoardToken.FromUrl</c> and
/// <c>AtsVendorDetector.Detect</c> own all of that and have their own suites. What is asserted here
/// is the part that is this file's: which addresses are handed to them, and what is concluded when
/// they answer more than once or not at all.
///
/// <b>The stub is the <c>IHttpClientFactory</c> rather than the client</b>, which is
/// <c>StubHttp</c>'s own argument: the reader takes the factory, so a test that stubbed the client
/// would exercise a construction path production does not have - and a reader that started newing
/// up its own <c>HttpClient</c>, dropping the cookie-less handler with it, would still pass.
/// </remarks>
public sealed class CareersPageReaderTests
{
    private const string CareersUrl = "https://careers.acmerobotics.example/";

    [Fact]
    public async Task The_token_comes_off_the_board_link_a_greenhouse_embed_leaves_beside_its_gh_jid()
    {
        var handler = new StubHandler(_ => RecordedCareersPages.Ok(RecordedCareersPages.GreenhouseEmbed));

        var read = await Reader(handler).ReadAsync(CareersUrl);

        // The whole point of this source, in one page. The gh_jid links prove the employer runs
        // Greenhouse and name no token at all - AtsBoardToken.FromUrl answers null for an embed
        // under the employer's own domain, and says why - so a host list or a parameter reader on
        // its own gets the vendor and stops. The board link two lines down carries the token, and
        // this employer is reachable by no other route: their apply links are LinkedIn's, which
        // publishes none.
        Assert.Equal(CareersPageOutcome.NamedABoard, read.Outcome);
        Assert.Equal(new AtsBoard(AtsVendor.Greenhouse, "acmerobotics"), read.Board);
        Assert.True(read.Requested);

        // One request for the employer, not one per vacancy the page lists.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_embed_with_no_board_link_names_a_vendor_and_no_token()
    {
        var handler = new StubHandler(
            _ => RecordedCareersPages.Ok(RecordedCareersPages.GreenhouseEmbedWithoutTheBoard));

        var read = await Reader(handler).ReadAsync(CareersUrl);

        // Asked and found nothing, which is a real state and not a near miss to be helped along.
        // Every gh_jid on that page is under the employer's own domain, where the token appears
        // nowhere, and guessing one from the host would attach this employer's postings to whoever
        // holds that slug - the failure every refusal in AtsBoardToken is written to avoid.
        Assert.Equal(CareersPageOutcome.NamedNothing, read.Outcome);
        Assert.Null(read.Board);
    }

    [Fact]
    public async Task A_page_with_no_applicant_tracking_system_on_it_is_found_to_have_none()
    {
        var handler = new StubHandler(_ => RecordedCareersPages.Ok(RecordedCareersPages.NoBoardAtAll));

        var read = await Reader(handler).ReadAsync(CareersUrl);

        // Distinct from Unavailable, and the distinction is what stops the pass re-reading this
        // employer's site every night: "we read it and there is nothing" is final until they get a
        // board, where "nothing came back" is not evidence about them at all.
        Assert.Equal(CareersPageOutcome.NamedNothing, read.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("unemployed")]
    [InlineData("mailto:jobs@acmerobotics.example")]
    [InlineData("javascript:void(0)")]
    [InlineData("file:///c:/careers.html")]
    [InlineData("ftp://files.acmerobotics.example/jobs")]
    public async Task A_company_url_that_is_junk_costs_no_request_and_throws_nothing(string? url)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("nothing may be fetched"));

        var read = await Reader(handler).ReadAsync(url);

        // CompanyUrl is a string a scraper lifted off somebody's page, and this runs over a batch
        // of them: one exception here loses every employer after it, which is the argument
        // AtsVendorDetector.Detect and AtsBoardToken.FromUrl both make for answering a value where
        // an exhaustive rule would have thrown.
        Assert.Equal(CareersPageOutcome.NotAnAddress, read.Outcome);

        // And the answer costs nobody anything, which is why it is reported separately from the
        // pages that were read.
        Assert.False(read.Requested);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://www.linkedin.com/company/acme-robotics")]
    [InlineData("https://uk.indeed.com/cmp/Acme-Robotics")]
    [InlineData("https://uk.whatjobs.com/company/acme-robotics")]
    public async Task An_aggregators_page_about_the_company_is_refused_before_a_socket_is_opened(string url)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("nothing may be fetched"));

        var read = await Reader(handler).ReadAsync(url);

        // Not a hypothetical: company_url is very often a job board's own profile page for the
        // employer rather than the employer's site. A board link found on one of those belongs to
        // whichever employer that page happens to be listing - on a search page, to no single one -
        // so the whole request is refused rather than the answer being graded down afterwards.
        // AtsVendorDetector already holds every aggregator this system has met, so this is one call
        // instead of a second list that would fall behind it.
        Assert.Equal(CareersPageOutcome.NotAnAddress, read.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_company_url_that_is_already_a_board_address_names_the_token_with_no_request()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("nothing may be fetched"));

        var read = await Reader(handler).ReadAsync("https://boards.greenhouse.io/acmerobotics");

        // The string in hand is the answer, so spending a request to be told what it already said
        // would be the "ask per posting" mistake in miniature. Reported as unrequested, so the
        // pass's own account of what it cost stays true.
        Assert.Equal(new AtsBoard(AtsVendor.Greenhouse, "acmerobotics"), read.Board);
        Assert.False(read.Requested);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_page_that_embeds_one_vendor_and_links_anothers_board_is_an_abstention()
    {
        var handler = new StubHandler(
            _ => RecordedCareersPages.Ok(RecordedCareersPages.TwoVendorsOnOnePage));

        var read = await Reader(handler).ReadAsync(CareersUrl);

        // The agency case, which is the largest remaining backlog in this corpus: the employers
        // holding the most link-less applyable postings are agencies advertising a client's vacancy
        // under their own name. Their page embeds their own Greenhouse and links a client's Lever
        // board, and the Lever token is the only one on the page - so a reader that took "the one
        // board I found" would attach the agency's postings to their client's board. Reading the
        // embed markers is what makes the disagreement visible.
        Assert.Equal(CareersPageOutcome.NamedSeveral, read.Outcome);
        Assert.Null(read.Board);
    }

    [Fact]
    public async Task A_page_naming_two_boards_on_one_vendor_is_an_abstention_too()
    {
        var handler = new StubHandler(
            _ => RecordedCareersPages.Ok(RecordedCareersPages.TwoBoardsOnOnePage));

        var read = await Reader(handler).ReadAsync(CareersUrl);

        // A parent and a subsidiary on one careers page. Nothing in the markup says which one the
        // advert's employer is, and taking the first would settle it on document order - a coin
        // toss with somebody's application on it, which is the same refusal AtsListingMatcher makes
        // when an employer advertises one title twice.
        Assert.Equal(CareersPageOutcome.NamedSeveral, read.Outcome);
    }

    [Fact]
    public async Task Nothing_but_a_get_an_accept_and_a_user_agent_reaches_the_employers_server()
    {
        var handler = new StubHandler(_ => RecordedCareersPages.Ok(RecordedCareersPages.GreenhouseEmbed));

        await Reader(handler).ReadAsync(CareersUrl);

        var sent = Assert.Single(handler.Headers);

        // The decision this whole feature rests on, asserted at the one place it could be broken by
        // an edit that looks like a courtesy. mcp_handoff.md 3.2 costed the authenticated route and
        // refused it; nothing on this path sends a credential, a cookie or a session, and an
        // employer's own server is where that is easiest to forget - it is not a vendor's API, so
        // nobody would notice a header being added to "help it answer".
        Assert.Equal("GET", sent.Method);
        Assert.False(sent.HasBody);

        // An equality rather than a pair of absences, and that is the difference between a test
        // that catches the next header and one that catches the two somebody thought of. The only
        // thing this reader puts on a request is the Accept that overrides the shared client's
        // JSON default, because a careers page is a document.
        Assert.Equal(["Accept"], sent.Names);

        // The User-Agent and the cookie-less handler are properties of the shared client rather
        // than of this reader, which is the whole reason it goes through that client instead of
        // newing up its own - AtsBoardRegistrationTests is where both are pinned, and a stub
        // factory here cannot see either.
        Assert.DoesNotContain("Authorization", sent.Names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", sent.Names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_page_larger_than_the_ceiling_is_unread_rather_than_reported_as_carrying_no_board()
    {
        // The board link is past the ceiling, which is exactly the case that decides the outcome:
        // read as far as the bound allows and the page appears to name nothing.
        var page = new string(' ', 4096) + RecordedCareersPages.GreenhouseEmbed;

        var handler = new StubHandler(_ => RecordedCareersPages.Ok(page));

        var read = await Reader(handler, options => options.MaxCareersPageBytes = 1024)
            .ReadAsync(CareersUrl);

        // Unavailable and never NamedNothing. "We did not read far enough" and "this employer has
        // no board" are different facts, and the second one gets stored: the pass stamps the
        // employer and stops asking, so a truncation reported as an absence is a recovery lost for
        // as long as the re-read window. Same rule the board client keeps for a truncated board.
        Assert.Equal(CareersPageOutcome.Unavailable, read.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_page_that_does_not_answer_claims_nothing_about_the_employer(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => RecordedCareersPages.Status(status));

        var read = await Reader(handler).ReadAsync(CareersUrl);

        // A 404 here is not the answer it is on a probed board token. There the vendor is saying
        // "no board under that slug"; here nobody has seen a page at all, so nothing is established
        // about whether this employer has a board. A 403 is emphatically not answered by adding a
        // credential - that is the decision behind the feature rather than one this file may take.
        Assert.Equal(CareersPageOutcome.Unavailable, read.Outcome);
        Assert.Null(read.Board);
    }

    [Fact]
    public async Task A_body_that_is_not_a_document_is_not_scanned()
    {
        var handler = new StubHandler(_ => RecordedCareersPages.NotADocument());

        var read = await Reader(handler).ReadAsync(CareersUrl);

        // A PDF or an image carries no addresses worth finding, and reading a megabyte of one to
        // establish that spends somebody else's bandwidth on a certainty.
        Assert.Equal(CareersPageOutcome.Unavailable, read.Outcome);
    }

    [Fact]
    public async Task A_host_that_never_finishes_answering_is_abandoned_at_the_deadline()
    {
        var handler = new StubHandler((_, ct) => Task.Delay(Timeout.Infinite, ct)
            .ContinueWith(_ => new HttpResponseMessage(HttpStatusCode.OK), TaskScheduler.Default));

        var read = await Reader(handler, options => options.CareersPageTimeout = TimeSpan.FromMilliseconds(50))
            .ReadAsync(CareersUrl);

        // The deadline is a linked cancellation source rather than HttpClient.Timeout, because that
        // one stops applying once the response headers are in hand - which on this path is where
        // the reading starts. A host that answered instantly and then dripped a body forever would
        // otherwise hold an invocation billed by the second for as long as it liked.
        Assert.Equal(CareersPageOutcome.Unavailable, read.Outcome);
    }

    [Fact]
    public async Task A_cancelled_pass_answers_rather_than_throwing()
    {
        using var cancelled = new CancellationTokenSource();

        await cancelled.CancelAsync();

        var handler = new StubHandler(_ => RecordedCareersPages.Ok(RecordedCareersPages.GreenhouseEmbed));

        var read = await Reader(handler).ReadAsync(CareersUrl, cancelled.Token);

        // Stopping is not a fact about the employer, and a caller that cancelled already knows it
        // did. Throwing here would lose every employer after this one in the same pass, which is
        // the contract AtsBoardClient keeps for exactly the same reason.
        Assert.Equal(CareersPageOutcome.Unavailable, read.Outcome);
    }

    [Fact]
    public async Task A_board_link_written_without_a_scheme_is_still_read()
    {
        var handler = new StubHandler(_ => RecordedCareersPages.Ok(
            """<html><body><a href="//job-boards.greenhouse.io/acmerobotics">Jobs</a></body></html>"""));

        var read = await Reader(handler).ReadAsync(CareersUrl);

        // Protocol-relative links are ordinary in embed snippets, and both Core functions already
        // accept a bare host - boards publish scheme-less apply links, so the leniency exists and
        // is reused rather than added.
        Assert.Equal(new AtsBoard(AtsVendor.Greenhouse, "acmerobotics"), read.Board);
    }

    private static CareersPageReader Reader(
        HttpMessageHandler handler, Action<AtsBoardOptions>? configure = null)
    {
        var options = new AtsBoardOptions();

        configure?.Invoke(options);

        return new CareersPageReader(
            new StubHttpClientFactory(handler),
            Options.Create(options),
            NullLogger<CareersPageReader>.Instance);
    }
}
