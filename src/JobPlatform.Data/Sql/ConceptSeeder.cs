using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>What a seed run changed.</summary>
public readonly record struct ConceptSeedResult(
    int Version,
    int ConceptsAdded,
    int ConceptsUpdated,
    int ConceptsDeactivated,
    int Labels,
    int Relations,
    int ClosureRows);

/// <summary>
/// Projects the embedded vocabulary into the concept tables.
/// </summary>
/// <remarks>
/// The vocabulary's source of truth is <c>concepts.json</c>; these tables are a copy that
/// exists so an analysis query can join to it. This is the only writer, and it is idempotent:
/// running it twice changes nothing the second time.
///
/// <b>Concepts are matched on <c>ConceptKey</c> and never deleted.</b> A key that disappears
/// from the file is marked inactive instead, because postings already reference it and
/// deleting the row would either fail on the foreign key or take real evidence with it. That
/// is the whole reason the key is opaque and stable rather than being the display label.
///
/// Labels, relations and the closure <i>are</i> replaced wholesale. They carry no references
/// from anywhere else, they are small, and rebuilding them is the only way to be sure the
/// closure agrees with the relations it is derived from.
/// </remarks>
public static class ConceptSeeder
{
    public static async Task<ConceptSeedResult> SeedAsync(
        JobsDbContext db,
        ConceptGraph? graph = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        graph ??= ConceptGraph.Default;

        var existing = await db.Concepts.ToDictionaryAsync(c => c.ConceptKey, StringComparer.Ordinal, ct);

        int addedCount = 0, updatedCount = 0;

        foreach (var concept in graph.Concepts)
        {
            if (existing.TryGetValue(concept.Key, out var row))
            {
                if (row.PrefLabel != concept.Label || row.Kind != concept.Kind || !row.IsActive)
                {
                    updatedCount++;
                }

                row.PrefLabel = concept.Label;
                row.Kind = concept.Kind;
                row.IsActive = true;
                row.TaxonomyVersion = graph.Version;
            }
            else
            {
                row = new ConceptEntity
                {
                    ConceptKey = concept.Key,
                    Kind = concept.Kind,
                    PrefLabel = concept.Label,
                    IsActive = true,
                    TaxonomyVersion = graph.Version,
                };

                db.Concepts.Add(row);
                existing[concept.Key] = row;
                addedCount++;
            }
        }

        var live = graph.Concepts.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var deactivated = 0;

        foreach (var (key, row) in existing)
        {
            if (!live.Contains(key) && row.IsActive)
            {
                // Kept, not deleted. Postings reference it and that evidence is still true.
                row.IsActive = false;
                deactivated++;
            }
        }

        // Ids are needed before the label, relation and closure rows can reference them.
        await db.SaveChangesAsync(ct);

        var idByKey = existing.ToDictionary(e => e.Key, e => e.Value.Id, StringComparer.Ordinal);

        await db.ConceptClosure.ExecuteDeleteAsync(ct);
        await db.ConceptRelations.ExecuteDeleteAsync(ct);
        await db.ConceptLabels.ExecuteDeleteAsync(ct);

        var labels = BuildLabels(graph, idByKey);
        var relations = BuildRelations(graph, idByKey);
        var closure = BuildClosure(graph, idByKey);

        db.ConceptLabels.AddRange(labels);
        db.ConceptRelations.AddRange(relations);
        db.ConceptClosure.AddRange(closure);

        await db.SaveChangesAsync(ct);

        return new ConceptSeedResult(
            graph.Version,
            addedCount,
            updatedCount,
            deactivated,
            labels.Count,
            relations.Count,
            closure.Count);
    }

    /// <summary>
    /// Whether the database already holds this version of the vocabulary.
    /// </summary>
    /// <remarks>
    /// Cheap enough to call on startup. The failure it prevents is quiet: code shipping a
    /// vocabulary the database has not been reseeded with produces no error, just assertions
    /// that stop appearing and counts that are silently low.
    /// </remarks>
    public static async Task<bool> IsCurrentAsync(
        JobsDbContext db,
        ConceptGraph? graph = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        graph ??= ConceptGraph.Default;

        var stored = await db.Concepts.CountAsync(c => c.TaxonomyVersion == graph.Version, ct);

        return stored == graph.Concepts.Count;
    }

    private static List<ConceptLabelEntity> BuildLabels(
        ConceptGraph graph,
        Dictionary<string, int> idByKey)
    {
        var labels = new List<ConceptLabelEntity>();
        var seen = new HashSet<(int, string)>();

        void Add(int conceptId, string label, ConceptLabelKind kind)
        {
            var normalized = Normalize(label);

            if (seen.Add((conceptId, normalized)))
            {
                labels.Add(new ConceptLabelEntity
                {
                    ConceptId = conceptId,
                    NormalizedLabel = normalized,
                    Label = label,
                    Kind = kind,
                });
            }
        }

        foreach (var concept in graph.Concepts)
        {
            var id = idByKey[concept.Key];
            Add(id, concept.Label, ConceptLabelKind.Preferred);

            foreach (var (form, kind) in graph.LabelsOf(concept.Key))
            {
                Add(id, form, kind);
            }
        }

        return labels;
    }

    private static List<ConceptRelationEntity> BuildRelations(
        ConceptGraph graph,
        Dictionary<string, int> idByKey)
        =>
        [
            .. graph.Relations()
                .Where(e => idByKey.ContainsKey(e.FromKey) && idByKey.ContainsKey(e.ToKey))
                .Select(e => new ConceptRelationEntity
                {
                    FromConceptId = idByKey[e.FromKey],
                    ToConceptId = idByKey[e.ToKey],
                    RelationType = e.Type,
                })
        ];

    private static List<ConceptClosureEntity> BuildClosure(
        ConceptGraph graph,
        Dictionary<string, int> idByKey)
        =>
        [
            .. graph.Closure()
                .Where(e => idByKey.ContainsKey(e.AncestorKey) && idByKey.ContainsKey(e.DescendantKey))
                .Select(e => new ConceptClosureEntity
                {
                    AncestorId = idByKey[e.AncestorKey],
                    DescendantId = idByKey[e.DescendantKey],
                    Depth = e.Depth,
                })
        ];

    /// <summary>Must fold identically to the resolver, or a lookup here misses what it matched.</summary>
    private static string Normalize(string value) => ConceptGraph.NormalizeLabel(value);
}
