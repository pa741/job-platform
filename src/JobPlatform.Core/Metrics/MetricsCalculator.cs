using System.Security.Cryptography;
using System.Text;
using JobPlatform.Core.Dedup;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Model;
using JobPlatform.Core.Parsing;
using JobPlatform.Core.Text;

namespace JobPlatform.Core.Metrics;

/// <summary>
/// Turns a parsed run into the <see cref="RunDigest"/> stored in Cosmos.
/// Deliberately pure and Azure-free so the whole metric surface is unit-testable.
/// </summary>
public sealed class MetricsCalculator(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private const int TopN = 20;
    private const int TopKeywordCount = 25;

    /// <param name="enriched">
    /// What the enricher concluded, when the caller has it. Absent in tests that only care
    /// about the raw counts, and absent for anything replaying a digest without re-ingesting.
    /// </param>
    public RunDigest Calculate(
        ScrapeRunContext context,
        CsvParseResult parseResult,
        UpsertOutcome upsert,
        long durationMs,
        IReadOnlyList<EnrichedPosting>? enriched = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);

        var postings = parseResult.Postings;
        var scrapeDate = context.ScrapeDate;

        return new RunDigest
        {
            Id = DocumentId(context.BlobPath),
            SearchTerm = context.SearchTerm,
            BlobPath = context.BlobPath,
            BlobSizeBytes = context.BlobSizeBytes,
            ScrapedAtUtc = context.ScrapedAtUtc,
            IngestedAtUtc = _time.GetUtcNow(),
            ScrapeDate = scrapeDate.ToString("yyyy-MM-dd"),
            DurationMs = durationMs,
            Counts = new RunCounts
            {
                RowsInFile = parseResult.RowsInFile,
                Parsed = postings.Count,
                Invalid = parseResult.InvalidRows,
                InFileDuplicates = parseResult.DuplicateRows,
                CrossSiteDuplicates = CountCrossSiteDuplicates(postings),
                New = upsert.New,
                Updated = upsert.Updated,
                Unchanged = upsert.Unchanged,
            },
            BySite = CountBy(postings, p => p.Site),
            ByJobType = CountJobTypes(postings),
            Remote = CalculateRemote(postings),
            Freshness = CalculateFreshness(postings, scrapeDate),
            Salary = CalculateSalary(postings),
            TopCompanies = TopBy(postings, p => p.Company, TopN),
            TopLocations = TopBy(postings, p => NullIfEmpty(JobLocation.Parse(p.Location).Display), TopN),
            TitleKeywords = TopKeywords(postings),
            Enrichment = CalculateEnrichment(enriched),
            DescriptionLength = CalculateLengths(postings),
            FieldFillRates = parseResult.FieldFillRates,
        };
    }

    /// <summary>Deterministic, so a redelivered Event Grid event upserts the same document.</summary>
    public static string DocumentId(string blobPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(blobPath));
        return string.Concat("run|", Convert.ToHexStringLower(hash).AsSpan(0, 16));
    }

    public static string DailyRollupId(string searchTerm, DateOnly date)
        => $"daily|{searchTerm}|{date:yyyy-MM-dd}";

    /// <summary>
    /// The structured view of a run.
    /// </summary>
    /// <remarks>
    /// Empty rather than absent when the caller supplied nothing, so a consumer never has to
    /// distinguish "not computed" from "computed as zero" - the dashboard reads these
    /// directly and a null section would be a second code path on every chart.
    /// </remarks>
    private static EnrichmentBreakdown CalculateEnrichment(IReadOnlyList<EnrichedPosting>? enriched)
    {
        if (enriched is null || enriched.Count == 0)
        {
            return new EnrichmentBreakdown();
        }

        var withSalary = enriched.Count(e => e.AnnualSalaryMin is not null || e.AnnualSalaryMax is not null);
        var fromText = enriched.Count(e => e.SalaryFromText);

        // The midpoint where a range is given, the single figure where only one is. Averaging
        // the two ends of a range and the one end of a floor would be mixing two different
        // measurements, which is the same mistake SalaryFromText exists to prevent.
        var salaries = enriched
            .Select(e => e.AnnualSalaryMin is { } min && e.AnnualSalaryMax is { } max
                ? (min + max) / 2
                : e.AnnualSalaryMin ?? e.AnnualSalaryMax)
            .OfType<decimal>()
            .Order()
            .ToList();

        return new EnrichmentBreakdown
        {
            BySeniority = CountEnum(enriched, e => e.Seniority.ToString()),
            ByWorkArrangement = CountEnum(enriched, e => e.WorkArrangement.ToString()),
            ByRoleFamily = CountEnum(enriched, e => e.RoleFamily.ToString()),

            // Distinct per posting: a concept the board tagged and the description also
            // mentioned is two assertions and one piece of demand.
            TopConcepts = Rank(enriched.SelectMany(e =>
                e.Concepts.Select(c => c.ConceptKey).Distinct(StringComparer.Ordinal))),

            TopDomains = Rank(enriched.SelectMany(e => e.Concepts
                .SelectMany(c => ConceptGraph.Default.Ancestors(c.ConceptKey).Keys)
                .Where(k => k.StartsWith("area.", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal))),

            SalaryCoverage = Share(withSalary, enriched.Count),
            SalaryFromTextShare = Share(fromText, withSalary),
            MedianAnnualSalary = salaries.Count == 0 ? null : salaries[salaries.Count / 2],
            UnresolvedMentions = enriched.Sum(e => e.Mentions.Count),
        };
    }

    private static IReadOnlyDictionary<string, int> CountEnum(
        IReadOnlyList<EnrichedPosting> enriched,
        Func<EnrichedPosting, string> select)
        => enriched
            .GroupBy(select, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static IReadOnlyList<NamedCount> Rank(IEnumerable<string> keys, int take = 15)
        => [.. keys
            .GroupBy(k => k, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(g => new NamedCount(g.Key, g.Count()))];

    private static int CountCrossSiteDuplicates(IReadOnlyList<JobPosting> postings)
        => postings.Count - postings.Select(JobFingerprint.ContentHash).Distinct(StringComparer.Ordinal).Count();

    /// <remarks>
    /// The share counts against postings that stated a work mode, not against every posting.
    /// <c>IsRemote</c> is nullable because silence is common and is not a "no" - and a share
    /// computed over the whole population would fall whenever coverage fell, turning a
    /// scraper regression into an apparent market shift.
    /// </remarks>
    private static RemoteBreakdown CalculateRemote(IReadOnlyList<JobPosting> postings)
    {
        var remote = postings.Count(p => p.IsRemote == true);
        var onSite = postings.Count(p => p.IsRemote == false);

        return new RemoteBreakdown(
            remote,
            onSite,
            postings.Count - remote - onSite,
            Share(remote, remote + onSite));
    }

    private static FreshnessBreakdown CalculateFreshness(IReadOnlyList<JobPosting> postings, DateOnly scrapeDate)
    {
        var dated = postings.Where(p => p.DatePosted is not null).Select(p => p.DatePosted!.Value).ToList();
        var ages = dated.Select(d => (double)(scrapeDate.DayNumber - d.DayNumber)).ToList();

        return new FreshnessBreakdown
        {
            WithDatePosted = dated.Count,
            MissingDatePosted = postings.Count - dated.Count,
            Coverage = Share(dated.Count, postings.Count),
            PostedToday = dated.Count(d => d == scrapeDate),
            OlderThanSevenDays = ages.Count(a => a > 7),
            MedianAgeDays = Median(ages),
        };
    }

    private static SalaryBreakdown CalculateSalary(IReadOnlyList<JobPosting> postings)
    {
        var withSalary = postings.Where(p => p.MinAmount is not null || p.MaxAmount is not null).ToList();
        var amounts = withSalary
            .Select(p => p.MinAmount ?? p.MaxAmount)
            .Where(a => a > 0)
            .Select(a => a!.Value)
            .OrderBy(a => a)
            .ToList();

        return new SalaryBreakdown
        {
            WithSalary = withSalary.Count,
            Coverage = Share(withSalary.Count, postings.Count),
            ByCurrency = CountBy(withSalary, p => p.Currency),
            MinAnnual = amounts.Count > 0 ? amounts[0] : null,
            MedianAnnual = amounts.Count > 0 ? amounts[amounts.Count / 2] : null,
            MaxAnnual = amounts.Count > 0 ? amounts[^1] : null,
        };
    }

    private static LengthStats CalculateLengths(IReadOnlyList<JobPosting> postings)
    {
        var lengths = postings.Select(p => p.DescriptionLength).OrderBy(l => l).ToList();
        if (lengths.Count == 0)
        {
            return new LengthStats(0, 0, 0);
        }

        return new LengthStats(Percentile(lengths, 0.50), Percentile(lengths, 0.90), lengths[^1]);
    }

    private static IReadOnlyList<NamedCount> TopKeywords(IReadOnlyList<JobPosting> postings)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var posting in postings)
        {
            // Distinct per title, so "Engineer II, Engineer" does not count twice for one job.
            foreach (var token in TitleTokenizer.Tokenize(posting.Title).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }
        }

        return counts
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Take(TopKeywordCount)
            .Select(kvp => new NamedCount(kvp.Key, kvp.Value))
            .ToList();
    }

    private static IReadOnlyDictionary<string, int> CountJobTypes(IReadOnlyList<JobPosting> postings)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var posting in postings)
        {
            if (string.IsNullOrWhiteSpace(posting.JobType))
            {
                counts["unspecified"] = counts.GetValueOrDefault("unspecified") + 1;
                continue;
            }

            // "parttime, fulltime" counts toward both.
            var types = posting.JobType.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var type in types)
            {
                var key = type.ToLowerInvariant();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return Sorted(counts);
    }

    private static IReadOnlyDictionary<string, int> CountBy(
        IEnumerable<JobPosting> postings,
        Func<JobPosting, string?> selector)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var posting in postings)
        {
            var key = selector(posting);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return Sorted(counts);
    }

    private static IReadOnlyList<NamedCount> TopBy(
        IEnumerable<JobPosting> postings,
        Func<JobPosting, string?> selector,
        int take)
        => CountBy(postings, selector)
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(kvp => new NamedCount(kvp.Key, kvp.Value))
            .ToList();

    private static IReadOnlyDictionary<string, int> Sorted(Dictionary<string, int> counts)
        => counts
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static double Share(int part, int total)
        => total == 0 ? 0 : Math.Round((double)part / total, 4);

    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : Math.Round((values[mid - 1] + values[mid]) / 2, 2);
    }

    private static int Percentile(List<int> sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
