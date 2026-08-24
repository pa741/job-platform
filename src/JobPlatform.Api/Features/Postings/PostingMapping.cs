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
    };

    private static IReadOnlyList<NamedCount> ToNamedCounts(this IReadOnlyList<NamedCountRow> rows)
        => rows.Select(r => new NamedCount(r.Name, r.Count)).ToList();
}
