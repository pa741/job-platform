using JobPlatform.Core.Settings;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The settings write path, against a real relational engine so the join and the nullable column
/// have to translate.
/// </summary>
/// <remarks>
/// Three things are worth pinning here and none of them is a query.
///
/// <b>First, that an absent row reads back as <see cref="PipelineSettings.Default"/>.</b> That is
/// the property the whole feature is judged on - a deployment that configures nothing must not be
/// able to tell the settings arrived - and the storage layer is where it is actually decided.
/// Anything else, null included, pushes "what does no row mean" out to the nightly passes and the
/// submission write path, and the first caller to read a null as zero switches somebody's pipeline
/// off silently.
///
/// <b>Second, that all nine fields survive a round trip.</b> The record and the row are two types
/// with no <c>with</c> spanning them, so <c>ToDomain</c> and <c>Apply</c> copy nine values across
/// by hand - and nothing in the type system catches a forgotten line in either direction. A field
/// missed on the read reads back as its default, which is indistinguishable from a candidate who
/// asked for the default; a field missed on the write is a save that silently drops one lever.
/// The repository's own remarks name this as a test obligation, so this is that test: nine values
/// all distinct and none of them the shipped default.
///
/// <b>Third, the authorisation boundary.</b> Every method a person can reach takes a subject id
/// and none takes a profile id, and the two named exceptions exist for the unattended passes.
/// A subject must not be able to read or overwrite another's row, and that is asserted rather than
/// assumed.
///
/// SQLite in memory, like every other test in this project: no Azure account, no credentials, and
/// the LINQ still has to become SQL.
/// </remarks>
public sealed class PipelineSettingsPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private readonly long _profileId;
    private readonly long _otherProfileId;

    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private const string OtherSubject = "22222222-2222-2222-2222-222222222222";

    /// <summary>A subject with no profile at all, which is a different case from an unsaved one.</summary>
    private const string StrangerSubject = "33333333-3333-3333-3333-333333333333";

    /// <summary>
    /// The hash the extraction sweep reads, pinned so a settings save can be shown not to move it.
    /// </summary>
    private const string ProfileHash = "hash-of-the-document-the-extractor-reads";

    private static readonly DateTimeOffset Created = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 9, 8, 21, 30, 0, TimeSpan.Zero);

    private static readonly FakeTime Time = new(Created);
    private static readonly FakeTime TimeLater = new(Later);

    public PipelineSettingsPersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        // The profile rows are inserted directly rather than through CandidateProfileRepository.
        // A settings row needs a key and nothing else about a profile matters to any assertion
        // here, where going through the repository would drag the concept seeder and the
        // extraction hash into a test about nine integers.
        var mine = Profile(Subject);
        var theirs = Profile(OtherSubject);

        db.CandidateProfiles.AddRange(mine, theirs);
        db.SaveChanges();

        _profileId = mine.Id;
        _otherProfileId = theirs.Id;
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static CandidateProfileEntity Profile(string subject) => new()
    {
        SubjectId = subject,
        FullName = "Ada Lovelace",
        CreatedUtc = Created,
        UpdatedUtc = Created,
        ExtractionInputHash = ProfileHash,
    };

    /// <summary>
    /// A configuration in which every one of the nine differs from the shipped default.
    /// </summary>
    /// <remarks>
    /// Nine distinct values, none of them equal to the default it replaces, so a mapping that
    /// crosses two fields or drops one cannot pass by coincidence. It is also a configuration
    /// somebody could really have saved - <see cref="PipelineSettingsValidation"/> accepts it -
    /// because a fixture the endpoint would refuse is a fixture that proves the wrong thing about
    /// the column widths it round trips through.
    /// </remarks>
    private static PipelineSettings Configured => PipelineSettings.Default with
    {
        AssessmentsPerNight = 7,
        AssessmentThreshold = 12,
        RecentSharePercent = 33,
        RecentWindowDays = 5,
        DraftsPerNight = 3,
        DraftMinAssessmentScore = 61,
        DraftPostedWithinDays = 21,
        DailySendCap = 9,
        ChaseAfterDays = 90,
    };

    /// <summary>
    /// The assertion the feature is judged on: nothing stored is the pipeline that already ran.
    /// </summary>
    /// <remarks>
    /// Never null, never a half-filled record, and never a signal for the caller to go and find
    /// the defaults itself. A candidate with no row behaves exactly as they did on a build from
    /// before the table existed.
    /// </remarks>
    [Fact]
    public async Task A_candidate_who_has_never_saved_reads_the_shipped_defaults()
    {
        await using var db = CreateContext();

        Assert.Equal(PipelineSettings.Default, await new PipelineSettingsRepository(db).GetAsync(Subject));
    }

    /// <summary>
    /// A subject with no profile at all reads the defaults too, and that costs nothing.
    /// </summary>
    /// <remarks>
    /// Somebody with no profile has no pipeline running, so the settings their pipeline would run
    /// under are the ones nobody configured. <c>SaveAsync</c> is the method that has to care about
    /// a missing profile, because a row needs a key.
    /// </remarks>
    [Fact]
    public async Task A_subject_with_no_profile_reads_the_defaults_rather_than_failing()
    {
        await using var db = CreateContext();

        Assert.Equal(PipelineSettings.Default, await new PipelineSettingsRepository(db).GetAsync(StrangerSubject));
    }

    /// <summary>
    /// Saving without a profile answers null and creates nothing.
    /// </summary>
    /// <remarks>
    /// The endpoint answers 404 from this. Inventing a profile here would create an empty one as a
    /// side effect of visiting a settings page - a row the extraction sweep would then pick up.
    /// </remarks>
    [Fact]
    public async Task Saving_for_a_subject_with_no_profile_answers_null_and_creates_no_profile()
    {
        await using var db = CreateContext();

        Assert.Null(await new PipelineSettingsRepository(db).SaveAsync(StrangerSubject, Configured, Time));

        await using var read = CreateContext();
        Assert.Equal(2, await read.CandidateProfiles.CountAsync());
        Assert.Empty(await read.PipelineSettings.ToListAsync());
    }

    /// <summary>
    /// All nine fields survive the round trip, asserted one at a time.
    /// </summary>
    /// <remarks>
    /// One at a time rather than as a whole-record comparison: a record equality failure names the
    /// record, and this is the test whose job is to name the field. It is read back through a
    /// fresh context, so what is asserted is what the database holds rather than what the change
    /// tracker remembers.
    /// </remarks>
    [Fact]
    public async Task Every_one_of_the_nine_fields_round_trips()
    {
        // The fixture is a configuration the endpoint would have accepted.
        Assert.Empty(PipelineSettingsValidation.Validate(Configured));

        await using (var db = CreateContext())
        {
            Assert.Equal(Configured, await new PipelineSettingsRepository(db).SaveAsync(Subject, Configured, Time));
        }

        await using var read = CreateContext();
        var stored = await new PipelineSettingsRepository(read).GetAsync(Subject);

        Assert.Equal(7, stored.AssessmentsPerNight);
        Assert.Equal(12, stored.AssessmentThreshold);
        Assert.Equal(33, stored.RecentSharePercent);
        Assert.Equal(5, stored.RecentWindowDays);
        Assert.Equal(3, stored.DraftsPerNight);
        Assert.Equal(61, stored.DraftMinAssessmentScore);
        Assert.Equal(21, stored.DraftPostedWithinDays);
        Assert.Equal(9, stored.DailySendCap);
        Assert.Equal(90, stored.ChaseAfterDays);
    }

    /// <summary>
    /// The age bound round trips as a number and as an absence, and clearing it writes NULL.
    /// </summary>
    /// <remarks>
    /// The one field where a merge and a replace differ visibly. Null is "no age bound at all" and
    /// zero would be "posted since this instant", so a cleared box that left yesterday's number
    /// standing - or that arrived as zero - is a pass writing for almost nothing and reading as
    /// broken. It is assigned unconditionally in <c>Apply</c> for exactly this reason.
    /// </remarks>
    [Fact]
    public async Task The_age_bound_round_trips_set_and_then_cleared_to_null()
    {
        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(
                Subject, PipelineSettings.Default with { DraftPostedWithinDays = 30 }, Time);
        }

        await using (var db = CreateContext())
        {
            Assert.Equal(30, (await new PipelineSettingsRepository(db).GetAsync(Subject)).DraftPostedWithinDays);
        }

        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(
                Subject, PipelineSettings.Default with { DraftPostedWithinDays = null }, TimeLater);
        }

        await using var read = CreateContext();
        Assert.Null((await new PipelineSettingsRepository(read).GetAsync(Subject)).DraftPostedWithinDays);

        // And NULL in the column rather than a zero that reads the same way through a nullable
        // int only until something compares it.
        await using var raw = CreateContext();
        var row = Assert.Single(await raw.PipelineSettings.ToListAsync());
        Assert.Null(row.DraftPostedWithinDays);
    }

    /// <summary>
    /// The authorisation boundary: one candidate's settings are invisible to another subject.
    /// </summary>
    /// <remarks>
    /// The subject id is the boundary expressed as a type - there is no overload an endpoint could
    /// hand a route parameter to - and this is that boundary tested from the outside. The other
    /// subject has a profile of their own, so what is asserted is scoping rather than the read
    /// failing to find anything at all.
    /// </remarks>
    [Fact]
    public async Task Another_subject_cannot_read_these_settings()
    {
        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(Subject, Configured, Time);
        }

        await using var read = CreateContext();

        Assert.Equal(PipelineSettings.Default, await new PipelineSettingsRepository(read).GetAsync(OtherSubject));
    }

    /// <summary>
    /// The authorisation boundary on the write side: one candidate cannot overwrite another's.
    /// </summary>
    /// <remarks>
    /// The failure this refuses is the expensive one. A save scoped by anything other than the
    /// saver's own subject would let one person reconfigure a stranger's nightly spending, and
    /// nothing downstream would report it - the passes would simply read the numbers they were
    /// given.
    /// </remarks>
    [Fact]
    public async Task A_save_by_another_subject_lands_on_their_own_row_and_not_on_this_one()
    {
        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(Subject, Configured, Time);
        }

        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(
                OtherSubject, PipelineSettings.Default with { AssessmentsPerNight = 199 }, TimeLater);
        }

        await using var read = CreateContext();
        var repository = new PipelineSettingsRepository(read);

        Assert.Equal(Configured, await repository.GetAsync(Subject));
        Assert.Equal(199, (await repository.GetAsync(OtherSubject)).AssessmentsPerNight);

        // Two candidates, two rows: the second save created one rather than moving the first.
        await using var raw = CreateContext();
        Assert.Equal(2, await raw.PipelineSettings.CountAsync());
    }

    /// <summary>
    /// The nightly passes read the same numbers the settings page shows.
    /// </summary>
    /// <remarks>
    /// <c>GetForProfileAsync</c> is the named exception to the subject-id rule and exists for the
    /// unattended passes, which iterate profile ids and hold no subject at all. If it and
    /// <c>GetAsync</c> could disagree, the page would be describing a pipeline that does not run.
    /// </remarks>
    [Fact]
    public async Task The_by_profile_read_answers_exactly_what_the_by_subject_read_answers()
    {
        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(Subject, Configured, Time);
        }

        await using var read = CreateContext();
        var repository = new PipelineSettingsRepository(read);

        var bySubject = await repository.GetAsync(Subject);
        var byProfile = await repository.GetForProfileAsync(_profileId);

        Assert.Equal(bySubject, byProfile);
        Assert.Equal(Configured, byProfile);
    }

    /// <summary>
    /// And it answers the defaults for an unconfigured profile, exactly as the subject read does.
    /// </summary>
    [Fact]
    public async Task The_by_profile_read_answers_the_defaults_for_a_profile_with_no_row()
    {
        await using var db = CreateContext();

        Assert.Equal(
            PipelineSettings.Default,
            await new PipelineSettingsRepository(db).GetForProfileAsync(_otherProfileId));
    }

    /// <summary>
    /// The set read the passes actually use: every id asked for is present in the answer.
    /// </summary>
    /// <remarks>
    /// Returning only the stored rows would put "absent means the defaults" back at the call site,
    /// and the call site is a loop - where the natural spelling of a miss is to skip the candidate
    /// rather than to run them on the defaults. An id nobody asked for throws instead, which is
    /// the right way round: a pass reading settings for a profile it did not fetch is a bug, and a
    /// loud one is cheaper than a candidate silently run on somebody else's assumptions.
    /// </remarks>
    [Fact]
    public async Task The_set_read_answers_for_every_profile_asked_for_and_only_those()
    {
        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(Subject, Configured, Time);
        }

        await using var read = CreateContext();
        var settings = await new PipelineSettingsRepository(read)
            .GetForProfilesAsync([_profileId, _otherProfileId]);

        Assert.Equal(Configured, settings[_profileId]);
        Assert.Equal(PipelineSettings.Default, settings[_otherProfileId]);
        Assert.Throws<KeyNotFoundException>(() => settings[_profileId + _otherProfileId + 1]);
    }

    /// <summary>
    /// A second save moves the one row rather than adding a second.
    /// </summary>
    /// <remarks>
    /// The primary key is the foreign key, so a second row could only ever be a stale first and
    /// the database refuses it - rather than leaving the upsert as the only thing standing between
    /// a candidate and two configurations. The created timestamp stays put and the updated one
    /// moves, which is what says the row was found rather than replaced.
    /// </remarks>
    [Fact]
    public async Task A_second_save_updates_the_one_row_the_first_created()
    {
        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(Subject, Configured, Time);
        }

        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(
                Subject, Configured with { AssessmentsPerNight = 11 }, TimeLater);
        }

        await using var read = CreateContext();
        var row = Assert.Single(await read.PipelineSettings.ToListAsync());

        Assert.Equal(_profileId, row.ProfileId);
        Assert.Equal(11, row.AssessmentsPerNight);
        Assert.Equal(Created, row.CreatedUtc);
        Assert.Equal(Later, row.UpdatedUtc);
    }

    /// <summary>
    /// Saving the defaults and never having saved read back the same, which is why there is no
    /// delete.
    /// </summary>
    /// <remarks>
    /// Resetting to the shipped behaviour is a save of <see cref="PipelineSettings.Default"/>. A
    /// delete would be a second spelling of one operation and the only difference between them
    /// would be a timestamp.
    /// </remarks>
    [Fact]
    public async Task Saving_the_defaults_reads_back_as_though_nothing_had_been_saved()
    {
        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(Subject, Configured, Time);
        }

        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(Subject, PipelineSettings.Default, TimeLater);
        }

        await using var read = CreateContext();
        var repository = new PipelineSettingsRepository(read);

        // The candidate who saved the defaults and the candidate who never saved are handed the
        // same record.
        Assert.Equal(PipelineSettings.Default, await repository.GetAsync(Subject));
        Assert.Equal(PipelineSettings.Default, await repository.GetAsync(OtherSubject));
    }

    /// <summary>
    /// A settings save touches no column on the profile, and above all not the extraction hash.
    /// </summary>
    /// <remarks>
    /// The second of the two mechanical reasons these nine numbers are a table of their own.
    /// <c>CandidateProfiles.ExtractionInputHash</c> is taken over the text the extractor reads and
    /// is what decides whether a save costs a model call and invalidates every match already
    /// scored, so a number that moves no text must not be able to move that hash. There is no path
    /// from this table into <c>CandidateProfile.ToDocument()</c>, which is what makes this true by
    /// construction rather than by care - and this is the assertion that would notice if a
    /// settings column were ever moved onto the profile row.
    /// </remarks>
    [Fact]
    public async Task Saving_settings_moves_nothing_on_the_profile_row()
    {
        await using (var db = CreateContext())
        {
            await new PipelineSettingsRepository(db).SaveAsync(Subject, Configured, TimeLater);
        }

        await using var read = CreateContext();
        var profile = await read.CandidateProfiles.SingleAsync(p => p.SubjectId == Subject);

        Assert.Equal(ProfileHash, profile.ExtractionInputHash);
        Assert.Equal(Created, profile.UpdatedUtc);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
