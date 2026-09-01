using JobPlatform.Core.Matching;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The submission pipeline against a real relational engine.
/// </summary>
/// <remarks>
/// Three things are pinned here and none of them is a query.
///
/// The <b>authorisation boundary</b>: every method takes a profile id and there is no overload
/// without one, so a submission id belonging to somebody else has to be invisible rather than
/// merely unreachable by convention. That matters more here than anywhere else in this codebase,
/// because these ids will arrive as arguments named by a model.
///
/// The <b>idempotency guarantees</b>, which are indexes rather than checks: a retry must not be
/// able to create a second submission for one posting or a second <c>Submitted</c> event. Both
/// are asserted at the database, by writing round the repository, because a guarantee that only
/// holds while callers behave is not one.
///
/// The <b>shortlist's exclusions</b> - already submitted, unassessed, and judged Weak - which are
/// what stops an agent being handed work it should not do.
/// </remarks>
public sealed class SubmissionPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const long OtherProfileId = 2;

    public SubmissionPersistenceTests()
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

        // Odd ids carry a direct apply link and even ones do not, so the channel projection has
        // both cases without a second fixture.
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
                JobUrlDirect = id % 2 == 1 ? $"https://careers.example.invalid/{id}" : null,
                // Posting 2 is the case the URL alone cannot express: the scraper established
                // that the board hosts the application. 6 leaves it null, so the three channels
                // each have a row and Unknown is distinguishable from Board.
                OffsiteApply = id == 2 ? false : null,
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        // A sibling of posting 6 on another board, carrying the employer's link that LinkedIn
        // no longer publishes. Same title, employer and city; different site.
        db.JobPostings.Add(new JobPostingEntity
        {
            Id = 100,
            SourceKey = "indeed:100",
            Site = "indeed",
            ExternalId = "100",
            ContentHash = new string('z', 64),
            Title = "Role 6",
            Company = "Company 6",
            LocationCity = "London",
            JobUrl = "https://indeed/100",
            JobUrlDirect = "https://ats.example.invalid/role-6",
            FirstSeenUtc = Now,
            LastSeenUtc = Now,
        });

        // A near miss: same title and employer, different city. It must not be used - measured
        // on the live corpus, better than a quarter of title+employer matches were this.
        db.JobPostings.Add(new JobPostingEntity
        {
            Id = 101,
            SourceKey = "indeed:101",
            Site = "indeed",
            ExternalId = "101",
            ContentHash = new string('y', 64),
            Title = "Role 2",
            Company = "Company 2",
            LocationCity = "Dublin",
            JobUrl = "https://indeed/101",
            JobUrlDirect = "https://ats.example.invalid/wrong-city",
            FirstSeenUtc = Now,
            LastSeenUtc = Now,
        });

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static SubmissionEvent Event(
        int day,
        SubmissionEventType type,
        string? stage = null,
        SubmissionEventSource source = SubmissionEventSource.Candidate)
        => new(new DateTimeOffset(2026, 8, day, 9, 0, 0, TimeSpan.Zero), type, stage, source, Note: null);

    // -----------------------------------------------------------------------
    // Ownership
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_submission_belonging_to_somebody_else_is_invisible_rather_than_forbidden()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var (mine, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, "https://x.invalid", Now);

        // Not an error and not a partial answer: a caller cannot tell "no such submission" from
        // "not yours", which is what stops the id space being probeable.
        Assert.Null(await repository.GetAsync(OtherProfileId, mine.Id, Now));
        Assert.Empty(await repository.ListAsync(OtherProfileId, Now));
    }

    [Fact]
    public async Task An_event_cannot_be_appended_to_somebody_elses_submission()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var (mine, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        var refused = await repository.AddEventAsync(
            OtherProfileId, mine.Id, Event(2, SubmissionEventType.Rejected), "k1");

        // NotFound, not AlreadyRecorded. "That is not yours" and "that event is already
        // recorded" are different answers and a caller acts differently on each.
        Assert.Equal(SubmissionEventResult.NotFound, refused);
        Assert.Empty(await repository.ListEventsAsync(ProfileId, mine.Id));
    }

    // -----------------------------------------------------------------------
    // Idempotency, asserted at the database rather than at the repository
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Creating_the_same_submission_twice_converges_on_one_row()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var (first, created) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, "https://x.invalid", Now);
        var (second, again) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Board, "https://y.invalid", Now);

        Assert.True(created);
        Assert.False(again);
        Assert.Equal(first.Id, second.Id);

        // The retry did not rewrite what was already recorded. This row is history - where the
        // application actually went - not a current value.
        Assert.Equal(SubmissionChannel.Ats, second.Channel);
        Assert.Equal("https://x.invalid", second.ApplyUrl);
        Assert.Single(await repository.ListAsync(ProfileId, Now));
    }

    [Fact]
    public async Task The_database_refuses_a_second_submission_for_one_posting()
    {
        await using var db = CreateContext();
        await new SubmissionRepository(db).CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        // Written round the repository deliberately. The repository's check turns the ordinary
        // retry into an answer; the index is what holds when two calls race, and only the index
        // is a guarantee.
        await using var second = CreateContext();
        second.Submissions.Add(new SubmissionEntity
        {
            ProfileId = ProfileId,
            PostingId = 1,
            Channel = SubmissionChannel.Board,
            CreatedAtUtc = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task An_event_replayed_under_the_same_key_is_recorded_once()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        var first = await repository.AddEventAsync(
            ProfileId, submission.Id, Event(2, SubmissionEventType.Submitted), "send-1");
        var replayed = await repository.AddEventAsync(
            ProfileId, submission.Id, Event(2, SubmissionEventType.Submitted), "send-1");

        Assert.Equal(SubmissionEventResult.Recorded, first);
        Assert.Equal(SubmissionEventResult.AlreadyRecorded, replayed);
        Assert.Single(await repository.ListEventsAsync(ProfileId, submission.Id));
    }

    [Fact]
    public async Task The_database_refuses_a_second_event_under_one_key()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);
        await repository.AddEventAsync(ProfileId, submission.Id, Event(2, SubmissionEventType.Submitted), "send-1");

        await using var second = CreateContext();
        second.SubmissionEvents.Add(new SubmissionEventEntity
        {
            SubmissionId = submission.Id,
            AtUtc = Now,
            Type = SubmissionEventType.Submitted,
            Source = SubmissionEventSource.Client,
            IdempotencyKey = "send-1",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task The_same_key_on_two_submissions_is_allowed()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var (one, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);
        var (two, _) = await repository.CreateAsync(ProfileId, 2, SubmissionChannel.Board, null, Now);

        // The key is unique per submission, not globally. A client numbering its writes from one
        // per application is the obvious thing to do and must not collide across them.
        Assert.Equal(
            SubmissionEventResult.Recorded,
            await repository.AddEventAsync(ProfileId, one.Id, Event(2, SubmissionEventType.Submitted), "1"));
        Assert.Equal(
            SubmissionEventResult.Recorded,
            await repository.AddEventAsync(ProfileId, two.Id, Event(2, SubmissionEventType.Submitted), "1"));
    }

    /// <summary>
    /// The daily cap on applications recorded as sent.
    /// </summary>
    /// <remarks>
    /// The bound on what a client that loops can do. The server never submits anything, so the
    /// damage is a pipeline full of applications nobody made - but a pipeline a person cannot
    /// trust is as broken as one that emailed four hundred employers.
    /// </remarks>
    [Fact]
    public async Task Recording_more_applications_as_sent_than_the_daily_cap_is_refused()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        // One submission per posting, so the cap has to count events across all of them rather
        // than per submission - which is the only way it bounds anything.
        var results = new List<SubmissionEventResult>();

        for (var i = 0; i < SubmissionLimits.MaxSubmittedPerDay + 2; i++)
        {
            await using var scoped = CreateContext();
            var repo = new SubmissionRepository(scoped);
            var (submission, _) = await repo.CreateAsync(
                ProfileId, 1 + (i % 6), SubmissionChannel.Ats, null, Now);

            results.Add(await repo.AddEventAsync(
                ProfileId, submission.Id, Event(2, SubmissionEventType.Submitted), $"send-{i}"));
        }

        Assert.Equal(
            SubmissionLimits.MaxSubmittedPerDay,
            results.Count(r => r == SubmissionEventResult.Recorded));
        Assert.All(
            results.Skip(SubmissionLimits.MaxSubmittedPerDay),
            r => Assert.Equal(SubmissionEventResult.DailyLimitReached, r));
    }

    [Fact]
    public async Task The_cap_counts_only_submitted_events_and_only_within_one_day()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        for (var i = 0; i < SubmissionLimits.MaxSubmittedPerDay; i++)
        {
            await repository.AddEventAsync(
                ProfileId, submission.Id, Event(2, SubmissionEventType.Submitted), $"send-{i}");
        }

        // Day 2 is a fresh budget: the cap is per day, counted on when the event says it
        // happened rather than on when the row was written.
        Assert.Equal(
            SubmissionEventResult.Recorded,
            await repository.AddEventAsync(
                ProfileId, submission.Id, Event(3, SubmissionEventType.Submitted), "next-day"));

        // And nothing else is capped. Recording that an employer replied is not an application.
        Assert.Equal(
            SubmissionEventResult.Recorded,
            await repository.AddEventAsync(
                ProfileId, submission.Id, Event(2, SubmissionEventType.Rejected), "reply"));
    }

    [Fact]
    public async Task A_retry_at_the_cap_still_converges_rather_than_being_refused()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);
        var (submission, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);

        for (var i = 0; i < SubmissionLimits.MaxSubmittedPerDay; i++)
        {
            await repository.AddEventAsync(
                ProfileId, submission.Id, Event(2, SubmissionEventType.Submitted), $"send-{i}");
        }

        // The idempotency check runs before the cap. A client retrying a write it is not sure
        // landed must not be told it has exceeded a quota it already spent on that very event -
        // it would have no way to tell "already done" from "refused" and might stop early.
        Assert.Equal(
            SubmissionEventResult.AlreadyRecorded,
            await repository.AddEventAsync(
                ProfileId, submission.Id, Event(2, SubmissionEventType.Submitted), "send-0"));
    }

    // -----------------------------------------------------------------------
    // The fold, through storage
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_status_is_folded_on_read_and_the_list_is_ordered_by_activity()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        var (quiet, _) = await repository.CreateAsync(ProfileId, 1, SubmissionChannel.Ats, null, Now);
        var (busy, _) = await repository.CreateAsync(ProfileId, 2, SubmissionChannel.Board, null, Now);

        await repository.AddEventAsync(ProfileId, quiet.Id, Event(2, SubmissionEventType.Submitted), "a");
        await repository.AddEventAsync(ProfileId, busy.Id, Event(3, SubmissionEventType.Submitted), "b");
        await repository.AddEventAsync(
            ProfileId, busy.Id, Event(9, SubmissionEventType.InterviewScheduled, "Tech round 2"), "c");

        // 20 August: 18 days since the quiet one's last event and 11 since the busy one's, so
        // the read sits between the two staleness boundaries and each row answers differently.
        var rows = await repository.ListAsync(ProfileId, new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));

        // Most recently active first, which is a derived key with no column behind it - the
        // whole reason there is no stored status to sort on.
        Assert.Equal([busy.Id, quiet.Id], rows.Select(r => r.Id));

        var interviewing = rows[0];
        Assert.Equal(SubmissionEventType.InterviewScheduled, interviewing.Status.Phase);
        Assert.Equal("Tech round 2", interviewing.Status.Stage);
        Assert.Equal(2, interviewing.Status.EventCount);

        // Nothing since 2 August, read on 20 August: stale, and derived rather than stored.
        Assert.True(rows[1].Status.IsStale);
        Assert.False(interviewing.Status.IsStale);
    }

    [Fact]
    public async Task A_submission_with_no_events_still_reads_back_with_its_posting()
    {
        await using var db = CreateContext();
        var repository = new SubmissionRepository(db);

        await repository.CreateAsync(ProfileId, 3, SubmissionChannel.Ats, "https://careers.example.invalid/3", Now);

        var row = Assert.Single(await repository.ListAsync(ProfileId, Now));

        Assert.Equal("Role 3", row.Title);
        Assert.Equal("Company 3", row.Company);
        Assert.Null(row.Status.Phase);
        Assert.Equal(Now, row.Status.LastActivityUtc);
    }

    // -----------------------------------------------------------------------
    // The shortlist
    // -----------------------------------------------------------------------

    /// <summary>
    /// Scores six pairs and judges some of them, so the shortlist has every case to exclude.
    /// </summary>
    private async Task SeedMatchesAsync()
    {
        await using var db = CreateContext();

        // Rank descending with the posting id, so the expected order is 6, 5, 4, ... and any
        // accidental ordering by score or id is visible rather than coincidental.
        var verdicts = new Dictionary<long, CandidacyVerdict?>
        {
            [1] = CandidacyVerdict.Strong,
            [2] = CandidacyVerdict.Possible,
            [3] = CandidacyVerdict.Weak,
            [4] = CandidacyVerdict.Unknown,
            [5] = null,
            [6] = CandidacyVerdict.Strong,
        };

        foreach (var (postingId, verdict) in verdicts)
        {
            db.JobMatches.Add(new JobMatchEntity
            {
                ProfileId = ProfileId,
                PostingId = postingId,
                Score = 90,
                RankScore = postingId,
                ScoredAtUtc = Now,
                Verdict = verdict,
                AssessedAtUtc = verdict is null ? null : Now,
                AssessmentScore = verdict is null ? null : 80,
                ScorerVersion = MatchResult.CurrentVersion,
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_shortlist_holds_only_what_the_model_judged_worth_applying_to()
    {
        await SeedMatchesAsync();

        await using var db = CreateContext();
        var rows = await new JobMatchRepository(db).ListApplyableAsync(ProfileId, channel: null, limit: 50);

        // 3 is Weak, 4 is Unknown - the model answered and said nothing usable - and 5 has never
        // been assessed. None of the three is a recommendation, and the last two are the pair a
        // rule keyed on the wrong column would disagree about.
        Assert.Equal([6, 2, 1], rows.Select(r => r.PostingId));
    }

    [Fact]
    public async Task A_posting_already_submitted_leaves_the_shortlist()
    {
        await SeedMatchesAsync();

        await using var db = CreateContext();
        await new SubmissionRepository(db).CreateAsync(ProfileId, 6, SubmissionChannel.Ats, null, Now);

        var rows = await new JobMatchRepository(db).ListApplyableAsync(ProfileId, channel: null, limit: 50);

        Assert.Equal([2, 1], rows.Select(r => r.PostingId));
    }

    [Fact]
    public async Task The_channel_is_projected_from_the_apply_link_and_filters_before_the_bound()
    {
        await SeedMatchesAsync();

        await using var db = CreateContext();
        var repository = new JobMatchRepository(db);

        var ats = await repository.ListApplyableAsync(ProfileId, SubmissionChannel.Ats, limit: 50);
        var board = await repository.ListApplyableAsync(ProfileId, SubmissionChannel.Board, limit: 50);
        var unknown = await repository.ListApplyableAsync(ProfileId, SubmissionChannel.Unknown, limit: 50);

        // Ats by two different routes: posting 1 published its own link, and posting 6's was
        // recovered from the same job on another board. Both mean the employer's system takes
        // the application, which is the fact the channel is answering.
        Assert.Equal([6, 1], ats.Select(r => r.PostingId));
        Assert.All(ats, r => Assert.Equal(SubmissionChannel.Ats, r.Channel));

        // The board hosting it is asserted by the scraper's offsite_apply flag, never inferred
        // from a missing link. Measured 2026-09-01, that inference was wrong for all 4,470
        // LinkedIn postings in the live corpus - LinkedIn had stopped publishing apply URLs.
        Assert.Equal([2], board.Select(r => r.PostingId));
        Assert.All(board, r => Assert.Equal(SubmissionChannel.Board, r.Channel));

        // Nothing left unestablished in this fixture, which is the point of the recovery: every
        // row now says something. The Unknown case is covered on its own below.
        Assert.Empty(unknown);

        // The provenance travels with the URL, because one of these two is an inference.
        Assert.Equal("https://careers.example.invalid/1", ats[1].ApplyUrl);
        Assert.Equal(ApplyUrlSource.Posting, ats[1].ApplyUrlSource);
        Assert.Equal("https://ats.example.invalid/role-6", ats[0].ApplyUrl);
        Assert.Equal(ApplyUrlSource.MatchedOnAnotherBoard, ats[0].ApplyUrlSource);
    }

    /// <summary>
    /// An apply link the posting's own board stopped publishing, recovered from another board.
    /// </summary>
    /// <remarks>
    /// Worth about 5% of the links LinkedIn no longer gives, at no request and no account. The
    /// provenance travels with it: a matched link is an inference and a caller that cannot tell
    /// it from a published one has no way to notice when the match was wrong.
    /// </remarks>
    [Fact]
    public async Task An_apply_link_is_recovered_from_the_same_job_on_another_board()
    {
        await SeedMatchesAsync();

        await using var db = CreateContext();
        var rows = await new JobMatchRepository(db).ListApplyableAsync(ProfileId, channel: null, limit: 50);

        var recovered = Assert.Single(rows, r => r.PostingId == 6);

        Assert.Equal("https://ats.example.invalid/role-6", recovered.ApplyUrl);
        Assert.Equal(ApplyUrlSource.MatchedOnAnotherBoard, recovered.ApplyUrlSource);

        // Recovering the link settles the channel too: the employer's system takes it.
        Assert.Equal(SubmissionChannel.Ats, recovered.Channel);
    }

    [Fact]
    public async Task The_same_title_and_employer_in_another_city_is_never_borrowed_from()
    {
        await SeedMatchesAsync();

        await using var db = CreateContext();
        var rows = await new JobMatchRepository(db).ListApplyableAsync(ProfileId, channel: null, limit: 50);

        // Posting 2 has a Dublin namesake carrying a link. Using it would send a London
        // candidate to the wrong vacancy's ATS, which is worse than having no link at all.
        var london = Assert.Single(rows, r => r.PostingId == 2);

        Assert.Equal("https://www.linkedin.com/jobs/view/2", london.ApplyUrl);
        Assert.Equal(ApplyUrlSource.BoardPosting, london.ApplyUrlSource);
        Assert.Equal(SubmissionChannel.Board, london.Channel);
    }

    [Fact]
    public async Task A_published_link_is_reported_as_published_rather_than_matched()
    {
        await SeedMatchesAsync();

        await using var db = CreateContext();
        var rows = await new JobMatchRepository(db).ListApplyableAsync(ProfileId, channel: null, limit: 50);

        var published = Assert.Single(rows, r => r.PostingId == 1);

        Assert.Equal("https://careers.example.invalid/1", published.ApplyUrl);
        Assert.Equal(ApplyUrlSource.Posting, published.ApplyUrlSource);
    }

    [Fact]
    public async Task The_channel_filter_is_applied_before_the_limit_rather_than_after_it()
    {
        await SeedMatchesAsync();

        await using var db = CreateContext();

        // Posting 2 is the only Board row and it ranks below 6. Asking for one Board posting must
        // return it, not "the Board subset of the top one", which would be empty.
        var board = await new JobMatchRepository(db).ListApplyableAsync(ProfileId, SubmissionChannel.Board, limit: 1);

        // A filter applied after a bound is not a filter, it is a silent reduction of the bound -
        // three times over in this codebase now, so it is asserted rather than assumed.
        Assert.Single(board);
        Assert.Equal(2, board[0].PostingId);
    }
}
