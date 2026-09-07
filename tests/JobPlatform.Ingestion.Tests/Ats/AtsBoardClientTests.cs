using System.Net;
using JobPlatform.Core.Applications;
using JobPlatform.Ingestion.Ats;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobPlatform.Ingestion.Tests.Ats;

/// <summary>
/// The four vendor readers, asserted against recorded payloads.
/// </summary>
/// <remarks>
/// <b>Everything here is a shape that occurs rather than one that could.</b> The payloads are the
/// ones the 2026-09-07 verification actually saw, and the failure cases are the ones
/// <c>ATS-ENDPOINTS.md</c> names: a 404 that is how a probe fails, Lever's well-formed "Document
/// not found", a board with nothing open, a body that will not parse, and a listing carrying no
/// apply URL.
///
/// <b>What is being defended is the distinction between three answers, not the parse.</b> A parse
/// that reads a title wrongly produces a match that does not happen. Reporting "the vendor did not
/// answer" as "this employer has no vacancies", or "no such board" as "try again later", produces a
/// number nobody can act on and a probe that either never stops or never starts - which is why
/// nearly every test below asserts <c>Outcome</c> before it asserts a listing.
/// </remarks>
public sealed class AtsBoardClientTests
{
    private static readonly AtsBoard CloudflareOnGreenhouse =
        new(AtsVendor.Greenhouse, "cloudflare");

    [Fact]
    public async Task A_greenhouse_board_answers_its_whole_catalogue_in_one_request()
    {
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(AtsBoardReadOutcome.Read, read.Outcome);

        // One request for the whole board. Asking per posting would have been 333 requests for
        // these same bytes on the board this payload was recorded from.
        Assert.Equal(
            "https://boards-api.greenhouse.io/v1/boards/cloudflare/jobs",
            Assert.Single(handler.Requests));

        var listing = Assert.Single(read.Listings, entry => entry.Title == "VoidZero Engineer");

        Assert.Equal("London, United Kingdom", listing.Location);
        Assert.Equal("https://boards.greenhouse.io/cloudflare/jobs/6304903", listing.ApplyUrl);
    }

