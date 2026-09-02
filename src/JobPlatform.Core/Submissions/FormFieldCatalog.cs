using System.Globalization;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Profiles;

namespace JobPlatform.Core.Submissions;

/// <summary>One answer a form might ask for, and how to get it from the profile.</summary>
/// <param name="Name">The name a caller asks by. Lower snake case, stable, part of the contract.</param>
/// <param name="Description">What it means, for a client choosing between them.</param>
/// <param name="Read">Produces the answer, or null where the profile does not carry one.</param>
public sealed record FormField(string Name, string Description, Func<CandidateProfile, string?> Read);

/// <summary>One field of one entry in a repeated group - the part after the dot.</summary>
/// <param name="Name">The member name, lower snake case, exactly as a singular field's is.</param>
/// <param name="Description">What it means. The group and the index are appended when it is expanded.</param>
/// <param name="Read">
/// Produces the answer for the entry at that index, or null where the candidate has no entry
/// there. <b>Null for "there is no fourth job" and null for "the record does not say when it
/// ended" are deliberately the same answer</b>, because they lead to the same place: the field
/// is left for a person rather than filled in with a guess.
/// </param>
public sealed record FormFieldMember(string Name, string Description, Func<CandidateProfile, int, string?> Read);

/// <summary>
/// A list on the profile that a form asks for one entry at a time.
/// </summary>
/// <remarks>
/// <b>Declared once and expanded into named fields, which is what keeps the allowlist an
/// allowlist.</b> A Workday-style form asks for employer, title and dates five rows down the
/// page; the alternative to naming those rows is handing over an employment history as an object
/// and letting the caller pick, which is the whole-profile disclosure this catalogue exists
/// instead of. Here the group is the readable declaration and
/// <see cref="FormFieldCatalog.All"/> is its expansion, so the two cannot drift: adding a member
/// is still one line in a diff, and it still adds a bounded, named, individually logged set of
/// things that can leave the system.
/// </remarks>
/// <param name="Name">The group name, e.g. <c>work_history</c>.</param>
/// <param name="Description">What one entry of it is.</param>
/// <param name="MaxEntries">
/// How many entries are addressable. The bound is load-bearing rather than defensive - see the
/// remarks on <see cref="FormFieldCatalog"/>.
/// </param>
/// <param name="Count">How many entries the profile holds, before the bound is applied.</param>
/// <param name="Members">The fields one entry answers.</param>
public sealed record FormFieldGroup(
    string Name,
    string Description,
    int MaxEntries,
    Func<CandidateProfile, int> Count,
    IReadOnlyList<FormFieldMember> Members)
{
    /// <summary>The field a client reads to find out how many entries it may ask for.</summary>
    public string CountName => $"{Name}.count";

    /// <summary>The name of one member of one entry. The naming scheme, in one place.</summary>
    public string NameOf(int index, string member) => $"{Name}[{index}].{member}";

    /// <summary>
    /// The group as ordinary named fields, count first and then entry by entry.
    /// </summary>
    /// <remarks>
    /// <b>The count is clamped to <see cref="MaxEntries"/> on purpose.</b> A client is expected
    /// to read it and then walk the indices below it, so reporting a number it cannot address
    /// would send it into refusals it has no way to distinguish from a misspelling - and this
    /// catalogue's refusal deliberately says nothing about why. Clamping makes that loop correct
    /// by construction; the entries past the bound are on the generated CV, which is a document
    /// a person reads rather than a field an agent fills.
    ///
    /// Entry fields are emitted together, in index order, because that is the order a form is
    /// filled in - the listing is read by a model deciding what to ask for next.
    /// </remarks>
    public IEnumerable<FormField> Expand()
    {
        yield return new FormField(
            CountName,
            $"How many {Name} entries may be asked for, at most {MaxEntries}. "
                + "Indices run from 0 to one less than this, most recent first.",
            profile => Math.Min(Count(profile), MaxEntries).ToString(CultureInfo.InvariantCulture));

        for (var index = 0; index < MaxEntries; index++)
        {
            // Copied per iteration because the closures below outlive the loop; capturing the
            // loop variable itself would give every field the last index.
            var at = index;

            foreach (var member in Members)
            {
                yield return new FormField(
                    NameOf(at, member.Name),
                    $"{member.Description} Entry {at} of {Name}, counting from the most recent.",
                    profile => member.Read(profile, at));
            }
        }
    }
}

