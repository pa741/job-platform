using JobPlatform.Core.Searches;
using JobPlatform.Data.Sql;

namespace JobPlatform.Api.Features.Searches;

/// <summary>
/// Between the wire contract and the domain record.
/// </summary>
/// <remarks>
/// The one interesting direction is inwards. A request names sites as strings because JSON has
/// no enums, and an unrecognised name is <b>refused rather than dropped</b>: a silently
/// discarded board produces a search that runs against fewer sources than the person asked for
/// and says so nowhere - the same failure the posting endpoints refuse when they answer 400 for
/// an unknown filter value instead of ignoring it.
/// </remarks>
public static class SearchMapping
{
    /// <summary>
    /// The request as a domain record, or the names it used that this build does not know.
    /// </summary>
    /// <remarks>
    /// The slug is not taken from the request - there is no field for it - so a placeholder
    /// stands in until the repository assigns the real one on create, or the route supplies the
    /// existing one on update. It is never persisted.
    /// </remarks>
    public static bool TryToDomain(
        this ScraperSearchRequest request,
        string slug,
        out ScraperSearch search,
        out IReadOnlyList<string> unknownSites)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sites = new List<ScraperSite>();
        var unknown = new List<string>();

        foreach (var name in request.Sites)
        {
            if (ScraperSites.TryParse(name, out var site))
            {
                sites.Add(site);
            }
            else
            {
                unknown.Add(name);
            }
        }

        unknownSites = unknown;

        search = new ScraperSearch
        {
            Slug = slug,
            Name = request.Name?.Trim() ?? string.Empty,
            Enabled = request.Enabled,
            SearchTerm = request.SearchTerm?.Trim() ?? string.Empty,
            Sites = [.. sites.Distinct().Order()],
            Location = request.Location,
            CountryIndeed = request.CountryIndeed,
            IsRemote = request.IsRemote,
            HoursOld = request.HoursOld,
            ResultsWanted = request.ResultsWanted,
            JobType = Canonical(request.JobType),
            FreehireFilters = request.FreehireFilters
                .Where(filter => !string.IsNullOrWhiteSpace(filter.Value))
                .ToDictionary(
                    filter => filter.Key.Trim(),
                    filter => filter.Value.Trim(),
                    StringComparer.OrdinalIgnoreCase),
        };

        return unknown.Count == 0;
    }

    public static ScraperSearchResponse ToResponse(this ScraperSearchView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var search = view.Search;

        return new ScraperSearchResponse(
            search.Slug,
            search.Name,
            search.Enabled,
            search.SearchTerm,
            [.. search.Sites.Select(site => site.ToWireName())],
            search.Location,
            search.CountryIndeed,
            search.IsRemote,
            search.HoursOld,
            search.ResultsWanted,
            search.JobType,
            search.FreehireFilters,
            view.CreatedUtc,
            view.UpdatedUtc);
    }

    /// <summary>
    /// The canonical spelling of a job type, or the input where it has none.
    /// </summary>
    /// <remarks>
    /// Normalising here rather than refusing means "Full Time" from a client that spells it that
    /// way is stored as <c>fulltime</c> and compares equal to a posting's. An unrecognised value
    /// is passed through unchanged so validation is the thing that reports it, rather than this
    /// method quietly turning it into null and reporting nothing.
    /// </remarks>
    private static string? Canonical(string? jobType)
        => string.IsNullOrWhiteSpace(jobType)
            ? null
            : Enrichment(jobType) ?? jobType.Trim();

    private static string? Enrichment(string jobType)
        => Core.Enrichment.JobTypeNormalizer.Normalize(jobType) is [var only] ? only : null;
}
