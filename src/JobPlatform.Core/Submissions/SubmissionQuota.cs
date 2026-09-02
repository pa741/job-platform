namespace JobPlatform.Core.Submissions;

/// <summary>
/// How much of one UTC day's cap on <c>Submitted</c> events is left, and what a run may plan
/// against it.
/// </summary>
/// <remarks>
/// <b>The cap stays; this makes it visible.</b> It is the one bound between a client that loops
/// and a pipeline full of applications nobody made, and the way to keep a bound unarguable is to
/// let the caller see it rather than to relax it. An agent that knows six are left sends six; an
/// agent that does not find out by being refused, one refusal at a time, after the fact.
///
/// <b>This is deliberately not returned from <c>create_submission</c>, which is what the spec
/// asked for and cannot work.</b> Creating a submission spends no quota - the cap counts
/// <c>Submitted</c> events, and a row with no events has claimed nothing - so a client calling
/// <c>create_submission</c> twenty times before recording a single event would be handed the
/// same <c>remaining</c> twenty times over. A number that does not move while the client works
/// is worse than no number: it reads as twenty applications' worth of headroom and is one.
///
/// <b>Where the cap actually fires is what decides where this belongs.</b> It fires in
/// <c>SubmissionRepository.AddEventAsync</c>, which by the loop's design runs <i>after</i> the
/// browser has already filled in and sent the form. A refusal there stops nothing - it produces
/// an application that exists in the world and cannot be recorded, which is the worst state this
/// system has, because every later decision reads the log rather than the world.
///
/// So the quota is answered in the two places that can act on it: on <c>list_applyable</c>,
/// where the run picks its batch before opening the first tab, and on <c>record_event</c>'s
/// success answer, as a burn-down so a long run watches it fall. Both are planning; neither is
/// a reservation.
///
/// <b>Pure, like the fold.</b> The counting is a query and lives in the repository - Core has no
/// EF, no Azure and no clock of its own. This is only the answer's shape, which is what lets the
/// arithmetic be asserted exactly rather than through a database.
/// </remarks>
/// <param name="DailyCap">
/// The bound in force, <see cref="SubmissionLimits.MaxSubmittedPerDay"/>. Carried rather than
/// left for the caller to know, because the caller is a model on the other side of a tool call
/// and has no constants: sending the cap alongside the count makes <see cref="Remaining"/>
/// arithmetic it can check rather than a number it has to trust.
/// </param>
/// <param name="SubmittedOnDay">
/// How many <c>Submitted</c> events the candidate has already recorded inside <paramref name="Day"/>,
/// <b>counted by the event's own <c>AtUtc</c> rather than by when the row was written</b> - the
/// same way the repository counts them. Backdating a hundred events into one day is the same
/// assertion as making them now, and a burn-down that counted rows by write time would disagree
/// with the bound that is actually enforced.
/// </param>
/// <param name="Day">
/// The UTC day the count is over. <b><see cref="DateOnly"/> rather than a timestamp</b>, because
/// the window is a calendar day in UTC and nothing else; a type that can carry an offset invites
/// a local-day reading, and at 01:00 in Madrid the local day is not the UTC one.
/// </param>
public sealed record SubmissionQuota(int DailyCap, int SubmittedOnDay, DateOnly Day)
{
    /// <summary>
    /// How many more applications may be recorded as sent on <see cref="Day"/>. Never negative.
    /// </summary>
    /// <remarks>
    /// <b>Floored at zero because the count can genuinely exceed the cap.</b> The repository
    /// counts and then inserts, and the two are not one transaction, so two clients writing at
    /// once can both pass a check that says twenty-four; and lowering
    /// <see cref="SubmissionLimits.MaxSubmittedPerDay"/> leaves every day already past the new
    /// bound permanently past it. "Minus three left" is not a state this system has - the state
    /// is "none" - and
    /// handing a negative number to a model that is about to multiply it by something is how a
    /// bound becomes a suggestion.
    /// </remarks>
    public int Remaining => Math.Max(0, DailyCap - SubmittedOnDay);

    /// <summary>Whether the day is spent, asked rather than left to the caller's arithmetic.</summary>
    /// <remarks>
    /// Same reason <c>SubmissionEventTypes.IsTerminal</c> is a question rather than a comparison:
    /// a caller writing <c>remaining &lt; 1</c> is one sign flip from a run that keeps going.
    /// </remarks>
    public bool IsExhausted => Remaining == 0;

    /// <summary>
    /// Builds the answer for a day, from a count the repository has already made.
    /// </summary>
    /// <remarks>
    /// <b>The day is derived here rather than by each caller</b>, because it has to be the same
    /// day the cap counts over. The repository's window starts at
    /// <c>submissionEvent.AtUtc.UtcDateTime.Date</c>; a caller deriving its own from a local
    /// clock would report a burn-down for a window the bound is not enforced on, and would be
    /// wrong by a whole day for part of every day rather than obviously wrong once.
    /// </remarks>
    /// <param name="atUtc">Any instant inside the day being asked about. Read in UTC, whatever offset it carries.</param>
    /// <param name="submittedOnDay">What the repository counted for that day.</param>
    public static SubmissionQuota For(DateTimeOffset atUtc, int submittedOnDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(submittedOnDay);

        return new SubmissionQuota(
            SubmissionLimits.MaxSubmittedPerDay,
            submittedOnDay,
            DateOnly.FromDateTime(atUtc.UtcDateTime));
    }

    /// <summary>
    /// How many of a proposed batch can actually be sent today.
    /// </summary>
    /// <remarks>
    /// <b>A run plans its batch before it opens the first tab.</b> The alternative is to discover
    /// the bound by being refused, and a refusal arrives at <c>record_event</c> - after the form
    /// has gone - so discovering it late means an application nobody can record. Ten postings
    /// against six remaining is a batch of six, chosen while it is still free to choose.
    ///
    /// <b>A plan of zero is an ordinary answer, not a failure.</b> The day is spent, the run does
    /// nothing and says so; that is the explanatory note this surface prefers to an error
    /// wherever something is simply absent.
    ///
    /// <b>It is a ceiling, not a booking.</b> Nothing is reserved: quota is spent by recording,
    /// and another client sharing the candidate may spend some between the plan and the last
    /// event. Planning twice therefore gives the same number twice, and the cap in the
    /// repository - not this - is what remains authoritative.
    /// </remarks>
    /// <param name="quota">Where the day stands.</param>
    /// <param name="candidateCount">How many postings the run would like to apply to.</param>
    public static int Plan(SubmissionQuota quota, int candidateCount)
    {
        ArgumentNullException.ThrowIfNull(quota);

        // Guarded rather than clamped. A negative batch is a caller's arithmetic having gone
        // wrong upstream, and answering zero would hide it behind an ordinary-looking "nothing
        // to do today".
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);

        return Math.Min(candidateCount, quota.Remaining);
    }
}