/// <summary>
/// The fields a client may ask for, one at a time.
/// </summary>
/// <remarks>
/// <b>This exists because there is no <c>get_profile</c> and there must not be one.</b> A tool
/// result is transcript content wherever the client happens to run, and may be retained there.
/// The profile is employment history, contact details and salary expectations - the same data
/// <c>AiLedger:RecordPrompts</c> is off by default for, and the same data the OpenAI batch path
/// is fenced away from. Handing all of it over in one call to save round trips would undo both.
///
/// <b>The catalogue decides what exists, not the caller.</b> An unknown name is refused rather
/// than resolved against the profile by reflection or by string matching, so the set of things
/// that can ever leave this system is a list somebody can read in one screen. Adding to it is a
/// deliberate act with a diff.
///
/// <b>What is absent is as considered as what is present.</b> There is no date of birth, no
/// nationality, no address beyond a city, no salary expectation and no referee. The repeated
/// entries are the same judgement applied twice over: no grade, though
/// <see cref="ProfileEducation.Grade"/> is right there on the record, and no reason for leaving,
/// because nobody has to ask this system for either. Some of those a form will genuinely ask
/// for; the answer is that a person types them, because a field an agent cannot fill is a field
/// an agent cannot get wrong on somebody's behalf.
///
/// <b>Repeated entries are named, not composed.</b> The naming scheme is
/// <c>group[index].member</c> with <c>group.count</c> alongside it - <c>work_history[0].employer</c>,
/// <c>work_history.count</c> - and that is the whole of it. Returning an employment history as an
/// object instead would put the shape of the disclosure in the caller's hands: a field added to
/// the underlying record would start leaving the system with no diff here saying so, which is
/// exactly the property the second paragraph buys.
///
/// <b>The index is bounded, and the bound is what keeps that paragraph true.</b> An unbounded
/// repeat has no list: <see cref="Names"/> could not be printed, a refusal could not name the
/// allowed set, and "readable in one screen" would quietly become "readable in principle".
/// Five roles and three qualifications is past what any application form asks a person to type
/// row by row, and a longer history already leaves by a different and better-supervised door -
/// the generated CV in the submission pack, which somebody reads before it goes anywhere.
///
/// <b>Expanded once, at construction, and looked up as a dictionary.</b> Parsing
/// <c>work_history[7].employer</c> when it is asked for would put index arithmetic and a bounds
/// check on the disclosure path, and would make a refusal depend on getting them right. Expanding
/// up front means <see cref="TryGet"/> is the same dictionary hit it always was, and
/// <c>work_history[7].employer</c> is refused for precisely the reason <c>date_of_birth</c> is:
/// it is not in the list.
///
/// <b>Dates are served as the record holds them.</b> The CV path renders "Mar 2021", because
/// month precision is all a CV needs, and a client reading a date back off that prose has to
/// invent a day - it will invent the first, and type an invented fact into somebody's
/// application. These fields read <see cref="ProfileExperience.StartDate"/> itself, in ISO
/// order, so there is nothing to re-derive and no locale to guess at.
/// </remarks>
public static class FormFieldCatalog
{
    /// <summary>
    /// How many roles are addressable through <c>work_history</c>.
    /// </summary>
    /// <remarks>
    /// Five, because a form that asks for employment history field by field is asking for the
    /// recent part of it - the rest is what the attached CV is for - and because the bound has
    /// to be small enough that the expansion is still a list somebody reads rather than scrolls.
    /// </remarks>
    public const int MaxWorkHistoryEntries = 5;

    /// <summary>
    /// How many qualifications are addressable through <c>education</c>.
    /// </summary>
    /// <remarks>
    /// Three. Degrees are few and a form wanting more than three is asking for a transcript,
    /// which is a document rather than a set of fields.
    /// </remarks>
    public const int MaxEducationEntries = 3;

