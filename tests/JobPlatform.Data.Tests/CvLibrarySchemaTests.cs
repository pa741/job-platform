using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The CV library's schema, against a real relational engine.
/// </summary>
/// <remarks>
/// <b>These tests are about the database and not about a repository.</b> Everything here writes
/// through <see cref="JobsDbContext"/> directly, because what is being pinned are constraints
/// rather than checks: a rule that holds only while callers behave is not one, and the callers here
/// are an unattended pass and a person with two browser tabs open on the same page.
///
/// <b>Nothing below relies on either engine's NULL semantics, which is what makes these worth
/// running against SQLite at all.</b> SQL Server treats two NULLs as equal in a unique index and
/// SQLite, like the standard, treats them as distinct - so an index over a nullable column would
/// be a production guarantee this file could not test and a test-suite guarantee production did
/// not have, discovered for the first time as a live constraint violation. Both key columns of
/// <c>IX_CvVariants_LiveLabel</c> are required, and so are both halves of the two composite keys,
/// so every assertion here holds identically on Azure SQL. The same technique, and the same
/// reasoning, as <c>ConfigureFormAnswers</c>.
///
/// <b>Uniqueness is over the folded label, and the fold happens in Core.</b> That is the second
/// engine difference this file is written around: SQLite compares strings under <c>BINARY</c> and
/// Azure SQL under a case-insensitive collation, so an index over the label as typed would enforce
/// two different rules. <see cref="CvVariantLibrary.FoldLabel"/> is called here rather than
/// imitated, for the reason it is the only writer in production too.
/// </remarks>
public sealed class CvLibrarySchemaTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const long OtherProfileId = 2;

    private const int Kubernetes = 1;
    private const int Terraform = 2;
    private const int DotNet = 3;

    /// <summary>A digest of the right shape. The content is irrelevant; the width is not.</summary>
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public CvLibrarySchemaTests()
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
                JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        foreach (var (id, key, label) in new[]
                 {
                     (Kubernetes, "skill.kubernetes", "Kubernetes"),
                     (Terraform, "skill.terraform", "Terraform"),
                     (DotNet, "skill.dotnet", ".NET"),
                 })
        {
            db.Concepts.Add(new ConceptEntity
            {
                Id = id,
                ConceptKey = key,
                PrefLabel = label,
                Kind = ConceptKind.Skill,
                TaxonomyVersion = 1,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    // -----------------------------------------------------------------------
    // One label per candidate, among the variants still in use
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_second_live_variant_may_not_take_a_label_another_is_using()
    {
        await using var db = CreateContext();

        db.CvVariants.Add(Variant("Backend .NET"));
        await db.SaveChangesAsync();

        db.CvVariants.Add(Variant("Backend .NET"));

        // At the database, not at a repository. A repository that reads the library and then
        // inserts is two statements with a gap in the middle, and two tabs saving a second apart
        // both find the name free.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_label_differing_only_in_case_or_spacing_is_the_same_label()
    {
        await using var db = CreateContext();

        db.CvVariants.Add(Variant("Backend .NET"));
        await db.SaveChangesAsync();

        // "Backend .NET" and "backend  .net" are the same label to anybody reading a picker, and
        // the pack's account of which CV it chose has to be checkable by the person reading it.
        // The fold is done by Core and stored, so the constraint means this on both engines rather
        // than meaning whatever the collation happens to say.
        db.CvVariants.Add(Variant("backend  .net"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Archiving_a_variant_gives_its_name_back_to_the_cv_that_replaces_it()
    {
        await using var db = CreateContext();

        var first = Variant("Backend .NET");
        db.CvVariants.Add(first);
        await db.SaveChangesAsync();

        // The ordinary case, and the whole reason the index is filtered: rewriting a CV and giving
        // the new one the old one's name. A rule reserving a label forever would push people into
        // calling their CVs "Backend .NET v3" to get around a constraint meant to help them.
        first.IsArchived = true;
        db.CvVariants.Add(Variant("Backend .NET"));
        await db.SaveChangesAsync();

        var stored = await db.CvVariants.Where(v => v.ProfileId == ProfileId).ToListAsync();

        // Both rows survive, because the archived one is still what an application made last year
        // was sent - and the submission that records it names it by id.
        Assert.Equal(2, stored.Count);
        Assert.Single(stored, v => !v.IsArchived);
    }

    [Fact]
    public async Task Several_archived_variants_may_share_one_label()
    {
        await using var db = CreateContext();

        db.CvVariants.Add(Variant("Backend .NET", archived: true));
        db.CvVariants.Add(Variant("Backend .NET", archived: true));
        db.CvVariants.Add(Variant("Backend .NET", archived: true));
        db.CvVariants.Add(Variant("Backend .NET"));

        // A CV rewritten three times is three archived rows and one live one, and none of them may
        // be deleted to make room. The filter is what lets the history accumulate under one name.
        await db.SaveChangesAsync();

        Assert.Equal(3, await db.CvVariants.CountAsync(v => v.IsArchived));
        Assert.Equal(4, await db.CvVariants.CountAsync());
    }

    [Fact]
    public async Task Two_candidates_may_call_a_cv_the_same_thing()
    {
        await using var db = CreateContext();

        db.CvVariants.Add(Variant("Backend .NET"));
        db.CvVariants.Add(Variant("Backend .NET", profileId: OtherProfileId));

        // The library is per person. Refusing this would tell one candidate that another exists.
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.CvVariants.CountAsync());
    }

    // -----------------------------------------------------------------------
    // The columns, at the widths their limits declare
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_variant_round_trips_at_every_bound_CvVariantLimits_declares()
    {
        var label = new string('a', CvVariantLimits.MaxLabelLength);
        var markdown = new string('m', CvVariantLimits.MaxMarkdownLength);
        var path = new string('p', CvVariantLimits.MaxBlobPathLength);

        await using (var write = CreateContext())
        {
            var variant = Variant(label);
            variant.Markdown = markdown;
            variant.PdfBlobPath = path;
            variant.DocxBlobPath = path;
            variant.Sha256 = Sha;

            write.CvVariants.Add(variant);
            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var stored = await db.CvVariants.SingleAsync();

        // The column and the validation are one decision - CvVariantLimits - so everything
        // CvVariant.Create accepts has to fit. A truncation here is a CV that ends mid-sentence in
        // front of an employer, or a path to a rendered file nobody can fetch again.
        Assert.Equal(CvVariantLimits.MaxLabelLength, stored.Label.Length);
        Assert.Equal(CvVariantLimits.MaxMarkdownLength, stored.Markdown.Length);
        Assert.Equal(CvVariantLimits.MaxBlobPathLength, stored.PdfBlobPath?.Length);
        Assert.Equal(CvVariantLimits.MaxBlobPathLength, stored.DocxBlobPath?.Length);
        Assert.Equal(CvVariantLimits.Sha256Length, stored.Sha256?.Length);
    }

    [Fact]
    public async Task The_folded_label_is_never_longer_than_the_label_it_came_from()
    {
        // LabelKey is stored at the label's own width, and this is the claim that makes that safe.
        // Folding collapses whitespace, which shrinks, and lower-cases through simple case mapping,
        // which is one character in and one out. Full case folding is the alternative that does not
        // hold - ß becomes ss, and U+0130 becomes i plus a combining dot - so the letters that would
        // break it are the ones asserted here. If FoldLabel ever adopts that spelling the column has
        // to widen with it, and this fails rather than somebody's save.
        foreach (var label in new[]
                 {
                     new string('İ', CvVariantLimits.MaxLabelLength),
                     new string('ß', CvVariantLimits.MaxLabelLength),
                     new string('a', CvVariantLimits.MaxLabelLength),
                 })
        {
            Assert.True(CvVariantLibrary.FoldLabel(label).Length <= CvVariantLimits.MaxLabelLength);
        }

        await using var db = CreateContext();

        var typed = new string('İ', CvVariantLimits.MaxLabelLength);

        db.CvVariants.Add(Variant(typed));
        await db.SaveChangesAsync();

        var stored = await db.CvVariants.SingleAsync();

        Assert.Equal(CvVariantLibrary.FoldLabel(typed), stored.LabelKey);
    }

    // -----------------------------------------------------------------------
    // Sendability, which is arithmetic over columns rather than a flag
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_sendable_rule_is_a_where_clause_the_database_runs()
    {
        await using (var write = CreateContext())
        {
            var archived = Rendered(Variant("Archived", archived: true));
            var unrendered = Variant("Never rendered");
            var stale = Rendered(Variant("Edited since it was rendered"));
            stale.AuthoredAtUtc = Now.AddHours(2);
            var sendable = Rendered(Variant("Backend .NET"));

            write.CvVariants.AddRange(archived, unrendered, stale, sendable);
            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        var query = db.CvVariants.Where(CvVariantEntity.Sendable);

        // Translated rather than evaluated in memory: EF refuses a predicate it cannot turn into
        // SQL, and the generated statement names the columns the rule is made of. Filtering after
        // materialising would read every CV this candidate has ever written to answer a question
        // the database can answer in an index.
        var sql = query.ToQueryString();

        Assert.Contains("IsArchived", sql, StringComparison.Ordinal);
        Assert.Contains("RenderedAtUtc", sql, StringComparison.Ordinal);
        Assert.Contains("AuthoredAtUtc", sql, StringComparison.Ordinal);

        var chosen = await query.Select(v => v.Label).ToListAsync();

        // Exactly the two exclusions Core makes. The edited-since-rendered row is out by
        // arithmetic - nothing had to remember to clear a flag - and it comes back the moment the
        // renderer stamps a new time.
        Assert.Equal(["Backend .NET"], chosen);
    }

    [Fact]
    public async Task A_variant_predating_the_profile_is_still_sendable()
    {
        await using (var write = CreateContext())
        {
            write.CvVariants.Add(Rendered(Variant("Backend .NET")));
            await write.SaveChangesAsync();

            var profile = await write.CandidateProfiles.AsTracking().SingleAsync(p => p.Id == ProfileId);
            profile.UpdatedUtc = Now.AddDays(30);

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        // Staleness is a nudge and never a lockout. Somebody who adds a job to their profile at
        // lunchtime must not find every CV excluded, every posting parked, and a dashboard telling
        // them they have no CVs when they have six good ones and a line to add to them.
        Assert.Single(await db.CvVariants.Where(CvVariantEntity.Sendable).ToListAsync());
    }

    // -----------------------------------------------------------------------
    // The mirror, which is the join selection is built on
    // -----------------------------------------------------------------------

    [Fact]
    public void The_variant_concept_table_mirrors_the_profile_one_column_for_column()
    {
        using var db = CreateContext();

        // Column for column, Source included, minus the owning key each side is named after. The
        // shapes are what make selection a join rather than a translation layer, and a column added
        // to one and not the other is a join that quietly starts answering a narrower question.
        var profile = Shape(db, typeof(ProfileConceptEntity), "ProfileId");
        var variant = Shape(db, typeof(CvVariantConceptEntity), "VariantId");

        Assert.Equal(profile, variant);

        // The posting side too, because that is the third table in the same join and the one both
        // of the others were shaped against.
        Assert.Equal(Shape(db, typeof(PostingConceptEntity), "PostingId"), variant);
    }

    // -----------------------------------------------------------------------
    // What was actually sent - the link outcome feedback correlates against
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_submission_records_which_variant_was_sent_and_which_rule_chose_it()
    {
        await using (var write = CreateContext())
        {
            var variant = Rendered(Variant("Backend .NET"));
            write.CvVariants.Add(variant);
            await write.SaveChangesAsync();

            write.Submissions.Add(new SubmissionEntity
            {
                ProfileId = ProfileId,
                PostingId = 1,
                Channel = SubmissionChannel.Ats,
                CreatedAtUtc = Now,
                CvVariantId = variant.Id,
                CvSelectionVersion = CvSelection.CurrentVersion,
            });

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        var stored = await db.Submissions.SingleAsync();
        var sent = await db.CvVariants.SingleAsync();

        Assert.Equal(sent.Id, stored.CvVariantId);

        // The version travels with the id, for the reason JobMatches carries ScorerVersion beside a
        // score: a floor moved halfway through a window silently averages two experiments together,
        // and the correlation is then over a rule nobody can name afterwards.
        Assert.Equal(CvSelection.CurrentVersion, stored.CvSelectionVersion);
    }

    [Fact]
    public async Task A_variant_an_application_was_sent_with_cannot_be_deleted()
    {
        long variantId;

        await using (var write = CreateContext())
        {
            var variant = Rendered(Variant("Backend .NET"));
            write.CvVariants.Add(variant);
            await write.SaveChangesAsync();

            variantId = variant.Id;

            write.Submissions.Add(new SubmissionEntity
            {
                ProfileId = ProfileId,
                PostingId = 1,
                Channel = SubmissionChannel.Ats,
                CreatedAtUtc = Now,
                CvVariantId = variantId,
            });

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        db.CvVariants.Remove(await db.CvVariants.AsTracking().SingleAsync(v => v.Id == variantId));

        // Restrict, and this is what it buys: archiving is how a variant is retired, and a delete
        // would make an application that was actually sent unexplainable. "What did we send them"
        // is a question about a file that has to still exist.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // -----------------------------------------------------------------------
    // What a NoCvVariant park is waiting on
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_park_for_a_missing_cv_records_the_concepts_it_is_waiting_on()
    {
        await using (var write = CreateContext())
        {
            await ParkAsync(write, postingId: 1, Kubernetes, Terraform);
        }

        await using var db = CreateContext();

        var stored = await db.SubmissionParkGaps.OrderBy(g => g.ConceptId).ToListAsync();

        Assert.Equal([Kubernetes, Terraform], stored.Select(g => g.ConceptId));

        // Stamped from the park's own instant and never from a second clock read: the two are
        // compared, and a park whose gaps have all fallen out of that comparison is an empty set
        // the release clause has to refuse to read as coverage.
        Assert.All(stored, gap => Assert.Equal(Now, gap.RecordedAtUtc));
    }

    [Fact]
    public async Task The_awaiting_cv_variant_reasons_become_an_IN_clause_rather_than_a_client_evaluation()
    {
        await using (var write = CreateContext())
        {
            var reasons = new[] { ParkReason.NoCvVariant, ParkReason.Captcha, ParkReason.MissingAnswer };

            for (var index = 0; index < reasons.Length; index++)
            {
                write.Submissions.Add(new SubmissionEntity
                {
                    ProfileId = ProfileId,
                    PostingId = index + 1,
                    Channel = SubmissionChannel.Ats,
                    CreatedAtUtc = Now,
                    ParkedReason = reasons[index],
                    ParkedAtUtc = Now,
                });
            }

            await write.SaveChangesAsync();
        }

        await using var db = CreateContext();

        // The third list ParkReasonPolicy derives, and it exists for exactly this: a static call
        // over a column has no SQL, so the queue asks Contains and EF turns it into an IN. Written
        // against ParkReasonPolicy.Requeue the query would not execute at all - and the clause it
        // feeds is what decides whether a parked posting ever comes back.
        var waiting = await db.Submissions
            .Where(s => s.ParkedReason != null
                && ParkReasonPolicy.AwaitingCvVariant.Contains(s.ParkedReason.Value))
            .Select(s => s.PostingId)
            .ToListAsync();

        Assert.Equal([1], waiting);
    }

    [Fact]
    public async Task The_brief_counts_how_many_parked_postings_each_missing_concept_blocks()
    {
        await using (var write = CreateContext())
        {
            await ParkAsync(write, postingId: 1, Kubernetes, Terraform);
            await ParkAsync(write, postingId: 2, Kubernetes);
            await ParkAsync(write, postingId: 3, DotNet);
        }

        await using var db = CreateContext();

        // The ranking the gap brief is built on, and the reason these are rows rather than a JSON
        // column: fifty individual "could not apply" notices is a queue nobody reads, and this
        // group-by is what turns them into one line naming the CV worth writing first.
        var blocking = await db.SubmissionParkGaps
            .GroupBy(g => g.ConceptId)
            .Select(group => new { ConceptId = group.Key, Postings = group.Count() })
            .OrderByDescending(row => row.Postings)
            .ThenBy(row => row.ConceptId)
            .ToListAsync();

        Assert.Equal(Kubernetes, blocking[0].ConceptId);
        Assert.Equal(2, blocking[0].Postings);
        Assert.Equal(3, blocking.Count);
    }

    // -----------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------

    /// <summary>Every mapped column of one concept table, minus the id it hangs off.</summary>
    private static IReadOnlyList<string> Shape(JobsDbContext db, Type entity, string owner)
        => [.. db.Model.FindEntityType(entity)!
            .GetProperties()
            .Where(property => property.Name != owner)
            .Select(property => $"{property.Name} {property.ClrType.Name} {property.GetMaxLength()}")
            .OrderBy(description => description, StringComparer.Ordinal)];

    private static CvVariantEntity Variant(
        string label, bool archived = false, long profileId = ProfileId)
        => new()
        {
            ProfileId = profileId,
            Label = label,
            LabelKey = CvVariantLibrary.FoldLabel(label),
            Markdown = $"# {label}\n\nSomething the candidate wrote.",
            AuthoredAtUtc = Now,
            IsArchived = archived,
        };

    /// <summary>The same variant with files against it, which is what makes it sendable.</summary>
    private static CvVariantEntity Rendered(CvVariantEntity variant)
    {
        variant.RenderedAtUtc = variant.AuthoredAtUtc.AddMinutes(1);
        variant.PdfBlobPath = "profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.pdf";
        variant.DocxBlobPath = "profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.docx";
        variant.Sha256 = Sha;

        return variant;
    }

    private static async Task WriteVariantAsync(JobsDbContext db, string label, params int[] concepts)
    {
        var variant = Rendered(Variant(label));

        db.CvVariants.Add(variant);
        await db.SaveChangesAsync();

        foreach (var conceptId in concepts)
        {
            db.CvVariantConcepts.Add(new CvVariantConceptEntity
            {
                VariantId = variant.Id,
                ConceptId = conceptId,
                Source = AssertionSource.Model,
                Polarity = AssertionPolarity.Proficient,
                EvidenceText = "read out of the candidate's own markdown",
                ResolverVersion = 1,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A posting parked for want of a CV, with what it asked for that nothing covered.</summary>
    private static async Task ParkAsync(JobsDbContext db, long postingId, params int[] concepts)
    {
        var submission = new SubmissionEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            Channel = SubmissionChannel.Ats,
            CreatedAtUtc = Now,
            ParkedReason = ParkReason.NoCvVariant,
            ParkedAtUtc = Now,
        };

        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        foreach (var conceptId in concepts)
        {
            db.SubmissionParkGaps.Add(new SubmissionParkGapEntity
            {
                SubmissionId = submission.Id,
                ConceptId = conceptId,
                RecordedAtUtc = submission.ParkedAtUtc!.Value,
            });
        }

        await db.SaveChangesAsync();
    }

    // The release rule used to be asserted here too, through a Releasable() helper that spelled
    // the queue's predicate a second time. It was removed rather than corrected: it passed while
    // the repository disagreed with it, because it was testing its own copy of the rule. What a
    // park is waiting on is a fact about the schema and is still asserted above; whether that fact
    // releases a posting is a question for the query that decides it, and CvParkQueueTests asks it
    // there.
}
