using JobPlatform.Core.Profiles;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The allowlist that stands in for a profile endpoint that does not exist.
/// </summary>
/// <remarks>
/// The agent surface refuses <c>get_profile</c> because a tool result is transcript content
/// wherever the client runs, and this is the substitute: one answer at a time, from a set this
/// repository defines. So the tests worth having are about the boundary rather than about the
/// values - what is in the list, what is refused, and that nothing resolves by accident.
/// </remarks>
public sealed class FormFieldCatalogTests
{
    private static CandidateProfile Profile() => new()
    {
        SubjectId = "11111111-1111-1111-1111-111111111111",
        FullName = "  Alex Candidate  ",
        Email = "alex@example.invalid",
        Phone = null,
        LocationCity = "London",
        LocationCountry = "United Kingdom",
        Headline = "Backend engineer",
        YearsExperience = 9,
        Experiences =
        [
            new ProfileExperience("Older Ltd", "Junior Engineer",
                new DateOnly(2016, 1, 1), new DateOnly(2019, 6, 1)),
            new ProfileExperience("Current Ltd", "Senior Engineer",
                new DateOnly(2019, 7, 1)),
        ],
        Links =
        [
            new ProfileLink("LinkedIn", "https://www.linkedin.com/in/example"),
            new ProfileLink("Personal site", "https://example.invalid"),
        ],
    };

    [Fact]
    public void An_unknown_name_is_refused_rather_than_resolved()
    {
        // The catalogue decides what exists, not the caller. Nothing here reflects over the
        // profile or matches on substrings, so a name outside the list cannot resolve by
        // accident - which is the property that makes the allowlist an allowlist.
        Assert.False(FormFieldCatalog.TryGet("date_of_birth", out _));
        Assert.False(FormFieldCatalog.TryGet("salary", out _));
        Assert.False(FormFieldCatalog.TryGet("name", out _));
        Assert.False(FormFieldCatalog.TryGet(null, out _));
        Assert.False(FormFieldCatalog.TryGet("   ", out _));
    }

    [Fact]
    public void A_known_name_resolves_whatever_case_it_is_written_in()
    {
        // A model writing Full_Name means full_name. Refusing on case would be a refusal about
        // spelling dressed up as one about disclosure.
        Assert.True(FormFieldCatalog.TryGet("full_name", out var lower));
        Assert.True(FormFieldCatalog.TryGet("Full_Name", out var mixed));
        Assert.True(FormFieldCatalog.TryGet("  email  ", out var padded));

        Assert.Equal("full_name", lower.Name);
        Assert.Equal("full_name", mixed.Name);
        Assert.Equal("email", padded.Name);
    }

    /// <summary>
    /// The whole list, pinned.
    /// </summary>
    /// <remarks>
    /// <b>What is absent is the point.</b> There is no date of birth, no nationality, no address
    /// beyond a city, no salary expectation and no referee. A form will genuinely ask for some of
    /// those; the answer is that a person types them, because a field an agent cannot fill is a
    /// field an agent cannot get wrong on somebody's behalf. Adding one should be a red build and
    /// then a deliberate edit, not a quiet widening.
    /// </remarks>
    [Fact]
    public void The_allowlist_is_exactly_this()
    {
        Assert.Equal(
            [
                "full_name", "email", "phone", "location_city", "location_country", "headline",
                "years_experience", "current_title", "current_employer", "linkedin_url", "github_url",
            ],
            FormFieldCatalog.Names);
    }

    [Fact]
    public void Every_field_describes_itself_and_the_names_are_unique()
    {
        // The description is what a model reads to choose between them, and a duplicate name
        // would make one entry unreachable through TryGet with nothing saying so.
        Assert.All(FormFieldCatalog.All, field => Assert.False(string.IsNullOrWhiteSpace(field.Description)));
        Assert.Equal(FormFieldCatalog.All.Count, FormFieldCatalog.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void An_absent_answer_is_null_rather_than_an_empty_string()
    {
        // The tool turns null into "ask the candidate". An empty string would be typed into a
        // form as a blank answer, which reads to an employer as a fact.
        Assert.True(FormFieldCatalog.TryGet("phone", out var phone));
        Assert.Null(phone.Read(Profile()));

        Assert.True(FormFieldCatalog.TryGet("github_url", out var github));
        Assert.Null(github.Read(Profile()));
    }

    [Fact]
    public void Values_are_read_from_the_profile_and_trimmed()
    {
        var profile = Profile();

        Assert.True(FormFieldCatalog.TryGet("full_name", out var name));
        Assert.Equal("Alex Candidate", name.Read(profile));

        Assert.True(FormFieldCatalog.TryGet("linkedin_url", out var linkedin));
        Assert.Equal("https://www.linkedin.com/in/example", linkedin.Read(profile));
    }

    [Fact]
    public void Years_of_experience_is_the_stated_number_and_never_a_sum_over_the_roles()
    {
        // CandidateProfile.YearsExperience is asked of the person deliberately: overlapping
        // roles, career breaks and freelance work all make a sum wrong. The roles on this
        // fixture span about ten years and the stated figure is nine, so a catalogue that
        // derived it instead would answer differently here.
        Assert.True(FormFieldCatalog.TryGet("years_experience", out var years));
        Assert.Equal("9", years.Read(Profile()));

        Assert.Null(years.Read(Profile() with { YearsExperience = null }));
    }

    [Fact]
    public void The_current_role_is_the_one_with_no_end_date()
    {
        // "Current title" has to mean what somebody would say out loud, so an open-ended role
        // beats a finished one even where the finished one started later.
        Assert.True(FormFieldCatalog.TryGet("current_title", out var title));
        Assert.True(FormFieldCatalog.TryGet("current_employer", out var employer));

        Assert.Equal("Senior Engineer", title.Read(Profile()));
        Assert.Equal("Current Ltd", employer.Read(Profile()));
    }

    [Fact]
    public void A_profile_with_nothing_in_it_answers_null_everywhere_rather_than_throwing()
    {
        // The tools call this before knowing whether the profile is filled in, and a half-empty
        // profile is the ordinary state of a new one.
        var empty = new CandidateProfile { SubjectId = "22222222-2222-2222-2222-222222222222" };

        Assert.All(FormFieldCatalog.All, field => Assert.Null(field.Read(empty)));
    }
}
