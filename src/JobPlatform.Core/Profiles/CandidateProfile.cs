using System.Globalization;
using System.Text;
using JobPlatform.Core.Enrichment;

namespace JobPlatform.Core.Profiles;

/// <summary>
/// What a candidate holds, as they filled it in.
/// </summary>
/// <remarks>
/// <b>A form, not an uploaded CV.</b> Parsing a PDF back into structure is a lossy guess at
/// something the person already knows: which employer, which dates, which of these bullet
/// points is the one that matters. Asking directly skips the guess entirely, and it produces
/// the one thing a parsed CV never does - a record with fields, which can be matched against
/// postings, filtered, and rewritten into a tailored document later. The generated CV is an
/// output of this, not an input to it.
///
/// This is the supply side of the shape the demand side already has. The free text in here goes
/// through <see cref="IDocumentExtractor"/> with <see cref="DocumentKind.Profile"/> and comes
/// back as concept assertions in the same vocabulary a posting's requirements land in, which is
/// what makes matching a join rather than a second pipeline.
/// </remarks>
public sealed record CandidateProfile
{
    /// <summary>
    /// The Entra object id of the person this belongs to.
    /// </summary>
    /// <remarks>
    /// <c>oid</c>, never <c>sub</c>. <c>sub</c> is pairwise per application, so a profile keyed
    /// on it would be orphaned the moment this system was reached through a second app
    /// registration. <c>oid</c> is the stable directory object id - the same distinction
    /// <c>/me</c> already makes, and for the same reason.
    /// </remarks>
    public required string SubjectId { get; init; }

    /// <summary>What the person calls themselves. Feeds the CV header and the match prompt.</summary>
    public string? Headline { get; init; }

    public string? FullName { get; init; }

    /// <summary>Contact block for a generated CV. Never used for anything else.</summary>
    public string? Email { get; init; }

    public string? Phone { get; init; }

    /// <summary>A short personal statement, in the candidate's own words.</summary>
    public string? Summary { get; init; }

    public string? LocationCity { get; init; }
    public string? LocationCountry { get; init; }

    /// <summary>Willing to move for the right role. Widens the location component of a match.</summary>
    public bool WillingToRelocate { get; init; }

    /// <summary>
    /// Where they want to work, on the same three-way scale a posting is classified on.
    /// </summary>
    /// <remarks>
    /// <see cref="Enrichment.WorkArrangement.Unknown"/> means no preference and is treated as
    /// such: it scores neutral rather than penalising every posting that states a policy.
    /// </remarks>
    public WorkArrangement PreferredArrangement { get; init; }

    /// <summary>Most days a week they will go in, where hybrid is acceptable.</summary>
    public int? MaxDaysInOffice { get; init; }

    /// <summary>Annualised, in <see cref="SalaryCurrency"/>. The floor, not the target.</summary>
    public decimal? MinimumSalary { get; init; }

    public string? SalaryCurrency { get; init; }

    /// <summary>Normalised job types they will consider - <c>fulltime</c>, <c>contract</c>.</summary>
    public IReadOnlyList<string> JobTypes { get; init; } = [];

    /// <summary>
    /// Total professional experience, in years.
    /// </summary>
    /// <remarks>
    /// Asked for rather than summed from <see cref="Experiences"/>. Overlapping roles, career
    /// breaks and freelance work all make the sum wrong, and the person knows the number.
    /// </remarks>
    public int? YearsExperience { get; init; }

    /// <summary>Where they sit on the seniority ladder postings are classified on.</summary>
    public Seniority Seniority { get; init; }

    public IReadOnlyList<ProfileExperience> Experiences { get; init; } = [];
    public IReadOnlyList<ProfileEducation> Education { get; init; } = [];
    public IReadOnlyList<ProfileProject> Projects { get; init; } = [];
    public IReadOnlyList<ProfileCertification> Certifications { get; init; } = [];
    public IReadOnlyList<ProfileLanguage> Languages { get; init; } = [];
    public IReadOnlyList<ProfileLink> Links { get; init; } = [];

