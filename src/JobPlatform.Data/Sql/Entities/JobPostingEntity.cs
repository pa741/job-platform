using JobPlatform.Core.Enrichment;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// A posting as stored. Keyed by <see cref="SourceKey"/> (board + site-local id), so the
/// same job cross-posted to two boards is two rows — <see cref="ContentHash"/> is what
/// links them.
/// </summary>
public sealed class JobPostingEntity
{
    public long Id { get; set; }

    /// <summary>"{site}:{externalId}" — stable across runs, unique.</summary>
    public required string SourceKey { get; set; }

    public required string Site { get; set; }
    public required string ExternalId { get; set; }

    /// <summary>SHA-256 of normalised title|company|location, for deciding whether a posting changed.</summary>
    /// <remarks>
    /// <b>Do not widen this to cross boards</b> - <see cref="CrossBoardKey"/> is the one for
    /// that. <c>EmbeddingRepository</c> compares this to decide whether a vector is stale, so
    /// changing what it hashes marks the whole embedded corpus for re-embedding, or worse,
    /// quietly stops marking things that did change.
    /// </remarks>
    public required string ContentHash { get; set; }

    /// <summary>
    /// SHA-256 of <c>JobFingerprint.CrossBoardKey</c> - normalised title|company|<b>city</b> -
    /// or null where the employer or the city is unknown.
    /// </summary>
    /// <remarks>
    /// <b>Two fingerprints, and merging them is a regression.</b> <see cref="ContentHash"/>
    /// answers "did this posting change" and folds in the <i>raw</i> location string, which
    /// boards write differently - "London, England, United Kingdom" against "London, UK" - so it
    /// matched across boards zero times in 5,268 live postings. This answers "is this the same
    /// job as that one" and parses the city out first, which matched 285 times on the same
    /// corpus.
    ///
    /// <b>Stored as the hash, not as the composite, and that is forced rather than chosen.</b>
    /// <c>JobFingerprint.CrossBoardKey</c> returns the readable <c>title|company|city</c> string,
    /// which against this schema's own column widths runs to 952 characters - 1,904 bytes, where
    /// SQL Server caps a nonclustered index key at 1,700. The index below would fail the
    /// migration outright, exactly as the comment on the <c>(Company, LocationCity)</c> index
    /// records for the same arithmetic. Hashing makes it 64 characters, indexable, and the same
    /// shape as every other fingerprint column here. <b>The writer must hash</b>: a raw composite
    /// written into this column is a value SQL Server refuses and SQLite silently keeps.
    ///
    /// <b>Null where the city or the employer is unknown, and that nullability is measured.</b>
    /// Title and employer alone matched 285 postings; adding the city left 211 - so 74 of them,
    /// better than a quarter, were one employer advertising one title in several cities, and
    /// merging those hands somebody the apply link for the wrong city's vacancy. An unlocated
    /// posting is not the same job as another unlocated posting, so it gets no key rather than
    /// the empty one.
    /// </remarks>
    public string? CrossBoardKey { get; set; }

    public required string Title { get; set; }
    public string? Company { get; set; }

    public string? LocationRaw { get; set; }
    public string? LocationCity { get; set; }
    public string? LocationRegion { get; set; }
    public string? LocationCountry { get; set; }

    /// <summary>
    /// Nullable because silence is common and is not a "no". freehire returns null whenever
    /// it has no work mode, and Indeed computes the flag by searching the text for "remote" -
    /// so false means those words were absent, not that the employer said office-based.
    /// </summary>
    public bool? IsRemote { get; set; }
    public string? JobType { get; set; }
    public DateOnly? DatePosted { get; set; }

    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public string? Currency { get; set; }
    public string? SalaryInterval { get; set; }
    public string? SalarySource { get; set; }

    public string? JobLevel { get; set; }
    public string? JobFunction { get; set; }
    public string? CompanyIndustry { get; set; }

    public string? JobUrl { get; set; }
    public string? JobUrlDirect { get; set; }

    /// <summary>
    /// Whether the application happens on the employer's own system. Null where the scraper
    /// did not establish it, which is not the same as the board hosting it - see
    /// <c>JobPosting.OffsiteApply</c>.
    /// </summary>
    public bool? OffsiteApply { get; set; }
    public string? CompanyUrl { get; set; }

    /// <summary>Full text. Only the posting detail returns it; it is the bulk of the row.</summary>
    public string? Description { get; set; }
    public int DescriptionLength { get; set; }

