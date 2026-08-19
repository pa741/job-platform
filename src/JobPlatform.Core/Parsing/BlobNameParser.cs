using System.Text.RegularExpressions;
using JobPlatform.Core.Model;

namespace JobPlatform.Core.Parsing;

/// <summary>
/// Recovers run metadata from the scraper's blob naming convention,
/// <c>jobs/&lt;search-term-slug&gt;_&lt;yyyy-MM-ddTHH-mm-ssZ&gt;.csv</c>
/// (see <c>scrape_jobs.py</c> in the job-scrapper repo).
/// </summary>
public static partial class BlobNameParser
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH-mm-ss'Z'";

    [GeneratedRegex(
        @"^(?<slug>.+)_(?<ts>\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z)\.csv$",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
    private static partial Regex BlobNamePattern { get; }

    /// <summary>
    /// Parses <paramref name="blobPath"/>. Falls back to the whole file name as the search
    /// term and <paramref name="fallbackTimestamp"/> as the scrape time when the name does
    /// not match — an unrecognised name must not stop an ingest.
    /// </summary>
    public static ScrapeRunContext Parse(
        string blobPath,
        DateTimeOffset fallbackTimestamp,
        string? etag = null,
        long sizeBytes = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);

        var fileName = blobPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
        var match = BlobNamePattern.Match(fileName);

        string searchTerm;
        DateTimeOffset scrapedAt;

        if (match.Success &&
            DateTimeOffset.TryParseExact(
                match.Groups["ts"].Value,
                TimestampFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            searchTerm = match.Groups["slug"].Value;
            scrapedAt = parsed;
        }
        else
        {
            searchTerm = Path.GetFileNameWithoutExtension(fileName);
            scrapedAt = fallbackTimestamp;
        }

        return new ScrapeRunContext
        {
            BlobPath = blobPath,
            SearchTerm = searchTerm,
            ScrapedAtUtc = scrapedAt,
            BlobETag = etag,
            BlobSizeBytes = sizeBytes,
        };
    }
}
