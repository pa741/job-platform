using JobPlatform.Core.Applications;
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

    /// <summary>
    /// The employer apply URL as the board carrying the advert published it.
    /// </summary>
    /// <remarks>
    /// <b>Only the board that carried the advert may write here, and a recovered link may not.</b>
    /// This column is the one fact in the apply-link story that nobody inferred: no token to be
    /// right about, no title to match, nothing between the advert and the link - which is why
    /// <c>ApplyUrlSource.Posting</c> outranks every other provenance and why the queue reads this
    /// column first. A link recovered from the employer's own applicant tracking system goes to
    /// <see cref="EmployerAtsApplyUrl"/> instead. <b>Writing one here would not be a shortcut, it
    /// would erase the distinction the whole feature rests on</b>: the recovered link opens a form
    /// exactly like a published one, so once the two share a column nothing downstream - and
    /// nobody reading the row afterwards - can tell which of them was a guess that went wrong.
    ///
    /// Its <i>absence</i> is load-bearing too, and is not on its own a fact about the board: "no
    /// apply URL" meant either "the board hosts the application" or "nobody opened the detail
    /// page" until <see cref="OffsiteApply"/> was added to tell them apart.
    /// </remarks>
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

    // --- recovered from the employer's own applicant tracking system --------------------

    /// <summary>
    /// The apply URL read off the employer's own board, and null where there is none.
    /// </summary>
    /// <remarks>
    /// <b>A second column rather than a value written into <see cref="JobUrlDirect"/>, and the
    /// separation is the entire provenance argument made physical.</b> That column is what the
    /// board publishing the advert said; this one is what the employer's applicant tracking system
    /// said when it was asked about a posting the board had gone quiet on. Both open a form, and
    /// nothing a browser sees separates them - so only the columns can, and a caller that cannot
    /// tell an inference from a published fact has no way to notice when the match was wrong. Two
    /// ways this one can be wrong and neither is visible downstream: the board token may belong to
    /// another company, and the title match may have landed on the vacancy next to the right one.
    /// The queue projects <c>ApplyUrlSource.MatchedOnEmployerAts</c> from the presence of this
    /// column, which ranks it below <see cref="JobUrlDirect"/> and above a link borrowed off a
    /// stranger's listing on another board.
    ///
    /// <b>The provenance is the column and is not repeated in one beside it.</b> A recovered link
    /// is <c>MatchedOnEmployerAts</c> by construction - nothing else may write here - so a second
    /// column naming the source would be a copy free to disagree with the fact it copies, which is
    /// the reason <c>AtsListing</c> carries no vendor either. The board it came from is
    /// recoverable the same way: <c>AtsBoardToken.FromUrl</c> over this value returns the vendor,
    /// token and region exactly, because a recovered link is by construction on a board host that
    /// function can read.
    ///
    /// 1000 characters, matching <see cref="JobUrlDirect"/> because it holds the same kind of
    /// value from the same hosts - and, as it happens, matching
    /// <c>SubmissionLimits.MaxApplyUrlLength</c>, so a recovered link carried into a submission
    /// records where the application actually went rather than a prefix of it. Deliberately not a
    /// shared constant: the three agreeing is a coincidence worth keeping, not a fact to centralise
    /// where widening one silently widens the others.
    /// </remarks>
    public string? EmployerAtsApplyUrl { get; set; }

    /// <summary>
    /// How much of the posting the matched board listing agreed with, and null where nothing was
    /// matched.
    /// </summary>
    /// <remarks>
    /// <b>Stored because a caller can act on it and cannot re-derive it.</b> <c>TitleOnly</c> means
    /// the titles agreed and the place could not be checked - one side named an arrangement rather
    /// than a city, or named nothing; <c>TitleAndPlace</c> means both sides named a place and it
    /// was the same place, which is the failure the whole match rule exists to exclude actually
    /// being excluded rather than merely undetectable. That is a bar an unattended run may want to
    /// raise itself to, and re-deriving it later would mean fetching somebody else's board again
    /// to ask a question already answered.
    ///
    /// It does not gate the link: <c>AtsMatchConfidence</c> numbers from one and never from zero,
    /// so this is null exactly when <see cref="EmployerAtsApplyUrl"/> is null and can never read as
    /// a match that was not made.
    /// </remarks>
    public AtsMatchConfidence? EmployerAtsMatchConfidence { get; set; }

    /// <summary>
    /// When the employer's board was last asked about this posting, and null where it never has
    /// been.
    /// </summary>
    /// <remarks>
    /// <b>The three-state rule, applied again, and the third state is again the load-bearing
    /// one.</b> Without this column a null <see cref="EmployerAtsApplyUrl"/> means either "the
    /// board was read and had nothing matching this posting" or "nobody has asked yet", and those
    /// want opposite work: the first is settled until the board changes, the second is the pass's
    /// entire work list. Collapsing them is the fault <see cref="OffsiteApply"/> was added to
    /// undo, and it would show up here as a pass that re-asks the same employer about the same
    /// unmatched postings every run, spending somebody else's rate limit to reach the same answer.
    ///
    /// It is also what makes the abstention visible. <c>AtsListingMatch</c> distinguishes
    /// "nothing matched" from "several listings matched and the rule declined to choose", and both
    /// leave the link null - so a stamp here with no link is a posting whose board <i>was</i> read,
    /// which is the only trace either outcome leaves on the row.
    /// </remarks>
    public DateTimeOffset? EmployerAtsCheckedUtc { get; set; }

    /// <summary>
    /// Whose application system sits at the end of this row's own apply link, or null where
    /// nothing has derived it yet.
    /// </summary>
    /// <remarks>
    /// <b>A stored derivation, for the reason <see cref="Seniority"/> and
    /// <see cref="AnnualSalaryMin"/> are stored ones.</b> <c>AtsVendorDetector.Detect</c> reads a
    /// URL rather than compares one - it parses a host, walks it to a label boundary and reads the
    /// query parameters - so it has no SQL at all, and every caller that wanted it has had to run
    /// it after materialisation. That is fine for the apply queue, which computes it over the page
    /// it already holds. It is not fine for a <i>filter</i>: the shortlist is <c>Skip</c>/<c>Take</c>
    /// paged, and a predicate applied after the bound is not a filter, it is a silent reduction of
    /// the page size with an offset that steps over rows the caller never saw. This column is what
    /// lets the shortlist's aggregator facet run in the query, before the bound, like every other
    /// filter on that path.
    ///
    /// <b>It is the vendor of the link this row itself would hand over, and it stops there.</b>
    /// <c>JobUrlDirect ?? EmployerAtsApplyUrl ?? JobUrl</c> - the ladder
    /// <c>JobMatchRepository.ResolveApplyTargetAsync</c> already resolves an apply target by, and
    /// deliberately not the queue's fourth rung. The queue may borrow a link off the same job on
    /// another board; a row here is one posting rather than a cluster, and the borrowed link
    /// belongs to the listing that published it - which is itself in the shortlist, carrying its
    /// own employer vendor. So a LinkedIn listing whose twin can be applied to is hidden by the
    /// facet and its twin is not, which is the answer the facet is for.
    ///
    /// <b>Null means nobody has derived it, and never <see cref="AtsVendor.Unknown"/>.</b> That
    /// member means "there is no destination to reason about" - a blank link, a <c>mailto:</c>, a
    /// string that is not a URL - which is a verdict about the row, and a migration that stamped it
    /// on 7,368 untouched postings would be asserting that verdict about every one of them. The
    /// same argument, and the same nullability, as <see cref="EmployerAtsMatchConfidence"/> and
    /// <see cref="OffsiteApply"/>. <b>The facet reads null as "keep"</b>: a filter that hid what it
    /// had not judged would empty the shortlist on the morning of the deploy.
    ///
    /// <b>Every writer of the three columns it reads must rewrite it, and there are two.</b>
    /// <c>JobPostingRepository.Apply</c> runs on every posting the scraper sees, so the corpus
    /// refreshes itself nightly; <c>EmployerAtsBoardRepository</c> rewrites it beside a recovered
    /// link, because that is the whole point of recovering one - a LinkedIn posting stops being an
    /// aggregator row the moment the employer's own board answers about it. Postings nobody scrapes
    /// any more are reached by <c>dbadmin backfill-apply-vendor</c>, which is a console command for
    /// the reason <c>backfill-crossboard</c> is: the rule is C#, and a second implementation of it
    /// in T-SQL would disagree on the first URL either spelled differently.
    /// </remarks>
    public AtsVendor? ApplyVendor { get; set; }

    /// <summary>
    /// The vendor of the apply link a posting with these columns would hand over.
    /// </summary>
    /// <remarks>
    /// One definition and three readers - the ingest upsert, the recovered-link write and the
    /// backfill command - for the reason <c>EmployerAtsBoardRepository.WithoutEmployerLink</c>
    /// gives: a ladder written out at each of them is three spellings held together by nothing,
    /// and this codebase has already paid for that once on the shortlist's channel filter.
    ///
    /// A plain static rather than an <see cref="System.Linq.Expressions.Expression"/>, because
    /// unlike that rule this one can never compose into a query: it is exactly the call SQL cannot
    /// make, which is why the column it fills exists.
    /// </remarks>
    public static AtsVendor VendorOf(string? jobUrlDirect, string? employerAtsApplyUrl, string? jobUrl)
        => AtsVendorDetector.Detect(jobUrlDirect ?? employerAtsApplyUrl ?? jobUrl);

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
