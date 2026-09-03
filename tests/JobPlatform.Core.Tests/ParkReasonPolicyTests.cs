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
        Assert.Equal(9, (int)ParkReason.NoCvVariant);
    }

    /// <summary>
    /// Every reason has a retry decision, and the four groups account for all of them.
    /// </summary>
    /// <remarks>
    /// Written the way <c>The_phase_ordering_the_fold_depends_on_is_the_process_order</c> is,
    /// and for the same reason: the groups are spelled out here rather than read back from the
    /// policy, so this is a check on it rather than a restatement of it. A reason added without
    /// a decision falls into none of the four lists and the last assertion fails - which is the
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

        ParkReason[] whenCovered =
            [ParkReason.NoCvVariant];

        Assert.All(permanent, reason => Assert.Equal(ParkRequeue.Never, ParkReasonPolicy.Requeue(reason)));
        Assert.All(nextRun, reason => Assert.Equal(ParkRequeue.NextRun, ParkReasonPolicy.Requeue(reason)));
        Assert.All(whenAnswered, reason => Assert.Equal(ParkRequeue.WhenAnswered, ParkReasonPolicy.Requeue(reason)));
        Assert.All(whenCovered, reason => Assert.Equal(ParkRequeue.WhenCovered, ParkReasonPolicy.Requeue(reason)));

        Assert.Equal(
            Enum.GetValues<ParkReason>().Order(),
            permanent.Concat(nextRun).Concat(whenAnswered).Concat(whenCovered).Order());
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

        // Nor by a CV written since. A vacancy that has closed does not reopen because the
        // candidate's library got better, and a job already applied to is still already applied
        // to.
        Assert.False(ParkReasonPolicy.ReturnsToQueue(
            reason, answerRecorded: true, coveringVariantExists: true));
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

        // And it reads the coverage argument no more than the answer one: a captcha waiting on a
        // CV nobody was asked to write is the same posting stranded for good, by the other
        // conditional class this time.
        Assert.True(ParkReasonPolicy.ReturnsToQueue(
            reason, answerRecorded: false, coveringVariantExists: false));
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

    /// <summary>
    /// A posting nothing in the library fits waits for the CV, not for the next run.
    /// </summary>
    /// <remarks>
    /// <b>The whole point of the third requeue class, asserted as the loop it prevents.</b> The
    /// gap is computed from the posting's requirements and the variants that exist, and a run
    /// changes neither - so a reason classified <see cref="ParkRequeue.NextRun"/> here would put
    /// the same posting in front of the same library on every pass, compute the same gap, and park
    /// it again for ever, at a page load a time. Retryable is still true, because the CV is one
    /// somebody can write.
    /// </remarks>
    [Fact]
    public void A_posting_no_cv_variant_covers_returns_only_once_one_that_covers_it_is_written()
    {
        Assert.True(ParkReasonPolicy.Retryable(ParkReason.NoCvVariant));
        Assert.NotEqual(ParkRequeue.NextRun, ParkReasonPolicy.Requeue(ParkReason.NoCvVariant));

        Assert.False(ParkReasonPolicy.ReturnsToQueue(
            ParkReason.NoCvVariant, answerRecorded: false, coveringVariantExists: false));

        Assert.True(ParkReasonPolicy.ReturnsToQueue(
            ParkReason.NoCvVariant, answerRecorded: false, coveringVariantExists: true));
    }

    /// <summary>
    /// Each conditional reason reads its own fact, and neither is released by the other's.
    /// </summary>
    /// <remarks>
    /// The arrangement an <c>or</c> between the two conditions would quietly accept, and the one
    /// a swapped pair of arguments at a call site produces. Answering an open question does not
    /// write a CV, and writing a CV does not answer a question - so a posting held for either must
    /// stay held while only the other has moved.
    /// </remarks>
    [Fact]
    public void Each_conditional_reason_reads_its_own_fact_and_not_the_other_ones()
    {
        Assert.False(ParkReasonPolicy.ReturnsToQueue(
            ParkReason.MissingAnswer, answerRecorded: false, coveringVariantExists: true));

        Assert.False(ParkReasonPolicy.ReturnsToQueue(
            ParkReason.NoCvVariant, answerRecorded: true, coveringVariantExists: false));
    }

    /// <summary>
    /// A missing CV is not filed with the parks that wait on an answer.
    /// </summary>
    /// <remarks>
    /// <b>The cheaper-looking change, and the one that reintroduces the loop.</b> Both reasons
    /// wait on a fact, so a single conditional class would have taken this one and left every
    /// caller compiling. It would also be wrong in the queue: a park awaiting an answer names the
    /// question it waits on, a park for a missing CV raises no question at all - the report is
    /// aggregated rather than asked per posting - and the predicate's fallback for a park naming
    /// no question holds the row only while some other advert's question is outstanding. So the
    /// posting would come back when an unrelated answer arrived, meet the same library, and park
    /// again.
    /// </remarks>
    [Fact]
    public void A_park_waiting_on_a_cv_is_not_filed_with_the_ones_waiting_on_an_answer()
    {
        Assert.DoesNotContain(ParkReason.NoCvVariant, ParkReasonPolicy.AwaitingAnswer);
        Assert.DoesNotContain(ParkReason.MissingAnswer, ParkReasonPolicy.AwaitingCvVariant);

        Assert.NotEqual(
            ParkReasonPolicy.Requeue(ParkReason.MissingAnswer),
            ParkReasonPolicy.Requeue(ParkReason.NoCvVariant));
    }

    /// <summary>
    /// An omitted coverage argument leaves the posting parked rather than offering it.
    /// </summary>
    /// <remarks>
    /// The default is the state the row is already in - nothing covered this posting, or it would
    /// not have been put down - so a caller that has not looked at the library repeats the park
    /// rather than guessing towards a re-offer. The lenient direction is right for an unrecognised
    /// stored value, where the alternative is losing a live vacancy silently; it is wrong here,
    /// where the alternative is the loop.
    /// </remarks>
    [Fact]
    public void An_omitted_coverage_argument_holds_the_posting_rather_than_offering_it()
    {
        Assert.False(ParkReasonPolicy.ReturnsToQueue(ParkReason.NoCvVariant, answerRecorded: true));
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

        Assert.NotEqual(
            ParkReasonPolicy.Retryable(ParkReason.NoCvVariant),
            ParkReasonPolicy.ReturnsToQueue(
                ParkReason.NoCvVariant, answerRecorded: true, coveringVariantExists: false));
    }

    [Fact]
    public void The_query_side_lists_say_the_same_thing_as_the_classification()
    {
        // These are what the queue predicate is written against, because a static call on a
        // column does not translate to SQL. They are derived from Requeue rather than typed out
        // again, and this is what pins that they still name what a reader expects.
        Assert.Equal([ParkReason.Expired, ParkReason.Duplicate], ParkReasonPolicy.Permanent);
        Assert.Equal([ParkReason.MissingAnswer], ParkReasonPolicy.AwaitingAnswer);
        Assert.Equal([ParkReason.NoCvVariant], ParkReasonPolicy.AwaitingCvVariant);

        var elsewhere = Enum.GetValues<ParkReason>()
            .Except(ParkReasonPolicy.Permanent)
            .Except(ParkReasonPolicy.AwaitingAnswer)
            .Except(ParkReasonPolicy.AwaitingCvVariant);

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

        // Including with neither conditional fact supplied. The discard arm is the lenient one on
        // purpose, and adding a conditional class must not quietly move an unknown value into it -
        // a stored int nobody can name would then be held for a CV nobody knows the shape of.
        Assert.True(ParkReasonPolicy.ReturnsToQueue(
            reason, answerRecorded: false, coveringVariantExists: false));
    }
}
