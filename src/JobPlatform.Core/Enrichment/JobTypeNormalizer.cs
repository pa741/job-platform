namespace JobPlatform.Core.Enrichment;

/// <summary>
/// Splits the multi-valued <c>job_type</c> column into a normalised set.
/// </summary>
/// <remarks>
/// The column arrives as <c>"parttime, fulltime"</c> — one string holding two facts. Every
/// query against it today is a <c>LIKE</c>, because equality misses exactly the multi-valued
/// rows the parser was careful to keep. A set turns that into a join, and lets a posting be
/// counted under both of the things it actually is rather than neither.
///
/// The vocabulary is closed on purpose. Boards spell the same six ideas a dozen ways
/// (<c>fulltime</c>, <c>full-time</c>, <c>Full Time</c>, <c>permanent</c>), and an open
/// vocabulary would make the breakdown a list of spellings rather than a list of job types.
/// Anything unrecognised is dropped rather than passed through — a stray value in a facet is
/// worse than a slightly short list, because it looks like a finding.
/// </remarks>
public static class JobTypeNormalizer
{
    public const string FullTime = "fulltime";
    public const string PartTime = "parttime";
    public const string Contract = "contract";
    public const string Temporary = "temporary";
    public const string Internship = "internship";
    public const string Volunteer = "volunteer";

    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fulltime"] = FullTime,
        ["full time"] = FullTime,
        ["full-time"] = FullTime,
        ["permanent"] = FullTime,
        ["perm"] = FullTime,

        ["parttime"] = PartTime,
        ["part time"] = PartTime,
        ["part-time"] = PartTime,

        ["contract"] = Contract,
        ["contractor"] = Contract,
        ["contracttoperm"] = Contract,
        ["contract to hire"] = Contract,
        ["freelance"] = Contract,
        ["fixed term"] = Contract,
        ["fixed-term"] = Contract,

        ["temporary"] = Temporary,
        ["temp"] = Temporary,
        ["seasonal"] = Temporary,

        ["internship"] = Internship,
        ["intern"] = Internship,
        ["apprenticeship"] = Internship,
        ["graduate"] = Internship,
        ["placement"] = Internship,

        ["volunteer"] = Volunteer,
        ["voluntary"] = Volunteer,
    };

    /// <summary>
    /// The distinct job types the column names, in a fixed order.
    /// </summary>
    /// <remarks>
    /// Ordered by the canonical vocabulary rather than by appearance, so <c>"parttime,
    /// fulltime"</c> and <c>"fulltime, parttime"</c> produce the same list. Two rows that say
    /// the same thing must hash the same, or every re-ingest looks like a change.
    /// </remarks>
    public static IReadOnlyList<string> Normalize(string? jobType)
    {
        if (string.IsNullOrWhiteSpace(jobType))
        {
            return [];
        }

        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var part in jobType.Split([',', ';', '|', '/'], StringSplitOptions.RemoveEmptyEntries
                                                                 | StringSplitOptions.TrimEntries))
        {
            if (Canonical.TryGetValue(part, out var canonical))
            {
                found.Add(canonical);
            }
        }

        return found.Count == 0 ? [] : [.. Order.Where(found.Contains)];
    }

    private static readonly string[] Order =
        [FullTime, PartTime, Contract, Temporary, Internship, Volunteer];
}
