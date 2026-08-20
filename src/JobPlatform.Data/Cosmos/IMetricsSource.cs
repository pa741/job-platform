using JobPlatform.Core.Metrics;

namespace JobPlatform.Data.Cosmos;

/// <summary>
/// Read access to the metric documents, as an abstraction over Cosmos.
/// </summary>
/// <remarks>
/// Exists so the metrics endpoints can be tested without a Cosmos emulator. That is worth an
/// interface here specifically: the rest of this repository's suite runs with no Azure
/// account and no credentials - which is what lets a fresh clone or a fork verify the project
/// end to end - and the emulator would be the first dependency to break that. The SQL side
/// needs no equivalent because SQLite already provides a real relational engine to translate
/// against.
/// </remarks>
public interface IMetricsSource
{
    Task<RunDigest?> GetLatestRunDigestAsync(string searchTerm, CancellationToken ct = default);

    Task<IReadOnlyList<RunDigest>> ListRunDigestsAsync(
        string searchTerm, DateTimeOffset? from, DateTimeOffset? to, int take, CancellationToken ct = default);

    Task<IReadOnlyList<DailyRollup>> ListDailyRollupsAsync(
        string searchTerm, DateOnly? from, DateOnly? to, CancellationToken ct = default);

    Task<DailyRollup?> GetDailyRollupAsync(string searchTerm, DateOnly date, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListSearchTermsAsync(CancellationToken ct = default);
}
