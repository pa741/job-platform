using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();

    public DbSet<JobPostingEntity> JobPostings => Set<JobPostingEntity>();

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
            entity.HasIndex(e => new { e.SearchTerm, e.FirstSeenUtc });
            entity.HasIndex(e => new { e.SearchTerm, e.LastSeenUtc });
            entity.HasIndex(e => e.Company);

            entity.Property(e => e.SourceKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Site).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ExternalId).HasMaxLength(150).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(e => e.SearchTerm).HasMaxLength(200).IsRequired();

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

            entity.Property(e => e.JobUrl).HasMaxLength(1000);
            entity.Property(e => e.JobUrlDirect).HasMaxLength(1000);
            entity.Property(e => e.CompanyUrl).HasMaxLength(1000);

            // Descriptions run to several KB and are needed intact for CV matching, so no
            // MaxLength is set. EF already maps an unbounded string to nvarchar(max) on SQL
            // Server; spelling that type out explicitly would also make the model
            // unbuildable on any other provider, including the SQLite used by the tests.
            entity.Property(e => e.Description);

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
}
