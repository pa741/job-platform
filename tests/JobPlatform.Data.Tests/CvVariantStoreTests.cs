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
/// <see cref="CvVariantRepository"/> against a real relational engine.
/// </summary>
/// <remarks>
/// <b>Every assertion reads the row back through a second context.</b> Reading it through the one
/// that wrote it would be answered from the change tracker and would pass whether or not anything
/// reached the database - which is precisely how a whole class of write went missing here once,
/// silently, under a host that had been registered with <c>NoTracking</c>.
///
/// <b>Nothing below depends on either engine's NULL semantics, which is what makes SQLite a fair
/// stand-in for Azure SQL here.</b> SQL Server treats two NULLs as equal in a unique index and
/// SQLite, like the standard, treats them as distinct; the only unique index these tests provoke is
/// <c>IX_CvVariants_LiveLabel</c>, whose two key columns are both required, so the refusals asserted
/// here are the refusals production makes. The one place the engines do differ - a case-insensitive
/// collation against SQLite's <c>BINARY</c> - is neutralised before the database sees it, because
/// <see cref="CvVariantLibrary.FoldLabel"/> lower-cases in Core and the index compares two folded
/// strings.
///
/// <b>Several fixtures write through the context rather than the repository, deliberately.</b> A
/// library already over the cap and a variant rendered before it was authored are states the
/// repository will not produce and has to answer about anyway - the first arrives by lowering a
/// constant, the second by two hosts disagreeing about the time.
/// </remarks>
public sealed class CvVariantStoreTests : IDisposable
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

    public CvVariantStoreTests()
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

        db.JobPostings.Add(new JobPostingEntity
        {
            Id = 1,
            SourceKey = "linkedin:1",
            Site = "linkedin",
            ExternalId = "1",
            ContentHash = new string('a', 64),
            Title = "Backend Engineer",
            Company = "Northwind",
            LocationCity = "London",
            JobUrl = "https://www.linkedin.com/jobs/view/1",
            FirstSeenUtc = Now,
            LastSeenUtc = Now,
        });

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

    private CvVariantRepository CreateRepository(JobsDbContext db) => new(db);

    // -----------------------------------------------------------------------
    // Reading a library
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_library_lists_live_variants_first_and_never_another_candidates()
    {
        await SeedAsync("Retired", archived: true);
        await SeedAsync("Backend .NET");
        await SeedAsync("Somebody else's", profileId: OtherProfileId);

        await using var db = CreateContext();

        var view = await CreateRepository(db).ListAsync(ProfileId);

        // Live before retired, so the page can rely on the order without sorting - and never a
        // ranking, which is the selector's job. The other candidate's CV is not absent because a
        // caller remembered to filter: there is no method here that could return it.
        Assert.Equal(["Backend .NET", "Retired"], view.Variants.Select(v => v.Label));
    }

    [Fact]
    public async Task The_selectable_subset_is_Core_s_rule_and_excludes_archived_and_unrendered()
    {
        await SeedAsync("Archived", archived: true);
        await SeedAsync("Never rendered", rendered: false);
        await SeedAsync("Edited since it was rendered", authoredAtUtc: Now.AddHours(2), renderedAtUtc: Now);
        await SeedAsync("Backend .NET");

        await using var db = CreateContext();

        var view = await CreateRepository(db).ListAsync(ProfileId);

        // Exactly the two exclusions Core makes, and the edited-since-rendered row is out by
        // arithmetic rather than because a writer remembered to clear a flag. All four are still in
        // the library: archiving retires a CV from selection and from nothing else.
        Assert.Equal(["Backend .NET"], view.Selectable.Select(v => v.Label));
        Assert.Equal(4, view.Variants.Count);
    }

    [Fact]
    public async Task The_sendable_predicate_the_database_runs_agrees_with_the_rule_Core_states()
    {
        await SeedAsync("Archived", archived: true);
        await SeedAsync("Never rendered", rendered: false);
        await SeedAsync("Edited since it was rendered", authoredAtUtc: Now.AddHours(2), renderedAtUtc: Now);
        await SeedAsync("Rendered in the same second it was written", renderedAtUtc: Now);
        await SeedAsync("Backend .NET");

        await using var db = CreateContext();

        var repository = CreateRepository(db);

        // Two spellings of one rule, and this is what holds them together - the technique
        // The_channel_is_projected_from_the_apply_link_and_filters_before_the_bound uses, because
        // there is no way to have a single spelling: CvVariant.IsSendable is a computed property
        // with no SQL, and CvVariantEntity.Sendable is a where clause with no CvVariant to hand
        // Core. A drift between them is silent - a CV missing from a selection is not something
        // anybody notices - and it decides which document an employer is sent.
        var core = (await repository.ListAsync(ProfileId)).Selectable.Select(v => v.Id).Order().ToList();

        var sql = await db.CvVariants
            .AsNoTracking()
            .Where(v => v.ProfileId == ProfileId)
            .Where(CvVariantEntity.Sendable)
            .Select(v => v.Id)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal(core, sql);

        // And the per-posting read, which is the one that runs the SQL spelling in production.
        var facts = await repository.ListSelectableFactsAsync(ProfileId);

        Assert.Equal(core, facts.Select(f => f.VariantId).Order());
    }

    [Fact]
    public async Task Staleness_is_computed_from_the_profile_and_counts_live_variants_only()
    {
        await SeedAsync("Written before the edit");
        await SeedAsync("Retired and out of date", archived: true);

        await using (var move = CreateContext())
        {
            var profile = await move.CandidateProfiles.AsTracking().SingleAsync(p => p.Id == ProfileId);
            profile.UpdatedUtc = Now.AddDays(30);

            await move.SaveChangesAsync();
        }

        await SeedAsync("Written after it", authoredAtUtc: Now.AddDays(31), renderedAtUtc: Now.AddDays(31));

        await using var db = CreateContext();

        var view = await CreateRepository(db).ListAsync(ProfileId);

        // Never a column: a stored flag needs a timer to write it and is wrong between the timer
        // and the edit. Archived variants are not counted - a retired CV falling behind the profile
        // is not a thing to nudge anybody about.
        Assert.Equal(2, view.Staleness.Considered);
        Assert.Equal(1, view.Staleness.Stale);
        Assert.Equal(Now.AddDays(30), view.Staleness.ProfileUpdatedUtc);

        // And it is a nudge rather than a lockout. Somebody who adds a job to their profile at
        // lunchtime must not find every CV excluded and every posting parked.
        Assert.Contains(view.Selectable, v => v.Label == "Written before the edit");
    }

    // -----------------------------------------------------------------------
    // The cap, enforced here and nowhere else
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_cap_is_enforced_in_the_repository_rather_than_at_the_call_sites()
    {
        await using (var write = CreateContext())
        {
            var repository = CreateRepository(write);

            for (var index = 0; index < CvVariantLimits.MaxPerProfile; index++)
            {
                var result = await repository.CreateAsync(
                    ProfileId, $"Variant {index}", "# CV\n\nSomething they wrote.", Now);

                Assert.True(result.Written);
            }

            // One past the cap, and the answer is an outcome rather than an exception on a page
            // somebody is only trying to save on.
            var refused = await repository.CreateAsync(
                ProfileId, "One too many", "# CV\n\nSomething they wrote.", Now);

            Assert.Equal(CvVariantWriteOutcome.LibraryFull, refused.Outcome);
            Assert.Null(refused.Variant);
        }

        await using var db = CreateContext();

        // Nothing was written, so a refusal cannot leave a row behind that occupies the cap it was
        // refused by.
        Assert.Equal(CvVariantLimits.MaxPerProfile, await db.CvVariants.CountAsync());
    }

    [Fact]
    public async Task A_library_already_over_the_cap_answers_no_room_rather_than_throwing()
    {
        // Reachable without anybody doing anything wrong: lowering MaxPerProfile leaves every
        // library above it above it. Written through the context because the repository will not
        // produce this state and still has to answer about it.
        for (var index = 0; index <= CvVariantLimits.MaxPerProfile; index++)
        {
            await SeedAsync($"Variant {index}");
        }

        await using var db = CreateContext();

        var result = await CreateRepository(db).CreateAsync(
            ProfileId, "One more", "# CV\n\nSomething they wrote.", Now);

        Assert.Equal(CvVariantWriteOutcome.LibraryFull, result.Outcome);
    }

    [Fact]
    public async Task Archiving_makes_room_without_deleting_anything()
    {
        for (var index = 0; index < CvVariantLimits.MaxPerProfile; index++)
        {
            await SeedAsync($"Variant {index}");
        }

        long archivedId;

        await using (var write = CreateContext())
        {
            var repository = CreateRepository(write);

            var first = (await repository.ListAsync(ProfileId)).Variants[0];
            var archived = await repository.ArchiveAsync(ProfileId, first.Id);

            Assert.True(archived.Written);
            archivedId = first.Id;

            var created = await repository.CreateAsync(
                ProfileId, "The seventh", "# CV\n\nSomething they wrote.", Now);

            Assert.True(created.Written);
        }

        await using var db = CreateContext();

        // The room came from an update. A cap that counted archived rows would have made the
        // seventh rewrite of a CV impossible until somebody erased a file an employer was sent -
        // which trades an auditable history for a row count, and the history is the dearer of the
        // two.
        Assert.Equal(CvVariantLimits.MaxPerProfile + 1, await db.CvVariants.CountAsync());

        var retired = await db.CvVariants.SingleAsync(v => v.Id == archivedId);

        Assert.True(retired.IsArchived);
        Assert.NotNull(retired.PdfBlobPath);
        Assert.NotNull(retired.Sha256);
    }

    // -----------------------------------------------------------------------
    // Labels
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_name_a_live_variant_holds_is_refused_and_archiving_gives_it_back()
    {
        await SeedAsync("Backend .NET");

        await using (var db = CreateContext())
        {
            var repository = CreateRepository(db);

            // Folded, so "Backend .NET" and "backend  .net" are the same name - which is what they
            // are to anybody reading a picker, and the pack's account of which CV it chose has to
            // be checkable by the person reading it.
            Assert.False(await repository.IsLabelAvailableAsync(ProfileId, "backend  .net"));

            var refused = await repository.CreateAsync(
                ProfileId, "backend  .net", "# CV\n\nSomething they wrote.", Now);

            Assert.Equal(CvVariantWriteOutcome.LabelTaken, refused.Outcome);
        }

        await using (var archive = CreateContext())
        {
            var repository = CreateRepository(archive);
            var existing = (await repository.ListAsync(ProfileId)).Variants[0];

            await repository.ArchiveAsync(ProfileId, existing.Id);

            // Rewriting a CV and giving the new one the old one's name is the ordinary case. A rule
            // reserving a label forever would push people into calling their CVs "Backend .NET v3".
            var created = await repository.CreateAsync(
                ProfileId, "Backend .NET", "# CV\n\nThe rewrite.", Now);

            Assert.True(created.Written);
        }

        await using var read = CreateContext();

        Assert.Equal(2, await read.CvVariants.CountAsync(v => v.ProfileId == ProfileId));
    }

    [Fact]
    public async Task A_variant_keeps_its_own_name_through_a_rename()
    {
        var variant = await SeedAsync("Backend .NET");

        await using var db = CreateContext();

        var repository = CreateRepository(db);

        // Without the exclusion this collides with itself, which reads to the person as the system
        // refusing a change they did not make.
        Assert.True(await repository.IsLabelAvailableAsync(ProfileId, "Backend .NET", variant.Id));
        Assert.True((await repository.RenameAsync(ProfileId, variant.Id, "Backend .NET")).Written);
    }

    [Fact]
    public async Task Renaming_moves_the_label_and_its_key_and_leaves_the_authoring_date_alone()
    {
        var variant = await SeedAsync("Backend");

        await using (var write = CreateContext())
        {
            var result = await CreateRepository(write).RenameAsync(ProfileId, variant.Id, "  Backend  .NET  ");

            Assert.True(result.Written);
        }

        await using var db = CreateContext();

        var stored = await db.CvVariants.SingleAsync(v => v.Id == variant.Id);

        Assert.Equal("Backend  .NET", stored.Label);

        // The key comes from CvVariantLibrary.FoldLabel and from nowhere else - a second spelling
        // of the fold splits one label into two with nothing failing.
        Assert.Equal(CvVariantLibrary.FoldLabel("Backend .NET"), stored.LabelKey);

        // And the date does not move. It is the whole of the staleness signal and half of the
        // render comparison, so a rename that touched it would make a document nobody edited look
        // freshly written and would quietly mark a stale PDF current.
        Assert.Equal(variant.AuthoredAtUtc, stored.AuthoredAtUtc);
        Assert.Equal(variant.RenderedAtUtc, stored.RenderedAtUtc);
    }

    [Fact]
    public async Task A_name_nobody_could_pick_a_cv_by_is_refused_by_both_write_paths()
    {
        var variant = await SeedAsync("Backend .NET");

        await using var db = CreateContext();

        var repository = CreateRepository(db);

        // "..." is unique as a string and indistinguishable from its neighbours in a picker, and
        // unquotable in the pack's account of why it chose that CV. Both paths refuse it the same
        // way, and they throw rather than answering: this is a caller that skipped a step, where a
        // taken name is a person who has to be told something they can act on.
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateAsync(ProfileId, "...", "# CV\n\nSomething.", Now));

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.RenameAsync(ProfileId, variant.Id, "..."));
    }

    // -----------------------------------------------------------------------
    // Re-authoring
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Re_authoring_moves_the_words_and_their_date_together_and_keeps_the_files()
    {
        var variant = await SeedAsync("Backend .NET");

        var edited = Now.AddDays(1);

        await using (var write = CreateContext())
        {
            var result = await CreateRepository(write)
                .ReauthorAsync(ProfileId, variant.Id, "  # CV\n\nThe paragraph they added.  ", edited);

            Assert.True(result.Written);
        }

        await using var db = CreateContext();

        var repository = CreateRepository(db);
        var stored = await repository.GetAsync(ProfileId, variant.Id);

        Assert.Equal("# CV\n\nThe paragraph they added.", stored!.Markdown);
        Assert.Equal(edited, stored.AuthoredAtUtc);

        // The paths survive the edit. The stored file is still the file a previous application
        // uploaded, and blanking the pointer would make that application unexplainable in order to
        // keep a row tidy.
        Assert.Equal(variant.PdfBlobPath, stored.PdfBlobPath);
        Assert.Equal(variant.Sha256, stored.Sha256);

        // It leaves selection through the timestamps instead - reversible by re-rendering, and
        // nothing had to remember to clear a flag.
        Assert.False(stored.IsRenderCurrent);
        Assert.Empty((await repository.ListAsync(ProfileId)).Selectable);
    }

    // -----------------------------------------------------------------------
    // Archiving, and the read that ignores it
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_variant_an_application_named_still_resolves_after_it_is_archived()
    {
        var variant = await SeedAsync("Backend .NET");

        await using (var write = CreateContext())
        {
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

            await CreateRepository(write).ArchiveAsync(ProfileId, variant.Id);
        }

        await using var db = CreateContext();

        var repository = CreateRepository(db);

        // The read that ignores IsArchived, and the reason it is separate from the selection reads:
        // "what did we send them" is a question about a document already sitting in somebody else's
        // system, so it has to answer after the CV has been retired - which is the ordinary end of
        // a CV's life. A system answering "that CV is archived" has lost the record it exists to
        // keep.
        var sent = await repository.GetAsync(ProfileId, variant.Id);

        Assert.NotNull(sent);
        Assert.True(sent.IsArchived);
        Assert.Equal(variant.PdfBlobPath, sent.PdfBlobPath);

        // And archiving did remove it from selection, which is the only thing it does.
        Assert.Empty(await repository.ListSelectableAsync(ProfileId));
        Assert.Empty(await repository.ListSelectableFactsAsync(ProfileId));
    }

    [Fact]
    public async Task Unarchiving_is_refused_where_the_library_is_full_or_the_name_was_taken()
    {
        var retired = await SeedAsync("Backend .NET", archived: true);

        await using (var full = CreateContext())
        {
            for (var index = 0; index < CvVariantLimits.MaxPerProfile; index++)
            {
                await SeedAsync($"Variant {index}");
            }

            // An unarchived variant occupies the cap, so a library already at six has no room for a
            // seventh however it arrives.
            var refused = await CreateRepository(full).UnarchiveAsync(ProfileId, retired.Id);

            Assert.Equal(CvVariantWriteOutcome.LibraryFull, refused.Outcome);
        }

        await using (var rename = CreateContext())
        {
            var repository = CreateRepository(rename);
            var live = (await repository.ListAsync(ProfileId)).Variants.First(v => !v.IsArchived);

            await repository.ArchiveAsync(ProfileId, live.Id);

            var other = (await repository.ListAsync(ProfileId)).Variants.First(v => !v.IsArchived);
            await repository.RenameAsync(ProfileId, other.Id, "Backend .NET");

            // Very often the name it was archived under is the name of the CV that replaced it.
            // Checking here turns a DbUpdateException on somebody's page into an outcome they can
            // act on.
            var refused = await repository.UnarchiveAsync(ProfileId, retired.Id);

            Assert.Equal(CvVariantWriteOutcome.LabelTaken, refused.Outcome);
        }

        await using var db = CreateContext();

        Assert.True(await db.CvVariants.Where(v => v.Id == retired.Id).Select(v => v.IsArchived).SingleAsync());
    }

    // -----------------------------------------------------------------------
    // Recording a render
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Recording_a_render_writes_the_time_the_paths_and_the_hash_in_one_update()
    {
        var variant = await SeedAsync("Backend .NET", rendered: false);

        var renderedAt = Now.AddMinutes(5);

        await using (var write = CreateContext())
        {
            var result = await CreateRepository(write).RecordRenderAsync(
                ProfileId,
                variant.Id,
                new RenderedVariant
                {
                    PdfBlobPath = "profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.pdf",
                    DocxBlobPath = "profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.docx",
                    Sha256 = Sha,
                    RenderedAtUtc = renderedAt,
                });

            Assert.True(result.Written);
        }

        await using var db = CreateContext();

        var stored = await db.CvVariants.SingleAsync(v => v.Id == variant.Id);

        // Everything that makes a variant sendable becomes true in one SaveChanges. Written in two,
        // the row spends a window claiming to be rendered while pointing at nothing - selection
        // picks it up and the pack hands the browser loop a URL whose file the loop discovers is
        // missing at the upload box, after the tab is already open.
        Assert.Equal(renderedAt, stored.RenderedAtUtc);
        Assert.Equal("profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.pdf", stored.PdfBlobPath);
        Assert.Equal("profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.docx", stored.DocxBlobPath);
        Assert.Equal(Sha, stored.Sha256);

        Assert.Single(await CreateRepository(db).ListSelectableAsync(ProfileId));
    }

    [Fact]
    public async Task A_digest_of_the_wrong_shape_is_refused_rather_than_padded_into_the_column()
    {
        var variant = await SeedAsync("Backend .NET", rendered: false);

        await using var db = CreateContext();

        var repository = CreateRepository(db);

        // nchar(64) pads a short value with spaces, so a digest of any other length would be stored
        // looking like a digest and would never match the file it claims to describe - the failure
        // the column exists to catch, arriving through the column itself.
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RecordRenderAsync(
            ProfileId,
            variant.Id,
            new RenderedVariant
            {
                PdfBlobPath = "profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.pdf",
                Sha256 = "not a digest",
                RenderedAtUtc = Now,
            }));

        // A path past the storage platform's own ceiling names no file that exists, so it is
        // refused too - truncating a pointer costs the thing pointed at.
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RecordRenderAsync(
            ProfileId,
            variant.Id,
            new RenderedVariant
            {
                PdfBlobPath = new string('p', CvVariantLimits.MaxBlobPathLength + 1),
                Sha256 = Sha,
                RenderedAtUtc = Now,
            }));

        await using var read = CreateContext();

        Assert.Null(await read.CvVariants.Where(v => v.Id == variant.Id).Select(v => v.RenderedAtUtc).SingleAsync());
    }

    // -----------------------------------------------------------------------
    // The host that tracks nothing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Every_read_then_mutate_still_reaches_the_database_under_a_global_NoTracking()
    {
        var variant = await SeedAsync("Backend .NET");

        // The configuration the production API once ran under, on the argument that it never wrote
        // to SQL. Four repositories silently saved nothing under it, and every write below asks for
        // tracking out loud so that it cannot happen again from a line in a composition root.
        var noTracking = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlite(_connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        await using (var db = new JobsDbContext(noTracking))
        {
            var repository = new CvVariantRepository(db);

            Assert.True((await repository.RenameAsync(ProfileId, variant.Id, "Platform engineering")).Written);
            Assert.True((await repository.ReauthorAsync(ProfileId, variant.Id, "# CV\n\nRewritten.", Now.AddDays(1))).Written);

            Assert.True((await repository.RecordRenderAsync(
                ProfileId,
                variant.Id,
                new RenderedVariant
                {
                    PdfBlobPath = "profile-cvs/1/1/Test_Candidate_Curriculum_Vitae.pdf",
                    Sha256 = Sha,
                    RenderedAtUtc = Now.AddDays(2),
                })).Written);

            Assert.Equal(1, await repository.ReplaceConceptsAsync(
                ProfileId, variant.Id, [new ConceptAssertion("skill.kubernetes", AssertionSource.Model)], 1));

            Assert.True((await repository.ArchiveAsync(ProfileId, variant.Id)).Written);
        }

        await using var read = CreateContext();

        var stored = await read.CvVariants.SingleAsync(v => v.Id == variant.Id);

        Assert.Equal("Platform engineering", stored.Label);
        Assert.Equal("# CV\n\nRewritten.", stored.Markdown);
        Assert.Equal(Now.AddDays(2), stored.RenderedAtUtc);
        Assert.True(stored.IsArchived);
        Assert.Equal(1, await read.CvVariantConcepts.CountAsync(c => c.VariantId == variant.Id));
    }

    // -----------------------------------------------------------------------
    // What a variant says, which feeds selection and nothing else
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Replacing_a_variants_concepts_touches_that_variant_and_never_the_profile()
    {
        var backend = await SeedAsync("Backend .NET");
        var platform = await SeedAsync("Platform engineering");

        await using (var seed = CreateContext())
        {
            seed.ProfileConcepts.Add(new ProfileConceptEntity
            {
                ProfileId = ProfileId,
                ConceptId = DotNet,
                Source = AssertionSource.Model,
                Polarity = AssertionPolarity.Proficient,
                ResolverVersion = 1,
            });

            await seed.SaveChangesAsync();
        }

        await using (var write = CreateContext())
        {
            var writer = CreateRepository(write);

            await writer.ReplaceConceptsAsync(
                ProfileId, platform.Id, [new ConceptAssertion("skill.terraform", AssertionSource.Model)], 1);

            await writer.ReplaceConceptsAsync(
                ProfileId,
                backend.Id,
                [
                    new ConceptAssertion("skill.dotnet", AssertionSource.Model, AssertionPolarity.Required),
                    new ConceptAssertion("skill.kubernetes", AssertionSource.Model, AssertionPolarity.Mentioned),
                ],
                1);

            // The replace is of that variant's rows only.
            Assert.Equal(1, await writer.ReplaceConceptsAsync(
                ProfileId, backend.Id, [new ConceptAssertion("skill.dotnet", AssertionSource.Model)], 2));
        }

        await using var db = CreateContext();

        var repository = CreateRepository(db);

        Assert.Equal(["skill.dotnet"], (await repository.GetConceptsAsync(ProfileId, backend.Id)).Select(c => c.ConceptKey));

        // The surviving row is the new one and not the one it replaced, which is what the delete
        // and the insert being one SaveChanges buys: a replace that quietly kept the old row would
        // leave a variant described by a resolver nobody is running any more.
        Assert.Equal(2, await db.CvVariantConcepts
            .Where(c => c.VariantId == backend.Id)
            .Select(c => c.ResolverVersion)
            .SingleAsync());
        Assert.Equal(["skill.terraform"], (await repository.GetConceptsAsync(ProfileId, platform.Id)).Select(c => c.ConceptKey));

        // And the profile's own concepts are untouched, which is the guard this whole feature turns
        // on: a CV is written from the profile, so a document that could write back would inflate
        // the record it was derived from - after which the loop applies to jobs on the strength of
        // its own prose. Nothing in the repository can do it, because nothing there names this
        // table.
        Assert.Equal(1, await db.ProfileConcepts.CountAsync(c => c.ProfileId == ProfileId));
    }

    [Fact]
    public async Task A_concept_the_vocabulary_does_not_know_is_dropped_rather_than_invented()
    {
        var variant = await SeedAsync("Backend .NET");

        await using (var write = CreateContext())
        {
            var written = await CreateRepository(write).ReplaceConceptsAsync(
                ProfileId,
                variant.Id,
                [
                    new ConceptAssertion("skill.dotnet", AssertionSource.Model, AssertionPolarity.Required),

                    // A hallucinated key is indistinguishable from a real one in SQL and would
                    // quietly split a concept in two.
                    new ConceptAssertion("skill.invented-by-a-model", AssertionSource.Model),

                    // Two readings of one concept from one source are one row, because Source is
                    // part of the primary key - the write would otherwise fail on a constraint
                    // rather than record what was read.
                    new ConceptAssertion("skill.dotnet", AssertionSource.Model, AssertionPolarity.Mentioned),
                ],
                7);

            // The count is what was written, so a caller comparing it against what it handed in can
            // see that the vocabulary missed something.
            Assert.Equal(1, written);
        }

        await using var db = CreateContext();

        var stored = await db.CvVariantConcepts.SingleAsync(c => c.VariantId == variant.Id);

        Assert.Equal(DotNet, stored.ConceptId);
        Assert.Equal(7, stored.ResolverVersion);

        // A CV is a supply document, as a profile is, and the extractor speaks the demand half
        // because it is the same prompt that reads adverts. "Required" there means the skill is
        // central to their work, which is Expert here.
        Assert.Equal(AssertionPolarity.Expert, stored.Polarity);
    }

    [Fact]
    public async Task The_selection_facts_carry_deduplicated_keys_for_sendable_variants_only()
    {
        var sendable = await SeedAsync("Backend .NET");
        var archived = await SeedAsync("Retired", archived: true);

        await using (var write = CreateContext())
        {
            var repository = CreateRepository(write);

            await repository.ReplaceConceptsAsync(
                ProfileId,
                sendable.Id,
                [
                    // One concept read out of a heading and again out of a bullet: two rows, kept
                    // apart because they are not equally good evidence and a collapse cannot be
                    // undone.
                    new ConceptAssertion("skill.dotnet", AssertionSource.Model),
                    new ConceptAssertion("skill.dotnet", AssertionSource.Taxonomy),
                    new ConceptAssertion("skill.kubernetes", AssertionSource.Model),
                ],
                1);

            await repository.ReplaceConceptsAsync(
                ProfileId, archived.Id, [new ConceptAssertion("skill.terraform", AssertionSource.Model)], 1);
        }

        await using var db = CreateContext();

        var facts = await CreateRepository(db).ListSelectableFactsAsync(ProfileId);

        var only = Assert.Single(facts);

        Assert.Equal(sendable.Id, only.VariantId);
        Assert.Equal("Backend .NET", only.Label);

        // Presence or absence is all a selection is entitled to read, and a selector counting
        // presence must not see one concept twice.
        Assert.Equal(["skill.dotnet", "skill.kubernetes"], only.ConceptKeys.Order());

        // Both rows are still stored, because the distinction is worth keeping even where selection
        // is not shown it: a concept named in a heading and again in a bullet is not equally good
        // evidence, and a collapse cannot be undone afterwards.
        Assert.Equal(3, await db.CvVariantConcepts.CountAsync(c => c.VariantId == sendable.Id));
    }

    // -----------------------------------------------------------------------
    // Somebody else's library
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_strangers_variant_is_neither_readable_nor_writable()
    {
        var variant = await SeedAsync("Backend .NET");

        await using var db = CreateContext();

        var repository = CreateRepository(db);

        // The profile id is in the predicate rather than checked afterwards, so a stranger's CV is
        // never materialised at all - and "no such variant" is indistinguishable from "not yours",
        // which is the rule every other read in this codebase follows.
        Assert.Null(await repository.GetAsync(OtherProfileId, variant.Id));
        Assert.Empty((await repository.ListAsync(OtherProfileId)).Variants);
        Assert.Empty(await repository.GetConceptsAsync(OtherProfileId, variant.Id));

        Assert.Equal(
            CvVariantWriteOutcome.NotFound,
            (await repository.RenameAsync(OtherProfileId, variant.Id, "Theirs now")).Outcome);

        Assert.Equal(
            CvVariantWriteOutcome.NotFound,
            (await repository.ReauthorAsync(OtherProfileId, variant.Id, "# Not theirs to write", Now)).Outcome);

        Assert.Equal(
            CvVariantWriteOutcome.NotFound,
            (await repository.ArchiveAsync(OtherProfileId, variant.Id)).Outcome);

        Assert.Equal(
            CvVariantWriteOutcome.NotFound,
            (await repository.RecordRenderAsync(
                OtherProfileId,
                variant.Id,
                new RenderedVariant
                {
                    PdfBlobPath = "profile-cvs/2/1/Test_Candidate_Curriculum_Vitae.pdf",
                    Sha256 = Sha,
                    RenderedAtUtc = Now,
                })).Outcome);

        // The one that would lose data if it got this wrong: a replace establishes the owner before
        // it removes anything, or an id from a route deletes a stranger's extraction and writes
        // nothing in its place.
        Assert.Equal(0, await repository.ReplaceConceptsAsync(
            OtherProfileId, variant.Id, [new ConceptAssertion("skill.dotnet", AssertionSource.Model)], 1));

        await using var read = CreateContext();

        Assert.Equal("Backend .NET", await read.CvVariants.Where(v => v.Id == variant.Id).Select(v => v.Label).SingleAsync());
    }

    // -----------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------

    /// <summary>
    /// One stored variant, written through the context rather than the repository.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="CvVariantRepository.CreateAsync"/>: several tests need a library
    /// the repository would refuse to build - one already over the cap, or a variant whose render
    /// predates its text - and a fixture that could only produce legal states could not set them up.
    /// The label key is <see cref="CvVariantLibrary.FoldLabel"/>'s, as it is in production, because
    /// it is what the unique index compares.
    /// </remarks>
    private async Task<CvVariant> SeedAsync(
        string label,
        bool rendered = true,
        bool archived = false,
        long profileId = ProfileId,
        DateTimeOffset? authoredAtUtc = null,
        DateTimeOffset? renderedAtUtc = null)
    {
        await using var db = CreateContext();

        var authored = authoredAtUtc ?? Now;

        var entity = new CvVariantEntity
        {
            ProfileId = profileId,
            Label = label,
            LabelKey = CvVariantLibrary.FoldLabel(label),
            Markdown = $"# {label}\n\nSomething the candidate wrote.",
            AuthoredAtUtc = authored,
            IsArchived = archived,
        };

        if (rendered)
        {
            entity.RenderedAtUtc = renderedAtUtc ?? authored.AddMinutes(1);
            entity.PdfBlobPath = $"profile-cvs/{profileId}/x/Test_Candidate_Curriculum_Vitae.pdf";
            entity.DocxBlobPath = $"profile-cvs/{profileId}/x/Test_Candidate_Curriculum_Vitae.docx";
            entity.Sha256 = Sha;
        }

        db.CvVariants.Add(entity);
        await db.SaveChangesAsync();

        return new CvVariant
        {
            Id = entity.Id,
            Label = entity.Label,
            Markdown = entity.Markdown,
            AuthoredAtUtc = entity.AuthoredAtUtc,
            RenderedAtUtc = entity.RenderedAtUtc,
            PdfBlobPath = entity.PdfBlobPath,
            DocxBlobPath = entity.DocxBlobPath,
            Sha256 = entity.Sha256,
            IsArchived = entity.IsArchived,
        };
    }
}
