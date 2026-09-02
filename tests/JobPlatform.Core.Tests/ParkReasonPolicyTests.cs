using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The parking policy - whether, and when, a parked posting comes back to the queue.
/// </summary>
/// <remarks>
/// The failure this file is written against is silence. A posting wrongly classified as
/// permanent leaves a queue nobody is comparing against anything, and the absence of a job from
/// a list of jobs is not a thing a person notices - so every assertion here is about a posting
/// that must return, or about one that must not, rather than about the shape of the enum.
///
/// The exhaustiveness test is the load-bearing one: <see cref="ParkReasonPolicy.Requeue"/> ends
/// in a discard arm, deliberately, so the compiler will not object to a reason added without a
/// decision. This is where that objection lives instead.
/// </remarks>
public sealed class ParkReasonPolicyTests
{
    [Fact]
    public void A_reason_is_never_zero_so_an_unset_value_cannot_read_as_a_real_one()
    {
        // ParkedReason is a nullable column and null is already "not parked". A zero member
        // would be a second spelling of that absence, and default(ParkReason) reaching the
        // column would read as somebody's deliberate decision to park.
        Assert.False(Enum.IsDefined((ParkReason)0));
        Assert.False(Enum.IsDefined((ParkRequeue)0));

        Assert.All(Enum.GetValues<ParkReason>(), reason => Assert.True((int)reason >= 1));
    }

    [Fact]
    public void The_numbering_is_the_stored_value_and_is_part_of_the_contract()
    {
        // Submissions.ParkedReason holds the int, so renumbering a member does not rename
        // anything - it silently reinterprets every row already written. Pinned for the same
        // reason a concept key is the identity and its label is an attribute.
        Assert.Equal(1, (int)ParkReason.Expired);
        Assert.Equal(2, (int)ParkReason.Duplicate);
        Assert.Equal(3, (int)ParkReason.LoginRequired);
        Assert.Equal(4, (int)ParkReason.Captcha);
        Assert.Equal(5, (int)ParkReason.AccountRequired);
        Assert.Equal(6, (int)ParkReason.MissingAnswer);
        Assert.Equal(7, (int)ParkReason.FormError);
        Assert.Equal(8, (int)ParkReason.OutOfQuota);
    }

    /// <summary>
    /// Every reason has a retry decision, and the three groups account for all of them.
    /// </summary>
    /// <remarks>
    /// Written the way <c>The_phase_ordering_the_fold_depends_on_is_the_process_order</c> is,
    /// and for the same reason: the groups are spelled out here rather than read back from the
    /// policy, so this is a check on it rather than a restatement of it. A reason added without
    /// a decision falls into none of the three lists and the last assertion fails - which is the
    /// only thing standing between a new member and the lenient discard arm quietly calling it
    /// retryable.
    /// </remarks>
    [Fact]
    public void Every_reason_has_a_requeue_decision_and_the_groups_cover_the_enum()
    {
        ParkReason[] permanent =
            [ParkReason.Expired, ParkReason.Duplicate];

        ParkReason[] nextRun =
        [
            ParkReason.LoginRequired,
            ParkReason.Captcha,
            ParkReason.AccountRequired,
            ParkReason.FormError,
            ParkReason.OutOfQuota,
        ];

        ParkReason[] whenAnswered =
            [ParkReason.MissingAnswer];

        Assert.All(permanent, reason => Assert.Equal(ParkRequeue.Never, ParkReasonPolicy.Requeue(reason)));
        Assert.All(nextRun, reason => Assert.Equal(ParkRequeue.NextRun, ParkReasonPolicy.Requeue(reason)));
        Assert.All(whenAnswered, reason => Assert.Equal(ParkRequeue.WhenAnswered, ParkReasonPolicy.Requeue(reason)));

        Assert.Equal(
            Enum.GetValues<ParkReason>().Order(),
            permanent.Concat(nextRun).Concat(whenAnswered).Order());
    }

