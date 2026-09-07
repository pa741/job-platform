using JobPlatform.Core.Applications;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// Which entry on an employer's own board a posting is, and when the answer is none of them.
/// </summary>
/// <remarks>
/// The cases are the live shapes rather than invented ones: Cloudflare's board is what proved the
/// recovery works at all - 333 jobs, carrying the <i>VoidZero Engineer</i> LinkedIn withheld a
/// link for - and the one-title-several-cities abstention is the 74-in-285 measurement that made
/// the city part of the cross-board key. The free-text location cases are what a recruiter
/// actually types.
///
/// <b>Several tests pin a match that is deliberately not made.</b> A missed recovery leaves a
/// posting exactly where it was and a wrong one sends an application to a vacancy nobody chose, so
/// the limitations are asserted rather than left to be rediscovered as bugs and "fixed".
/// </remarks>
public sealed class AtsListingMatchTests
{
    private const string CloudflareUrl = "https://boards.greenhouse.io/cloudflare/jobs/7000001";

    private static AtsListing Listing(string title, string? location = null, string? applyUrl = null)
        => new(title, location, applyUrl ?? CloudflareUrl);

    [Fact]
    public void Match_returns_the_listing_whose_title_and_place_both_agree()
    {
        // The posting the whole feature was proved on: held here as 3020, advertised on LinkedIn
        // with no apply URL, and published by the employer's own board with one.
        var wanted = Listing("VoidZero Engineer", "London, United Kingdom", CloudflareUrl);

        var match = AtsListingMatcher.Match(
            "VoidZero Engineer",
            "London",
            [Listing("Security Engineer", "London, United Kingdom"), wanted]);

        Assert.Equal(AtsListingMatchOutcome.Matched, match.Outcome);
        Assert.Same(wanted, match.Listing);
        Assert.Equal(AtsMatchConfidence.TitleAndPlace, match.Confidence);
        Assert.Empty(match.Contenders);
    }

    [Theory]
    [InlineData("Senior Software Engineer (Platform)")]
    [InlineData("senior software engineer - platform")]
    [InlineData("  Senior   Software Engineer, Platform  ")]
    [InlineData("SENIOR SOFTWARE ENGINEER / PLATFORM")]
    public void Match_folds_case_punctuation_and_whitespace_before_comparing_titles(string boardTitle)
    {
        var match = AtsListingMatcher.Match(
            "Senior Software Engineer, Platform",
            "London",
            [Listing(boardTitle, "London, UK")]);

        Assert.Equal(AtsListingMatchOutcome.Matched, match.Outcome);
    }

    [Theory]
    [InlineData("London, UK")]
    [InlineData("London, England, United Kingdom")]
    [InlineData("Greater London, United Kingdom")]
    [InlineData("London Area")]
    [InlineData("City of London")]
    [InlineData("London (Hybrid)")]
    [InlineData("Hybrid - London")]
    [InlineData("London / Remote")]
    [InlineData("london")]
    public void Match_reads_a_city_out_of_the_free_text_an_ats_publishes(string location)
    {
        // An ATS location is whatever a recruiter typed. Each of these names London somewhere in
        // it, and the confidence has to say so - a rule that reached TitleOnly on half of them
        // would be reporting that the city was never checked when it was.
        var match = AtsListingMatcher.Match("Data Engineer", "London", [Listing("Data Engineer", location)]);

        Assert.Equal(AtsMatchConfidence.TitleAndPlace, match.Confidence);
    }

    [Theory]
    [InlineData("Greater London")]
    [InlineData("London Area")]
    [InlineData("City Of London")]
    public void Match_folds_the_metro_spellings_on_the_posting_side_too(string postingCity)
    {
        // LocationCity holds whichever spelling the board carrying the advert used: "London Area"
        // is LinkedIn's and "Greater London" is Indeed's. Folding only the board side would make
        // every LinkedIn posting - which is all 309 of the ones missing a link - contradict the
        // employer's own "London, UK" and be dropped.
        var match = AtsListingMatcher.Match("Data Engineer", postingCity, [Listing("Data Engineer", "London, UK")]);

        Assert.Equal(AtsMatchConfidence.TitleAndPlace, match.Confidence);
    }

