using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The day's cap, as a client sees it, and what a run may plan against it.
/// </summary>
/// <remarks>
/// Two of these are the whole point of the type and neither is arithmetic anybody would write
/// wrong on purpose: a day counted in the caller's local time names a different window from the
/// one the repository enforces, and a day already past the cap yields a negative "remaining"
/// that reads as headroom the wrong way round. The rest pin the planning contract - a ceiling,
/// never a booking.
/// </remarks>
public sealed class SubmissionQuotaTests
{
    private static readonly DateTimeOffset Midday = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Remaining_is_the_cap_less_what_the_day_has_already_recorded_as_sent()
    {
        var quota = SubmissionQuota.For(Midday, submittedOnDay: 4);

        Assert.Equal(SubmissionLimits.MaxSubmittedPerDay, quota.DailyCap);
        Assert.Equal(4, quota.SubmittedOnDay);
        Assert.Equal(SubmissionLimits.MaxSubmittedPerDay - 4, quota.Remaining);
        Assert.False(quota.IsExhausted);
    }

    /// <summary>
    /// A day holding more than the cap answers none left, not a negative number.
    /// </summary>
    /// <remarks>
    /// Reachable two ways, so it is not a hypothetical: the repository counts and then inserts
    /// without one transaction around the pair, so two clients can both pass the check; and
    /// lowering the constant leaves days already above it above it for good.
    /// </remarks>
    [Fact]
    public void Remaining_is_never_negative_when_a_day_already_holds_more_than_the_cap()
    {
        var quota = SubmissionQuota.For(Midday, submittedOnDay: SubmissionLimits.MaxSubmittedPerDay + 3);

        Assert.Equal(0, quota.Remaining);
        Assert.True(quota.IsExhausted);
    }

    [Fact]
    public void A_day_at_exactly_the_cap_is_exhausted_rather_than_having_one_left()
    {
        var quota = SubmissionQuota.For(Midday, submittedOnDay: SubmissionLimits.MaxSubmittedPerDay);

        Assert.Equal(0, quota.Remaining);
        Assert.True(quota.IsExhausted);
    }

    /// <summary>
    /// The day is the UTC one, whatever offset the instant arrived with.
    /// </summary>
    /// <remarks>
    /// The repository's window is <c>AtUtc.UtcDateTime.Date</c>, so a quota naming the caller's
    /// local day would report a burn-down for a window the cap is not enforced on - and it would
    /// be wrong for part of every day rather than obviously wrong once.
    /// </remarks>
    [Fact]
    public void The_day_is_the_utc_day_rather_than_the_offsets_own()
    {
        // 01:30 on the 3rd in Madrid is 23:30 on the 2nd in UTC.
        var earlyInMadrid = new DateTimeOffset(2026, 9, 3, 1, 30, 0, TimeSpan.FromHours(2));

        var quota = SubmissionQuota.For(earlyInMadrid, submittedOnDay: 0);

        Assert.Equal(new DateOnly(2026, 9, 2), quota.Day);
    }

    [Fact]
    public void An_impossible_count_of_sent_applications_is_refused_rather_than_folded_into_the_answer()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => SubmissionQuota.For(Midday, submittedOnDay: -1));

    [Fact]
    public void Plan_bounds_the_batch_by_what_is_left_rather_than_by_what_was_asked_for()
    {
        var quota = new SubmissionQuota(DailyCap: 25, SubmittedOnDay: 19, Day: new DateOnly(2026, 9, 2));

        Assert.Equal(6, SubmissionQuota.Plan(quota, candidateCount: 10));
    }

    [Fact]
    public void Plan_returns_the_whole_batch_where_the_day_has_room_for_all_of_it()
    {
        var quota = new SubmissionQuota(DailyCap: 25, SubmittedOnDay: 2, Day: new DateOnly(2026, 9, 2));

        Assert.Equal(10, SubmissionQuota.Plan(quota, candidateCount: 10));
    }

    /// <summary>
    /// An exhausted day plans nothing, and that is an answer rather than an error.
    /// </summary>
    /// <remarks>
    /// Written against the version that subtracts and hands back the sign: an over-spent day
    /// would plan a negative batch, which a caller looping <c>while (planned-- > 0)</c> reads as
    /// "stop" and a caller taking <c>Math.Abs</c> reads as "send three more".
    /// </remarks>
    [Fact]
    public void Plan_answers_nothing_on_a_day_that_is_already_over_the_cap()
    {
        var quota = new SubmissionQuota(DailyCap: 25, SubmittedOnDay: 28, Day: new DateOnly(2026, 9, 2));

        Assert.Equal(0, SubmissionQuota.Plan(quota, candidateCount: 10));
    }

    [Fact]
    public void Plan_of_an_empty_queue_is_nothing_even_with_the_whole_day_left()
    {
        // The queue, not the quota, is what is empty here - answering the day's remainder would
        // be a run opening tabs for postings it does not have.
        var quota = SubmissionQuota.For(Midday, submittedOnDay: 0);

        Assert.Equal(0, SubmissionQuota.Plan(quota, candidateCount: 0));
    }

    /// <summary>
    /// Planning is a ceiling and never a booking.
    /// </summary>
    /// <remarks>
    /// Nothing is reserved by asking: quota is spent by recording a <c>Submitted</c> event, and
    /// the repository stays authoritative. A plan that consumed something would let a run that
    /// asks twice send half as much, and would put a second enforcement point in Core - which is
    /// the thing the cap living in one place exists to prevent.
    /// </remarks>
    [Fact]
    public void Planning_twice_reserves_nothing_and_gives_the_same_answer()
    {
        var quota = SubmissionQuota.For(Midday, submittedOnDay: 20);

        var first = SubmissionQuota.Plan(quota, candidateCount: 10);
        var second = SubmissionQuota.Plan(quota, candidateCount: 10);

        Assert.Equal(first, second);
        Assert.Equal(20, quota.SubmittedOnDay);
    }

    [Fact]
    public void A_negative_batch_is_a_callers_bug_rather_than_an_empty_plan()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => SubmissionQuota.Plan(SubmissionQuota.For(Midday, 0), candidateCount: -1));
}
