using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The fifth clause of the apply queue - a park held for want of a CV - and the park that writes
/// what it is waiting on, against a real engine.
/// </summary>
/// <remarks>
/// <b>This is the corner the CV library turns on, and everything here is about one asymmetry:</b>
/// a posting parked <c>NoCvVariant</c> must come back when a covering variant exists and must not
/// come back before. Offered again next run it meets the same advert, scores the same library,
/// computes the same gap and is parked again - a loop, not a retry, and one that a single missing
/// CV inflicts on every posting that wanted it, on every run. Released on any authoring event it
/// is the same loop at a longer period, paid for by the person who has just written a CV.
///
/// <b>So the tests come in pairs, and the negative half of each pair is the one that matters.</b>
/// A CV covering part of the gap does not release; two CVs covering it between them do not
/// release; a park that recorded nothing does not release; another candidate's library does not
/// release. Each of those is a way the clause could be written that still passes the happy case,
/// and a posting missing from a list is not something anybody notices.
///
/// <b>The vocabulary is seeded from <c>concepts.json</c> rather than invented here</b>, through
/// the same <see cref="ConceptSeeder"/> production runs. Coverage is a join through
/// <c>ConceptClosure</c> - a CV naming EKS answers an advert asking for Kubernetes, exactly as
/// <c>MatchScorer</c> credits a specialisation - and a hand-written closure would be this file's
/// opinion of that relation rather than the vocabulary's. It also lets the gap brief run over the
/// real graph, so the labels and the discriminating flag are the ones production reads.
///
/// <b>Nothing here relies on either engine's NULL semantics.</b> SQL Server treats two NULLs as
/// equal in a unique index and SQLite, like the standard, treats them as distinct, so a rule
/// resting on that would be a production guarantee this file could not test. It does not arise:
/// every key column of <c>SubmissionParkGaps</c> and <c>CvVariantConcepts</c> is required. The one
/// nullable value the clause reads is <c>Submissions.ParkedAtUtc</c>, and it is <i>compared</i>
/// rather than keyed on - a comparison against NULL is UNKNOWN under both engines and under the
/// standard, so the row falls on the holding side identically on both.
/// <c>A_park_with_no_instant_holds_the_posting_rather_than_releasing_it</c> is what says so out
/// loud, because that is the one place a reader might wonder.
/// </remarks>
public sealed class CvParkQueueTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const long OtherProfileId = 2;

    private const string Kubernetes = "skill.kubernetes";
    private const string Terraform = "skill.terraform";
    private const string DotNet = "skill.dotnet";
    private const string Aws = "skill.aws";

    /// <summary>Elastic Kubernetes Service, whose <c>broader</c> edge is Kubernetes itself.</summary>
    /// <remarks>
    /// Taken from the vocabulary rather than made up, because the specialisation test is only
    /// worth anything if the edge it walks is one production actually has.
    /// </remarks>
    private const string Eks = "skill.eks";

    public CvParkQueueTests()
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

        for (var id = 1; id <= 4; id++)
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
                LocationCity = "London",
                LocationRaw = "London, UK",
                JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
                JobUrlDirect = $"https://boards.greenhouse.io/company{id}/jobs/{id}",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });

            // Judged worth applying to, so every exclusion this file asserts is the park's doing
            // and never the verdict's.
            db.JobMatches.Add(new JobMatchEntity
            {
                ProfileId = ProfileId,
                PostingId = id,
                Score = 70,
                RankScore = 10 - id,
                ScoredAtUtc = Now,
                Verdict = CandidacyVerdict.Strong,
                AssessmentScore = 80,
                AssessedAtUtc = Now,
                ScorerVersion = MatchResult.CurrentVersion,
            });
        }

        db.SaveChanges();

        // The projection production runs, over the vocabulary shipped in the build. The closure it
        // writes - self rows at depth 0 included - is what the release clause joins through.
        ConceptSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    // -----------------------------------------------------------------------
    // What a park for a missing CV does to the queue
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_posting_parked_for_want_of_a_cv_leaves_the_queue()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, Terraform);

        var ids = await QueueAsync();

        Assert.Equal([2, 3, 4], ids);
    }

    /// <summary>A CV answering part of what was missing brings the posting back to be re-judged.</summary>
    /// <remarks>
    /// <b>Releasing a posting sends nothing.</b> It returns to the queue, where
    /// <c>CvVariantSelector</c> runs again and applies its floor and its margin; a library that
    /// still does not fit yields NoFit and parks it once more, at the cost of arithmetic. So this
    /// clause is not the thing that stops a badly aimed application - selection is - and asking it
    /// to be makes it wrong in the expensive direction, which is the next test.
    /// </remarks>
    [Fact]
    public async Task A_variant_covering_part_of_the_gap_returns_the_posting()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, Terraform);

        await WriteVariantAsync("Platform", [Kubernetes]);

        Assert.Contains(1L, await QueueAsync());
    }

    /// <summary>
    /// The case that decides the rule: a CV good enough to be chosen, behind gaps it does not fill.
    /// </summary>
    /// <remarks>
    /// <b>Requiring one variant to cover every recorded gap strands this posting for ever.</b>
    /// Selection's floor is half the stated requirements rather than all of them, so a park that
    /// recorded four missing concepts is answerable by a CV covering three of them and much else -
    /// and under the stricter reading that posting stays parked silently while the CV that would
    /// have been chosen sits in the library. A wrong release costs one re-park; a wrong hold costs
    /// the application, and nothing reports it.
    /// </remarks>
    [Fact]
    public async Task A_posting_whose_gaps_are_mostly_covered_comes_back_to_be_judged()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, Terraform, DotNet, Aws);

        await WriteVariantAsync("Platform engineering", [Kubernetes, Terraform, DotNet]);

        Assert.Contains(1L, await QueueAsync());
    }

    /// <summary>
    /// Two CVs covering the gap between them, neither covering it alone.
    /// </summary>
    /// <remarks>
    /// The posting comes back, and what goes out is still one document: selection scores each
    /// variant on its own and can choose neither. The queue's job here is to stop asking the same
    /// question of an unchanged library, not to answer it.
    /// </remarks>
    [Fact]
    public async Task Two_variants_that_cover_the_gap_between_them_return_the_posting()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, Terraform);

        await WriteVariantAsync("Platform", [Kubernetes]);
        await WriteVariantAsync("Infrastructure", [Terraform]);

        Assert.Contains(1L, await QueueAsync());
    }

    [Fact]
    public async Task The_posting_returns_once_one_variant_covers_every_concept_it_named()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, Terraform);

        await WriteVariantAsync("Platform engineering", [Kubernetes, Terraform]);

        // On the first run afterwards, and without anything having unparked the row: the park
        // stands and stops holding the posting, which is what "returns when a covering variant is
        // written" has to mean when parking is an attribute rather than an event.
        Assert.Contains(1L, await QueueAsync());

        await using var db = CreateContext();

        var parked = await db.Submissions.SingleAsync(s => s.PostingId == 1);

        Assert.Equal(ParkReason.NoCvVariant, parked.ParkedReason);
        Assert.Null(parked.UnparkedAtUtc);
    }

    /// <summary>One CV releases every posting waiting on the same gap.</summary>
    /// <remarks>
    /// The many-to-one release, which is the reason parking had to be an attribute rather than an
    /// event: an append-only log with no eraser cannot express one document letting a dozen
    /// postings back at once.
    /// </remarks>
    [Fact]
    public async Task One_variant_returns_every_posting_that_was_waiting_on_the_same_gap()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes);
        await ParkForCvAsync(postingId: 2, Kubernetes);
        await ParkForCvAsync(postingId: 3, DotNet);

        await WriteVariantAsync("Platform", [Kubernetes]);

        var ids = await QueueAsync();

        Assert.Contains(1L, ids);
        Assert.Contains(2L, ids);

        // And nothing else. Posting 3 waits on a CV nobody has written, and a release keyed on the
        // library having grown would have handed back all three.
        Assert.DoesNotContain(3L, ids);
    }

    /// <summary>A CV naming a specialisation covers the demand above it.</summary>
    /// <remarks>
    /// The join goes through <c>ConceptClosure</c> in the direction <c>MatchScorer</c> credits -
    /// the gap is the ancestor, the variant's concept the descendant - so the release agrees with
    /// the scoring that parked the posting in the first place. Comparing concept ids alone would
    /// hold this posting against a CV whose whole subject is the thing it asked for.
    /// </remarks>
    [Fact]
    public async Task A_variant_naming_a_specialisation_covers_the_demand_above_it()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes);

        await WriteVariantAsync("AWS platform", [Eks]);

        Assert.Contains(1L, await QueueAsync());
    }

    /// <summary>A park that recorded nothing is held, not released.</summary>
    /// <remarks>
    /// A universal over an empty set is vacuously true, so a clause that did not ask for standing
    /// gaps would release this the moment any CV existed - the loop arriving through an oversight
    /// rather than through a decision. Holding costs a delay the gap brief already names; the
    /// posting is in <see cref="JobMatchRepository.ListCvBlockedPostingsAsync"/>'s answer with an
    /// empty set, which is what makes it visible rather than merely absent.
    /// </remarks>
    [Fact]
    public async Task A_park_that_recorded_no_gaps_is_held_rather_than_released()
    {
        await ParkWithNoGapsAsync(postingId: 1);

        await WriteVariantAsync("Backend .NET", [DotNet]);

        Assert.DoesNotContain(1L, await QueueAsync());
    }

    /// <summary>
    /// A park with no instant holds the posting, on both engines.
    /// </summary>
    /// <remarks>
    /// The one nullable value the clause reads. <c>RecordedAtUtc &gt;= ParkedAtUtc</c> against a
    /// NULL is UNKNOWN under the standard, under SQLite and under SQL Server alike, so the gap
    /// does not stand and the posting is held - which is the direction every judgement call on
    /// this path takes. Written directly rather than through the repository because the repository
    /// cannot produce this row; it is here so that a later reader knows the answer was chosen
    /// rather than inherited from whichever engine the tests happen to run on.
    /// </remarks>
    [Fact]
    public async Task A_park_with_no_instant_holds_the_posting_rather_than_releasing_it()
    {
        await using (var write = CreateContext())
        {
            var submission = new SubmissionEntity
            {
                ProfileId = ProfileId,
                PostingId = 1,
                Channel = SubmissionChannel.Unknown,
                CreatedAtUtc = Now,
                ParkedReason = ParkReason.NoCvVariant,
                ParkedAtUtc = null,
            };

            write.Submissions.Add(submission);
            await write.SaveChangesAsync();

            write.SubmissionParkGaps.Add(new SubmissionParkGapEntity
            {
                SubmissionId = submission.Id,
                ConceptId = await ConceptIdAsync(write, Kubernetes),
                RecordedAtUtc = Now,
            });

            await write.SaveChangesAsync();
        }

        await WriteVariantAsync("Platform", [Kubernetes]);

        Assert.DoesNotContain(1L, await QueueAsync());
    }

    /// <summary>Another candidate's library releases nothing here.</summary>
    /// <remarks>
    /// The clause matches variants on the profile the park belongs to, and the failure it prevents
    /// is not a performance one: a library is somebody's own documents, and a queue that consulted
    /// the wrong one would offer an application it has no CV for.
    /// </remarks>
    [Fact]
    public async Task Another_candidates_library_does_not_return_this_candidates_posting()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes);

        await WriteVariantAsync("Platform", [Kubernetes], OtherProfileId);

        Assert.DoesNotContain(1L, await QueueAsync());
    }

    /// <summary>An archived or unrendered variant covers nothing.</summary>
    /// <remarks>
    /// Archiving is what removes a CV from selection, and a variant with no current PDF has no URL
    /// for the pack to hand over - so choosing it produces a pack whose file the browser loop
    /// discovers is missing at the upload box, after the tab is open. Both exclusions belong to the
    /// release clause too, or a posting comes back to be applied to with a document that cannot be
    /// sent.
    /// </remarks>
    [Fact]
    public async Task An_archived_or_unrendered_variant_does_not_return_the_posting()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes);

        await WriteVariantAsync("Retired platform", [Kubernetes], archived: true);
        await WriteVariantAsync("Draft platform", [Kubernetes], rendered: false);

        Assert.DoesNotContain(1L, await QueueAsync());

        await WriteVariantAsync("Platform", [Kubernetes]);

        Assert.Contains(1L, await QueueAsync());
    }

    /// <summary>
    /// The SQL shadow of sendability answers what the pure property answers, row for row.
    /// </summary>
    /// <remarks>
    /// <c>CvVariant.IsSendable</c> is the rule and has no SQL; <c>CvVariantEntity.Sendable</c> is
    /// the one expression the queue and the pack compose instead of respelling it. Two spellings of
    /// one rule is the shortlist channel filter's problem, survivable only because a test holds
    /// them together - so this is that test for this pair, and it covers the cases the two could
    /// disagree on: an edit that outran its render, a render stamped before the text it came from,
    /// and an empty path where Core reads whitespace.
    /// </remarks>
    [Fact]
    public async Task The_sendable_expression_agrees_with_the_pure_property_row_for_row()
    {
        await using (var write = CreateContext())
        {
            write.CvVariants.AddRange(
                Variant("Rendered"),
                Variant("Archived", archived: true),
                Variant("Never rendered", rendered: false),
                Edited(Variant("Edited since rendering")),
                Backdated(Variant("Rendered before it was written")),
                Pathless(Variant("Stamped with no file")));

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        var rows = await db.CvVariants.AsNoTracking().OrderBy(v => v.Id).ToListAsync();

        var translated = await db.CvVariants
            .Where(CvVariantEntity.Sendable)
            .Select(v => v.Id)
            .ToListAsync();

        Assert.NotEmpty(translated);

        foreach (var row in rows)
        {
            var core = new CvVariant
            {
                Id = row.Id,
                Label = row.Label,
                Markdown = row.Markdown,
                AuthoredAtUtc = row.AuthoredAtUtc,
                RenderedAtUtc = row.RenderedAtUtc,
                PdfBlobPath = row.PdfBlobPath,
                DocxBlobPath = row.DocxBlobPath,
                Sha256 = row.Sha256,
                IsArchived = row.IsArchived,
            };

            Assert.Equal(core.IsSendable, translated.Contains(row.Id));
        }
    }

    // -----------------------------------------------------------------------
    // What a park records, and what a re-park replaces
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_park_for_a_missing_cv_records_the_concepts_it_is_waiting_on()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, Terraform);

        await using var db = CreateContext();

        var stored = await db.SubmissionParkGaps
            .Include(gap => gap.Concept)
            .OrderBy(gap => gap.Concept!.ConceptKey)
            .ToListAsync();

        Assert.Equal([Kubernetes, Terraform], stored.Select(gap => gap.Concept!.ConceptKey));

        // Stamped from the park's own instant and never from a second clock read: the two are
        // compared, and a writer a millisecond early would drop every gap it had just written out
        // of the predicate, leaving the empty set the clause has to refuse to read as coverage.
        Assert.All(stored, gap => Assert.Equal(Now, gap.RecordedAtUtc));
    }

    /// <summary>Only a reason that waits on a CV records what it is missing.</summary>
    /// <remarks>
    /// The rule <c>AwaitingQuestionId</c> already follows one line above it in the writer: a
    /// captcha must not inherit a gap, and a caller passing concepts with the wrong reason must not
    /// be able to hold a posting that nothing is waiting on.
    /// </remarks>
    [Fact]
    public async Task A_park_for_any_other_reason_records_no_gaps()
    {
        await using (var db = CreateContext())
        {
            await new SubmissionRepository(db).ParkAsync(
                ProfileId, 1, ParkReason.Captcha, Now, missingConceptKeys: [Kubernetes]);
        }

        await using var read = CreateContext();

        Assert.Empty(await read.SubmissionParkGaps.ToListAsync());
    }

    /// <summary>A key the projection does not carry cannot be recorded.</summary>
    /// <remarks>
    /// The join is on the concept id, so there is nothing to write for a key with no row. Every key
    /// that reaches the writer came off a <c>PostingConcepts</c> row and has one, so this is the
    /// state <c>dbadmin seed-concepts</c> exists to prevent rather than an ordinary one - and it is
    /// pinned so that the direction of the loss is on the record: one gap fewer costs a re-park,
    /// where a park refused outright would lose the reason the attempt was abandoned.
    /// </remarks>
    [Fact]
    public async Task A_key_the_vocabulary_does_not_carry_is_dropped_rather_than_refusing_the_park()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, "skill.nothing-of-the-sort");

        await using var db = CreateContext();

        var gap = await db.SubmissionParkGaps.Include(g => g.Concept).SingleAsync();

        Assert.Equal(Kubernetes, gap.Concept!.ConceptKey);
        Assert.Equal(ParkReason.NoCvVariant, (await db.Submissions.SingleAsync()).ParkedReason);
    }

    /// <summary>Parking again on the same gaps does not walk the park forward.</summary>
    /// <remarks>
    /// Idempotent by state, exactly as the reason already is: a nightly pass meeting the same
    /// advert must not turn "blocked since Tuesday" into "blocked a minute ago", which is the fact
    /// somebody reading the queue is actually after.
    /// </remarks>
    [Fact]
    public async Task Parking_again_on_the_same_gaps_leaves_the_park_where_it_was()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, Terraform);
        await ParkForCvAsync(postingId: 1, Now.AddHours(2), Terraform, Kubernetes);

        await using var db = CreateContext();

        Assert.Equal(Now, (await db.Submissions.SingleAsync()).ParkedAtUtc);

        var stored = await db.SubmissionParkGaps.ToListAsync();

        Assert.Equal(2, stored.Count);
        Assert.All(stored, gap => Assert.Equal(Now, gap.RecordedAtUtc));
    }

    /// <summary>
    /// A re-park on different gaps replaces them, and nothing is deleted to say so.
    /// </summary>
    /// <remarks>
    /// The library moved underneath the advert, so what the park waits on is what this pass
    /// computed rather than the union of every pass. The superseded row stays where it is and
    /// leaves the predicate by arithmetic - the rule <c>SubmissionParkGapEntity</c> lives under,
    /// and what keeps "what was this waiting for in March" answerable. It is also why the park's
    /// own instant moves: leaving it would have both sets standing at once, and the posting would
    /// be held against a concept nothing is missing any more.
    /// </remarks>
    [Fact]
    public async Task A_re_park_on_different_gaps_supersedes_the_ones_before_without_deleting_them()
    {
        var later = Now.AddDays(1);

        await ParkForCvAsync(postingId: 1, Kubernetes);
        await ParkForCvAsync(postingId: 1, later, Terraform);

        await using (var db = CreateContext())
        {
            var submission = await db.Submissions.SingleAsync();

            Assert.Equal(later, submission.ParkedAtUtc);

            var stored = await db.SubmissionParkGaps
                .Include(gap => gap.Concept)
                .ToListAsync();

            // Both rows are still there. Only one of them stands.
            Assert.Equal(2, stored.Count);

            var standing = stored
                .Where(gap => gap.RecordedAtUtc >= submission.ParkedAtUtc)
                .Select(gap => gap.Concept!.ConceptKey);

            Assert.Equal([Terraform], standing);
        }

        // So the CV the first park was waiting on no longer releases anything...
        await WriteVariantAsync("Platform", [Kubernetes]);

        Assert.DoesNotContain(1L, await QueueAsync());

        // ...and the one this park is waiting on does.
        await WriteVariantAsync("Infrastructure", [Terraform]);

        Assert.Contains(1L, await QueueAsync());
    }

    /// <summary>A park for a different reason leaves the gaps behind without them standing.</summary>
    /// <remarks>
    /// Nothing is cleared when the reason changes: the reason moving is already enough to move the
    /// park's instant, so the gaps of the park it replaced stop standing without a row being
    /// touched. That is the whole of how this table supersedes, and it is what stops a captcha
    /// inheriting a gap when the advert is met again.
    /// </remarks>
    [Fact]
    public async Task A_re_park_for_another_reason_leaves_the_old_gaps_standing_on_nothing()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes);

        await using (var db = CreateContext())
        {
            await new SubmissionRepository(db).ParkAsync(
                ProfileId, 1, ParkReason.Captcha, Now.AddHours(1));
        }

        await using var read = CreateContext();

        var submission = await read.Submissions.SingleAsync();

        Assert.Equal(ParkReason.Captcha, submission.ParkedReason);
        Assert.Equal(Now.AddHours(1), submission.ParkedAtUtc);

        var gap = await read.SubmissionParkGaps.SingleAsync();

        Assert.True(gap.RecordedAtUtc < submission.ParkedAtUtc);
    }

    // -----------------------------------------------------------------------
    // What the gap brief is computed over
    // -----------------------------------------------------------------------

    /// <summary>
    /// The read hands the brief what each park recorded, and computes no difference of its own.
    /// </summary>
    /// <remarks>
    /// <c>CvGapBrief.Compute</c> takes already-differenced input deliberately, so that the Data
    /// layer does not become a second definition of "covers". What is asserted here is the join and
    /// the shape - one entry per parked posting, keys as the vocabulary spells them - and then that
    /// the pure ranking on top of it answers the question the standing view asks.
    /// </remarks>
    [Fact]
    public async Task The_blocked_read_returns_the_concept_keys_each_park_recorded()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes, Terraform);
        await ParkForCvAsync(postingId: 2, Kubernetes);
        await ParkForCvAsync(postingId: 3, DotNet);

        var blocked = await BlockedAsync();

        Assert.Equal([1, 2, 3], blocked.Select(posting => posting.PostingId));
        Assert.Equal([Kubernetes, Terraform], blocked[0].MissingConcepts);
        Assert.Equal([Kubernetes], blocked[1].MissingConcepts);

        var brief = CvGapBrief.Compute(blocked, ConceptGraph.Default);

        Assert.Equal(3, brief.BlockedPostings);

        // Kubernetes blocks two postings and .NET one, and the floor is two - so the brief names
        // one CV worth writing rather than three things to feel bad about.
        var gap = Assert.Single(brief.Gaps);

        Assert.Equal(Kubernetes, gap.Concepts[0].Key);
        Assert.Equal("Kubernetes", gap.Concepts[0].Label);
        Assert.Equal(2, gap.Postings);
    }

    /// <summary>A posting with nothing recorded still counts in the brief's total.</summary>
    /// <remarks>
    /// It is blocked - parked for want of a CV, and nothing has released it - so dropping it here
    /// would make the headline count disagree with the queue. A brief whose gaps do not account for
    /// its own total is saying something worth hearing: postings are being parked over requirements
    /// that are all tags, all domains or all unnameable, which is a selection or vocabulary fault
    /// rather than a document anybody can write.
    /// </remarks>
    [Fact]
    public async Task A_posting_whose_park_recorded_nothing_still_counts_as_blocked()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes);
        await ParkWithNoGapsAsync(postingId: 2);

        var blocked = await BlockedAsync();

        Assert.Equal([1, 2], blocked.Select(posting => posting.PostingId));
        Assert.Empty(blocked[1].MissingConcepts);

        var brief = CvGapBrief.Compute(blocked, ConceptGraph.Default);

        Assert.Equal(2, brief.BlockedPostings);
        Assert.Empty(brief.Gaps);
    }

    /// <summary>Only the gaps of the park that stands reach the brief.</summary>
    /// <remarks>
    /// The same standing-set arithmetic the queue clause applies, for the same reason: a concept
    /// the last park was waiting on has no business at the top of somebody's Saturday afternoon.
    /// </remarks>
    [Fact]
    public async Task The_brief_reads_the_gaps_of_the_park_that_stands()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes);
        await ParkForCvAsync(postingId: 1, Now.AddDays(1), Terraform);

        var blocked = await BlockedAsync();

        Assert.Equal([Terraform], Assert.Single(blocked).MissingConcepts);
    }

    /// <summary>An unparked row is not blocked on anything.</summary>
    [Fact]
    public async Task A_park_that_has_been_let_back_in_is_not_reported_as_blocked()
    {
        await ParkForCvAsync(postingId: 1, Kubernetes);

        await using (var db = CreateContext())
        {
            await new SubmissionRepository(db).UnparkAsync(ProfileId, 1, Now.AddHours(1));
        }

        Assert.Empty(await BlockedAsync());
    }

    // -----------------------------------------------------------------------
    // The pair of tests every reader of a park has to agree on
    // -----------------------------------------------------------------------

    /// <summary>
    /// What the queue considers, asked about one pair, agrees with what the queue returns.
    /// </summary>
    /// <remarks>
    /// <b>The third spelling of two tests, pinned against the first.</b>
    /// <see cref="JobMatchRepository.IsQueueEligibleAsync"/> exists so <c>get_submission_pack</c>
    /// cannot park a posting <see cref="JobMatchRepository.ListCvBlockedPostingsAsync"/> then
    /// filters out - a standing block no report accounts for. Three copies of a predicate is
    /// exactly the arrangement that drifts, so the two are asserted against each other here rather
    /// than trusted to stay in step: a posting the queue offers is eligible, and one it has never
    /// judged is not.
    ///
    /// A dismissal is asserted alongside because it is the half that is a decision rather than a
    /// timing accident. An unjudged posting is one the nightly pass has not reached; a dismissed
    /// one is the candidate saying no, and a park on it argues a Saturday's work from a vacancy
    /// they have already refused.
    /// </remarks>
    [Fact]
    public async Task Queue_eligibility_answers_for_one_pair_what_the_queue_answers_for_all()
    {
        await using (var db = CreateContext())
        {
            var unjudged = await db.JobMatches.SingleAsync(m => m.PostingId == 2);

            unjudged.Verdict = null;
            unjudged.AssessmentScore = null;
            unjudged.AssessedAtUtc = null;

            var dismissed = await db.JobMatches.SingleAsync(m => m.PostingId == 3);

            dismissed.DismissedAtUtc = Now;

            await db.SaveChangesAsync();
        }

        var offered = await QueueAsync();

        Assert.Equal([1L, 4L], offered);

        await using var read = CreateContext();
        var matches = new JobMatchRepository(read);

        Assert.True(await matches.IsQueueEligibleAsync(ProfileId, 1));
        Assert.False(await matches.IsQueueEligibleAsync(ProfileId, 2));
        Assert.False(await matches.IsQueueEligibleAsync(ProfileId, 3));

        // A posting this candidate was never scored against, which is the ordinary way a model
        // names an id that is not theirs. It answers the same as a dismissal rather than throwing:
        // there is nothing to park either way.
        Assert.False(await matches.IsQueueEligibleAsync(ProfileId, 99));
    }

    // -----------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------

    private async Task<long[]> QueueAsync()
    {
        await using var db = CreateContext();

        var rows = await new JobMatchRepository(db)
            .ListApplyableAsync(ProfileId, new ApplyableQuery { Limit = 50 });

        return [.. rows.Select(row => row.PostingId).Order()];
    }

    private async Task<IReadOnlyList<BlockedPosting>> BlockedAsync()
    {
        await using var db = CreateContext();

        return await new JobMatchRepository(db).ListCvBlockedPostingsAsync(ProfileId);
    }

    /// <summary>
    /// A posting put down for want of a CV, through the writer rather than around it.
    /// </summary>
    /// <remarks>
    /// The opposite choice from <c>CvLibrarySchemaTests</c>, which writes these rows directly
    /// because what it pins are constraints. What is pinned here is the pair of a park and the gaps
    /// it records - the stamping, the superseding and the class of reason that gets them at all -
    /// so going through <see cref="SubmissionRepository.ParkAsync"/> is the point rather than a
    /// convenience.
    /// </remarks>
    private Task ParkForCvAsync(long postingId, params string[] concepts)
        => ParkForCvAsync(postingId, Now, concepts);

    /// <summary>
    /// A park with no gap rows, written round the repository because the repository refuses it.
    /// </summary>
    /// <remarks>
    /// <b>The state is unreachable through <c>ParkAsync</c> and still worth pinning.</b> Parking
    /// for want of a CV without naming what was missing holds the posting for ever, so the
    /// repository throws rather than storing it - but a row can arrive in that state anyway: a
    /// migration, a half-written batch, a bug in whatever computes the gaps. What the queue does
    /// when it meets one is a property of the queue, and it is asserted here by constructing the
    /// row directly, the same way the idempotency guarantees are asserted by writing round the
    /// repository that upholds them.
    /// </remarks>
    private async Task ParkWithNoGapsAsync(long postingId, DateTimeOffset? at = null)
    {
        await using var db = CreateContext();

        db.Submissions.Add(new SubmissionEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            Channel = SubmissionChannel.Unknown,
            CreatedAtUtc = at ?? Now,
            ParkedReason = ParkReason.NoCvVariant,
            ParkedAtUtc = at ?? Now,
        });

        await db.SaveChangesAsync();
    }

    private async Task ParkForCvAsync(long postingId, DateTimeOffset at, params string[] concepts)
    {
        await using var db = CreateContext();

        await new SubmissionRepository(db).ParkAsync(
            ProfileId, postingId, ParkReason.NoCvVariant, at, missingConceptKeys: concepts);
    }

    /// <summary>One CV in the library, with what an extractor read out of it.</summary>
    /// <remarks>
    /// The concepts are a list rather than a <c>params</c> tail so that the flags after them have
    /// to be named at the call site. Two adjacent booleans the compiler cannot tell apart is the
    /// signature <c>ApplyableQuery</c> exists to avoid, and "archived" and "unrendered" mean
    /// opposite things to a reader while meaning the same thing to the release clause.
    /// </remarks>
    private async Task<long> WriteVariantAsync(
        string label,
        string[] concepts,
        long profileId = ProfileId,
        bool archived = false,
        bool rendered = true)
    {
        await using var db = CreateContext();

        var variant = Variant(label, archived, rendered, profileId);

        db.CvVariants.Add(variant);
        await db.SaveChangesAsync();

        foreach (var key in concepts)
        {
            db.CvVariantConcepts.Add(new CvVariantConceptEntity
            {
                VariantId = variant.Id,
                ConceptId = await ConceptIdAsync(db, key),
                Source = AssertionSource.Model,
                Polarity = AssertionPolarity.Proficient,
                EvidenceText = "read out of the candidate's own markdown",
                ResolverVersion = 1,
            });
        }

        await db.SaveChangesAsync();

        return variant.Id;
    }

    private static Task<int> ConceptIdAsync(JobsDbContext db, string key)
        => db.Concepts.Where(concept => concept.ConceptKey == key).Select(c => c.Id).SingleAsync();

    private static CvVariantEntity Variant(
        string label, bool archived = false, bool rendered = true, long profileId = ProfileId)
    {
        var variant = new CvVariantEntity
        {
            ProfileId = profileId,
            Label = label,
            LabelKey = CvVariantLibrary.FoldLabel(label),
            Markdown = $"# {label}\n\nSomething the candidate wrote.",
            AuthoredAtUtc = Now,
            IsArchived = archived,
        };

        if (!rendered)
        {
            return variant;
        }

        variant.RenderedAtUtc = Now.AddMinutes(1);
        variant.PdfBlobPath = "profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.pdf";
        variant.DocxBlobPath = "profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.docx";
        variant.Sha256 = new string('a', CvVariantLimits.Sha256Length);

        return variant;
    }

    /// <summary>Edited after it was rendered, so the stored files are last week's paragraph.</summary>
    private static CvVariantEntity Edited(CvVariantEntity variant)
    {
        variant.AuthoredAtUtc = variant.RenderedAtUtc!.Value.AddMinutes(1);

        return variant;
    }

    /// <summary>Rendered before the text it came from - two hosts disagreeing, read as stale.</summary>
    private static CvVariantEntity Backdated(CvVariantEntity variant)
    {
        variant.RenderedAtUtc = variant.AuthoredAtUtc.AddMinutes(-1);

        return variant;
    }

    /// <summary>Stamped as rendered with no file behind it.</summary>
    private static CvVariantEntity Pathless(CvVariantEntity variant)
    {
        variant.PdfBlobPath = string.Empty;

        return variant;
    }

    /// <summary>
    /// The application that used a chosen CV records which one, even though a park made the row.
    /// </summary>
    /// <remarks>
    /// <b>The sequence the whole feature exists for, and the one that recorded nothing.</b> A
    /// posting nothing fits is parked <c>NoCvVariant</c>, and parking CREATES the submission row.
    /// The candidate writes the covering CV, the posting returns, the pack chooses it and the loop
    /// applies - and <c>create_submission</c> then takes its existing-row path, where the variant
    /// id was only ever written on the insert. So the applications the library was built to make
    /// possible were exactly the ones recording no CV, which is the correlation C4 was specified
    /// to provide and would have been silently empty.
    /// </remarks>
    [Fact]
    public async Task An_application_after_a_park_records_the_variant_that_was_chosen()
    {
        var variantId = await WriteVariantAsync("Platform engineering", [Kubernetes, Terraform]);

        await using var db = CreateContext();

        var repository = new SubmissionRepository(db);

        var (parked, _) = await repository.ParkAsync(
            ProfileId, 1, ParkReason.NoCvVariant, Now,
            missingConceptKeys: [Kubernetes, Terraform]);

        Assert.Null((await db.Submissions.SingleAsync(x => x.Id == parked.Id)).CvVariantId);

        var result = await repository.CreateWithEventAsync(
            ProfileId, 1, SubmissionChannel.Ats, "https://ats.example.invalid/1",
            new SubmissionEvent(Now.AddDays(1), SubmissionEventType.Submitted, null, SubmissionEventSource.Client, null),
            "run-2:1:Submitted",
            Now.AddDays(1),
            cvVariantId: variantId);

        Assert.Equal(SubmissionEventResult.Recorded, result.Event);

        await using var read = CreateContext();

        var stored = await read.Submissions.SingleAsync(x => x.PostingId == 1);

        Assert.Equal(variantId, stored.CvVariantId);
        Assert.Equal(CvSelection.CurrentVersion, stored.CvSelectionVersion);
    }

    /// <summary>A CV already recorded is never overwritten by a later claim naming another.</summary>
    /// <remarks>
    /// The other half of the rule, and the reason the fill is a fill rather than an assignment: a
    /// second call naming a different variant is a client that re-fetched the pack after the
    /// library moved, and the document the employer actually received is the one the first call
    /// named.
    /// </remarks>
    [Fact]
    public async Task A_second_claim_naming_another_variant_does_not_move_what_was_sent()
    {
        var sent = await WriteVariantAsync("Platform engineering", [Kubernetes, Terraform]);
        var other = await WriteVariantAsync("Backend .NET", [DotNet]);

        await using var db = CreateContext();

        var repository = new SubmissionRepository(db);

        await repository.CreateWithEventAsync(
            ProfileId, 1, SubmissionChannel.Ats, null,
            new SubmissionEvent(Now, SubmissionEventType.Submitted, null, SubmissionEventSource.Client, null),
            "k1", Now, cvVariantId: sent);

        await repository.CreateWithEventAsync(
            ProfileId, 1, SubmissionChannel.Ats, null,
            new SubmissionEvent(Now.AddDays(1), SubmissionEventType.Acknowledged, null, SubmissionEventSource.Client, null),
            "k2", Now.AddDays(1), cvVariantId: other);

        await using var read = CreateContext();

        Assert.Equal(sent, (await read.Submissions.SingleAsync(x => x.PostingId == 1)).CvVariantId);
    }

    /// <summary>
    /// Parking for want of a CV without saying what was missing is refused, not stored.
    /// </summary>
    /// <remarks>
    /// The queue holds a park with no standing gaps deliberately, so that a park whose rows failed
    /// to be written is not released by the mere existence of any CV. The cost of that decision is
    /// that an empty set is permanent - the posting leaves the queue and no document written
    /// afterwards brings it back, and nothing reports it. It is one missing argument away at every
    /// call site, so it is refused where every call site passes through.
    /// </remarks>
    [Fact]
    public async Task A_park_for_want_of_a_cv_must_say_what_was_missing()
    {
        await using var db = CreateContext();

        var repository = new SubmissionRepository(db);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ParkAsync(
            ProfileId, 1, ParkReason.NoCvVariant, Now, missingConceptKeys: []));

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ParkAsync(
            ProfileId, 1, ParkReason.NoCvVariant, Now));

        // And nothing was written, so the refusal is not a half-park somebody has to unpick.
        Assert.Empty(await db.Submissions.ToListAsync());
    }

    /// <summary>Every other reason still parks without one, because none of them waits on a CV.</summary>
    [Fact]
    public async Task A_park_for_any_other_reason_needs_no_concepts()
    {
        await using var db = CreateContext();

        var (row, created) = await new SubmissionRepository(db).ParkAsync(
            ProfileId, 1, ParkReason.Captcha, Now);

        Assert.True(created);
        Assert.Equal(ParkReason.Captcha, row.ParkedReason);
    }


}
