using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using JobPlatform.Ingestion.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobPlatform.Ingestion.Tests;

/// <summary>
/// Reachability as a claim on the judgement budget, and never as a claim on the match.
/// </summary>
/// <remarks>
/// <b>Against SQLite rather than a stubbed repository, for the reason
/// <c>MatchSweepShortlistTests</c> gives.</b> The rule is a clause in a query, applied before a
/// bound; a test handed a list would assert nothing about either half and would keep passing on
/// the day the clause moved after the <c>Take</c>.
///
/// <b>What is pinned, and the boundary is the whole design.</b> A posting whose board hosts the
/// application and whose employer publishes no confirmed board is one no unattended run can ever
/// apply to, so it is not drawn into the shortlist. A posting that merely has no link <i>yet</i>
/// keeps its place, because apply-link recovery targets exactly those and succeeds on a material
/// share - and a pair that is never drawn is never assessed, so it never becomes eligible again
/// for any other reason. <b>Getting the boundary wrong in the permanent direction drops jobs
/// forever and reports nothing</b>, which is why the temporary cases are asserted one at a time
/// rather than as a group: <c>OffsiteApply</c> null is 72% of LinkedIn and 52% of the corpus, and
/// it is the single value most likely to be folded into "board-hosted" by somebody tidying.
///
/// <b>And what it must never become.</b> The rule may decide who is judged and nothing else. No
/// score moves, no rank moves, no verdict changes and no threshold reads it -
/// <c>The_reachability_rule_changes_no_score_no_rank_and_no_verdict</c> runs the whole sweep twice
/// over two corpora that differ in nothing else and compares the stored arithmetic row for row.
///
/// The selection tests deliberately give the profile no concepts, so the scoring pass skips it and
/// the fixture's scores are the ones the selection runs against. The two-world test does the
/// opposite for the same reason: there, the arithmetic is the thing under test, so it has to
/// actually run.
/// </remarks>
public sealed class MatchSweepReachabilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    /// <summary>Half past three in the morning, which is when the timer fires.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 3, 30, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const string SubjectId = "66666666-6666-6666-6666-666666666666";

    /// <summary>An employer with no board of their own, confirmed or otherwise.</summary>
    private const int Boardless = 1;

    /// <summary>An employer whose own ATS board has been confirmed. Ten postings in the corpus.</summary>
    private const int WithConfirmedBoard = 2;

    /// <summary>An employer whose token was probed and never confirmed.</summary>
    private const int WithProbedBoard = 3;

    /// <summary>Board-hosted, no link, no employer board. The 681 this rule exists for.</summary>
    private const long BoardHosted = 300;

    /// <summary>No link, but the board says the application is offsite. Recovery targets this.</summary>
    private const long OffsiteNoLink = 301;

    /// <summary>No link and no answer either way. 4,364 LinkedIn postings read like this.</summary>
    private const long RouteUnknown = 302;

    /// <summary>Board-hosted at an employer whose own board is confirmed. Ten of 691.</summary>
    private const long BoardHostedConfirmedEmployer = 303;

    /// <summary>Board-hosted at an employer whose token was probed and never confirmed.</summary>
    private const long BoardHostedProbedEmployer = 304;

    /// <summary>Board-hosted, but the board published a direct link anyway.</summary>
    private const long BoardHostedWithDirectLink = 305;

    /// <summary>Board-hosted, but the employer's own system answered with a link.</summary>
    private const long BoardHostedWithRecoveredLink = 306;

    /// <summary>Board-hosted at an employer that normalised to no key at all.</summary>
    private const long BoardHostedNoEmployer = 307;

    /// <summary>The postings this rule holds back, and the whole of that set in this fixture.</summary>
    private static readonly long[] Unreachable =
        [BoardHosted, BoardHostedProbedEmployer, BoardHostedNoEmployer];

    /// <summary>
    /// What one night's shortlist half of the budget is, given the fixture's scores.
    /// </summary>
    /// <remarks>
    /// Forty judgements less the quarter reserved for the measurement sample. Every score here is
    /// above the top measurement band, exactly as in <c>MatchSweepShortlistTests</c>, so the bands
    /// return nothing and the whole budget is the shortlist's - which is what makes a short draw
    /// visible as a number rather than hidden inside a sample that happened to fill.
    /// </remarks>
    private const int ShortlistBudget = 30;

    /// <summary>
    /// What the unreachable rows score, which is more than anything else in the fixture.
    /// </summary>
    /// <remarks>
    /// Deliberately the wrong way round. Reachability is not an ordering and must not become one,
    /// so the rows it removes are the ones the draw would otherwise have taken first - which is
    /// also the only arrangement that can tell a clause in the query from a filter over the page
    /// the query already chose.
    /// </remarks>
    private const int Highest = 99;

    public MatchSweepReachabilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(Profile());

        db.Companies.Add(Company(Boardless, "boardless", "Boardless Ltd"));
        db.Companies.Add(Company(WithConfirmedBoard, "confirmed", "Confirmed Ltd"));
        db.Companies.Add(Company(WithProbedBoard, "probed", "Probed Ltd"));

        // Confirmed by a timestamp rather than by the row existing, which is the distinction the
        // probed employer below exists to hold open: a token that answered 200 and named somebody
        // else is a row, and it is not permission to expect a link.
        db.EmployerAtsBoards.Add(Board(WithConfirmedBoard, "confirmed-co", confirmed: Now.AddDays(-7)));
        db.EmployerAtsBoards.Add(Board(WithProbedBoard, "probed-co", confirmed: null));

        // The three unreachable rows score higher than everything else, on purpose and the wrong
        // way round: they head the top-down draw and the recent draw both, so a rule that ran
        // after the bound would still have removed them from a page it had already chosen. That
        // is the only arrangement in which a filter and a post-filter give different answers.
        Add(db, BoardHosted, Boardless, offsiteApply: false, score: Highest);
        Add(db, BoardHostedProbedEmployer, WithProbedBoard, offsiteApply: false, score: Highest);
        Add(db, BoardHostedNoEmployer, companyId: null, offsiteApply: false, score: Highest);

        Add(db, OffsiteNoLink, Boardless, offsiteApply: true);
        Add(db, RouteUnknown, Boardless, offsiteApply: null);
        Add(db, BoardHostedConfirmedEmployer, WithConfirmedBoard, offsiteApply: false);
        Add(db, BoardHostedWithDirectLink, Boardless, offsiteApply: false,
            jobUrlDirect: "https://boards.example.invalid/apply/305");
        Add(db, BoardHostedWithRecoveredLink, Boardless, offsiteApply: false,
            employerAtsApplyUrl: "https://jobs.example.invalid/apply/306");

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_posting_the_board_hosts_with_no_employer_board_is_never_judged()
    {
        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        var judged = Judged(assessor);

        // Nothing unattended can drive a LinkedIn Easy Apply form, and the corpus says recovery
        // will not rescue these: of 691 board-hosted link-less postings across 336 companies,
        // exactly 10 at 7 companies sit at an employer with a confirmed board. So a judgement
        // bought here buys a verdict on a vacancy this pipeline cannot reach.
        Assert.DoesNotContain(BoardHosted, judged);

        // A probe is not a confirmation. The board row exists; ConfirmedAtUtc is null, and that
        // timestamp is the whole trust test - a version of this rule that asked whether a row
        // existed would expect a link from a token that may belong to another company.
        Assert.DoesNotContain(BoardHostedProbedEmployer, judged);

        // No employer key means no board can ever be attached, so every route by which a link
        // could arrive is closed. That is more certainly unreachable rather than less.
        Assert.DoesNotContain(BoardHostedNoEmployer, judged);
    }

    /// <param name="postingId">
    /// The two temporarily-unreachable states, asserted separately rather than as a set. Null is
    /// the one that matters: 4,364 LinkedIn postings - 72% of that board - read null because they
    /// were last seen before the scraper's offsite_apply classifier shipped, and a rule that read
    /// null as "board-hosted" would stop judging half the corpus overnight.
    /// </param>
    [Theory]
    [InlineData(OffsiteNoLink)]
    [InlineData(RouteUnknown)]
    public async Task A_posting_whose_link_has_not_arrived_yet_keeps_its_place_in_the_budget(
        long postingId)
    {
        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        // Apply-link recovery targets exactly these - EmployerAtsBoardRepository's rule requires
        // OffsiteApply != false - and it succeeds on a material share of them. Skipping one would
        // mean a posting whose link arrives tomorrow was never judged and never comes back: the
        // shortlist is drawn from unassessed pairs, so a pair that is never drawn is never
        // assessed, and nothing else would ever make it eligible again. Spending one judgement on
        // it is strictly the cheaper mistake.
        Assert.Contains(postingId, Judged(assessor));
    }

    [Fact]
    public async Task A_board_hosted_posting_at_an_employer_with_a_confirmed_board_is_still_judged()
    {
        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        // The 10-of-691 case, kept deliberately. These are the only rows where the word
        // "permanent" is arguable - the employer's own system publishes vacancies and could yet
        // answer about this one - and 1.4% of the saving is a cheap price for an exclusion that
        // never has to defend the hardest part of its own claim. The error falls towards judging.
        Assert.Contains(BoardHostedConfirmedEmployer, Judged(assessor));
    }

    [Fact]
    public async Task A_link_from_either_source_settles_it_whatever_the_board_says()
    {
        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        var judged = Judged(assessor);

        // Both link columns are read, and separately. A posting carrying either is reachable by
        // definition, so OffsiteApply == false decides nothing on its own - which is also why
        // these are not read through ApplyableRow's ladder: this rule wants both absent rather
        // than wanting to know which of the two won.
        Assert.Contains(BoardHostedWithDirectLink, judged);
        Assert.Contains(BoardHostedWithRecoveredLink, judged);
    }

    [Fact]
    public async Task The_draw_still_fills_its_budget_when_unreachable_postings_are_excluded()
    {
        // A backlog of reachable postings, every one of them scoring below the unreachable rows
        // already in the fixture. This is the arrangement that tells a filter from a bound: the
        // board-hosted rows head the top-down order, so a rule applied after the Take would remove
        // them from a page that was already chosen and hand back a short night, with nothing in
        // the summary saying so. Three filters in this codebase have had to be moved for exactly
        // that, which is why it is asserted rather than assumed.
        await using (var db = CreateContext())
        {
            for (var i = 0; i < ShortlistBudget + 5; i++)
            {
                Add(db, 400 + i, Boardless, offsiteApply: true);
            }

            await db.SaveChangesAsync();
        }

        var assessor = new RecordingAssessor();

        await RunAsync(assessor);

        var judged = Judged(assessor);

        Assert.Equal(ShortlistBudget, judged.Length);
        Assert.Equal(judged.Length, judged.Distinct().Count());

        foreach (var excluded in Unreachable)
        {
            Assert.DoesNotContain(excluded, judged);
        }
    }

    [Fact]
    public async Task The_count_of_pairs_held_back_is_reported_rather_than_inferred()
    {
        var summary = await SweepOnDemandAsync();

        // The rule moves no score, no rank and no verdict, so it shows up nowhere else at all -
        // which is precisely what makes an unmeasured version of it so easy to believe. A night
        // that judged forty looks identical whether this removed nothing or removed four hundred.
        Assert.Equal(Unreachable.Length, summary.Unreachable);

        // And it is the pool the rule reached, not the judgements it saved. Everything else here
        // was drawn, so the night cost exactly what it would have cost anyway: a bounded draw
        // replaces an excluded pair with the next-best one. Quoting this as money is the mistake
        // the log line's wording exists to prevent.
        Assert.Equal(8 - Unreachable.Length, summary.Requested);
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// The claim the whole change rests on, asserted against a corpus that differs in nothing else.
    /// </summary>
    /// <remarks>
    /// <b>Two sweeps over two databases identical apart from one column, compared row for row.</b>
    /// The rule is allowed to decide who is judged and nothing else, so the arithmetic half of
    /// every stored match - score, coverage-derived rank, similarity and both version stamps - has
    /// to come out byte-identical whether a posting is reachable or not. Asserting that on the
    /// excluded row alone would be much weaker: the failure worth catching is a reachability term
    /// leaking into <c>MatchScorer</c> or <c>MatchRanker</c>, and the ranker ranks over the whole
    /// pool at once, so a leak on one row moves every other row's rank as well.
    ///
    /// <b>The profile holds a concept here, unlike everywhere else in this file, because the
    /// arithmetic has to actually run.</b> A concept-less profile is skipped by the scoring pass,
    /// which would make "the scores are unchanged" true because nothing wrote one.
    ///
    /// <b>And the assessor answers with the pair's own deterministic score</b>, so the verdicts
    /// are a function of the arithmetic rather than a constant. A stub returning the same verdict
    /// for everything would agree across the two worlds no matter what the rule had done to the
    /// numbers, which is a test that cannot fail.
    /// </remarks>
    [Fact]
    public async Task The_reachability_rule_changes_no_score_no_rank_and_no_verdict()
    {
        var held = await ScoredWorldAsync(boardHosted: true);
        var judgedWorld = await ScoredWorldAsync(boardHosted: false);

        // The guard against a comparison that agrees for the wrong reason. The fixture writes its
        // match rows with a scorer version of zero, so a stamped current version is proof the
        // arithmetic pass actually ran and rewrote them - without which two worlds of untouched
        // rows would agree no matter what the rule had done.
        Assert.All(
            held.Arithmetic.Values,
            row => Assert.Contains($"scorer={MatchResult.CurrentVersion}", row, StringComparison.Ordinal));

        // Every number the match is made of, on every pair, in both worlds.
        Assert.Equal(judgedWorld.Arithmetic, held.Arithmetic);

        // The verdicts too, for every pair that was judged in the world where the posting was
        // reachable. Reachability decided that one pair was not bought; it changed no answer.
        foreach (var (postingId, verdict) in judgedWorld.Verdicts)
        {
            if (postingId == BoardHosted)
            {
                continue;
            }

            Assert.Equal(verdict, held.Verdicts[postingId]);
        }

        // The excluded pair is left exactly as the scoring pass wrote it: a score, a rank, and no
        // judgement. Not a downgraded verdict, not a zeroed score, not a dismissal - the three
        // shapes this could have taken that would each have made reachability a claim on the match.
        Assert.Null(held.Verdicts[BoardHosted]);
        Assert.DoesNotContain(BoardHosted, held.Assessed);
        Assert.Contains(BoardHosted, judgedWorld.Assessed);
    }

    /// <summary>
    /// The structural half: neither pure type can be told about reachability in the first place.
    /// </summary>
    /// <remarks>
    /// <b>A rule enforced by there being no way to express the alternative outlives one enforced
    /// by everybody remembering it</b>, which is the argument <c>AtsBoardToFetch</c> already makes
    /// about unconfirmed boards. <c>MatchScorer</c> reads <c>PostingFacts</c> and
    /// <c>MatchRanker</c> reads <c>RankInput</c>; neither has a member an apply route could be
    /// passed in, so the leak this change is most at risk of would have to start as a diff that
    /// widens one of these two records - which is a change somebody signs off rather than one that
    /// slips through as a clause in a query.
    ///
    /// Named members rather than a count, for the reason the MCP surface's equality test gives:
    /// a number repeated in a test goes stale silently, where a list does not.
    /// </remarks>
    [Fact]
    public void Neither_the_scorer_nor_the_ranker_can_be_told_about_reachability()
    {
        string[] forbidden = ["apply", "offsite", "reach", "url", "board", "vendor"];

        foreach (var type in new[] { typeof(PostingFacts), typeof(RankInput) })
        {
            foreach (var member in type.GetProperties())
            {
                var name = member.Name.ToLowerInvariant();

                Assert.DoesNotContain(forbidden, word => name.Contains(word, StringComparison.Ordinal));
            }
        }
    }

    // -----------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------

    private JobsDbContext CreateContext() => new(_options);

    /// <summary>The postings this sweep actually sent to the model, in the order it sent them.</summary>
    private static long[] Judged(RecordingAssessor assessor)
        => [.. assessor.Requested.SelectMany(batch => batch).Select(r => r.PostingId)];

    private async Task RunAsync(RecordingAssessor assessor)
    {
        await using var db = CreateContext();

        await Function(db, assessor).RunAsync(new TimerInfo(), CancellationToken.None);
    }

    /// <summary>
    /// The same sweep through the admin route, which is the only entry point that answers.
    /// </summary>
    /// <remarks>
    /// The timer returns nothing, so the summary is unreachable through it. This route draws a
    /// smaller shortlist - ten rather than forty - which changes nothing here: the fixture holds
    /// five reachable pairs and three unreachable ones, so both budgets are larger than the pool
    /// and both draw the same five.
    /// </remarks>
    private async Task<MatchSweepFunction.SweepSummary> SweepOnDemandAsync()
    {
        await using var db = CreateContext();

        var result = await Function(db, new RecordingAssessor())
            .RunMatchSweepFunction(new DefaultHttpContext().Request, CancellationToken.None);

        return Assert.IsType<MatchSweepFunction.SweepSummary>(
            Assert.IsType<OkObjectResult>(result).Value);
    }

    private static MatchSweepFunction Function(JobsDbContext db, ICandidacyAssessor assessor)
        => new(
            db,
            new CandidateProfileRepository(db),
            new JobMatchRepository(db),
            new EmbeddingRepository(db),
            new FakeTime(Now),
            NullLogger<MatchSweepFunction>.Instance,
            assessor);

    private static CandidateProfileEntity Profile() => new()
    {
        Id = ProfileId,
        SubjectId = SubjectId,
        FullName = "Test Candidate",
        Email = "candidate@example.invalid",
        CreatedUtc = Now,
        UpdatedUtc = Now,
        // What makes the sweep consider this profile at all. Extraction has run; whether it found
        // anything is what separates the selection tests from the two-world one.
        ExtractedAtUtc = Now,
    };

    private static CompanyEntity Company(int id, string key, string name) => new()
    {
        Id = id,
        CompanyKey = key,
        DisplayName = name,
        FirstSeenUtc = Now,
        LastSeenUtc = Now,
    };

    private static EmployerAtsBoardEntity Board(int companyId, string token, DateTimeOffset? confirmed)
        => new()
        {
            CompanyId = companyId,
            Vendor = AtsVendor.Greenhouse,
            Token = token,
            Discovery = confirmed is null ? AtsBoardDiscovery.Probed : AtsBoardDiscovery.Learned,
            DiscoveredAtUtc = Now.AddDays(-30),
            ConfirmedAtUtc = confirmed,
        };

    /// <summary>
    /// One posting and its scored match, dated today so the recent reservation is not in play.
    /// </summary>
    /// <remarks>
    /// <c>LastSeenUtc</c> is distinct per posting deliberately: the scoring pass reads the corpus
    /// ordered by it, and a fixture where every row shares one instant would let the provider
    /// choose the order - which is fine for a selection test and not fine for a comparison
    /// between two runs.
    /// </remarks>
    private static void Add(
        JobsDbContext db,
        long id,
        int? companyId,
        bool? offsiteApply,
        string? jobUrlDirect = null,
        string? employerAtsApplyUrl = null,
        int score = 90)
    {
        db.JobPostings.Add(new JobPostingEntity
        {
            Id = id,
            SourceKey = $"test:{id}",
            Site = "linkedin",
            ExternalId = id.ToString(),
            ContentHash = new string((char)('a' + (id % 20)), 64),
            Title = $"Role {id}",
            Company = companyId is null ? null : $"Employer {companyId}",
            CompanyId = companyId,
            Description = $"An advert for role {id}.",
            DescriptionLength = 24,
            DatePosted = DateOnly.FromDateTime(Now.UtcDateTime),
            FirstSeenUtc = Now,
            LastSeenUtc = Now.AddSeconds(-id),
            OffsiteApply = offsiteApply,
            JobUrlDirect = jobUrlDirect,
            EmployerAtsApplyUrl = employerAtsApplyUrl,
            EmployerAtsCheckedUtc = employerAtsApplyUrl is null ? null : Now.AddDays(-1),
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

    // -----------------------------------------------------------------------
    // The two-world comparison
    // -----------------------------------------------------------------------

    /// <summary>What one sweep left behind, as the numbers a match is made of.</summary>
    /// <param name="Arithmetic">Posting id to score, rank, similarity and both version stamps.</param>
    /// <param name="Verdicts">Posting id to the model's verdict, null where none was bought.</param>
    /// <param name="Assessed">The postings a judgement was actually spent on.</param>
    private sealed record ScoredWorld(
        IReadOnlyDictionary<long, string> Arithmetic,
        IReadOnlyDictionary<long, CandidacyVerdict?> Verdicts,
        IReadOnlySet<long> Assessed);

    /// <summary>
    /// A corpus scored from scratch, with one posting either board-hosted or offsite.
    /// </summary>
    /// <remarks>
    /// A database of its own rather than a mutation of the shared fixture, because the comparison
    /// is between two complete runs: re-running the sweep over the same rows would meet an already
    /// assessed set and draw a different shortlist for a reason that has nothing to do with the
    /// rule under test.
    /// </remarks>
    private static async Task<ScoredWorld> ScoredWorldAsync(bool boardHosted)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(connection).Options;

        await using (var seed = new JobsDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            // The vocabulary, because ProfileConcepts and PostingConcepts are foreign keys into it
            // and the scorer resolves through the graph rather than through the strings.
            await ConceptSeeder.SeedAsync(seed);

            var concept = await seed.Concepts.FirstAsync(c => c.ConceptKey == Skill);

            seed.CandidateProfiles.Add(Profile());
            seed.Companies.Add(Company(Boardless, "boardless", "Boardless Ltd"));

            seed.ProfileConcepts.Add(new ProfileConceptEntity
            {
                ProfileId = ProfileId,
                ConceptId = concept.Id,
                Source = AssertionSource.Board,
                Polarity = AssertionPolarity.Proficient,
            });

            for (var i = 0; i < 4; i++)
            {
                var id = BoardHosted + i;

                // Only the first posting differs between the two worlds, and only in this column.
                Add(seed, id, Boardless, offsiteApply: !(boardHosted && id == BoardHosted));

                seed.PostingConcepts.Add(new PostingConceptEntity
                {
                    PostingId = id,
                    ConceptId = concept.Id,
                    Source = AssertionSource.Model,
                    Polarity = AssertionPolarity.Required,
                });
            }

            await seed.SaveChangesAsync();
        }

        await using var db = new JobsDbContext(options);

        await Function(db, new ScoreEchoingAssessor()).RunAsync(new TimerInfo(), CancellationToken.None);

        var rows = await db.JobMatches.AsNoTracking().ToListAsync();

        return new ScoredWorld(
            rows.ToDictionary(
                m => m.PostingId,
                // One string rather than five columns, so a difference in any of them fails as a
                // readable diff naming the posting rather than as five near-identical assertions.
                m => $"score={m.Score} rank={m.RankScore} similarity={m.Similarity} "
                    + $"scorer={m.ScorerVersion} ranker={m.RankerVersion}"),
            rows.ToDictionary(m => m.PostingId, m => m.Verdict),
            rows.Where(m => m.AssessedAtUtc is not null).Select(m => m.PostingId).ToHashSet());
    }

    /// <summary>The one concept both sides of the two-world match are built from.</summary>
    private const string Skill = "skill.csharp";

    /// <summary>
    /// An assessor whose answer is a function of the arithmetic it was handed.
    /// </summary>
    /// <remarks>
    /// The point of the two-world comparison is that the model saw the same numbers in both, and a
    /// stub answering a constant would agree across the two however badly the score had been
    /// mangled. Echoing the pair's own score makes the stored verdict a witness to it.
    /// </remarks>
    private sealed class ScoreEchoingAssessor : ICandidacyAssessor
    {
        public Task<IReadOnlyList<CandidacyAssessment?>> AssessAsync(
            CandidateProfile profile,
            IReadOnlyList<CandidacyRequest> requests,
            CancellationToken ct = default)
        {
            IReadOnlyList<CandidacyAssessment?> answers =
            [
                .. requests.Select(r => (CandidacyAssessment?)new CandidacyAssessment
                {
                    Verdict = r.Match.Score >= 80 ? CandidacyVerdict.Strong : CandidacyVerdict.Possible,
                    Score = r.Match.Score,
                    Version = CandidacyAssessment.CurrentVersion,
                }),
            ];

            return Task.FromResult(answers);
        }
    }

    /// <summary>
    /// An assessor that records what it was asked and judges everything.
    /// </summary>
    /// <remarks>
    /// It answers rather than returning nulls, for the reason <c>MatchSweepShortlistTests</c>
    /// gives: rows it saw leave the unassessed set, so a resumed sweep is distinguishable from a
    /// repeated one.
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
