using JobPlatform.Core.Applications;
using JobPlatform.Core.Dedup;
using JobPlatform.Core.Matching;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The apply queue's aggregator filter, against a real relational engine.
/// </summary>
/// <remarks>
/// <b>A separate fixture from <see cref="ApplyQueueTests"/> because this one turns on a column
/// that file never sets.</b> Every posting there has a null <c>ApplyVendor</c>, which is the right
/// shape for the questions it asks and would make every assertion here vacuously pass.
///
/// <b>The filter is enforcement rather than advice, and that is what it is for.</b>
/// <c>list_applyable</c> has told its caller that <c>Aggregator</c> is worth skipping since the
/// surface existed, and a prompt-level instruction is a request: the only reader that acted on it
/// filtered the rows it had already been handed. A server-side filter is a promise the caller
/// cannot forget to keep.
///
/// <b>It runs in SQL, which is possible only because <c>JobPostings.ApplyVendor</c> stores the
/// derivation.</b> <c>AtsVendorDetector.Detect</c> reads a URL rather than compares one, so it has
/// no translation, and after materialisation is exactly where a filter must not run: applied after
/// <c>Take</c> it stops being a filter and becomes a silent reduction of the limit, which this
/// codebase has paid for three times.
/// <see cref="The_filter_runs_before_the_bound_rather_than_over_the_read_window"/> is the
/// assertion that matters most here, and it has to defeat the over-read as well as the limit - the
/// queue reads <c>ClusterWindow</c> rows for every job it returns, so a predicate run over the
/// materialised page still answers correctly whenever that window happens to hold a survivor.
/// Only a head of aggregators longer than the window tells the two implementations apart.
///
/// <b>The stored column is a rung short of the link the queue hands over, and the interesting
/// tests are all about that.</b> The column is derived from
/// <c>JobUrlDirect ?? EmployerAtsApplyUrl ?? JobUrl</c>; the queue may also borrow a link from the
/// same job on another board. So the filter keeps a row whose column says <c>Aggregator</c> where
/// a borrow could rescue it, and hiding a job that could have been applied to is the failure being
/// avoided - it is invisible, where a posting shown and refused is a row the caller reads.
/// </remarks>
public sealed class ApplyQueueAggregatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;

    /// <summary>Ranked first, and a LinkedIn posting page with no twin anywhere. Dropped.</summary>
    private const long TopAggregator = 1;

    /// <summary>A "direct" link that re-lists the advert on another board. Dropped.</summary>
    /// <remarks>
    /// Unrescuable by construction rather than by luck: a published <c>JobUrlDirect</c> vetoes the
    /// borrow, so the column and the row cannot disagree about it whatever else is in the corpus.
    /// </remarks>
    private const long DirectToAnotherBoard = 2;

    /// <summary>A board page whose board says it hosts the application. Dropped.</summary>
    /// <remarks>
    /// It has a twin carrying an employer link, so it is the row that separates "a borrow is
    /// available" from "a twin exists". <c>OffsiteApply == false</c> vetoes borrowing, so the
    /// queue would hand over this board page - and dropping it is therefore right.
    /// </remarks>
    private const long BoardHosted = 3;

    /// <summary>An employer's own form. The row the filter exists to leave standing.</summary>
    private const long Employer = 4;

    /// <summary>A board page whose twin published an employer form. Kept, on the twin's link.</summary>
    private const long Rescued = 5;

    /// <summary>Nothing has derived a vendor for it. Kept, because null is not a verdict.</summary>
    private const long NotYetDerived = 6;

    /// <summary>Rescued by a borrowed link that is itself another job board. Kept, and says so.</summary>
    private const long RescuedToAnotherBoard = 7;

    /// <summary>The twins. Never matched to the profile: they exist to be borrowed from.</summary>
    private const long BoardHostedTwin = 300;
    private const long RescuedTwin = 500;
    private const long RescuedToAnotherBoardTwin = 700;

    public ApplyQueueAggregatorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        db.CandidateProfiles.Add(new CandidateProfileEntity
        {
            Id = ProfileId,
            SubjectId = "77777777-7777-7777-7777-777777777777",
            FullName = "Test Candidate",
            Email = "candidate@example.invalid",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });

        // Three aggregators at the head, above the first row the filter keeps. Not padding: a
        // limit of one reads ClusterWindow rows, so a head of two would be survivable by a
        // predicate applied to the materialised page and the bound assertion below would pass
        // against the implementation it exists to reject. Each of the three is also a different
        // reason for the answer to be Aggregator, which is what the head is worth spending.
        //
        // Every URL is a real vendor's shape. The detector reads hosts and query parameters, so a
        // made-up one would assert nothing about the corpus - and the stored column is written
        // through JobPostingEntity.VendorOf rather than by hand, or this file would be asserting
        // its own arithmetic instead of the rule the two live writers apply.
        Add(db, TopAggregator, "Data Engineer", rank: 99);
        Add(db, DirectToAnotherBoard, "Analyst", rank: 98,
            direct: "https://uk.whatjobs.com/pub_api__cpl__2");
        Add(db, BoardHosted, "Scientist", rank: 97, offsiteApply: false);
        Add(db, Employer, "Architect", rank: 80,
            direct: "https://boards.greenhouse.io/acme/jobs/4012345");
        Add(db, Rescued, "Platform Engineer", rank: 70);
        Add(db, NotYetDerived, "Reliability Engineer", rank: 60, derived: false);
        Add(db, RescuedToAnotherBoard, "Security Engineer", rank: 50);

        // The twins, on another site and carrying the link their siblings' boards did not publish.
        // Matched to nobody, so they never appear in the queue in their own right - the borrow is
        // a fact about the corpus rather than about this candidate's shortlist.
        Add(db, BoardHostedTwin, "Scientist", rank: null, site: "indeed",
            direct: "https://jobs.lever.co/gamma/scientist");
        Add(db, RescuedTwin, "Platform Engineer", rank: null, site: "indeed",
            direct: "https://jobs.lever.co/cloudflare/platform");

        // The residual error, made concrete: this twin's "direct" link is another job board, so
        // the rescue keeps a posting whose form is still not an employer's. It is kept
        // deliberately - SQL here reads one row and the borrowed link belongs to another - and the
        // row reports Aggregator, which is what makes the error a visible one.
        Add(db, RescuedToAnotherBoardTwin, "Security Engineer", rank: null, site: "indeed",
            direct: "https://uk.whatjobs.com/pub_api__cpl__700");

        db.SaveChanges();

        static void Add(
            JobsDbContext db,
            long id,
            string title,
            double? rank,
            string? direct = null,
            bool? offsiteApply = null,
            string site = "linkedin",
            bool derived = true)
        {
            var jobUrl = $"https://www.linkedin.com/jobs/view/{id}";

            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"{site}:{id}",
                Site = site,
                ExternalId = id.ToString(),
                ContentHash = new string((char)('a' + (id % 20)), 64),
                Title = title,
                Company = "Acme",
                LocationCity = "London",
                LocationRaw = "London, UK",
                Description = $"Advert {id}",
                DescriptionLength = 9,
                JobUrl = jobUrl,
                JobUrlDirect = direct,
                OffsiteApply = offsiteApply,

                // Null is the state of a posting no writer has reached since the column was added,
                // and it is not AtsVendor.Unknown - that member is a verdict about the row.
                ApplyVendor = derived
                    ? JobPostingEntity.VendorOf(direct, null, jobUrl)
                    : null,
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });

            if (rank is not { } rankScore)
            {
                return;
            }

            db.JobMatches.Add(new JobMatchEntity
            {
                ProfileId = ProfileId,
                PostingId = id,
                Score = 90,
                RankScore = rankScore,
                ScoredAtUtc = Now,
                Verdict = CandidacyVerdict.Strong,
                AssessmentScore = 90,
                AssessedAtUtc = Now,
                ScorerVersion = MatchResult.CurrentVersion,
            });
        }
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private async Task<IReadOnlyList<ApplyableRow>> QueueAsync(ApplyableQuery query)
    {
        await using var db = CreateContext();

        return await new JobMatchRepository(db).ListApplyableAsync(ProfileId, query);
    }

    private static long[] Ids(IReadOnlyList<ApplyableRow> rows) => [.. rows.Select(row => row.PostingId)];

    [Fact]
    public async Task The_filter_is_off_unless_it_is_asked_for()
    {
        var rows = await QueueAsync(new ApplyableQuery { Limit = 50 });

        // Opt-in, like every other narrowing on this queue and for the reason the shortlist facet
        // is: a work queue that quietly returned a subset would read as a market gone quiet, and
        // the caller would have no way to tell that from a filter it never asked for.
        Assert.Equal(
            [TopAggregator, DirectToAnotherBoard, BoardHosted, Employer, Rescued, NotYetDerived,
                RescuedToAnotherBoard],
            Ids(rows));
    }

    [Fact]
    public async Task The_filter_drops_the_aggregators_and_leaves_the_employer_rows()
    {
        var rows = await QueueAsync(new ApplyableQuery { ExcludeAggregators = true, Limit = 50 });

        // Spelled out rather than counted: a clause that dropped everything, or that dropped the
        // wrong three, satisfies a count just as well as the right one does.
        Assert.Equal(
            [Employer, Rescued, NotYetDerived, RescuedToAnotherBoard],
            Ids(rows));

        // The employer row is what a run is here for, and this is the assertion that fails if the
        // clause is ever inverted - dropping everything would satisfy the DoesNotContain pair
        // below on its own.
        Assert.Equal(AtsVendor.Greenhouse, rows.Single(row => row.PostingId == Employer).AtsVendor);

        Assert.DoesNotContain(TopAggregator, Ids(rows));
        Assert.DoesNotContain(DirectToAnotherBoard, Ids(rows));
    }

    /// <summary>
    /// The assertion the file is for: filtered in the query, not over the page.
    /// </summary>
    /// <remarks>
    /// A limit of one against a queue whose first three rows are aggregators. Filtered in SQL the
    /// answer is the employer row; filtered over the materialised page it is nothing at all, and
    /// the caller reads "no employer has a job for you" off a page size.
    ///
    /// <b>The head is three rows long because the bound is not the limit.</b>
    /// <c>ListApplyableAsync</c> reads <c>ClusterWindow</c> rows per job so that duplicate
    /// listings can collapse without shortening the page, so a limit of one reads three - and a
    /// head of two aggregators would leave a survivor inside that window, which an in-memory
    /// predicate would find and return. That version of this test passes against the
    /// implementation it exists to reject.
    /// </remarks>
    [Fact]
    public async Task The_filter_runs_before_the_bound_rather_than_over_the_read_window()
    {
        var rows = await QueueAsync(new ApplyableQuery { ExcludeAggregators = true, Limit = 1 });

        Assert.Equal([Employer], Ids(rows));
    }

    [Fact]
    public async Task A_posting_nobody_has_derived_a_vendor_for_is_kept()
    {
        var rows = await QueueAsync(new ApplyableQuery { ExcludeAggregators = true, Limit = 50 });

        // Null is "nobody has looked", not "there is nothing at the end of this link" - that is
        // AtsVendor.Unknown, and it is a verdict about the row. A filter that hid what it had not
        // judged would empty the queue on the morning of a deploy, and would go on hiding jobs for
        // no stated reason for as long as anybody left it alone.
        Assert.Contains(NotYetDerived, Ids(rows));

        await using var db = CreateContext();

        Assert.Null((await db.JobPostings.SingleAsync(p => p.Id == NotYetDerived)).ApplyVendor);
    }

    /// <summary>
    /// A row the column calls an aggregator is kept where a borrowed link rescues it.
    /// </summary>
    /// <remarks>
    /// <b>The divergence this filter is written around, asserted from both sides.</b> The stored
    /// column is derived from the posting's own
    /// <c>JobUrlDirect ?? EmployerAtsApplyUrl ?? JobUrl</c> and this posting has only its LinkedIn
    /// page, so the column says <c>Aggregator</c>. The queue borrows the employer link its twin
    /// published, so the row says <c>Lever</c> and hands over a form that can be filled in.
    ///
    /// <b>Filtering on the column alone would hide exactly this row</b>, and roughly 5% of the
    /// links LinkedIn stopped publishing come back by this route - so the one-comparison version
    /// of the clause is at its most wrong on the postings it was most needed for. Hiding a job
    /// that could have been applied to is the worse of the two errors because nobody can see it: a
    /// row that should be absent from a list gets noticed, and a row that should be present does
    /// not.
    ///
    /// The stored column is read here rather than assumed, because the whole argument rests on the
    /// two disagreeing - if a writer ever taught the column the fourth rung, this test would go on
    /// passing while asserting nothing.
    /// </remarks>
    [Fact]
    public async Task The_aggregator_filter_keeps_a_row_a_borrowed_link_rescues()
    {
        var rows = await QueueAsync(new ApplyableQuery { ExcludeAggregators = true, Limit = 50 });
        var row = Assert.Single(rows, entry => entry.PostingId == Rescued);

        Assert.Equal(ApplyUrlSource.MatchedOnAnotherBoard, row.ApplyUrlSource);
        Assert.Equal("https://jobs.lever.co/cloudflare/platform", row.ApplyUrl);
        Assert.Equal(AtsVendor.Lever, row.AtsVendor);
        Assert.True(row.AtsVendor.IsEmployerAts());

        await using var db = CreateContext();

        // The column says the opposite about the same posting, which is not a bug in either: they
        // answer about different links, and the filter is what has to know that.
        Assert.Equal(
            AtsVendor.Aggregator,
            (await db.JobPostings.SingleAsync(p => p.Id == Rescued)).ApplyVendor);
    }

    /// <summary>
    /// The rescue carries the <c>OffsiteApply</c> veto rather than asking whether a twin exists.
    /// </summary>
    /// <remarks>
    /// This posting has the same twin arrangement as <see cref="Rescued"/> and its own board says
    /// it hosts the application - so no link is borrowed, the queue hands over the board page, and
    /// the row really is an aggregator. A rescue clause written as "some twin publishes a link"
    /// would keep it, and would then be keeping a row the caller cannot apply through while
    /// claiming to have filtered those out. The posting's own board outranks a title match on
    /// another, everywhere on this path.
    /// </remarks>
    [Fact]
    public async Task A_board_that_says_it_hosts_the_application_is_not_rescued_by_its_twin()
    {
        var unfiltered = await QueueAsync(new ApplyableQuery { Limit = 50 });
        var row = Assert.Single(unfiltered, entry => entry.PostingId == BoardHosted);

        // Unfiltered first, or the assertion below could pass because the twin was never there.
        Assert.Equal(ApplyUrlSource.BoardPosting, row.ApplyUrlSource);
        Assert.Equal(AtsVendor.Aggregator, row.AtsVendor);

        var filtered = await QueueAsync(new ApplyableQuery { ExcludeAggregators = true, Limit = 50 });

        Assert.DoesNotContain(BoardHosted, Ids(filtered));
    }

    /// <summary>
    /// The residual error, kept deliberately and reported on the row.
    /// </summary>
    /// <remarks>
    /// <b>The rescue asks whether a borrow is available and never whose form is at the end of
    /// it</b>, because the borrowed link belongs to a different row and this clause reads one. So
    /// a posting rescued by a twin whose own "direct" link re-lists the advert on a third board
    /// survives a filter named after dropping exactly that.
    ///
    /// It is the error worth having, and the reason is the direction it falls in: the row carries
    /// <c>Aggregator</c> and <c>MatchedOnAnotherBoard</c>, which the tool description tells a
    /// client to read, so the caller can refuse it in the same turn. Hiding a rescued posting is
    /// on no row at all. Closing it means storing a vendor for a link the posting does not own - a
    /// property of a cluster rather than of a row, and DDL bought for the smaller error.
    /// </remarks>
    [Fact]
    public async Task A_rescued_row_whose_borrowed_link_is_another_board_survives_and_says_so()
    {
        var rows = await QueueAsync(new ApplyableQuery { ExcludeAggregators = true, Limit = 50 });
        var row = Assert.Single(rows, entry => entry.PostingId == RescuedToAnotherBoard);

        Assert.Equal(ApplyUrlSource.MatchedOnAnotherBoard, row.ApplyUrlSource);
        Assert.Equal(AtsVendor.Aggregator, row.AtsVendor);
        Assert.False(row.AtsVendor.IsEmployerAts());
    }

    /// <summary>
    /// <c>Unknown</c> is not what this filter excludes, and that is a decision.
    /// </summary>
    /// <remarks>
    /// <c>GenerateApplicationsFunction</c> skips on <c>!IsEmployerAts()</c>, which also takes
    /// <c>Unknown</c>, and it is right to where it stands: it reads the vendor off a materialised
    /// row, after the borrowed rung has been applied, and it is deciding whether to spend a model
    /// call. This clause reads a stored column that is a rung short of that, so <c>Unknown</c> is
    /// the value most likely to be wrong about the row - a posting with no link of its own is
    /// exactly the posting a borrow rescues. It is also a different fact from the one the option is
    /// named after, and the shortlist facet next door already spells "exclude aggregators" as this
    /// same single comparison; two readings of one phrase in one file is the drift this codebase
    /// keeps paying for.
    /// </remarks>
    [Fact]
    public async Task A_posting_with_nothing_to_open_is_not_an_aggregator_and_is_kept()
    {
        await using (var seed = CreateContext())
        {
            var posting = await seed.JobPostings.SingleAsync(p => p.Id == NotYetDerived);

            posting.JobUrl = null;
            posting.ApplyVendor = JobPostingEntity.VendorOf(null, null, null);

            await seed.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            // Unknown is a verdict - there is nothing at the end of this link - and the queue
            // reports it per row, so a caller that means "only postings I can open" has channel
            // and applyUrlSource to say so with.
            Assert.Equal(
                AtsVendor.Unknown,
                (await db.JobPostings.SingleAsync(p => p.Id == NotYetDerived)).ApplyVendor);
        }

        var rows = await QueueAsync(new ApplyableQuery { ExcludeAggregators = true, Limit = 50 });
        var row = Assert.Single(rows, entry => entry.PostingId == NotYetDerived);

        Assert.Equal(AtsVendor.Unknown, row.AtsVendor);
        Assert.False(row.AtsVendor.IsEmployerAts());
    }
}
