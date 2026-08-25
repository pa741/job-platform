using System.Security.Cryptography;
using System.Text;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Profiles;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// A profile and everything derived from it, in one read.
/// </summary>
/// <param name="Id">
/// The internal key. Needed by the match and application paths, which take a profile id the
/// caller has already proved is theirs - this is where that proof comes from.
/// </param>
/// <param name="Profile">The record as the candidate filled it in.</param>
/// <param name="Extracted">
/// What the model found in their prose. Kept apart from the declared skills on
/// <see cref="Profile"/>, because what somebody said about themselves and what was inferred
/// about them are different claims and a person is entitled to see which is which.
/// </param>
/// <param name="ExtractedAtUtc">Null until the extractor has read this profile.</param>
public sealed record ProfileView(
    long Id,
    CandidateProfile Profile,
    IReadOnlyList<ConceptAssertion> Extracted,
    DateTimeOffset? ExtractedAtUtc);

/// <summary>
/// Reads and writes one person's profile, and only ever their own.
/// </summary>
/// <remarks>
/// <b>Every method takes a subject id and no method takes a profile id.</b> That is the
/// authorisation boundary, expressed as a type rather than as a rule somebody has to remember:
/// there is no overload that can be handed an id from a route parameter, so there is no way to
/// write an endpoint that reads a stranger's employment history by mistake. The internal id
/// exists because the child tables need a foreign key, and it never leaves this class.
///
/// The save is a replace, not a merge. A profile is a form: the client sends the whole thing
/// and the whole thing is what is stored, because a partial update has no way to express
/// "delete the third job" - which is a thing people do, and which a merge would silently
/// refuse. Concept rows survive the replace, since they are derived from the text rather than
/// submitted with it.
/// </remarks>
public sealed class CandidateProfileRepository(JobsDbContext db)
{
    /// <summary>
    /// The whole profile and everything derived from it, or null where none exists.
    /// </summary>
    /// <remarks>
    /// One method rather than four, because every caller needs all of it and this database is
    /// billed by wall-clock time online: fetching the profile, then its id, then its assertions,
    /// then its extraction timestamp is four wakeups for one page load. The
    /// <see cref="ProfileView"/> is what the endpoints hand straight to their mapping.
    ///
    /// Split queries rather than one <c>Include</c> chain. Seven collections on a single join
    /// multiply into a cartesian product - eight roles by five projects by four links is 160
    /// rows carrying eight copies of every description - and those descriptions are unbounded
    /// text. Several small queries beat one enormous one whenever the collections are
    /// independent, which these are.
    /// </remarks>
    public async Task<ProfileView?> GetAsync(string subjectId, CancellationToken ct = default)
    {
        var entity = await LoadAsync(subjectId, tracking: false, ct);

        return entity is null ? null : await ViewAsync(entity, ct);
    }

