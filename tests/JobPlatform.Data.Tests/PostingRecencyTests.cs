using JobPlatform.Core.Model;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// How old a posting is, asked of a real relational engine.
/// </summary>
/// <remarks>
/// <b>The rule lives in <c>PostingAge</c> and is written out twice more so the database can run
/// it.</b> EF translates an expression tree rather than executing it, so neither query can call
/// the pure rule, and the two roots - postings for the corpus search, matches for the shortlist
/// and the apply queue - cannot be written in terms of each other without a rewriter nobody would
/// want to read afterwards. Duplication is therefore the deliberate choice, and this is the thing
/// that makes it safe: every spelling is asserted against the pure rule over the same rows, so a
/// drift in any one of them fails here rather than showing up as a shortlist quietly missing a
/// third of the market.
///
/// The fixture is the matrix the rule exists for. The row that matters most is the posting the
/// board dates three weeks ago and this system first read this morning - a search term added
/// today delivers hundreds of them, and treating them as today's work is the failure this whole
/// filter is against.
/// </remarks>
public sealed class PostingRecencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 3, 30, 0, TimeSpan.Zero);

    /// <summary>The window a daily run means, resolved once so every assertion shares it.</summary>
    private static readonly DateTimeOffset Cutoff = PostingAge.Cutoff(Now, PostingAge.DailyWindowDays);

    private const long ProfileId = 1;

    /// <summary>Posted today and seen today. In, by both columns.</summary>
    private const long Fresh = 1;

    /// <summary>Dated three weeks ago, first read this morning. Out - and this is the case.</summary>
    private const long OldButNewlyFound = 2;

    /// <summary>No stated date, first read this morning. In, on the fallback.</summary>
    private const long SilentAndNew = 3;

    /// <summary>No stated date, first read three weeks ago. Out, on the fallback.</summary>
    private const long SilentAndOld = 4;

    /// <summary>Dated exactly on the cutoff day. In: the bound is inclusive.</summary>
    private const long OnTheBoundary = 5;

    /// <summary>Dated today, first read three weeks ago. In - a re-dated posting is a posting.</summary>
    private const long DatedForwardOfFirstSeen = 6;

    public PostingRecencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = "44444444-4444-4444-4444-444444444444",
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });

        var old = Now.AddDays(-21);

        Add(db, Fresh, DateOnly.FromDateTime(Now.UtcDateTime), Now);
        Add(db, OldButNewlyFound, DateOnly.FromDateTime(old.UtcDateTime), Now);
        Add(db, SilentAndNew, null, Now);
        Add(db, SilentAndOld, null, old);
        Add(db, OnTheBoundary, DateOnly.FromDateTime(Cutoff.UtcDateTime), old);
        Add(db, DatedForwardOfFirstSeen, DateOnly.FromDateTime(Now.UtcDateTime), old);

        db.SaveChanges();

        static void Add(JobsDbContext db, long id, DateOnly? datePosted, DateTimeOffset firstSeen)
        {
            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"test:{id}",
                Site = "test",
                ExternalId = id.ToString(),
                ContentHash = new string((char)('a' + id), 64),
                Title = $"Role {id}",
                // Every posting carries an advert: the nightly draw excludes rows without one,
                // and a fixture that left them empty would pass this file for the wrong reason.
                Description = $"Advert {id}",
                DescriptionLength = 9,
                DatePosted = datePosted,
                FirstSeenUtc = firstSeen,
                LastSeenUtc = Now,
            });

            db.JobMatches.Add(new JobMatchEntity
            {
                ProfileId = ProfileId,
                PostingId = id,
                // Every pair scores the same, so nothing in this file can pass on an ordering
                // rather than on the filter it is testing.
                Score = 90,
                RankScore = 90,
                ScoredAtUtc = Now,
            });
        }
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    /// <summary>What the rule itself says, computed outside the database.</summary>
    private static async Task<long[]> ExpectedAsync(JobsDbContext db)
    {
        var rows = await db.JobPostings
            .AsNoTracking()
            .Select(p => new { p.Id, p.DatePosted, p.FirstSeenUtc })
            .ToListAsync();

        return [.. rows
            .Where(p => PostingAge.PostedSince(p.DatePosted, p.FirstSeenUtc, Cutoff))
            .Select(p => p.Id)
            .Order()];
    }

    [Fact]
    public async Task The_posting_predicate_answers_what_the_pure_rule_answers()
    {
        await using var db = CreateContext();

        var actual = await db.JobPostings
            .AsNoTracking()
            .Where(PostingRecency.Postings(Cutoff))
            .Select(p => p.Id)
            .OrderBy(id => id)
            .ToArrayAsync();

        // Spelled out as well as compared, so a fixture that stopped covering a case cannot make
        // the comparison pass by agreeing about nothing.
        Assert.Equal(
            [Fresh, SilentAndNew, OnTheBoundary, DatedForwardOfFirstSeen],
            [.. actual.Order()]);

        Assert.Equal(await ExpectedAsync(db), actual);
    }

    [Fact]
    public async Task The_match_predicate_answers_the_same_as_the_posting_predicate()
    {
        await using var db = CreateContext();

        var actual = await db.JobMatches
            .AsNoTracking()
            .Where(PostingRecency.Matches(Cutoff))
            .Select(m => m.PostingId)
            .OrderBy(id => id)
            .ToArrayAsync();

        Assert.Equal(await ExpectedAsync(db), actual);
    }

    [Fact]
    public async Task The_shortlist_can_be_narrowed_to_what_was_posted_recently()
    {
        await using var db = CreateContext();

        var rows = await new JobMatchRepository(db)
            .ListAsync(ProfileId, minimumScore: 0, assessedOnly: false, limit: 50, offset: 0,
                dismissed: false, postedSince: Cutoff);

        Assert.Equal(await ExpectedAsync(db), rows.Select(r => r.PostingId).Order().ToArray());

        // Unbounded is still unbounded: the filter is opt-in, and a shortlist that quietly showed
        // only this week's postings would read as a quiet market rather than as a filter.
        var all = await new JobMatchRepository(db)
            .ListAsync(ProfileId, minimumScore: 0, assessedOnly: false, limit: 50, offset: 0);

        Assert.Equal(6, all.Count);
    }

    [Fact]
    public async Task The_nightly_draw_can_be_bounded_by_age()
    {
        await using var db = CreateContext();

        var recent = await new JobMatchRepository(db)
            .GetUnassessedAsync(ProfileId, minimumScore: 0, limit: 50, maximumScore: null,
                postedSince: Cutoff);

        Assert.Equal(await ExpectedAsync(db), recent.Select(r => r.PostingId).Order().ToArray());

        var everything = await new JobMatchRepository(db)
            .GetUnassessedAsync(ProfileId, minimumScore: 0, limit: 50);

        Assert.Equal(6, everything.Count);
    }
}
