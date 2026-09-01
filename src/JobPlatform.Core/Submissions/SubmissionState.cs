namespace JobPlatform.Core.Submissions;

/// <summary>
/// Where an application stands, folded from its events.
/// </summary>
/// <param name="Phase">
/// The furthest the application has got, or <b>null where nothing has happened yet</b> - the row
/// exists but no event has been recorded against it. Null rather than a <c>Created</c> member,
/// for the reason <c>MatchSummary.Verdict</c> is null rather than <c>Unknown</c>: a client has
/// to be able to tell "not started" from "started and we cannot say", and a default enum name
/// collapses the two.
/// </param>
/// <param name="Stage">The label carried by the event that decided the phase, where it had one.</param>
/// <param name="LastActivityUtc">
/// The most recent event, or the submission's creation time where there is none. What staleness
/// is measured from, and what a list is sensibly ordered by.
/// </param>
/// <param name="IsStale">
/// Nothing has happened for <see cref="SubmissionState.StaleAfter"/>. <b>Derived, never stored.</b>
/// </param>
/// <param name="EventCount">How much history there is, so a client can decide whether to fetch it.</param>
public sealed record SubmissionStatus(
    SubmissionEventType? Phase,
    string? Stage,
    DateTimeOffset LastActivityUtc,
    bool IsStale,
    int EventCount)
{
    /// <summary>Whether the application is closed, either way.</summary>
    public bool IsClosed => Phase?.IsTerminal() == true;
}

/// <summary>
/// The fold from an event log to a status.
/// </summary>
/// <remarks>
/// Pure and free of every Azure type, exactly like <c>MatchScorer</c> and
/// <c>MetricsCalculator</c> - which is what makes its answers assertable exactly rather than
/// approximately. Nothing here reads a clock of its own: <c>now</c> is a parameter, because a
/// function that decides staleness from <c>DateTimeOffset.UtcNow</c> cannot be tested at the
/// boundary and the boundary is the only interesting part.
/// </remarks>
public static class SubmissionState
{
    /// <summary>
    /// Silence for this long makes an application stale.
    /// </summary>
    /// <remarks>
    /// A fortnight, and the length is a judgement rather than a measurement. Shorter would flag
    /// every application in its first week, which is the normal state of a live one, and a
    /// warning that fires on the ordinary case is one people learn to ignore - the same reason
    /// the digest's board-hosted alarm sits at a near-total share rather than at a suspicion.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>
    /// Folds an application's events into where it stands.
    /// </summary>
    /// <remarks>
    /// Three rules, and each exists because the naive version of it is wrong:
    ///
    /// <b>A terminal event wins outright.</b> Not "the latest event wins": a rejection followed
    /// by an automated "thanks for applying" must stay a rejection. Where there are two terminal
    /// events - withdrawn after a rejection, say - the later one by <c>AtUtc</c> takes it,
    /// because that is a question about time rather than about rank.
    ///
    /// <b>Otherwise the furthest-advanced phase wins, not the most recent one.</b> These events
    /// arrive from a client reading an inbox, so they are late and out of order routinely. A
    /// late <c>Acknowledged</c> landing after an <c>OfferReceived</c> must not walk the
    /// application backwards.
    ///
    /// <b>Staleness is measured from the last activity of any kind</b>, including an event that
    /// did not move the phase, and a closed application is never stale. An employer who has
    /// stopped replying has gone quiet; one who has said no has not.
    /// </remarks>
    /// <param name="createdAtUtc">When the submission row was made, which is what staleness reads before any event exists.</param>
    /// <param name="events">The application's whole log, in any order.</param>
    /// <param name="now">The clock, passed in so the boundary is testable.</param>
    public static SubmissionStatus Fold(
        DateTimeOffset createdAtUtc,
        IReadOnlyList<SubmissionEvent> events,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return new SubmissionStatus(
                Phase: null,
                Stage: null,
                LastActivityUtc: createdAtUtc,
                IsStale: now - createdAtUtc > StaleAfter,
                EventCount: 0);
        }

        var lastActivity = events.Max(e => e.AtUtc);

        // Latest terminal by time, or - where there is none - the furthest-advanced phase. Ties
        // on the phase are broken by time so that two events of the same type contribute the
        // later one's stage text, which is the one a reader means by "where is this now".
        var deciding =
            events
                .Where(e => e.Type.IsTerminal())
                .OrderBy(e => e.AtUtc)
                .LastOrDefault()
            ?? events
                .OrderBy(e => e.Type)
                .ThenBy(e => e.AtUtc)
                .Last();

        return new SubmissionStatus(
            Phase: deciding.Type,
            Stage: deciding.Stage,
            LastActivityUtc: lastActivity,
            IsStale: !deciding.Type.IsTerminal() && now - lastActivity > StaleAfter,
            EventCount: events.Count);
    }
}