    /// <summary>The internal id, for the paths that need a foreign key. Null if no profile exists.</summary>
    public Task<long?> GetIdAsync(string subjectId, CancellationToken ct = default)
        => db.CandidateProfiles
            .AsNoTracking()
            .Where(p => p.SubjectId == subjectId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Stores the submitted form, replacing what was there.
    /// </summary>
    /// <returns>
    /// The stored profile, and whether the text the extractor reads actually changed. A caller
    /// that queues extraction should do so only when it did - otherwise correcting a phone
    /// number costs a model call and invalidates every match already scored.
    /// </returns>
    public async Task<(ProfileView View, bool TextChanged)> SaveAsync(
        CandidateProfile profile, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(time);

        var now = time.GetUtcNow();
        var entity = await LoadAsync(profile.SubjectId, tracking: true, ct);

        if (entity is null)
        {
            entity = new CandidateProfileEntity
            {
                SubjectId = profile.SubjectId,
                CreatedUtc = now,
            };

            db.CandidateProfiles.Add(entity);
        }
        else
        {
            // Cascade delete handles this at the database, but only for rows the context is
            // not already tracking. Clearing the collections is what makes EF issue the
            // deletes rather than trying to null out a non-nullable foreign key.
            entity.Experiences.Clear();
            entity.Education.Clear();
            entity.Projects.Clear();
            entity.Certifications.Clear();
            entity.Languages.Clear();
            entity.Links.Clear();
            entity.JobTypes.Clear();
        }

        var previousHash = entity.ExtractionInputHash;

        Apply(entity, profile, now);

        var textChanged = !string.Equals(entity.ExtractionInputHash, previousHash, StringComparison.Ordinal);

        await db.SaveChangesAsync(ct);

        await ReplaceDeclaredAsync(entity.Id, profile.DeclaredSkills, ct);

        return (await ViewAsync(entity, ct), textChanged);
    }

    /// <summary>The profile plus its derived rows, in two queries.</summary>
    private async Task<ProfileView> ViewAsync(CandidateProfileEntity entity, CancellationToken ct)
    {
        var assertions = await GetAssertionsAsync(entity.Id, ct);

        var declared = assertions
            .Where(a => a.Source == AssertionSource.Board)
            .Select(a => new DeclaredSkill(a.ConceptKey, a.Polarity, a.YearsMin))
            .ToList();

        return new ProfileView(
            entity.Id,
            Map(entity, declared),
            assertions.Where(a => a.Source == AssertionSource.Model).ToList(),
            entity.ExtractedAtUtc);
    }

    /// <summary>
    /// Records what the extractor found in the candidate's prose.
    /// </summary>
    /// <remarks>
    /// Only the <see cref="AssertionSource.Model"/> rows are replaced, so the skills the
    /// candidate declared on the form survive a re-extraction untouched. That is the same rule
    /// the posting side follows for board-supplied tags, and it exists for the same reason:
    /// these are different evidence produced by a different pass, and neither has any business
    /// overwriting the other.
    /// </remarks>
    public async Task ApplyExtractionAsync(
        long profileId,
        DocumentExtraction extraction,
        string inputHash,
        DateTimeOffset extractedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        await db.ProfileConcepts
            .Where(c => c.ProfileId == profileId && c.Source == AssertionSource.Model)
            .ExecuteDeleteAsync(ct);

        await db.ProfileMentions
            .Where(m => m.ProfileId == profileId && m.Reason == MentionReason.UnknownModelSkill)
            .ExecuteDeleteAsync(ct);

        var conceptIds = await db.Concepts
            .Select(c => new { c.ConceptKey, c.Id })
            .ToDictionaryAsync(c => c.ConceptKey, c => c.Id, StringComparer.Ordinal, ct);

        foreach (var assertion in extraction.Concepts)
        {
            if (!conceptIds.TryGetValue(assertion.ConceptKey, out var conceptId))
            {
                continue;
            }

            db.ProfileConcepts.Add(new ProfileConceptEntity
            {
                ProfileId = profileId,
                ConceptId = conceptId,
                Source = AssertionSource.Model,

                // The extractor speaks the demand half - it is the same prompt that reads
                // adverts. Translating here rather than in the prompt keeps one extraction
                // path for both document kinds, which is the whole point of the shared
                // contract; the alternative is a second prompt that drifts from the first.
                Polarity = ToSupply(assertion.Polarity),
                YearsMin = assertion.YearsMin,
                YearsMax = assertion.YearsMax,
                EvidenceText = assertion.EvidenceText,
                Confidence = assertion.Confidence,
                ResolverVersion = extraction.Version,
            });
        }

        foreach (var mention in extraction.Mentions.DistinctBy(m => m.SurfaceForm, StringComparer.OrdinalIgnoreCase))
        {
            db.ProfileMentions.Add(new ProfileMentionEntity
            {
                ProfileId = profileId,
                SurfaceForm = mention.SurfaceForm,
                Reason = mention.Reason,
                Occurrences = mention.Occurrences,
                ResolverVersion = extraction.Version,
            });
        }

        await db.CandidateProfiles
            .Where(p => p.Id == profileId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(p => p.ExtractorVersion, extraction.Version)
                    .SetProperty(p => p.ExtractionModel, extraction.Model)
                    .SetProperty(p => p.ExtractionPayloadJson, extraction.PayloadJson)
                    .SetProperty(p => p.ExtractedAtUtc, (DateTimeOffset?)extractedAtUtc)
                    .SetProperty(p => p.ExtractionInputHash, inputHash),
                ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Everything the candidate holds, declared and extracted, as one set of assertions.
    /// </summary>
    /// <remarks>
    /// The supply half of the match join, and deliberately the same
    /// <see cref="ConceptAssertion"/> the posting side produces - so <c>MatchScorer</c> takes
    /// two lists of one type rather than knowing which side is which.
    /// </remarks>
    public async Task<IReadOnlyList<ConceptAssertion>> GetAssertionsAsync(
        long profileId, CancellationToken ct = default)
        => await db.ProfileConcepts
            .AsNoTracking()
            .Where(c => c.ProfileId == profileId)
            .Select(c => new ConceptAssertion(
                c.Concept!.ConceptKey,
                c.Source,
                c.Polarity,
                c.YearsMin,
                c.YearsMax,
                c.EvidenceText,
                c.Confidence))
            .ToListAsync(ct);

    /// <summary>
    /// Profiles whose text has changed since the extractor last read it, or that it never has.
    /// </summary>
    /// <remarks>
    /// Compared on the hash rather than on a timestamp. A profile is saved repeatedly while
    /// someone edits it and most of those saves change nothing the extractor would read
    /// differently; a timestamp would re-extract on every one of them.
    /// </remarks>
    public Task<List<long>> GetStaleExtractionIdsAsync(int limit, CancellationToken ct = default)
        => db.CandidateProfiles
            .AsNoTracking()
            .Where(p => p.ExtractedAtUtc == null
                || p.ExtractorVersion != DocumentExtraction.CurrentVersion)
            .OrderBy(p => p.UpdatedUtc)
            .Select(p => p.Id)
            .Take(limit)
            .ToListAsync(ct);

    /// <summary>
    /// The supply polarity for what the extractor read as a demand polarity.
    /// </summary>
    /// <remarks>
    /// The two halves of <see cref="AssertionPolarity"/> exist precisely so this conversion has
    /// to be written down rather than happening by accident. "Required" in a profile means the
    /// candidate wrote about the skill as central to their work, which is Expert; "mentioned"
    /// is a passing reference, which is Familiar.
    /// </remarks>
    private static AssertionPolarity ToSupply(AssertionPolarity demand) => demand switch
    {
        AssertionPolarity.Required => AssertionPolarity.Expert,
        AssertionPolarity.Preferred => AssertionPolarity.Proficient,
        AssertionPolarity.Mentioned => AssertionPolarity.Familiar,

        // Already a supply value, or genuinely unspecified. Passed through rather than
        // defaulted, so a value that skipped this mapping stays visible in the data.
        _ => demand,
    };

    private async Task<CandidateProfileEntity?> LoadAsync(
        string subjectId, bool tracking, CancellationToken ct)
    {
        var query = tracking
            ? db.CandidateProfiles.AsTracking()
            : db.CandidateProfiles.AsNoTracking();

        return await query
            .Include(p => p.Experiences)
            .Include(p => p.Education)
            .Include(p => p.Projects)
            .Include(p => p.Certifications)
            .Include(p => p.Languages)
            .Include(p => p.Links)
            .Include(p => p.JobTypes)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.SubjectId == subjectId, ct);
    }

    /// <summary>
    /// Rewrites the declared skills, rejecting keys the vocabulary does not know.
    /// </summary>
    /// <remarks>
    /// An unknown key becomes a mention rather than an error, which is the same treatment a
    /// posting's unresolvable surface form gets. Failing the save instead would mean a
    /// candidate cannot record their profile because they named a technology this system has
    /// not caught up with yet - and the mention log is exactly how it catches up.
    /// </remarks>
    private async Task ReplaceDeclaredAsync(
        long profileId, IReadOnlyList<DeclaredSkill> declared, CancellationToken ct)
    {
        await db.ProfileConcepts
            .Where(c => c.ProfileId == profileId && c.Source == AssertionSource.Board)
            .ExecuteDeleteAsync(ct);

        await db.ProfileMentions
            .Where(m => m.ProfileId == profileId && m.Reason == MentionReason.UnknownBoardSkill)
            .ExecuteDeleteAsync(ct);

        if (declared.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var conceptIds = await db.Concepts
            .Select(c => new { c.ConceptKey, c.Id })
            .ToDictionaryAsync(c => c.ConceptKey, c => c.Id, StringComparer.Ordinal, ct);

        var seen = new HashSet<int>();

        foreach (var skill in declared.DistinctBy(s => s.ConceptKey, StringComparer.Ordinal))
        {
            if (!conceptIds.TryGetValue(skill.ConceptKey, out var conceptId))
            {
                db.ProfileMentions.Add(new ProfileMentionEntity
                {
                    ProfileId = profileId,
                    SurfaceForm = Truncate(skill.ConceptKey, 120),
                    Reason = MentionReason.UnknownBoardSkill,
                    Occurrences = 1,
                    ResolverVersion = ConceptGraph.Default.Version,
                });

                continue;
            }

            if (!seen.Add(conceptId))
            {
                continue;
            }

            db.ProfileConcepts.Add(new ProfileConceptEntity
            {
                ProfileId = profileId,
                ConceptId = conceptId,
                Source = AssertionSource.Board,

                // Anything from the demand half is a client sending the wrong enum. Clamped to
                // Proficient rather than stored, because a Required sitting in a supply column
                // would compare as larger than Expert and quietly inflate every match.
                Polarity = skill.Polarity is AssertionPolarity.Familiar
                    or AssertionPolarity.Proficient
                    or AssertionPolarity.Expert
                    ? skill.Polarity
                    : AssertionPolarity.Proficient,
                YearsMin = skill.Years,
                ResolverVersion = ConceptGraph.Default.Version,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Apply(CandidateProfileEntity entity, CandidateProfile profile, DateTimeOffset now)
    {
        entity.FullName = profile.FullName;
        entity.Headline = profile.Headline;
        entity.Email = profile.Email;
        entity.Phone = profile.Phone;
        entity.Summary = profile.Summary;
        entity.LocationCity = profile.LocationCity;
        entity.LocationCountry = profile.LocationCountry;
        entity.WillingToRelocate = profile.WillingToRelocate;
        entity.PreferredArrangement = profile.PreferredArrangement;
        entity.MaxDaysInOffice = profile.MaxDaysInOffice;
        entity.MinimumSalary = profile.MinimumSalary;
        entity.SalaryCurrency = profile.SalaryCurrency;
        entity.YearsExperience = profile.YearsExperience;
        entity.Seniority = profile.Seniority;
        entity.UpdatedUtc = now;

        var ordinal = 0;

        foreach (var experience in profile.Experiences)
        {
            entity.Experiences.Add(new ProfileExperienceEntity
            {
                Ordinal = ordinal++,
                Company = experience.Company,
                Title = experience.Title,
                StartDate = experience.StartDate,
                EndDate = experience.EndDate,
                LocationCity = experience.LocationCity,
                LocationCountry = experience.LocationCountry,
                Description = experience.Description,
            });
        }

        ordinal = 0;

        foreach (var education in profile.Education)
        {
            entity.Education.Add(new ProfileEducationEntity
            {
                Ordinal = ordinal++,
                Institution = education.Institution,
                Qualification = education.Qualification,
                FieldOfStudy = education.FieldOfStudy,
                StartDate = education.StartDate,
                EndDate = education.EndDate,
                Grade = education.Grade,
                Description = education.Description,
            });
        }

        ordinal = 0;

        foreach (var project in profile.Projects)
        {
            entity.Projects.Add(new ProfileProjectEntity
            {
                Ordinal = ordinal++,
                Name = project.Name,
                Description = project.Description,
                Url = project.Url,
                CompletedOn = project.CompletedOn,
            });
        }

        ordinal = 0;

        foreach (var certification in profile.Certifications)
        {
            entity.Certifications.Add(new ProfileCertificationEntity
            {
                Ordinal = ordinal++,
                Name = certification.Name,
                Issuer = certification.Issuer,
                Year = certification.Year,
            });
        }

        foreach (var language in profile.Languages.DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            entity.Languages.Add(new ProfileLanguageEntity
            {
                Name = language.Name,
                Level = language.Level,
            });
        }

        foreach (var link in profile.Links.DistinctBy(l => l.Label, StringComparer.OrdinalIgnoreCase))
        {
            entity.Links.Add(new ProfileLinkEntity
            {
                Label = link.Label,
                Url = link.Url,
            });
        }

        foreach (var jobType in profile.JobTypes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            entity.JobTypes.Add(new ProfileJobTypeEntity { JobType = jobType });
        }

        // Computed from the composed document rather than from the whole record, so that a
        // change to a phone number or a link does not read as a change to what the extractor
        // would find. It is the text that decides, and this is the text.
        entity.ExtractionInputHash = Hash(profile.ToDocument());
    }

    private static CandidateProfile Map(CandidateProfileEntity entity, List<DeclaredSkill> declared)
        => new()
        {
            SubjectId = entity.SubjectId,
            FullName = entity.FullName,
            Headline = entity.Headline,
            Email = entity.Email,
            Phone = entity.Phone,
            Summary = entity.Summary,
            LocationCity = entity.LocationCity,
            LocationCountry = entity.LocationCountry,
            WillingToRelocate = entity.WillingToRelocate,
            PreferredArrangement = entity.PreferredArrangement,
            MaxDaysInOffice = entity.MaxDaysInOffice,
            MinimumSalary = entity.MinimumSalary,
            SalaryCurrency = entity.SalaryCurrency,
            YearsExperience = entity.YearsExperience,
            Seniority = entity.Seniority,
            UpdatedUtc = entity.UpdatedUtc,
            JobTypes = entity.JobTypes.Select(j => j.JobType).ToList(),
            DeclaredSkills = declared,
            Experiences = entity.Experiences
                .OrderBy(e => e.Ordinal)
                .Select(e => new ProfileExperience(
                    e.Company, e.Title, e.StartDate, e.EndDate,
                    e.Description, e.LocationCity, e.LocationCountry))
                .ToList(),
            Education = entity.Education
                .OrderBy(e => e.Ordinal)
                .Select(e => new ProfileEducation(
                    e.Institution, e.Qualification, e.FieldOfStudy,
                    e.StartDate, e.EndDate, e.Grade, e.Description))
                .ToList(),
            Projects = entity.Projects
                .OrderBy(p => p.Ordinal)
                .Select(p => new ProfileProject(p.Name, p.Description, p.Url, p.CompletedOn))
                .ToList(),
            Certifications = entity.Certifications
                .OrderBy(c => c.Ordinal)
                .Select(c => new ProfileCertification(c.Name, c.Issuer, c.Year))
                .ToList(),
            Languages = entity.Languages
                .Select(l => new ProfileLanguage(l.Name, l.Level))
                .ToList(),
            Links = entity.Links
                .Select(l => new ProfileLink(l.Label, l.Url))
                .ToList(),
        };

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static string Hash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
