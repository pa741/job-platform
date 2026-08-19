using JobPlatform.Core.Dedup;
using JobPlatform.Core.Metrics;
using JobPlatform.Core.Model;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Data.Sql;

public sealed class JobPostingRepository(JobsDbContext db, ILogger<JobPostingRepository> logger)
{
    /// <summary>
    /// Records the run and reconciles its postings against what is already stored.
    /// </summary>
    /// <remarks>
    /// Two round trips regardless of row count: one query to load the postings this run
    /// might touch, one <c>SaveChanges</c> for the whole batch. That matters because the
    /// database is serverless and billed by the second — a per-row round trip would keep
    /// it awake far longer than the work justifies.
    /// </remarks>
    public async Task<(ScrapeRun Run, UpsertOutcome Outcome)> IngestAsync(
        ScrapeRunContext context,
        IReadOnlyList<JobPosting> postings,
        int rowsInFile,
        int invalidRows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(postings);

        var now = DateTimeOffset.UtcNow;

        var run = await db.ScrapeRuns
            .FirstOrDefaultAsync(r => r.BlobPath == context.BlobPath, ct);

        if (run is null)
        {
            run = new ScrapeRun
            {
                BlobPath = context.BlobPath,
                SearchTerm = context.SearchTerm,
                ScrapedAtUtc = context.ScrapedAtUtc,
                ScrapeDate = context.ScrapeDate,
            };
            db.ScrapeRuns.Add(run);
        }
        else
        {
            logger.LogInformation(
                "Blob {BlobPath} was already ingested as run {RunId}; reprocessing idempotently.",
                context.BlobPath, run.Id);
        }

        run.BlobETag = context.BlobETag;
        run.BlobSizeBytes = context.BlobSizeBytes;
        run.IngestedAtUtc = now;
        run.RowCount = rowsInFile;
        run.ParsedCount = postings.Count;
        run.InvalidCount = invalidRows;

        // The run needs an Id before postings can reference it.
        await db.SaveChangesAsync(ct);

        // A List, not an array: on an array, `Contains` can bind to
        // MemoryExtensions.Contains(ReadOnlySpan<T>, T) rather than Enumerable.Contains,
        // which EF cannot translate and which fails at runtime with
        // "GenericArguments[1], 'System.ReadOnlySpan`1[System.String]' ... violates the
        // constraint of type parameter 'TRet'".
        var sourceKeys = postings.Select(p => p.SourceKey).ToList();

        var existing = await db.JobPostings
            .Where(p => sourceKeys.Contains(p.SourceKey))
            .ToDictionaryAsync(p => p.SourceKey, StringComparer.OrdinalIgnoreCase, ct);

        int added = 0, updated = 0, unchanged = 0;

        foreach (var posting in postings)
        {
            var contentHash = JobFingerprint.ContentHash(posting);
            var location = JobLocation.Parse(posting.Location);

            if (existing.TryGetValue(posting.SourceKey, out var entity))
            {
                var changed = HasMaterialChange(entity, posting, contentHash);

                Apply(entity, posting, contentHash, location, context.SearchTerm);
                entity.LastSeenUtc = now;
                entity.LastSeenRunId = run.Id;
                entity.SeenCount++;

                if (changed)
                {
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }
            else
            {
                entity = new JobPostingEntity
                {
                    SourceKey = posting.SourceKey,
                    Site = posting.Site,
                    ExternalId = posting.ExternalId,
                    ContentHash = contentHash,
                    Title = posting.Title,
                    SearchTerm = context.SearchTerm,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    FirstSeenRunId = run.Id,
                    LastSeenRunId = run.Id,
                    SeenCount = 1,
                };

                Apply(entity, posting, contentHash, location, context.SearchTerm);
                db.JobPostings.Add(entity);
                added++;
            }
        }

        var outcome = new UpsertOutcome(added, updated, unchanged);

        run.NewCount = outcome.New;
        run.UpdatedCount = outcome.Updated;
        run.UnchangedCount = outcome.Unchanged;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Run {RunId}: {New} new, {Updated} updated, {Unchanged} unchanged posting(s).",
            run.Id, outcome.New, outcome.Updated, outcome.Unchanged);

        return (run, outcome);
    }

