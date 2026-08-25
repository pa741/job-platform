using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;

namespace JobPlatform.Api.Features.Postings;

internal static class PostingMapping
{
    public static PostingSummary ToSummary(this JobPostingEntity entity) => new()
    {
        Id = entity.Id,
        SourceKey = entity.SourceKey,
        Site = entity.Site,
        Title = entity.Title,
        Company = entity.Company,
        Location = entity.LocationRaw,
        City = entity.LocationCity,
        Country = entity.LocationCountry,
        IsRemote = entity.IsRemote,
        JobType = entity.JobType,
        DatePosted = entity.DatePosted,
        MinAmount = entity.MinAmount,
        MaxAmount = entity.MaxAmount,
        Currency = entity.Currency,
        SalaryInterval = entity.SalaryInterval,
        AnnualSalaryMin = entity.AnnualSalaryMin,
        AnnualSalaryMax = entity.AnnualSalaryMax,
        AnnualSalaryCurrency = entity.AnnualSalaryCurrency,
        SalaryFromText = entity.SalaryFromText,
        SalaryStatedInterval = entity.SalaryStatedInterval,
        Seniority = entity.Seniority.ToString(),
        RoleFamily = entity.RoleFamily.ToString(),
        WorkArrangement = entity.WorkArrangement.ToString(),
        HybridDaysInOffice = entity.HybridDaysInOffice,
        YearsExperienceMin = entity.YearsExperienceMin,
        YearsExperienceMax = entity.YearsExperienceMax,
        RequiresSecurityClearance = entity.RequiresSecurityClearance,
        Ir35 = entity.Ir35,
        JobUrl = entity.JobUrl,
        DescriptionLength = entity.DescriptionLength,
        FreshnessClass = entity.FreshnessClass,
        PostingAgeDays = entity.PostingAgeDays,
        RepostCount = entity.RepostCount,
        FakeFreshness = entity.FakeFreshness,
        FirstSeenUtc = entity.FirstSeenUtc,
        LastSeenUtc = entity.LastSeenUtc,
        SeenCount = entity.SeenCount,
        SearchTerms = entity.SearchTerms.Select(l => l.SearchTerm).ToList(),
    };

    public static PostingDetail ToDetail(this JobPostingEntity entity) => new()
    {
        Summary = entity.ToSummary(),
        Description = entity.Description,
        JobUrlDirect = entity.JobUrlDirect,
        CompanyUrl = entity.CompanyUrl,
        JobLevel = entity.JobLevel,
        JobFunction = entity.JobFunction,
        CompanyIndustry = entity.CompanyIndustry,
        SalarySource = entity.SalarySource,
        Synopsis = entity.Summary,
        ExperienceRange = entity.ExperienceRange,
        CompanyNumEmployees = entity.CompanyNumEmployees,
        Applicants = entity.Applicants,
        ApplicantCount = entity.ApplicantCount,
        VacancyCount = entity.VacancyCount,
        WorkFromHomeType = entity.WorkFromHomeType,
        ListingType = entity.ListingType,
        Ir35 = entity.Ir35,
        VisaSponsorship = entity.VisaSponsorship,
        ContentHash = entity.ContentHash,
        FirstSeenRunId = entity.SearchTerms.Count == 0 ? 0 : entity.SearchTerms.Min(l => l.FirstSeenRunId),
        LastSeenRunId = entity.SearchTerms.Count == 0 ? 0 : entity.SearchTerms.Max(l => l.LastSeenRunId),
    };

    public static FacetsResponse ToResponse(this PostingFacets facets) => new()
    {
        SearchTerm = facets.SearchTerm,
        Total = facets.Total,
        RemoteCount = facets.RemoteCount,
        WithSalaryCount = facets.WithSalaryCount,
        EarliestDatePosted = facets.EarliestDatePosted,
        LatestDatePosted = facets.LatestDatePosted,
        LastSeenUtc = facets.LastSeenUtc,
        Sites = facets.Sites.ToNamedCounts(),
        JobTypes = facets.JobTypes.ToNamedCounts(),
        Countries = facets.Countries.ToNamedCounts(),
        Cities = facets.Cities.ToNamedCounts(),
        Companies = facets.Companies.ToNamedCounts(),
        Concepts = [.. facets.Concepts.Select(c => new ConceptCount(c.Key, c.Label, c.Count))],
    };

