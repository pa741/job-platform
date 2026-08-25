using JobPlatform.Core;

namespace JobPlatform.Api.Features.Postings;

/// <summary>
/// A posting in a list response.
/// </summary>
/// <remarks>
/// Note what is absent: <c>Description</c>. It is unbounded <c>nvarchar(max)</c> and is the
/// bulk of a row, so including it here would turn a 100-row page into megabytes. The full
/// text is available from the detail endpoint, which returns one posting. This is the reason
/// contracts exist separately from entities rather than being ceremony.
/// </remarks>
public sealed record PostingSummary
{
    public required long Id { get; init; }
    public required string SourceKey { get; init; }
    public required string Site { get; init; }
    public required string Title { get; init; }
    public string? Company { get; init; }

    public string? Location { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }

    /// <summary>Null where the board said nothing, which is most of the corpus.</summary>
    public bool? IsRemote { get; init; }
    public string? JobType { get; init; }
    public DateOnly? DatePosted { get; init; }

    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public string? Currency { get; init; }
    public string? SalaryInterval { get; init; }

    // --- derived ------------------------------------------------------------

    /// <summary>
    /// Salary on one scale, from the board's columns where it filled them and from the
    /// description where it did not.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="MinAmount"/> rather than replacing it. That one is what the
    /// scraper delivered and is what the fill-rate metrics measure; overwriting it would make
    /// coverage look like it improved when only the inference did. Most of the corpus has a
    /// figure here and not there.
    /// </remarks>
    public decimal? AnnualSalaryMin { get; init; }
    public decimal? AnnualSalaryMax { get; init; }
    public string? AnnualSalaryCurrency { get; init; }

    /// <summary>
    /// True where the figure came from prose. Weaker evidence, and a client averaging across
    /// both without splitting on this is measuring two different things at once.
    /// </summary>
    public bool SalaryFromText { get; init; }

    /// <summary>
    /// What the source said before annualisation. A GBP 600/day contract annualised to
    /// 156,000 is not the same offer as a 156,000 salary, and this is the only field that
    /// distinguishes them afterwards.
    /// </summary>
    public string? SalaryStatedInterval { get; init; }

    /// <summary>Ordinal, so it sorts. Unknown for the 18% of titles that say nothing.</summary>
    public string Seniority { get; init; } = nameof(Core.Enrichment.Seniority.Unknown);

    public string RoleFamily { get; init; } = nameof(Core.Enrichment.RoleFamily.Unknown);

    /// <summary>
    /// The three-way answer <see cref="IsRemote"/> cannot express.
    /// </summary>
    public string WorkArrangement { get; init; } = nameof(Core.Enrichment.WorkArrangement.Unknown);

    public int? HybridDaysInOffice { get; init; }

    public int? YearsExperienceMin { get; init; }
    public int? YearsExperienceMax { get; init; }

    public bool RequiresSecurityClearance { get; init; }

    /// <summary><c>inside</c>, <c>outside</c>, or null.</summary>
    public string? Ir35 { get; init; }

    public string? JobUrl { get; init; }

    /// <summary>Length of the description without the description itself, so a client can
    /// tell a substantive posting from a stub before fetching it.</summary>
    public int DescriptionLength { get; init; }

    /// <summary>
    /// Whether the posting is a real, current opening — <c>fresh</c>, <c>stale</c> or
    /// <c>likely-evergreen</c>. Only freehire supplies these; null on every scraped board,
    /// which is why they sit in the list contract: they are triage, not detail.
    /// </summary>
    public string? FreshnessClass { get; init; }

    public int? PostingAgeDays { get; init; }

    /// <summary>How many times the role has been reposted.</summary>
    public int? RepostCount { get; init; }

    /// <summary>
    /// True when the stated posting date looks refreshed rather than real. Null means
    /// nobody checked, which is not the same as false.
    /// </summary>
    public bool? FakeFreshness { get; init; }

    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public int SeenCount { get; init; }

    /// <summary>
    /// Every configured search that turned this posting up, not just the one being viewed.
    /// A posting can match several, and a single value here would have to pick one.
    /// </summary>
    public required IReadOnlyList<string> SearchTerms { get; init; }
}

/// <summary>One posting in full. The only contract carrying the description.</summary>
public sealed record PostingDetail
{
    public required PostingSummary Summary { get; init; }
    public string? Description { get; init; }
    public string? JobUrlDirect { get; init; }
    public string? CompanyUrl { get; init; }
    public string? JobLevel { get; init; }
    public string? JobFunction { get; init; }
    public string? CompanyIndustry { get; init; }
    public string? SalarySource { get; init; }

