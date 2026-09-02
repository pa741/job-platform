using System.Collections.ObjectModel;
using System.Globalization;

namespace JobPlatform.Core.Submissions;

/// <summary>
/// A finished run's account of what it did.
/// </summary>
/// <remarks>
/// <b>Four numbers and a breakdown, chosen so that "the pass sent two applications last night"
/// can be told apart from the several different things that produce it.</b> An empty queue,
/// every posting stopping at a login wall, every posting missing its documents, and a daily cap
/// already spent are indistinguishable from outside - each of them is two rows in
/// <c>Submissions</c> and nothing else. <see cref="Considered"/> against <see cref="Submitted"/>
/// against <see cref="ParkedByReason"/> separates them in a single row.
///
/// <b>This is the client's account of itself, and only one of these numbers is checkable.</b>
/// The submissions carry the run id, so <see cref="Submitted"/> can be counted against them.
/// <see cref="Considered"/> can be checked against nothing at all, because a posting the client
/// read and passed over leaves no row anywhere - which is precisely why it is worth recording,
/// and precisely why it cannot be audited. The summary is evidence about a run; the rows that
/// name the run are the record of it. Anything treating these counts as authoritative is
/// trusting the client, and should say so.
/// </remarks>
/// <param name="Considered">
/// How many postings the run looked at. <b>The only number here that exists nowhere else</b>, and
/// the one that separates "the queue was empty" from "the queue was full and nothing got
/// through" - the two ordinary causes of a quiet night, which look identical in every other
/// table.
/// </param>
/// <param name="Submitted">How many applications the run recorded as sent.</param>
/// <param name="Questions">
/// How many questions the run had to put to a person. A run that stopped for want of an answer
/// rather than for want of a posting says so here, and that is a queue somebody can go and drain
/// rather than a bug to go looking for.
/// </param>
/// <param name="ParkedByReason">
/// Why the run put postings down, tallied per reason. <b>Keyed on the enum rather than on free
/// text</b>, so the breakdown is bounded by construction: this is written into a column, and a
/// dictionary whose keys a client invents is a column a client can write an essay into.
/// </param>
public sealed record RunSummary(
    int Considered,
    int Submitted,
    int Questions,
    IReadOnlyDictionary<ParkReason, int> ParkedByReason)
{
    /// <summary>
    /// A run that reported that nothing happened.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as <c>ApplyRun.Summary</c> being null</b>, which is a run that never
    /// reported at all. "Looked and found nothing" and "died before it could say" want opposite
    /// responses - the first is a queue to go and fill, the second is a client to go and restart
    /// - and collapsing them is the same mistake as reading an absent apply URL as "the board
    /// hosts it".
    /// </remarks>
    public static RunSummary Empty { get; } = new(0, 0, 0, ReadOnlyDictionary<ParkReason, int>.Empty);

    /// <summary>
    /// How many postings the run parked.
    /// </summary>
    /// <remarks>
    /// Summed from the breakdown rather than stored beside it. A stored total is a second copy of
    /// a fact the breakdown already carries, free to disagree with it - the same reason
    /// <c>MatchResult.Coverage</c> is recomputed from its components rather than given a column,
    /// and the same reason "already applied" is asked of <c>PostingExtractions</c> rather than
    /// flagged on the item.
    /// </remarks>
    public int Parked => ParkedByReason.Values.Sum();

    /// <summary>
    /// What the run considered and then neither sent nor parked.
    /// </summary>
    /// <remarks>
    /// <b>The number that catches the failure the other four cannot.</b> A run that considered
    /// forty postings, sent two and parked three has dropped thirty-five somewhere it did not
    /// report, and every other figure in this record looks unremarkable while it does so.
    ///
    /// Left signed rather than clamped at zero. A negative value means the client's own tallies
    /// do not add up, and clamping would replace a visibly broken count with a tidy one that is
    /// quietly wrong - the failure this codebase has already paid for three times, where the
    /// symptom was a count nobody was comparing to anything.
    /// </remarks>
    public int Unaccounted => Considered - Submitted - Parked;

    /// <summary>Tallies the reasons a run parked on, and builds the summary around them.</summary>
    /// <remarks>
    /// The counting lives here rather than at the call site because the counting is the part that
    /// goes wrong: a caller that sums its own parks has written the total twice, and the two
    /// copies drift the first time a park is added on a path that remembers only one of them.
    /// </remarks>
    /// <param name="parks">One entry per parked posting, in any order.</param>
    public static RunSummary From(
        int considered,
        int submitted,
        int questions,
        IEnumerable<ParkReason> parks)
    {
        ArgumentNullException.ThrowIfNull(parks);

        var tallies = new Dictionary<ParkReason, int>();

        foreach (var reason in parks)
        {
            tallies[reason] = tallies.GetValueOrDefault(reason) + 1;
        }

        return new RunSummary(
            considered,
            submitted,
            questions,
            new ReadOnlyDictionary<ParkReason, int>(tallies));
    }

    /// <summary>
    /// Equality over the breakdown's contents rather than over the dictionary reference.
    /// </summary>
    /// <remarks>
    /// <b>The generated record equality compares <see cref="ParkedByReason"/> by reference</b>, so
    /// two identical summaries would report as different for a reason no reader could see from
    /// the outside. That matters here rather than being tidiness: this codebase already turns on
    /// such a comparison being honest - <c>RankScore</c> is rounded to two decimals precisely so
    /// that an unchanged night writes no rows - and a summary that never equals itself is a
    /// summary every write path rewrites.
    ///
    /// A reason tallied at zero and a reason absent from the breakdown are the same fact, so
    /// neither side's zero entries are counted. Otherwise a summary built by <see cref="From"/>
    /// and one built by hand would differ over a reason nothing happened for.
    /// </remarks>
    public bool Equals(RunSummary? other)
        => other is not null
            && Considered == other.Considered
            && Submitted == other.Submitted
            && Questions == other.Questions
            && Covers(this, other)
            && Covers(other, this);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(Considered);
        hash.Add(Submitted);
        hash.Add(Questions);

        // Ordered, and zeroes dropped, for the same reason Equals ignores both: a hash that walks
        // a dictionary in its own insertion order puts two equal summaries in different buckets,
        // which breaks the contract in the one direction a test rarely looks.
        foreach (var entry in ParkedByReason.Where(e => e.Value != 0).OrderBy(e => e.Key))
        {
            hash.Add(entry.Key);
            hash.Add(entry.Value);
        }

        return hash.ToHashCode();
    }

    private static bool Covers(RunSummary left, RunSummary right)
        => left.ParkedByReason
            .Where(entry => entry.Value != 0)
            .All(entry => right.ParkedByReason.GetValueOrDefault(entry.Key) == entry.Value);
}

