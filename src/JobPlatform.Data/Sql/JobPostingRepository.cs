using JobPlatform.Core.Dedup;
using JobPlatform.Core.Enrichment;
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
    public async Task<IngestResult> IngestAsync(
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

        // This search's existing attributions, loaded once. A posting can already be here
        // from a different search, which is exactly the case the link table exists for.
        var existingIds = existing.Values.Select(p => p.Id).ToList();
        var links = await db.JobPostingSearchTerms
            .Where(l => l.SearchTerm == context.SearchTerm && existingIds.Contains(l.PostingId))
            .ToDictionaryAsync(l => l.PostingId, ct);

        // Surface forms already held by a mention the model wrote. The rebuild below leaves the
        // model's rows in place, and PostingMentions is keyed on (PostingId, SurfaceForm), so a
        // taxonomy mention of a form the model also failed to resolve would collide with a row
        // that is deliberately staying put. Loaded once for the batch, like everything else here.
        var reservedForms = (await db.PostingMentions
            .Where(m => existingIds.Contains(m.PostingId)
                && m.Reason == MentionReason.UnknownModelSkill)
            .Select(m => new { m.PostingId, m.SurfaceForm })
            .ToListAsync(ct))
            .GroupBy(m => m.PostingId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<string>)g.Select(m => m.SurfaceForm)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));

        // Enrichment is in-memory CPU work, so it runs here rather than in its own pass:
        // it adds no round trip and does not lengthen the connection.
        var graph = ConceptGraph.Default;
        var enrichedByKey = postings.ToDictionary(
            p => p.SourceKey,
            p => PostingEnricher.Enrich(p, graph),
            StringComparer.OrdinalIgnoreCase);

        // Both dimensions resolved set-based, once for the whole batch. A per-row lookup
        // here would be exactly the shape the cost model forbids.
        var conceptIds = await db.Concepts
            .Select(c => new { c.ConceptKey, c.Id })
            .ToDictionaryAsync(c => c.ConceptKey, c => c.Id, StringComparer.Ordinal, ct);

        var companies = await ResolveCompaniesAsync(enrichedByKey.Values, now, ct);

        // Child rows are rewritten only for postings that actually changed, or whose
        // enrichment is stale. Most postings in a daily re-scrape are neither, and rewriting
        // theirs would be churn against a 2 GB database for no change in the data.
        var rebuild = new List<long>();
        var missingConcepts = new HashSet<string>(StringComparer.Ordinal);

        // Postings whose text is new or has changed. Only these are worth a model call: an
        // unchanged re-listing already has an extraction keyed on the same input hash, and
        // enqueueing it would mean a few hundred messages a day whose only outcome is a
        // database round trip that decides to do nothing.
        var needExtraction = new List<string>();

        int added = 0, updated = 0, unchanged = 0;

        foreach (var posting in postings)
        {
            var contentHash = JobFingerprint.ContentHash(posting);
            var location = JobLocation.Parse(posting.Location);
            var enriched = enrichedByKey[posting.SourceKey];

            if (existing.TryGetValue(posting.SourceKey, out var entity))
            {
                var changed = HasMaterialChange(entity, posting, contentHash);
                var stale = entity.EnrichmentVersion != EnrichedPosting.CurrentVersion;

                Apply(entity, posting, contentHash, location);
                ApplyEnrichment(entity, enriched, companies);
                entity.LastSeenUtc = now;
                entity.SeenCount++;

                if (changed || stale)
                {
                    rebuild.Add(entity.Id);
                    AttachDerivedRows(
                        entity,
                        enriched,
                        conceptIds,
                        missingConcepts,
                        reservedForms.GetValueOrDefault(entity.Id, EmptyForms));
                    needExtraction.Add(entity.SourceKey);
                }

                if (links.TryGetValue(entity.Id, out var link))
                {
                    link.LastSeenUtc = now;
                    link.LastSeenRunId = run.Id;
                    link.SeenCount++;

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
                    // Already in the table, but this search had not turned it up before.
                    // New to this run, whether or not the posting itself changed - which is
                    // what makes "new today" mean something per search rather than only for
                    // whichever search happened to see it first.
                    db.JobPostingSearchTerms.Add(NewLink(entity.Id, context.SearchTerm, run.Id, now));
                    added++;
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
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    SeenCount = 1,
                };

                Apply(entity, posting, contentHash, location);
                ApplyEnrichment(entity, enriched, companies);
                AttachDerivedRows(entity, enriched, conceptIds, missingConcepts, EmptyForms);

                // Through the navigation, not the DbSet: the posting has no Id until
                // SaveChanges, and EF fills the foreign key from the relationship.
                entity.SearchTerms.Add(NewLink(0, context.SearchTerm, run.Id, now));

                db.JobPostings.Add(entity);
                needExtraction.Add(entity.SourceKey);
                added++;
            }
        }

        // Deleted before SaveChanges so the inserts queued above land on a clean slate.
        // Set-based and only for the ids that changed, so an unchanged re-scrape issues
        // nothing at all here.
        await ClearDerivedRowsAsync(rebuild, ct);

        if (missingConcepts.Count > 0)
        {
            // Loud on purpose. This happens when the code ships a vocabulary the database has
            // not been reseeded with, and the symptom is otherwise silent: assertions simply
            // stop appearing for the new concepts and every count involving them is quietly
            // low. Reseed with `JobPlatform.DbAdmin -- seed-concepts`.
            logger.LogWarning(
                "{Count} concept keys are in the vocabulary but not in the database and were "
                + "skipped: {Keys}. The Concepts table needs reseeding.",
                missingConcepts.Count,
                string.Join(", ", missingConcepts.Take(10)));
        }

        var outcome = new UpsertOutcome(added, updated, unchanged);

        run.NewCount = outcome.New;
        run.UpdatedCount = outcome.Updated;
        run.UnchangedCount = outcome.Unchanged;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Run {RunId}: {New} new, {Updated} updated, {Unchanged} unchanged posting(s).",
            run.Id, outcome.New, outcome.Updated, outcome.Unchanged);

        return new IngestResult(run, outcome, needExtraction, [.. enrichedByKey.Values]);
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

        // Counted on the attribution rows, not the postings: the run ids that matter are
        // this search's, and a posting first surfaced by a different search is still new
        // to this one the day it turns up here.
        var newPostings = await db.JobPostingSearchTerms
            .CountAsync(l => runIds.Contains(l.FirstSeenRunId), ct);

        var cumulative = await db.JobPostingSearchTerms
            .CountAsync(l => runsUpToDate.Contains(l.FirstSeenRunId), ct);

        // Characteristics are taken from the postings as the day last saw them.
        var lastSeenOnDate = db.JobPostingSearchTerms
            .Where(l => runIds.Contains(l.LastSeenRunId))
            .Select(l => l.Posting!);
        var distinctSeen = await lastSeenOnDate.CountAsync(ct);

        var bySite = await lastSeenOnDate
            .GroupBy(p => p.Site)
            .Select(g => new { Site = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var remoteCount = await lastSeenOnDate.CountAsync(p => p.IsRemote == true, ct);
        var statedRemote = await lastSeenOnDate.CountAsync(p => p.IsRemote != null, ct);
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
            // Denominator is the postings that stated a work mode, matching
            // MetricsCalculator.CalculateRemote. Two definitions of one metric name is how a
            // dashboard ends up disagreeing with itself depending on which store answered.
            RemoteShare = Share(remoteCount, statedRemote),
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
            || !string.Equals(entity.JobType, posting.JobType, StringComparison.Ordinal)
            // A repost is a real event about the job, so it counts as a change.
            // PostingAgeDays and FreshnessClass deliberately do not: both move with the
            // clock alone, and including them would mark every freehire posting updated
            // on every run, which is precisely what this metric exists to distinguish.
            || entity.RepostCount != posting.RepostCount;

    private static JobPostingSearchTerm NewLink(
        long postingId, string searchTerm, int runId, DateTimeOffset now) => new()
    {
        PostingId = postingId,
        SearchTerm = searchTerm,
        FirstSeenRunId = runId,
        LastSeenRunId = runId,
        FirstSeenUtc = now,
        LastSeenUtc = now,
        SeenCount = 1,
    };

    private static void Apply(
        JobPostingEntity entity,
        JobPosting posting,
        string contentHash,
        JobLocation location)
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
        entity.CompanyNumEmployees = posting.CompanyNumEmployees;
        entity.ExperienceRange = posting.ExperienceRange;
        entity.Summary = posting.Summary;
        entity.FreshnessClass = posting.FreshnessClass;
        entity.PostingAgeDays = posting.PostingAgeDays;
        entity.RepostCount = posting.RepostCount;
        entity.FakeFreshness = posting.FakeFreshness;
    }

    /// <summary>
    /// Finds or creates a company row per distinct folded key, in one query.
    /// </summary>
    /// <remarks>
    /// New rows are returned untracked-by-id and attached through the posting navigation, so
    /// EF fills the foreign key at <c>SaveChanges</c> and no second round trip is needed to
    /// learn the generated ids.
    /// </remarks>
    private async Task<Dictionary<string, CompanyEntity>> ResolveCompaniesAsync(
        IEnumerable<EnrichedPosting> enriched,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var byKey = new Dictionary<string, EnrichedPosting>(StringComparer.Ordinal);

        foreach (var item in enriched)
        {
            if (item.CompanyKey is { } key)
            {
                // Last write wins, so the freshest spelling and blurb are the ones stored.
                byKey[key] = item;
            }
        }

        if (byKey.Count == 0)
        {
            return [];
        }

        var keys = byKey.Keys.ToList();

        var companies = await db.Companies
            .Where(c => keys.Contains(c.CompanyKey))
            .ToDictionaryAsync(c => c.CompanyKey, StringComparer.Ordinal, ct);

        foreach (var (key, item) in byKey)
        {
            if (!companies.TryGetValue(key, out var company))
            {
                company = new CompanyEntity
                {
                    CompanyKey = key,
                    DisplayName = item.Posting.Company ?? key,
                    FirstSeenUtc = now,
                };

                db.Companies.Add(company);
                companies[key] = company;
            }

            company.DisplayName = item.Posting.Company ?? company.DisplayName;
            company.Industry = item.Posting.CompanyIndustry ?? company.Industry;
            company.EmployeesBand = item.Posting.CompanyNumEmployees ?? company.EmployeesBand;
            company.EmployeesMin = item.EmployeesMin ?? company.EmployeesMin;
            company.EmployeesMax = item.EmployeesMax ?? company.EmployeesMax;
            company.Revenue = item.Posting.CompanyRevenue ?? company.Revenue;
            company.Url = item.Posting.CompanyUrl ?? company.Url;
            company.Description = item.Posting.CompanyDescription ?? company.Description;
            company.Rating = item.Posting.CompanyRating ?? company.Rating;
            company.ReviewsCount = item.Posting.CompanyReviewsCount ?? company.ReviewsCount;
            company.LastSeenUtc = now;
        }

        return companies;
    }

    /// <summary>The derived scalar columns. Child rows are handled separately.</summary>
    private static void ApplyEnrichment(
        JobPostingEntity entity,
        EnrichedPosting enriched,
        Dictionary<string, CompanyEntity> companies)
    {
        entity.SourceBoard = enriched.Posting.SourceBoard;
        entity.Applicants = enriched.Posting.Applicants;
        entity.ApplicantCount = enriched.Posting.ApplicantCount;
        entity.ListingType = enriched.Posting.ListingType;
        entity.WorkFromHomeType = enriched.Posting.WorkFromHomeType;
        entity.VacancyCount = enriched.Posting.VacancyCount;
        entity.HasContactEmail = enriched.Posting.HasContactEmail;

        entity.Seniority = enriched.Seniority;
        entity.RoleFamily = enriched.RoleFamily;
        entity.WorkArrangement = enriched.WorkArrangement;
        entity.HybridDaysInOffice = enriched.HybridDaysInOffice;
        entity.YearsExperienceMin = enriched.YearsExperienceMin;
        entity.YearsExperienceMax = enriched.YearsExperienceMax;

        entity.AnnualSalaryMin = enriched.AnnualSalaryMin;
        entity.AnnualSalaryMax = enriched.AnnualSalaryMax;
        entity.AnnualSalaryCurrency = enriched.SalaryCurrency;
        entity.SalaryFromText = enriched.SalaryFromText;
        entity.SalaryStatedInterval = enriched.SalaryStatedInterval;

        entity.VisaSponsorship = enriched.VisaSponsorship;
        entity.RequiresSecurityClearance = enriched.RequiresSecurityClearance;
        entity.RequiresDegree = enriched.RequiresDegree;
        entity.Ir35 = enriched.Ir35;

        entity.EnrichmentVersion = enriched.Version;

        if (enriched.CompanyKey is { } key && companies.TryGetValue(key, out var company))
        {
            // The navigation rather than the id: a company created in this batch has no id
            // until SaveChanges, and EF resolves the relationship either way.
            entity.CompanyRef = company;
        }
    }

    /// <summary>
    /// Queues the assertion, mention, job-type and tag rows for one posting.
    /// </summary>
    /// <remarks>
    /// Through the navigation collections rather than the DbSets, for the same reason the
    /// search-term link is: a new posting has no Id yet. On an existing posting the collection
    /// was never loaded, so everything added here is treated as new - which is correct only
    /// because <see cref="ClearDerivedRowsAsync"/> removes the previous generation first.
    /// </remarks>
    private static readonly IReadOnlySet<string> EmptyForms =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static void AttachDerivedRows(
        JobPostingEntity entity,
        EnrichedPosting enriched,
        Dictionary<string, int> conceptIds,
        HashSet<string> missingConcepts,
        IReadOnlySet<string> reservedForms)
    {
        foreach (var assertion in enriched.Concepts)
        {
            if (!conceptIds.TryGetValue(assertion.ConceptKey, out var conceptId))
            {
                missingConcepts.Add(assertion.ConceptKey);
                continue;
            }

            entity.Concepts.Add(new PostingConceptEntity
            {
                ConceptId = conceptId,
                Source = assertion.Source,
                Polarity = assertion.Polarity,
                YearsMin = assertion.YearsMin,
                YearsMax = assertion.YearsMax,
                EvidenceText = Truncate(assertion.EvidenceText, 120),
                Confidence = assertion.Confidence,
                ResolverVersion = enriched.Version,
            });
        }

        // Keyed on (PostingId, SurfaceForm), and the same form can arrive twice: the
        // description says "Go" and the board also lists a skill "Go" that resolves to
        // nothing. Two mentions, two reasons, one key. The ambiguous reading wins because it
        // is the more specific claim - the vocabulary knows that form and distrusts it.
        var mentions = enriched.Mentions
            .GroupBy(m => m.SurfaceForm, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(m => m.Reason).First())
            // The same key can also be held by a mention the model wrote, which this rebuild
            // does not clear. The model saw the whole document and said the form names a
            // technology; the taxonomy pass only knows it did not resolve. The stronger claim
            // stays.
            .Where(m => !reservedForms.Contains(m.SurfaceForm));

        foreach (var mention in mentions)
        {
            entity.Mentions.Add(new PostingMentionEntity
            {
                SurfaceForm = Truncate(mention.SurfaceForm, 120)!,
                Reason = mention.Reason,
                Occurrences = mention.Occurrences,
                ResolverVersion = enriched.Version,
            });
        }

        foreach (var jobType in enriched.JobTypes)
        {
            entity.JobTypes.Add(new JobPostingJobTypeEntity { JobType = jobType });
        }

        foreach (var tag in enriched.Tags)
        {
            entity.Tags.Add(new PostingTagEntity { Tag = tag.Name, Value = tag.Value });
        }
    }

    /// <summary>
    /// Removes the previous generation of derived rows for postings being rewritten.
    /// </summary>
    /// <remarks>
    /// Four set-based statements, issued only when something changed, rather than loading
    /// every child row to delete it through the change tracker. Nothing is issued at all for
    /// an unchanged re-scrape, which is the common case.
    ///
    /// Extractions are deliberately not touched: a model response is expensive and is keyed on
    /// the input hash, so it survives a re-enrichment of the same text.
    /// </remarks>
    /// <summary>
    /// Removes the rows this pass is about to rewrite, and only those.
    /// </summary>
    /// <remarks>
    /// Each producer owns its own rows. Enrichment writes what the board published and what a
    /// string match found; extraction writes what the model read. Clearing the lot would mean
    /// enrichment silently discarding the other producer's work, and the loss is not
    /// recoverable by retrying: the re-extraction this queues is keyed on a hash of the
    /// description, so for a posting marked stale by a vocabulary change - unchanged text, same
    /// hash - it converges on the extraction row that is already there and skips. The
    /// assertions would be gone, the audit table would say the work had been done, and the only
    /// visible symptom would be a graded share quietly falling back toward zero.
    ///
    /// Where the text genuinely changed the model's rows are stale too, and they are still
    /// replaced - just by their owner. <c>PostingExtractionWriter</c> deletes its own rows
    /// before writing new ones, and the changed hash means it runs. Between the two passes the
    /// posting carries the previous reading, which is a few minutes of slightly stale evidence
    /// rather than a permanent hole.
    /// </remarks>
    private async Task ClearDerivedRowsAsync(List<long> postingIds, CancellationToken ct)
    {
        if (postingIds.Count == 0)
        {
            return;
        }

        await db.PostingConcepts
            .Where(x => postingIds.Contains(x.PostingId) && x.Source != AssertionSource.Model)
            .ExecuteDeleteAsync(ct);
        await db.PostingMentions
            .Where(x => postingIds.Contains(x.PostingId)
                && x.Reason != MentionReason.UnknownModelSkill)
            .ExecuteDeleteAsync(ct);
        await db.JobPostingJobTypes.Where(x => postingIds.Contains(x.PostingId)).ExecuteDeleteAsync(ct);
        await db.PostingTags.Where(x => postingIds.Contains(x.PostingId)).ExecuteDeleteAsync(ct);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
