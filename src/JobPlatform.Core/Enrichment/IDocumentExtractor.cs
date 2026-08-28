namespace JobPlatform.Core.Enrichment;

/// <summary>What kind of document is being read.</summary>
/// <remarks>
/// The reason this contract talks about documents rather than postings. Requirements come out
/// of an advert and qualifications come out of a profile through the same vocabulary and into
/// the same assertion shape, so pointing the extractor at a profile turned out to be a
/// prompt-template argument rather than a second component — which is exactly what writing it
/// this way round was for.
/// </remarks>
public enum DocumentKind
{
    /// <summary>A job advert. Demand: what an employer is asking for.</summary>
    Posting = 0,

    /// <summary>A candidate profile. Supply: what a candidate holds.</summary>
    Profile = 1,
}

/// <summary>One document handed to the model.</summary>
/// <param name="Kind">Which half of the match this is.</param>
/// <param name="Text">The body. The caller decides how much of it to send.</param>
/// <param name="Title">The advert title or the candidate's headline, where there is one.</param>
/// <param name="SourceId">
/// The row this document came from, carried only so a failure can say what it lost.
/// </param>
/// <remarks>
/// <see cref="SourceId"/> is optional and the extractor never reads it. It exists because a
/// dropped answer used to be reportable only as "one of ten failed", which is not something
/// anybody can act on - the AI call ledger names the rows instead. An id is safe to record where
/// the document itself is not: a profile's text is somebody's employment history, its id is an
/// integer.
/// </remarks>
public readonly record struct ExtractionRequest(
    DocumentKind Kind,
    string Text,
    string? Title = null,
    long? SourceId = null);

/// <summary>
/// What the model concluded, in the same vocabulary the deterministic pass uses.
/// </summary>
/// <remarks>
/// <see cref="PayloadJson"/> is kept whole alongside the parsed fields. Re-deriving a column
/// then never means re-calling the model, which is the difference between a schema change
/// costing nothing and costing a full re-extraction of the corpus.
/// </remarks>
public sealed record DocumentExtraction
{
    /// <summary>
    /// Bumped when the prompt or the parsing changes what the same input would produce.
    /// Rows below the current value are stale and eligible for a backfill pass.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Concepts the model found, with the strength it read them at.
    /// </summary>
    /// <remarks>
    /// Every one carries <see cref="AssertionSource.Model"/>, so an analysis can weigh them
    /// against board-supplied and text-matched evidence rather than treating all three as the
    /// same fact. This is the only source that can populate
    /// <see cref="AssertionPolarity"/> — a regex cannot tell "essential" from "desirable".
    /// </remarks>
    public IReadOnlyList<ConceptAssertion> Concepts { get; init; } = [];

    /// <summary>
    /// Technologies the model saw that the vocabulary has no concept for.
    /// </summary>
    /// <remarks>
    /// The model is asked for these explicitly rather than being allowed to invent keys. An
    /// invented key would look exactly like a real one in the data and would quietly split a
    /// concept in two; a mention is honest and feeds the vocabulary's growth loop.
    /// </remarks>
    public IReadOnlyList<UnresolvedMention> Mentions { get; init; } = [];

    /// <summary>Where the title was uninformative and the body was not.</summary>
    public Seniority? Seniority { get; init; }

    public WorkArrangement? WorkArrangement { get; init; }

    public int? HybridDaysInOffice { get; init; }

    public decimal? AnnualSalaryMin { get; init; }
    public decimal? AnnualSalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }

    /// <summary>How sure the model was about the salary it read. Null if it read none.</summary>
    public double? SalaryConfidence { get; init; }

    /// <summary>The model id, so a change of model is visible in the data rather than inferred.</summary>
    public string? Model { get; init; }

    /// <summary>The response body, kept verbatim.</summary>
    public string? PayloadJson { get; init; }

    public int Version { get; init; } = CurrentVersion;
}

/// <summary>
/// Reads a document for the things a regex genuinely cannot.
/// </summary>
/// <remarks>
/// Implemented in <c>JobPlatform.Ai</c> and registered <b>only</b> where a Kernel is, so a
/// deployment with no provider configured resolves this as null and skips the step rather
/// than throwing. That is the shape the whole AI layer already follows: a missing environment
/// variable must not take down endpoints that have nothing to do with AI.
///
/// Consumers therefore take <c>IDocumentExtractor?</c>, never <c>IDocumentExtractor</c>.
/// </remarks>
public interface IDocumentExtractor
{
    /// <summary>Null when the model returned nothing usable. Never throws for a bad response.</summary>
    Task<DocumentExtraction?> ExtractAsync(ExtractionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Several documents in one pass. The result is positional: index <c>i</c> answers
    /// <c>requests[i]</c>, and any element may be null exactly as
    /// <see cref="ExtractAsync"/> may return null.
    /// </summary>
    /// <remarks>
    /// This exists because of what has to precede every extraction: the whole concept
    /// vocabulary, several thousand tokens of it, as the model's allowed output set. Sent once
    /// per document, it is most of what each call costs — the advert itself is the small half.
    /// Packing ten documents into one call pays for the vocabulary once instead of ten times,
    /// and that ratio, not the per-token price, is what makes a corpus-wide pass affordable.
    ///
    /// A default implementation loops, so an extractor that has no cheaper way to do this is
    /// still correct and no caller has to branch on which kind it has. The positional contract
    /// is the part implementations must honour: a batch that comes back short, reordered, or
    /// with an index attached to the wrong document silently writes one posting's requirements
    /// onto another, which is worse than returning nothing. <see cref="ExtractionRequest"/>
    /// carries no identity for exactly that reason — correlating on position is checkable,
    /// correlating on a model-supplied id is not.
    /// </remarks>
    async Task<IReadOnlyList<DocumentExtraction?>> ExtractBatchAsync(
        IReadOnlyList<ExtractionRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var results = new DocumentExtraction?[requests.Count];

        for (var i = 0; i < requests.Count; i++)
        {
            results[i] = await ExtractAsync(requests[i], ct).ConfigureAwait(false);
        }

        return results;
    }
}