    /// <summary>The repeated groups, each declared once and expanded into <see cref="All"/>.</summary>
    public static readonly IReadOnlyList<FormFieldGroup> Groups =
    [
        new("work_history",
            "One role from the candidate's employment history.",
            MaxWorkHistoryEntries,
            profile => profile.Experiences.Count,
            [
                new("employer", "The employer, or the client for contract work.",
                    (profile, index) => Trimmed(RoleAt(profile, index)?.Company)),

                new("title", "The job title as held, not as normalised.",
                    (profile, index) => Trimmed(RoleAt(profile, index)?.Title)),

                new("start_date", "When the role started, as the record holds it: yyyy-MM-dd.",
                    (profile, index) => Iso(RoleAt(profile, index)?.StartDate)),

                new("end_date", "When the role ended, yyyy-MM-dd. Absent while the role is held.",
                    (profile, index) => Iso(RoleAt(profile, index)?.EndDate)),

                // A role with no end date is a role still held - the same rule current_title
                // answers by, so the two cannot contradict each other.
                new("current", "'true' where the candidate still holds this role, 'false' where they do not.",
                    (profile, index) => RoleAt(profile, index) switch
                    {
                        null => null,
                        { EndDate: null } => "true",
                        _ => "false",
                    }),

                new("description", "What they did there, in their own words.",
                    (profile, index) => Trimmed(RoleAt(profile, index)?.Description)),
            ]),

        new("education",
            "One qualification, held or in progress.",
            MaxEducationEntries,
            profile => profile.Education.Count,
            [
                new("institution", "The university, college or school.",
                    (profile, index) => Trimmed(StudyAt(profile, index)?.Institution)),

                new("qualification", "The qualification itself - 'BSc', 'MSc', 'Apprenticeship'.",
                    (profile, index) => Trimmed(StudyAt(profile, index)?.Qualification)),

                new("field", "What it was in, where the candidate said.",
                    (profile, index) => Trimmed(StudyAt(profile, index)?.FieldOfStudy)),

                new("start_date", "When it started, as the record holds it: yyyy-MM-dd.",
                    (profile, index) => Iso(StudyAt(profile, index)?.StartDate)),

                new("end_date", "When it finished, yyyy-MM-dd. Absent while it is in progress.",
                    (profile, index) => Iso(StudyAt(profile, index)?.EndDate)),
            ]),
    ];

    /// <summary>Every field, in the order a client should be shown them.</summary>
    public static readonly IReadOnlyList<FormField> All =
    [
        new("full_name", "The candidate's name as it should appear on an application.",
            profile => Trimmed(profile.FullName)),

        new("email", "Contact email address.",
            profile => Trimmed(profile.Email)),

        new("phone", "Contact telephone number.",
            profile => Trimmed(profile.Phone)),

        new("location_city", "The city the candidate is based in.",
            profile => Trimmed(profile.LocationCity)),

        new("location_country", "The country the candidate is based in.",
            profile => Trimmed(profile.LocationCountry)),

        new("headline", "One line describing what the candidate does.",
            profile => Trimmed(profile.Headline)),

        // Read, never derived. CandidateProfile.YearsExperience is asked of the person for a
        // documented reason - overlapping roles, career breaks and freelance work all make a sum
        // over Experiences wrong, and they know the number. Computing it here would be a second
        // answer to a question this system has already decided how to ask.
        new("years_experience", "Total years of professional experience, as the candidate stated it.",
            profile => profile.YearsExperience?.ToString(CultureInfo.InvariantCulture)),

        new("current_title", "The candidate's most recent job title.",
            profile => Trimmed(Latest(profile)?.Title)),

        new("current_employer", "The candidate's most recent employer.",
            profile => Trimmed(Latest(profile)?.Company)),

        new("linkedin_url", "The candidate's LinkedIn profile, where they listed one.",
            profile => Link(profile, "linkedin")),

        new("github_url", "The candidate's GitHub profile, where they listed one.",
            profile => Link(profile, "github")),

        // Joins the two above rather than arriving as links.portfolio next to links.linkedin: a
        // second spelling for a field that already has one is a second name to keep in step, and
        // a caller that finds both has to work out whether they can differ.
        new("portfolio_url", "The candidate's portfolio or personal site, where they listed one.",
            profile => Link(profile, "portfolio", "website", "personal site")),

        // Labels, not keys. skill.kubernetes is this system's identity for the concept and
        // "Kubernetes" is what somebody types into a form; the vocabulary already draws that
        // distinction, so reading it here keeps an internal identifier out of an employer's
        // form. A key the graph no longer knows is dropped rather than printed raw.
        new("skills", "The skills the candidate claimed outright, comma separated, in the order they claimed them.",
            profile => Joined(profile.DeclaredSkills.Select(Label))),

        .. Groups.SelectMany(group => group.Expand()),
    ];

