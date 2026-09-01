namespace JobPlatform.Core.Searches;

/// <summary>
/// The job boards a configured search may name.
/// </summary>
/// <remarks>
/// A closed set, and deliberately smaller than the set the scraper's JobSpy fork can address.
/// Glassdoor and Google are both documented broken in the scraper's <c>config.yaml</c> - one
/// 404s on its CSRF endpoint after a Next.js migration, the other cannot find its cursor in the
/// search markup - and offering a board that returns nothing is worse than not offering it: the
/// failure lands at 03:00 on somebody else's schedule and reads as a quiet market rather than a
/// broken source.
///
/// Adding one here is a two-line change plus a fixture case. Removing one leaves stored rows
/// naming it, which is why <see cref="ScraperSites.TryParse"/> answers false rather than
/// throwing.
/// </remarks>
public enum ScraperSite
{
    Indeed = 0,
    LinkedIn,
    Freehire,
}

/// <summary>The wire spelling of a <see cref="ScraperSite"/>, which is jobspy's own.</summary>
/// <remarks>
/// These strings reach <c>scrape_jobs(site_name=[...])</c> unchanged, so they are a contract
/// with the scraper rather than a display concern. <c>ScraperConfigDocumentTests</c> pins them.
/// </remarks>
public static class ScraperSites
{
    public const string Indeed = "indeed";
    public const string LinkedIn = "linkedin";
    public const string Freehire = "freehire";

    /// <summary>Every site, in the order a form should offer them.</summary>
    public static IReadOnlyList<ScraperSite> All { get; } =
        [ScraperSite.Indeed, ScraperSite.LinkedIn, ScraperSite.Freehire];

    public static string ToWireName(this ScraperSite site) => site switch
    {
        ScraperSite.Indeed => Indeed,
        ScraperSite.LinkedIn => LinkedIn,
        ScraperSite.Freehire => Freehire,
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, "Unknown scraper site."),
    };

    /// <summary>
    /// Reads a wire name back. False for anything unrecognised, never an exception.
    /// </summary>
    /// <remarks>
    /// A stored row can name a site this build no longer offers - the enum shrank, the database
    /// did not - and that must degrade to "this search names a board we no longer scrape" rather
    /// than to a 500 on somebody's settings page.
    /// </remarks>
    public static bool TryParse(string? value, out ScraperSite site)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Indeed:
                site = ScraperSite.Indeed;
                return true;
            case LinkedIn:
                site = ScraperSite.LinkedIn;
                return true;
            case Freehire:
                site = ScraperSite.Freehire;
                return true;
            default:
                site = default;
                return false;
        }
    }
}
