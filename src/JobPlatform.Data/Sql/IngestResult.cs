using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Metrics;
using JobPlatform.Data.Sql.Entities;

namespace JobPlatform.Data.Sql;

/// <summary>What one ingest did.</summary>
/// <param name="Run">The run row, with its generated id.</param>
/// <param name="Outcome">The new/updated/unchanged split the metrics report.</param>
/// <param name="SourceKeysNeedingExtraction">
/// Postings whose text is new or has changed since the last run.
/// </param>
/// <remarks>
/// The third value exists so the caller can hand exactly those postings to the model pass. An
/// unchanged re-listing already has an extraction keyed on the same input hash, so enqueueing
/// it would produce a few hundred messages a day whose only effect is a database round trip
/// that decides to do nothing.
/// </remarks>
/// <param name="Enriched">
/// What the enricher concluded for each posting in this run.
/// </param>
/// <remarks>
/// Returned rather than discarded so the metrics can be computed from it. Enrichment is pure
/// and cheap, so recomputing it would be affordable - but it would also be a second place
/// that decides what a posting means, and those two places would eventually disagree.
/// </remarks>
public readonly record struct IngestResult(
    ScrapeRun Run,
    UpsertOutcome Outcome,
    IReadOnlyList<string> SourceKeysNeedingExtraction,
    IReadOnlyList<EnrichedPosting> Enriched);
