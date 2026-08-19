namespace JobPlatform.Core.Model;

/// <summary>Everything known about an ingest before the CSV is opened.</summary>
public sealed record ScrapeRunContext
{
    /// <summary>Blob path relative to the container, e.g. <c>jobs/software-engineer_2026-08-18T20-30-01Z.csv</c>.</summary>
    public required string BlobPath { get; init; }

    /// <summary>Search-term slug recovered from the blob name. Doubles as the Cosmos partition key.</summary>
    public required string SearchTerm { get; init; }

    /// <summary>When the scraper produced the file (from the blob name), not when we read it.</summary>
    public required DateTimeOffset ScrapedAtUtc { get; init; }

    public string? BlobETag { get; init; }
    public long BlobSizeBytes { get; init; }

    public DateOnly ScrapeDate => DateOnly.FromDateTime(ScrapedAtUtc.UtcDateTime);
}
