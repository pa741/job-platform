using JobPlatform.Core.Enrichment;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// A candidate's own record, as they filled it in.
/// </summary>
/// <remarks>
/// Keyed on the Entra <c>oid</c>, and every read and write is scoped to the calling principal's
/// own. There is no endpoint that lists profiles, no admin path that reads one, and no query in
/// this repository that resolves a profile by anything other than the subject id of the token
/// that arrived - which is the property that keeps one person's employment history out of
/// another person's response.
///
/// The child collections are the form's sections, one table each rather than a JSON blob.
/// A blob would be less code and would make the entire profile opaque to the thing that has to
/// read it back: the CV writer needs the roles in order with their dates, and the match needs
/// the skills as rows to join on.
/// </remarks>
public sealed class CandidateProfileEntity
{
    public long Id { get; set; }

    /// <summary>
    /// The Entra object id. Unique, and the only way a profile is ever looked up.
    /// </summary>
    /// <remarks>
    /// <c>oid</c> and never <c>sub</c>: <c>sub</c> is pairwise per application, so a second app
    /// registration reaching this system would orphan every profile stored under the first.
    /// </remarks>
    public required string SubjectId { get; set; }

    public string? FullName { get; set; }
    public string? Headline { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>Unbounded, like a posting's description: it is a personal statement, not a field.</summary>
    public string? Summary { get; set; }

    public string? LocationCity { get; set; }
    public string? LocationCountry { get; set; }
    public bool WillingToRelocate { get; set; }

    public WorkArrangement PreferredArrangement { get; set; }
    public int? MaxDaysInOffice { get; set; }

    public decimal? MinimumSalary { get; set; }
    public string? SalaryCurrency { get; set; }

    public int? YearsExperience { get; set; }
    public Seniority Seniority { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>
    /// Hash of the document last sent to the extractor.
    /// </summary>
    /// <remarks>
    /// The same idempotency contract <c>PostingExtractions.InputHash</c> carries, and it earns
    /// its place here more obviously: a profile is saved repeatedly while someone edits it, and
    /// most of those saves change a phone number rather than anything the extractor would read
    /// differently. Comparing the composed document's hash is what stops a typo correction from
    /// costing a model call - and, more importantly, from invalidating every match already
    /// scored against it.
    /// </remarks>
    public string? ExtractionInputHash { get; set; }

    public int? ExtractorVersion { get; set; }
    public string? ExtractionModel { get; set; }
    public DateTimeOffset? ExtractedAtUtc { get; set; }
    public string? ExtractionPayloadJson { get; set; }

    public ICollection<ProfileExperienceEntity> Experiences { get; } = [];
    public ICollection<ProfileEducationEntity> Education { get; } = [];
    public ICollection<ProfileProjectEntity> Projects { get; } = [];
    public ICollection<ProfileCertificationEntity> Certifications { get; } = [];
    public ICollection<ProfileLanguageEntity> Languages { get; } = [];
    public ICollection<ProfileLinkEntity> Links { get; } = [];
    public ICollection<ProfileJobTypeEntity> JobTypes { get; } = [];
    public ICollection<ProfileConceptEntity> Concepts { get; } = [];
    public ICollection<ProfileMentionEntity> Mentions { get; } = [];
}

/// <summary>
/// One concept the candidate holds. The supply side of <see cref="PostingConceptEntity"/>.
/// </summary>
/// <remarks>
/// <b>Deliberately the same columns as the posting side.</b> That is the whole payoff of the
/// design decision taken before there was a CV: matching is a join between two tables of
/// identical shape rather than a translation layer between two vocabularies. The only thing
/// that differs is which half of <see cref="AssertionPolarity"/> is meaningful - Familiar
/// through Expert here, Mentioned through Required there - and the numeric gap between the
/// halves is what makes a demand value accidentally compared against a supply value obviously
/// wrong rather than subtly wrong.
///
/// <see cref="AssertionSource"/> carries the same distinction too, one step across:
/// <see cref="AssertionSource.Board"/> is what the candidate declared on the form - their own
/// structured statement, the supply-side equivalent of an employer's tagging - and
/// <see cref="AssertionSource.Model"/> is what the extractor found in their prose. Source is
/// part of the key for the same reason it is on the posting side, so a skill both declared and
/// written about is two rows and the match can prefer the stronger evidence.
/// </remarks>
public sealed class ProfileConceptEntity
{
    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public int ConceptId { get; set; }
    public ConceptEntity? Concept { get; set; }

    public AssertionSource Source { get; set; }

    /// <summary>The supply half: Familiar, Proficient, Expert.</summary>
    public AssertionPolarity Polarity { get; set; }

    public int? YearsMin { get; set; }
    public int? YearsMax { get; set; }

    /// <summary>The phrase the extractor read it from. Null for a declared skill, which has none.</summary>
    public string? EvidenceText { get; set; }

    public double? Confidence { get; set; }

    public int ResolverVersion { get; set; }
}

/// <summary>
/// Something the candidate named that the vocabulary has no concept for.
/// </summary>
/// <remarks>
/// The same growth loop the posting side runs, fed from the other end. A technology candidates
/// keep writing about and adverts never mention is just as much a gap in the vocabulary as the
/// reverse, and recording it separates "nobody has this skill" from "we cannot represent it".
/// </remarks>
public sealed class ProfileMentionEntity
{
    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public required string SurfaceForm { get; set; }

    public MentionReason Reason { get; set; }

    public int Occurrences { get; set; }

    public int ResolverVersion { get; set; }
}

/// <summary>One role held. Ordered by <see cref="Ordinal"/>, not by date.</summary>
/// <remarks>
/// The candidate's ordering is kept because it is a choice they made: a CV that leads with a
/// side contract rather than the most recent job is sometimes exactly right, and re-sorting by
/// date would silently overrule it. Dates are stored too, and the CV writer is given both.
/// </remarks>
public sealed class ProfileExperienceEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public int Ordinal { get; set; }

    public required string Company { get; set; }
    public required string Title { get; set; }

    public DateOnly? StartDate { get; set; }

    /// <summary>Null means current.</summary>
    public DateOnly? EndDate { get; set; }

    public string? LocationCity { get; set; }
    public string? LocationCountry { get; set; }

    /// <summary>Unbounded. The richest single input to both the extractor and the CV writer.</summary>
    public string? Description { get; set; }
}

public sealed class ProfileEducationEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public int Ordinal { get; set; }

