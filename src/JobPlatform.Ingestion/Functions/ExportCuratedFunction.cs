using System.Globalization;
using System.Text.Json;
using JobPlatform.Ingestion.Curated;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Rebuilds the curated Parquet zone for a day.
/// </summary>
/// <remarks>
/// A timer rather than something the ingest calls, because model extractions land
/// asynchronously — minutes to hours after the CSV was read. Exporting inline would freeze the
/// partition before the columns worth exporting exist.
///
/// It runs at 04:00 UTC, several hours after the scraper's daily run and after any extraction
/// queued by it has drained. The window is generous on purpose: re-running is free, because a
/// partition is recomputed whole rather than appended to.
///
/// Yesterday, not today. The day the timer fires into has barely started, and exporting it
/// would produce a partition that is correct and almost empty, then leave it that way until
/// something re-ran it.
/// </remarks>
public sealed class ExportCuratedFunction(
    CuratedExporter exporter,
    ILogger<ExportCuratedFunction> logger)
{
    public sealed record ExportRequest(string? Date, int? Days);

    /// <summary>Ceiling on a backfill request, so one call cannot walk the whole corpus.</summary>
    private const int MaxDays = 90;

    [Function(nameof(ExportCuratedFunction))]
    public async Task RunAsync(
        [TimerTrigger("0 0 4 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        logger.LogInformation("Exporting curated partitions for {Date}.", date);

        await exporter.ExportAsync(date, ct);
    }

    /// <summary>
    /// Admin endpoint for backfill, running the same exporter over a range.
    /// </summary>
    /// <remarks>
    /// No <c>admin/</c> route prefix: the Functions host reserves it, and claiming it puts the
    /// function in an error state that surfaces only as a 404.
    /// </remarks>
    [Function(nameof(ExportCuratedBackfill))]
    public async Task<IActionResult> ExportCuratedBackfill(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "export-curated")]
        HttpRequest request,
        CancellationToken ct)
    {
        // Read and parse the body here rather than through [FromBody]. The worker's binder
        // silently handed this method a null for a well-formed body, and a silently-ignored
        // parameter on a backfill endpoint is worse than a 400: the call returns 200 having
        // quietly done something other than what was asked.
        var body = await ReadBodyAsync(request, ct);

        var end = ParseDate(body?.Date) ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var days = Math.Clamp(body?.Days ?? 1, 1, MaxDays);

        var partitions = 0;
        var postings = 0;
        var pairs = 0;

        for (var offset = 0; offset < days; offset++)
        {
            var result = await exporter.ExportAsync(end.AddDays(-offset), ct);

            partitions += result.Partitions;
            postings += result.Postings;
            pairs += result.Pairs;
        }

        return new OkObjectResult(new
        {
            from = end.AddDays(-(days - 1)).ToString("yyyy-MM-dd"),
            to = end.ToString("yyyy-MM-dd"),
            partitions,
            postings,
            pairs,
        });
    }

    private static async Task<ExportRequest?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or 0)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<ExportRequest>(request.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static DateOnly? ParseDate(string? raw)
        => DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
