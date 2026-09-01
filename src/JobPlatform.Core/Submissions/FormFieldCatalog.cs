using System.Globalization;
using JobPlatform.Core.Profiles;

namespace JobPlatform.Core.Submissions;

/// <summary>One answer a form might ask for, and how to get it from the profile.</summary>
/// <param name="Name">The name a caller asks by. Lower snake case, stable, part of the contract.</param>
/// <param name="Description">What it means, for a client choosing between them.</param>
/// <param name="Read">Produces the answer, or null where the profile does not carry one.</param>
public sealed record FormField(string Name, string Description, Func<CandidateProfile, string?> Read);

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
/// nationality, no address beyond a city, no salary expectation and no referee. Some of those a
/// form will genuinely ask for; the answer is that a person types them, because a field an agent
/// cannot fill is a field an agent cannot get wrong on somebody's behalf.
/// </remarks>
public static class FormFieldCatalog
{
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

    /// <summary>The most recent role, current ones first.</summary>
    /// <remarks>
    /// A role with no end date is current, and there may be more than one. Ordering by start date
    /// within that is what makes "current title" the answer somebody would give out loud.
    /// </remarks>
    private static ProfileExperience? Latest(CandidateProfile profile)
        => profile.Experiences
            .OrderByDescending(role => role.EndDate is null)
            .ThenByDescending(role => role.EndDate)
            .ThenByDescending(role => role.StartDate)
            .FirstOrDefault();

    /// <summary>A link the candidate listed, matched on its label.</summary>
    private static string? Link(CandidateProfile profile, string label)
        => Trimmed(profile.Links
            .FirstOrDefault(link => link.Label.Contains(label, StringComparison.OrdinalIgnoreCase))
            ?.Url);
}
