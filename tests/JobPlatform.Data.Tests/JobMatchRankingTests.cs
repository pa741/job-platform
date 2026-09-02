using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// How the ranking is stored and read back, against a real relational engine.
/// </summary>
/// <remarks>
/// Three things here fail silently and nothing else catches them. The list has to come back in
/// rank order rather than score order, or the whole measurement behind <c>MatchRanker</c> is
/// computed and then thrown away by an ORDER BY. A rank that moves must not clear the assessment,
/// or re-sorting the page costs the model judgements it was fitted on. And an unchanged pair must
/// not be rewritten, or the nightly sweep touches every row it looked at on a database billed by
/// wall-clock time.
/// </remarks>
public sealed class JobMatchRankingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 8, 28, 3, 30, 0, TimeSpan.Zero);

    private const long ProfileId = 1;

    public JobMatchRankingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = "33333333-3333-3333-3333-333333333333",
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });

        // Ids 1-3 have NO description; 4-6 do. The order matters: a band is drawn by posting id
        // ascending, so putting the unusable rows at the head is what reproduces the starvation -
        // with them anywhere else, Take() never reaches them and the bug is invisible.
        //
        // A posting with no description cannot be assessed, and the low score bands are full of
        // them: no description means no concepts resolved, which means the concept floor scores
        // it at zero.
        for (var id = 1; id <= 6; id++)
        {
            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"test:{id}",
                Site = "test",
                ExternalId = id.ToString(),
                ContentHash = new string((char)('a' + id), 64),
                Title = $"Role {id}",
                Description = id <= 3 ? null : $"Advert {id}",
                DescriptionLength = id <= 3 ? 0 : 9,
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static PostingFacts Posting(long id) => new() { PostingId = id };

    private static MatchResult Result(int score) => new() { Score = score, Coverage = 0.5 };

    private static (PostingFacts, MatchResult)[] Scores(params (long Id, int Score)[] pairs)
        => [.. pairs.Select(p => (Posting(p.Id), Result(p.Score)))];

    private static async Task<int> WriteAsync(
        JobsDbContext db,
        (PostingFacts, MatchResult)[] scores,
        IReadOnlyList<RankedMatch> ranking,
        DateTimeOffset? at = null)
        => await new JobMatchRepository(db)
            .UpsertScoresAsync(ProfileId, scores, ranking, at ?? Now);

    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_list_comes_back_in_rank_order_not_score_order()
    {
        // The pair the score puts first is the one the embedding demotes. If the ORDER BY still
        // reads Score, every number MatchRanker computes is discarded at the last step.
        var scores = Scores((1, 100), (2, 90));
        var ranking = MatchRanker.Rank([new(1, 100, 0.10), new(2, 90, 0.90)]);

        await using (var db = CreateContext())
        {
            await WriteAsync(db, scores, ranking);
        }

        await using (var db = CreateContext())
        {
            var rows = await new JobMatchRepository(db)
                .ListAsync(ProfileId, minimumScore: 0, assessedOnly: false, limit: 10, offset: 0);

            Assert.Equal([2L, 1L], [.. rows.Select(r => r.PostingId)]);
            Assert.Equal(100, rows[1].Score);
        }
    }

    [Fact]
    public async Task Similarity_and_rank_survive_the_round_trip()
    {
        var ranking = MatchRanker.Rank([new(1, 90, 0.4321), new(2, 50, 0.1234)]);

        await using (var db = CreateContext())
        {
            await WriteAsync(db, Scores((1, 90), (2, 50)), ranking);
        }

        await using (var db = CreateContext())
        {
            var row = await new JobMatchRepository(db).GetDetailAsync(ProfileId, 1);

            Assert.NotNull(row);
            Assert.Equal(0.4321, row!.Similarity);
            Assert.Equal(ranking.Single(r => r.PostingId == 1).RankScore, row.RankScore);
        }
    }

    [Fact]
    public async Task An_unchanged_pair_is_not_rewritten()
    {
        // The property the sweep depends on: a night where nothing moved must touch no rows. It
        // is also why MatchRanker rounds - at full precision the key would drift every night and
        // this would never pass again.
        var scores = Scores((1, 90), (2, 50));
        var ranking = MatchRanker.Rank([new(1, 90, 0.8), new(2, 50, 0.2)]);

        await using (var db = CreateContext())
        {
            Assert.Equal(2, await WriteAsync(db, scores, ranking));
        }

        await using (var db = CreateContext())
        {
            Assert.Equal(0, await WriteAsync(db, scores, ranking));
        }
    }

    [Fact]
    public async Task A_moved_rank_alone_is_written()
    {
        var scores = Scores((1, 90), (2, 50));

        await using (var db = CreateContext())
        {
            await WriteAsync(db, scores, MatchRanker.Rank([new(1, 90, 0.8), new(2, 50, 0.2)]));
        }

        await using (var db = CreateContext())
        {
            // Same scores, different similarities: only the ordering moved, and it still has to
            // reach the table or the page keeps yesterday's order.
            Assert.Equal(2, await WriteAsync(db, scores, MatchRanker.Rank([new(1, 90, 0.2), new(2, 50, 0.8)])));
        }
    }

    [Fact]
    public async Task A_moved_rank_does_not_clear_the_assessment()
    {
        // A re-score that moves the number clears the judgement, because it was made against
        // different arithmetic. Re-sorting the page is not that, and treating it as if it were
        // would spend the model's budget again for a different ORDER BY.
        await using (var db = CreateContext())
        {
            await WriteAsync(db, Scores((1, 90)), MatchRanker.Rank([new(1, 90, 0.8)]));

            await new JobMatchRepository(db).ApplyAssessmentsAsync(
                ProfileId,
                [(1L, new CandidacyAssessment
                {
                    Verdict = CandidacyVerdict.Strong,
                    Score = 88,
                    Rationale = "A good fit.",
                })],
                Now);
        }

        await using (var db = CreateContext())
        {
            await WriteAsync(db, Scores((1, 90)), MatchRanker.Rank([new(1, 90, 0.1)]));
        }

        await using (var db = CreateContext())
        {
            var row = await new JobMatchRepository(db).GetDetailAsync(ProfileId, 1);

            Assert.Equal(CandidacyVerdict.Strong, row!.Verdict);
            Assert.Equal(88, row.AssessmentScore);
        }
    }

    [Fact]
    public async Task A_band_draw_skips_postings_with_no_description_rather_than_returning_fewer()
    {
        // The starvation this fixes. A band is ordered by posting id, so the unusable rows sit at
        // its head; filtering them after the Take means a request for two rows returns none, and
        // because they are never assessed they never leave the unassessed set - so the next draw
        // fetches exactly the same dead rows. Measured in production on 2026-08-30, the 60-69
        // band returned nothing at a limit of five and five usable rows at a limit of ten.
        //
        // Ids 1, 2 and 3 have no description and are the lowest ids in this band, so a query
        // that filters after the Take returns an empty list here.
        await using (var db = CreateContext())
        {
            await WriteAsync(
                db,
                Scores((1, 65), (2, 65), (3, 65), (4, 62), (5, 61)),
                MatchRanker.Rank([]));
        }

        await using (var db = CreateContext())
        {
            var shortlist = await new JobMatchRepository(db)
                .GetUnassessedAsync(ProfileId, minimumScore: 60, limit: 2, maximumScore: 69);

            Assert.Equal(2, shortlist.Count);
            Assert.All(shortlist, r => Assert.False(string.IsNullOrWhiteSpace(r.Text)));
        }
    }

    [Fact]
    public async Task A_pair_the_ranker_did_not_reach_falls_back_to_its_score()
    {
        // Ranked by posting id rather than by position, so a ranking that omits a pair leaves it
        // orderable against the ones it did reach instead of sinking it to zero.
        await using (var db = CreateContext())
        {
            await WriteAsync(db, Scores((1, 90), (2, 70)), MatchRanker.Rank([new(1, 90, 0.5)]));
        }

        await using (var db = CreateContext())
        {
            var row = await new JobMatchRepository(db).GetDetailAsync(ProfileId, 2);

            Assert.Equal(70, row!.RankScore);
            Assert.Null(row.Similarity);
        }
    }

    [Fact]
    public async Task A_re_scored_pair_stays_dismissed()
    {
        // The trap. A re-score that moves the number clears everything the model concluded
        // from the old arithmetic - but the candidate's "no" was not concluded from a number,
        // and sweeping it up in that reset would put every dismissed posting back at the top
        // of the shortlist on the first night its score shifted by a point.
        await using (var db = CreateContext())
        {
            await WriteAsync(db, Scores((1, 90)), MatchRanker.Rank([new(1, 90, 0.8)]));
            await new JobMatchRepository(db).SetDismissedAsync(ProfileId, 1, Now);
        }

        await using (var db = CreateContext())
        {
            await WriteAsync(db, Scores((1, 74)), MatchRanker.Rank([new(1, 74, 0.8)]));
        }

        await using (var db = CreateContext())
        {
            var row = await new JobMatchRepository(db).GetDetailAsync(ProfileId, 1);

            Assert.Equal(74, row!.Score);
            Assert.Equal(Now, row.DismissedAtUtc);
        }
    }

    [Fact]
    public async Task A_dismissed_pair_leaves_the_shortlist_and_can_come_back()
    {
        await using (var db = CreateContext())
        {
            await WriteAsync(db, Scores((1, 90), (2, 80)), MatchRanker.Rank([]));
            await new JobMatchRepository(db).SetDismissedAsync(ProfileId, 1, Now);
        }

        await using (var db = CreateContext())
        {
            var repo = new JobMatchRepository(db);

            var shortlist = await repo.ListAsync(ProfileId, 0, false, 25, 0);
            Assert.Equal([2L], shortlist.Select(r => r.PostingId));

            // The same shape read the other way round, so a client can show what it set aside
            // without a second contract.
            var dismissed = await repo.ListAsync(ProfileId, 0, false, 25, 0, dismissed: true);
            Assert.Equal([1L], dismissed.Select(r => r.PostingId));

            // An undo is the half that makes the feature safe to use at all.
            await repo.SetDismissedAsync(ProfileId, 1, null);
            var restored = await repo.ListAsync(ProfileId, 0, false, 25, 0);
            Assert.Equal([1L, 2L], restored.Select(r => r.PostingId).Order());
        }
    }

    [Fact]
    public async Task A_dismissed_pair_does_not_spend_the_assessment_budget()
    {
        // The point of the column. Forty judgements a night, and one spent on a posting the
        // candidate has already said no to is a judgement not spent on one they have not seen.
        // Filtered inside the query for the same reason the empty descriptions are: a band
        // ordered by posting id would draw the same dismissed rows into every sample forever.
        await using (var db = CreateContext())
        {
            await WriteAsync(db, Scores((4, 65), (5, 64)), MatchRanker.Rank([]));
            await new JobMatchRepository(db).SetDismissedAsync(ProfileId, 4, Now);
        }

        await using (var db = CreateContext())
        {
            var shortlist = await new JobMatchRepository(db)
                .GetUnassessedAsync(ProfileId, minimumScore: 60, limit: 10);

            Assert.Equal([5L], shortlist.Select(r => r.PostingId));
        }
    }

    [Fact]
    public async Task Dismissing_a_pair_that_was_never_scored_is_a_miss_not_a_silent_success()
    {
        await using var db = CreateContext();

        Assert.False(await new JobMatchRepository(db).SetDismissedAsync(ProfileId, 99, Now));
    }
}
