using System.Globalization;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// What a run is, and the three things it is not.
/// </summary>
/// <remarks>
/// Two groups of assertion here, and they are testing different kinds of claim. The summary and
/// key tests pin arithmetic and a string format. The rest pin the <i>decisions</i> the remarks
/// argue for - that nothing but a client closes a run, that a run that reported nothing is not a
/// run that never reported, that the key still fits its column when the event enum grows - and
/// each of those is a property somebody would otherwise reasonably implement the other way.
/// </remarks>
public sealed class ApplyRunTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 2, 3, 0, 0, TimeSpan.Zero);

    private static ApplyRun Run(DateTimeOffset? finished = null, RunSummary? summary = null)
        => new(Id: 41, Started, finished, summary, Note: null);

    [Fact]
    public void A_run_with_no_finish_time_is_open()
        => Assert.True(Run().IsOpen);

    [Fact]
    public void A_run_the_client_finished_is_not_open()
        => Assert.False(Run(finished: Started.AddMinutes(9)).IsOpen);

    /// <summary>
    /// Time passing does not close a run.
    /// </summary>
    /// <remarks>
    /// The claim behind the whole open-run decision: no timer writes <c>FinishedAtUtc</c>, so a
    /// run abandoned by a client that died stays open forever rather than being swept closed by
    /// something that never watched it finish. The obvious implementation is a sweep, and this is
    /// what says it was not chosen.
    /// </remarks>
    [Fact]
    public void An_abandoned_run_is_still_open_because_nothing_but_the_client_closes_one()
    {
        var run = Run();
        var later = Started + ApplyRun.AbandonedAfter + TimeSpan.FromDays(30);

        Assert.True(run.IsAbandoned(later));
        Assert.True(run.IsOpen);
    }

    [Fact]
    public void An_open_run_is_abandoned_only_once_it_has_been_open_too_long()
    {
        var run = Run();

        // A run that is merely slow is not abandoned. The boundary is exact rather than
        // approximate because the whole point of passing the clock in is that it can be.
        Assert.False(run.IsAbandoned(Started));
        Assert.False(run.IsAbandoned(Started + ApplyRun.AbandonedAfter));
        Assert.True(run.IsAbandoned(Started + ApplyRun.AbandonedAfter + TimeSpan.FromTicks(1)));
    }

    /// <summary>
    /// A finished run is never abandoned, however long ago it ran.
    /// </summary>
    /// <remarks>
    /// The same rule as a closed application never being stale: it said what it had to say, and
    /// reading age alone would put every completed run from last month on a list of things to go
    /// and look at.
    /// </remarks>
    [Fact]
    public void A_finished_run_is_never_abandoned_however_old_it_is()
    {
        var run = Run(finished: Started.AddMinutes(9));

        Assert.False(run.IsAbandoned(Started.AddYears(1)));
    }

    /// <summary>
    /// The abandonment window is shorter than a UTC day, and that is the constraint on it.
    /// </summary>
    /// <remarks>
    /// The daily cap is per UTC day, so a run still readable as "open" a day later could be read
    /// as still spending a quota that has since reset. Pinned rather than left to the constant's
    /// remarks, because the number is the kind of thing somebody widens to be generous.
    /// </remarks>
    [Fact]
    public void A_run_cannot_stay_unabandoned_across_a_whole_utc_day()
        => Assert.True(ApplyRun.AbandonedAfter < TimeSpan.FromDays(1));

    /// <summary>
    /// A run that reported nothing happened is not a run that never reported.
    /// </summary>
    /// <remarks>
    /// The two want opposite responses - fill the queue, or restart the client - and a reader
    /// that folds a null summary into an empty one loses the distinction silently, which is the
    /// same fault as reading an absent apply URL as "the board hosts it".
    /// </remarks>
    [Fact]
    public void A_run_that_reported_nothing_is_distinguishable_from_one_that_never_reported()
    {
        var silent = Run(finished: Started.AddMinutes(1));
        var empty = Run(finished: Started.AddMinutes(1), summary: RunSummary.Empty);

        Assert.Null(silent.Summary);
        Assert.NotNull(empty.Summary);
        Assert.Equal(0, empty.Summary!.Considered);
        Assert.Empty(empty.Summary.ParkedByReason);
    }

    [Fact]
    public void Parked_is_summed_from_the_breakdown_rather_than_carried_beside_it()
    {
        var summary = RunSummary.From(
            considered: 12,
            submitted: 3,
            questions: 1,
            parks: [ParkReason.Captcha, ParkReason.Captcha, ParkReason.MissingAnswer]);

        Assert.Equal(3, summary.Parked);
        Assert.Equal(2, summary.ParkedByReason[ParkReason.Captcha]);
        Assert.Equal(1, summary.ParkedByReason[ParkReason.MissingAnswer]);
    }

    [Fact]
    public void From_records_only_the_reasons_that_actually_happened()
    {
        var summary = RunSummary.From(4, 1, 0, [ParkReason.Expired]);

        Assert.Single(summary.ParkedByReason);
        Assert.False(summary.ParkedByReason.ContainsKey(ParkReason.OutOfQuota));
    }

    /// <summary>
    /// The number that says a run dropped work without reporting why.
    /// </summary>
    /// <remarks>
    /// Considered against submitted and parked is the diagnostic the run exists for: forty
    /// looked at, two sent, three parked and thirty-five unaccounted for is a run to go and
    /// read the logs of, and no other figure on the record says so.
    /// </remarks>
    [Fact]
    public void Unaccounted_names_what_the_run_neither_sent_nor_parked()
    {
        var summary = RunSummary.From(
            considered: 40,
            submitted: 2,
            questions: 0,
            parks: [ParkReason.Expired, ParkReason.Duplicate, ParkReason.FormError]);

        Assert.Equal(35, summary.Unaccounted);
    }

    /// <summary>
    /// Counts that do not add up are reported as they are, not tidied.
    /// </summary>
    /// <remarks>
    /// These are the client's own tallies and nothing audits them, so they can be wrong.
    /// Clamping at zero would turn a visibly broken count into a plausible one - the shape of
    /// failure this codebase has paid for three times, where the symptom was a number nobody was
    /// comparing to anything.
    /// </remarks>
    [Fact]
    public void Unaccounted_stays_negative_when_the_run_claims_more_than_it_considered()
    {
        var summary = RunSummary.From(1, 4, 0, [ParkReason.Captcha]);

        Assert.Equal(-4, summary.Unaccounted);
    }

    /// <summary>
    /// The breakdown handed out cannot be edited through the summary.
    /// </summary>
    /// <remarks>
    /// A record whose one reference member is a live dictionary is a record any holder can
    /// rewrite after the fact, which would make the summary disagree with the row it was read
    /// from and with the <see cref="RunSummary.Parked"/> total somebody already read off it.
    /// </remarks>
    [Fact]
    public void The_park_breakdown_cannot_be_mutated_through_the_summary()
    {
        var summary = RunSummary.From(2, 1, 0, [ParkReason.LoginRequired]);

        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<ParkReason, int>)summary.ParkedByReason).Add(ParkReason.Expired, 1));
    }

    /// <summary>
    /// Two summaries with the same tallies are the same summary.
    /// </summary>
    /// <remarks>
    /// The generated record equality would compare the breakdown by reference, so this fails
    /// without the hand-written one - and it fails in the direction that costs something, because
    /// a summary that never equals itself is a summary every "write only if it changed" path
    /// rewrites every time.
    /// </remarks>
    [Fact]
    public void Two_summaries_with_the_same_tallies_are_equal_however_they_were_built()
    {
        var tallied = RunSummary.From(
            9, 2, 1, [ParkReason.Captcha, ParkReason.Expired, ParkReason.Captcha]);

        var built = new RunSummary(9, 2, 1, new Dictionary<ParkReason, int>
        {
            [ParkReason.Expired] = 1,
            [ParkReason.Captcha] = 2,
        });

        Assert.Equal(tallied, built);
        Assert.Equal(tallied.GetHashCode(), built.GetHashCode());
    }

    /// <summary>
    /// A reason tallied at zero is a reason nothing happened for.
    /// </summary>
    /// <remarks>
    /// A caller writing every member out with its count would otherwise never compare equal to
    /// one that wrote only what occurred, and both are honest descriptions of the same run.
    /// </remarks>
    [Fact]
    public void A_reason_tallied_at_zero_is_the_same_summary_as_one_that_never_happened()
    {
        var occurred = RunSummary.From(3, 1, 0, [ParkReason.Duplicate]);

        var spelledOut = new RunSummary(3, 1, 0, new Dictionary<ParkReason, int>
        {
            [ParkReason.Duplicate] = 1,
            [ParkReason.Captcha] = 0,
            [ParkReason.Expired] = 0,
        });

        Assert.Equal(occurred, spelledOut);
        Assert.Equal(occurred.GetHashCode(), spelledOut.GetHashCode());
    }

    /// <summary>
    /// Equality is not so loose that it stops telling runs apart.
    /// </summary>
    /// <remarks>
    /// Written because the interesting way to get the previous two tests passing is to ignore the
    /// breakdown altogether, which would make every summary with the same totals identical and
    /// throw away the only part that says why a run sent nothing.
    /// </remarks>
    [Fact]
    public void Summaries_that_parked_for_different_reasons_are_not_equal()
    {
        var captcha = RunSummary.From(3, 1, 0, [ParkReason.Captcha]);
        var expired = RunSummary.From(3, 1, 0, [ParkReason.Expired]);

        Assert.NotEqual(captcha, expired);
        Assert.Equal(captcha.Parked, expired.Parked);
    }

    [Fact]
    public void The_idempotency_key_is_namespaced_by_run_posting_and_type()
        => Assert.Equal("41:9027:Submitted", ApplyRun.Key(41, 9027, SubmissionEventType.Submitted));

    /// <summary>
    /// One run can record two phases against one posting.
    /// </summary>
    /// <remarks>
    /// Without the type in the key a run that recorded a submission and then an acknowledgement
    /// on the same posting would find its second write converging onto the first and answering
    /// <c>AlreadyRecorded</c> - a silently lost event, which is the failure the log's whole shape
    /// exists to make impossible.
    /// </remarks>
    [Fact]
    public void The_key_separates_two_phases_recorded_on_one_posting_in_one_run()
        => Assert.NotEqual(
            ApplyRun.Key(41, 9027, SubmissionEventType.Submitted),
            ApplyRun.Key(41, 9027, SubmissionEventType.Acknowledged));

    [Fact]
    public void The_key_separates_two_runs_over_the_same_posting()
        => Assert.NotEqual(
            ApplyRun.Key(41, 9027, SubmissionEventType.Submitted),
            ApplyRun.Key(42, 9027, SubmissionEventType.Submitted));

    /// <summary>
    /// The longest key this can produce still fits the column.
    /// </summary>
    /// <remarks>
    /// The repository truncates an over-long key rather than refusing it, and a truncated key
    /// collides with its neighbours - so the bound has to hold for the worst case rather than for
    /// the ids that happen to exist today. Taken over every member of the event enum, so that
    /// adding a longer-named phase fails here rather than in production.
    /// </remarks>
    [Fact]
    public void The_longest_key_the_helper_can_produce_still_fits_the_column()
    {
        var longest = Enum.GetValues<SubmissionEventType>()
            .MaxBy(type => type.ToString().Length);

        var key = ApplyRun.Key(long.MaxValue, long.MaxValue, longest);

        Assert.True(
            key.Length <= SubmissionLimits.MaxIdempotencyKeyLength,
            $"'{key}' is {key.Length} characters and the column takes {SubmissionLimits.MaxIdempotencyKeyLength}.");
    }

    /// <summary>
    /// The key does not depend on the culture the calling thread is carrying.
    /// </summary>
    /// <remarks>
    /// The database compares these byte for byte, so a key built under a culture with its own
    /// negative sign or digit separator is a key that never matches the one a retry produces.
    /// The culture here is built rather than named so the assertion does not depend on what a
    /// particular machine's ICU data says about a particular locale.
    /// </remarks>
    [Fact]
    public void The_key_is_written_in_an_invariant_culture()
    {
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "MINUS";

        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = hostile;

            Assert.Equal("-12:-34:Submitted", ApplyRun.Key(-12, -34, SubmissionEventType.Submitted));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
