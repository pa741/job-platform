using JobPlatform.Core.Matching;
using JobPlatform.Core.Model;
using JobPlatform.Core.Profiles;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using JobPlatform.Ingestion.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobPlatform.Ingestion.Tests;

/// <summary>
/// Where the nightly judgement budget actually goes.
/// </summary>
/// <remarks>
/// <b>Against SQLite rather than a stubbed repository, for the reason
/// <c>GenerateApplicationsTests</c> gives.</b> The claim being tested is about a selection over
/// the corpus, and a test that handed the sweep a list would assert nothing about the selection
/// and would keep passing on the day the query and the intent drifted apart.
///
/// <b>What is pinned.</b> The system exists to answer a day's postings on the day they appear, so
/// a fixed share of each night goes to postings inside <c>PostingAge.DailyWindowDays</c> even when
/// every one of them scores below the backlog above it. That is the whole change, and it is
/// invisible in a summary count - forty judgements were bought either way - so it is asserted on
/// which postings were sent, not on how many.
///
/// <b>And what it must not become.</b> The share is a reservation and not an ordering: the
/// backlog still gets the rest of the budget, a night with nothing new spends the whole budget on
/// the backlog, and recency never carries a pair over the threshold that decides whether a
/// judgement is worth buying at all. Each of those is a way this could have been written that
/// looks the same on a good day and starves something on a bad one.
///
/// The profile deliberately holds no concepts, so the scoring pass skips it and the scores in the
/// fixture are the ones the selection runs against. Scoring is tested where it lives.
/// </remarks>
public sealed class MatchSweepShortlistTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    /// <summary>Half past three in the morning, which is when the timer fires.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 3, 30, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const string SubjectId = "55555555-5555-5555-5555-555555555555";

    /// <summary>Postings dated today. Twenty-five of them, ids 100-124.</summary>
    private const long FirstRecent = 100;

    /// <summary>A posting dated today that the arithmetic put below the threshold.</summary>
    private const long RecentAndWeak = 150;

    /// <summary>Postings dated a month ago, scoring higher. Twenty-five of them, ids 200-224.</summary>
    private const long FirstOld = 200;

    private const int Cohort = 25;

    /// <summary>What the nightly sweep is allowed to buy, before the measurement sample.</summary>
    private const int ShortlistBudget = 30;

    /// <summary>Two thirds of it, which is what the recent cohort is owed.</summary>
    private const int RecentShare = 20;

    public MatchSweepShortlistTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = SubjectId,
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
            // What makes the sweep consider this profile at all. Extraction has run and found no
            // concepts, which is also what keeps the scoring pass off the fixture's scores: a
            // profile holding nothing scores nothing, and the selection is what is under test.
            ExtractedAtUtc = Now,
        });

        var month = Now.AddDays(-30);

        for (var i = 0; i < Cohort; i++)
        {
            // Today's postings, scoring below the backlog. The wrong way round on purpose: if
            // recency were only a tie-break, none of these would ever be judged.
            Add(db, FirstRecent + i, DateOnly.FromDateTime(Now.UtcDateTime), Now, score: 90);

            Add(db, FirstOld + i, DateOnly.FromDateTime(month.UtcDateTime), month, score: 95);
        }

        Add(db, RecentAndWeak, DateOnly.FromDateTime(Now.UtcDateTime), Now, score: 40);

        db.SaveChanges();

        static void Add(
            JobsDbContext db, long id, DateOnly datePosted, DateTimeOffset firstSeen, int score)
        {
            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"test:{id}",
                Site = "test",
                ExternalId = id.ToString(),
                ContentHash = new string((char)('a' + (id % 20)), 64),
                Title = $"Role {id}",
                Description = $"An advert for role {id}.",
                DescriptionLength = 24,
                DatePosted = datePosted,
                FirstSeenUtc = firstSeen,
                LastSeenUtc = Now,
            });

            db.JobMatches.Add(new JobMatchEntity
            {
                ProfileId = ProfileId,
                PostingId = id,
                Score = score,
                RankScore = score,
                ScoredAtUtc = Now,
            });
        }
    }

    public void Dispose() => _connection.Dispose();

    // -----------------------------------------------------------------------

    [Fact]
    public async Task Two_thirds_of_the_nightly_budget_goes_to_postings_from_the_last_few_days()
    {
        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        var judged = Judged(assessor);

        Assert.Equal(RecentShare, judged.Count(Recent));

        // The other third is the backlog, and it is the half that stops this being an ordering.
        // Everything old here outscores everything recent, so a sweep that had simply sorted by
        // age would show thirty recent postings and no sign that anything was wrong.
        Assert.Equal(ShortlistBudget - RecentShare, judged.Count(id => !Recent(id)));
    }

    [Fact]
    public async Task No_posting_is_paid_for_twice_because_it_is_both_recent_and_top_scoring()
    {
        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        var judged = Judged(assessor);

        // The two draws overlap by design - the top-down one is asked for the whole shortlist
        // rather than for what recency left - so the merge is the only thing standing between
        // this and a night that buys the same judgement twice.
        Assert.Equal(judged.Length, judged.Distinct().Count());
        Assert.Equal(ShortlistBudget, judged.Length);
    }

    [Fact]
    public async Task Recency_never_carries_a_pair_over_the_threshold()
    {
        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        // Posted today and scored 40. The window decides which rows are eligible for the reserved
        // share; it is not a way past the number that decides whether a judgement is worth buying,
        // and a sweep that spent the morning on adverts the arithmetic had already rejected would
        // be the most expensive possible reading of "prioritise recent postings".
        Assert.DoesNotContain(RecentAndWeak, Judged(assessor));
    }

    [Fact]
    public async Task A_night_with_nothing_new_spends_the_whole_budget_on_the_backlog()
    {
        await using (var db = CreateContext())
        {
            // The market goes quiet: everything is a month old. The reservation must cost nothing
            // here - an empty recent draw that still held its twenty slots would cut the night's
            // work by two thirds and nothing in the summary would say why.
            await db.JobPostings
                .Where(p => p.DatePosted > new DateOnly(2026, 8, 20))
                .ExecuteUpdateAsync(p => p
                    .SetProperty(x => x.DatePosted, new DateOnly(2026, 8, 1))
                    .SetProperty(x => x.FirstSeenUtc, Now.AddDays(-38)));
        }

        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        var judged = Judged(assessor);

        Assert.Equal(ShortlistBudget, judged.Length);
        Assert.DoesNotContain(RecentAndWeak, judged);
    }

    // -----------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------

    private JobsDbContext CreateContext() => new(_options);

    private static bool Recent(long postingId)
        => postingId >= FirstRecent && postingId < FirstRecent + Cohort;

    /// <summary>The postings this sweep actually sent to the model, in the order it sent them.</summary>
    private static long[] Judged(RecordingAssessor assessor)
        => [.. assessor.Requested.SelectMany(batch => batch).Select(r => r.PostingId)];

    private async Task RunAsync(RecordingAssessor assessor)
    {
        await using var db = CreateContext();

        var function = new MatchSweepFunction(
            db,
            new CandidateProfileRepository(db),
            new JobMatchRepository(db),
            new EmbeddingRepository(db),
            new FakeTime(Now),
            NullLogger<MatchSweepFunction>.Instance,
            assessor);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);
    }

    /// <summary>
    /// An assessor that records what it was asked and judges everything.
    /// </summary>
    /// <remarks>
    /// It answers rather than returning nulls, so the rows it saw leave the unassessed set - a
    /// stub that judged nothing would leave every one of them eligible again and could not tell a
    /// resumed sweep from a repeated one.
    /// </remarks>
    private sealed class RecordingAssessor : ICandidacyAssessor
    {
        public List<IReadOnlyList<CandidacyRequest>> Requested { get; } = [];

        public Task<IReadOnlyList<CandidacyAssessment?>> AssessAsync(
            CandidateProfile profile,
            IReadOnlyList<CandidacyRequest> requests,
            CancellationToken ct = default)
        {
            Requested.Add(requests);

            IReadOnlyList<CandidacyAssessment?> answers =
            [
                .. requests.Select(_ => (CandidacyAssessment?)new CandidacyAssessment
                {
                    Verdict = CandidacyVerdict.Possible,
                    Score = 70,
                    Version = CandidacyAssessment.CurrentVersion,
                }),
            ];

            return Task.FromResult(answers);
        }
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
