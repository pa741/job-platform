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
        JobUrl = entity.JobUrl,
        DescriptionLength = entity.DescriptionLength,
        FirstSeenUtc = entity.FirstSeenUtc,
        LastSeenUtc = entity.LastSeenUtc,
        SeenCount = entity.SeenCount,
        SearchTerm = entity.SearchTerm,
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
        ContentHash = entity.ContentHash,
        FirstSeenRunId = entity.FirstSeenRunId,
        LastSeenRunId = entity.LastSeenRunId,
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
    };

    private static IReadOnlyList<NamedCount> ToNamedCounts(this IReadOnlyList<NamedCountRow> rows)
        => rows.Select(r => new NamedCount(r.Name, r.Count)).ToList();
}
