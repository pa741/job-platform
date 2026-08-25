using System.ComponentModel.DataAnnotations;

namespace JobPlatform.Api.Features.Profiles;

/// <summary>
/// The profile form, as the client sends and receives it.
/// </summary>
/// <remarks>
/// Contracts rather than the domain record, for the reason contracts usually exist and one that
/// is specific here. <see cref="Core.Profiles.CandidateProfile"/> carries
/// <c>SubjectId</c>, and a client must never be able to set it: a request body naming somebody
/// else's directory object id would otherwise write into their profile. The identifier comes
/// from the token and only from the token, which is enforced by it being absent from this type
/// rather than by a check somebody has to remember.
///
/// Enums travel as strings and are parsed against the same values the posting side publishes,
/// so a client can offer the same vocabulary for both halves of a match.
/// </remarks>
public record ProfileRequest
{
    [MaxLength(200)]
    public string? FullName { get; init; }

    [MaxLength(300)]
    public string? Headline { get; init; }

    [EmailAddress]
    [MaxLength(320)]
    public string? Email { get; init; }

    [MaxLength(50)]
    public string? Phone { get; init; }

    /// <summary>
    /// A personal statement. Bounded generously rather than not at all.
    /// </summary>
    /// <remarks>
    /// The column is unbounded but the request is not: this text is sent to a model, and an
    /// unbounded field is a way to make somebody else's extraction call arbitrarily expensive.
    /// </remarks>
    [MaxLength(8000)]
    public string? Summary { get; init; }

    [MaxLength(150)]
    public string? LocationCity { get; init; }

    [MaxLength(100)]
    public string? LocationCountry { get; init; }

    public bool WillingToRelocate { get; init; }

    /// <summary><c>Unknown</c>, <c>OnSite</c>, <c>Hybrid</c> or <c>Remote</c>. Unknown means no preference.</summary>
    public string? PreferredArrangement { get; init; }

    [Range(0, 5)]
    public int? MaxDaysInOffice { get; init; }

    [Range(0, 100_000_000)]
    public decimal? MinimumSalary { get; init; }

    [MaxLength(10)]
    public string? SalaryCurrency { get; init; }

    [MaxLength(10)]
    public IReadOnlyList<string> JobTypes { get; init; } = [];

    [Range(0, 70)]
    public int? YearsExperience { get; init; }

    /// <summary>Where they sit on the ladder postings are classified on.</summary>
    public string? Seniority { get; init; }

    [MaxLength(40)]
    public IReadOnlyList<ExperienceRequest> Experiences { get; init; } = [];

    [MaxLength(20)]
    public IReadOnlyList<EducationRequest> Education { get; init; } = [];

    [MaxLength(40)]
    public IReadOnlyList<ProjectRequest> Projects { get; init; } = [];

    [MaxLength(40)]
    public IReadOnlyList<CertificationRequest> Certifications { get; init; } = [];

    [MaxLength(20)]
    public IReadOnlyList<LanguageRequest> Languages { get; init; } = [];

    [MaxLength(10)]
    public IReadOnlyList<LinkRequest> Links { get; init; } = [];

    /// <summary>
    /// Skills claimed outright, as concept keys from the shared vocabulary.
    /// </summary>
    /// <remarks>
    /// Keys, not labels. A client offering this field builds its picker from
    /// <c>/postings/facets</c>, which already publishes the vocabulary - so the two halves of a
    /// match are guaranteed to be talking about the same concepts rather than two spellings of
    /// one. A key the graph does not know is recorded as a mention and does not fail the save.
    /// </remarks>
    [MaxLength(200)]
    public IReadOnlyList<DeclaredSkillRequest> DeclaredSkills { get; init; } = [];
}

public sealed record ExperienceRequest
{
    [Required]
    [MaxLength(300)]
    public string Company { get; init; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string Title { get; init; } = string.Empty;

    public DateOnly? StartDate { get; init; }

    /// <summary>Null means current.</summary>
    public DateOnly? EndDate { get; init; }

    [MaxLength(150)]
    public string? LocationCity { get; init; }

    [MaxLength(100)]
    public string? LocationCountry { get; init; }

    [MaxLength(8000)]
    public string? Description { get; init; }
}

public sealed record EducationRequest
{
    [Required]
    [MaxLength(300)]
    public string Institution { get; init; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Qualification { get; init; } = string.Empty;

    [MaxLength(200)]
    public string? FieldOfStudy { get; init; }

    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }

    [MaxLength(100)]
    public string? Grade { get; init; }

    [MaxLength(4000)]
    public string? Description { get; init; }
}

public sealed record ProjectRequest
{
    [Required]
    [MaxLength(300)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(4000)]
    public string? Description { get; init; }

    [MaxLength(1000)]
    public string? Url { get; init; }

    public DateOnly? CompletedOn { get; init; }
}

public sealed record CertificationRequest
{
    [Required]
    [MaxLength(300)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(300)]
    public string? Issuer { get; init; }

    [Range(1950, 2100)]
    public int? Year { get; init; }
}

public sealed record LanguageRequest
{
    [Required]
    [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(50)]
    public string? Level { get; init; }
}

public sealed record LinkRequest
{
    [Required]
    [MaxLength(50)]
    public string Label { get; init; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string Url { get; init; } = string.Empty;
}

public sealed record DeclaredSkillRequest
{
    [Required]
    [MaxLength(100)]
    public string ConceptKey { get; init; } = string.Empty;

    /// <summary><c>Familiar</c>, <c>Proficient</c> or <c>Expert</c>. Anything else is stored as Proficient.</summary>
    public string? Level { get; init; }

    [Range(0, 70)]
    public int? Years { get; init; }
}

/// <summary>
/// The stored profile, as it is read back.
/// </summary>
/// <remarks>
/// Deliberately close to the request, plus what only the server knows: when it was last saved,
/// whether the extractor has read it, and which of the declared skills resolved. That last one
/// is the useful part - a client can show the candidate that the technology they typed is not
/// in the vocabulary yet, rather than silently dropping it.
/// </remarks>
public sealed record ProfileResponse : ProfileRequest
{
    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>
    /// Concepts found in the candidate's prose by the model pass.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="ProfileRequest.DeclaredSkills"/>, which are what the candidate
    /// claimed. Merging them would make it impossible to tell what someone said about themselves
    /// from what was inferred about them, which is a distinction a person is entitled to see.
    /// </remarks>
    public IReadOnlyList<ExtractedSkillResponse> ExtractedSkills { get; init; } = [];

    /// <summary>Null until the extractor has read this profile.</summary>
    public DateTimeOffset? ExtractedAtUtc { get; init; }
}

public sealed record ExtractedSkillResponse
{
    public required string ConceptKey { get; init; }

    /// <summary>The human-readable name, so a client need not carry the vocabulary.</summary>
    public required string Label { get; init; }

    public required string Level { get; init; }

    public int? Years { get; init; }

    /// <summary>The phrase it was read from. What makes an inference checkable by the candidate.</summary>
    public string? Evidence { get; init; }
}
