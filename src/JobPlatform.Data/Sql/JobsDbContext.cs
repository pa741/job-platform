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