    /// <summary>
    /// Aggregates for one search term on one day, recomputed rather than incremented so a
    /// replayed blob converges instead of double-counting.
    /// </summary>
    /// <remarks>
    /// Everything is scoped by the *runs* that belong to the date, not by row timestamps.
    /// <c>FirstSeenUtc</c>/<c>LastSeenUtc</c> record when we ingested a posting, which is
    /// not the day it was scraped - a blob scraped at 23:50 and ingested after midnight,
    /// or any backfill, would otherwise land in the wrong bucket or in none at all.
    /// </remarks>
    public async Task<DailyRollup> BuildDailyRollupAsync(
        string searchTerm, DateOnly date, CancellationToken ct = default)
    {
        var runsOnDate = db.ScrapeRuns
            .Where(r => r.SearchTerm == searchTerm && r.ScrapeDate == date);

        var runIds = await runsOnDate.Select(r => r.Id).ToListAsync(ct);

        var runsUpToDate = db.ScrapeRuns
            .Where(r => r.SearchTerm == searchTerm && r.ScrapeDate <= date)
            .Select(r => r.Id);

        // How many postings the day's scraping actually surfaced.
        var postingsSeen = runIds.Count == 0
            ? 0
            : await runsOnDate.SumAsync(r => r.ParsedCount, ct);

        var newPostings = await db.JobPostings
            .CountAsync(p => runIds.Contains(p.FirstSeenRunId), ct);

        var cumulative = await db.JobPostings
            .CountAsync(p => runsUpToDate.Contains(p.FirstSeenRunId), ct);

        // Characteristics are taken from the postings as the day last saw them.
        var lastSeenOnDate = db.JobPostings.Where(p => runIds.Contains(p.LastSeenRunId));
        var distinctSeen = await lastSeenOnDate.CountAsync(ct);

        var bySite = await lastSeenOnDate
            .GroupBy(p => p.Site)
            .Select(g => new { Site = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var remoteCount = await lastSeenOnDate.CountAsync(p => p.IsRemote, ct);
        var withSalary = await lastSeenOnDate
            .CountAsync(p => p.MinAmount != null || p.MaxAmount != null, ct);

        var topCompanies = await lastSeenOnDate
            .Where(p => p.Company != null)
            .GroupBy(p => p.Company!)
            .Select(g => new { Company = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Company)
            .Take(20)
            .ToListAsync(ct);

        return new DailyRollup
        {
            Id = MetricsCalculator.DailyRollupId(searchTerm, date),
            SearchTerm = searchTerm,
            Date = date.ToString("yyyy-MM-dd"),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            RunsIngested = runIds.Count,
            PostingsSeen = postingsSeen,
            NewPostings = newPostings,
            CumulativePostings = cumulative,
            BySite = bySite.ToDictionary(x => x.Site, x => x.Count, StringComparer.OrdinalIgnoreCase),
            RemoteShare = Share(remoteCount, distinctSeen),
            SalaryCoverage = Share(withSalary, distinctSeen),
            TopCompanies = [.. topCompanies.Select(x => new NamedCount(x.Company, x.Count))],
        };
    }

    private static double Share(int part, int total)
        => total == 0 ? 0 : Math.Round((double)part / total, 4);

    /// <summary>
    /// Whether the board changed anything we care about, as opposed to simply re-listing
    /// the posting. Drives the new/updated/unchanged split in the metrics.
    /// </summary>
    private static bool HasMaterialChange(JobPostingEntity entity, JobPosting posting, string contentHash)
        => !string.Equals(entity.ContentHash, contentHash, StringComparison.Ordinal)
            || entity.DescriptionLength != posting.DescriptionLength
            || entity.MinAmount != posting.MinAmount
            || entity.MaxAmount != posting.MaxAmount
            || entity.IsRemote != posting.IsRemote
            || entity.DatePosted != posting.DatePosted
            || !string.Equals(entity.JobType, posting.JobType, StringComparison.Ordinal);

    private static void Apply(
        JobPostingEntity entity,
        JobPosting posting,
        string contentHash,
        JobLocation location,
        string searchTerm)
    {
        entity.ContentHash = contentHash;
        entity.Title = posting.Title;
        entity.Company = posting.Company;
        entity.LocationRaw = posting.Location;
        entity.LocationCity = location.City;
        entity.LocationRegion = location.Region;
        entity.LocationCountry = location.Country;
        entity.IsRemote = posting.IsRemote;
        entity.JobType = posting.JobType;
        entity.DatePosted = posting.DatePosted;
        entity.MinAmount = posting.MinAmount;
        entity.MaxAmount = posting.MaxAmount;
        entity.Currency = posting.Currency;
        entity.SalaryInterval = posting.SalaryInterval;
        entity.SalarySource = posting.SalarySource;
        entity.JobLevel = posting.JobLevel;
        entity.JobFunction = posting.JobFunction;
        entity.CompanyIndustry = posting.CompanyIndustry;
        entity.JobUrl = posting.JobUrl;
        entity.JobUrlDirect = posting.JobUrlDirect;
        entity.CompanyUrl = posting.CompanyUrl;
        entity.Description = posting.Description;
        entity.DescriptionLength = posting.DescriptionLength;
        entity.SearchTerm = searchTerm;
    }
}
