namespace JobPlatform.Core.Enrichment;

/// <summary>Why a surface form was seen but not turned into an assertion.</summary>
public enum MentionReason
{
    /// <summary>
    /// The form names a known concept but cannot be trusted to mean it. "Go", "R", "C" and
    /// "Julia" are ordinary words or names, and a false spike in demand for Go is worse than
    /// undercounting it.
    /// </summary>
    Ambiguous = 0,

    /// <summary>
    /// A board published this as a structured skill and the vocabulary has no concept for it.
    /// freehire's <c>tagsAndSkills</c> is free text an employer typed, so this is where new
    /// vocabulary comes from.
    /// </summary>
    UnknownBoardSkill = 1,

    /// <summary>The model flagged it as a technology and the vocabulary does not know it.</summary>
    UnknownModelSkill = 2,
}

/// <summary>
/// A surface form that was seen and deliberately not resolved.
/// </summary>
/// <remarks>
/// The honest half of the resolver, and the reason it exists. The vocabulary this replaces
/// handled ambiguous names by refusing to match them at all, which meant every mention of Go,
/// R, C and Julia was discarded leaving no trace — the data was wrong and there was no way to
/// find out by how much. Recording the mention separates "nobody asked for this" from "we
/// could not tell", which are very different answers to the same query.
///
/// It also closes the loop on vocabulary growth: the most frequent unresolved forms each month
/// are precisely the list of concepts worth adding next, derived from the corpus rather than
/// guessed at.
/// </remarks>
/// <param name="SurfaceForm">Verbatim, as the source wrote it.</param>
/// <param name="Reason">Ambiguous, or unknown to the vocabulary.</param>
/// <param name="Occurrences">How many times it appeared in this document.</param>
public readonly record struct UnresolvedMention(
    string SurfaceForm,
    MentionReason Reason,
    int Occurrences = 1);