    public string? CompanyNumEmployees { get; set; }
    public string? ExperienceRange { get; set; }

    /// <summary>freehire's synopsis. Null for every scraped board.</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// freehire's read on whether the posting is a real, current opening.
    /// <see cref="FakeFreshness"/> stays nullable: false is a verdict, null is silence.
    /// </summary>
    public string? FreshnessClass { get; set; }

    public int? PostingAgeDays { get; set; }
    public int? RepostCount { get; set; }
    public bool? FakeFreshness { get; set; }

    // --- recovered from columns the parser used to read and discard -------------------

    /// <summary>freehire's real origin board: a first-party ATS versus a re-aggregation.</summary>
    public string? SourceBoard { get; set; }

    /// <summary>LinkedIn's competition signal, as published and as a number.</summary>
    public string? Applicants { get; set; }
    public int? ApplicantCount { get; set; }

    public string? ListingType { get; set; }

    /// <summary>The board's own work-mode string. What fixes the hybrid/on-site collapse.</summary>
    public string? WorkFromHomeType { get; set; }

    public int? VacancyCount { get; set; }

    /// <summary>
    /// Whether the listing exposed a direct contact address. The addresses themselves are
    /// never stored: they are recruiter PII and this repository is public. The signal is kept,
    /// the personal data is not.
    /// </summary>
    public bool HasContactEmail { get; set; }

    // --- derived by PostingEnricher ----------------------------------------------------

    public int? CompanyId { get; set; }
    public CompanyEntity? CompanyRef { get; set; }

    public Seniority Seniority { get; set; }
    public RoleFamily RoleFamily { get; set; }

    public WorkArrangement WorkArrangement { get; set; }
    public int? HybridDaysInOffice { get; set; }

    public int? YearsExperienceMin { get; set; }
    public int? YearsExperienceMax { get; set; }

    /// <summary>
    /// Salary on one scale, from the board's columns where it filled them and from the
    /// description where it did not.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MinAmount"/> rather than overwriting it: that column is what
    /// the scraper delivered and is what <c>fieldFillRates</c> measures. Filling it with an
    /// inferred value would make coverage look like it improved when only the inference did.
    /// </remarks>
    public decimal? AnnualSalaryMin { get; set; }
    public decimal? AnnualSalaryMax { get; set; }
    public string? AnnualSalaryCurrency { get; set; }

    /// <summary>
    /// True where the figure came from prose. A weaker number, and an average that mixes the
    /// two without distinguishing them is measuring two different things at once.
    /// </summary>
    public bool SalaryFromText { get; set; }

    /// <summary>
    /// What the source said before annualisation. A GBP 600/day contract annualised to 156,000
    /// is not the same offer as a 156,000 salary, and this is the only field that can tell them
    /// apart afterwards.
    /// </summary>
    public string? SalaryStatedInterval { get; set; }

    /// <summary>Null where the posting said nothing, rather than false.</summary>
    public bool? VisaSponsorship { get; set; }

    /// <summary>Derived from the concept closure, not extracted separately.</summary>
    public bool RequiresSecurityClearance { get; set; }
    public bool RequiresDegree { get; set; }

    /// <summary><c>inside</c>, <c>outside</c>, or null. UK contract market only.</summary>
    public string? Ir35 { get; set; }

    /// <summary>
    /// Which enricher wrote the derived columns. Rows below the current value are stale and
    /// can be recomputed from the stored description without re-scraping.
    /// </summary>
    public int EnrichmentVersion { get; set; }

    /// <summary>What the posting asks for. See <see cref="PostingConceptEntity"/>.</summary>
    public ICollection<PostingConceptEntity> Concepts { get; set; } = [];

    /// <summary>Surface forms seen and deliberately not resolved.</summary>
    public ICollection<PostingMentionEntity> Mentions { get; set; } = [];

    public ICollection<JobPostingJobTypeEntity> JobTypes { get; set; } = [];

    public ICollection<PostingTagEntity> Tags { get; set; } = [];

    public ICollection<PostingExtractionEntity> Extractions { get; set; } = [];

    /// <summary>Across every search. Per-search timings live on <see cref="SearchTerms"/>.</summary>
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int SeenCount { get; set; }

    /// <summary>
    /// Which configured searches turned this posting up. A posting can match several, so
    /// this is a collection rather than a column — see <see cref="JobPostingSearchTerm"/>.
    /// The run ids that drive the "new today" metric live here, per term.
    /// </summary>
    public ICollection<JobPostingSearchTerm> SearchTerms { get; set; } = [];
}
