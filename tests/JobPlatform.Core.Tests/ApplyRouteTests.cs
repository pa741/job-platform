using JobPlatform.Core.Applications;
using JobPlatform.Core.Dedup;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// <see cref="ApplyRoute"/> - whether an employer's form is reachable from a queue row.
/// </summary>
/// <remarks>
/// <b>The case worth writing this file for is the third one.</b> A published link into WhatJobs
/// and a LinkedIn posting whose address the board withheld both read
/// <see cref="SubmissionChannel.Ats"/> and both read <see cref="AtsVendor.Aggregator"/>, so the
/// obvious spelling of this rule - channel plus vendor - admits the row the skip exists for. Only
/// <see cref="ApplyUrlSource"/> separates them. That mistake was made while writing this and caught
/// by a fixture, so it gets an assertion rather than a comment.
/// </remarks>
public sealed class ApplyRouteTests
{
    [Theory]
    [InlineData(AtsVendor.Greenhouse)]
    [InlineData(AtsVendor.Lever)]
    [InlineData(AtsVendor.Workday)]
    [InlineData(AtsVendor.Other)]
    public void An_employers_own_system_is_reachable_however_the_address_was_found(AtsVendor vendor)
    {
        // Every provenance, because a link is a link: published, borrowed from the same job on
        // another board, or answered by the employer's own board. Whose form is at the end of it
        // is the only question this arm asks.
        Assert.True(ApplyRoute.ReachesAnEmployer(
            SubmissionChannel.Ats, ApplyUrlSource.Posting, vendor));

        Assert.True(ApplyRoute.ReachesAnEmployer(
            SubmissionChannel.Ats, ApplyUrlSource.MatchedOnAnotherBoard, vendor));
    }

    [Fact]
    public void An_offsite_listing_with_no_address_is_reachable_through_the_boards_apply_link()
    {
        // The LinkedIn shape: the board says the employer takes the application and publishes no
        // address, so the row carries the board's own posting page and its vendor reads as one.
        Assert.True(ApplyRoute.ReachesAnEmployer(
            SubmissionChannel.Ats, ApplyUrlSource.BoardPosting, AtsVendor.Aggregator));

        Assert.True(ApplyRoute.FollowsBoardApplyLink(
            SubmissionChannel.Ats, ApplyUrlSource.BoardPosting));
    }

    [Fact]
    public void A_published_link_into_another_job_board_is_not_reachable()
    {
        // Same channel and same vendor as the case above, and the opposite answer. This is the
        // whole reason the provenance is a parameter.
        Assert.False(ApplyRoute.ReachesAnEmployer(
            SubmissionChannel.Ats, ApplyUrlSource.Posting, AtsVendor.Aggregator));

        Assert.False(ApplyRoute.FollowsBoardApplyLink(
            SubmissionChannel.Ats, ApplyUrlSource.Posting));
    }

    [Fact]
    public void A_borrowed_link_into_another_job_board_is_not_reachable_either()
    {
        // The fourth rung: a link taken from the same job on a different board, which can itself
        // be a re-listing. The queue reports the vendor beside it so a caller can refuse it.
        Assert.False(ApplyRoute.ReachesAnEmployer(
            SubmissionChannel.Ats, ApplyUrlSource.MatchedOnAnotherBoard, AtsVendor.Aggregator));
    }

    [Fact]
    public void A_board_hosted_listing_is_not_reachable()
    {
        // Easy Apply. Driveable by an authenticated browser and deliberately still out - the cost
        // is in mcp_handoff.md 3.2b and none of it is about whether the form can be filled in.
        Assert.False(ApplyRoute.ReachesAnEmployer(
            SubmissionChannel.Board, ApplyUrlSource.BoardPosting, AtsVendor.Aggregator));

        Assert.False(ApplyRoute.FollowsBoardApplyLink(
            SubmissionChannel.Board, ApplyUrlSource.BoardPosting));
    }

    [Fact]
    public void A_listing_that_says_nothing_about_where_it_applies_is_not_reachable()
    {
        // Route unknown - 4,346 LinkedIn rows on 2026-09-10, scraped before the classifier
        // shipped. Nothing has established that an employer is at the end of anything, so there
        // is no apply link to promise. They reclassify when a later search turns them up again.
        Assert.False(ApplyRoute.ReachesAnEmployer(
            SubmissionChannel.Unknown, ApplyUrlSource.BoardPosting, AtsVendor.Aggregator));
    }

    [Fact]
    public void Unknown_is_not_treated_as_an_employer()
    {
        // AtsVendor.Unknown means there is nothing at the end of the link, which is a different
        // fact from "the link is another board" and is not a route either.
        Assert.False(ApplyRoute.ReachesAnEmployer(
            SubmissionChannel.Unknown, ApplyUrlSource.BoardPosting, AtsVendor.Unknown));
    }
}
