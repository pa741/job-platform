namespace JobPlatform.Core.Model;

/// <summary>
/// How old a posting is, answered from the two dates the corpus actually carries.
/// </summary>
/// <remarks>
/// <b>The whole pipeline is built around a day.</b> The scraper runs once, the ingest and the
/// extraction queues drain behind it, the sweep judges what arrived and the apply run sends. So
/// "posted about twenty-four hours ago" is the question every list on the way through is read
/// for, and it has to be answerable for every posting rather than for the ones whose board was
/// forthcoming.
///
/// <b>Two columns, in this order, and the order is the whole rule.</b> <c>DatePosted</c> is the
/// board's own claim about the job and is believed wherever it exists - which is two postings in
/// five. <c>FirstSeenUtc</c> is when this system first read the posting, and it answers for the
/// other three: where nobody said when the job was posted, the only evidence of its age is when
/// it turned up.
///
/// <b>Not <c>LastSeenUtc</c>, ever.</b> A re-scrape moves that on every live posting in the
/// corpus, so a window over it returns the whole corpus every day - an answer no daily run can
/// act on. The apply queue's <c>Since</c> already carries that warning for the same reason.
///
/// <b>The asymmetry is deliberate.</b> A three-week-old posting discovered this morning is
/// excluded where its board published the date and included where it did not. That is the right
/// way round: a stated date is evidence and its absence is not, so silence falls back to the one
/// fact this system holds first-hand rather than being treated as an admission of age.
/// </remarks>
public static class PostingAge
{
    /// <summary>
    /// The window a daily run means by "posted about twenty-four hours ago".
    /// </summary>
    /// <remarks>
    /// Three days rather than one, because a day is the smallest unit either column can be
    /// compared in and one day would be a bound that only just holds. <c>DatePosted</c> is a
    /// date, not an instant, so "the last twenty-four hours" is already "today or yesterday" -
    /// two - and the third absorbs a scrape that did not run, a NAS that was off, or an ingest
    /// that drained after the sweep. The cost of the extra day is that a posting stays eligible
    /// for the recent share of the budget for two nights after the one it should have been
    /// judged on, which is a cost paid in duplicate consideration rather than in a missed job.
    /// </remarks>
    public const int DailyWindowDays = 3;

    /// <summary>The date a posting is treated as having been posted on.</summary>
    public static DateOnly PostedOn(DateOnly? datePosted, DateTimeOffset firstSeenUtc)
        => datePosted ?? DateOnly.FromDateTime(firstSeenUtc.UtcDateTime);

    /// <summary>The instant a "within this many days" bound starts at.</summary>
    /// <remarks>
    /// Clamped at zero rather than refused: a caller asking for a negative window has asked for
    /// nothing, and the honest reading of nothing is "since now", which returns today's arrivals
    /// rather than an error a client has to handle on a path that is otherwise total.
    /// </remarks>
    public static DateTimeOffset Cutoff(DateTimeOffset now, int withinDays)
        => now.AddDays(-Math.Max(withinDays, 0));

    /// <summary>
    /// Whether a posting is at or after the cutoff, by the rule this class exists to state once.
    /// </summary>
    /// <remarks>
    /// <b>The relational spellings of this live in <c>PostingRecency</c> and are held to this one
    /// by test.</b> Two of them are needed - the corpus search runs over postings and the
    /// shortlist runs over matches - and neither can call a method, because EF translates an
    /// expression tree rather than executing it. So this is the definition and they are its
    /// transcriptions.
    /// </remarks>
    public static bool PostedSince(
        DateOnly? datePosted, DateTimeOffset firstSeenUtc, DateTimeOffset cutoff)
        => datePosted is { } posted
            ? posted >= DateOnly.FromDateTime(cutoff.UtcDateTime)
            : firstSeenUtc >= cutoff;
}
