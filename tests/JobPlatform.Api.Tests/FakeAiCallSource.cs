using JobPlatform.Core.Ai;

namespace JobPlatform.Api.Tests;

/// <summary>
/// In-memory stand-in for the Cosmos AI call ledger.
/// </summary>
/// <remarks>
/// Holds the real <see cref="AiCallRecord"/>, so a change to what the ingest writes still breaks
/// the endpoint tests rather than passing against a parallel shape.
/// </remarks>
public sealed class FakeAiCallSource : IAiCallSource
{
    public List<AiCallRecord> Records { get; } = [];

    public Task<IReadOnlyList<AiCallRecord>> ListAsync(
        int days, bool failuresOnly, int limit, CancellationToken ct = default)
    {
        IReadOnlyList<AiCallRecord> results =
        [
            .. Records
                .Where(r => !failuresOnly || r.Outcome != AiCallOutcome.Succeeded)
                .OrderByDescending(r => r.OccurredAtUtc)
                .Take(limit),
        ];

        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<AiCallTotals>> SummariseAsync(int days, CancellationToken ct = default)
    {
        IReadOnlyList<AiCallTotals> results =
        [
            .. Records
                .GroupBy(r => r.Operation, StringComparer.Ordinal)
                .Select(g => new AiCallTotals(
                    g.Key,
                    g.Count(),
                    g.Count(r => r.Outcome != AiCallOutcome.Succeeded),
                    g.Sum(r => r.Requested),
                    g.Sum(r => r.Returned))),
        ];

        return Task.FromResult(results);
    }
}
