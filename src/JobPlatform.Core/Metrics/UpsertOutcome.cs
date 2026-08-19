namespace JobPlatform.Core.Metrics;

/// <summary>Result of reconciling a run's postings against what is already stored.</summary>
public readonly record struct UpsertOutcome(int New, int Updated, int Unchanged)
{
    public static UpsertOutcome Empty => new(0, 0, 0);
}