/// <summary>
/// One unattended pass over the applyable queue.
/// </summary>
/// <remarks>
/// <b>A run buys per-run observability, and that is the whole of what it buys.</b> The two things
/// it looks like it should buy are already provided, better, elsewhere - so this type is
/// deliberately small, and the temptation to grow it in either of those directions is what the
/// next two paragraphs exist to head off.
///
/// <b>It is not a quota, and the daily cap must not move into it.</b> The cap on <c>Submitted</c>
/// events is per UTC day, lives in <c>SubmissionRepository</c> and nowhere else, and counts by
/// the event's own <c>AtUtc</c> across every submission, which is what makes it unarguable. A
/// per-run counter would be a second and weaker copy of a rule that already works: it resets
/// every time a client starts a run, so a client that crashes and restarts twenty times would
/// spend twenty budgets against one day. And a run has no fixed relationship to a day anyway - it
/// may span midnight, in which case no per-run number can be converted into the one that is
/// enforced. Making the remaining quota <i>visible</i> to a run is right, and is what
/// <c>list_applyable</c> and <c>record_event</c> return; making the run <i>hold</i> it is not.
///
/// <b>It is not idempotency either.</b> <c>(SubmissionId, IdempotencyKey)</c> is unique by index
/// already, and the key is checked before the cap, so a client retrying a write it is unsure
/// landed converges with or without a run. <see cref="Key"/> is a naming convention the server
/// cannot police rather than a guarantee it enforces - see its own remarks.
///
/// <b>What is genuinely new is <see cref="RunSummary.Considered"/>.</b> Submissions record what
/// was created and are silent about what was looked at and passed over, so "the pass sent two
/// applications" has several causes that produce identical data. One row per pass, holding what
/// the pass saw, is the only thing in this system that tells them apart. That is a small benefit
/// honestly stated, and it is worth a table.
///
/// <b>There is no profile id on this record.</b> The row has one - it is what makes a run
/// somebody's - but nothing crossing the tool surface needs it, and "no tool takes a profile id"
/// is easier to keep true when the type that would carry one cannot.
/// </remarks>
/// <param name="Id">The run's row id, which is what a submission carries to name its run.</param>
/// <param name="StartedAtUtc">When <c>start_run</c> was called.</param>
/// <param name="FinishedAtUtc">
/// When <c>finish_run</c> was called, or null where it never was. <b>Written by the client and by
/// nothing else</b> - see the remarks on <see cref="IsOpen"/>.
/// </param>
/// <param name="Summary">
/// What the run reported, or null where it reported nothing. Null and
/// <see cref="RunSummary.Empty"/> are different answers and a reader must not fold them together.
/// </param>
/// <param name="Note">
/// A sentence from the client about the pass as a whole. Bounded by
/// <c>SubmissionLimits.MaxNoteLength</c>, deliberately reusing that constant rather than minting
/// a second one: it is the same kind of text under the same argument, and two bounds on one kind
/// of thing is how a column and its validation drift apart.
/// </param>
public sealed record ApplyRun(
    long Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    RunSummary? Summary,
    string? Note)
{
    /// <summary>
    /// How long an open run is given before it is read as abandoned.
    /// </summary>
    /// <remarks>
    /// Twelve hours, and two bounds fix the choice. <b>Shorter than a UTC day</b>, so an
    /// abandoned run can never be mistaken for one still working through the day's cap - the cap
    /// is what a reader is really asking about when they ask whether a run is still going.
    /// <b>Longer than any unattended pass that is merely slow</b>, because a threshold that fires
    /// on the ordinary case is one people learn to scroll past, which is the judgement
    /// <c>SubmissionState.StaleAfter</c> and the digest's apply-link warning are both settled by.
    /// </remarks>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(12);

    /// <summary>
    /// The run has not been finished.
    /// </summary>
    /// <remarks>
    /// <b>An open run is an ordinary end state, not an exceptional one.</b> The client is
    /// unattended: it will sometimes be killed, lose its network, or be stopped by the person who
    /// started it, and none of those call <c>finish_run</c>. So "open" has to mean something
    /// definite, and it means exactly this: <i>the client never reported, and nothing but the
    /// client will ever close it.</i>
    ///
    /// <b>No timer writes <see cref="FinishedAtUtc"/>.</b> Sweeping open runs closed needs a job
    /// that races a real <c>finish_run</c>, and between the two the row asserts a finish nothing
    /// observed - which is the argument <c>SubmissionState</c> already makes for deriving
    /// staleness instead of storing it, and the argument against a status column in the first
    /// place. The column keeps one meaning: a client said it was done.
    ///
    /// Openness is therefore read rather than written, and <see cref="IsAbandoned"/> is the
    /// reading. Past <see cref="AbandonedAfter"/> the summary is not coming, and a reader should
    /// stop waiting for it and count the submissions carrying this run's id instead. <b>That is
    /// what makes an abandoned run cost observability rather than data</b>: the work the run did
    /// is in the submissions, attributed correctly, whether or not the run ever spoke again. What
    /// is lost is the one number the rows cannot supply - how much the run looked at.
    ///
    /// <b>A second run does not close the first.</b> Two open runs for one candidate is a client
    /// that crashed and restarted - a fact worth being able to see rather than a conflict to
    /// resolve - and nothing here enforces one at a time. Each submission names the run it
    /// belongs to, so the arithmetic is right either way.
    /// </remarks>
    public bool IsOpen => FinishedAtUtc is null;

    /// <summary>
    /// The run is open, and has been open long enough that it is not coming back.
    /// </summary>
    /// <remarks>
    /// <b>Derived, never stored</b>, and the clock is a parameter for the reason
    /// <c>SubmissionState.Fold</c> takes one: a function that reads <c>DateTimeOffset.UtcNow</c>
    /// itself cannot be tested at the boundary, and the boundary is the only interesting part of
    /// it.
    ///
    /// A finished run is never abandoned however old it is, exactly as a closed application is
    /// never stale. It said what it had to say.
    /// </remarks>
    /// <param name="now">The clock, passed in so the boundary is assertable.</param>
    public bool IsAbandoned(DateTimeOffset now)
        => IsOpen && now - StartedAtUtc > AbandonedAfter;

    /// <summary>
    /// The idempotency key a run uses for one piece of work on one posting.
    /// </summary>
    /// <remarks>
    /// <b>A convention, not a guarantee, and that distinction is the whole of what to expect from
    /// it.</b> The convergence a retry needs exists without runs: <c>(SubmissionId,
    /// IdempotencyKey)</c> is unique by index, and <c>SubmissionRepository.AddEventAsync</c>
    /// checks the key <i>before</i> the daily cap, so a client retrying a write it is unsure
    /// landed is answered <c>AlreadyRecorded</c> rather than refused for quota that very event
    /// already spent. A random key buys all of that.
    ///
    /// What this namespace adds is agreement: one client retrying, or two clients resuming the
    /// same run, derive the same key from the same intent, and a person reading the column can
    /// see which run wrote a row. <b>The server neither parses nor validates it.</b> It does not
    /// check that <paramref name="runId"/> names a real run, cannot tell a key of this shape from
    /// any other hundred characters, and will not refuse a client that gets the shape wrong -
    /// such a client simply loses convergence, exactly as it would have with no run at all.
    ///
    /// The result must fit <c>SubmissionLimits.MaxIdempotencyKeyLength</c>, because the
    /// repository <i>truncates</i> an over-long key rather than refusing it, and a truncated key
    /// collides with its neighbours - which is strictly worse than carrying no key. Two
    /// <see cref="long"/> ids and the longest member name leave room to spare, and a test pins
    /// that against the enum growing a longer one.
    /// </remarks>
    /// <param name="runId">The run doing the work. A submission made outside a run has no key of this shape and supplies its own.</param>
    /// <param name="postingId">The posting being applied to.</param>
    /// <param name="type">What is being recorded, so one run can record two phases on one posting.</param>
    public static string Key(long runId, long postingId, SubmissionEventType type)
        // Invariant, because the key is compared byte for byte by the database and must not be
        // built in whatever culture the calling thread happens to be carrying.
        => string.Create(CultureInfo.InvariantCulture, $"{runId}:{postingId}:{type}");
}