    private static IReadOnlyList<NamedCount> ToNamedCounts(this IReadOnlyList<NamedCountRow> rows)
        => rows.Select(r => new NamedCount(r.Name, r.Count)).ToList();

    /// <summary>
    /// One posting with its provenance, and the closure rollup computed here.
    /// </summary>
    /// <remarks>
    /// The rollup is the reason this cannot be a client-side transform: reaching "this advert
    /// is a backend role" from "it wants C#, ASP.NET Core and SQL Server" means walking the
    /// concept DAG upward, and the DAG lives in the vocabulary shipped with this build.
    /// </remarks>
    public static PostingInsight ToInsight(this PostingProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        var graph = ConceptGraph.Default;
        var posting = provenance.Posting;

        // Deduplicated before rolling up. The same concept arrives once per source by design,
        // and counting it twice would overstate the domain it sits under.
        var distinct = provenance.Concepts
            .Select(c => c.ConceptKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rollup = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var key in distinct)
        {
            foreach (var ancestor in graph.Ancestors(key).Keys)
            {
                if (graph.TryGet(ancestor, out var concept) && concept.Kind == ConceptKind.Domain)
                {
                    rollup[ancestor] = rollup.GetValueOrDefault(ancestor) + 1;
                }
            }
        }

        return new PostingInsight
        {
            Detail = posting.ToDetail(),
            Concepts = provenance.Concepts
                // Strongest demand first, then by how much evidence backs it: what the advert
                // insists on should be the first thing a reader sees.
                .OrderByDescending(c => (int)c.Polarity)
                .ThenBy(c => c.Source)
                .ThenBy(c => Label(graph, c.ConceptKey), StringComparer.OrdinalIgnoreCase)
                .Select(c => new AssertionResponse(
                    c.ConceptKey,
                    Label(graph, c.ConceptKey),
                    graph.TryGet(c.ConceptKey, out var concept) ? concept.Kind.ToString() : "Skill",
                    c.Source.ToString(),
                    c.Polarity.ToString(),
                    c.YearsMin,
                    c.YearsMax,
                    c.EvidenceText,
                    c.Confidence))
                .ToList(),
            Domains = rollup
                .OrderByDescending(r => r.Value)
                .ThenBy(r => r.Key, StringComparer.Ordinal)
                .Select(r => new RollupResponse(r.Key, Label(graph, r.Key), r.Value))
                .ToList(),
            Mentions = provenance.Mentions
                .OrderByDescending(m => m.Occurrences)
                .Select(m => new MentionResponse(m.SurfaceForm, m.Reason.ToString(), m.Occurrences))
                .ToList(),
            Tags = posting.Tags
                .OrderBy(t => t.Tag, StringComparer.Ordinal)
                .Select(t => new TagResponse(t.Tag, t.Value))
                .ToList(),
            JobTypes = posting.JobTypes.Select(j => j.JobType).OrderBy(j => j, StringComparer.Ordinal).ToList(),
            FoundBy = posting.SearchTerms
                .OrderBy(t => t.SearchTerm, StringComparer.Ordinal)
                .Select(t => new AttributionResponse(t.SearchTerm, t.FirstSeenUtc, t.LastSeenUtc))
                .ToList(),
            Company = posting.CompanyRef is { } company
                ? new CompanyResponse(
                    company.DisplayName, company.Industry, company.EmployeesBand,
                    company.Revenue, company.Url)
                : null,
            Provenance = new ProvenanceResponse(
                posting.EnrichmentVersion,
                provenance.Extraction?.Version,
                provenance.Extraction?.Model,
                provenance.Extraction?.ExtractedAtUtc,
                posting.SeenCount,
                posting.FirstSeenUtc,
                posting.LastSeenUtc),
        };
    }

    private static string Label(ConceptGraph graph, string key)
        => graph.TryGet(key, out var concept) ? concept.Label : key;
}
