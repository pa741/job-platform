namespace JobPlatform.Core.Matching;

/// <summary>
/// What we understand about a candidate, extracted from their CV. Everything is optional:
/// CVs are free text and a prototype must not reject one for lacking a "years" line.
/// </summary>
public sealed record CvProfile
{
    public required string RawText { get; init; }

    /// <summary>Normalised skill tokens, e.g. <c>["c#", "azure", "kubernetes"]</c>.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>Job titles the CV claims, most recent first where that is recoverable.</summary>
    public IReadOnlyList<string> Titles { get; init; } = [];

    public IReadOnlyList<string> Locations { get; init; } = [];

    public double? YearsExperience { get; init; }

    public bool? PrefersRemote { get; init; }

    /// <summary>Every distinct token in the CV. The retrieval floor scores against this.</summary>
    public IReadOnlyList<string> Tokens { get; init; } = [];
}

/// <summary>A posting offered to a ranker. Carries only what ranking needs.</summary>
public sealed record MatchCandidate
{
    public required long PostingId { get; init; }
    public required string Title { get; init; }
    public string? Company { get; init; }
    public string? Location { get; init; }
    public bool IsRemote { get; init; }
    public string? JobType { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public string? Currency { get; init; }

    /// <summary>Full posting text. Trimmed before it reaches a token-billed ranker.</summary>
    public string? Description { get; init; }
}

/// <summary>One ranked posting.</summary>
public sealed record PostingMatch
{
    public required long PostingId { get; init; }

    /// <summary>0-100. Comparable within one result set, not across requests.</summary>
    public required double Score { get; init; }

    /// <summary>Why this posting scored as it did. Empty from the keyword ranker, which
    /// has no basis for prose it could honestly write.</summary>
    public string? Rationale { get; init; }

    public IReadOnlyList<string> MatchedSkills { get; init; } = [];
    public IReadOnlyList<string> MissingSkills { get; init; } = [];
}

/// <summary>Which ranker actually produced a result, so the caller is never guessing.</summary>
public sealed record MatchProvenance(string Provider, int CandidatesConsidered, bool DegradedToFallback)
{
    public string? DegradationReason { get; init; }
}

public sealed record MatchOutcome(
    IReadOnlyList<PostingMatch> Matches,
    CvProfile Profile,
    MatchProvenance Provenance);
