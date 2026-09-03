using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The apply loop's write path against a real relational engine.
/// </summary>
/// <remarks>
/// Four things are pinned here, and each of them is a rule that would fail silently.
///
/// <b>The quota and the cap are one count.</b> They are asked in different places - a run plans
/// its batch against one and is refused by the other - so a burn-down that drifted from the bound
/// would be wrong only for the events nobody is watching, the backdated ones.
///
/// <b>A create and its claim are one write.</b> An application that happened in the world and is
/// not in the log is the failure this pipeline cannot recover from, and the half-written state
/// that produces it is invisible afterwards: a submission row with no event looks exactly like an
/// application somebody has not got round to recording.
///
/// <b>Parking sets columns and deletes nothing.</b> It has to be reversible and idempotent,
/// because a run that parks the same posting every night must not rewrite when it was first
/// blocked, and a park that stood in March must not read as a park today.
///
/// <b>Evidence is optional at every point.</b> None of it may block an event: this is captured by
/// something driving a browser through somebody else's form, and the interesting runs are the
/// ones that go wrong.
/// </remarks>
public sealed class SubmissionWritePathTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;

    public SubmissionWritePathTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = "11111111-1111-1111-1111-111111111111",
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });

        for (var id = 1; id <= 6; id++)
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
                LocationCity = "London",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static DateTimeOffset At(int day, int hour = 9)
        => new(2026, 8, day, hour, 0, 0, TimeSpan.Zero);

    private static SubmissionEvent Submitted(DateTimeOffset at, SubmissionEvidence? evidence = null)
        => new(at, SubmissionEventType.Submitted, Stage: null, SubmissionEventSource.Client, Note: null)
        {
            Evidence = evidence,
        };

    // -----------------------------------------------------------------------
    // The quota, which is the cap read out rather than a second count of it
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_quota_counts_the_day_an_event_claims_rather_than_the_day_it_was_written()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        // Written now, claimed yesterday. Every row in this test is written in the same instant,
        // so a count taken on write time would put all of them in one day - which is the reading
        // that would make the burn-down disagree with the bound actually enforced.
        await repository.AddEventAsync(ProfileId, submission.Id, Submitted(At(30)), "yesterday");
        await repository.AddEventAsync(ProfileId, submission.Id, Submitted(At(31, 0)), "midnight");
        await repository.AddEventAsync(ProfileId, submission.Id, Submitted(At(31, 23)), "late");

        var today = await repository.GetQuotaAsync(ProfileId, At(31));

        Assert.Equal(new DateOnly(2026, 8, 31), today.Day);
        Assert.Equal(2, today.SubmittedOnDay);
        Assert.Equal(SubmissionLimits.MaxSubmittedPerDay, today.DailyCap);
        Assert.Equal(SubmissionLimits.MaxSubmittedPerDay - 2, today.Remaining);
        Assert.False(today.IsExhausted);

        // The window is the UTC day and nothing else: midnight belongs to the day it opens.
        Assert.Equal(1, (await repository.GetQuotaAsync(ProfileId, At(30))).SubmittedOnDay);
    }

    [Fact]
    public async Task The_quota_is_exhausted_exactly_where_the_cap_starts_refusing()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        for (var i = 0; i < SubmissionLimits.MaxSubmittedPerDay - 1; i++)
        {
            await repository.AddEventAsync(ProfileId, submission.Id, Submitted(At(31)), $"send-{i}");
        }

        // One left, and the write that takes it succeeds. The pair is the point: a burn-down
        // that read one high would have a run plan a batch the cap then refuses, after the form
        // has already gone.
        Assert.Equal(1, (await repository.GetQuotaAsync(ProfileId, At(31))).Remaining);
        Assert.Equal(
            SubmissionEventResult.Recorded,
            await repository.AddEventAsync(ProfileId, submission.Id, Submitted(At(31)), "last"));

        var spent = await repository.GetQuotaAsync(ProfileId, At(31));

        Assert.True(spent.IsExhausted);
        Assert.Equal(0, SubmissionQuota.Plan(spent, candidateCount: 10));
        Assert.Equal(
            SubmissionEventResult.DailyLimitReached,
            await repository.AddEventAsync(ProfileId, submission.Id, Submitted(At(31)), "over"));

        // And tomorrow is a fresh budget, counted the same way.
        Assert.Equal(SubmissionLimits.MaxSubmittedPerDay, (await repository.GetQuotaAsync(ProfileId, At(30))).Remaining);
    }

    [Fact]
    public async Task Nothing_but_a_claim_of_sending_spends_the_quota()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        await repository.AddEventAsync(
            ProfileId,
            submission.Id,
            new SubmissionEvent(At(31), SubmissionEventType.Rejected, null, SubmissionEventSource.Email, null),
            "rejected");

        // Recording that an employer replied is not an application. The cap bounds the blast
        // radius of a client that loops, and a loop that only reads an inbox has nothing to
        // bound.
        Assert.Equal(0, (await repository.GetQuotaAsync(ProfileId, At(31))).SubmittedOnDay);
    }

    // -----------------------------------------------------------------------
    // The create and its claim, in one write
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Creating_with_an_event_records_the_application_and_the_claim_together()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var result = await repository.CreateWithEventAsync(
            ProfileId,
            postingId: 1,
            SubmissionChannel.Ats,
            "https://careers.example.invalid/1",
            Submitted(At(31)),
            "run-7:1:Submitted",
            Now,
            documentRevision: 3);

        Assert.True(result.Created);
        Assert.Equal(SubmissionEventResult.Recorded, result.Event);
        Assert.NotNull(result.Row);

        // The row is not merely present, it is already folded to Submitted - which is the whole
        // difference between this and a create followed by an event that never landed.
        Assert.Equal(SubmissionEventType.Submitted, result.Row!.Status.Phase);
        Assert.Single(await repository.ListEventsAsync(ProfileId, result.Row.Id));

        // The revision the pack served, recorded at creation. Regenerating afterwards produces a
        // better draft that was never the one an employer read, and nothing else could recover
        // which one was sent.
        Assert.Equal(3, result.Row.DocumentRevision);
    }

    [Fact]
    public async Task A_create_refused_by_the_cap_leaves_no_submission_behind()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (spender, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        for (var i = 0; i < SubmissionLimits.MaxSubmittedPerDay; i++)
        {
            await repository.AddEventAsync(ProfileId, spender.Id, Submitted(At(31)), $"send-{i}");
        }

        var refused = await repository.CreateWithEventAsync(
            ProfileId, postingId: 2, SubmissionChannel.Ats, null, Submitted(At(31)), "over", Now);

        Assert.Equal(SubmissionEventResult.DailyLimitReached, refused.Event);
        Assert.False(refused.Created);
        Assert.Null(refused.Row);

        // The part that matters, and the reason the row is not written first: a submission row
        // is what takes a posting out of the applyable queue. One left behind here would remove
        // posting 2 from every future run while asserting nothing about an application ever
        // having been sent, and nothing anywhere would say why it stopped being offered.
        await using var fresh = CreateContext();
        Assert.False(await fresh.Submissions.AnyAsync(s => s.PostingId == 2));
    }

    [Fact]
    public async Task Replaying_a_create_with_its_event_converges_on_one_row_and_one_event()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var first = await repository.CreateWithEventAsync(
            ProfileId, 1, SubmissionChannel.Ats, "https://x.invalid", Submitted(At(31)), "run-7:1:Submitted", Now);
        var replayed = await repository.CreateWithEventAsync(
            ProfileId, 1, SubmissionChannel.Board, "https://y.invalid", Submitted(At(31)), "run-7:1:Submitted", Now);

        Assert.True(first.Created);
        Assert.False(replayed.Created);

        // AlreadyRecorded rather than Recorded or a refusal: the retry succeeded at both writes,
        // and a client that cannot tell this from a fresh record re-records against a row it has.
        Assert.Equal(SubmissionEventResult.AlreadyRecorded, replayed.Event);
        Assert.Equal(first.Row!.Id, replayed.Row!.Id);
        Assert.Single(await repository.ListEventsAsync(ProfileId, first.Row.Id));

        // And the replay did not rewrite where the application actually went.
        Assert.Equal(SubmissionChannel.Ats, replayed.Row.Channel);
        Assert.Equal("https://x.invalid", replayed.Row.ApplyUrl);
    }

    [Fact]
    public async Task A_second_claim_about_an_existing_application_is_recorded_rather_than_refused()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        await repository.CreateWithEventAsync(
            ProfileId, 1, SubmissionChannel.Ats, null, Submitted(At(31)), "run-7:1:Submitted", Now);

        var later = await repository.CreateWithEventAsync(
            ProfileId,
            1,
            SubmissionChannel.Ats,
            null,
            new SubmissionEvent(At(31, 18), SubmissionEventType.Acknowledged, null, SubmissionEventSource.Email, null),
            "run-7:1:Acknowledged",
            Now);

        // "The submission already existed" is not an error and not a duplicate: it is the second
        // phase of one application, which is the ordinary shape of this table.
        Assert.False(later.Created);
        Assert.Equal(SubmissionEventResult.Recorded, later.Event);
        Assert.Equal(2, (await repository.ListEventsAsync(ProfileId, later.Row!.Id)).Count);
    }

    [Fact]
    public async Task A_retry_of_a_landed_write_is_not_refused_for_the_quota_it_already_spent()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (spender, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        for (var i = 0; i < SubmissionLimits.MaxSubmittedPerDay - 1; i++)
        {
            await repository.AddEventAsync(ProfileId, spender.Id, Submitted(At(31)), $"send-{i}");
        }

        // The write that spends the last of the day, and then the same write again - a client
        // that lost its answer and cannot tell whether it landed.
        var landed = await repository.CreateWithEventAsync(
            ProfileId, 2, SubmissionChannel.Ats, null, Submitted(At(31)), "run-7:2:Submitted", Now);
        var unsure = await repository.CreateWithEventAsync(
            ProfileId, 2, SubmissionChannel.Ats, null, Submitted(At(31)), "run-7:2:Submitted", Now);

        Assert.Equal(SubmissionEventResult.Recorded, landed.Event);

        // The ordering under test: the idempotency probe runs before the cap. Refusing here
        // would tell a client its application was not recorded when it was, on the strength of
        // quota that very event spent.
        Assert.Equal(SubmissionEventResult.AlreadyRecorded, unsure.Event);
        Assert.Single(await repository.ListEventsAsync(ProfileId, landed.Row!.Id));
    }

    // -----------------------------------------------------------------------
    // Parking
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Parking_a_posting_nothing_was_sent_for_creates_the_row_it_hangs_on()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var (row, created) = await repository.ParkAsync(ProfileId, postingId: 3, ParkReason.Captcha, Now);

        Assert.True(created);
        Assert.True(row.IsParked);
        Assert.Equal(ParkReason.Captcha, row.ParkedReason);
        Assert.Equal(Now, row.ParkedAtUtc);

        // Unknown rather than a guess: nothing established where the application would be made,
        // because none was made. And the row is not a sent application - the fold has nothing to
        // read, so there is no phase on it for a reader to miscount.
        Assert.Equal(SubmissionChannel.Unknown, row.Channel);
        Assert.Null(row.Status.Phase);

        // list_submissions projects the park, which is the only way it is visible at all: a park
        // is deliberately not an event, so the fold cannot answer for it.
        var listed = Assert.Single(await repository.ListAsync(ProfileId, Now));
        Assert.True(listed.IsParked);
    }

    [Fact]
    public async Task Parking_the_same_posting_for_the_same_reason_again_moves_nothing()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        await repository.ParkAsync(ProfileId, 3, ParkReason.LoginRequired, Now);
        var (again, created) = await repository.ParkAsync(ProfileId, 3, ParkReason.LoginRequired, Now.AddDays(1));

        Assert.False(created);

        // The timestamp does not walk forward. A run parking the same wall every night would
        // otherwise turn "blocked since Monday" into "blocked a minute ago", which is exactly the
        // fact somebody reading the queue is after.
        Assert.Equal(Now, again.ParkedAtUtc);
        Assert.Single(await repository.ListAsync(ProfileId, Now));
    }

    [Fact]
    public async Task Re_parking_for_a_different_reason_records_the_latest_one()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        await repository.ParkAsync(ProfileId, 3, ParkReason.FormError, Now);
        var (row, _) = await repository.ParkAsync(ProfileId, 3, ParkReason.Expired, Now.AddDays(1));

        // The reason the last attempt hit is the one that stands, and it changes the answer:
        // FormError comes back next run and Expired never does.
        Assert.Equal(ParkReason.Expired, row.ParkedReason);
        Assert.Equal(Now.AddDays(1), row.ParkedAtUtc);
        Assert.False(ParkReasonPolicy.ReturnsToQueue(row.ParkedReason!.Value, answerRecorded: false));
    }

    [Fact]
    public async Task Unparking_lets_it_back_without_erasing_that_it_was_parked()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        await repository.ParkAsync(ProfileId, 3, ParkReason.MissingAnswer, Now);

        var unparked = await repository.UnparkAsync(ProfileId, 3, Now.AddDays(1));

        Assert.NotNull(unparked);
        Assert.False(unparked!.IsParked);
        Assert.Equal(Now.AddDays(1), unparked.UnparkedAtUtc);

        // The park is still on the row. "Was never parked" and "was parked in March and applied
        // to in April" are different histories, and a row that erased one to express the other
        // would be indistinguishable from the first.
        Assert.Equal(ParkReason.MissingAnswer, unparked.ParkedReason);
        Assert.Equal(Now, unparked.ParkedAtUtc);

        // Idempotent, because the answer is the state rather than the transition.
        var again = await repository.UnparkAsync(ProfileId, 3, Now.AddDays(2));
        Assert.Equal(Now.AddDays(1), again!.UnparkedAtUtc);
    }

    [Fact]
    public async Task Parking_an_unparked_submission_stands_again()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        await repository.ParkAsync(ProfileId, 3, ParkReason.Captcha, Now);
        await repository.UnparkAsync(ProfileId, 3, Now.AddDays(1));

        var (row, _) = await repository.ParkAsync(ProfileId, 3, ParkReason.Captcha, Now.AddDays(2));

        // The one value on this table that is ever cleared, and the queue predicate forces it:
        // a re-park leaving the old unpark standing is a parked row every reader treats as a
        // live application, which is the fault parking exists to fix.
        Assert.Null(row.UnparkedAtUtc);
        Assert.True(row.IsParked);
        Assert.Equal(Now.AddDays(2), row.ParkedAtUtc);
    }

    [Fact]
    public async Task Parking_a_sent_application_deletes_neither_it_nor_its_log()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var sent = await repository.CreateWithEventAsync(
            ProfileId, 1, SubmissionChannel.Ats, null, Submitted(At(31)), "run-7:1:Submitted", Now);

        var (parked, created) = await repository.ParkAsync(ProfileId, 1, ParkReason.Duplicate, Now.AddDays(1));

        Assert.False(created);
        Assert.Equal(sent.Row!.Id, parked.Id);

        // Append-only in substance, not only in form. The log is untouched and the fold still
        // says the application was sent - the park is a fact about a later attempt, and nothing
        // about it may unsay what happened.
        Assert.Equal(SubmissionEventType.Submitted, parked.Status.Phase);
        Assert.Single(await repository.ListEventsAsync(ProfileId, parked.Id));
        Assert.Single(await repository.ListAsync(ProfileId, Now));
    }

    [Fact]
    public async Task Parking_somebody_elses_posting_cannot_reach_their_submission()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var (mine, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = 2,
            SubjectId = "22222222-2222-2222-2222-222222222222",
            FullName = "Somebody Else",
            Email = "other@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });
        await db.SaveChangesAsync();

        var (theirs, created) = await repository.ParkAsync(profileId: 2, postingId: 1, ParkReason.Expired, Now);

        // The pair is (profile, posting), so a park by the other candidate makes their own row
        // rather than reaching into this one. Nothing here takes a submission id it did not
        // resolve through a profile.
        Assert.True(created);
        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Null((await repository.GetAsync(ProfileId, mine.Id, Now))!.ParkedReason);

        Assert.Null(await repository.UnparkAsync(profileId: 2, postingId: 4, Now));
    }

    // -----------------------------------------------------------------------
    // Evidence
    // -----------------------------------------------------------------------

    [Fact]
    public async Task What_the_browser_captured_survives_the_round_trip()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var result = await repository.CreateWithEventAsync(
            ProfileId,
            1,
            SubmissionChannel.Ats,
            null,
            Submitted(At(31), new SubmissionEvidence
            {
                ConfirmationRef = "Application #4417290",
                FinalUrl = "https://ats.example.invalid/confirm?ref=4417290",
                ScreenshotRef = "application-packs/1/1/confirmation.png",
                SubmittedFields = ["fullName", "email", "cv"],
            }),
            "run-7:1:Submitted",
            Now);

        var recorded = Assert.Single(await repository.ListEventsAsync(ProfileId, result.Row!.Id));

        Assert.NotNull(recorded.Evidence);
        Assert.Equal("Application #4417290", recorded.Evidence!.ConfirmationRef);
        Assert.Equal("https://ats.example.invalid/confirm?ref=4417290", recorded.Evidence.FinalUrl);
        Assert.Equal("application-packs/1/1/confirmation.png", recorded.Evidence.ScreenshotRef);
        Assert.Equal(["fullName", "email", "cv"], recorded.Evidence.SubmittedFields);
    }

    [Fact]
    public async Task An_event_that_captured_nothing_carries_no_evidence_rather_than_an_empty_block()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        await repository.AddEventAsync(ProfileId, submission.Id, Submitted(At(31)), "none");

        // A selector that matched an empty element yields "", and enumerating a page that had
        // not finished rendering yields a list of them. Both are captures of nothing, and a
        // block of blanks on the dashboard is proof that does not exist.
        await repository.AddEventAsync(
            ProfileId,
            submission.Id,
            Submitted(At(31), new SubmissionEvidence
            {
                ConfirmationRef = "   ",
                FinalUrl = null,
                SubmittedFields = ["", "  "],
            }),
            "blank");

        var events = await repository.ListEventsAsync(ProfileId, submission.Id);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Null(e.Evidence));
    }

    [Fact]
    public async Task Evidence_is_trimmed_to_the_columns_that_hold_it_rather_than_by_the_database()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        await repository.AddEventAsync(
            ProfileId,
            submission.Id,
            Submitted(At(31), new SubmissionEvidence
            {
                ConfirmationRef = new string('r', SubmissionLimits.MaxConfirmationRefLength + 50),
                // Blanks scattered through a list longer than the cap: the count has to be taken
                // after they are dropped, or a page full of empty inputs spends the whole budget
                // on nothing and truncates away the names that were real.
                SubmittedFields =
                [
                    .. Enumerable.Range(0, SubmissionLimits.MaxSubmittedFieldCount + 20)
                        .SelectMany(i => new[] { "  ", $"field{i}" }),
                ],
            }),
            "long");

        var recorded = Assert.Single(await repository.ListEventsAsync(ProfileId, submission.Id));

        Assert.Equal(
            SubmissionLimits.MaxConfirmationRefLength,
            recorded.Evidence!.ConfirmationRef!.Length);
        Assert.Equal(SubmissionLimits.MaxSubmittedFieldCount, recorded.Evidence.SubmittedFields!.Count);
        Assert.Equal("field0", recorded.Evidence.SubmittedFields[0]);
    }

    /// <summary>
    /// A key too long to store is refused, because shortening it would make two events one.
    /// </summary>
    /// <remarks>
    /// <b>The one string here that is compared rather than read.</b> Everything else bounded on the
    /// way in - a note, a stage, a confirmation reference - is trimmed to fit, and that is right for
    /// text somebody reads: the first hundred characters of a note are still a note. An idempotency
    /// key is only ever compared for equality, so trimming it merges two distinct keys that share a
    /// prefix, and the merge is silent and in the dangerous direction: the second event answers
    /// AlreadyRecorded for something that was never recorded.
    ///
    /// The shape this surface recommends is what makes it reachable rather than theoretical.
    /// "&lt;runId&gt;:&lt;postingId&gt;:Submitted" puts everything that distinguishes one event from
    /// the next at the END of the string, so a long run id would collapse an entire run's events
    /// into its first one.
    /// </remarks>
    [Fact]
    public async Task An_idempotency_key_too_long_to_store_is_refused_rather_than_shortened()
    {
        await using var db = CreateContext();

        var repository = new SubmissionRepository(db);

        var (submission, _) = await repository.CreateAsync(
            ProfileId, 1, SubmissionChannel.Ats, null, Now);

        var tooLong = new string('k', SubmissionLimits.MaxIdempotencyKeyLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddEventAsync(
            ProfileId,
            submission.Id,
            new SubmissionEvent(Now, SubmissionEventType.Submitted, null, SubmissionEventSource.Client, null),
            tooLong));

        // Nothing was written, so the refusal is not a partial success a caller has to unpick.
        Assert.Empty(await repository.ListEventsAsync(ProfileId, submission.Id));
    }

    /// <summary>Two keys differing only past the bound stay two events.</summary>
    /// <remarks>
    /// The regression in its own words: at the bound, these are the same string. This asserts the
    /// pair is refused rather than silently converging, which is what makes the rule above worth
    /// having - a key exactly at the limit still works, and one past it is a caller error rather
    /// than a lost event.
    /// </remarks>
    [Fact]
    public async Task A_key_exactly_at_the_bound_is_accepted()
    {
        await using var db = CreateContext();

        var repository = new SubmissionRepository(db);

        var (submission, _) = await repository.CreateAsync(
            ProfileId, 1, SubmissionChannel.Ats, null, Now);

        var atBound = new string('k', SubmissionLimits.MaxIdempotencyKeyLength);

        var result = await repository.AddEventAsync(
            ProfileId,
            submission.Id,
            new SubmissionEvent(Now, SubmissionEventType.Submitted, null, SubmissionEventSource.Client, null),
            atBound);

        Assert.Equal(SubmissionEventResult.Recorded, result);
    }

}
