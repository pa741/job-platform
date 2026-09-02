using JobPlatform.Core.Applications;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// What an apply URL says about whose form is at the end of it.
/// </summary>
/// <remarks>
/// The cases worth pinning are the ones measured against the live corpus rather than the ones a
/// host list makes obvious: the embed behind an employer's own domain, the aggregator arriving as
/// a "direct" link, and the substring match that would report a vendor that was never there.
/// Every URL here is a shape that occurs in the data or one that would silently break it.
/// </remarks>
public sealed class AtsVendorDetectorTests
{
    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/4012345", AtsVendor.Greenhouse)]
    [InlineData("https://job-boards.greenhouse.io/acme/jobs/4012345", AtsVendor.Greenhouse)]
    [InlineData("https://grnh.se/abc123def", AtsVendor.Greenhouse)]
    [InlineData("https://jobs.lever.co/acme/8f1c2f1e-0000-4a1b-9d0e-000000000000", AtsVendor.Lever)]
    [InlineData("https://jobs.eu.lever.co/acme/8f1c2f1e", AtsVendor.Lever)]
    [InlineData("https://apply.workable.com/acme/j/AB12CD34EF/", AtsVendor.Workable)]
    [InlineData("https://jobs.ashbyhq.com/acme/8f1c2f1e", AtsVendor.Ashby)]
    [InlineData("https://acme.wd3.myworkdayjobs.com/en-US/External/job/London/Engineer_R-1234", AtsVendor.Workday)]
    [InlineData("https://acme.myworkdaysite.com/recruiting/acme/External", AtsVendor.Workday)]
    [InlineData("https://career5.successfactors.eu/careers?career_job_req_id=1234", AtsVendor.SuccessFactors)]
    [InlineData("https://performancemanager.sapsf.com/career?career_ns=job_listing", AtsVendor.SuccessFactors)]
    [InlineData("https://jobs.smartrecruiters.com/Acme/744000000000000", AtsVendor.SmartRecruiters)]
    [InlineData("https://acme.teamtailor.com/jobs/1234567-senior-engineer", AtsVendor.Teamtailor)]
    [InlineData("https://acme.bamboohr.com/careers/42", AtsVendor.BambooHR)]
    [InlineData("https://careers-acme.icims.com/jobs/1234/senior-engineer/job", AtsVendor.Icims)]
    [InlineData("https://tbe.taleo.net/CHK04/ats/careers/v2/viewRequisition?org=ACME&cws=1", AtsVendor.Taleo)]
    [InlineData("https://acme.pinpointhq.com/en/postings/8f1c2f1e", AtsVendor.Pinpoint)]
    public void Detect_names_the_vendor_hosting_the_form(string url, AtsVendor expected)
        => Assert.Equal(expected, AtsVendorDetector.Detect(url));

    [Fact]
    public void Detect_reads_greenhouse_out_of_the_query_when_the_host_is_the_employers_own()
    {
        // The measured reason this function reads query parameters at all. A host list answers
        // "Other" here, and 1,259 of 2,006 direct apply URLs in the corpus match no bare host -
        // so a host-only detector is not slightly worse, it misses most of what there is.
        Assert.Equal(
            AtsVendor.Greenhouse,
            AtsVendorDetector.Detect("https://careers.withwaymo.com/jobs?gh_jid=7852098"));

        Assert.Equal(
            AtsVendor.Greenhouse,
            AtsVendorDetector.Detect("https://www.acme.com/careers?utm_source=board&gh_src=abc123"));

        Assert.Equal(
            AtsVendor.Ashby,
            AtsVendorDetector.Detect("https://acme.com/careers/open-roles?ashby_jid=8f1c2f1e"));

        // Lever writes its repeated parameter as lever-source[], which arrives percent-encoded.
        Assert.Equal(
            AtsVendor.Lever,
            AtsVendorDetector.Detect("https://careers.acme.com/apply?lever-origin=applied&lever-source%5B%5D=job-post"));
    }

    [Fact]
    public void Detect_matches_a_query_parameter_by_its_whole_name_and_not_by_the_text_in_the_query()
    {
        // A URL is full of somebody else's strings. A tracking value that happens to contain
        // gh_jid is a fact about a board's analytics, not about who takes the application.
        Assert.Equal(AtsVendor.Other, AtsVendorDetector.Detect("https://careers.acme.com/jobs?utm_content=gh_jid"));
        Assert.Equal(AtsVendor.Other, AtsVendorDetector.Detect("https://careers.acme.com/jobs?not_gh_jid=1"));
        Assert.Equal(AtsVendor.Other, AtsVendorDetector.Detect("https://careers.acme.com/jobs?gh_jid_ref=1"));
        Assert.Equal(AtsVendor.Other, AtsVendorDetector.Detect("https://careers.acme.com/gh_jid=7852098"));

        // ...and the real thing still resolves when it is not the first parameter.
        Assert.Equal(
            AtsVendor.Greenhouse,
            AtsVendorDetector.Detect("https://careers.acme.com/jobs?utm_content=gh_jid&gh_jid=7852098"));
    }

