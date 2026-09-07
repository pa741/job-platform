using JobPlatform.Core.Applications;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// What a link an employer already published says about which board is theirs.
/// </summary>
/// <remarks>
/// <b>Half of these assert a null, and that is the shape of the feature rather than an oversight.</b>
/// A shortener and an embed under the employer's own domain both prove the vendor and hide the
/// employer; a board host with the vendor's own furniture in the first path segment looks exactly
/// like a token. Answering any of them with a guess attaches one employer's postings to another
/// employer's board, and the apply link that results looks entirely ordinary - so there is nothing
/// downstream that could notice, and these are the assertions that keep it from happening.
///
/// Every URL here is either a shape the live corpus contains or one that would silently break it.
/// </remarks>
public sealed class AtsBoardTokenTests
{
    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/4012345", AtsVendor.Greenhouse, "acme")]
    [InlineData("https://job-boards.greenhouse.io/acme/jobs/4012345", AtsVendor.Greenhouse, "acme")]
    [InlineData("https://jobs.ashbyhq.com/acme/8f1c2f1e-0000-4a1b-9d0e-000000000000", AtsVendor.Ashby, "acme")]
    [InlineData("https://jobs.lever.co/acme/8f1c2f1e-0000-4a1b-9d0e-000000000000", AtsVendor.Lever, "acme")]
    [InlineData("https://acme.workable.com/j/AB12CD34EF", AtsVendor.Workable, "acme")]
    [InlineData("https://apply.workable.com/acme/j/AB12CD34EF", AtsVendor.Workable, "acme")]
    [InlineData("https://jobs.smartrecruiters.com/Acme/744000000000000", AtsVendor.SmartRecruiters, "Acme")]
    public void FromUrl_learns_the_board_token_from_a_link_the_employer_already_published(
        string url,
        AtsVendor vendor,
        string token)
    {
        // The 122 of 309 link-less applyable postings that are reachable with no request at all:
        // the employer's board is already knowable from a direct link held against another of
        // their postings, and this is the join that finds it.
        Assert.Equal(new AtsBoard(vendor, token), AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://boards.greenhouse.io/acme")]
    [InlineData("https://boards.greenhouse.io/acme/")]
    [InlineData("https://jobs.lever.co/acme/")]
    [InlineData("https://jobs.lever.co/acme/8f1c2f1e/apply")]
    [InlineData("https://jobs.ashbyhq.com/acme/8f1c2f1e/application?utm_source=linkedin")]
    [InlineData("https://apply.workable.com/acme/j/AB12CD34EF/#apply")]
    public void FromUrl_reads_the_token_from_a_board_root_and_from_a_deeper_page_alike(string url)
    {
        // The token identifies the board, and the tail identifies one vacancy on it. A link to the
        // board itself names the employer just as well as a link to one of their jobs, so refusing
        // it would discard an employer for having been linked to too generally.
        var board = AtsBoardToken.FromUrl(url);

        Assert.NotNull(board);
        Assert.Equal("acme", board.Token);
    }

    [Fact]
    public void FromUrl_reads_the_european_host_as_a_different_endpoint_and_not_as_the_same_board()
    {
        // Lever's data-residency tenants are served from jobs.eu.lever.co and their listings from
        // a separate API host. Asking the ordinary one answers nothing, which reads as "not on
        // Lever" and is indistinguishable from the employer genuinely not being there - and the
        // two namespaces are independent, so the same slug in the other one need not be the same
        // company.
        Assert.Equal(
            new AtsBoard(AtsVendor.Lever, "acme", AtsBoardRegion.Europe),
            AtsBoardToken.FromUrl("https://jobs.eu.lever.co/acme/8f1c2f1e"));

        Assert.NotEqual(
            AtsBoardToken.FromUrl("https://jobs.lever.co/acme/8f1c2f1e"),
            AtsBoardToken.FromUrl("https://jobs.eu.lever.co/acme/8f1c2f1e"));

        // An unmarked host is the ordinary endpoint, and that is a real answer rather than an
        // absent one.
        Assert.Equal(AtsBoardRegion.Default, AtsBoardToken.FromUrl("https://jobs.lever.co/acme/x")!.Region);
    }

    [Theory]
    [InlineData("https://grnh.se/abc123def")]
    [InlineData("https://grnh.se/8f1c2f1e2")]
    public void FromUrl_answers_null_for_a_shortener_that_proves_the_vendor_and_hides_the_employer(string url)
    {
        // AtsVendorDetector calls this Greenhouse and is right. The token is only knowable by
        // following the link, which this may not do - and a Greenhouse board named from anything
        // else on this URL would be somebody else's.
        Assert.Equal(AtsVendor.Greenhouse, AtsVendorDetector.Detect(url));
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://careers.withwaymo.com/jobs?gh_jid=7852098")]
    [InlineData("https://www.acme.com/careers?utm_source=board&gh_src=abc123")]
    [InlineData("https://acme.com/careers/open-roles?ashby_jid=8f1c2f1e")]
    [InlineData("https://careers.acme.com/apply?lever-origin=applied")]
    public void FromUrl_answers_null_for_an_embed_under_the_employers_own_domain(string url)
    {
        // The vendor is certain - the query parameter is theirs - and the board token appears
        // nowhere in the URL. Reading the employer's own host as a token would probe a board that
        // is not theirs; reading the job id as one would probe nothing at all.
        Assert.True(AtsVendorDetector.Detect(url).IsEmployerAts());
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://boards.greenhouse.io/jobs/4012345")]
    [InlineData("https://boards.greenhouse.io/embed/job_app?for=acme&token=4012345")]
    [InlineData("https://jobs.ashbyhq.com/careers/8f1c2f1e")]
    [InlineData("https://jobs.lever.co/apply/8f1c2f1e")]
    [InlineData("https://apply.workable.com/j/AB12CD34EF")]
    [InlineData("https://jobs.smartrecruiters.com/oneclick-ui/company/Acme/publication/1")]
    [InlineData("https://boards.greenhouse.io/careers.html")]
    public void FromUrl_refuses_a_segment_that_is_the_vendors_own_url_furniture(string url)
    {
        // Every one of these passes the slug shape check, so without a reserved list the commonest
        // wrong answer would be a board named after the page. A board called "jobs" or "j" belongs
        // to whoever registered it, and its vacancies would be published as this employer's.
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://uk.whatjobs.com/pub_api__cpl__1234567__2609.html?utm_source=publisher")]
    [InlineData("https://uk.whatjobs.com/r?u=https%3A%2F%2Fboards.greenhouse.io%2Facme")]
    [InlineData("https://www.linkedin.com/jobs/view/4012345678/")]
    [InlineData("https://uk.indeed.com/viewjob?jk=abc123def456")]
    [InlineData("https://www.reed.co.uk/jobs/senior-engineer/54321234")]
    public void FromUrl_answers_null_for_another_job_board_however_it_spells_itself(string url)
    {
        // The second case is why the vendor is asked of AtsVendorDetector rather than decided
        // again here: it carries a board host inside a query string, and a host table of this
        // file's own would have to repeat the whole aggregator list to refuse it. Asking the
        // detector means every aggregator it learns about later is refused here for free.
        Assert.Equal(AtsVendor.Aggregator, AtsVendorDetector.Detect(url));
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://acme.wd3.myworkdayjobs.com/en-US/External/job/London/Engineer_R-1234")]
    [InlineData("https://acme.myworkdaysite.com/recruiting/acme/External")]
    [InlineData("https://careers-acme.icims.com/jobs/1234/senior-engineer/job")]
    [InlineData("https://tbe.taleo.net/CHK04/ats/careers/v2/viewRequisition?org=ACME&cws=1")]
    [InlineData("https://career5.successfactors.eu/careers?career_job_req_id=1234")]
    [InlineData("https://acme.teamtailor.com/jobs/1234567-senior-engineer")]
    [InlineData("https://acme.bamboohr.com/careers/42")]
    [InlineData("https://acme.pinpointhq.com/en/postings/8f1c2f1e")]
    public void FromUrl_answers_null_for_a_vendor_with_no_public_board_to_read(string url)
    {
        // Workday is the one that matters by volume - 69 employers, more than any other named
        // vendor - and it has no clean public listing. A caller handed a board for one would have
        // to know by hearsay not to ask for it, so the answer is that there is no board here.
        Assert.True(AtsVendorDetector.Detect(url).IsEmployerAts());
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://mylever.co/acme/8f1c2f1e")]
    [InlineData("https://clever.com/acme/8f1c2f1e")]
    [InlineData("https://jobs.lever.co.acme-careers.com/acme/8f1c2f1e")]
    [InlineData("https://notgreenhouse.io/acme/jobs/1")]
    [InlineData("https://greenhouse.io.example.com/acme/jobs/1")]
    [InlineData("https://myworkable.com/j/AB12CD34EF")]
    public void FromUrl_matches_a_host_on_a_label_boundary_and_never_on_a_substring(string url)
    {
        // EndsWith alone is true of mylever.co and Contains is true of clever.com; and anybody may
        // register greenhouse.io.example.com. Either mistake here produces a board token lifted
        // from a stranger's URL, which is worse than the missing link it was meant to replace.
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://www.greenhouse.io/acme/jobs/4012345")]
    [InlineData("https://greenhouse.io/acme/jobs/4012345")]
    [InlineData("https://www.workable.com/j/AB12CD34EF")]
    [InlineData("https://jobs.workable.com/senior-engineer-acme")]
    [InlineData("https://www.smartrecruiters.com/job/senior-engineer-1234")]
    [InlineData("https://www.lever.co/customers")]
    public void FromUrl_answers_null_for_a_vendor_host_that_is_not_a_board(string url)
    {
        // A vendor's marketing site sits on the same domain as its boards and has a first path
        // segment that looks exactly like a token. jobs.workable.com is the sharpest of them: it
        // is Workable's own job search, so the subdomain that usually names an employer names
        // every employer at once.
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://BOARDS.GREENHOUSE.IO/acme/jobs/4012345")]
    [InlineData("http://boards.greenhouse.io/acme/jobs/4012345")]
    [InlineData("https://boards.greenhouse.io./acme/jobs/4012345")]
    [InlineData("  https://boards.greenhouse.io/acme/jobs/4012345  ")]
    [InlineData("boards.greenhouse.io/acme/jobs/4012345")]
    [InlineData("https://boards.greenhouse.io/acme/jobs/4012345?utm_source=linkedin#apply")]
    public void FromUrl_reads_a_url_however_a_board_happened_to_write_it(string url)
    {
        // Case, scheme, the fully qualified trailing dot, surrounding whitespace, a missing scheme
        // and a tracking tail. All of these occur in the corpus; none of them is a different
        // employer, and a scheme-less link discarded here is an employer sent to the probe path
        // for no reason.
        Assert.Equal(new AtsBoard(AtsVendor.Greenhouse, "acme"), AtsBoardToken.FromUrl(url));
    }

    [Fact]
    public void FromUrl_preserves_the_case_the_vendor_spelled_the_token_in()
    {
        // SmartRecruiters keys its listings on a company id it writes as Acme. Folding case would
        // turn a valid token into one that resolves to nothing, which is a silent false negative;
        // keeping it means two spellings look like two boards, which costs a duplicate request and
        // never a wrong answer. The host is case-insensitive and is folded; the path is not.
        Assert.Equal(
            new AtsBoard(AtsVendor.SmartRecruiters, "AcmeCorp"),
            AtsBoardToken.FromUrl("https://JOBS.SMARTRECRUITERS.COM/AcmeCorp/744000000000000"));

        Assert.NotEqual(
            AtsBoardToken.FromUrl("https://jobs.smartrecruiters.com/acmecorp/744000000000000"),
            AtsBoardToken.FromUrl("https://jobs.smartrecruiters.com/AcmeCorp/744000000000000"));
    }

    [Theory]
    [InlineData("https://jobs.lever.co/")]
    [InlineData("https://jobs.lever.co")]
    [InlineData("https://boards.greenhouse.io")]
    [InlineData("https://apply.workable.com/")]
    [InlineData("https://jobs.eu.lever.co/")]
    public void FromUrl_answers_null_where_the_link_names_the_vendor_and_no_employer(string url)
    {
        // A board host with no path is the shortener's situation spelled differently: the vendor is
        // proved and the employer is absent.
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Theory]
    [InlineData("https://jobs.lever.co/acme%20corp/8f1c2f1e")]
    [InlineData("https://jobs.lever.co/..%2fother/8f1c2f1e")]
    [InlineData("https://jobs.lever.co/acme.corp/8f1c2f1e")]
    [InlineData("https://boards.greenhouse.io/acme+corp/jobs/1")]
    public void FromUrl_refuses_a_segment_that_is_not_a_slug_rather_than_decoding_one_out_of_it(string url)
    {
        // A token that needs an escape is not a token, and decoding one is how ..%2f becomes a
        // path. A dot in a first path segment means a file far more often than it means a company.
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Fact]
    public void FromUrl_refuses_a_segment_longer_than_any_board_token_is()
    {
        // A sanity bound rather than a specification: nothing may turn a pathological path segment
        // into a token that some later caller pastes into a request.
        var overlong = "https://boards.greenhouse.io/" + new string('a', 101) + "/jobs/1";

        Assert.Null(AtsBoardToken.FromUrl(overlong));
        Assert.NotNull(AtsBoardToken.FromUrl("https://boards.greenhouse.io/" + new string('a', 100) + "/jobs/1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("apply by email")]
    [InlineData("see description")]
    [InlineData("://")]
    [InlineData("https://")]
    [InlineData("http:///jobs")]
    [InlineData("%%%")]
    [InlineData("\t\n")]
    [InlineData("mailto:jobs@acme.com")]
    [InlineData("javascript:void(0)")]
    [InlineData("ftp://files.acme.com/jobs.pdf")]
    [InlineData("https://[not-a-host/jobs")]
    public void FromUrl_never_throws_on_a_string_that_is_not_a_url(string? url)
    {
        // The input is text a scraper lifted off somebody's page, and this runs across a corpus of
        // them in one pass - a single exception would lose every other row with it.
        Assert.Null(AtsBoardToken.FromUrl(url));
    }

    [Fact]
    public void FromUrl_still_reads_a_board_link_carrying_a_redirect_chain_or_a_long_tail()
    {
        // Corpus URLs carry tracking payloads. None of that is a reason to fail; the host is still
        // the host and the first segment is still the employer.
        var padded = "https://boards.greenhouse.io/acme/jobs/4012345?ref=" + new string('a', 4000);

        Assert.Equal(new AtsBoard(AtsVendor.Greenhouse, "acme"), AtsBoardToken.FromUrl(padded));
    }

    [Fact]
    public void ServesPublicBoard_names_the_five_vendors_this_feature_can_actually_read()
    {
        // The reach of the feature, not a fact about the vendor - which is why it lives beside the
        // host table it has to agree with rather than on AtsVendors.
        Assert.True(AtsBoardToken.ServesPublicBoard(AtsVendor.Greenhouse));
        Assert.True(AtsBoardToken.ServesPublicBoard(AtsVendor.Ashby));
        Assert.True(AtsBoardToken.ServesPublicBoard(AtsVendor.Lever));
        Assert.True(AtsBoardToken.ServesPublicBoard(AtsVendor.Workable));
        Assert.True(AtsBoardToken.ServesPublicBoard(AtsVendor.SmartRecruiters));

        // An employer ATS with no readable board is still an employer ATS. The two questions are
        // separate, and folding them would either skip Workday postings or promise a board for one.
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.Workday));
        Assert.True(AtsVendor.Workday.IsEmployerAts());

        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.Icims));
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.Taleo));
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.SuccessFactors));
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.Teamtailor));
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.BambooHR));
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.Pinpoint));
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.Other));
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.Aggregator));
        Assert.False(AtsBoardToken.ServesPublicBoard(AtsVendor.Unknown));
    }

    [Fact]
    public void IsToken_answers_on_the_shape_alone_and_leaves_the_reserved_words_to_FromUrl()
    {
        // The split is the point. FromUrl is guessing which segment of a URL is the employer and
        // has to be timid about words that are the vendor's own furniture; a probed token is
        // confirmed against a posting before it is trusted, so it may be anything a vendor accepts.
        Assert.True(AtsBoardToken.IsToken("jobs"));
        Assert.Null(AtsBoardToken.FromUrl("https://jobs.lever.co/jobs/8f1c2f1e"));

        Assert.True(AtsBoardToken.IsToken("acme"));
        Assert.True(AtsBoardToken.IsToken("acme-corp"));
        Assert.True(AtsBoardToken.IsToken("acme_corp"));
        Assert.True(AtsBoardToken.IsToken("Acme2"));

        Assert.False(AtsBoardToken.IsToken(null));
        Assert.False(AtsBoardToken.IsToken(""));
        Assert.False(AtsBoardToken.IsToken(" acme"));
        Assert.False(AtsBoardToken.IsToken("acme corp"));
        Assert.False(AtsBoardToken.IsToken("acme.corp"));
        Assert.False(AtsBoardToken.IsToken("acme/corp"));
        Assert.False(AtsBoardToken.IsToken("acme%20corp"));
        Assert.False(AtsBoardToken.IsToken(new string('a', 101)));
    }

    [Fact]
    public void A_board_refuses_a_vendor_whose_listings_cannot_be_read_without_an_account()
    {
        // A type that cannot represent the unreadable case cannot be misread. Workday is not a
        // board with a token nobody has found; there is nothing here to ask.
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtsBoard(AtsVendor.Workday, "acme"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtsBoard(AtsVendor.Aggregator, "acme"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtsBoard(AtsVendor.Unknown, "acme"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtsBoard(AtsVendor.Other, "acme"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("acme corp")]
    [InlineData("acme/corp")]
    [InlineData("../etc")]
    [InlineData("https://boards.greenhouse.io/acme")]
    public void A_board_refuses_a_token_that_is_not_a_slug(string token)
    {
        // The probe path builds a token out of a company name, so the bound has to be on the type
        // rather than in the caller that happens to remember it - the argument AiCallRecord.Create
        // makes for being the only constructor.
        Assert.Throws<ArgumentException>(() => new AtsBoard(AtsVendor.Greenhouse, token));
    }

    [Fact]
    public void Two_boards_are_the_same_board_when_the_vendor_the_token_and_the_region_agree()
    {
        // Value equality is what lets a caller collapse the boards learned from a dozen of one
        // employer's links into one row without writing a comparer that could disagree with this
        // file about what a board is.
        Assert.Equal(new AtsBoard(AtsVendor.Lever, "acme"), new AtsBoard(AtsVendor.Lever, "acme"));

        Assert.NotEqual(
            new AtsBoard(AtsVendor.Lever, "acme"),
            new AtsBoard(AtsVendor.Lever, "acme", AtsBoardRegion.Europe));

        Assert.NotEqual(new AtsBoard(AtsVendor.Lever, "acme"), new AtsBoard(AtsVendor.Ashby, "acme"));
        Assert.NotEqual(new AtsBoard(AtsVendor.Lever, "acme"), new AtsBoard(AtsVendor.Lever, "Acme"));
    }

    [Fact]
    public void An_unset_region_reads_as_the_vendors_ordinary_endpoint()
    {
        // Zero has to be the answer that is true of nearly every board, because a stored value
        // nobody wrote will read as this one.
        Assert.Equal(AtsBoardRegion.Default, default(AtsBoardRegion));
    }
}