    [Fact]
    public void Match_does_not_find_york_inside_new_york()
    {
        // Why the segments are compared for equality rather than searched. Containment is the
        // generous version of this rule and it is generous in the one direction that hurts: a
        // real city inside a real city, at the right employer, 200 miles away.
        var match = AtsListingMatcher.Match("Data Engineer", "York", [Listing("Data Engineer", "New York, NY")]);

        Assert.Equal(AtsListingMatchOutcome.NoMatch, match.Outcome);
    }

    [Fact]
    public void Match_reads_a_city_that_carries_a_parenthesised_note()
    {
        var match = AtsListingMatcher.Match(
            "Data Engineer",
            "New York",
            [Listing("Data Engineer", "New York, NY (HQ)")]);

        Assert.Equal(AtsMatchConfidence.TitleAndPlace, match.Confidence);
    }

    [Fact]
    public void Match_abstains_when_one_title_is_advertised_in_several_cities()
    {
        // The measured case, replayed: title and employer alone matched 285 postings across boards
        // and adding the city left 211, so better than a quarter of them were this. Here the
        // posting states no city, so nothing separates the three and there is no answer to give.
        var listings = new[]
        {
            Listing("Data Engineer", "London, UK", "https://boards.greenhouse.io/acme/jobs/1"),
            Listing("Data Engineer", "Berlin, Germany", "https://boards.greenhouse.io/acme/jobs/2"),
            Listing("Data Engineer", "New York, NY", "https://boards.greenhouse.io/acme/jobs/3"),
        };

        var match = AtsListingMatcher.Match("Data Engineer", postingCity: null, listings);

        Assert.Equal(AtsListingMatchOutcome.Ambiguous, match.Outcome);
        Assert.Null(match.Listing);
        Assert.Null(match.Confidence);
        Assert.Equal(3, match.Contenders.Count);
    }

    [Fact]
    public void Match_uses_the_city_to_separate_one_title_advertised_in_several_cities()
    {
        var london = Listing("Data Engineer", "London, UK", "https://boards.greenhouse.io/acme/jobs/1");

        var listings = new[]
        {
            london,
            Listing("Data Engineer", "Berlin, Germany", "https://boards.greenhouse.io/acme/jobs/2"),
            Listing("Data Engineer", "New York, NY", "https://boards.greenhouse.io/acme/jobs/3"),
        };

        var match = AtsListingMatcher.Match("Data Engineer", "London", listings);

        Assert.Same(london, match.Listing);
        Assert.Equal(AtsMatchConfidence.TitleAndPlace, match.Confidence);
    }

    [Theory]
    [InlineData("Remote")]
    [InlineData("Remote (UK)")]
    [InlineData("Hybrid")]
    [InlineData("Anywhere")]
    [InlineData(null)]
    [InlineData("")]
    public void Match_treats_an_arrangement_as_silence_about_the_place(string? location)
    {
        // "Remote" answers how the job is done, not where it is, and boards file remote vacancies
        // under a city constantly - so this must not read as a contradiction. It costs the
        // confirmation and not the match, which is what TitleOnly is for.
        var match = AtsListingMatcher.Match("Data Engineer", "London", [Listing("Data Engineer", location)]);

        Assert.Equal(AtsListingMatchOutcome.Matched, match.Outcome);
        Assert.Equal(AtsMatchConfidence.TitleOnly, match.Confidence);
    }

    [Fact]
    public void Match_reports_title_only_when_the_posting_itself_states_no_city()
    {
        var match = AtsListingMatcher.Match(
            "Data Engineer",
            postingCity: null,
            [Listing("Data Engineer", "London, UK")]);

        Assert.Equal(AtsMatchConfidence.TitleOnly, match.Confidence);
    }

