namespace JobPlatform.Core.Enrichment;

/// <summary>What sort of node this is.</summary>
public enum ConceptKind
{
    /// <summary>
    /// A grouping, never matched in text. "Backend Development" is reached by walking the
    /// closure up from something concrete, not by finding the phrase in an advert - adverts
    /// do not describe themselves that way, and matching the phrase would count the handful
    /// that happen to use it as though they were the whole population.
    /// </summary>
    Domain = 0,

    /// <summary>Something an employer asks for and a candidate can hold.</summary>
    Skill = 1,

    /// <summary>A credential: certification, security clearance, or academic qualification.</summary>
    Qualification = 2,
}

/// <summary>What a surface form is good for.</summary>
public enum ConceptLabelKind
{
    /// <summary>The concept preferred name.</summary>
    Preferred = 0,

    /// <summary>A synonym or alternative spelling that resolves cleanly.</summary>
    Alternate = 1,

    /// <summary>
    /// Names the concept but cannot be trusted to mean it - "Go", "R", "C", "Julia". A match
    /// on one of these is recorded as an unresolved mention, never as an assertion.
    /// </summary>
    Ambiguous = 2,
}

/// <summary>How two concepts relate.</summary>
/// <remarks>
/// Only <see cref="Broader"/> feeds the closure. The rest are queried directly, because they
/// are not transitive in a way that survives chaining: React is related to Vue and Vue to
/// Angular, but walking that transitively relates everything to everything.
/// </remarks>
public enum ConceptRelationType
{
    /// <summary>
    /// Is-a. A <b>DAG, not a tree</b> - a concept may have several parents, and the data
    /// needs it: Python is a language, and is used in backend, data and ML. The flat
    /// <c>category</c> field this replaces forced one answer and was wrong three ways.
    /// </summary>
    Broader = 0,

    /// <summary>
    /// Naming the source almost certainly means wanting the target too - Kubernetes implies
    /// containerisation, Spring Boot implies Java.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> expanded into assertions at resolve time. An assertion records
    /// what a posting said; an implication is what we would conclude from it. Materialising
    /// them would mean "demand for containerisation" counted adverts that never mentioned it,
    /// with nothing left to distinguish the two. The edge is stored so a query can choose to
    /// follow it; the choice belongs to the query.
    /// </remarks>
    Implies = 1,

    /// <summary>A spelling or packaging of the same underlying thing.</summary>
    VariantOf = 2,

    /// <summary>
    /// The target replaced the source. AngularJS and Angular are different frameworks, not
    /// two spellings - folding them, as the flat vocabulary did, overstates demand for the
    /// current one and hides a real migration signal.
    /// </summary>
    SucceededBy = 3,

    /// <summary>
    /// Competing or commonly substituted. The substrate for "roles like this one": two
    /// postings wanting React and Vue respectively are more alike than the raw skill sets say.
    /// </summary>
    Related = 4,
}

/// <summary>One node of the vocabulary.</summary>
/// <param name="Key">
/// The identity. Stable, opaque, and never displayed - <c>skill.kubernetes</c>. Renaming
/// <see cref="Label"/> is an edit; renaming a key is a data migration, which is exactly the
/// distinction the previous design lacked when the canonical name *was* the key.
/// </param>
/// <param name="Kind">Domain, skill or qualification.</param>
/// <param name="Label">The preferred human-readable name.</param>
/// <param name="Broader">Parent keys. Several is normal.</param>
/// <param name="Implies">Keys this one strongly suggests.</param>
/// <param name="Related">Competing or substitutable keys.</param>
/// <param name="SucceededBy">The key that replaced this one, if any.</param>
public sealed record Concept(
    string Key,
    ConceptKind Kind,
    string Label,
    IReadOnlyList<string> Broader,
    IReadOnlyList<string> Implies,
    IReadOnlyList<string> Related,
    string? SucceededBy);
