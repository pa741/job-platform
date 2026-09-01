namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// A search somebody configured, and the slug its results come back under.
/// </summary>
/// <remarks>
/// <b>The slug is globally unique and the name is unique only per owner.</b> Two people may
/// both call a search "London backend"; they cannot both own the slug, because a slug is what
/// the scraper writes into a blob name and what <c>JobPostingSearchTerms</c> keys attribution
/// on. <c>SearchSlug.Unique</c> resolves the collision by suffixing rather than by refusing the
/// save, so neither person learns anything about the other's searches.
///
/// <b><see cref="OwnerSubjectId"/> is the Entra <c>oid</c>, and every read and write through
/// <c>ScraperSearchRepository</c> is scoped to it</b> - with one named exception, the publisher,
/// which needs every enabled search across all owners because the scraper runs one config. The
/// corpus itself stays shared: a posting is public text and <c>JobPostingSearchTerms</c> already
/// attributes one posting to every search that found it. This column is what makes scoping the
/// browse experience a later filter rather than a later migration.
/// </remarks>
public sealed class ScraperSearchEntity
{
    public long Id { get; set; }

    /// <summary>
    /// The Entra object id of whoever configured it.
    /// </summary>
    /// <remarks>
    /// <c>oid</c> and never <c>sub</c>, for the reason <c>CandidateProfileEntity</c> gives:
    /// <c>sub</c> is pairwise per application, so a second app registration would orphan every
    /// row stored under the first.
    /// </remarks>
    public required string OwnerSubjectId { get; set; }

    /// <summary>The global identity. Never edited once assigned.</summary>
    public required string Slug { get; set; }

    /// <summary>What the owner called it.</summary>
    public required string Name { get; set; }

    /// <summary>Paused searches are kept and not published.</summary>
    public bool Enabled { get; set; } = true;

    public required string SearchTerm { get; set; }

    public string? Location { get; set; }
    public string? CountryIndeed { get; set; }
    public bool? IsRemote { get; set; }
    public int? HoursOld { get; set; }
    public int? ResultsWanted { get; set; }
    public string? JobType { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public List<ScraperSearchSiteEntity> Sites { get; } = [];
    public List<ScraperSearchFilterEntity> Filters { get; } = [];
}

/// <summary>One board a search names.</summary>
/// <remarks>
/// A child table rather than a delimited column, following <c>ProfileJobTypes</c>: the list is
/// short, closed, and the alternative is a column nothing can index and every reader has to
/// re-parse.
/// </remarks>
public sealed class ScraperSearchSiteEntity
{
    public long SearchId { get; set; }
    public ScraperSearchEntity? Search { get; set; }

    /// <summary>The wire name, e.g. <c>linkedin</c>. See <c>ScraperSites</c>.</summary>
    public required string Site { get; set; }
}

/// <summary>One freehire facet a search sets by hand.</summary>
public sealed class ScraperSearchFilterEntity
{
    public long SearchId { get; set; }
    public ScraperSearchEntity? Search { get; set; }

    public required string Key { get; set; }
    public required string Value { get; set; }
}
