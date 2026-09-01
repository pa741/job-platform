using System.ComponentModel.DataAnnotations;
using JobPlatform.Core.Searches;

namespace JobPlatform.Api.Features.Searches;

/// <summary>
/// One search, as the client sends it.
/// </summary>
/// <remarks>
/// <b>Every field is named here, and that is the security boundary of the whole feature.</b> The
/// scraper ends up calling <c>scrape_jobs(**params)</c>, so a contract carrying a free-form map
/// of parameter names would let a browser reach any keyword argument that library takes -
/// including <c>proxies</c> and <c>freehire_api_key</c>. There is no such field, and
/// <see cref="ScraperConfigDocument.ToParams"/> is the only code that writes a jobspy parameter
/// name.
///
/// <c>Slug</c> is absent for the same reason <c>SubjectId</c> is absent from
/// <c>ProfileRequest</c>: it is an identity the platform assigns, and a client that could choose
/// one could attach its results to somebody else's search term.
/// </remarks>
public record ScraperSearchRequest
{
    /// <summary>What to call it. Unique among the caller's own searches.</summary>
    [Required]
    [MaxLength(ScraperSearchValidation.MaxNameLength)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Paused searches are kept and not scraped.</summary>
    public bool Enabled { get; init; } = true;

    [Required]
    [MaxLength(ScraperSearchValidation.MaxSearchTermLength)]
    public string SearchTerm { get; init; } = string.Empty;

    /// <summary>Wire names, e.g. <c>["indeed", "linkedin"]</c>. See <c>GET /searches/options</c>.</summary>
    public IReadOnlyList<string> Sites { get; init; } = [];

    [MaxLength(ScraperSearchValidation.MaxLocationLength)]
    public string? Location { get; init; }

    [MaxLength(100)]
    public string? CountryIndeed { get; init; }

    /// <summary>Null is "no preference", which is not the same as false.</summary>
    public bool? IsRemote { get; init; }

    [Range(1, ScraperSearchValidation.MaxHoursOld)]
    public int? HoursOld { get; init; }

    [Range(1, ScraperSearchValidation.MaxResultsWanted)]
    public int? ResultsWanted { get; init; }

    [MaxLength(30)]
    public string? JobType { get; init; }

    /// <summary>Extra freehire facets. Keys are bounded; see <c>GET /searches/options</c>.</summary>
    public IReadOnlyDictionary<string, string> FreehireFilters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One search as it is stored, plus what the platform decided about it.</summary>
/// <param name="Slug">
/// The identity. It is what appears in the blob name the scraper writes, and therefore what the
/// dashboard's search-term picker and every metric partition call this search - so it is shown
/// rather than hidden, or the two views cannot be reconciled by the person looking at them.
/// </param>
public sealed record ScraperSearchResponse(
    string Slug,
    string Name,
    bool Enabled,
    string SearchTerm,
    IReadOnlyList<string> Sites,
    string? Location,
    string? CountryIndeed,
    bool? IsRemote,
    int? HoursOld,
    int? ResultsWanted,
    string? JobType,
    IReadOnlyDictionary<string, string> FreehireFilters,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// The caller's searches, and whether the scraper has been told about them.
/// </summary>
/// <param name="PublishedUtc">
/// When the scraper's configuration was last written. Null means it has not been - either no
/// storage is configured for this deployment, or the last publish failed. Either way the answer
/// to "why is my new search not running" is on the page rather than in a log.
/// </param>
/// <param name="Published">
/// Whether the most recent write succeeded. Deliberately separate from the timestamp: a stale
/// timestamp and no timestamp are different problems.
/// </param>
public sealed record ScraperSearchListResponse(
    IReadOnlyList<ScraperSearchResponse> Searches,
    bool Published,
    DateTimeOffset? PublishedUtc);

/// <summary>
/// The vocabulary a form needs, in one call.
/// </summary>
/// <remarks>
/// Served rather than duplicated in the client, following <c>/postings/facets</c>: a dropdown
/// listing boards this build does not accept produces a save that fails for a reason the person
/// cannot see, and a hard-coded list in TypeScript is exactly how that happens.
/// </remarks>
public sealed record ScraperSearchOptionsResponse(
    IReadOnlyList<string> Sites,
    IReadOnlyList<string> JobTypes,
    IReadOnlyList<string> FreehireFilterKeys,
    int MaxHoursOld,
    int MaxResultsWanted);
