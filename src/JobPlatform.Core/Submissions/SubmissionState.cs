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
/// Nothing has happened for the window the fold was given -
/// <see cref="SubmissionState.StaleAfter"/> unless the caller named one of their own, which is
/// where <c>PipelineSettings.ChaseAfterDays</c> arrives. <b>Derived, never stored</b>, and a
/// configurable window does not weaken that: the answer is still computed at read time from the
/// event log, so lowering the number makes older quiet applications stale on the next read and
/// raising it makes them live again, with nothing migrated and nothing to migrate.
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
///
/// <b>The staleness window is a parameter for the same reason the clock is</b>, and it defaults
/// to <see cref="StaleAfter"/>, so every existing caller and every unconfigured deployment folds
/// to the answer it always did. <c>PipelineSettings.ChaseAfterDays</c> is the per-candidate
/// setting behind it, and the conversion from a day count to a <c>TimeSpan</c> belongs to the
/// consumer - so this class still knows nothing about settings, storage or where a number came
/// from, which is the property that makes it worth having at all.
/// </remarks>
public static class SubmissionState
{
    /// <summary>
    /// Silence for this long makes an application stale, where the caller names no window.
    /// </summary>
    /// <remarks>
    /// A fortnight, and the length is a judgement rather than a measurement. Shorter would flag
    /// every application in its first week, which is the normal state of a live one, and a
    /// warning that fires on the ordinary case is one people learn to ignore - the same reason
    /// the digest's board-hosted alarm sits at a near-total share rather than at a suspicion.
    ///
    /// <b>The default now rather than the only value, and it stays here rather than moving.</b>
    /// <c>PipelineSettings.ChaseAfterDays</c> is the per-candidate setting, and its own default
    /// is fourteen days precisely so that a candidate with nothing configured folds to the answer
    /// this constant has always given. Three things need it to keep a name: <see cref="Fold"/>
    /// uses it when nobody passes a window, a caller with no settings to hand has somewhere to
    /// read the shipped number from, and the tests assert the boundary against it - a test
    /// written as <c>Created + StaleAfter</c> and its successor a tick later is exact, where the
    /// same test written as fourteen literal days is a second copy of the decision.
    ///
    /// <b>A <c>TimeSpan</c> here and a day count in the settings, deliberately.</b> The fold
    /// subtracts two instants, so a duration is the type the arithmetic wants; a settings surface
    /// has to render and store one number, and a <c>TimeSpan</c> is a wire format with several
    /// spellings - "14.00:00:00", "P14D", 1209600000 - which would be three contracts wearing one
    /// type. The consumer bridges them with <c>TimeSpan.FromDays(settings.ChaseAfterDays)</c>,
    /// in one place, rather than either side taking the other's shape.
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
    ///
    /// <b>The window is configurable and staleness is still derived, never stored.</b> That rule
    /// is about where the answer lives rather than about where the threshold came from, and both
    /// halves of it are intact: the answer is computed here, at read time, from the event log,
    /// and there is no column anywhere that holds it. What the rule refuses is the version with a
    /// timer writing a flag - a race between that timer and a real event, and a row that is wrong
    /// between the two - and passing a different <paramref name="staleAfter"/> creates no timer
    /// and no row. It is worth saying because the next reader will worry about it: a number a
    /// person can change reads like something that must be stored somewhere, and the thing stored
    /// is the day count, never the verdict it produces. Lowering it makes older quiet
    /// applications stale on the next read and raising it makes them live again, with nothing
    /// migrated, because there is nothing to migrate - which is the property the event log was
    /// chosen for and what makes this safe to expose at all.
    /// </remarks>
    /// <param name="createdAtUtc">When the submission row was made, which is what staleness reads before any event exists.</param>
    /// <param name="events">The application's whole log, in any order.</param>
    /// <param name="now">The clock, passed in so the boundary is testable.</param>
    /// <param name="staleAfter">
    /// How long silence has to last. <c>TimeSpan.FromDays(settings.ChaseAfterDays)</c> where the
    /// caller holds the candidate's settings, and null - the default - for
    /// <see cref="StaleAfter"/>, which is what that setting itself defaults to.
    ///
    /// <b>Nullable because C# cannot default a parameter to <see cref="StaleAfter"/>.</b> A
    /// parameter default has to be a compile-time constant and a <c>TimeSpan</c> cannot be one,
    /// so the only value the signature could name is <c>default</c> - <c>TimeSpan.Zero</c> - and
    /// zero is a window with a meaning of its own: at zero every non-terminal application is
    /// stale the instant after its last event, so the chase list becomes the whole list. Reading
    /// a caller's zero as "they meant fourteen" would be the "did not choose and chose nothing
    /// are different bytes" fault the scraper config is written to avoid, on a path where the
    /// wrong reading is silent. So absence is spelt null and zero is left meaning zero.
    ///
    /// <b>Not validated here.</b> <c>PipelineSettingsValidation.MinChaseAfterDays</c> owns that
    /// bound and refuses zero for exactly the reason above; the fold stays total, so a nonsense
    /// window gives a defensible answer rather than an exception on a read path that every
    /// submission list goes through.
    /// </param>
    public static SubmissionStatus Fold(
        DateTimeOffset createdAtUtc,
        IReadOnlyList<SubmissionEvent> events,
        DateTimeOffset now,
        TimeSpan? staleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Resolved once rather than at each comparison, so the no-events branch and the folded
        // one can never answer against different windows - the shape of divergence that only
        // shows up on the empty log, which is the case least likely to be looked at.
        var window = staleAfter ?? StaleAfter;

        if (events.Count == 0)
        {
            return new SubmissionStatus(
                Phase: null,
                Stage: null,
                LastActivityUtc: createdAtUtc,
                IsStale: now - createdAtUtc > window,
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
            IsStale: !deciding.Type.IsTerminal() && now - lastActivity > window,
            EventCount: events.Count);
    }
}