    [Theory]
    [InlineData("https://uk.whatjobs.com/pub_api__cpl__1234567__2609.html?utm_source=publisher")]
    [InlineData("https://www.whatjobs.com/job/senior-engineer/1234567")]
    [InlineData("https://www.linkedin.com/jobs/view/4012345678/")]
    [InlineData("https://uk.indeed.com/viewjob?jk=abc123def456")]
    [InlineData("https://www.reed.co.uk/jobs/senior-engineer/54321234")]
    [InlineData("https://www.totaljobs.com/job/senior-engineer/acme-job1234567")]
    [InlineData("https://www.glassdoor.co.uk/job-listing/senior-engineer-acme-JV_KO0,15.htm")]
    [InlineData("https://www.cv-library.co.uk/job/223344556/senior-engineer")]
    public void Detect_calls_another_job_board_an_aggregator_rather_than_an_unrecognised_ats(string url)
    {
        // Aggregator is a distinct answer because the loop acts on it differently: following one
        // costs a slot from the daily cap and arrives at a second search results page. Folding it
        // into Other would put WhatJobs in the same bucket as a real employer form.
        Assert.Equal(AtsVendor.Aggregator, AtsVendorDetector.Detect(url));
        Assert.False(AtsVendorDetector.Detect(url).IsEmployerAts());
    }

    [Fact]
    public void Detect_lets_the_host_decide_before_the_query()
    {
        // A WhatJobs page carrying a Greenhouse job id in its tracking is still a WhatJobs page,
        // and what a person opens is the host.
        Assert.Equal(
            AtsVendor.Aggregator,
            AtsVendorDetector.Detect("https://uk.whatjobs.com/pub_api__cpl__1234567.html?gh_jid=7852098"));
    }

    [Theory]
    [InlineData("https://clever.com/careers")]
    [InlineData("https://www.clever.com/jobs/123")]
    [InlineData("https://deliverance.co.uk/jobs")]
    [InlineData("https://notgreenhouse.io/acme/jobs/1")]
    [InlineData("https://mylever.co/apply")]
    [InlineData("https://workday.company-careers.com/apply")]
    public void Detect_matches_a_host_on_a_label_boundary_and_never_on_a_substring(string url)
    {
        // Contains("lever.co") is true of clever.com, and the failure is silent: the loop takes a
        // Lever-shaped path on a stranger's site and reports a vendor that was never there.
        Assert.Equal(AtsVendor.Other, AtsVendorDetector.Detect(url));
    }

    [Theory]
    [InlineData("https://greenhouse.io.example.com/jobs/1")]
    [InlineData("https://lever.co.acme-careers.com/apply")]
    [InlineData("https://myworkdayjobs.com.acme.net/en-US/External")]
    public void Detect_ignores_a_vendor_domain_used_as_a_prefix_of_somebody_elses(string url)
    {
        // Anybody may register greenhouse.io.example.com. Suffix matching is the only reading of
        // a host that a stranger cannot forge from the left.
        Assert.Equal(AtsVendor.Other, AtsVendorDetector.Detect(url));
    }

    [Theory]
    [InlineData("https://BOARDS.GREENHOUSE.IO/acme/jobs/4012345")]
    [InlineData("http://boards.greenhouse.io/acme/jobs/4012345")]
    [InlineData("https://www.greenhouse.io/acme/jobs/4012345")]
    [InlineData("https://boards.greenhouse.io./acme/jobs/4012345")]
    [InlineData("  https://boards.greenhouse.io/acme/jobs/4012345  ")]
    [InlineData("boards.greenhouse.io/acme/jobs/4012345")]
    public void Detect_reads_a_url_however_a_board_happened_to_write_it(string url)
    {
        // Case, scheme, a www prefix, the fully qualified trailing dot, surrounding whitespace and
        // a missing scheme altogether. All of these occur; none of them is a different employer.
        Assert.Equal(AtsVendor.Greenhouse, AtsVendorDetector.Detect(url));
    }

