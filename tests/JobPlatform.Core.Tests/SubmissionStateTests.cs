using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The fold from an event log to a status, and the shape of the events it folds.
/// </summary>
/// <remarks>
/// Every test here is written against the version of the rule that is wrong: the most recent
/// event winning, a terminal event being outranked, staleness read only from events, a closed
/// application going quiet. Each of those is what the obvious implementation does, so each
/// assertion is a check rather than a restatement.
///
/// The evidence tests at the end follow the same discipline. What is wrong there is the assumption
/// that proof belongs to the application: a submission is sent once, its log carries several claims
/// about it, and each claim's evidence has to survive the next one. The other wrong version is a
/// null check, which reads an empty string scraped off an unrendered page as a capture.
/// </remarks>
public sealed class SubmissionStateTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day) => new(2026, 8, day, 9, 0, 0, TimeSpan.Zero);

    private static SubmissionEvent Event(
        int day,
        SubmissionEventType type,
        string? stage = null,
        SubmissionEventSource source = SubmissionEventSource.Candidate,
        SubmissionEvidence? evidence = null)
        => new(At(day), type, stage, source, Note: null) { Evidence = evidence };

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

    [Fact]
    public void An_event_built_the_way_every_existing_caller_builds_one_carries_no_evidence()
    {
        // The five positional arguments, untouched. Evidence went on as an init property rather
        // than a sixth parameter exactly so this call keeps compiling - and so does the identical
        // shape inside SubmissionRepository's EF projection, where an omitted optional argument is
        // a compile error rather than a default. If this line ever needs a null on the end, that
        // decision has been reversed and every expression-tree call site has gone red with it.
        var plain = new SubmissionEvent(
            At(2), SubmissionEventType.Submitted, "Applied", SubmissionEventSource.Client, "note");

        Assert.Null(plain.Evidence);

        var witnessed = plain with { Evidence = new SubmissionEvidence { ConfirmationRef = "A-1" } };

        // Attaching proof does not restate the claim.
        Assert.Equal(plain.AtUtc, witnessed.AtUtc);
        Assert.Equal(plain.Note, witnessed.Note);
        Assert.Equal("A-1", witnessed.Evidence?.ConfirmationRef);
    }

    [Fact]
    public void Two_events_alike_but_for_their_evidence_are_not_equal()
    {
        var bare = Event(2, SubmissionEventType.Submitted);
        var witnessed = Event(
            2, SubmissionEventType.Submitted, evidence: new SubmissionEvidence { ConfirmationRef = "A-1" });

        // Equality has to reach the evidence, or a read path that forgets to project the columns
        // back produces a value equal to the one that carried them - a loss nothing else reports.
        Assert.NotEqual(bare, witnessed);
        Assert.Equal(
            witnessed,
            Event(2, SubmissionEventType.Submitted, evidence: new SubmissionEvidence { ConfirmationRef = "A-1" }));
    }

    /// <summary>
    /// Evidence belongs to one claim, and the fold does not read it.
    /// </summary>
    /// <remarks>
    /// Both halves of the design's argument, asserted together. A submission is sent once but its
    /// log carries several assertions about it, so the reference captured when it was sent has to
    /// still be there after an interview is recorded a week later - on <c>Submissions</c> there
    /// would be one slot and the second capture would have overwritten the first. And evidence is
    /// proof <i>of</i> a claim rather than part of one, so attaching it must not move the phase,
    /// the stage or the staleness by a tick.
    /// </remarks>
    [Fact]
    public void Each_event_keeps_its_own_evidence_and_none_of_it_reaches_the_fold()
    {
        string[] filled = ["full_name", "email"];

        SubmissionEvent[] witnessed =
        [
            Event(2, SubmissionEventType.Submitted, evidence: new SubmissionEvidence
            {
                ConfirmationRef = "A-1",
                FinalUrl = "https://ats.example/confirm?ref=A-1",
                SubmittedFields = filled,
            }),
            Event(9, SubmissionEventType.InterviewScheduled, "Tech round 1",
                evidence: new SubmissionEvidence { ConfirmationRef = "INT-77" }),
        ];

        SubmissionEvent[] bare =
        [
            Event(2, SubmissionEventType.Submitted),
            Event(9, SubmissionEventType.InterviewScheduled, "Tech round 1"),
        ];

        Assert.Equal(
            SubmissionState.Fold(Created, bare, At(10)),
            SubmissionState.Fold(Created, witnessed, At(10)));

        Assert.Equal("A-1", witnessed[0].Evidence?.ConfirmationRef);
        Assert.Equal("INT-77", witnessed[1].Evidence?.ConfirmationRef);
        Assert.Equal(filled, witnessed[0].Evidence?.SubmittedFields);
    }

    [Fact]
    public void Evidence_that_captured_nothing_is_empty_even_when_the_strings_are_blank()
    {
        SubmissionEvidence[] captured =
        [
            new(),
            new() { ConfirmationRef = "", FinalUrl = "   ", ScreenshotRef = "\t" },
            new() { SubmittedFields = [] },
            new() { SubmittedFields = ["", "  "] },
        ];

        // A selector that matched an empty element yields "" rather than null, and enumerating a
        // page that had not finished rendering yields a list of blanks. Both pass a null check,
        // and both would put an evidence block on the dashboard with nothing inside it.
        Assert.All(captured, evidence => Assert.True(evidence.IsEmpty));
    }

    [Fact]
    public void Any_one_capture_is_enough_for_the_evidence_to_count()
    {
        // Each alone, because the run that comes back with only a reference and the run that comes
        // back with only a screenshot are both ordinary, and either is worth keeping. A rule that
        // wanted two of them would discard the evidence from exactly the runs that went wrong.
        Assert.False(new SubmissionEvidence { ConfirmationRef = "A-1" }.IsEmpty);
        Assert.False(new SubmissionEvidence { FinalUrl = "https://ats.example/done" }.IsEmpty);
        Assert.False(new SubmissionEvidence { ScreenshotRef = "evidence/2026/09/02/a.png" }.IsEmpty);
        Assert.False(new SubmissionEvidence { SubmittedFields = ["full_name"] }.IsEmpty);
    }

    /// <summary>
    /// The evidence bounds, and the one of them chosen against a different rule.
    /// </summary>
    /// <remarks>
    /// The lengths are asserted as a set rather than one by one: pinning 200 here says nothing a
    /// reader could not read off the constant, and would turn widening a column into a failing
    /// test rather than a decision. What is worth pinning is the reasoning that the number does
    /// not carry - that the screenshot pointer is bounded by the blob store's own maximum name
    /// length rather than by what a path is expected to be, because truncating a pointer loses the
    /// object it points at rather than the tail of a sentence. Set below the store's limit, this
    /// column becomes the one thing that can break a reference the store itself would have taken.
    /// </remarks>
    [Fact]
    public void The_evidence_bounds_are_set_and_the_screenshot_pointer_is_bounded_by_the_blob_stores_limit()
    {
        int[] bounds =
        [
            SubmissionLimits.MaxConfirmationRefLength,
            SubmissionLimits.MaxFinalUrlLength,
            SubmissionLimits.MaxScreenshotRefLength,
            SubmissionLimits.MaxSubmittedFieldNameLength,
            SubmissionLimits.MaxSubmittedFieldCount,
        ];

        Assert.All(bounds, bound => Assert.True(bound > 0));

        // 1,024 characters is the longest name Azure Blob Storage accepts.
        Assert.True(SubmissionLimits.MaxScreenshotRefLength >= 1024);
    }
}
