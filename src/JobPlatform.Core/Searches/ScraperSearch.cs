namespace JobPlatform.Core.Searches;

/// <summary>
/// One configured search: what to scrape, and the slug the results come back under.
/// </summary>
/// <remarks>
/// <b>Every field here is a typed property, and that is the whole safety property of letting a
/// search be configured from a browser.</b> The scraper calls
/// <c>scrape_jobs(**params)</c>, so a path that let a client name a parameter would let a
/// client reach any keyword argument that library has - including the ones carrying proxies and
/// credentials. There is no such path: a request is mapped onto these properties, and
/// <see cref="ScraperConfigDocument.ToParams"/> is the only code in the system that writes a
/// jobspy parameter name.
///
/// The owner is deliberately absent. This is the shape that gets published to a blob the NAS
/// reads, and who asked for a search is nobody's business out there;
/// <c>ScraperSearchEntity.OwnerSubjectId</c> holds it on the platform side.
/// </remarks>
public sealed record ScraperSearch
{
    /// <summary>The global identity. See <see cref="SearchSlug"/>.</summary>
    public required string Slug { get; init; }

    /// <summary>What the person called it. Display only.</summary>
    public required string Name { get; init; }

    /// <summary>Paused searches are stored and not published.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The query itself, e.g. <c>software engineer</c>.</summary>
    public required string SearchTerm { get; init; }

    public IReadOnlyList<ScraperSite> Sites { get; init; } = [];

    public string? Location { get; init; }

    /// <summary>Indeed needs its country named separately from the location string.</summary>
    public string? CountryIndeed { get; init; }

    /// <summary>Null means "no preference", which is not the same as false.</summary>
    public bool? IsRemote { get; init; }

    /// <summary>How far back to look. Null leaves the scraper's own default in place.</summary>
    public int? HoursOld { get; init; }

    public int? ResultsWanted { get; init; }

    /// <summary>One of <see cref="Enrichment.JobTypeNormalizer"/>'s canonical values.</summary>
    public string? JobType { get; init; }

    /// <summary>
    /// Extra freehire facets, applied over whatever the options above imply.
    /// </summary>
    /// <remarks>
    /// Keys are bounded by <see cref="ScraperSearchValidation.FreehireFilterKeys"/>; values are
    /// not, because freehire answers an unknown value with no matches rather than an error, so a
    /// typo costs an empty search rather than a failed run.
    /// </remarks>
    public IReadOnlyDictionary<string, string> FreehireFilters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
