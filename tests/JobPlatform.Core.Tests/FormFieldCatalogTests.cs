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
///
/// The repeated entries are the same tests one layer along. A form asks for employment history
/// row by row, so the catalogue answers row by row; what has to hold is that the rows are named,
/// that there is a last one, and that a client can find out how many it may ask for without
/// walking into refusals.
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
                new DateOnly(2016, 1, 1), new DateOnly(2019, 6, 1),
                Description: "  Maintained the billing service.  "),
            new ProfileExperience("Current Ltd", "Senior Engineer",
                new DateOnly(2019, 7, 15)),
        ],
        Education =
        [
            new ProfileEducation("Older College", "BTEC", "Computing",
                new DateOnly(2011, 9, 1), new DateOnly(2013, 6, 30), Grade: "Distinction"),
            new ProfileEducation("A University", "BSc", "Computer Science",
                new DateOnly(2013, 9, 1), new DateOnly(2016, 6, 30), Grade: "2:1"),
        ],
        DeclaredSkills =
        [
            new DeclaredSkill("skill.csharp"),
            new DeclaredSkill("skill.kubernetes"),
        ],
        Links =
        [
            new ProfileLink("LinkedIn", "https://www.linkedin.com/in/example"),
            new ProfileLink("Personal site", "https://example.invalid"),
        ],
    };

    private static string? Read(string name, CandidateProfile profile)
    {
        Assert.True(FormFieldCatalog.TryGet(name, out var field), $"'{name}' is not in the catalogue.");

        return field.Read(profile);
    }

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

        // A repeated group is not a thing you can ask for whole. There is no work_history and no
        // work_history[0] - only named members of it - because an entry returned as an object
        // would carry whatever the underlying record grows next, with no diff here saying so.
        Assert.False(FormFieldCatalog.TryGet("work_history", out _));
        Assert.False(FormFieldCatalog.TryGet("work_history[0]", out _));
        Assert.False(FormFieldCatalog.TryGet("education[0]", out _));

        // And a member nobody put in the group is refused exactly as date_of_birth is, even
        // where the record holds it: ProfileEducation carries a grade and this will not say it.
        Assert.False(FormFieldCatalog.TryGet("education[0].grade", out _));
        Assert.False(FormFieldCatalog.TryGet("work_history[0].salary", out _));
        Assert.False(FormFieldCatalog.TryGet("work_history[0].reason_for_leaving", out _));
    }

    [Fact]
    public void A_known_name_resolves_whatever_case_it_is_written_in()
    {
        // A model writing Full_Name means full_name. Refusing on case would be a refusal about
        // spelling dressed up as one about disclosure.
        Assert.True(FormFieldCatalog.TryGet("full_name", out var lower));
        Assert.True(FormFieldCatalog.TryGet("Full_Name", out var mixed));
        Assert.True(FormFieldCatalog.TryGet("  email  ", out var padded));
        Assert.True(FormFieldCatalog.TryGet("WORK_HISTORY[0].EMPLOYER", out var repeated));

        Assert.Equal("full_name", lower.Name);
        Assert.Equal("full_name", mixed.Name);
        Assert.Equal("email", padded.Name);
        Assert.Equal("work_history[0].employer", repeated.Name);
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
    ///
    /// The repeated names are written out here by scheme rather than one by one, and the scheme
    /// is spelled a second time rather than read off <see cref="FormFieldCatalog.Groups"/> - a
    /// test that asks the implementation what it produces cannot notice the implementation
    /// changing. Renaming <c>work_history[0].employer</c> is a contract change and belongs in a
    /// diff on this line.
    /// </remarks>
    [Fact]
    public void The_allowlist_is_exactly_this()
    {
        string[] singular =
        [
            "full_name", "email", "phone", "location_city", "location_country", "headline",
            "years_experience", "current_title", "current_employer", "linkedin_url", "github_url",
            "portfolio_url", "skills",
        ];

        (string Group, int Entries, string[] Members)[] repeated =
        [
            ("work_history", 5, ["employer", "title", "start_date", "end_date", "current", "description"]),
            ("education", 3, ["institution", "qualification", "field", "start_date", "end_date"]),
        ];

        List<string> expected = [.. singular];

        foreach (var (group, entries, members) in repeated)
        {
            expected.Add($"{group}.count");

            for (var index = 0; index < entries; index++)
            {
                expected.AddRange(members.Select(member => $"{group}[{index}].{member}"));
            }
        }

        Assert.Equal(expected, FormFieldCatalog.Names);
    }

    [Fact]
    public void The_repeated_groups_are_bounded_and_declared_in_one_place()
    {
        // The bound is what keeps the list above finite, so it is pinned rather than left to
        // whatever the expansion happens to do. Raising it widens the disclosure surface by a
        // whole entry and should read that way in a diff.
        Assert.Equal(["work_history", "education"], FormFieldCatalog.Groups.Select(group => group.Name));
        Assert.Equal([5, 3], FormFieldCatalog.Groups.Select(group => group.MaxEntries));
        Assert.Equal(FormFieldCatalog.MaxWorkHistoryEntries, FormFieldCatalog.Groups[0].MaxEntries);
        Assert.Equal(FormFieldCatalog.MaxEducationEntries, FormFieldCatalog.Groups[1].MaxEntries);
    }

    [Fact]
    public void Every_field_describes_itself_and_the_names_are_unique()
    {
        // The description is what a model reads to choose between them, and a duplicate name
        // would make one entry unreachable through TryGet with nothing saying so.
        Assert.All(FormFieldCatalog.All, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Description)));
        Assert.Equal(FormFieldCatalog.All.Count, FormFieldCatalog.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void An_absent_answer_is_null_rather_than_an_empty_string()
    {
        // The tool turns null into "ask the candidate". An empty string would be typed into a
        // form as a blank answer, which reads to an employer as a fact.
        Assert.Null(Read("phone", Profile()));
        Assert.Null(Read("github_url", Profile()));

        // The same answer for a row the candidate does not have and for a field they left
        // blank: both mean nobody should type anything there.
        Assert.Null(Read("work_history[0].description", Profile()));
        Assert.Null(Read("work_history[2].employer", Profile()));
        Assert.Null(Read("education[2].institution", Profile()));
    }

    [Fact]
    public void Values_are_read_from_the_profile_and_trimmed()
    {
        var profile = Profile();

        Assert.Equal("Alex Candidate", Read("full_name", profile));
        Assert.Equal("https://www.linkedin.com/in/example", Read("linkedin_url", profile));
        Assert.Equal("Maintained the billing service.", Read("work_history[1].description", profile));
    }

    [Fact]
    public void Years_of_experience_is_the_stated_number_and_never_a_sum_over_the_roles()
    {
        // CandidateProfile.YearsExperience is asked of the person deliberately: overlapping
        // roles, career breaks and freelance work all make a sum wrong. The roles on this
        // fixture span about ten years and the stated figure is nine, so a catalogue that
        // derived it instead would answer differently here.
        Assert.Equal("9", Read("years_experience", Profile()));
        Assert.Null(Read("years_experience", Profile() with { YearsExperience = null }));
    }

    [Fact]
    public void The_current_role_is_the_one_with_no_end_date()
    {
        // "Current title" has to mean what somebody would say out loud, so an open-ended role
        // beats a finished one even where the finished one started later.
        Assert.Equal("Senior Engineer", Read("current_title", Profile()));
        Assert.Equal("Current Ltd", Read("current_employer", Profile()));
    }

    [Fact]
    public void The_first_repeated_entry_is_the_role_current_employer_names()
    {
        // Two orderings would let this catalogue call one employer the current one and put a
        // different employer on the first row of the form. There is one ordering.
        var profile = Profile();

        Assert.Equal(Read("current_employer", profile), Read("work_history[0].employer", profile));
        Assert.Equal(Read("current_title", profile), Read("work_history[0].title", profile));
    }

    [Fact]
    public void Repeated_entries_are_ordered_most_recent_first()
    {
        var profile = Profile();

        Assert.Equal("Current Ltd", Read("work_history[0].employer", profile));
        Assert.Equal("Older Ltd", Read("work_history[1].employer", profile));

        // Education is read the same way, so a client filling both sections walks both in the
        // order the sections are printed on a form.
        Assert.Equal("A University", Read("education[0].institution", profile));
        Assert.Equal("BSc", Read("education[0].qualification", profile));
        Assert.Equal("Computer Science", Read("education[0].field", profile));
        Assert.Equal("Older College", Read("education[1].institution", profile));
    }

    [Fact]
    public void A_role_still_held_says_so_and_offers_no_end_date()
    {
        var profile = Profile();

        Assert.Equal("true", Read("work_history[0].current", profile));
        Assert.Null(Read("work_history[0].end_date", profile));

        Assert.Equal("false", Read("work_history[1].current", profile));
        Assert.Equal("2019-06-01", Read("work_history[1].end_date", profile));

        // A row nobody has is neither current nor not current.
        Assert.Null(Read("work_history[3].current", profile));
    }

    [Fact]
    public void Dates_are_the_stored_ones_rather_than_a_month_read_back_off_the_cv()
    {
        // The CV renders this role as "Jul 2019 - Present" because month precision is all a CV
        // needs. A client re-deriving a date from that prose has to invent a day and will invent
        // the first, and then type an invented fact into somebody's application form. The
        // fixture's start date is the 15th precisely so that mistake fails here.
        var profile = Profile();

        Assert.Equal("Jul 2019 - Present", profile.Experiences[1].Period());
        Assert.Equal("2019-07-15", Read("work_history[0].start_date", profile));

        Assert.Equal("2013-09-01", Read("education[0].start_date", profile));
        Assert.Equal("2016-06-30", Read("education[0].end_date", profile));
    }

    [Fact]
    public void The_count_reports_what_may_be_asked_for_rather_than_what_exists()
    {
        // A client reads the count and then walks the indices below it. Reporting seven when
        // five are addressable would send it into two refusals it cannot tell apart from a
        // misspelling, so the count is clamped and the rest of the history goes out on the CV.
        var profile = Profile() with
        {
            Experiences = [.. Enumerable.Range(0, 7).Select(index => new ProfileExperience(
                $"Employer {index}",
                "Engineer",
                new DateOnly(2000 + index, 1, 1),
                new DateOnly(2001 + index, 1, 1))),
            ],
        };

        Assert.Equal("5", Read("work_history.count", profile));
        Assert.False(FormFieldCatalog.TryGet("work_history[5].employer", out _));

        Assert.Equal("2", Read("education.count", Profile()));
        Assert.False(FormFieldCatalog.TryGet("education[3].institution", out _));
    }

    [Fact]
    public void Skills_are_the_vocabulary_labels_and_never_its_keys()
    {
        // skill.kubernetes is this system's identity for the concept; "Kubernetes" is what a
        // person types into a form. A key typed into an employer's form would be this
        // repository's internals leaking onto somebody's application.
        Assert.Equal("C#, Kubernetes", Read("skills", Profile()));

        // A key the vocabulary no longer knows is dropped rather than printed raw - a renamed
        // key is a fact about this system, not a skill the candidate claimed.
        var stale = Profile() with
        {
            DeclaredSkills = [new DeclaredSkill("skill.csharp"), new DeclaredSkill("skill.no_such_thing")],
        };

        Assert.Equal("C#", Read("skills", stale));
        Assert.Null(Read("skills", Profile() with { DeclaredSkills = [new DeclaredSkill("skill.no_such_thing")] }));
    }

    [Fact]
    public void The_portfolio_link_is_matched_on_the_label_the_candidate_wrote()
    {
        // People label the same link "Portfolio", "Website" or "Personal site". Answering only
        // the first would have the agent ask for a URL the profile is already holding.
        Assert.Equal("https://example.invalid", Read("portfolio_url", Profile()));

        // An explicitly labelled portfolio wins over a generic site, because the labels are
        // tried in order rather than matched all at once.
        var both = Profile() with
        {
            Links =
            [
                new ProfileLink("Personal site", "https://blog.example.invalid"),
                new ProfileLink("Portfolio", "https://work.example.invalid"),
            ],
        };

        Assert.Equal("https://work.example.invalid", Read("portfolio_url", both));
        Assert.Null(Read("portfolio_url", Profile() with { Links = [] }));
    }

    [Fact]
    public void A_profile_with_nothing_in_it_answers_null_everywhere_rather_than_throwing()
    {
        // The tools call this before knowing whether the profile is filled in, and a half-empty
        // profile is the ordinary state of a new one.
        var empty = new CandidateProfile { SubjectId = "22222222-2222-2222-2222-222222222222" };

        var counts = FormFieldCatalog.Groups.Select(group => group.CountName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(
            FormFieldCatalog.All.Where(entry => !counts.Contains(entry.Name)),
            entry => Assert.Null(entry.Read(empty)));

        // The counts answer zero rather than null, and the difference is real: a null means the
        // record does not say, and this record says. Null there would tell a client to go and
        // ask somebody how many jobs are in a form they filled in themselves.
        Assert.Equal("0", Read("work_history.count", empty));
        Assert.Equal("0", Read("education.count", empty));
    }
}
