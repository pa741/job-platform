using JobPlatform.Core.Metrics;
using JobPlatform.Data.Cosmos;

namespace JobPlatform.Api.Tests;

/// <summary>
/// In-memory stand-in for the Cosmos metrics reader. Documents are the real record types, so
/// a contract change in the ingestion side still breaks these tests.
/// </summary>
public sealed class FakeMetricsSource : IMetricsSource
{
    public List<RunDigest> Digests { get; } = [];
    public List<DailyRollup> Rollups { get; } = [];

    public Task<RunDigest?> GetLatestRunDigestAsync(string searchTerm, CancellationToken ct = default)
        => Task.FromResult(Digests
            .Where(d => d.SearchTerm == searchTerm)
            .OrderByDescending(d => d.ScrapedAtUtc)
            .FirstOrDefault());

    public Task<IReadOnlyList<RunDigest>> ListRunDigestsAsync(
        string searchTerm, DateTimeOffset? from, DateTimeOffset? to, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RunDigest>>(Digests
            .Where(d => d.SearchTerm == searchTerm)
            .Where(d => from is null || d.ScrapedAtUtc >= from)
            .Where(d => to is null || d.ScrapedAtUtc <= to)
            .OrderByDescending(d => d.ScrapedAtUtc)
            .Take(take)
            .ToList());

    public Task<IReadOnlyList<DailyRollup>> ListDailyRollupsAsync(
        string searchTerm, DateOnly? from, DateOnly? to, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DailyRollup>>(Rollups
            .Where(r => r.SearchTerm == searchTerm)
            .Where(r => from is null || string.CompareOrdinal(r.Date, from.Value.ToString("yyyy-MM-dd")) >= 0)
            .Where(r => to is null || string.CompareOrdinal(r.Date, to.Value.ToString("yyyy-MM-dd")) <= 0)
            .OrderBy(r => r.Date, StringComparer.Ordinal)
            .ToList());

    public Task<DailyRollup?> GetDailyRollupAsync(
        string searchTerm, DateOnly date, CancellationToken ct = default)
        => Task.FromResult(Rollups.FirstOrDefault(r =>
            r.SearchTerm == searchTerm && r.Date == date.ToString("yyyy-MM-dd")));

    public Task<IReadOnlyList<string>> ListSearchTermsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Digests
            .Select(d => d.SearchTerm)
            .Concat(Rollups.Select(r => r.SearchTerm))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList());
}
