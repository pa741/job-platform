using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Profiles;

namespace JobPlatform.Api.Features.Profiles;

/// <summary>Between the wire contract and the domain record.</summary>
internal static class ProfileMapping
{
    public static CandidateProfile ToDomain(this ProfileRequest request, string subjectId)
        => new()
        {
            // From the token, never from the body. See ProfileRequest for why the field is
            // absent from the contract rather than merely ignored here.
            SubjectId = subjectId,
            FullName = Trim(request.FullName),
            Headline = Trim(request.Headline),
            Email = Trim(request.Email),
            Phone = Trim(request.Phone),
            Summary = Trim(request.Summary),
            LocationCity = Trim(request.LocationCity),
            LocationCountry = Trim(request.LocationCountry),
            WillingToRelocate = request.WillingToRelocate,
            PreferredArrangement = Parse<WorkArrangement>(request.PreferredArrangement),
            MaxDaysInOffice = request.MaxDaysInOffice,
            MinimumSalary = request.MinimumSalary,
            SalaryCurrency = Trim(request.SalaryCurrency),
            JobTypes = request.JobTypes.Where(NotBlank).Select(t => t.Trim().ToLowerInvariant()).ToList(),
            YearsExperience = request.YearsExperience,
            Seniority = Parse<Seniority>(request.Seniority),
            Experiences = request.Experiences
                .Where(e => NotBlank(e.Company) || NotBlank(e.Title))
                .Select(e => new ProfileExperience(
                    e.Company.Trim(),
                    e.Title.Trim(),
                    e.StartDate,
                    e.EndDate,
                    Trim(e.Description),
                    Trim(e.LocationCity),
                    Trim(e.LocationCountry)))
                .ToList(),
            Education = request.Education
                .Where(e => NotBlank(e.Institution) || NotBlank(e.Qualification))
                .Select(e => new ProfileEducation(
                    e.Institution.Trim(),
                    e.Qualification.Trim(),
                    Trim(e.FieldOfStudy),
                    e.StartDate,
                    e.EndDate,
                    Trim(e.Grade),
                    Trim(e.Description)))
                .ToList(),
            Projects = request.Projects
                .Where(p => NotBlank(p.Name))
                .Select(p => new ProfileProject(p.Name.Trim(), Trim(p.Description), Trim(p.Url), p.CompletedOn))
                .ToList(),
            Certifications = request.Certifications
                .Where(c => NotBlank(c.Name))
                .Select(c => new ProfileCertification(c.Name.Trim(), Trim(c.Issuer), c.Year))
                .ToList(),
            Languages = request.Languages
                .Where(l => NotBlank(l.Name))
                .Select(l => new ProfileLanguage(l.Name.Trim(), Trim(l.Level)))
                .ToList(),
            Links = request.Links
                .Where(l => NotBlank(l.Label) && NotBlank(l.Url))
                .Select(l => new ProfileLink(l.Label.Trim(), l.Url.Trim()))
                .ToList(),
            DeclaredSkills = request.DeclaredSkills
                .Where(s => NotBlank(s.ConceptKey))
                .Select(s => new DeclaredSkill(s.ConceptKey.Trim(), ParseLevel(s.Level), s.Years))
                .ToList(),
        };

    public static ProfileResponse ToResponse(
        this CandidateProfile profile,
        IReadOnlyList<ConceptAssertion> extracted,
        DateTimeOffset? extractedAtUtc)
    {
        var graph = ConceptGraph.Default;

        return new ProfileResponse
        {
            FullName = profile.FullName,
            Headline = profile.Headline,
            Email = profile.Email,
            Phone = profile.Phone,
            Summary = profile.Summary,
            LocationCity = profile.LocationCity,
            LocationCountry = profile.LocationCountry,
            WillingToRelocate = profile.WillingToRelocate,
            PreferredArrangement = profile.PreferredArrangement.ToString(),
            MaxDaysInOffice = profile.MaxDaysInOffice,
            MinimumSalary = profile.MinimumSalary,
            SalaryCurrency = profile.SalaryCurrency,
            JobTypes = profile.JobTypes,
            YearsExperience = profile.YearsExperience,
            Seniority = profile.Seniority.ToString(),
            UpdatedUtc = profile.UpdatedUtc,
            ExtractedAtUtc = extractedAtUtc,
            Experiences = profile.Experiences
                .Select(e => new ExperienceRequest
                {
                    Company = e.Company,
                    Title = e.Title,
                    StartDate = e.StartDate,
                    EndDate = e.EndDate,
                    LocationCity = e.LocationCity,
                    LocationCountry = e.LocationCountry,
                    Description = e.Description,
                })
                .ToList(),
            Education = profile.Education
                .Select(e => new EducationRequest
                {
                    Institution = e.Institution,
                    Qualification = e.Qualification,
                    FieldOfStudy = e.FieldOfStudy,
                    StartDate = e.StartDate,
                    EndDate = e.EndDate,
                    Grade = e.Grade,
                    Description = e.Description,
                })
                .ToList(),
            Projects = profile.Projects
                .Select(p => new ProjectRequest
                {
                    Name = p.Name,
                    Description = p.Description,
                    Url = p.Url,
                    CompletedOn = p.CompletedOn,
                })
                .ToList(),
            Certifications = profile.Certifications
                .Select(c => new CertificationRequest { Name = c.Name, Issuer = c.Issuer, Year = c.Year })
                .ToList(),
            Languages = profile.Languages
                .Select(l => new LanguageRequest { Name = l.Name, Level = l.Level })
                .ToList(),
            Links = profile.Links
                .Select(l => new LinkRequest { Label = l.Label, Url = l.Url })
                .ToList(),
            DeclaredSkills = profile.DeclaredSkills
                .Select(s => new DeclaredSkillRequest
                {
                    ConceptKey = s.ConceptKey,
                    Level = s.Polarity.ToString(),
                    Years = s.Years,
                })
                .ToList(),
            ExtractedSkills = extracted
                .Where(a => a.Source == AssertionSource.Model)
                .Select(a => new ExtractedSkillResponse
                {
                    ConceptKey = a.ConceptKey,
                    Label = graph.TryGet(a.ConceptKey, out var concept) ? concept.Label : a.ConceptKey,
                    Level = a.Polarity.ToString(),
                    Years = a.YearsMin,
                    Evidence = a.EvidenceText,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// A named enum value, defaulting to the zero member.
    /// </summary>
    /// <remarks>
    /// Lenient here, unlike the posting search filters which reject an unknown value outright.
    /// The difference is what a wrong answer costs: a mistyped search filter silently returns
    /// the wrong postings and gets believed, where a mistyped preference falls back to "no
    /// preference" - which is both visible in the response and the neutral outcome.
    /// </remarks>
    private static T Parse<T>(string? value) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : default;

    /// <summary>
    /// A declared skill's level, clamped to the supply half.
    /// </summary>
    /// <remarks>
    /// A demand value arriving here means a client sent the wrong half of the enum. Clamping
    /// rather than accepting matters: <c>Required</c> is 3 and <c>Expert</c> is 13, so storing
    /// it would compare as weaker than every genuine claim and quietly deflate the match.
    /// </remarks>
    private static AssertionPolarity ParseLevel(string? value)
        => Enum.TryParse<AssertionPolarity>(value, ignoreCase: true, out var parsed)
            && parsed is AssertionPolarity.Familiar or AssertionPolarity.Proficient or AssertionPolarity.Expert
                ? parsed
                : AssertionPolarity.Proficient;

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