    /// <summary>
    /// freehire's one or two sentence synopsis. Named Synopsis rather than Summary
    /// because <see cref="Summary"/> on this record is already the list contract.
    /// </summary>
    public string? Synopsis { get; init; }

    public string? ExperienceRange { get; init; }
    public string? CompanyNumEmployees { get; init; }

    /// <summary>
    /// Verbatim applicant caption, e.g. "Over 200 applicants". LinkedIn only.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="ApplicantCount"/> rather than replaced by it, for the same reason
    /// the raw salary columns sit beside the annualised ones: "Over 200" and "200" are not the
    /// same statement, and only the caption says which one the board actually made.
    /// </remarks>
    public string? Applicants { get; init; }

    /// <summary>
    /// The figure parsed out of <see cref="Applicants"/>. The competition signal.
    /// </summary>
    /// <remarks>
    /// The single most decision-relevant number on a posting after salary, and it was being
    /// parsed, normalised and stored without ever reaching a screen. Sparse - LinkedIn is the
    /// only board that publishes it - so a client must treat null as "not stated" rather than
    /// as zero.
    /// </remarks>
    public int? ApplicantCount { get; init; }

    /// <summary>Openings this listing covers, where the board says. Naukri and freehire.</summary>
    public int? VacancyCount { get; init; }

    /// <summary>
    /// The board's own three-way work mode: <c>remote</c>, <c>hybrid</c>, <c>onsite</c>.
    /// </summary>
    /// <remarks>
    /// Worth showing next to the derived <see cref="PostingSummary.WorkArrangement"/>, because
    /// this is what the employer stated and that is what we concluded. Where they disagree, the
    /// disagreement is the interesting part.
    /// </remarks>
    public string? WorkFromHomeType { get; init; }

    public string? ListingType { get; init; }

    /// <summary><c>inside</c>, <c>outside</c>, or null. UK contract postings only.</summary>
    public string? Ir35 { get; init; }

    /// <summary>Null where the posting is silent, which is not the same as "no".</summary>
    public bool? VisaSponsorship { get; init; }


    public required string ContentHash { get; init; }
    public int FirstSeenRunId { get; init; }
    public int LastSeenRunId { get; init; }
}

public sealed record PageResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required bool HasMore { get; init; }
    public int? Total { get; init; }
    public required int Limit { get; init; }
    public required int Offset { get; init; }
}

public sealed record NamedCount(string Name, int Count);