    [Fact]
    public void Match_drops_a_listing_whose_place_contradicts_the_posting()
    {
        // Dropped rather than returned at a weaker confidence. A confidence value is no defence
        // against a caller that stores it and applies anyway, and this is the failure the whole
        // rule exists to refuse.
        var match = AtsListingMatcher.Match("Data Engineer", "London", [Listing("Data Engineer", "Berlin, Germany")]);

        Assert.Equal(AtsListingMatchOutcome.NoMatch, match.Outcome);
        Assert.Null(match.Listing);
        Assert.Null(match.Confidence);
    }

    [Fact]
    public void Match_prefers_the_listing_the_place_confirms_over_one_it_cannot_place()
    {
        // Two entries under one title, one of them located here and one unpinned. Abstaining would
        // forfeit the recovery on exactly the evidence the cross-board measurement says to trust.
        var london = Listing("Data Engineer", "London, UK", "https://boards.greenhouse.io/acme/jobs/1");
        var remote = Listing("Data Engineer", "Remote", "https://boards.greenhouse.io/acme/jobs/2");

        Assert.Same(london, AtsListingMatcher.Match("Data Engineer", "London", [london, remote]).Listing);
        Assert.Same(london, AtsListingMatcher.Match("Data Engineer", "London", [remote, london]).Listing);
    }

    [Fact]
    public void Match_abstains_between_two_listings_that_agree_on_the_place()
    {
        // The place has separated everything it can, and what is left is two requisitions with one
        // title in one city. Nothing here ranks them; an employer is entitled to two of these.
        var listings = new[]
        {
            Listing("Data Engineer", "London, UK", "https://boards.greenhouse.io/acme/jobs/1"),
            Listing("Data Engineer", "Greater London", "https://boards.greenhouse.io/acme/jobs/2"),
        };

        Assert.Equal(
            AtsListingMatchOutcome.Ambiguous,
            AtsListingMatcher.Match("Data Engineer", "London", listings).Outcome);
    }

    [Fact]
    public void Match_treats_listings_sharing_one_apply_url_as_one_vacancy()
    {
        // A board listing one requisition under two departments answers both with the same URL.
        // There is only one destination, so there is no wrong answer available to give.
        var listings = new[]
        {
            Listing("Data Engineer", "London, UK", CloudflareUrl),
            Listing("Data Engineer", "London, UK", CloudflareUrl),
        };

        var match = AtsListingMatcher.Match("Data Engineer", "London", listings);

        Assert.Equal(AtsListingMatchOutcome.Matched, match.Outcome);
        Assert.Equal(CloudflareUrl, match.Listing?.ApplyUrl);
    }

    [Fact]
    public void Match_keeps_a_seniority_ladder_apart()
    {
        // Harnham advertised one requisition four times - Junior, plain, Senior and Lead. Merging
        // a rung in the cross-board key costs a duplicate row; merging one here sends an
        // application to the wrong grade.
        var listings = new[]
        {
            Listing("Senior Data Engineer", "London, UK"),
            Listing("Lead Data Engineer", "London, UK"),
        };

        Assert.Equal(
            AtsListingMatchOutcome.NoMatch,
            AtsListingMatcher.Match("Data Engineer", "London", listings).Outcome);
    }

    [Theory]
    [InlineData("Software Engineer (Remote)")]
    [InlineData("Software Engineer - Remote")]
    [InlineData("Software Engineer, London")]
    [InlineData("Software Engineer - London (Hybrid)")]
    public void Match_strips_a_trailing_run_of_place_and_arrangement_words_from_a_title(string boardTitle)
    {
        var match = AtsListingMatcher.Match("Software Engineer", "London", [Listing(boardTitle, "London, UK")]);

        Assert.Equal(AtsListingMatchOutcome.Matched, match.Outcome);
    }

    [Fact]
    public void Match_does_not_strip_those_words_from_the_middle_of_a_title()
    {
        // The walk stops at the first word that is not one of these, so "sensing" protects
        // "remote". A rule removing them wherever they appeared would match remote sensing work
        // against a job that is not it.
        var match = AtsListingMatcher.Match(
            "Engineer",
            "London",
            [Listing("Engineer, Remote Sensing", "London, UK")]);

        Assert.Equal(AtsListingMatchOutcome.NoMatch, match.Outcome);
    }