    private static readonly Dictionary<string, FormField> ByName =
        All.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The names a client may ask for.</summary>
    public static IReadOnlyList<string> Names => [.. All.Select(entry => entry.Name)];

    /// <summary>Looks a field up, or false where the name is not one this system will answer.</summary>
    /// <remarks>
    /// Case-insensitive on the way in and exact on the way out: a model writing
    /// <c>full_name</c> or <c>Full_Name</c> means the same thing, and refusing on case would be
    /// a refusal about spelling rather than about disclosure.
    ///
    /// A repeated field is looked up here exactly as a singular one is, because the expansion
    /// already happened. There is no index to parse and therefore no index to get wrong.
    /// </remarks>
    public static bool TryGet(string? name, out FormField field)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return ByName.TryGetValue(name.Trim(), out field!);
        }

        field = null!;

        return false;
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The stored date, in ISO order. Never a month read back off the CV's prose.</summary>
    private static string? Iso(DateOnly? date)
        => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The roles, current ones first and most recently finished after them.</summary>
    /// <remarks>
    /// A role with no end date is current, and there may be more than one. Ordering by start date
    /// within that is what makes "current title" the answer somebody would give out loud.
    ///
    /// <b>The same ordering serves <c>current_title</c> and <c>work_history[0]</c></b>, which is
    /// why it is one function. Two orderings would let the catalogue name one employer as the
    /// current one and a different employer as the first row of the form.
    /// </remarks>
    private static IEnumerable<ProfileExperience> Roles(CandidateProfile profile)
        => profile.Experiences
            .OrderByDescending(role => role.EndDate is null)
            .ThenByDescending(role => role.EndDate)
            .ThenByDescending(role => role.StartDate);

    /// <summary>The most recent role, current ones first.</summary>
    private static ProfileExperience? Latest(CandidateProfile profile)
        => Roles(profile).FirstOrDefault();

    private static ProfileExperience? RoleAt(CandidateProfile profile, int index)
        => Roles(profile).ElementAtOrDefault(index);

    /// <summary>Qualifications, in progress first and most recently finished after them.</summary>
    /// <remarks>Read the same way roles are, because a form asks for both the same way.</remarks>
    private static ProfileEducation? StudyAt(CandidateProfile profile, int index)
        => profile.Education
            .OrderByDescending(study => study.EndDate is null)
            .ThenByDescending(study => study.EndDate)
            .ThenByDescending(study => study.StartDate)
            .ElementAtOrDefault(index);

    /// <summary>A link the candidate listed, matched on its label.</summary>
    /// <remarks>
    /// The labels are tried in order, so somebody who wrote both "Portfolio" and "Website" gets
    /// the one they meant. Listing the alternatives rather than pattern-matching on them is the
    /// same choice the catalogue makes everywhere else: a short list in a diff beats a rule
    /// nobody can enumerate.
    /// </remarks>
    private static string? Link(CandidateProfile profile, params string[] labels)
        => labels
            .SelectMany(label => profile.Links
                .Where(link => link.Label.Contains(label, StringComparison.OrdinalIgnoreCase)))
            .Select(link => Trimmed(link.Url))
            .FirstOrDefault(url => url is not null);

    /// <summary>The vocabulary's label for a claimed skill, or null where it no longer knows the key.</summary>
    private static string? Label(DeclaredSkill skill)
        => ConceptGraph.Default.TryGet(skill.ConceptKey, out var concept) ? concept.Label : null;

    /// <summary>One form field's worth of list, or null where there is nothing to put in it.</summary>
    private static string? Joined(IEnumerable<string?> values)
    {
        var joined = string.Join(
            ", ",
            values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));

        return joined.Length == 0 ? null : joined;
    }
}
