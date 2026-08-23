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

    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}
