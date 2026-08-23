using JobPlatform.Core.Enrichment;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One posting bound to one concept — what the advert asks for.
/// </summary>
/// <remarks>
/// <b>This shape is the CV contract.</b> When qualifications start coming out of a profile they
/// land in a <c>ProfileConcepts</c> table with these same columns, differing only in which half
/// of <see cref="AssertionPolarity"/> is meaningful. Matching is then a join between two tables
/// of identical shape rather than two pipelines to reconcile. Only the posting side exists
/// today; the shape was fixed now because retrofitting it later is the expensive version.
///
/// The primary key includes <see cref="Source"/>, so a concept the employer tagged <i>and</i>
/// the description mentioned produces two rows. They are not equally good evidence and an
/// analysis that cannot tell them apart cannot say whether a spike in demand is real or a
/// vocabulary change. Queries that do not care can group them away; the distinction cannot be
/// recovered once it is collapsed.
/// </remarks>
public sealed class PostingConceptEntity
{
    public long PostingId { get; set; }
    public JobPostingEntity? Posting { get; set; }

    public int ConceptId { get; set; }
    public ConceptEntity? Concept { get; set; }

    public AssertionSource Source { get; set; }

    /// <summary>
    /// Unspecified for everything deterministic. A regex cannot tell "must have" from "would
    /// be nice", and only the model pass is asked to.
    /// </summary>
    public AssertionPolarity Polarity { get; set; }

    /// <summary>Years attached to this concept specifically, not to the role overall.</summary>
    public int? YearsMin { get; set; }
    public int? YearsMax { get; set; }

    /// <summary>
    /// The surface form actually found, verbatim.
    /// </summary>
    /// <remarks>
    /// Two jobs. It makes a match explainable — "your CV says k8s, the advert says Kubernetes"
    /// — which is the difference between a recommendation someone trusts and a number they do
    /// not. And it makes re-resolution possible: when the vocabulary improves, rows can be
    /// reconsidered without re-reading the description or re-scraping anything.
    /// </remarks>
    public string? EvidenceText { get; set; }

    /// <summary>Null for anything deterministic; only the model produces a confidence.</summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// Which resolver wrote this. Rows below the current value are stale and can be recomputed
    /// from the stored description without touching the scraper.
    /// </summary>
    public int ResolverVersion { get; set; }
}

/// <summary>
/// A surface form that was seen and deliberately not turned into an assertion.
/// </summary>
/// <remarks>
/// The honest half of the resolver. The vocabulary this design replaces handled ambiguous
/// names by refusing to match them at all, so every mention of Go, R, C and Julia was
/// discarded leaving no trace — the data was wrong and there was no way to find out by how
/// much. This separates "nobody asked for this" from "we could not tell", which are very
/// different answers to the same query.
///
/// It is also the vocabulary's growth loop. The most frequent unresolved forms each month are
/// exactly the concepts worth adding next, derived from the corpus rather than guessed at.
/// </remarks>
public sealed class PostingMentionEntity
{
    public long PostingId { get; set; }
    public JobPostingEntity? Posting { get; set; }

    /// <summary>Verbatim, as the source wrote it.</summary>
    public required string SurfaceForm { get; set; }

    public MentionReason Reason { get; set; }

    public int Occurrences { get; set; }

    public int ResolverVersion { get; set; }
}

/// <summary>One of a posting's normalised job types. The multi-valued column as rows.</summary>
/// <remarks>
/// <c>job_type</c> arrives as <c>"parttime, fulltime"</c> — one string holding two facts, which
/// forces every query against it to be a <c>LIKE</c> and makes equality miss exactly the
/// multi-valued rows the parser was careful to keep.
/// </remarks>
public sealed class JobPostingJobTypeEntity
{
    public long PostingId { get; set; }
    public JobPostingEntity? Posting { get; set; }

    public required string JobType { get; set; }
}

/// <summary>A sparse fact about a posting — the long tail that does not deserve a column.</summary>
public sealed class PostingTagEntity
{
    public long PostingId { get; set; }
    public JobPostingEntity? Posting { get; set; }

    public required string Tag { get; set; }

    /// <summary>Null where the tag is a bare flag and its presence is the fact.</summary>
    public string? Value { get; set; }
}

/// <summary>
/// The raw output of one model extraction, kept whole.
/// </summary>
/// <remarks>
/// Storing the payload means re-deriving a column never means re-calling the model, which is
/// the difference between a schema change costing nothing and costing a full re-extraction of
/// the corpus.
///
/// <see cref="InputHash"/> plus <see cref="ExtractorVersion"/> is the idempotency key — the
/// same contract as <c>ScrapeRuns.BlobPath</c>. A posting re-listed across runs with unchanged
/// text is extracted once, and a replayed queue message converges instead of duplicating.
/// </remarks>
public sealed class PostingExtractionEntity
{
    public long Id { get; set; }

    public long PostingId { get; set; }
    public JobPostingEntity? Posting { get; set; }

    public int ExtractorVersion { get; set; }

    /// <summary>Hash of the text that was sent, so unchanged text is not re-extracted.</summary>
    public required string InputHash { get; set; }

    /// <summary>The model id, so a change of model is visible in the data.</summary>
    public string? Model { get; set; }

    public DateTimeOffset ExtractedAtUtc { get; set; }

    public string? PayloadJson { get; set; }
}
