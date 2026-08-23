namespace JobPlatform.Core.Enrichment;

/// <summary>Where an assertion came from, in descending order of trust.</summary>
/// <remarks>
/// Kept alongside the assertion rather than discarded because the three are not equally good
/// evidence. <see cref="Board"/> is the employer's own tagging; <see cref="Taxonomy"/> is a
/// string match against the description, which finds concepts mentioned in passing as readily
/// as ones the role requires; <see cref="Model"/> is a judgement. An analysis that cannot tell
/// them apart cannot say whether a spike in demand is real or a vocabulary change.
/// </remarks>
public enum AssertionSource
{
    /// <summary>Published as structured data by the board itself. freehire and Naukri.</summary>
    Board = 0,

    /// <summary>Matched out of the title or description by <see cref="ConceptGraph"/>.</summary>
    Taxonomy = 1,

    /// <summary>Extracted by the language model.</summary>
    Model = 2,
}

/// <summary>
/// How strongly the subject is bound to the concept.
/// </summary>
/// <remarks>
/// One enum spanning both sides of the match on purpose. A posting asserts demand
/// (<see cref="Mentioned"/>..<see cref="Required"/>); a CV will assert supply
/// (<see cref="Familiar"/>..<see cref="Expert"/>). Which half applies is determined by the
/// subject, so a single value type lets <c>PostingConcepts</c> and the eventual
/// <c>ProfileConcepts</c> share a column definition — and lets matching be a join between two
/// tables of identical shape rather than two pipelines to reconcile.
///
/// The values are ordinal within each half, so "at least preferred" is a comparison rather
/// than a set membership test. The gap between the halves is deliberate: it leaves room, and
/// it makes a demand value accidentally compared against a supply value obviously wrong
/// rather than subtly wrong.
/// </remarks>
public enum AssertionPolarity
{
    /// <summary>
    /// Nothing was said about strength. The honest answer for everything deterministic — a
    /// regex cannot tell "must have" from "nice to have", and only the model is asked to.
    /// </summary>
    Unspecified = 0,

    /// <summary>The text names it, with no indication of whether it matters.</summary>
    Mentioned = 1,

    /// <summary>Nice to have, desirable, bonus.</summary>
    Preferred = 2,

    /// <summary>Essential, must have, required.</summary>
    Required = 3,

    /// <summary>Supply side: exposure, some experience.</summary>
    Familiar = 11,

    /// <summary>Supply side: working competence.</summary>
    Proficient = 12,

    /// <summary>Supply side: deep or leading experience.</summary>
    Expert = 13,
}

/// <summary>
/// One subject bound to one concept. The shared shape of demand and supply.
/// </summary>
/// <remarks>
/// This interface is the CV contract, written down before there is a CV. Requirements come out
/// of a posting and qualifications will come out of a profile through the same vocabulary and
/// into the same column set, so the eventual match is a join rather than a translation layer.
/// Only the posting side is built; defining the shape once costs nothing now and a rewrite
/// later.
/// </remarks>
public interface IConceptAssertion
{
    /// <summary>The concept's stable key, not its label.</summary>
    string ConceptKey { get; }

    AssertionSource Source { get; }

    AssertionPolarity Polarity { get; }

    /// <summary>Years attached to this concept specifically, where the text gives them.</summary>
    int? YearsMin { get; }

    int? YearsMax { get; }

    /// <summary>
    /// The surface form actually found, kept verbatim.
    /// </summary>
    /// <remarks>
    /// Two jobs. It makes a match explainable — "your CV says k8s, the advert says
    /// Kubernetes" — which is the difference between a recommendation someone trusts and a
    /// number they do not. And it makes re-resolution possible: when the vocabulary improves,
    /// rows can be reconsidered without re-reading the description or re-scraping anything.
    /// </remarks>
    string? EvidenceText { get; }

    /// <summary>Null for anything deterministic; only the model produces a confidence.</summary>
    double? Confidence { get; }
}

/// <inheritdoc cref="IConceptAssertion" />
public sealed record ConceptAssertion(
    string ConceptKey,
    AssertionSource Source,
    AssertionPolarity Polarity = AssertionPolarity.Unspecified,
    int? YearsMin = null,
    int? YearsMax = null,
    string? EvidenceText = null,
    double? Confidence = null) : IConceptAssertion;
