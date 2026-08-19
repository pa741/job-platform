namespace JobPlatform.Data.Sql.Entities;

/// <summary>One ingested blob. <see cref="BlobPath"/> is unique, which is what makes a
/// redelivered Event Grid event harmless.</summary>
public sealed class ScrapeRun
{
    public int Id { get; set; }

    public required string BlobPath { get; set; }
    public string? BlobETag { get; set; }
    public long BlobSizeBytes { get; set; }

    public required string SearchTerm { get; set; }

    /// <summary>When the scraper produced the file, taken from the blob name.</summary>
    public DateTimeOffset ScrapedAtUtc { get; set; }

    /// <summary>When we processed it.</summary>
    public DateTimeOffset IngestedAtUtc { get; set; }

    public DateOnly ScrapeDate { get; set; }

    public int RowCount { get; set; }
    public int ParsedCount { get; set; }
    public int InvalidCount { get; set; }
    public int NewCount { get; set; }
    public int UpdatedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int DurationMs { get; set; }
}
