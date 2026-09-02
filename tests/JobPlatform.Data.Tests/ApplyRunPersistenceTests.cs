using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// Apply runs against a real relational engine.
/// </summary>
/// <remarks>
/// <b>The unfinished run is what this file is really about.</b> The client is unattended: it will
/// be killed, lose its network, or be stopped by the person who started it, and none of those
/// call <c>finish_run</c>. So an open run is an ordinary end state, it is <i>read</i> as abandoned
/// rather than written closed, and the work it did is still attributed to it through the
/// submissions carrying its id. Nothing here may quietly close one, because a row that asserts a
/// finish nothing observed is worse than a row that admits it never heard back.
///
/// The rest is the account itself: only the four reported counts are stored, the derived ones are
/// recomputed, and a run that reported nothing is kept distinct from a run that reported zero -
/// the first is a client to go and restart, the second is a queue to go and fill.
/// </remarks>
public sealed class ApplyRunPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 21, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const long OtherProfileId = 2;

    public ApplyRunPersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        foreach (var (id, subject) in new[]
                 {
                     (ProfileId, "11111111-1111-1111-1111-111111111111"),
                     (OtherProfileId, "22222222-2222-2222-2222-222222222222"),
                 })
        {
            db.CandidateProfiles.Add(new CandidateProfileEntity
            {
                Id = id,
                SubjectId = subject,
                FullName = "Test Candidate",
                Email = "candidate@example.invalid",
                CreatedUtc = Now,
                UpdatedUtc = Now,
            });
        }

        for (var id = 1; id <= 3; id++)
        {
            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"linkedin:{id}",
                Site = "linkedin",
                ExternalId = id.ToString(),
                ContentHash = new string((char)('a' + id), 64),
                Title = $"Role {id}",
                Company = $"Company {id}",
                JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static RunSummary Reported() => RunSummary.From(
        considered: 12,
        submitted: 3,
        questions: 2,
        parks: [ParkReason.Captcha, ParkReason.Captcha, ParkReason.MissingAnswer]);

    // -----------------------------------------------------------------------
    // Starting, and the run that never finishes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_started_run_is_open_and_has_reported_nothing()
    {
        await using var db = CreateContext();

        var run = await new RunRepository(db).StartAsync(ProfileId, Now);

        Assert.True(run.IsOpen);
        Assert.Null(run.FinishedAtUtc);

        // Null rather than RunSummary.Empty. It has not said what it did; it has not said it did
        // nothing.
        Assert.Null(run.Summary);
    }

    [Fact]
    public async Task Starting_a_second_run_does_not_close_the_first()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var first = await repository.StartAsync(ProfileId, Now);
        var second = await repository.StartAsync(ProfileId, Now.AddMinutes(5));

        // Two open runs is a client that crashed and restarted - a fact worth being able to see
        // rather than a conflict to resolve. Closing the first here would be the sweeper this
        // design refuses, wearing a different hat.
        var open = await repository.ListOpenAsync(ProfileId);

        Assert.Equal(2, open.Count);
        Assert.Contains(open, r => r.Id == first.Id);
        Assert.Contains(open, r => r.Id == second.Id);
    }

    [Fact]
    public async Task An_unfinished_run_is_read_as_abandoned_rather_than_swept_closed()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var started = await repository.StartAsync(ProfileId, Now);

        // Reading it hours later must not write anything. A job that closed old runs would race
        // a real finish_run, and between the two the row would assert a finish nobody observed.
        var later = await repository.GetAsync(ProfileId, started.Id);

        Assert.NotNull(later);
        Assert.Null(later.FinishedAtUtc);
        Assert.False(later.IsAbandoned(Now.Add(ApplyRun.AbandonedAfter)));
        Assert.True(later.IsAbandoned(Now.Add(ApplyRun.AbandonedAfter).AddSeconds(1)));
        Assert.Single(await repository.ListOpenAsync(ProfileId));
    }

    [Fact]
    public async Task A_run_read_as_abandoned_can_still_be_finished_by_its_client()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var started = await repository.StartAsync(ProfileId, Now);

        // Abandonment is a reading taken against a clock, not a state written into the row, so a
        // client that comes back late is still the only thing entitled to say what it did.
        var (finished, outcome) = await repository.FinishAsync(
            ProfileId, started.Id, Reported(), note: null, Now.AddHours(13));

        Assert.Equal(RunFinishResult.Finished, outcome);
        Assert.NotNull(finished);
        Assert.False(finished.IsOpen);
        Assert.False(finished.IsAbandoned(Now.AddDays(3)));
        Assert.Empty(await repository.ListOpenAsync(ProfileId));
    }

    // -----------------------------------------------------------------------
    // The account a run gives of itself
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_park_breakdown_survives_the_round_trip()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var started = await repository.StartAsync(ProfileId, Now);
        await repository.FinishAsync(ProfileId, started.Id, Reported(), "Ran clean.", Now.AddHours(1));

        var stored = await repository.GetAsync(ProfileId, started.Id);

        Assert.NotNull(stored);
        Assert.Equal(Reported(), stored.Summary);
        Assert.Equal("Ran clean.", stored.Note);

        // Keyed on the enum, so the breakdown is bounded by construction rather than by width.
        Assert.Equal(2, stored.Summary!.ParkedByReason[ParkReason.Captcha]);
        Assert.Equal(1, stored.Summary.ParkedByReason[ParkReason.MissingAnswer]);
    }

    [Fact]
    public async Task Only_the_reported_counts_are_stored_and_the_rest_are_derived()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var started = await repository.StartAsync(ProfileId, Now);
        await repository.FinishAsync(ProfileId, started.Id, Reported(), null, Now.AddHours(1));

        var json = await db.Runs
            .AsNoTracking()
            .Where(r => r.Id == started.Id)
            .Select(r => r.SummaryJson)
            .FirstAsync();

        Assert.NotNull(json);

        // A stored total is a second copy of a fact the breakdown already carries, free to
        // disagree with it - and Unaccounted in particular is the number that catches a client
        // whose tallies do not add up, which a column would let the client write a zero into.
        Assert.DoesNotContain("unaccounted", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"parked\"", json, StringComparison.OrdinalIgnoreCase);

        var stored = await repository.GetAsync(ProfileId, started.Id);

        Assert.Equal(3, stored!.Summary!.Parked);
        Assert.Equal(6, stored.Summary.Unaccounted);
    }

    [Fact]
    public async Task A_run_that_reported_nothing_is_not_a_run_that_reported_zero()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var silent = await repository.StartAsync(ProfileId, Now);
        var empty = await repository.StartAsync(ProfileId, Now);

        await repository.FinishAsync(ProfileId, silent.Id, summary: null, note: null, Now.AddHours(1));
        await repository.FinishAsync(ProfileId, empty.Id, RunSummary.Empty, null, Now.AddHours(1));

        // "Died before it could say" is a client to go and restart; "looked and found nothing" is
        // a queue to go and fill. Folding them together is the mistake the column is nullable to
        // prevent.
        Assert.Null((await repository.GetAsync(ProfileId, silent.Id))!.Summary);
        Assert.Equal(RunSummary.Empty, (await repository.GetAsync(ProfileId, empty.Id))!.Summary);
    }

    [Fact]
    public async Task Finishing_a_run_twice_keeps_the_first_account()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var started = await repository.StartAsync(ProfileId, Now);
        await repository.FinishAsync(ProfileId, started.Id, Reported(), "First.", Now.AddHours(1));

        var (run, outcome) = await repository.FinishAsync(
            ProfileId, started.Id, RunSummary.Empty, "Second.", Now.AddHours(2));

        // A second finish carrying different counts is a client that has lost track of itself.
        // Overwriting would replace an account somebody may already have read with one nobody
        // can compare it to - and it is handed back rather than refused blind, so a client that
        // lost its answer can see what stands.
        Assert.Equal(RunFinishResult.AlreadyFinished, outcome);
        Assert.NotNull(run);
        Assert.Equal(Now.AddHours(1), run.FinishedAtUtc);
        Assert.Equal(Reported(), run.Summary);
        Assert.Equal("First.", run.Note);
    }

    [Fact]
    public async Task A_note_longer_than_the_column_is_trimmed_rather_than_refused()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var started = await repository.StartAsync(ProfileId, Now);

        // Bounded on the way in rather than at the database: a silent truncation by the engine is
        // the shape of bug this codebase has paid for before, and losing a run's account to an
        // over-long sentence would lose the only record of what it saw.
        var (run, outcome) = await repository.FinishAsync(
            ProfileId,
            started.Id,
            RunSummary.Empty,
            new string('x', SubmissionLimits.MaxNoteLength + 50),
            Now.AddHours(1));

        Assert.Equal(RunFinishResult.Finished, outcome);
        Assert.Equal(SubmissionLimits.MaxNoteLength, run!.Note!.Length);
    }

    // -----------------------------------------------------------------------
    // What the account is checked against
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_submissions_carrying_a_run_id_are_what_its_claim_is_checked_against()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var started = await repository.StartAsync(ProfileId, Now);

        db.Submissions.Add(new SubmissionEntity
        {
            ProfileId = ProfileId,
            PostingId = 1,
            Channel = SubmissionChannel.Ats,
            CreatedAtUtc = Now,
            RunId = started.Id,
        });

        // Created outside the run: it belongs to the candidate and not to this pass, and counting
        // it would flatter the run's own claim.
        db.Submissions.Add(new SubmissionEntity
        {
            ProfileId = ProfileId,
            PostingId = 2,
            Channel = SubmissionChannel.Board,
            CreatedAtUtc = Now,
        });

        await db.SaveChangesAsync();

        await repository.FinishAsync(
            ProfileId,
            started.Id,
            RunSummary.From(considered: 9, submitted: 4, questions: 0, parks: []),
            null,
            Now.AddHours(1));

        var claimed = (await repository.GetAsync(ProfileId, started.Id))!.Summary!.Submitted;
        var recorded = await repository.CountSubmissionsAsync(ProfileId, started.Id);

        // The disagreement is the interesting part and is left visible rather than corrected:
        // Submitted is the client's claim, and these rows are the record.
        Assert.Equal(4, claimed);
        Assert.Equal(1, recorded);
    }

    // -----------------------------------------------------------------------
    // Reading, and the authorisation boundary
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Runs_are_listed_newest_first_and_the_bound_is_applied_after_the_order()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var oldest = await repository.StartAsync(ProfileId, Now);
        var middle = await repository.StartAsync(ProfileId, Now.AddHours(1));
        var newest = await repository.StartAsync(ProfileId, Now.AddHours(2));

        var page = await repository.ListAsync(ProfileId, limit: 2);

        Assert.Equal(new[] { newest.Id, middle.Id }, page.Select(r => r.Id));
        Assert.DoesNotContain(page, r => r.Id == oldest.Id);
    }

    [Fact]
    public async Task A_stranger_can_neither_read_nor_finish_somebody_elses_run()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        var mine = await repository.StartAsync(ProfileId, Now);

        var (row, outcome) = await repository.FinishAsync(
            OtherProfileId, mine.Id, Reported(), null, Now.AddHours(1));

        // NotFound rather than a partial answer: a caller cannot tell "no such run" from "not
        // yours", the rule the whole of this side of the system follows.
        Assert.Equal(RunFinishResult.NotFound, outcome);
        Assert.Null(row);
        Assert.Null(await repository.GetAsync(OtherProfileId, mine.Id));
        Assert.Empty(await repository.ListAsync(OtherProfileId, limit: 50));

        // And it is still open, which is the part that would matter: a refusal that closed the
        // run anyway would be the worst of both.
        Assert.True((await repository.GetAsync(ProfileId, mine.Id))!.IsOpen);
    }

    [Fact]
    public async Task A_candidates_runs_are_theirs_alone()
    {
        await using var db = CreateContext();
        var repository = new RunRepository(db);

        await repository.StartAsync(ProfileId, Now);
        await repository.StartAsync(OtherProfileId, Now);

        Assert.Single(await repository.ListAsync(ProfileId, limit: 50));
        Assert.Single(await repository.ListOpenAsync(OtherProfileId));
    }
}
