using System.Globalization;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JobPlatform.Data.Sql;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();

    public DbSet<JobPostingEntity> JobPostings => Set<JobPostingEntity>();
    public DbSet<JobPostingSearchTerm> JobPostingSearchTerms => Set<JobPostingSearchTerm>();

    public DbSet<CompanyEntity> Companies => Set<CompanyEntity>();

    public DbSet<ConceptEntity> Concepts => Set<ConceptEntity>();
    public DbSet<ConceptLabelEntity> ConceptLabels => Set<ConceptLabelEntity>();
    public DbSet<ConceptRelationEntity> ConceptRelations => Set<ConceptRelationEntity>();
    public DbSet<ConceptClosureEntity> ConceptClosure => Set<ConceptClosureEntity>();

    public DbSet<PostingConceptEntity> PostingConcepts => Set<PostingConceptEntity>();
    public DbSet<PostingMentionEntity> PostingMentions => Set<PostingMentionEntity>();
    public DbSet<JobPostingJobTypeEntity> JobPostingJobTypes => Set<JobPostingJobTypeEntity>();
    public DbSet<PostingTagEntity> PostingTags => Set<PostingTagEntity>();
    public DbSet<PostingExtractionEntity> PostingExtractions => Set<PostingExtractionEntity>();

    public DbSet<CandidateProfileEntity> CandidateProfiles => Set<CandidateProfileEntity>();
    public DbSet<ProfileExperienceEntity> ProfileExperiences => Set<ProfileExperienceEntity>();
    public DbSet<ProfileEducationEntity> ProfileEducation => Set<ProfileEducationEntity>();
    public DbSet<ProfileProjectEntity> ProfileProjects => Set<ProfileProjectEntity>();
    public DbSet<ProfileCertificationEntity> ProfileCertifications => Set<ProfileCertificationEntity>();
    public DbSet<ProfileLanguageEntity> ProfileLanguages => Set<ProfileLanguageEntity>();
    public DbSet<ProfileLinkEntity> ProfileLinks => Set<ProfileLinkEntity>();
    public DbSet<ProfileJobTypeEntity> ProfileJobTypes => Set<ProfileJobTypeEntity>();
    public DbSet<ProfileConceptEntity> ProfileConcepts => Set<ProfileConceptEntity>();
    public DbSet<ProfileMentionEntity> ProfileMentions => Set<ProfileMentionEntity>();

    public DbSet<ExtractionBatchEntity> ExtractionBatches => Set<ExtractionBatchEntity>();
    public DbSet<ExtractionBatchItemEntity> ExtractionBatchItems => Set<ExtractionBatchItemEntity>();

    public DbSet<JobMatchEntity> JobMatches => Set<JobMatchEntity>();
    public DbSet<ApplicationDocumentEntity> ApplicationDocuments => Set<ApplicationDocumentEntity>();

    public DbSet<SubmissionEntity> Submissions => Set<SubmissionEntity>();
    public DbSet<SubmissionEventEntity> SubmissionEvents => Set<SubmissionEventEntity>();

    /// <summary>Apply passes. Not <see cref="ScrapeRuns"/>, which belongs to ingestion.</summary>
    public DbSet<RunEntity> Runs => Set<RunEntity>();

    public DbSet<FormAnswerEntity> FormAnswers => Set<FormAnswerEntity>();
    public DbSet<FormAnswerResolutionEntity> FormAnswerResolutions => Set<FormAnswerResolutionEntity>();
    public DbSet<OpenQuestionEntity> OpenQuestions => Set<OpenQuestionEntity>();

    public DbSet<ScraperSearchEntity> ScraperSearches => Set<ScraperSearchEntity>();
    public DbSet<ScraperSearchSiteEntity> ScraperSearchSites => Set<ScraperSearchSiteEntity>();
    public DbSet<ScraperSearchFilterEntity> ScraperSearchFilters => Set<ScraperSearchFilterEntity>();

    public DbSet<PostingEmbeddingEntity> PostingEmbeddings => Set<PostingEmbeddingEntity>();
    public DbSet<ProfileEmbeddingEntity> ProfileEmbeddings => Set<ProfileEmbeddingEntity>();

    /// <summary>
    /// On SQLite, stores <see cref="DateTimeOffset"/> as ticks.
    /// </summary>
    /// <remarks>
    /// SQLite has no native date type and its provider refuses to translate a
    /// <c>DateTimeOffset</c> in an ORDER BY at all - "SQLite does not support expressions of
    /// type 'DateTimeOffset' in ORDER BY clauses". The API's default posting order is by
    /// LastSeenUtc, so without this the tests could not exercise the ordering the production
    /// query actually uses, which is the part most worth testing.
    ///
    /// Provider-conditional, so SQL Server keeps its native datetimeoffset columns and
    /// nothing about the deployed schema changes. This follows the precedent already set by
    /// the Description column below: the model is deliberately kept buildable on the SQLite
    /// the tests run against.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // A string comparison rather than Database.IsSqlite(), which lives in the SQLite
        // provider package - this project must not take a dependency on it.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToTicksConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToTicksConverter>();
        }

        base.ConfigureConventions(configurationBuilder);
    }

    private sealed class DateTimeOffsetToTicksConverter()
        : ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    /// <summary>
    /// How wide a column holding a caller's subject id has to be.
    /// </summary>
    /// <remarks>
    /// Entra object ids are GUIDs, but the width is chosen for the general case rather than
    /// assuming a format the token is not obliged to keep. It is a constant rather than two
    /// literals because <c>Submissions.ApprovedBy</c> is a <i>copy</i> of
    /// <c>CandidateProfiles.SubjectId</c>: a narrower column there would truncate the id it
    /// records, and a truncated id in an authorisation record names somebody else. There is no
    /// Core constant to reach for - this bounds no validated value, only two columns that must
    /// agree with each other.
    /// </remarks>
    private const int SubjectIdLength = 100;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScrapeRun>(entity =>
        {
            entity.ToTable("ScrapeRuns");
            entity.HasKey(e => e.Id);

            // The idempotency guarantee: one row per blob, so a redelivered event
            // cannot produce a second run.
            entity.HasIndex(e => e.BlobPath).IsUnique();
            entity.HasIndex(e => new { e.SearchTerm, e.ScrapeDate });

            entity.Property(e => e.BlobPath).HasMaxLength(512).IsRequired();
            entity.Property(e => e.BlobETag).HasMaxLength(128);
            entity.Property(e => e.SearchTerm).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<JobPostingEntity>(entity =>
        {
            entity.ToTable("JobPostings");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.SourceKey).IsUnique();
            entity.HasIndex(e => e.ContentHash);

            // The dedupe axis: every row sharing this key is one job, listed more than once.
            // Not unique, obviously - duplicates are the thing it finds - and not filtered
            // either, because the column is already null for every posting with no cross-board
            // identity, so the nulls are what a filter would have excluded anyway.
            entity.HasIndex(e => e.CrossBoardKey);

            entity.HasIndex(e => e.LastSeenUtc);
            entity.HasIndex(e => e.Company);

            // The cross-board lookup: the same job on another board, which is where an apply
            // link is recovered when the posting's own board stopped publishing one.
            //
            // Company and city are the key and the title is included rather than keyed, because
            // Title is nvarchar(500) and SQL Server caps a nonclustered key at 1700 bytes - all
            // three would be 1900 and the migration would fail outright. JobUrlDirect and Site
            // are included so the subquery is answered from the index alone; it runs once per
            // shortlist row against a database billed by the second.
            //
            // The single-column Company index above is now a prefix of this one and is probably
            // redundant. Left alone deliberately: dropping an index is a query-plan change and
            // nothing here has measured the plans that use it.
            entity.HasIndex(e => new { e.Company, e.LocationCity })
                .IncludeProperties(e => new { e.Title, e.JobUrlDirect, e.Site });
            entity.HasIndex(e => e.FreshnessClass);

            // The new group-by axes. Every one of these is a dashboard facet or an analysis
            // dimension; without them each breakdown is a full scan of the table.
            entity.HasIndex(e => e.Seniority);
            entity.HasIndex(e => e.RoleFamily);
            entity.HasIndex(e => e.WorkArrangement);
            entity.HasIndex(e => e.SourceBoard);
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.EnrichmentVersion);

            entity.Property(e => e.SourceKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Site).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ExternalId).HasMaxLength(150).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsFixedLength().IsRequired();

            // 64 is the width of a SHA-256 hex string, which is a fact about the algorithm rather
            // than a bound anybody chose - so it is spelled the same way as ContentHash on the
            // line above and the three other hash columns in this file, rather than promoted to a
            // constant that would only ever say "SHA-256 is still SHA-256".
            //
            // Nullable, because JobFingerprint.CrossBoardKey answers null where the employer or
            // the city is unknown, and fixed-length so a raw composite written here fails loudly
            // on SQL Server instead of being silently truncated into a false merge.
            entity.Property(e => e.CrossBoardKey).HasMaxLength(64).IsFixedLength();

            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Company).HasMaxLength(300);
            entity.Property(e => e.LocationRaw).HasMaxLength(300);
            entity.Property(e => e.LocationCity).HasMaxLength(150);
            entity.Property(e => e.LocationRegion).HasMaxLength(150);
            entity.Property(e => e.LocationCountry).HasMaxLength(100);
            entity.Property(e => e.JobType).HasMaxLength(150);

            entity.Property(e => e.MinAmount).HasPrecision(12, 2);
            entity.Property(e => e.MaxAmount).HasPrecision(12, 2);
            entity.Property(e => e.Currency).HasMaxLength(10);
            entity.Property(e => e.SalaryInterval).HasMaxLength(30);
            entity.Property(e => e.SalarySource).HasMaxLength(50);

            entity.Property(e => e.JobLevel).HasMaxLength(100);
            entity.Property(e => e.JobFunction).HasMaxLength(150);
            entity.Property(e => e.CompanyIndustry).HasMaxLength(200);

            entity.Property(e => e.CompanyNumEmployees).HasMaxLength(50);
            entity.Property(e => e.ExperienceRange).HasMaxLength(100);
            entity.Property(e => e.FreshnessClass).HasMaxLength(30);
            // Two sentences in practice, but it is model-generated, so the bound is
            // generous rather than measured.
            entity.Property(e => e.Summary).HasMaxLength(1000);

            entity.Property(e => e.JobUrl).HasMaxLength(1000);
            entity.Property(e => e.JobUrlDirect).HasMaxLength(1000);
            entity.Property(e => e.CompanyUrl).HasMaxLength(1000);

            // Descriptions run to several KB and are stored intact, so no MaxLength is
            // set. EF already maps an unbounded string to nvarchar(max) on SQL Server;
            // spelling that type out explicitly would also make the model unbuildable on
            // any other provider, including the SQLite used by the tests.
            entity.Property(e => e.Description);

            entity.Property(e => e.SourceBoard).HasMaxLength(100);
            entity.Property(e => e.Applicants).HasMaxLength(100);
            entity.Property(e => e.ListingType).HasMaxLength(50);
            entity.Property(e => e.WorkFromHomeType).HasMaxLength(50);
            entity.Property(e => e.AnnualSalaryCurrency).HasMaxLength(10);
            entity.Property(e => e.SalaryStatedInterval).HasMaxLength(30);
            entity.Property(e => e.Ir35).HasMaxLength(10);

            entity.Property(e => e.AnnualSalaryMin).HasPrecision(12, 2);
            entity.Property(e => e.AnnualSalaryMax).HasPrecision(12, 2);

            // Restrict, not Cascade: deleting a company must not take its postings with it.
            // The company row is a lookup, the postings are the record.
            entity.HasOne(e => e.CompanyRef)
                .WithMany()
                .HasForeignKey(e => e.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CompanyEntity>(entity =>
        {
            entity.ToTable("Companies");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.CompanyKey).IsUnique();

            entity.Property(e => e.CompanyKey).HasMaxLength(300).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Industry).HasMaxLength(200);
            entity.Property(e => e.EmployeesBand).HasMaxLength(50);
            entity.Property(e => e.Revenue).HasMaxLength(100);
            entity.Property(e => e.Url).HasMaxLength(1000);

            // Unbounded, like the posting description. Deduplicating it out of every posting
            // row is most of why this table exists.
            entity.Property(e => e.Description);
        });

        ConfigureConceptGraph(modelBuilder);
        ConfigureAssertions(modelBuilder);
        ConfigureProfiles(modelBuilder);
        ConfigureMatches(modelBuilder);
        ConfigureSubmissions(modelBuilder);
        ConfigureRuns(modelBuilder);
        ConfigureFormAnswers(modelBuilder);
        ConfigureExtractionBatches(modelBuilder);
        ConfigureScraperSearches(modelBuilder);

        modelBuilder.Entity<JobPostingSearchTerm>(entity =>
        {
            entity.ToTable("JobPostingSearchTerms");
            entity.HasKey(e => new { e.PostingId, e.SearchTerm });

            // The two axes every per-term query reads: which postings a term holds, and
            // which of them a given day's runs surfaced.
            entity.HasIndex(e => new { e.SearchTerm, e.FirstSeenUtc });
            entity.HasIndex(e => new { e.SearchTerm, e.LastSeenUtc });
            entity.HasIndex(e => e.FirstSeenRunId);
            entity.HasIndex(e => e.LastSeenRunId);

            entity.Property(e => e.SearchTerm).HasMaxLength(200).IsRequired();

            // Cascade: an attribution has no meaning without its posting. The runs are
            // Restrict, matching what the posting's own run links used to do - a run is
            // history and should not be deletable out from under what references it.
            entity.HasOne(e => e.Posting)
                .WithMany(p => p.SearchTerms)
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.FirstSeenRun)
                .WithMany()
                .HasForeignKey(e => e.FirstSeenRunId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.LastSeenRun)
                .WithMany()
                .HasForeignKey(e => e.LastSeenRunId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// The candidate's own record, and the concepts extracted from it.
    /// </summary>
    /// <remarks>
    /// Every child cascades from the profile: none of it means anything without the person, and
    /// deleting a profile has to take the whole record with it rather than leaving orphaned
    /// employment history behind. The concept side stays Restrict, because a concept is a lookup
    /// shared with the entire posting corpus and removing one must not silently delete evidence.
    ///
    /// <c>ProfileConcepts</c> is deliberately configured to match <c>PostingConcepts</c> column
    /// for column, including <c>Source</c> in the key. Matching joins the two, and a join between
    /// two tables of the same shape is the entire payoff of having fixed that shape before there
    /// was a profile to put in it.
    /// </remarks>
    private static void ConfigureProfiles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CandidateProfileEntity>(entity =>
        {
            entity.ToTable("CandidateProfiles");
            entity.HasKey(e => e.Id);

            // The only lookup path there is. Unique, because a second row for one principal
            // would make "the caller's profile" an ambiguous question.
            entity.HasIndex(e => e.SubjectId).IsUnique();

            entity.Property(e => e.SubjectId).HasMaxLength(SubjectIdLength).IsRequired();

            entity.Property(e => e.FullName).HasMaxLength(200);
            entity.Property(e => e.Headline).HasMaxLength(300);
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.LocationCity).HasMaxLength(150);
            entity.Property(e => e.LocationCountry).HasMaxLength(100);
            entity.Property(e => e.SalaryCurrency).HasMaxLength(10);
            entity.Property(e => e.MinimumSalary).HasPrecision(12, 2);
            entity.Property(e => e.ExtractionInputHash).HasMaxLength(64).IsFixedLength();
            entity.Property(e => e.ExtractionModel).HasMaxLength(100);

            // Unbounded, like a posting's description: a personal statement is prose, and
            // capping it would silently truncate the part the extractor reads best.
            entity.Property(e => e.Summary);
            entity.Property(e => e.ExtractionPayloadJson);
        });

        modelBuilder.Entity<ProfileExperienceEntity>(entity =>
        {
            entity.ToTable("ProfileExperiences");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProfileId, e.Ordinal });

            entity.Property(e => e.Company).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(300).IsRequired();
            entity.Property(e => e.LocationCity).HasMaxLength(150);
            entity.Property(e => e.LocationCountry).HasMaxLength(100);
            entity.Property(e => e.Description);

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Experiences)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileEducationEntity>(entity =>
        {
            entity.ToTable("ProfileEducation");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProfileId, e.Ordinal });

            entity.Property(e => e.Institution).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Qualification).HasMaxLength(200).IsRequired();
            entity.Property(e => e.FieldOfStudy).HasMaxLength(200);
            entity.Property(e => e.Grade).HasMaxLength(100);
            entity.Property(e => e.Description);

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Education)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileProjectEntity>(entity =>
        {
            entity.ToTable("ProfileProjects");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProfileId, e.Ordinal });

            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Url).HasMaxLength(1000);
            entity.Property(e => e.Description);

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Projects)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileCertificationEntity>(entity =>
        {
            entity.ToTable("ProfileCertifications");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProfileId, e.Ordinal });

            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Issuer).HasMaxLength(300);

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Certifications)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileLanguageEntity>(entity =>
        {
            entity.ToTable("ProfileLanguages");
            entity.HasKey(e => new { e.ProfileId, e.Name });

            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Level).HasMaxLength(50);

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Languages)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileLinkEntity>(entity =>
        {
            entity.ToTable("ProfileLinks");
            entity.HasKey(e => new { e.ProfileId, e.Label });

            entity.Property(e => e.Label).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Url).HasMaxLength(1000).IsRequired();

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Links)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileJobTypeEntity>(entity =>
        {
            entity.ToTable("ProfileJobTypes");
            entity.HasKey(e => new { e.ProfileId, e.JobType });

            // Sized to match JobPostingJobTypes exactly. The two are compared, so a difference
            // here would be a truncation that only ever shows up as a match that never fires.
            entity.Property(e => e.JobType).HasMaxLength(30).IsRequired();

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.JobTypes)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileConceptEntity>(entity =>
        {
            entity.ToTable("ProfileConcepts");

            // Source in the key, exactly as on the posting side: a skill the candidate declared
            // and also wrote about is two rows, and the match is allowed to prefer the stronger
            // of the two rather than having to guess which one survived a collapse.
            entity.HasKey(e => new { e.ProfileId, e.ConceptId, e.Source });

            // The supply query: which candidates hold this concept. The mirror of the demand
            // index on PostingConcepts, and what the match join reads.
            entity.HasIndex(e => new { e.ConceptId, e.ProfileId });

            entity.Property(e => e.Source).HasConversion<int>();
            entity.Property(e => e.Polarity).HasConversion<int>();
            entity.Property(e => e.EvidenceText).HasMaxLength(120);

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Concepts)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Concept)
                .WithMany()
                .HasForeignKey(e => e.ConceptId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProfileMentionEntity>(entity =>
        {
            entity.ToTable("ProfileMentions");
            entity.HasKey(e => new { e.ProfileId, e.SurfaceForm });

            entity.HasIndex(e => new { e.Reason, e.SurfaceForm });

            entity.Property(e => e.SurfaceForm).HasMaxLength(120).IsRequired();
            entity.Property(e => e.Reason).HasConversion<int>();

            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Mentions)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Scored matches and the documents generated from them.
    /// </summary>
    /// <remarks>
    /// The profile side cascades and the posting side does not, which is the asymmetry the data
    /// actually has: deleting a profile should take that person's matches with it, while a
    /// posting is a shared record that must not be deletable out from under the matches
    /// referencing it. SQL Server refuses two cascade paths into one table anyway, and this is
    /// the direction that is right on its own terms rather than merely the one permitted.
    /// </remarks>
    /// <summary>
    /// The searches the scraper is told to run, and who asked for each.
    /// </summary>
    /// <remarks>
    /// Two uniqueness rules, and they are different on purpose. <c>Slug</c> is unique across the
    /// whole table because it is a global identity: it becomes a blob name, a
    /// <c>JobPostingSearchTerms</c> key, a Cosmos partition key and a curated Parquet partition.
    /// <c>(OwnerSubjectId, Name)</c> is unique per owner because a name is a label, and two
    /// people naming a search the same thing is not a conflict.
    /// </remarks>
    private static void ConfigureScraperSearches(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScraperSearchEntity>(entity =>
        {
            entity.ToTable("ScraperSearches");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.OwnerSubjectId).HasMaxLength(64).IsRequired();

            // Matches SearchSlug.MaxLength and JobPostingSearchTerms.SearchTerm. A slug longer
            // than the column it ends up in would truncate on the way through and stop matching
            // the blob name it was derived from.
            entity.Property(e => e.Slug).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(80).IsRequired();
            entity.Property(e => e.SearchTerm).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Location).HasMaxLength(200);
            entity.Property(e => e.CountryIndeed).HasMaxLength(100);
            entity.Property(e => e.JobType).HasMaxLength(30);

            entity.HasIndex(e => e.Slug).IsUnique();
            entity.HasIndex(e => new { e.OwnerSubjectId, e.Name }).IsUnique();

            // The publisher's query: every enabled search, whoever owns it.
            entity.HasIndex(e => e.Enabled);
        });

        modelBuilder.Entity<ScraperSearchSiteEntity>(entity =>
        {
            entity.ToTable("ScraperSearchSites");
            entity.HasKey(e => new { e.SearchId, e.Site });

            entity.Property(e => e.Site).HasMaxLength(30).IsRequired();

            entity.HasOne(e => e.Search)
                .WithMany(s => s.Sites)
                .HasForeignKey(e => e.SearchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScraperSearchFilterEntity>(entity =>
        {
            entity.ToTable("ScraperSearchFilters");
            entity.HasKey(e => new { e.SearchId, e.Key });

            entity.Property(e => e.Key).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Value).HasMaxLength(200).IsRequired();

            entity.HasOne(e => e.Search)
                .WithMany(s => s.Filters)
                .HasForeignKey(e => e.SearchId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// What was sent, and the append-only log of what came back.
    /// </summary>
    /// <remarks>
    /// <b>Both unique indexes are the feature, not an optimisation.</b>
    /// <c>(ProfileId, PostingId)</c> stops a retrying client creating a second submission for one
    /// posting; <c>(SubmissionId, IdempotencyKey)</c> stops it recording a second
    /// <c>Submitted</c>. They are here before the write path that needs them because
    /// retro-fitting a unique index to a table that already holds duplicates is a data migration
    /// rather than a schema change.
    ///
    /// The profile side cascades and the posting side does not, exactly as
    /// <see cref="ConfigureMatches"/> has it: deleting a profile should take that person's
    /// submissions with it, while a posting is a shared record that must not be deletable out
    /// from under the rows referencing it. SQL Server refuses two cascade paths into one table
    /// anyway, and this is the direction that is right on its own terms.
    ///
    /// <b>No status column.</b> The status is a fold over the events - see
    /// <c>SubmissionState</c>. Storing it would mean a timer to keep staleness current, a race
    /// between that timer and a real event, and a row that is wrong in between.
    /// </remarks>
    private static void ConfigureSubmissions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SubmissionEntity>(entity =>
        {
            entity.ToTable("Submissions");
            entity.HasKey(e => e.Id);

            // One submission per pair. The write path's idempotency guarantee, in the one place
            // a retry cannot argue with it.
            entity.HasIndex(e => new { e.ProfileId, e.PostingId }).IsUnique();

            // The list query: this candidate's submissions, newest first.
            entity.HasIndex(e => new { e.ProfileId, e.CreatedAtUtc });

            // What a run's own account of itself is checked against. Submitted is countable
            // against these rows and Considered is countable against nothing, which is the whole
            // difference between the summary and the record.
            entity.HasIndex(e => e.RunId);

            entity.Property(e => e.Channel).HasConversion<int>();
            entity.Property(e => e.ApplyUrl).HasMaxLength(SubmissionLimits.MaxApplyUrlLength);

            // int? rather than int, so null keeps meaning "not parked" and no member of
            // ParkReason is zero. Mapped as the enum rather than as a raw int so the queue
            // predicate can ask ParkReasonPolicy.Permanent.Contains(row.ParkedReason) and have
            // EF turn it into an IN clause - a static call over a column has no SQL, which is
            // exactly why that list exists.
            entity.Property(e => e.ParkedReason).HasConversion<int?>();

            entity.Property(e => e.ApprovedBy).HasMaxLength(SubjectIdLength);

            entity.HasOne(e => e.Profile)
                .WithMany()
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Posting)
                .WithMany()
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Restrict);

            // Restrict, for the reason JobPostingSearchTerms restricts to ScrapeRuns: a run is
            // history and must not be deletable out from under the rows that name it. It also
            // has to be Restrict rather than Cascade - a profile already cascades into both
            // tables, and SQL Server refuses two cascade paths into one.
            entity.HasOne(e => e.Run)
                .WithMany()
                .HasForeignKey(e => e.RunId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SubmissionEventEntity>(entity =>
        {
            entity.ToTable("SubmissionEvents");
            entity.HasKey(e => e.Id);

            // A redelivered write converges on the row it already made.
            entity.HasIndex(e => new { e.SubmissionId, e.IdempotencyKey }).IsUnique();

            // What the fold reads: one submission's whole log, in time order.
            entity.HasIndex(e => new { e.SubmissionId, e.AtUtc });

            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Source).HasConversion<int>();
            entity.Property(e => e.Stage).HasMaxLength(SubmissionLimits.MaxStageLength);
            entity.Property(e => e.Note).HasMaxLength(SubmissionLimits.MaxNoteLength);
            entity.Property(e => e.IdempotencyKey)
                .HasMaxLength(SubmissionLimits.MaxIdempotencyKeyLength)
                .IsRequired();

            // The evidence block. Every one of these is optional and none of them may block an
            // event: this is gathered by something driving a browser through somebody else's
            // form, and the interesting runs are the ones that go wrong. Refusing to record that
            // an application was sent because the screenshot failed loses the fact in order to
            // protect the proof of it.
            //
            // Three separate constants for three separate columns, though two of them read 1000
            // today. SubmissionLimits.MaxFinalUrlLength says why: they are measured against the
            // same ATS URLs and agreeing is a coincidence worth keeping rather than a fact to
            // share, because one constant behind both means widening either silently widens the
            // other.
            entity.Property(e => e.ConfirmationRef).HasMaxLength(SubmissionLimits.MaxConfirmationRefLength);
            entity.Property(e => e.FinalUrl).HasMaxLength(SubmissionLimits.MaxFinalUrlLength);
            entity.Property(e => e.ScreenshotRef).HasMaxLength(SubmissionLimits.MaxScreenshotRefLength);

            // Unbounded, like EmphasisedJson: read back whole to be shown, never queried into.
            // The list is bounded where it is built - MaxSubmittedFieldNameLength per name and
            // MaxSubmittedFieldCount names - because a column width would have to guess at how
            // far JSON escaping expands it, and that guess fails as an insert error on a name
            // full of quotes rather than as anything a reader could have predicted.
            entity.Property(e => e.SubmittedFieldsJson);

            entity.HasOne(e => e.Submission)
                .WithMany(s => s.Events)
                .HasForeignKey(e => e.SubmissionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// One row per unattended pass, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately the smallest table here. It holds neither a quota - the daily cap is counted
    /// over <c>SubmissionEvents.AtUtc</c> and would be weakened by a counter that resets when a
    /// crashed client restarts - nor an idempotency guarantee, which
    /// <c>(SubmissionId, IdempotencyKey)</c> already is. What it holds is
    /// <c>RunSummary.Considered</c>, which exists nowhere else: submissions record what was
    /// created and are silent about what was looked at and passed over.
    ///
    /// Cascading from the profile like every other child of it. The submissions that name a run
    /// restrict rather than cascade in the other direction - see <see cref="ConfigureSubmissions"/>.
    /// </remarks>
    private static void ConfigureRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunEntity>(entity =>
        {
            entity.ToTable("Runs");
            entity.HasKey(e => e.Id);

            // The list query, and the one that answers "is a run still going": this candidate's
            // runs, newest first. Openness is read from FinishedAtUtc on the rows this returns
            // rather than indexed separately - a candidate has a handful of runs a day, not a
            // corpus of them.
            entity.HasIndex(e => new { e.ProfileId, e.StartedAtUtc });

            // The same constant SubmissionEvents.Note carries, reused deliberately rather than
            // minting a second: it is the same kind of text under the same argument, and two
            // bounds on one kind of thing is how a column and its validation drift apart.
            entity.Property(e => e.Note).HasMaxLength(SubmissionLimits.MaxNoteLength);

            // Unbounded, and bounded by construction instead: the park breakdown is keyed on
            // ParkReason, so a client cannot invent keys and write an essay into it.
            entity.Property(e => e.SummaryJson);

            entity.HasOne(e => e.Profile)
                .WithMany()
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// What the candidate has said, what a question resolved to last time, and what is still
    /// waiting on a person.
    /// </summary>
    /// <remarks>
    /// <b>Every uniqueness rule here is written so that SQL Server and SQLite agree, and that is
    /// the decision worth reading twice.</b> SQL Server treats two NULLs as equal in a unique
    /// index; SQLite, like the standard, treats them as distinct. So the obvious index - one
    /// unique key over <c>(ProfileId, QuestionHash, Scope, CompanyId, PostingId)</c> - would
    /// reject a second global answer in production and admit it in the test fixture, which is a
    /// guarantee that cannot be tested by the tests that exist. Worse than untested: the
    /// difference only shows up as a live constraint violation somebody meets for the first time
    /// against the real database.
    ///
    /// <b>So no nullable column is ever a key column in a unique index here.</b> The scoped rules
    /// are split one per scope, and the two narrow ones additionally require their id to be
    /// present - so every row that reaches those indexes has non-null values in every key column,
    /// and uniqueness over non-nulls is identical on both engines. The same technique splits the
    /// resolution cache in two along <c>OptionsHash</c>, where null means "this field offered no
    /// options" and is the common case rather than an edge one. Nothing here relies on either
    /// engine's NULL semantics, which is what makes the tests that assert these indexes worth
    /// having.
    ///
    /// <b>A row whose scope and id disagree - <c>Company</c> with no company - lands in no index
    /// at all, and that is correct.</b> <c>FormAnswer.Create</c> refuses to build one and
    /// <c>AnswerPrecedence.Applies</c> would never return one, so such a row is unreachable by
    /// construction; an index pretending to police it would be enforcing a rule two layers above
    /// already enforce, in the one place that cannot explain itself.
    ///
    /// <b>Superseding, not updating.</b> The uniqueness is over <i>live</i> answers only, which
    /// is what lets the history stay: replacing an answer stamps <c>SupersededAtUtc</c> on the
    /// old row and inserts a new one, and the old row leaves the index without leaving the table.
    /// <c>OpenQuestions</c> works the same way on <c>AnsweredAtUtc</c> - answering a question
    /// closes it rather than deleting it, so what was asked is still readable afterwards.
    /// </remarks>
    private static void ConfigureFormAnswers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FormAnswerEntity>(entity =>
        {
            entity.ToTable("FormAnswers");
            entity.HasKey(e => e.Id);

            // One live answer per question per scope. Three indexes rather than one over the
            // nullable scope ids - see the remarks above for why that is not a tidying exercise.
            entity.HasIndex(e => new { e.ProfileId, e.QuestionHash }, "IX_FormAnswers_LiveGlobal")
                .IsUnique()
                .HasFilter(LiveAnswersAt(AnswerScope.Global));

            entity.HasIndex(e => new { e.ProfileId, e.QuestionHash, e.CompanyId }, "IX_FormAnswers_LiveCompany")
                .IsUnique()
                .HasFilter(LiveAnswersAt(AnswerScope.Company, " AND [CompanyId] IS NOT NULL"));

            entity.HasIndex(e => new { e.ProfileId, e.QuestionHash, e.PostingId }, "IX_FormAnswers_LivePosting")
                .IsUnique()
                .HasFilter(LiveAnswersAt(AnswerScope.Posting, " AND [PostingId] IS NOT NULL"));

            // The resolver's own lookup, and unfiltered on purpose: AnswerPrecedence.Best is
            // handed superseded answers too, because the last thing somebody actually said beats
            // a blank when it is all there is. A filtered index cannot serve that read.
            entity.HasIndex(e => new { e.ProfileId, e.QuestionHash });

            // The escape from phrasing: two employers wording one question differently produce
            // two hashes, and a name written once lets both resolve.
            entity.HasIndex(e => new { e.ProfileId, e.Name });

            // The dashboard's list, newest first - the same shape as Submissions and
            // ApplicationDocuments, and for the same reason.
            entity.HasIndex(e => new { e.ProfileId, e.AnsweredAtUtc });

            entity.Property(e => e.Name).HasMaxLength(FormAnswerLimits.MaxNameLength);
            entity.Property(e => e.QuestionText)
                .HasMaxLength(FormAnswerLimits.MaxQuestionTextLength)
                .IsRequired();

            entity.Property(e => e.QuestionHash)
                .HasMaxLength(FormAnswerLimits.QuestionHashLength)
                .IsFixedLength()
                .IsRequired();

            // Bounded by the question it is derived from: normalisation folds and never grows,
            // so anything longer than the source text could only be a row written by something
            // other than QuestionKey.Normalise.
            entity.Property(e => e.NormalisedQuestion)
                .HasMaxLength(FormAnswerLimits.MaxQuestionTextLength)
                .IsRequired();

            entity.Property(e => e.Value).HasMaxLength(FormAnswerLimits.MaxValueLength).IsRequired();

            entity.Property(e => e.Scope).HasConversion<int>();
            entity.Property(e => e.Source).HasConversion<int>();

            entity.HasOne(e => e.Profile)
                .WithMany()
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict on both scope targets, the way everything else here restricts to a shared
            // record: a company row is a lookup and a posting is the corpus, and neither should
            // be able to take somebody's stored answer with it.
            entity.HasOne(e => e.Company)
                .WithMany()
                .HasForeignKey(e => e.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Posting)
                .WithMany()
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FormAnswerResolutionEntity>(entity =>
        {
            entity.ToTable("FormAnswerResolutions");
            entity.HasKey(e => e.Id);

            // One cached outcome per question, per option set. Split on whether there were
            // options at all, so the nullable column is never a key column - the same rule the
            // answer indexes follow, and here the null case is the common one: most fields are
            // free text.
            entity.HasIndex(e => new { e.ProfileId, e.QuestionHash }, "IX_FormAnswerResolutions_FreeText")
                .IsUnique()
                .HasFilter("[OptionsHash] IS NULL");

            entity.HasIndex(
                    e => new { e.ProfileId, e.QuestionHash, e.OptionsHash },
                    "IX_FormAnswerResolutions_Options")
                .IsUnique()
                .HasFilter("[OptionsHash] IS NOT NULL");

            entity.Property(e => e.QuestionHash)
                .HasMaxLength(FormAnswerLimits.QuestionHashLength)
                .IsFixedLength()
                .IsRequired();

            entity.Property(e => e.OptionsHash)
                .HasMaxLength(FormAnswerLimits.QuestionHashLength)
                .IsFixedLength();

            entity.Property(e => e.ResolvedName).HasMaxLength(FormAnswerLimits.MaxNameLength);

            // MaxNoteLength again, on the ApplyRun.Note precedent: a rationale is a sentence or
            // two of context about one decision, which is what that constant already bounds.
            entity.Property(e => e.Rationale).HasMaxLength(SubmissionLimits.MaxNoteLength).IsRequired();

            // 100, like every other column here recording which deployment served a call.
            entity.Property(e => e.Model).HasMaxLength(100);

            entity.HasOne(e => e.Profile)
                .WithMany()
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict: an answer is never deleted, and a cache row pointing at one must not be
            // what makes that untrue.
            entity.HasOne(e => e.Answer)
                .WithMany()
                .HasForeignKey(e => e.AnswerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenQuestionEntity>(entity =>
        {
            entity.ToTable("OpenQuestions");
            entity.HasKey(e => e.Id);

            // One live question per wording. A run meeting the same question on four adverts
            // must put it to a person once, and a person who has answered it must not be asked
            // again next week. Both key columns are non-nullable, so this behaves the same on
            // SQL Server and on the SQLite the tests run against.
            entity.HasIndex(e => new { e.ProfileId, e.QuestionHash }, "IX_OpenQuestions_Unanswered")
                .IsUnique()
                .HasFilter("[AnsweredAtUtc] IS NULL");

            // What list_open_questions reads: this candidate's queue, oldest first.
            entity.HasIndex(e => new { e.ProfileId, e.AskedAtUtc });

            // What the queue predicate reads. A posting parked for MissingAnswer comes back only
            // once nothing unanswered is left against it, and that is asked once per shortlist
            // row against a database billed by the second.
            entity.HasIndex(e => new { e.ProfileId, e.PostingId });

            entity.Property(e => e.QuestionText)
                .HasMaxLength(FormAnswerLimits.MaxQuestionTextLength)
                .IsRequired();

            entity.Property(e => e.QuestionHash)
                .HasMaxLength(FormAnswerLimits.QuestionHashLength)
                .IsFixedLength()
                .IsRequired();

            // Unbounded, like every other JSON column here: read back whole, never queried into.
            entity.Property(e => e.OptionsJson);

            entity.HasOne(e => e.Profile)
                .WithMany()
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Posting)
                .WithMany()
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Restrict);

            // A run is history: an abandoned one still has to be able to say what it asked.
            entity.HasOne(e => e.Run)
                .WithMany()
                .HasForeignKey(e => e.RunId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Answer)
                .WithMany()
                .HasForeignKey(e => e.AnswerId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// The filter a live-answer index is built on, with the scope written as its stored number.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="AnswerScope"/> rather than typed as a literal, so renumbering the
    /// enum regenerates the index instead of silently pointing it at a different scope - which is
    /// the one thing a magic number inside an index filter can never report. Bracket quoting
    /// because both SQL Server and SQLite accept it, and this string is emitted verbatim by
    /// whichever provider is building the model.
    ///
    /// Invariant, because the filter is compared character for character against what the
    /// migration wrote: built under a culture with different digits it would read as a changed
    /// index on every model build.
    /// </remarks>
    private static string LiveAnswersAt(AnswerScope scope, string alsoRequired = "")
        => string.Create(
            CultureInfo.InvariantCulture,
            $"[SupersededAtUtc] IS NULL AND [Scope] = {(int)scope}{alsoRequired}");

    private static void ConfigureMatches(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobMatchEntity>(entity =>
        {
            entity.ToTable("JobMatches");
            entity.HasKey(e => e.Id);

            // One row per pair. The sweep upserts against this, so a re-score converges rather
            // than accumulating a row per night - the same contract every other write path in
            // this system carries.
            entity.HasIndex(e => new { e.ProfileId, e.PostingId }).IsUnique();

            // The shortlist query: this candidate's matches, best first. Without it, "show me
            // my top 50" is a scan of every pair ever scored for them. Kept although the list
            // now orders by RankScore, because the sweep still selects its model budget by
            // score and a band sweep still filters on it.
            entity.HasIndex(e => new { e.ProfileId, e.Score });

            // What the list actually orders by. Same shape as the index above and the same
            // reason for existing - without it, showing fifty matches sorts every pair ever
            // scored for this candidate.
            entity.HasIndex(e => new { e.ProfileId, e.RankScore });

            // What the nightly sweep selects on: this profile, not yet assessed.
            entity.HasIndex(e => new { e.ProfileId, e.AssessedAtUtc });

            entity.Property(e => e.AssessmentModel).HasMaxLength(100);
            entity.Property(e => e.Verdict).HasConversion<int?>();
            entity.Property(e => e.Rationale).HasMaxLength(2000);

            // Unbounded: read back whole to be shown, never queried into.
            entity.Property(e => e.ComponentsJson);
            entity.Property(e => e.MatchedJson);
            entity.Property(e => e.GapsJson);
            entity.Property(e => e.StrengthsJson);
            entity.Property(e => e.AssessmentGapsJson);
            entity.Property(e => e.EmphasiseJson);
            entity.Property(e => e.AssessmentPayloadJson);

            entity.HasOne(e => e.Profile)
                .WithMany()
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Posting)
                .WithMany()
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Vectors. Side tables rather than columns, so the blob is read by the two passes that
        // need it and by none of the queries that merely touch the row it hangs off.
        modelBuilder.Entity<PostingEmbeddingEntity>(entity =>
        {
            entity.ToTable("PostingEmbeddings");

            // The posting id is the key. One vector per posting - a second could only ever be a
            // stale first, and the staleness columns already say which.
            entity.HasKey(e => e.PostingId);

            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsFixedLength();

            entity.HasOne(e => e.Posting)
                .WithMany()
                .HasForeignKey(e => e.PostingId)
                // A posting that goes takes its vector with it. Unlike a match, this row is
                // derived from the posting alone and means nothing without it.
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileEmbeddingEntity>(entity =>
        {
            entity.ToTable("ProfileEmbeddings");
            entity.HasKey(e => e.ProfileId);

            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.InputHash).HasMaxLength(64).IsFixedLength();

            entity.HasOne(e => e.Profile)
                .WithMany()
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApplicationDocumentEntity>(entity =>
        {
            entity.ToTable("ApplicationDocuments");
            entity.HasKey(e => e.Id);

            // Rows per generation rather than one row updated in place, so a revision is part
            // of the key rather than a column that overwrites what the candidate already sent.
            entity.HasIndex(e => new { e.ProfileId, e.PostingId, e.Revision }).IsUnique();
            entity.HasIndex(e => new { e.ProfileId, e.CreatedAtUtc });

            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.Instructions).HasMaxLength(2000);

            // Unbounded: whole documents, and two JSON columns read back whole.
            entity.Property(e => e.CurriculumVitaeMarkdown);
            entity.Property(e => e.CoverLetterMarkdown);
            entity.Property(e => e.EmphasisedJson);
            entity.Property(e => e.DraftedAnswersJson);

            // The rendered files. SubmissionLimits.MaxScreenshotRefLength is the Azure blob-name
            // ceiling rather than a fact about screenshots - read its remarks - so these four
            // columns share it because they share the ceiling. That is the opposite case from
            // MaxApplyUrlLength and MaxFinalUrlLength above, which agree by coincidence and are
            // deliberately kept apart: here, widening one and not the others would be the bug.
            // A path too long for these is a path the store would have refused anyway, so the
            // column can never be what breaks a reference to a file that exists.
            entity.Property(e => e.CvBlobPath).HasMaxLength(SubmissionLimits.MaxScreenshotRefLength);
            entity.Property(e => e.CvDocxBlobPath).HasMaxLength(SubmissionLimits.MaxScreenshotRefLength);
            entity.Property(e => e.CoverLetterBlobPath).HasMaxLength(SubmissionLimits.MaxScreenshotRefLength);

            // 64 and fixed, exactly as every other SHA-256 hex column here - see the note on
            // JobPostings.CrossBoardKey.
            entity.Property(e => e.CvSha256).HasMaxLength(64).IsFixedLength();

            entity.HasOne(e => e.Profile)
                .WithMany()
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Posting)
                .WithMany()
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// Batches handed to a provider, and the documents inside them.
    /// </summary>
    /// <remarks>
    /// The items cascade from the batch: an item has no meaning without the submission it
    /// belonged to. The posting side is Restrict for the reason it always is here - a posting is
    /// a shared record and must not be deletable out from under the rows referencing it.
    /// </remarks>
    private static void ConfigureExtractionBatches(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExtractionBatchEntity>(entity =>
        {
            entity.ToTable("ExtractionBatches");
            entity.HasKey(e => e.Id);

            // The idempotency guarantee: one row per provider batch, so a collector running
            // twice cannot apply one batch's results twice.
            entity.HasIndex(e => e.ProviderBatchId).IsUnique();

            // What the collector selects on: everything still open, oldest first.
            entity.HasIndex(e => new { e.State, e.SubmittedAtUtc });

            entity.Property(e => e.ProviderBatchId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.State).HasConversion<int>();
            entity.Property(e => e.Error).HasMaxLength(2000);
        });

        modelBuilder.Entity<ExtractionBatchItemEntity>(entity =>
        {
            entity.ToTable("ExtractionBatchItems");
            entity.HasKey(e => new { e.BatchId, e.PostingId });

            entity.Property(e => e.InputHash).HasMaxLength(64).IsFixedLength().IsRequired();

            entity.HasOne(e => e.Batch)
                .WithMany(b => b.Items)
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Posting)
                .WithMany()
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// The vocabulary projected into SQL, so an analysis query can roll a posting up to a
    /// domain without loading the embedded resource.
    /// </summary>
    /// <remarks>
    /// Every one of these tables is reseeded wholesale from <c>concepts.json</c> when its
    /// version moves. Nothing else writes them, which is why none of them carries audit
    /// columns: the file history is the audit trail.
    /// </remarks>
    private static void ConfigureConceptGraph(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConceptEntity>(entity =>
        {
            entity.ToTable("Concepts");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.ConceptKey).IsUnique();
            entity.HasIndex(e => e.Kind);

            entity.Property(e => e.ConceptKey).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PrefLabel).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Kind).HasConversion<int>();
        });

        modelBuilder.Entity<ConceptLabelEntity>(entity =>
        {
            entity.ToTable("ConceptLabels");
            entity.HasKey(e => e.Id);

            // Not unique across concepts: an ambiguous form and a resolvable one are allowed
            // to coexist, and enforcing uniqueness here would reject a vocabulary the resolver
            // already validates more precisely at load time.
            entity.HasIndex(e => e.NormalizedLabel);
            entity.HasIndex(e => new { e.ConceptId, e.NormalizedLabel }).IsUnique();

            entity.Property(e => e.NormalizedLabel).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Label).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Kind).HasConversion<int>();

            entity.HasOne(e => e.Concept)
                .WithMany(c => c.Labels)
                .HasForeignKey(e => e.ConceptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConceptRelationEntity>(entity =>
        {
            entity.ToTable("ConceptRelations");
            entity.HasKey(e => new { e.FromConceptId, e.ToConceptId, e.RelationType });

            // The reverse direction is queried as often as the forward one - "what is under
            // this domain" and "what is this concept under" are both everyday questions.
            entity.HasIndex(e => new { e.ToConceptId, e.RelationType });

            entity.Property(e => e.RelationType).HasConversion<int>();

            entity.HasOne(e => e.FromConcept)
                .WithMany()
                .HasForeignKey(e => e.FromConceptId)
                .OnDelete(DeleteBehavior.Cascade);

            // One side must be Restrict: SQL Server refuses multiple cascade paths into the
            // same table, and a self-referencing edge is exactly that shape.
            entity.HasOne(e => e.ToConcept)
                .WithMany()
                .HasForeignKey(e => e.ToConceptId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConceptClosureEntity>(entity =>
        {
            entity.ToTable("ConceptClosure");
            entity.HasKey(e => new { e.AncestorId, e.DescendantId });

            // The rollup direction: given a posting concept, find its ancestors.
            entity.HasIndex(e => new { e.DescendantId, e.AncestorId });

            entity.HasOne(e => e.Ancestor)
                .WithMany()
                .HasForeignKey(e => e.AncestorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Descendant)
                .WithMany()
                .HasForeignKey(e => e.DescendantId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// What each posting asserts, and what it said that we could not resolve.
    /// </summary>
    /// <remarks>
    /// Everything here cascades from the posting: an assertion has no meaning without the
    /// document that made it, the same reasoning <c>JobPostingSearchTerms</c> already follows.
    /// The concept side is Restrict, because a concept is a lookup and removing one should not
    /// silently delete evidence.
    /// </remarks>
    private static void ConfigureAssertions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PostingConceptEntity>(entity =>
        {
            entity.ToTable("PostingConcepts");

            // Source is part of the key on purpose - see the entity remarks. A concept the
            // board tagged and the description also mentioned is two rows, not one.
            entity.HasKey(e => new { e.PostingId, e.ConceptId, e.Source });

            // The demand query: which postings want this concept.
            entity.HasIndex(e => new { e.ConceptId, e.PostingId });
            entity.HasIndex(e => e.ResolverVersion);

            entity.Property(e => e.Source).HasConversion<int>();
            entity.Property(e => e.Polarity).HasConversion<int>();
            entity.Property(e => e.EvidenceText).HasMaxLength(120);

            entity.HasOne(e => e.Posting)
                .WithMany(p => p.Concepts)
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Concept)
                .WithMany()
                .HasForeignKey(e => e.ConceptId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PostingMentionEntity>(entity =>
        {
            entity.ToTable("PostingMentions");
            entity.HasKey(e => new { e.PostingId, e.SurfaceForm });

            // The vocabulary growth query: the most frequent unresolved forms, by reason.
            entity.HasIndex(e => new { e.Reason, e.SurfaceForm });

            entity.Property(e => e.SurfaceForm).HasMaxLength(120).IsRequired();
            entity.Property(e => e.Reason).HasConversion<int>();

            entity.HasOne(e => e.Posting)
                .WithMany(p => p.Mentions)
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobPostingJobTypeEntity>(entity =>
        {
            entity.ToTable("JobPostingJobTypes");
            entity.HasKey(e => new { e.PostingId, e.JobType });

            entity.HasIndex(e => e.JobType);
            entity.Property(e => e.JobType).HasMaxLength(30).IsRequired();

            entity.HasOne(e => e.Posting)
                .WithMany(p => p.JobTypes)
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PostingTagEntity>(entity =>
        {
            entity.ToTable("PostingTags");
            entity.HasKey(e => new { e.PostingId, e.Tag });

            entity.HasIndex(e => e.Tag);
            entity.Property(e => e.Tag).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Value).HasMaxLength(100);

            entity.HasOne(e => e.Posting)
                .WithMany(p => p.Tags)
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PostingExtractionEntity>(entity =>
        {
            entity.ToTable("PostingExtractions");
            entity.HasKey(e => e.Id);

            // The idempotency key. A replayed queue message, or a posting re-listed with
            // unchanged text, converges on one row instead of accumulating duplicates - the
            // same contract ScrapeRuns.BlobPath carries for ingestion.
            entity.HasIndex(e => new { e.PostingId, e.ExtractorVersion, e.InputHash }).IsUnique();

            entity.Property(e => e.InputHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(e => e.Model).HasMaxLength(100);

            // Unbounded: the whole model response, so re-deriving a column never means
            // re-calling the model.
            entity.Property(e => e.PayloadJson);

            entity.HasOne(e => e.Posting)
                .WithMany(p => p.Extractions)
                .HasForeignKey(e => e.PostingId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
