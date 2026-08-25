using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Concepts;

/// <summary>
/// The vocabulary, and where the corpus's knowledge comes from.
/// </summary>
/// <remarks>
/// <b>The concept graph decides everything and had no view at all.</b> It is what a posting is
/// understood to ask for, what a profile is understood to hold, and therefore every match and
/// every rollup - and until now the only way to look at it was to read <c>concepts.json</c> in
/// the repository.
///
/// <see cref="ListAsync"/> and the edge half of <see cref="GetAsync"/> read the graph shipped
/// in the build and touch no database whatsoever. Only the demand counts do, and they are
/// bounded to one neighbourhood.
///
/// <see cref="SourceCompositionAsync"/> is the exception and is deliberately fenced: it is a
/// SQL aggregate, allowed on the same terms <c>/postings/facets</c> is - one round trip, cached
/// hard, changing once a day when the ingest runs, and never on a bootstrap or polling path.
/// </remarks>
public sealed class ConceptEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/concepts")
            .WithTags("Concepts")
            .RequireAuthorization(AuthSetup.PublicReadPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListConcepts")
            .WithSummary("The whole vocabulary. Served from the build, never the database.")
            .CacheOutput(CacheSetup.FacetsPolicy);

        group.MapGet("/source-composition", SourceCompositionAsync)
            .WithName("GetSourceComposition")
            .WithSummary("Where the corpus's assertions come from, by pass and by strength.")
            .CacheOutput(CacheSetup.FacetsPolicy);

        // After the literal route, deliberately: a catch-all `{key}` registered first would
        // swallow `/source-composition` and answer 404 for a concept nobody asked for.
        group.MapGet("/{key}", GetAsync)
            .WithName("GetConcept")
            .WithSummary("One concept, its neighbourhood, and how much of the corpus wants it.")
            .CacheOutput(CacheSetup.FacetsPolicy);
    }

    /// <summary>
    /// The whole vocabulary, from the embedded graph.
    /// </summary>
    /// <remarks>
    /// No database at all, which is why this one is safe to load on page open. 222 entries is a
    /// few kilobytes and it changes only when the build does.
    /// </remarks>
    private static IResult ListAsync()
    {
        var graph = ConceptGraph.Default;

        return TypedResults.Ok(new
        {
            version = graph.Version,
            items = graph.Concepts
                .OrderBy(c => c.Kind)
                .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ConceptListItem(c.Key, c.Label, c.Kind.ToString()))
                .ToList(),
        });
    }

    private static async Task<IResult> GetAsync(
        string key,
        [FromServices] JobPostingQueryRepository repository,
        CancellationToken ct,
        string? searchTerm = null)
    {
        var graph = ConceptGraph.Default;

        if (!graph.TryGet(key, out var concept))
        {
            return TypedResults.NotFound();
        }

        var edges = BuildEdges(graph, key).ToList();

        // One query covering this concept and every neighbour, rather than one per node. The
        // list is a dozen keys, so it is an index seek rather than the whole-vocabulary
        // aggregate that "demand for everything" would be.
        var wanted = edges.Select(e => e.Concept).Append(key).Distinct(StringComparer.Ordinal).ToList();
        var demand = await repository.GetConceptDemandAsync(wanted, searchTerm, ct);

        return TypedResults.Ok(new ConceptDetail
        {
            Concept = concept.Key,
            Label = concept.Label,
            Kind = concept.Kind.ToString(),
            Demand = demand.GetValueOrDefault(key),
            Labels = graph.LabelsOf(key)
                .Select(l => new LabelResponse(l.Label, l.Kind.ToString()))
                .ToList(),
            Edges = edges
                .Select(e => e with { Demand = demand.GetValueOrDefault(e.Concept) })
                .OrderBy(e => e.Relation, StringComparer.Ordinal)
                .ThenByDescending(e => e.Demand)
                .ToList(),
            Ancestors = graph.Ancestors(key)
                .Where(a => a.Key != key)
                .OrderBy(a => a.Value)
                .Select(a => new AncestorResponse(a.Key, Label(graph, a.Key), a.Value))
                .ToList(),
        });
    }

    /// <summary>
    /// Every edge touching this concept, in both directions, named from its point of view.
    /// </summary>
    /// <remarks>
    /// Both directions matter and they are different questions. "What is this under" and "what
    /// is under this" are asked equally often, and a graph view that only walked one way would
    /// make a domain look like a leaf. <c>Broader</c>/<c>Narrower</c> and
    /// <c>Implies</c>/<c>ImpliedBy</c> are therefore the same stored edge read from each end.
    ///
    /// Succession keeps its direction in the naming rather than being folded into a symmetric
    /// relation: holding AngularJS is weak evidence for an Angular role and holding Angular is
    /// no evidence at all for an AngularJS one, which is the whole reason the vocabulary
    /// records it separately from similarity.
    /// </remarks>
    private static IEnumerable<ConceptEdgeResponse> BuildEdges(ConceptGraph graph, string key)
    {
        foreach (var edge in graph.Relations())
        {
            var (other, relation) = (edge.FromKey == key, edge.ToKey == key, edge.Type) switch
            {
                (true, _, ConceptRelationType.Broader) => (edge.ToKey, "Broader"),
                (_, true, ConceptRelationType.Broader) => (edge.FromKey, "Narrower"),
                (true, _, ConceptRelationType.Implies) => (edge.ToKey, "Implies"),
                (_, true, ConceptRelationType.Implies) => (edge.FromKey, "ImpliedBy"),
                (true, _, ConceptRelationType.SucceededBy) => (edge.ToKey, "SucceededBy"),
                (_, true, ConceptRelationType.SucceededBy) => (edge.FromKey, "Succeeds"),
                (true, _, ConceptRelationType.Related) => (edge.ToKey, "Related"),
                (_, true, ConceptRelationType.Related) => (edge.FromKey, "Related"),
                (true, _, ConceptRelationType.VariantOf) => (edge.ToKey, "VariantOf"),
                (_, true, ConceptRelationType.VariantOf) => (edge.FromKey, "VariantOf"),
                _ => (string.Empty, string.Empty),
            };

            if (other.Length == 0 || !graph.TryGet(other, out var concept))
            {
                continue;
            }

            yield return new ConceptEdgeResponse(
                other, concept.Label, concept.Kind.ToString(), relation, null);
        }
    }

    /// <summary>
    /// Where the corpus's assertions come from, by pass and by strength.
    /// </summary>
    /// <remarks>
    /// One SQL aggregate, cached on the facets policy. See the repository method for why that
    /// is allowed here and what would stop it being allowed.
    /// </remarks>
    private static async Task<IResult> SourceCompositionAsync(
        [FromServices] JobPostingQueryRepository repository,
        CancellationToken ct,
        string? searchTerm = null)
    {
        var rows = await repository.GetSourceCompositionAsync(searchTerm, ct);

        var total = rows.Sum(r => r.Assertions);

        var sources = rows
            .GroupBy(r => r.Source)
            .OrderBy(g => g.Key)
            .Select(g => new SourceBreakdown(
                g.Key.ToString(),
                g.Sum(r => r.Assertions),
                // Summed rather than distinct-counted across polarities: the same posting can
                // appear under two strengths for one source, so this is an upper bound and is
                // named Postings rather than presented as a population.
                g.Sum(r => r.Postings),
                g.OrderByDescending(r => (int)r.Polarity)
                    .Select(r => new PolarityCount(r.Polarity.ToString(), r.Assertions))
                    .ToList()))
            .ToList();

        var graded = rows
            .Where(r => r.Polarity != AssertionPolarity.Unspecified)
            .Sum(r => r.Assertions);

        return TypedResults.Ok(new SourceCompositionResponse
        {
            SearchTerm = searchTerm,
            Sources = sources,
            TotalAssertions = total,
            GradedShare = total == 0 ? 0 : (double)graded / total,
        });
    }

    private static string Label(ConceptGraph graph, string key)
        => graph.TryGet(key, out var concept) ? concept.Label : key;
}
