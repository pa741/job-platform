using JobPlatform.Core.Enrichment;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One node of the vocabulary, projected into SQL so analysis queries can join to it.
/// </summary>
/// <remarks>
/// A <b>projection</b>, not the source of truth. The vocabulary lives in
/// <c>concepts.json</c> as an embedded resource, because the resolver runs inside the ingest
/// function where a round trip to read labels would hold a SQL connection open for no reason.
/// These rows are reseeded from that file whenever its version moves; nothing else writes them.
/// </remarks>
public sealed class ConceptEntity
{
    public int Id { get; set; }

    /// <summary>
    /// The stable, opaque identity — <c>skill.kubernetes</c>. Unique.
    /// </summary>
    /// <remarks>
    /// The surrogate <see cref="Id"/> exists only to keep the bridge tables narrow; the key is
    /// what has meaning, and what a backfill or an export is written against. Renaming
    /// <see cref="PrefLabel"/> is an edit; renaming this is a data migration.
    /// </remarks>
    public required string ConceptKey { get; set; }

    public ConceptKind Kind { get; set; }

    public required string PrefLabel { get; set; }

    /// <summary>False once a concept is superseded; rows referencing it are left alone.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Which vocabulary version last wrote this row.</summary>
    public int TaxonomyVersion { get; set; }

    public ICollection<ConceptLabelEntity> Labels { get; set; } = [];
}

/// <summary>
/// A surface form that names a concept, or that names one but cannot be trusted to.
/// </summary>
/// <remarks>
/// Stored so a query can go from a raw string back to a concept without loading the resource,
/// and so <c>Ambiguous</c> forms are visible to anyone reading the schema rather than being an
/// implementation detail of the matcher.
/// </remarks>
public sealed class ConceptLabelEntity
{
    public int Id { get; set; }

    public int ConceptId { get; set; }
    public ConceptEntity? Concept { get; set; }

    /// <summary>Case and punctuation folded, so lookups agree with the matcher.</summary>
    public required string NormalizedLabel { get; set; }

    /// <summary>As written in the vocabulary.</summary>
    public required string Label { get; set; }

    public ConceptLabelKind Kind { get; set; }
}

/// <summary>One typed edge of the DAG.</summary>
public sealed class ConceptRelationEntity
{
    public int FromConceptId { get; set; }
    public ConceptEntity? FromConcept { get; set; }

    public int ToConceptId { get; set; }
    public ConceptEntity? ToConcept { get; set; }

    public ConceptRelationType RelationType { get; set; }
}

/// <summary>
/// The transitive <c>Broader</c> closure, materialised.
/// </summary>
/// <remarks>
/// This table is what makes a rollup a join. Without it, "how many postings want a backend
/// skill" is a recursive CTE per query; with it, it is an ordinary indexed join against a few
/// hundred rows. The depth-0 self rows are deliberate — they let one query shape count both
/// "postings wanting C#" and "postings wanting any backend skill" instead of a union of two.
///
/// Recomputed wholesale when the vocabulary version moves, never incrementally. At this size
/// there is nothing to gain from being clever and a real risk of drifting out of step with the
/// relations it is derived from.
/// </remarks>
public sealed class ConceptClosureEntity
{
    public int AncestorId { get; set; }
    public ConceptEntity? Ancestor { get; set; }

    public int DescendantId { get; set; }
    public ConceptEntity? Descendant { get; set; }

    /// <summary>Hops. 0 is the concept itself; where several paths exist, the shortest.</summary>
    public int Depth { get; set; }
}