    [Fact]
    public void Detect_answers_unknown_when_there_is_no_url_and_other_when_there_is_no_match()
    {
        // Two different answers wanting opposite work: Unknown is fixed by finding a link, Other
        // by opening the one already held. One "we don't know" value would hide which.
        Assert.Equal(AtsVendor.Unknown, AtsVendorDetector.Detect(null));
        Assert.Equal(AtsVendor.Unknown, AtsVendorDetector.Detect(string.Empty));
        Assert.Equal(AtsVendor.Unknown, AtsVendorDetector.Detect("   "));

        Assert.Equal(AtsVendor.Other, AtsVendorDetector.Detect("https://careers.acme.com/jobs/senior-engineer"));
    }

    [Theory]
    [InlineData("mailto:jobs@acme.com")]
    [InlineData("tel:+441234567890")]
    [InlineData("javascript:void(0)")]
    [InlineData("ftp://files.acme.com/jobs.pdf")]
    public void Detect_answers_unknown_for_an_address_that_opens_no_web_page(string url)
    {
        // mailto:jobs@acme.com parses with a host of acme.com, so a detector that only read the
        // host would report that employer's application system for an inbox with no form in it.
        Assert.Equal(AtsVendor.Unknown, AtsVendorDetector.Detect(url));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("apply by email")]
    [InlineData("unemployed")]
    [InlineData("://")]
    [InlineData("https://")]
    [InlineData("http:///jobs")]
    [InlineData("%%%")]
    [InlineData("\t\n")]
    [InlineData("see description")]
    [InlineData("https://[not-a-host/jobs")]
    public void Detect_never_throws_on_a_string_that_is_not_a_url(string url)
    {
        // The input is text a scraper lifted off somebody's page, and this runs inside a
        // projection over the whole queue - one exception would lose every other row with it.
        // A bare word must not become "Other" either: that asserts an employer ATS is there.
        Assert.Equal(AtsVendor.Unknown, AtsVendorDetector.Detect(url));
    }

    [Fact]
    public void Detect_still_reads_a_percent_encoded_or_over_long_url_without_complaint()
    {
        // Corpus URLs carry redirect chains and tracking payloads. None of that is a reason to
        // fail; the host is still the host.
        var padded = "https://boards.greenhouse.io/acme/jobs/4012345?ref=" + new string('a', 4000);

        Assert.Equal(AtsVendor.Greenhouse, AtsVendorDetector.Detect(padded));
        Assert.Equal(
            AtsVendor.Aggregator,
            AtsVendorDetector.Detect("https://uk.whatjobs.com/r?u=https%3A%2F%2Fboards.greenhouse.io%2Facme"));
    }

    [Fact]
    public void An_unset_vendor_reads_as_unknown()
    {
        // Zero means "nothing established", the same default and the same argument as
        // SubmissionChannel.Unknown. A column defaulting to a vendor name would be a claim.
        Assert.Equal(AtsVendor.Unknown, default(AtsVendor));
    }

    [Fact]
    public void An_unrecognised_ats_is_still_an_employer_ats_and_an_aggregator_is_not()
    {
        // Other is the long tail of real employer forms, so the queue must not skip it. The skip
        // exists for boards, which are not the employer at all.
        Assert.True(AtsVendor.Other.IsEmployerAts());
        Assert.True(AtsVendor.Greenhouse.IsEmployerAts());
        Assert.True(AtsVendor.Workday.IsEmployerAts());

        Assert.False(AtsVendor.Aggregator.IsEmployerAts());
        Assert.False(AtsVendor.Unknown.IsEmployerAts());
    }

    [Fact]
    public void Workday_is_known_to_want_an_account_before_the_tab_is_opened()
    {
        // Worth knowing up front rather than ten minutes in: the form sits behind a per-tenant
        // signup and an email confirmation, so the apply is a registration and not a form fill.
        Assert.True(AtsVendor.Workday.RequiresAccount());
        Assert.True(AtsVendor.SuccessFactors.RequiresAccount());
        Assert.True(AtsVendor.Taleo.RequiresAccount());
        Assert.True(AtsVendor.Icims.RequiresAccount());

        Assert.False(AtsVendor.Greenhouse.RequiresAccount());
        Assert.False(AtsVendor.Lever.RequiresAccount());
        Assert.False(AtsVendor.Ashby.RequiresAccount());
        Assert.False(AtsVendor.Other.RequiresAccount());

        // A warning about a form, not a verdict on the posting. Nothing here excludes it.
        Assert.True(AtsVendor.Workday.IsEmployerAts());
    }
}
