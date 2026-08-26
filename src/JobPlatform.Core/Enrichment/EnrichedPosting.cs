using JobPlatform.Core.Model;

namespace JobPlatform.Core.Enrichment;

/// <summary>
/// Everything derived from a <see cref="JobPosting"/> without calling anything out of process.
/// </summary>
/// <remarks>
/// Separate from <see cref="JobPosting"/> rather than folded into it, because the two answer
/// different questions. <see cref="JobPosting"/> is what the scraper said; this is what we
/// concluded. Keeping them apart means a change to the classifiers can never be mistaken for a
/// change in the source data — which is the distinction <c>fieldFillRates</c> exists to
/// protect, and it would be lost if inferred values could fill the same columns.
///
/// <see cref="Version"/> is stored with the row so a vocabulary change can be backfilled by
/// selecting everything below the current version, without re-scraping anything.
/// </remarks>
public sealed record EnrichedPosting
{
    /// <summary>
    /// Bumped whenever a classifier changes what it would produce for the same input.
    /// Rows below the current value are stale and eligible for a backfill pass.
    /// </summary>
    /// <remarks>
    /// 2: board tags may name a domain, ten concepts added from the mention log, and
    /// ambiguous names resolve where their context settles it.
    /// 3: "containers" is ambiguous rather than a plain alias of Containerisation.
    ///
    /// <b>A change to <c>concepts.json</c> is a change to what the classifiers produce, so it
    /// belongs here too.</b> The vocabulary carries its own version, but nothing reads that when
    /// deciding whether a stored posting is stale - <c>JobPostingRepository</c> compares this
    /// constant against <c>JobPostings.EnrichmentVersion</c>, and a vocabulary edit that leaves
    /// this alone is an edit no existing row will ever pick up. Bumping it marks the corpus
    /// stale so a reprocess, or simply the next day the posting is re-scraped, rebuilds its
    /// assertions.
    /// </remarks>
    public const int CurrentVersion = 3;

    public required JobPosting Posting { get; init; }

    public Seniority Seniority { get; init; }
    public RoleFamily RoleFamily { get; init; }

    public WorkArrangement WorkArrangement { get; init; }

    /// <summary>Days per week in the office, where a hybrid posting states a number.</summary>
    public int? HybridDaysInOffice { get; init; }

    public int? YearsExperienceMin { get; init; }
    public int? YearsExperienceMax { get; init; }

    public int? EmployeesMin { get; init; }
    public int? EmployeesMax { get; init; }

    /// <summary>
    /// Salary annualised to a single figure, from the board's columns where it filled them
    /// and from the description text where it did not.
    /// </summary>
    public decimal? AnnualSalaryMin { get; init; }
    public decimal? AnnualSalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }

    /// <summary>
    /// True when the figure came from prose rather than a salary field. It is a weaker number
    /// and a query that averages the two without distinguishing them is measuring two
    /// different things at once.
    /// </summary>
    public bool SalaryFromText { get; init; }

    /// <summary>
    /// What the source said before annualisation — <c>yearly</c>, <c>daily</c>, <c>hourly</c>.
    /// </summary>
    /// <remarks>
    /// Kept because a £600/day contract annualised to £156,000 is not the same offer as a
    /// £156,000 salary, and after annualisation this is the only field that can tell them
    /// apart.
    /// </remarks>
    public string? SalaryStatedInterval { get; init; }

    /// <summary>Company name folded to a stable key, so "Contoso Ltd" and "Contoso Limited" agree.</summary>
    public string? CompanyKey { get; init; }

    /// <summary>Normalised, deduplicated job types — the multi-valued column as a set.</summary>
    public IReadOnlyList<string> JobTypes { get; init; } = [];

    /// <summary>
    /// What the posting asks for, as concept keys. Board-supplied and text-matched assertions
    /// both appear, distinguished by <see cref="ConceptAssertion.Source"/>.
    /// </summary>
    public IReadOnlyList<ConceptAssertion> Concepts { get; init; } = [];

    /// <summary>
    /// Surface forms seen and deliberately not resolved. Never empty for the sake of it — see
    /// <see cref="UnresolvedMention"/> for why discarding these was the bug worth fixing.
    /// </summary>
    public IReadOnlyList<UnresolvedMention> Mentions { get; init; } = [];

    public IReadOnlyList<PostingTag> Tags { get; init; } = [];

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Convenience for the promoted tag columns; null when the posting is silent.</summary>
    public bool? VisaSponsorship => Flag(PostingTagNames.VisaSponsorship);

    /// <summary><c>inside</c>, <c>outside</c>, or null. UK contract postings only.</summary>
    public string? Ir35 => Tags.FirstOrDefault(t => t.Name == PostingTagNames.Ir35).Value;

    /// <summary>
    /// True where the posting names any security clearance.
    /// </summary>
    /// <remarks>
    /// Set by the enricher from the concept closure — anything under <c>type.clearance</c> —
    /// rather than extracted separately or matched against a hardcoded key list here. An
    /// earlier draft had this as its own tag alongside <c>qual.sc-clearance</c>, which meant
    /// two extractions of one fact that could disagree and no rule for which won. One source,
    /// two representations: the concept says which clearance, this says whether there is one,
    /// and adding a clearance to the vocabulary needs no change in this file.
    /// </remarks>
    public bool RequiresSecurityClearance { get; init; }

    /// <summary>True where the posting names a degree or academic qualification.</summary>
    public bool RequiresDegree { get; init; }

    private bool? Flag(string name)
        => Tags.Any(t => t.Name == name) ? true : null;
}