    [Fact]
    public void Match_abstains_where_the_trailing_strip_merges_two_board_entries()
    {
        // What makes the widening affordable: if it folds two of the employer's own entries into
        // one title, both become contenders and nothing is returned.
        var listings = new[]
        {
            Listing("Engineer", "London, UK", "https://boards.greenhouse.io/acme/jobs/1"),
            Listing("Engineer (Remote)", "London, UK", "https://boards.greenhouse.io/acme/jobs/2"),
        };

        Assert.Equal(
            AtsListingMatchOutcome.Ambiguous,
            AtsListingMatcher.Match("Engineer", "London", listings).Outcome);
    }

    [Fact]
    public void Match_ignores_a_listing_with_no_apply_url()
    {
        // A listing with nowhere to go cannot be the answer to "where does this application go",
        // and counting it as a contender would abstain over an entry that could never be returned.
        var real = Listing("Data Engineer", "London, UK", CloudflareUrl);

        var match = AtsListingMatcher.Match(
            "Data Engineer",
            "London",
            [Listing("Data Engineer", "London, UK", "   "), real]);

        Assert.Same(real, match.Listing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--")]
    public void Match_answers_nothing_for_a_posting_with_no_usable_title(string? postingTitle)
    {
        // The shape this mistake takes is a column that was never populated, and the dangerous
        // reading is "matches everything" - a blank title folds to the same empty string a blank
        // listing title does.
        var match = AtsListingMatcher.Match(postingTitle, "London", [Listing("--", "London, UK")]);

        Assert.Equal(AtsListingMatchOutcome.NoMatch, match.Outcome);
    }

    [Fact]
    public void Match_answers_nothing_for_a_board_that_publishes_nothing()
        => Assert.Equal(
            AtsListingMatchOutcome.NoMatch,
            AtsListingMatcher.Match("Data Engineer", "London", []).Outcome);

    [Fact]
    public void Match_does_not_depend_on_the_order_the_board_returned()
    {
        var first = Listing("Data Engineer", "London, UK", "https://boards.greenhouse.io/acme/jobs/1");
        var second = Listing("Data Engineer", "London, UK", "https://boards.greenhouse.io/acme/jobs/2");

        Assert.Equal(
            AtsListingMatchOutcome.Ambiguous,
            AtsListingMatcher.Match("Data Engineer", "London", [first, second]).Outcome);

        Assert.Equal(
            AtsListingMatchOutcome.Ambiguous,
            AtsListingMatcher.Match("Data Engineer", "London", [second, first]).Outcome);
    }

    [Fact]
    public void Match_declines_a_listing_that_names_only_a_country()
    {
        // A known cost, asserted rather than left to be rediscovered. Separating a country from a
        // city needs the gazetteer this rule refuses to carry, so "United Kingdom" reads as a
        // place that disagrees with London and the recovery is lost. The safe direction.
        var match = AtsListingMatcher.Match("Data Engineer", "London", [Listing("Data Engineer", "United Kingdom")]);

        Assert.Equal(AtsListingMatchOutcome.NoMatch, match.Outcome);
    }

    [Fact]
    public void Match_declines_a_title_carrying_a_department_the_posting_does_not()
    {
        // The other known cost. A trailing word saying what the job is, is not noise - stripping
        // it would match a payments team's vacancy against an advert that never mentioned one.
        var match = AtsListingMatcher.Match(
            "Software Engineer",
            "London",
            [Listing("Software Engineer, Payments", "London, UK")]);

        Assert.Equal(AtsListingMatchOutcome.NoMatch, match.Outcome);
    }

    [Fact]
    public void An_abstention_cannot_be_built_out_of_one_listing()
    {
        // The factories are what make an outcome and its payload impossible to disagree: there is
        // no way to express a match with no listing, and no way to express a tie between one.
        Assert.Throws<ArgumentException>(
            () => AtsListingMatch.Abstained([Listing("Data Engineer", "London, UK")]));

        Assert.Equal(AtsListingMatchOutcome.NoMatch, AtsListingMatch.None.Outcome);
        Assert.Null(AtsListingMatch.None.Listing);
        Assert.Empty(AtsListingMatch.None.Contenders);
    }
}
