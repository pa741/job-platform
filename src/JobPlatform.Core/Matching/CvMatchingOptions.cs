namespace JobPlatform.Core.Matching;

public sealed class CvMatchingOptions
{
    public const string SectionName = "Matching";

    /// <summary>Which <see cref="ICvRanker"/> to resolve: <c>keyword</c> or <c>anthropic</c>.</summary>
    public string Provider { get; set; } = "keyword";

    /// <summary>How many postings to pull from SQL before ranking narrows them.</summary>
    public int RetrievalLimit { get; set; } = 400;

    /// <summary>
    /// How many survive the keyword prefilter and reach the configured ranker. This is the
    /// cost dial for a token-billed provider: every candidate is prompt tokens.
    /// </summary>
    public int RerankLimit { get; set; } = 40;

    /// <summary>Default result size when the caller does not ask for one.</summary>
    public int DefaultTopN { get; set; } = 10;

    public int MaxTopN { get; set; } = 50;

    /// <summary>
    /// Description characters sent per candidate. Descriptions run to several KB and the
    /// signal is overwhelmingly at the top; sending all of it multiplies cost for very
    /// little ranking benefit.
    /// </summary>
    public int DescriptionCharacterBudget { get; set; } = 1500;

    /// <summary>Largest CV accepted, in characters.</summary>
    public int MaxCvCharacters { get; set; } = 40_000;
}
