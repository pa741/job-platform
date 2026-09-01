using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobPlatform.Core.Searches;

/// <summary>
/// The document the scraper reads instead of its local <c>config.yaml</c> searches.
/// </summary>
/// <remarks>
/// Published to a blob rather than served from an endpoint, and that is an architectural
/// decision rather than a convenience. The scraper runs on a NAS with no managed identity, so
/// an API it had to authenticate against would need a client secret or a function key sitting
/// on that NAS - and a credential appearing where the design has none is the signal that the
/// design is being worked around. It already holds a storage credential and already speaks to
/// exactly one Azure service; this keeps both facts true.
///
/// <b>Rebuilt whole, never amended</b>, like a curated Parquet partition: a republish converges
/// and a failed one needs no cleanup.
///
/// No owner travels with it. The NAS has no business knowing whose search it is running, and a
/// field that does not exist cannot leak.
/// </remarks>
public sealed record ScraperConfigDocument
{
    /// <summary>
    /// The shape's version, so the scraper can refuse a document it does not understand.
    /// </summary>
    /// <remarks>
    /// Bump it only when an existing key changes meaning. Adding a key is not a version change -
    /// the scraper merges what it is given over its own defaults and ignores nothing, so a new
    /// key reaches an old scraper as an unknown jobspy argument, which is a loud failure rather
    /// than a silent one.
    /// </remarks>
    public const int CurrentVersion = 1;

    [JsonPropertyOrder(0)]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyOrder(1)]
    public required DateTimeOffset PublishedUtc { get; init; }

    [JsonPropertyOrder(2)]
    public required IReadOnlyList<PublishedSearch> Searches { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Builds the document from every search that should run.
    /// </summary>
    /// <remarks>
    /// Filtering to enabled searches is the caller's job, not this one's: a document is a
    /// faithful rendering of what it was handed, and a builder that quietly dropped rows would
    /// make "why is my search not running" unanswerable from the published file.
    ///
    /// Ordered by slug so an unchanged set of searches produces a byte-identical document. That
    /// is what makes a diff of two published files mean something.
    /// </remarks>
    public static ScraperConfigDocument Build(
        IEnumerable<ScraperSearch> searches, DateTimeOffset publishedUtc)
    {
        ArgumentNullException.ThrowIfNull(searches);

        return new ScraperConfigDocument
        {
            PublishedUtc = publishedUtc,
            Searches =
            [
                .. searches
                    .OrderBy(search => search.Slug, StringComparer.Ordinal)
                    .Select(search => new PublishedSearch(search.Slug, ToParams(search)))
            ],
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// One search's jobspy keyword arguments.
    /// </summary>
    /// <remarks>
    /// <b>This method is the only place in the system where a jobspy parameter name is
    /// written.</b> The scraper calls <c>scrape_jobs(**params)</c>, so any second writer of
    /// these strings is a second way for an unvalidated key to reach that call - and the keys it
    /// could reach include the ones carrying proxies and API keys. Everything upstream of here
    /// is typed properties on <see cref="ScraperSearch"/>; a client never names a parameter.
    ///
    /// <b>An absent value is omitted, never written as null.</b> The scraper merges this over
    /// its own <c>defaults:</c> block, which is where the operational settings live - verbosity,
    /// LinkedIn description fetching, annual salary enforcement. A key present with a null value
    /// would overwrite one of those defaults with nothing, so "the person did not choose" and
    /// "the person chose nothing" have to be different bytes on the wire.
    /// </remarks>
    public static IReadOnlyDictionary<string, object> ToParams(ScraperSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);

        var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["search_term"] = search.SearchTerm,
            ["site_name"] = search.Sites.Select(site => site.ToWireName()).ToArray(),
        };

        Add("location", search.Location);
        Add("country_indeed", search.CountryIndeed);
        Add("job_type", search.JobType);

        if (search.IsRemote is { } remote)
        {
            parameters["is_remote"] = remote;
        }

        if (search.HoursOld is { } hours)
        {
            parameters["hours_old"] = hours;
        }

        if (search.ResultsWanted is { } wanted)
        {
            parameters["results_wanted"] = wanted;
        }

        if (search.FreehireFilters.Count > 0)
        {
            parameters["freehire_filters"] = search.FreehireFilters
                .OrderBy(filter => filter.Key, StringComparer.Ordinal)
                .ToDictionary(filter => filter.Key, filter => filter.Value, StringComparer.Ordinal);
        }

        return parameters;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[key] = value.Trim();
            }
        }
    }
}

/// <summary>One search as the scraper sees it: a slug, and what to call jobspy with.</summary>
public sealed record PublishedSearch(
    [property: JsonPropertyOrder(0)] string Slug,
    [property: JsonPropertyOrder(1)] IReadOnlyDictionary<string, object> Params);
