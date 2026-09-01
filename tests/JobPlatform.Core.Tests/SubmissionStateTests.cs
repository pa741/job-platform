using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The fold from an event log to a status.
/// </summary>
/// <remarks>
/// Every test here is written against the version of the rule that is wrong: the most recent
/// event winning, a terminal event being outranked, staleness read only from events, a closed
/// application going quiet. Each of those is what the obvious implementation does, so each
/// assertion is a check rather than a restatement.
/// </remarks>
public sealed class SubmissionStateTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day) => new(2026, 8, day, 9, 0, 0, TimeSpan.Zero);

    private static SubmissionEvent Event(
        int day,
        SubmissionEventType type,
        string? stage = null,
        SubmissionEventSource source = SubmissionEventSource.Candidate)
        => new(At(day), type, stage, source, Note: null);

    [Fact]
    public void A_submission_with_no_events_has_no_phase_rather_than_a_default_one()
    {
        var status = SubmissionState.Fold(Created, [], Created.AddDays(1));

        // Null, not Submitted and not a Created member. "Nothing has happened" and "it was sent"
        // are different facts and a client has to be able to tell them apart.
        Assert.Null(status.Phase);
        Assert.Equal(0, status.EventCount);
        Assert.Equal(Created, status.LastActivityUtc);
        Assert.False(status.IsClosed);
    }

    [Fact]
    public void Staleness_before_any_event_is_measured_from_the_creation_time()
    {
        // A row created and never sent still goes quiet, and reading staleness only from events
        // would leave it fresh forever.
        var fresh = SubmissionState.Fold(Created, [], Created + SubmissionState.StaleAfter);
        var stale = SubmissionState.Fold(Created, [], Created + SubmissionState.StaleAfter + TimeSpan.FromTicks(1));

        Assert.False(fresh.IsStale);
        Assert.True(stale.IsStale);
    }

    [Fact]
    public void The_furthest_advanced_phase_wins_rather_than_the_most_recent_event()
    {
        // The case that breaks "take the latest": an inbox reader catching up delivers an
        // acknowledgement long after the offer it preceded. Ordering by time would walk the
        // application backwards from OfferReceived to Acknowledged.
        SubmissionEvent[] events =
        [
            Event(2, SubmissionEventType.Submitted),
            Event(9, SubmissionEventType.OfferReceived),
            Event(12, SubmissionEventType.Acknowledged),
        ];

        var status = SubmissionState.Fold(Created, events, At(13));

        Assert.Equal(SubmissionEventType.OfferReceived, status.Phase);

        // The phase did not move, but something happened, and staleness reads activity of any
        // kind rather than only what advanced the application.
        Assert.Equal(At(12), status.LastActivityUtc);
    }

    [Fact]
    public void Order_of_the_input_does_not_change_the_answer()
    {
        SubmissionEvent[] events =
        [
            Event(9, SubmissionEventType.InterviewScheduled, "Tech round 2"),
            Event(2, SubmissionEventType.Submitted),
            Event(5, SubmissionEventType.Acknowledged),
        ];

        var forwards = SubmissionState.Fold(Created, events, At(10));
        var backwards = SubmissionState.Fold(Created, [.. events.Reverse()], At(10));

        Assert.Equal(forwards, backwards);
        Assert.Equal(SubmissionEventType.InterviewScheduled, forwards.Phase);
    }

    [Fact]
    public void A_terminal_event_wins_even_when_something_later_did_not_close_it()
    {
        // The automated "thanks for applying" that arrives after the rejection. Taking the
        // furthest-advanced non-terminal phase would report this as acknowledged and put a dead
        // application back on the list.
        SubmissionEvent[] events =
        [
            Event(2, SubmissionEventType.Submitted),
            Event(6, SubmissionEventType.Rejected),
            Event(8, SubmissionEventType.Acknowledged),
        ];

        var status = SubmissionState.Fold(Created, events, At(9));

        Assert.Equal(SubmissionEventType.Rejected, status.Phase);
        Assert.True(status.IsClosed);
    }

    /// <summary>
    /// The numbering the fold leans on, pinned so a new member cannot quietly break it.
    /// </summary>
    /// <remarks>
    /// Written after an earlier version of this test asserted a case the naive implementations
    /// both got right, which made it a restatement of the enum rather than a check on it. What
    /// the fold actually depends on is two properties: that the non-terminal members ascend in
    /// the order an application moves through them, so "furthest advanced" is a comparison; and
    /// that terminality is asked of <c>IsTerminal</c> rather than inferred from being larger.
    /// Inserting a phase in the middle, or adding a terminal one below the others, breaks the
    /// first and this fails; the second is what stops it breaking silently.
    /// </remarks>
    [Fact]
    public void The_phase_ordering_the_fold_depends_on_is_the_process_order()
    {
        SubmissionEventType[] inProcessOrder =
        [
            SubmissionEventType.Submitted,
            SubmissionEventType.Acknowledged,
            SubmissionEventType.ScreeningScheduled,
            SubmissionEventType.InterviewScheduled,
            SubmissionEventType.OfferReceived,
        ];

        Assert.Equal(inProcessOrder, inProcessOrder.OrderBy(t => t));
        Assert.All(inProcessOrder, type => Assert.False(type.IsTerminal()));

        // Every member is accounted for: a phase added without a decision about terminality is
        // a phase the fold will silently treat as open.
        SubmissionEventType[] terminal =
            [SubmissionEventType.Rejected, SubmissionEventType.Withdrawn];

        Assert.All(terminal, type => Assert.True(type.IsTerminal()));
        Assert.Equal(
            Enum.GetValues<SubmissionEventType>().Order(),
            inProcessOrder.Concat(terminal).Order());
    }

    [Fact]
    public void Between_two_terminal_events_the_later_one_takes_it()
    {
        SubmissionEvent[] events =
        [
            Event(4, SubmissionEventType.Withdrawn),
            Event(6, SubmissionEventType.Rejected),
        ];

        // Withdrawn is the larger member; Rejected is the later event. Time decides.
        Assert.Equal(SubmissionEventType.Rejected, SubmissionState.Fold(Created, events, At(7)).Phase);
    }

    [Fact]
    public void A_closed_application_is_never_stale()
    {
        SubmissionEvent[] events = [Event(2, SubmissionEventType.Rejected)];

        var status = SubmissionState.Fold(Created, events, At(2) + SubmissionState.StaleAfter.Add(TimeSpan.FromDays(30)));

        // An employer that stopped replying has gone quiet; one that said no has not. Reading
        // staleness from the clock alone would leave every rejection nagging forever.
        Assert.False(status.IsStale);
        Assert.True(status.IsClosed);
    }

    [Fact]
    public void Staleness_is_measured_at_the_boundary_from_the_last_event()
    {
        SubmissionEvent[] events = [Event(2, SubmissionEventType.Submitted)];

        var fresh = SubmissionState.Fold(Created, events, At(2) + SubmissionState.StaleAfter);
        var stale = SubmissionState.Fold(Created, events, At(2) + SubmissionState.StaleAfter + TimeSpan.FromTicks(1));

        Assert.False(fresh.IsStale);
        Assert.True(stale.IsStale);
    }

    [Fact]
    public void The_deciding_events_stage_is_carried_out_and_the_later_of_two_wins()
    {
        SubmissionEvent[] events =
        [
            Event(4, SubmissionEventType.InterviewScheduled, "Tech round 1"),
            Event(9, SubmissionEventType.InterviewScheduled, "Tech round 2"),
        ];

        // Two events of the same phase: "where is this now" means the later one. The round is
        // free text on the event and deliberately not a member of the enum.
        Assert.Equal("Tech round 2", SubmissionState.Fold(Created, events, At(10)).Stage);
    }
}
