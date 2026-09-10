namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// An employer, deduplicated out of the postings that mention it.
/// </summary>
/// <remarks>
/// Two reasons, both concrete. "Contoso Ltd", "Contoso Limited" and "Contoso" are three rows in
/// every ranking today, because the only folding that exists lowercases and strips punctuation
/// but keeps the legal suffix — so "who is hiring most" splits one employer's demand across
/// three lines and is simply wrong.
///
/// And <see cref="Description"/> is the company blurb, repeated verbatim on every posting that
/// employer has open. Against a 2 GB Basic ceiling that duplication is not free: a company with
/// four hundred live listings pays for four hundred copies of the same paragraph.
/// </remarks>
public sealed class CompanyEntity
{
    public int Id { get; set; }

    /// <summary>
    /// The folded name — lower-cased, punctuation collapsed, legal form stripped. Unique.
    /// </summary>
    /// <remarks>
    /// Geographic qualifiers survive on purpose: "Contoso" and "Contoso UK" plausibly are
    /// different hiring entities with different pay and different offices, and merging them
    /// would destroy a distinction the data actually contains. This folds spelling, not
    /// corporate structure.
    /// </remarks>
    public required string CompanyKey { get; set; }

    /// <summary>The spelling most recently seen, for display.</summary>
    public required string DisplayName { get; set; }

    public string? Industry { get; set; }

    /// <summary>The band as published, kept beside the parsed numbers for traceability.</summary>
    public string? EmployeesBand { get; set; }

    public int? EmployeesMin { get; set; }
    public int? EmployeesMax { get; set; }

    public string? Revenue { get; set; }
    public string? Url { get; set; }

    /// <summary>The company blurb. Unbounded, and the reason this table pays for itself.</summary>
    public string? Description { get; set; }

    public double? Rating { get; set; }
    public int? ReviewsCount { get; set; }

    /// <summary>
    /// When this employer's own careers page was last considered for an embedded board token, and
    /// null while nobody has looked.
    /// </summary>
    /// <remarks>
    /// <b>"Asked and found nothing" and "nobody has asked" are different facts, and this column is
    /// the only thing that can tell them apart.</b> A careers-page read that finds no board writes
    /// no row anywhere else - <c>EmployerAtsBoards</c> holds boards that exist - so without a stamp
    /// the absence of a board would mean both, the pass would fetch the same employer's page every
    /// night for ever, and it would be the identical two-nulls-in-one-column fault
    /// <c>JobPostings.OffsiteApply</c> was added to undo and that
    /// <c>JobPostings.EmployerAtsCheckedUtc</c> already answers one layer down.
    ///
    /// <b>It is stamped before the request rather than after it</b>, which is
    /// <c>EmployerAtsBoardRepository.RecordFetchAsync</c>'s rule for the same reason: a pass that
    /// recorded the attempt only on success would re-ask a careers page that times out on every
    /// pass for ever, and unlike the five board APIs this one is an arbitrary third-party host that
    /// has made us no promises at all.
    ///
    /// <b>"Considered" rather than "fetched", and the difference is deliberate.</b> An employer
    /// whose stored URL is not an address anybody could fetch - blank, a tracker, a job board's own
    /// profile page for the company - is stamped as well, even though no request was made. The
    /// employer occupies a slot in a bounded work list whether or not a socket is opened, so
    /// leaving them unstamped would starve the employers behind them on every pass; and what was
    /// established is a fact about <see cref="Url"/>, which changes only when an ingest rewrites
    /// it. That is what the re-read window covers.
    ///
    /// <b>Why it lives on the scraped employer row rather than in a table of its own.</b> The
    /// alternative is a two-column side table whose only reader is one query, and the precedent
    /// runs the other way: the recovery pass already stamps its state on the scraped posting row -
    /// <c>EmployerAtsApplyUrl</c>, <c>EmployerAtsMatchConfidence</c>, <c>EmployerAtsCheckedUtc</c>
    /// are three such columns on <c>JobPostings</c> - and this is the same fact one level up,
    /// because a board hangs off <c>Companies.Id</c> and so does the page that named it. The cost
    /// is that this table stops being purely a projection of scraped fields, which is worth saying
    /// out loud: <c>JobPostingRepository</c>'s company upsert writes every other column here on
    /// every ingest and must never learn to write this one, because an ingest has asked nobody
    /// anything.
    ///
    /// <b>Not indexed, deliberately.</b> The read that consults it is already bounded by an
    /// employer having a blocked posting and no board of any kind, which is a few thousand rows on
    /// this corpus; an index on a column whose selectivity is "most employers, most of the time" is
    /// a write cost on every pass to buy nothing.
    /// </remarks>
    public DateTimeOffset? AtsCareersPageReadUtc { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}
