namespace JobPlatform.Core.Model;

/// <summary>
/// A single job posting as produced by JobSpy, normalised into CLR types.
/// Nearly everything is optional: real scraper output routinely has whole columns
/// empty (a sample run had 0% salary coverage and only 40% <c>date_posted</c>).
/// </summary>
public sealed record JobPosting
{
    /// <summary>Site-local identifier, e.g. <c>in-f793bbe463f08be2</c>.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Originating board: <c>indeed</c>, <c>linkedin</c>, <c>google</c>, …</summary>
    public required string Site { get; init; }

    public required string Title { get; init; }
    public string? Company { get; init; }

    /// <summary>Raw location string, e.g. <c>"London, ENG, GB"</c>.</summary>
    public string? Location { get; init; }

    public DateOnly? DatePosted { get; init; }

    /// <summary>May carry several comma-separated values, e.g. <c>"parttime, fulltime"</c>.</summary>
    public string? JobType { get; init; }

    /// <summary>
    /// Nullable on purpose. <c>false</c> means the board said the role is not remote;
    /// null means it said nothing.
    /// </summary>
    /// <remarks>
    /// freehire sends null rather than false when it cannot resolve a work mode, and a
    /// non-nullable bool turned that silence into a confident "not remote" for every
    /// hybrid and on-site posting it published. Same reasoning as <see cref="FakeFreshness"/>;
    /// <see cref="WorkFromHomeType"/> carries the three-way answer where a board offers one.
    /// </remarks>
    public bool? IsRemote { get; init; }

    public string? SalarySource { get; init; }
    public string? SalaryInterval { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public string? Currency { get; init; }

    public string? JobLevel { get; init; }
    public string? JobFunction { get; init; }
    public string? CompanyIndustry { get; init; }

    public string? JobUrl { get; init; }
    public string? JobUrlDirect { get; init; }
    public string? CompanyUrl { get; init; }

    public string? Description { get; init; }

    /// <summary>Employee band, e.g. <c>"51-200"</c>. Indeed and freehire.</summary>
    public string? CompanyNumEmployees { get; init; }

    /// <summary>Experience asked for, e.g. <c>"3+ Yrs"</c>. Naukri and freehire.</summary>
    public string? ExperienceRange { get; init; }

    /// <summary>
    /// The numbers behind <see cref="ExperienceRange"/>, where the board published them.
    /// </summary>
    /// <remarks>
    /// Preferred over parsing the string back out of <c>"3+ Yrs"</c>: the fork carries the
    /// integers the source actually had, and a round trip through a display format can only
    /// lose information. Absent from every board but freehire, and from freehire only after
    /// the fork change that added them - so the text parser stays the fallback rather than
    /// being replaced.
    /// </remarks>
    public int? ExperienceYearsMin { get; init; }
    public int? ExperienceYearsMax { get; init; }

    /// <summary>
    /// Indeed's own attribute list: benefits, shift, schedule, education.
    /// </summary>
    /// <remarks>
    /// A labelled taxonomy the employer picked from, which is better evidence than the same
    /// facts recovered from prose. Only the job type was ever read out of it upstream; the
    /// rest was discarded one line later.
    /// </remarks>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>
    /// When the posting first appeared on Indeed, as opposed to <see cref="DatePosted"/>,
    /// which the board refreshes when a posting is bumped.
    /// </summary>
    /// <remarks>
    /// The gap between the two is the only repost signal Indeed gives, and reposting is the
    /// mechanism behind most apparent "freshness" in the corpus.
    /// </remarks>
    public DateOnly? DateOnIndeed { get; init; }

    /// <summary>One or two sentence synopsis. freehire only.</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// How much of a posting's own freshness claim to believe. freehire only —
    /// a scraped board can only repeat what the listing says about itself.
    /// </summary>
    /// <remarks>
    /// <see cref="FakeFreshness"/> is nullable on purpose. <c>false</c> means freehire
    /// looked and found nothing suspect; null means nobody looked. Collapsing the two
    /// would let every Indeed row claim it had been checked.
    /// </remarks>
    public string? FreshnessClass { get; init; }

    public int? PostingAgeDays { get; init; }
    public int? RepostCount { get; init; }
    public bool? FakeFreshness { get; init; }

    /// <summary>
    /// Skills the board itself published, already structured. freehire and Naukri only —
    /// for every other board this is empty and the taxonomy pass has to infer them.
    /// </summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>
    /// Which of freehire's crawled boards a posting actually came from — <c>greenhouse</c>
    /// (a first-party ATS) versus <c>whatjobs-uk</c> (re-aggregated). <see cref="Site"/>
    /// says <c>freehire</c> for all of them, so without this the aggregator's own
    /// composition is invisible.
    /// </summary>
    public string? SourceBoard { get; init; }

    /// <summary>Verbatim applicant caption, e.g. <c>"Over 200 applicants"</c>. LinkedIn only.</summary>
    public string? Applicants { get; init; }

    /// <summary>The figure parsed out of <see cref="Applicants"/>. The competition signal.</summary>
    public int? ApplicantCount { get; init; }

    /// <summary>
    /// Three-way work mode where the board states one: <c>remote</c>, <c>hybrid</c>,
    /// <c>onsite</c>. This is the only field that distinguishes hybrid from on-site;
    /// <see cref="IsRemote"/> cannot.
    /// </summary>
    public string? WorkFromHomeType { get; init; }

    public string? ListingType { get; init; }

    /// <summary>Openings this listing covers, where the board says. Naukri and freehire.</summary>
    public int? VacancyCount { get; init; }

    /// <summary>
    /// Whether the listing exposed a contact address.
    /// </summary>
    /// <remarks>
    /// Derived from the <c>emails</c> column, which is never itself stored: real exports
    /// carry recruiter names and addresses, and the repository is public. The boolean is
    /// the part with analytical value — a direct address distinguishes an employer posting
    /// from an agency one — and it carries no personal data.
    /// </remarks>
    public bool HasContactEmail { get; init; }

    // Company attributes. These describe the employer rather than the posting, and are
    // folded into the Companies dimension on write rather than repeated on every row.
    public string? CompanyDescription { get; init; }
    public string? CompanyRevenue { get; init; }
    public double? CompanyRating { get; init; }
    public int? CompanyReviewsCount { get; init; }

    public int DescriptionLength => Description?.Length ?? 0;

    /// <summary>Natural key within a source board. Stable across runs.</summary>
    public string SourceKey => $"{Site}:{ExternalId}";
}