    public required string Institution { get; set; }
    public required string Qualification { get; set; }
    public string? FieldOfStudy { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public string? Grade { get; set; }
    public string? Description { get; set; }
}

/// <summary>Something built outside employment. A first-class section, not folded into experience.</summary>
public sealed class ProfileProjectEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public int Ordinal { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public DateOnly? CompletedOn { get; set; }
}

public sealed class ProfileCertificationEntity
{
    public long Id { get; set; }

    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public int Ordinal { get; set; }

    public required string Name { get; set; }
    public string? Issuer { get; set; }
    public int? Year { get; set; }
}

public sealed class ProfileLanguageEntity
{
    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public required string Name { get; set; }
    public string? Level { get; set; }
}

/// <summary>GitHub, LinkedIn, a portfolio. Rendered into the CV header verbatim.</summary>
public sealed class ProfileLinkEntity
{
    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public required string Label { get; set; }
    public required string Url { get; set; }
}

/// <summary>
/// One normalised job type the candidate will consider.
/// </summary>
/// <remarks>
/// Rows rather than a delimited string, matching <see cref="JobPostingJobTypeEntity"/> exactly -
/// the two are compared, so they had better be the same shape.
/// </remarks>
public sealed class ProfileJobTypeEntity
{
    public long ProfileId { get; set; }
    public CandidateProfileEntity? Profile { get; set; }

    public required string JobType { get; set; }
}
