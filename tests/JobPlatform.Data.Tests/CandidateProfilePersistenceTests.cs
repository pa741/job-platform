using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Profiles;
using JobPlatform.Data.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The profile write path, against a real relational engine so the LINQ has to translate and
/// the cascades have to hold.
/// </summary>
/// <remarks>
/// The two things most worth pinning here are both about correctness of the write rather than
/// of a query: that a save is a genuine replace - because a merge cannot express "delete the
/// third job" - and that the extraction hash only moves when the text the extractor reads
/// moves. The second is what stops correcting a phone number from costing a model call and
/// invalidating every match already scored against the profile.
/// </remarks>
public sealed class CandidateProfilePersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private const string OtherSubject = "22222222-2222-2222-2222-222222222222";

    private static readonly FakeTime Time = new(new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero));

    public CandidateProfilePersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();
        ConceptSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static CandidateProfile Profile(
        string subject = Subject,
        string? summary = "Backend engineer, mostly C# and Kubernetes.",
        IReadOnlyList<ProfileExperience>? experiences = null,
        IReadOnlyList<DeclaredSkill>? declared = null,
        string? phone = null)
        => new()
        {
            SubjectId = subject,
            FullName = "Ada Lovelace",
            Headline = "Senior Backend Engineer",
            Email = "ada@example.com",
            Phone = phone,
            Summary = summary,
            LocationCity = "London",
            LocationCountry = "United Kingdom",
            PreferredArrangement = WorkArrangement.Hybrid,
            MaxDaysInOffice = 2,
            MinimumSalary = 75_000m,
            SalaryCurrency = "GBP",
            YearsExperience = 8,
            Seniority = Seniority.Senior,
            JobTypes = ["fulltime"],
            Experiences = experiences ??
            [
                new ProfileExperience("Contoso", "Senior Engineer",
                    new DateOnly(2021, 3, 1), null, "Ran the ingestion pipeline."),
                new ProfileExperience("Fabrikam", "Engineer",
                    new DateOnly(2018, 1, 1), new DateOnly(2021, 2, 1), "Owned billing."),
            ],
            Education = [new ProfileEducation("University of Somewhere", "BSc", "Computer Science")],
            Projects = [new ProfileProject("job-platform", "A job market pipeline.")],
            DeclaredSkills = declared ?? [new DeclaredSkill("skill.csharp", AssertionPolarity.Expert, 8)],
        };

    [Fact]
    public async Task A_profile_round_trips_with_all_its_sections()
    {
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);
        }

        await using var read = CreateContext();
        var view = await new CandidateProfileRepository(read).GetAsync(Subject);

        Assert.NotNull(view);
        Assert.Equal("Ada Lovelace", view.Profile.FullName);
        Assert.Equal(2, view.Profile.Experiences.Count);
        Assert.Single(view.Profile.Education);
        Assert.Single(view.Profile.Projects);
        Assert.Equal(["fulltime"], view.Profile.JobTypes);
        Assert.Equal(Seniority.Senior, view.Profile.Seniority);
    }

    [Fact]
    public async Task The_candidates_ordering_of_their_roles_survives_the_round_trip()
    {
        // Their ordering is a choice they made: leading with a side contract rather than the
        // most recent job is sometimes exactly right, and re-sorting by date would overrule it.
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(
                Profile(experiences:
                [
                    new ProfileExperience("Second Place", "Contractor", new DateOnly(2019, 1, 1)),
                    new ProfileExperience("First Place", "Engineer", new DateOnly(2023, 1, 1)),
                ]),
                Time);
        }

        await using var read = CreateContext();
        var view = await new CandidateProfileRepository(read).GetAsync(Subject);

        Assert.Equal("Second Place", view!.Profile.Experiences[0].Company);
        Assert.Equal("First Place", view.Profile.Experiences[1].Company);
    }

    [Fact]
    public async Task A_save_replaces_rather_than_merges()
    {
        // The behaviour a merge cannot express. Someone deleting the third job on the form and
        // pressing save must end up with two jobs, not three.
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);
        }

        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(
                Profile(experiences: [new ProfileExperience("Contoso", "Senior Engineer")]),
                Time);
        }

        await using var read = CreateContext();
        var view = await new CandidateProfileRepository(read).GetAsync(Subject);

        Assert.Single(view!.Profile.Experiences);
        Assert.Equal(1, await read.CandidateProfiles.CountAsync());
    }

    [Fact]
    public async Task One_person_cannot_read_anothers_profile_through_this_repository()
    {
        // There is no overload that takes a profile id, so this is the only way to ask - which
        // is the point. An endpoint cannot be written that reads a stranger's record by mistake.
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);
        }

        await using var read = CreateContext();

        Assert.NotNull(await new CandidateProfileRepository(read).GetAsync(Subject));
        Assert.Null(await new CandidateProfileRepository(read).GetAsync(OtherSubject));
    }

    // -----------------------------------------------------------------------
    // The extraction hash
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Changing_something_the_extractor_never_reads_does_not_mark_the_text_as_changed()
    {
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);
        }

        await using var second = CreateContext();

        var (_, textChanged) = await new CandidateProfileRepository(second)
            .SaveAsync(Profile(phone: "+44 7700 900000"), Time);

        // A phone number is not in the composed document, so re-extracting would cost a model
        // call and invalidate every match already scored, for nothing.
        Assert.False(textChanged);
    }

    [Fact]
    public async Task Changing_what_the_extractor_reads_does_mark_the_text_as_changed()
    {
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);
        }

        await using var second = CreateContext();

        var (_, textChanged) = await new CandidateProfileRepository(second)
            .SaveAsync(Profile(summary: "Now mostly a platform engineer, working in Go."), Time);

        Assert.True(textChanged);
    }

    [Fact]
    public async Task A_first_save_always_counts_as_changed()
    {
        await using var db = CreateContext();

        var (_, textChanged) = await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);

        Assert.True(textChanged);
    }

    // -----------------------------------------------------------------------
    // Declared and extracted skills
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_declared_skill_is_stored_as_the_candidates_own_structured_claim()
    {
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);
        }

        await using var read = CreateContext();
        var view = await new CandidateProfileRepository(read).GetAsync(Subject);

        var declared = Assert.Single(view!.Profile.DeclaredSkills);
        Assert.Equal("skill.csharp", declared.ConceptKey);
        Assert.Equal(AssertionPolarity.Expert, declared.Polarity);

        // Board, not Model: the supply-side equivalent of an employer's own tagging, so the
        // match can weigh it above something inferred from prose.
        var stored = Assert.Single(await read.ProfileConcepts.ToListAsync());
        Assert.Equal(AssertionSource.Board, stored.Source);
    }

    [Fact]
    public async Task A_declared_key_the_vocabulary_does_not_know_becomes_a_mention_rather_than_an_error()
    {
        // Failing the save would mean a candidate cannot record their profile because they named
        // a technology this system has not caught up with - and the mention log is how it
        // catches up.
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(
                Profile(declared: [new DeclaredSkill("skill.frobnicator-9000")]), Time);
        }

        await using var read = CreateContext();

        Assert.Empty(await read.ProfileConcepts.ToListAsync());

        var mention = Assert.Single(await read.ProfileMentions.ToListAsync());
        Assert.Equal("skill.frobnicator-9000", mention.SurfaceForm);
        Assert.Equal(MentionReason.UnknownBoardSkill, mention.Reason);
    }

    [Fact]
    public async Task A_demand_polarity_on_a_declared_skill_is_clamped_rather_than_stored()
    {
        // Required is 3 and Expert is 13, so storing it would compare as weaker than every
        // genuine claim and quietly deflate the match.
        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).SaveAsync(
                Profile(declared: [new DeclaredSkill("skill.csharp", AssertionPolarity.Required)]), Time);
        }

        await using var read = CreateContext();
        var stored = Assert.Single(await read.ProfileConcepts.ToListAsync());

        Assert.Equal(AssertionPolarity.Proficient, stored.Polarity);
    }

    [Fact]
    public async Task An_extraction_replaces_only_what_the_model_wrote()
    {
        long profileId;

        await using (var db = CreateContext())
        {
            var (view, _) = await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);
            profileId = view.Id;
        }

        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).ApplyExtractionAsync(
                profileId,
                new DocumentExtraction
                {
                    Concepts =
                    [
                        new ConceptAssertion("skill.kubernetes", AssertionSource.Model,
                            AssertionPolarity.Required, EvidenceText: "ran Kubernetes"),
                    ],
                    Model = "gpt-5-6-luna",
                },
                inputHash: new string('a', 64),
                extractedAtUtc: Time.GetUtcNow());
        }

        await using var read = CreateContext();
        var repository = new CandidateProfileRepository(read);
        var assertions = await repository.GetAssertionsAsync(profileId);

        // Both survive: the declared skill is the candidate's own claim and the model has no
        // business overwriting it, exactly as the model pass does not overwrite a board tag on
        // the posting side.
        Assert.Contains(assertions, a => a.ConceptKey == "skill.csharp" && a.Source == AssertionSource.Board);
        Assert.Contains(assertions, a => a.ConceptKey == "skill.kubernetes" && a.Source == AssertionSource.Model);
    }

    [Fact]
    public async Task An_extracted_demand_polarity_is_translated_to_the_supply_half()
    {
        // One extraction path reads both adverts and profiles, so the prompt speaks demand and
        // the conversion is written down here rather than duplicated into a second prompt.
        long profileId;

        await using (var db = CreateContext())
        {
            var (view, _) = await new CandidateProfileRepository(db).SaveAsync(
                Profile(declared: []), Time);
            profileId = view.Id;
        }

        await using (var db = CreateContext())
        {
            await new CandidateProfileRepository(db).ApplyExtractionAsync(
                profileId,
                new DocumentExtraction
                {
                    Concepts =
                    [
                        new ConceptAssertion("skill.kubernetes", AssertionSource.Model, AssertionPolarity.Required),
                        new ConceptAssertion("skill.terraform", AssertionSource.Model, AssertionPolarity.Preferred),
                        new ConceptAssertion("skill.rust", AssertionSource.Model, AssertionPolarity.Mentioned),
                    ],
                },
                inputHash: new string('b', 64),
                extractedAtUtc: Time.GetUtcNow());
        }

        await using var read = CreateContext();
        var assertions = await new CandidateProfileRepository(read).GetAssertionsAsync(profileId);

        Assert.Equal(AssertionPolarity.Expert, assertions.Single(a => a.ConceptKey == "skill.kubernetes").Polarity);
        Assert.Equal(AssertionPolarity.Proficient, assertions.Single(a => a.ConceptKey == "skill.terraform").Polarity);
        Assert.Equal(AssertionPolarity.Familiar, assertions.Single(a => a.ConceptKey == "skill.rust").Polarity);
    }

    [Fact]
    public async Task Deleting_a_profile_takes_everything_derived_from_it()
    {
        // The reason matches cascade from the profile side. A system that stores an employment
        // history without a way to remove it is not one anybody should hand a CV to.
        long profileId;

        await using (var db = CreateContext())
        {
            var (view, _) = await new CandidateProfileRepository(db).SaveAsync(Profile(), Time);
            profileId = view.Id;
        }

        await using (var db = CreateContext())
        {
            await db.CandidateProfiles.Where(p => p.Id == profileId).ExecuteDeleteAsync();
        }

        await using var read = CreateContext();

        Assert.Empty(await read.CandidateProfiles.ToListAsync());
        Assert.Empty(await read.ProfileExperiences.ToListAsync());
        Assert.Empty(await read.ProfileConcepts.ToListAsync());
        Assert.Empty(await read.ProfileEducation.ToListAsync());
        Assert.Empty(await read.ProfileProjects.ToListAsync());
    }

    /// <summary>A fixed clock, so timestamps are known by construction.</summary>
    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