public sealed record FacetsResponse
{
    public string? SearchTerm { get; init; }
    public int Total { get; init; }
    public int RemoteCount { get; init; }
    public int WithSalaryCount { get; init; }
    public DateOnly? EarliestDatePosted { get; init; }
    public DateOnly? LatestDatePosted { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public IReadOnlyList<NamedCount> Sites { get; init; } = [];
    public IReadOnlyList<NamedCount> JobTypes { get; init; } = [];
    public IReadOnlyList<NamedCount> Countries { get; init; } = [];
    public IReadOnlyList<NamedCount> Cities { get; init; } = [];
    public IReadOnlyList<NamedCount> Companies { get; init; } = [];

    /// <summary>Concepts and domains present, so a filter UI can offer them by name.</summary>
    public IReadOnlyList<ConceptCount> Concepts { get; init; } = [];
}

/// <param name="Key">What the filter passes back.</param>
/// <param name="Label">What a person reads.</param>
public sealed record ConceptCount(string Key, string Label, int Count);

/// <summary>
/// One search term the platform holds data for.
/// </summary>
/// <remarks>
/// Sourced from the latest daily rollup in Cosmos rather than from SQL. Clients fetch this
/// before they can fetch anything else, so it must not depend on a database that spends most
/// of the day paused - see the endpoint for the failure that caused.
/// </remarks>
public sealed record SearchTermResponse(
    string SearchTerm,
    /// <summary>Every distinct posting recorded for this term, as of the latest rollup.</summary>
    int PostingCount,
    string? LastScrapeDate,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>
/// One posting with everything the pipeline concluded, and how it concluded it.
/// </summary>
/// <remarks>
/// <b>Provenance is the point of this contract.</b> A list of skills a posting wants is the
/// shallow half; which of them the employer tagged, which a string match found, which the model
/// read out of prose - and the exact phrase it read - is what makes the conclusion checkable
/// rather than merely presented. This is also the only place the concept graph becomes visible
/// to a person: the rollup below is computed through the closure and cannot be derived by a
/// client that does not carry the vocabulary.
/// </remarks>
public sealed record PostingInsight
{
    public required PostingDetail Detail { get; init; }

    /// <summary>What the posting asks for, each with its source and evidence.</summary>
    public IReadOnlyList<AssertionResponse> Concepts { get; init; } = [];

    /// <summary>
    /// The domains this posting rolls up to, with how many of its concepts sit under each.
    /// </summary>
    /// <remarks>
    /// Derived by walking the closure upward from every asserted concept, never by matching the
    /// domain's name in the text - adverts do not describe themselves as "backend development",
    /// and matching the phrase would count the handful that happen to use it as though they
    /// were the population. Server-side because the closure lives in the vocabulary, and a
    /// client would need the whole graph to compute it.
    /// </remarks>
    public IReadOnlyList<RollupResponse> Domains { get; init; } = [];

    /// <summary>
    /// Surface forms seen and deliberately not resolved.
    /// </summary>
    /// <remarks>
    /// Shown rather than hidden. "Nobody asked for this" and "we could not tell" are different
    /// answers, and this is the only place a reader can see which one they are looking at - it
    /// is also where the next batch of vocabulary comes from.
    /// </remarks>
    public IReadOnlyList<MentionResponse> Mentions { get; init; } = [];

    /// <summary>Sparse facts: visa sponsorship, IR35. Name plus an optional value.</summary>
    public IReadOnlyList<TagResponse> Tags { get; init; } = [];

    /// <summary>Normalised job types, as a set rather than the delimited column.</summary>
    public IReadOnlyList<string> JobTypes { get; init; } = [];

    /// <summary>Which configured searches surfaced this posting, and when each last did.</summary>
    public IReadOnlyList<AttributionResponse> FoundBy { get; init; } = [];

    public CompanyResponse? Company { get; init; }

    /// <summary>
    /// Which passes have run over this posting, and at which version.
    /// </summary>
    /// <remarks>
    /// The honest footer. A posting enriched at version 1 and never re-read is a different
    /// object from one at the current version, and without this the difference is invisible -
    /// a reader would take a thin set of concepts for a thin advert.
    /// </remarks>
    public required ProvenanceResponse Provenance { get; init; }
}

/// <param name="Concept">The stable key, e.g. <c>skill.kubernetes</c>.</param>
/// <param name="Label">Its human-readable name.</param>
/// <param name="Kind"><c>Skill</c> or <c>Qualification</c>. Domains are never asserted directly.</param>
/// <param name="Source">
/// <c>Board</c> (the employer's own tagging), <c>Taxonomy</c> (a string match against the text)
/// or <c>Model</c> (a judgement). Not equally good evidence, and worth rendering differently.
/// </param>
/// <param name="Polarity">
/// <c>Required</c>, <c>Preferred</c>, <c>Mentioned</c> or <c>Unspecified</c>. Only the model
/// pass can populate anything but Unspecified - a regex cannot tell essential from desirable.
/// </param>
/// <param name="Evidence">The phrase it was read from, verbatim. Null for board tags, which have none.</param>
public readonly record struct AssertionResponse(
    string Concept,
    string Label,
    string Kind,
    string Source,
    string Polarity,
    int? YearsMin,
    int? YearsMax,
    string? Evidence,
    double? Confidence);

/// <param name="Concept">A domain key, e.g. <c>area.backend</c>.</param>
/// <param name="Count">How many of this posting's concepts sit beneath it in the closure.</param>
public readonly record struct RollupResponse(string Concept, string Label, int Count);

/// <param name="Reason">
/// <c>Ambiguous</c> (the form names a concept but cannot be trusted to mean it - "Go", "R"),
/// <c>UnknownBoardSkill</c> or <c>UnknownModelSkill</c>.
/// </param>
public readonly record struct MentionResponse(string SurfaceForm, string Reason, int Occurrences);

public readonly record struct TagResponse(string Name, string? Value);

public readonly record struct AttributionResponse(
    string SearchTerm,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

public readonly record struct CompanyResponse(
    string DisplayName,
    string? Industry,
    string? EmployeesBand,
    string? Revenue,
    string? Url);

/// <param name="EnrichmentVersion">Which deterministic classifier set produced the columns.</param>
/// <param name="ExtractorVersion">Which model pass produced the assertions. Null where none has run.</param>
/// <param name="Model">The deployment that answered.</param>
/// <param name="SeenCount">How many scrape runs have surfaced this posting.</param>
public readonly record struct ProvenanceResponse(
    int EnrichmentVersion,
    int? ExtractorVersion,
    string? Model,
    DateTimeOffset? ExtractedAtUtc,
    int SeenCount,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);
