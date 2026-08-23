using Azure.Storage.Blobs;
using JobPlatform.Core.Curated;
using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Parquet.Serialization;

namespace JobPlatform.Ingestion.Curated;

/// <summary>What one export wrote.</summary>
public readonly record struct CuratedExportResult(int Partitions, int Postings, int Pairs);

/// <summary>
/// The curated container, distinct from the landing one.
/// </summary>
/// <remarks>
/// A named wrapper rather than a second <see cref="BlobContainerClient"/> registration.
/// Registering the same type twice resolves to whichever came last, and the failure mode here
/// is the export silently writing into the landing container - which is both the loop the
/// separate container exists to prevent and the write the scoped RBAC grant exists to refuse.
/// A distinct type makes that unrepresentable rather than merely unlikely.
/// </remarks>
public sealed record CuratedContainer(BlobContainerClient Client);

/// <summary>
/// Writes the curated Parquet zone from SQL.
/// </summary>
/// <remarks>
/// <b>Recomputed per partition, never appended.</b> A partition is rewritten whole from
/// whatever SQL currently holds, so running the export twice converges instead of doubling —
/// the same reasoning the daily rollup follows, and the reason a failed run needs no cleanup
/// before the next one.
///
/// It runs on a timer rather than inline with the ingest because model extractions land
/// asynchronously, minutes to hours after the CSV was read. Exporting inline would freeze the
/// partition before the most interesting column in it exists.
///
/// The SQL here is read-heavy and the database is billed on wall-clock time online, so each
/// partition is one query with everything it needs projected in a single pass. This is the one
/// place in the system that deliberately reads a lot of SQL at once; it is a scheduled batch,
/// not a request path, and it is why the dashboard's separation from SQL matters.
/// </remarks>
public sealed class CuratedExporter(
    JobsDbContext db,
    CuratedContainer curated,
    ILogger<CuratedExporter> logger)
{
    public async Task<CuratedExportResult> ExportAsync(DateOnly date, CancellationToken ct = default)
    {
        var day = date.ToString("yyyy-MM-dd");

        var terms = await db.JobPostingSearchTerms
            .Where(l => l.LastSeenRun!.ScrapeDate == date)
            .Select(l => l.SearchTerm)
            .Distinct()
            .ToListAsync(ct);

        if (terms.Count == 0)
        {
            logger.LogInformation("No postings were seen on {Date}; nothing to export.", day);
            return default;
        }

        var graph = ConceptGraph.Default;
        var totalPostings = 0;
        var totalPairs = 0;

        foreach (var term in terms)
        {
            var (postings, pairs) = await BuildAsync(term, date, graph, ct);

            if (postings.Count == 0)
            {
                continue;
            }

            await WriteAsync($"postings/searchTerm={Slug(term)}/date={day}/postings.parquet", postings, ct);
            await WriteAsync($"pairs/searchTerm={Slug(term)}/date={day}/pairs.parquet", pairs, ct);

            totalPostings += postings.Count;
            totalPairs += pairs.Count;
        }

        logger.LogInformation(
            "Exported {Postings} posting(s) and {Pairs} pair(s) across {Partitions} partition(s) for {Date}.",
            totalPostings, totalPairs, terms.Count, day);

        return new CuratedExportResult(terms.Count, totalPostings, totalPairs);
    }

    /// <summary>
    /// One partition: the postings a search last surfaced on that scrape date.
    /// </summary>
    /// <remarks>
    /// Keyed on the <b>run's scrape date</b>, which comes from the blob name, rather than on
    /// <c>LastSeenUtc</c>, which is stamped when the row was written. The difference only
    /// shows up during a backfill, and then it shows up badly: re-ingesting a year of blobs
    /// today would stamp every posting with today's timestamp and collapse the whole corpus
    /// into a single partition dated the day the backfill ran. The scrape date is a property
    /// of the data, so a partition means the same thing however many times it is rebuilt.
    ///
    /// Each posting lands in exactly one partition per search - the day that search last saw
    /// it. That makes the zone a snapshot by recency rather than a full daily census, which
    /// is what the link table can actually support: it records first and last, not every day
    /// in between.
    /// </remarks>
    private async Task<(List<CuratedPosting> Postings, List<CuratedPair> Pairs)> BuildAsync(
        string term,
        DateOnly date,
        ConceptGraph graph,
        CancellationToken ct)
    {
        // Projected rather than materialising entities: Description is nvarchar(max) and is
        // not in the curated row, so pulling it across would multiply the transfer for nothing.
        var rows = await db.JobPostingSearchTerms
            .Where(l => l.SearchTerm == term && l.LastSeenRun!.ScrapeDate == date)
            .Select(l => new
            {
                l.Posting!.Id,
                l.Posting.SourceKey,
                l.Posting.Site,
                l.Posting.SourceBoard,
                l.Posting.Title,
                l.Posting.Company,
                CompanyKey = l.Posting.CompanyRef!.CompanyKey,
                l.Posting.LocationCity,
                l.Posting.LocationRegion,
                l.Posting.LocationCountry,
                l.Posting.DatePosted,
                l.Posting.FirstSeenUtc,
                l.Posting.LastSeenUtc,
                l.Posting.SeenCount,
                l.Posting.Seniority,
                l.Posting.RoleFamily,
                l.Posting.WorkArrangement,
                l.Posting.HybridDaysInOffice,
                l.Posting.IsRemote,
                l.Posting.YearsExperienceMin,
                l.Posting.YearsExperienceMax,
                l.Posting.AnnualSalaryMin,
                l.Posting.AnnualSalaryMax,
                l.Posting.AnnualSalaryCurrency,
                l.Posting.SalaryFromText,
                l.Posting.SalaryStatedInterval,
                l.Posting.VisaSponsorship,
                l.Posting.RequiresSecurityClearance,
                l.Posting.RequiresDegree,
                l.Posting.Ir35,
                l.Posting.DescriptionLength,
                l.Posting.HasContactEmail,
                l.Posting.EnrichmentVersion,
                JobTypes = l.Posting.JobTypes.Select(j => j.JobType).ToList(),
                Tags = l.Posting.Tags.Select(t => t.Value == null ? t.Tag : t.Tag + "=" + t.Value).ToList(),
                Concepts = l.Posting.Concepts.Select(c => new
                {
                    Key = c.Concept!.ConceptKey,
                    Label = c.Concept.PrefLabel,
                    c.Concept.Kind,
                    c.Source,
                    c.Polarity,
                    c.YearsMin,
                    c.YearsMax,
                    c.Confidence,
                    c.EvidenceText,
                }).ToList(),
            })
            .ToListAsync(ct);

        var postings = new List<CuratedPosting>(rows.Count);
        var pairs = new List<CuratedPair>(rows.Count * 8);

        foreach (var row in rows)
        {
            var conceptKeys = row.Concepts.Select(c => c.Key).Distinct(StringComparer.Ordinal).ToList();

            // Rolled up here rather than left to the reader: the closure is small and static,
            // and embedding it saves every downstream query from joining to a table that does
            // not exist outside SQL.
            var domains = conceptKeys
                .SelectMany(k => graph.Ancestors(k).Keys)
                .Where(k => k.StartsWith("area.", StringComparison.Ordinal)
                    || k.StartsWith("type.", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            postings.Add(new CuratedPosting
            {
                source_key = row.SourceKey,
                site = row.Site,
                source_board = row.SourceBoard,
                title = row.Title,
                company = row.Company,
                company_key = row.CompanyKey,
                location_city = row.LocationCity,
                location_region = row.LocationRegion,
                location_country = row.LocationCountry,
                date_posted = row.DatePosted?.ToDateTime(TimeOnly.MinValue),
                first_seen_utc = row.FirstSeenUtc.UtcDateTime,
                last_seen_utc = row.LastSeenUtc.UtcDateTime,
                seen_count = row.SeenCount,
                seniority = (int)row.Seniority,
                seniority_name = row.Seniority.ToString(),
                role_family = (int)row.RoleFamily,
                role_family_name = row.RoleFamily.ToString(),
                work_arrangement = (int)row.WorkArrangement,
                work_arrangement_name = row.WorkArrangement.ToString(),
                hybrid_days_in_office = row.HybridDaysInOffice,
                is_remote = row.IsRemote,
                years_experience_min = row.YearsExperienceMin,
                years_experience_max = row.YearsExperienceMax,
                annual_salary_min = row.AnnualSalaryMin,
                annual_salary_max = row.AnnualSalaryMax,
                annual_salary_currency = row.AnnualSalaryCurrency,
                salary_from_text = row.SalaryFromText,
                salary_stated_interval = row.SalaryStatedInterval,
                visa_sponsorship = row.VisaSponsorship,
                requires_security_clearance = row.RequiresSecurityClearance,
                requires_degree = row.RequiresDegree,
                ir35 = row.Ir35,
                job_types = Join(row.JobTypes),
                concept_keys = Join(conceptKeys),
                domain_keys = Join(domains),
                tags = Join(row.Tags),
                description_length = row.DescriptionLength,
                has_contact_email = row.HasContactEmail,
                enrichment_version = row.EnrichmentVersion,
                search_term = term,
            });

            foreach (var concept in row.Concepts)
            {
                pairs.Add(new CuratedPair
                {
                    source_key = row.SourceKey,
                    title = row.Title,
                    seniority = (int)row.Seniority,
                    role_family_name = row.RoleFamily.ToString(),
                    concept_key = concept.Key,
                    concept_label = concept.Label,
                    concept_kind = concept.Kind.ToString(),
                    source = concept.Source.ToString(),
                    polarity = (int)concept.Polarity,
                    years_min = concept.YearsMin,
                    years_max = concept.YearsMax,
                    confidence = concept.Confidence,
                    evidence = concept.EvidenceText,
                    last_seen_utc = row.LastSeenUtc.UtcDateTime,
                });
            }
        }

        return (postings, pairs);
    }

    private async Task WriteAsync<T>(string path, IReadOnlyCollection<T> rows, CancellationToken ct)
        where T : new()
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Buffered rather than streamed straight to the blob: Parquet writes its footer last
        // and needs to seek, which a blob upload stream cannot do. A day's partition is a few
        // MB, so the memory is not worth a more complicated arrangement.
        using var buffer = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, buffer, cancellationToken: ct);
        buffer.Position = 0;

        // Overwrite: the partition is recomputed whole, so the previous generation is stale
        // by definition rather than something to merge with.
        await curated.Client
            .GetBlobClient($"curated/{path}")
            .UploadAsync(buffer, overwrite: true, cancellationToken: ct);
    }

    private static string? Join(IReadOnlyCollection<string> values)
        => values.Count == 0 ? null : string.Join('|', values);

    /// <summary>
    /// Hive-style partition values have to survive being a path segment.
    /// </summary>
    /// <remarks>
    /// Every engine that reads Hive partitioning parses <c>key=value</c> out of the directory
    /// name, so a value containing a slash, a space or an equals sign silently produces a
    /// partition nobody can select.
    /// </remarks>
    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        return new string(chars).Trim('-');
    }
}