    [Theory]
    [InlineData(ParkReason.Expired)]
    [InlineData(ParkReason.Duplicate)]
    public void A_permanently_parked_posting_never_returns_even_once_an_answer_exists(ParkReason reason)
    {
        Assert.False(ParkReasonPolicy.Retryable(reason));

        // Permanence is not conditional on anything. Answering an open question raised against
        // some other field of the same posting must not resurrect a vacancy that has closed, or
        // one already applied to on another board.
        Assert.False(ParkReasonPolicy.ReturnsToQueue(reason, answerRecorded: false));
        Assert.False(ParkReasonPolicy.ReturnsToQueue(reason, answerRecorded: true));
    }

    [Theory]
    [InlineData(ParkReason.LoginRequired)]
    [InlineData(ParkReason.Captcha)]
    [InlineData(ParkReason.AccountRequired)]
    [InlineData(ParkReason.FormError)]
    [InlineData(ParkReason.OutOfQuota)]
    public void A_blocked_attempt_returns_next_run_with_nothing_answered(ParkReason reason)
    {
        // The case the shorthand `Retryable(reason) && answerRecorded` gets wrong: none of these
        // raised a question, so waiting for an answer would strand the posting for good.
        Assert.True(ParkReasonPolicy.Retryable(reason));
        Assert.True(ParkReasonPolicy.ReturnsToQueue(reason, answerRecorded: false));
        Assert.True(ParkReasonPolicy.ReturnsToQueue(reason, answerRecorded: true));
    }

    [Fact]
    public void A_missing_answer_returns_only_once_the_answer_exists()
    {
        // Retryable, so the queue's permanent-block clause lets it through - and still held back
        // until the open question is answered, because offering it unanswered produces the same
        // park on every run and nothing else.
        Assert.True(ParkReasonPolicy.Retryable(ParkReason.MissingAnswer));

        Assert.False(ParkReasonPolicy.ReturnsToQueue(ParkReason.MissingAnswer, answerRecorded: false));
        Assert.True(ParkReasonPolicy.ReturnsToQueue(ParkReason.MissingAnswer, answerRecorded: true));
    }

    [Fact]
    public void Retryable_answers_gone_for_good_rather_than_offer_it_now()
    {
        // The distinction the two functions exist to keep apart. MissingAnswer is the member
        // where they disagree, and a caller reading Retryable as the whole policy re-offers a
        // posting it cannot yet apply to.
        Assert.All(
            Enum.GetValues<ParkReason>(),
            reason => Assert.Equal(
                ParkReasonPolicy.Requeue(reason) is not ParkRequeue.Never,
                ParkReasonPolicy.Retryable(reason)));

        Assert.NotEqual(
            ParkReasonPolicy.Retryable(ParkReason.MissingAnswer),
            ParkReasonPolicy.ReturnsToQueue(ParkReason.MissingAnswer, answerRecorded: false));
    }

    [Fact]
    public void The_query_side_lists_say_the_same_thing_as_the_classification()
    {
        // These are what the queue predicate is written against, because a static call on a
        // column does not translate to SQL. They are derived from Requeue rather than typed out
        // again, and this is what pins that they still name what a reader expects.
        Assert.Equal([ParkReason.Expired, ParkReason.Duplicate], ParkReasonPolicy.Permanent);
        Assert.Equal([ParkReason.MissingAnswer], ParkReasonPolicy.AwaitingAnswer);

        var elsewhere = Enum.GetValues<ParkReason>()
            .Except(ParkReasonPolicy.Permanent)
            .Except(ParkReasonPolicy.AwaitingAnswer);

        Assert.All(elsewhere, reason => Assert.Equal(ParkRequeue.NextRun, ParkReasonPolicy.Requeue(reason)));
        Assert.NotEmpty(elsewhere);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_unrecognised_stored_reason_returns_to_the_queue_rather_than_disappearing(int stored)
    {
        // The column holds an int, and an int outlives the member that wrote it. The two
        // mistakes available here are not the same size: reading an unknown value as permanent
        // drops a live vacancy forever with nothing to notice, and reading it as retryable costs
        // one page load and a second park.
        var reason = (ParkReason)stored;

        Assert.Equal(ParkRequeue.NextRun, ParkReasonPolicy.Requeue(reason));
        Assert.True(ParkReasonPolicy.Retryable(reason));
        Assert.True(ParkReasonPolicy.ReturnsToQueue(reason, answerRecorded: false));
    }
}