    [Fact]
    public async Task The_apply_url_is_kept_even_where_it_leaves_the_vendors_own_domain()
    {
        // Greenhouse embedded under the employer's own careers site is the commonest shape in this
        // corpus - 1,259 of 2,006 direct apply URLs match no vendor host at all - so a reader that
        // checked absolute_url against greenhouse.io would throw away most of what there is to find.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Contains(
            read.Listings,
            listing => listing.ApplyUrl == "https://careers.example-employer.com/jobs?gh_jid=6304904");
    }

    [Fact]
    public async Task A_listing_with_no_apply_url_is_dropped_rather_than_carried_empty()
    {
        // Five entries in the payload; the fourth publishes no absolute_url and the fifth publishes
        // something a browser would not open. Neither is a vacancy anybody can apply through, and
        // AtsListingMatcher already skips both - so dropping them here changes no match, no
        // abstention and no refusal, and makes this count mean what it says.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(3, read.Listings.Count);
        Assert.DoesNotContain(read.Listings, listing => listing.Title == "Engineering Manager");
        Assert.DoesNotContain(read.Listings, listing => listing.Title == "Support Engineer");
    }

    [Fact]
    public async Task A_place_the_board_did_not_state_is_silence_rather_than_an_empty_string()
    {
        // AtsListingMatcher reads a null location as the question being unanswered, which costs
        // the confirmation and never the match. An empty string would read the same way today and
        // is one refactor away from reading as a place that disagrees.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        var listing = Assert.Single(read.Listings, entry => entry.Title == "Systems Engineer");

        Assert.Null(listing.Location);
    }

    [Fact]
    public async Task An_employer_with_nothing_open_is_read_rather_than_reported_missing()
    {
        // The distinction this whole result type exists for. A board that answers with no jobs is
        // that employer's board, confirmed and empty today; reporting it as "not a board" would
        // tell a caller the token was wrong and stop it ever being asked again.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseEmptyBoard));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(AtsBoardReadOutcome.Read, read.Outcome);
        Assert.Empty(read.Listings);
    }

    [Fact]
    public async Task A_404_is_an_ordinary_answer_meaning_not_a_board()
    {
        // How a probed slug fails, and the commonest outcome the probe path has: 99 of the 120
        // employers measured on 2026-09-07 landed here. It is a value rather than an exception and
        // it is logged at debug, because several hundred warnings a pass is a log nobody reads.
        var handler = new StubHandler(_ => RecordedBoards.Status(HttpStatusCode.NotFound));

        var read = await Greenhouse(handler).ReadAsync(new AtsBoard(AtsVendor.Greenhouse, "notaboard"));

        Assert.Equal(AtsBoardReadOutcome.NotABoard, read.Outcome);
        Assert.Empty(read.Listings);
    }

    [Fact]
    public async Task A_body_that_will_not_parse_is_unavailable_and_never_an_empty_board()
    {
        // A gateway serving an HTML error page under a 200 is the live shape of this. Reading it as
        // an empty board would report an employer as having closed every vacancy.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.MalformedBody));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
        Assert.Empty(read.Listings);
    }

    [Fact]
    public async Task Json_that_is_not_the_documented_shape_is_unavailable_rather_than_empty()
    {
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.NotABoardShape));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_vendor_refusing_or_failing_is_unavailable_and_never_not_a_board(
        HttpStatusCode status)
    {
        // Every one of these has to stay distinguishable from a 404. Read as "not a board" they
        // would take a real employer out of the probe path over one bad afternoon at a vendor -
        // and the 403 case additionally must never be answered by adding a credential.
        var handler = new StubHandler(_ => RecordedBoards.Status(status));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
    }

    [Fact]
    public async Task A_vendor_that_cannot_be_reached_at_all_does_not_throw_into_the_pass()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused."));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
    }

    [Fact]
    public async Task A_request_that_outlives_its_timeout_does_not_throw_into_the_pass()
    {
        // HttpClient reports a timeout as a cancellation rather than as a timeout, so the two
        // arrive by the same route. Both are the same fact: nothing was learned this pass.
        var handler = new StubHandler(_ => throw new TaskCanceledException("The request timed out."));

        var read = await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
    }

    [Fact]
    public async Task Nothing_a_reader_sends_carries_a_credential_or_a_cookie()
    {
        // The hard rule of this whole feature, asserted on the wire rather than trusted to a
        // review. These boards are published to job seekers unauthenticated; a header here would
        // put the feature back on the wrong side of the line mcp_handoff.md 3.2a draws.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));

        await Greenhouse(handler).ReadAsync(CloudflareOnGreenhouse);

        Assert.True(handler.Headers.TryDequeue(out var headers));
        Assert.Equal("GET", headers.Method);
        Assert.False(headers.HasBody);
        Assert.DoesNotContain("Authorization", headers.Names);
        Assert.DoesNotContain("Cookie", headers.Names);
        Assert.DoesNotContain("Proxy-Authorization", headers.Names);
    }

    [Fact]
    public async Task A_reader_that_breaks_its_own_contract_still_answers()
    {
        // The outermost net. A pass reads hundreds of boards in one invocation, so one exception
        // escaping loses every employer after it - the same argument AtsVendorDetector.Detect and
        // PostingCluster.Strength make for answering a value where a rule could have thrown.
        var reader = new ThrowingBoardClient(
            new StubHttpClientFactory(new StubHandler(_ => RecordedBoards.Status(HttpStatusCode.OK))),
            NullLogger<ThrowingBoardClient>.Instance);

        var read = await reader.ReadAsync(CloudflareOnGreenhouse);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
    }

    [Fact]
    public async Task A_reader_handed_another_vendors_board_is_a_wiring_error_and_says_so()
    {
        // The one thing that does throw, because it is not a vendor answer at all. Answered as a
        // value it would surface as an employer who is not on Greenhouse, which is a conclusion a
        // caller might store.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));

        await Assert.ThrowsAsync<ArgumentException>(
            () => Greenhouse(handler).ReadAsync(new AtsBoard(AtsVendor.Lever, "apolloresearch")));
    }

    [Fact]
    public async Task A_region_with_no_verified_endpoint_is_never_asked_of_the_ordinary_host()
    {
        // The trap AtsBoardRegion was added to close. Lever's European tenants are served by a
        // separate API host that is not in the verified record; asking api.lever.co for one of
        // their tokens answers nothing, which reads as "this employer is not on Lever" and is
        // indistinguishable from the employer genuinely not being there. So no request is made.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.LeverBoard));

        var read = await Lever(handler).ReadAsync(
            new AtsBoard(AtsVendor.Lever, "apolloresearch", AtsBoardRegion.Europe));

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_ashby_board_reads_its_title_place_and_apply_url()
    {
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.AshbyBoard));

        var read = await Ashby(handler).ReadAsync(new AtsBoard(AtsVendor.Ashby, "sampura"));

        Assert.Equal(AtsBoardReadOutcome.Read, read.Outcome);
        Assert.Equal(
            "https://api.ashbyhq.com/posting-api/job-board/sampura",
            Assert.Single(handler.Requests));

        var listing = Assert.Single(read.Listings, entry => entry.Title == "Senior Software Engineer");

        Assert.Equal("London, United Kingdom", listing.Location);
        Assert.EndsWith("/application", listing.ApplyUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ashby_falls_back_to_the_advert_page_where_it_publishes_no_apply_url()
    {
        // A weaker answer rather than a wrong one: the form is one click behind the advert, and
        // the alternative is no link at all.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.AshbyBoard));

        var read = await Ashby(handler).ReadAsync(new AtsBoard(AtsVendor.Ashby, "sampura"));

        var listing = Assert.Single(read.Listings, entry => entry.Title == "Data Engineer");

        Assert.Equal(
            "https://jobs.ashbyhq.com/sampura/8c9d0e11-2233-4455-6677-889900aabbcc",
            listing.ApplyUrl);
    }

    [Fact]
    public async Task Lever_reads_its_title_from_text_and_its_place_from_categories()
    {
        // The whole reason this layer exists: nothing above it should have to know that one vendor
        // spells a title differently from the other three.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.LeverBoard));

        var read = await Lever(handler).ReadAsync(new AtsBoard(AtsVendor.Lever, "apolloresearch"));

        Assert.Equal(AtsBoardReadOutcome.Read, read.Outcome);
        Assert.Equal(
            "https://api.lever.co/v0/postings/apolloresearch?mode=json",
            Assert.Single(handler.Requests));

        var listing = Assert.Single(read.Listings, entry => entry.Title == "Research Engineer");

        Assert.Equal("London, UK", listing.Location);
        Assert.EndsWith("/apply", listing.ApplyUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Levers_own_document_not_found_body_is_not_a_board_even_at_200()
    {
        // ATS-ENDPOINTS.md records this shape by name: Lever answers a well-formed object rather
        // than an empty list. Read as "unavailable" it would be retried for ever; read as an empty
        // board it would claim a stranger's slug belongs to this employer.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.LeverDocumentNotFound));

        var read = await Lever(handler).ReadAsync(new AtsBoard(AtsVendor.Lever, "notaboard"));

        Assert.Equal(AtsBoardReadOutcome.NotABoard, read.Outcome);
    }

    [Fact]
    public async Task Lever_publishes_no_company_name_so_a_probed_token_stays_unconfirmed()
    {
        // Recorded rather than lamented. AtsBoardCandidates.Confirm needs a name and this feed has
        // none, so a probed Lever token is Unconfirmed on this reader's evidence - which is a fact
        // about the vendor and the reason the learned path carries Lever instead.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.LeverBoard));

        var read = await Lever(handler).ReadAsync(new AtsBoard(AtsVendor.Lever, "apolloresearch"));

        Assert.Null(read.BoardName);
        Assert.Equal(
            AtsBoardConfidence.Unconfirmed,
            AtsBoardCandidates.Confirm("Apollo Research", read.BoardName));
    }

    [Fact]
    public async Task SmartRecruiters_names_the_employer_which_is_what_confirms_a_probed_token()
    {
        // The one board name any of the four verified endpoints publishes, and therefore the one
        // vendor where a slug guessed from a company name can be confirmed from this reader alone.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.SmartRecruitersBoard));

        var read = await SmartRecruiters(handler)
            .ReadAsync(new AtsBoard(AtsVendor.SmartRecruiters, "BlueOptima"));

        Assert.Equal(AtsBoardReadOutcome.Read, read.Outcome);
        Assert.Equal("BlueOptima", read.BoardName);
        Assert.Equal(
            AtsBoardConfidence.NameAgrees,
            AtsBoardCandidates.Confirm("BlueOptima Ltd", read.BoardName));
    }

    [Fact]
    public async Task The_smartrecruiters_token_keeps_its_case_all_the_way_into_the_url()
    {
        // SmartRecruiters keys on a case-sensitive company id, which is why AtsBoard preserves a
        // token's case. Folding it here would turn a valid token into a silent 404.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.SmartRecruitersBoard));

        await SmartRecruiters(handler)
            .ReadAsync(new AtsBoard(AtsVendor.SmartRecruiters, "BlueOptima"));

        Assert.Equal(
            "https://api.smartrecruiters.com/v1/companies/BlueOptima/postings?limit=100&offset=0",
            Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task A_place_split_across_fields_is_recomposed_as_the_free_text_the_matcher_wants()
    {
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.SmartRecruitersBoard));

        var read = await SmartRecruiters(handler)
            .ReadAsync(new AtsBoard(AtsVendor.SmartRecruiters, "BlueOptima"));

        // Commas, because AtsListingMatcher cuts on them and compares each segment whole. The
        // region and the country can never falsely equal a city, so they cost nothing.
        Assert.Equal(
            "London, England, uk",
            Assert.Single(read.Listings, entry => entry.Title == "Senior Software Engineer").Location);

        // A row with no place at all but marked remote answers with the arrangement, which the
        // matcher reads as the place being unstated rather than as a contradiction.
        Assert.Equal(
            "Remote",
            Assert.Single(read.Listings, entry => entry.Title == "Data Scientist").Location);
    }

    [Fact]
    public async Task A_row_offering_only_its_api_ref_is_not_an_apply_url()
    {
        // ref is an api.smartrecruiters.com address. A candidate sent there finds JSON rather than
        // a form, so it is deliberately absent from the field list and the row is dropped instead.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.SmartRecruitersBoard));

        var read = await SmartRecruiters(handler)
            .ReadAsync(new AtsBoard(AtsVendor.SmartRecruiters, "BlueOptima"));

        Assert.Equal(2, read.Listings.Count);
        Assert.DoesNotContain(read.Listings, listing => listing.Title == "Delivery Manager");
        Assert.DoesNotContain(read.Listings, listing => listing.ApplyUrl.Contains("api.smartrecruiters.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_board_longer_than_one_page_is_read_in_full_one_request_per_hundred()
    {
        // A board read short is a board missing contenders, and a missing contender turns an
        // abstention into a confident match on whichever listing survived the page boundary.
        var handler = new StubHandler(url => RecordedBoards.Ok(
            url.Contains("offset=100", StringComparison.Ordinal)
                ? RecordedBoards.SmartRecruitersPage(100, 40, 140)
                : RecordedBoards.SmartRecruitersPage(0, 100, 140)));

        var read = await SmartRecruiters(handler)
            .ReadAsync(new AtsBoard(AtsVendor.SmartRecruiters, "BlueOptima"));

        Assert.Equal(AtsBoardReadOutcome.Read, read.Outcome);
        Assert.Equal(140, read.Listings.Count);
        Assert.Equal(2, handler.Requests.Count);

        // The second page's own rows, rather than the first page's a second time.
        Assert.Contains(read.Listings, listing => listing.Title == "Engineer 139");
    }

    [Fact]
    public async Task A_page_after_the_first_that_fails_is_reported_unread_rather_than_partial()
    {
        // The rule that makes paging worth doing at all: a partial catalogue is never handed back
        // as a whole one, because the hundred listings already read look exactly like a complete
        // board to everything downstream.
        var handler = new StubHandler(url => url.Contains("offset=100", StringComparison.Ordinal)
            ? RecordedBoards.Status(HttpStatusCode.BadGateway)
            : RecordedBoards.Ok(RecordedBoards.SmartRecruitersPage(0, 100, 140)));

        var read = await SmartRecruiters(handler)
            .ReadAsync(new AtsBoard(AtsVendor.SmartRecruiters, "BlueOptima"));

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
        Assert.Empty(read.Listings);
    }

    [Fact]
    public async Task A_vendor_ignoring_the_offset_is_caught_by_the_offset_it_echoes_back()
    {
        // Without this the loop reads page one repeatedly, counts its rows towards the total, and
        // stops believing it has read a whole board when it has read the first hundred twice.
        var handler = new StubHandler(_ => RecordedBoards.Ok(
            RecordedBoards.SmartRecruitersPage(0, 100, 140)));

        var read = await SmartRecruiters(handler)
            .ReadAsync(new AtsBoard(AtsVendor.SmartRecruiters, "BlueOptima"));

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static GreenhouseBoardClient Greenhouse(StubHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<GreenhouseBoardClient>.Instance);

    private static AshbyBoardClient Ashby(StubHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<AshbyBoardClient>.Instance);

    private static LeverBoardClient Lever(StubHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<LeverBoardClient>.Instance);

    private static SmartRecruitersBoardClient SmartRecruiters(StubHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<SmartRecruitersBoardClient>.Instance);

    /// <summary>A reader whose vendor mapping is broken, to exercise the outermost net.</summary>
    private sealed class ThrowingBoardClient : AtsBoardClient
    {
        public ThrowingBoardClient(IHttpClientFactory httpClientFactory, ILogger logger)
            : base(httpClientFactory, logger)
        {
        }

        public override AtsVendor Vendor => AtsVendor.Greenhouse;

        protected override Task<AtsBoardRead> ReadBoardAsync(
            AtsBoard board,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("A mapping this reader was sure could not fail.");
    }
}
