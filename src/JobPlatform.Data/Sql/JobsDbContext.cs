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
            entity.HasIndex(e => e.LastSeenUtc);
            entity.HasIndex(e => e.Company);
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
        ConfigureExtractionBatches(modelBuilder);

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

            // Entra object ids are GUIDs, but the column is sized for the general case rather
            // than assuming a format the token is not obliged to keep.
            entity.Property(e => e.SubjectId).HasMaxLength(100).IsRequired();

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

            // Unbounded: whole documents.
            entity.Property(e => e.CurriculumVitaeMarkdown);
            entity.Property(e => e.CoverLetterMarkdown);
            entity.Property(e => e.EmphasisedJson);

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