    /// <summary>
    /// Skills the candidate claimed outright, with the strength they claimed them at.
    /// </summary>
    /// <remarks>
    /// Kept separate from what the extractor finds in their prose, and stored under
    /// <see cref="AssertionSource.Board"/> - the supply-side equivalent of an employer's own
    /// structured tagging. Someone stating "expert in Kubernetes" is better evidence than a
    /// model inferring it from a sentence, and the match is allowed to weigh the two
    /// differently precisely because the source survives into the data.
    /// </remarks>
    public IReadOnlyList<DeclaredSkill> DeclaredSkills { get; init; } = [];

    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>
    /// Everything free-text in this profile, as one document for the extractor.
    /// </summary>
    /// <remarks>
    /// Labelled section by section rather than concatenated raw. The model is being asked to
    /// tell an employment history from a side project - a bullet point reading "built a
    /// Kubernetes operator" means something different in each - and stripping the headings
    /// throws away the only thing that distinguishes them.
    ///
    /// Declared skills are deliberately absent: they already carry a concept key and a
    /// strength, and feeding them back through the model would invite it to re-derive worse
    /// versions of facts that are already exact.
    /// </remarks>
    public string ToDocument()
    {
        var builder = new StringBuilder(4_000);

        Section(builder, "Headline", Headline);
        Section(builder, "Summary", Summary);

        if (YearsExperience is { } years)
        {
            Section(builder, "Total experience", $"{years} years");
        }

        foreach (var experience in Experiences)
        {
            Section(
                builder,
                $"Experience: {experience.Title} at {experience.Company} ({experience.Period()})",
                experience.Description);
        }

        foreach (var education in Education)
        {
            Section(
                builder,
                $"Education: {education.Qualification} at {education.Institution}",
                education.Description ?? education.FieldOfStudy);
        }

        foreach (var project in Projects)
        {
            Section(builder, $"Project: {project.Name}", project.Description);
        }

        foreach (var certification in Certifications)
        {
            Section(builder, "Certification", $"{certification.Name} ({certification.Issuer})");
        }

        return builder.ToString();
    }

    private static void Section(StringBuilder builder, string heading, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        builder.Append("## ").AppendLine(heading);
        builder.AppendLine(body.Trim()).AppendLine();
    }
}

/// <summary>One role held.</summary>
/// <param name="Company">Employer, or the client for contract work.</param>
/// <param name="Title">The job title as held, not as normalised.</param>
/// <param name="StartDate">Month precision is enough; the day is noise on a CV.</param>
/// <param name="EndDate">Null means current, and is rendered as "Present".</param>
/// <param name="Description">
/// What they did, in their own words. The single richest input the extractor gets, and the raw
/// material the writing model rewrites into tailored bullet points later.
/// </param>
public sealed record ProfileExperience(
    string Company,
    string Title,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    string? Description = null,
    string? LocationCity = null,
    string? LocationCountry = null)
{
    /// <summary>"Mar 2021 - Present", for a heading the model and the CV both read.</summary>
    public string Period()
        => (StartDate, EndDate) switch
        {
            (null, null) => "dates not given",
            ({ } from, null) => $"{Month(from)} - Present",
            (null, { } to) => $"until {Month(to)}",
            ({ } from, { } to) => $"{Month(from)} - {Month(to)}",
        };

    private static string Month(DateOnly date)
        => date.ToString("MMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>One qualification, held or in progress.</summary>
public sealed record ProfileEducation(
    string Institution,
    string Qualification,
    string? FieldOfStudy = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    string? Grade = null,
    string? Description = null);

/// <summary>
/// Something built outside employment.
/// </summary>
/// <remarks>
/// A first-class section rather than folded into experience, because it is what a candidate
/// with a thin employment history actually has to offer and a posting's requirements match
/// against it exactly as well. Collapsing the two would hide which is which from everything
/// reading the profile back, including the model writing the CV.
/// </remarks>
public sealed record ProfileProject(
    string Name,
    string? Description = null,
    string? Url = null,
    DateOnly? CompletedOn = null);

public sealed record ProfileCertification(
    string Name,
    string? Issuer = null,
    int? Year = null);

/// <summary>A spoken language and how well. CEFR where the candidate knows it, prose where not.</summary>
public sealed record ProfileLanguage(string Name, string? Level = null);

/// <summary>GitHub, LinkedIn, a portfolio. Rendered into the CV header verbatim.</summary>
public sealed record ProfileLink(string Label, string Url);

/// <summary>
/// A skill the candidate claimed, against a key from the shared vocabulary.
/// </summary>
/// <param name="ConceptKey">
/// Must exist in the graph. A key that does not is recorded as an unresolved mention rather
/// than stored, exactly as it would be on the posting side - the vocabulary has one growth
/// mechanism and this feeds the same one.
/// </param>
/// <param name="Polarity">
/// The supply half of <see cref="AssertionPolarity"/>: Familiar, Proficient or Expert. A value
/// from the demand half is a caller bug, and the gap between the halves is what makes it a
/// loud one rather than a subtle one.
/// </param>
/// <param name="Years">Years using it specifically, where the candidate gives a number.</param>
public sealed record DeclaredSkill(
    string ConceptKey,
    AssertionPolarity Polarity = AssertionPolarity.Proficient,
    int? Years = null);
