using JobPlatform.Core.Searches;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>A stored search, with the timestamps the owner is entitled to see.</summary>
public sealed record ScraperSearchView(
    ScraperSearch Search,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Reads and writes one person's searches, and only ever their own.
/// </summary>
/// <remarks>
/// <b>Every method that touches a specific row takes a subject id, and none takes a slug or an
/// id on its own.</b> That is the authorisation boundary expressed as a type, exactly as
/// <see cref="CandidateProfileRepository"/> expresses it: there is no overload an endpoint could
/// hand a route parameter to, so a stranger's search cannot be edited or deleted by mistake.
///
/// <b><see cref="ListForPublishAsync"/> is the one deliberate exception</b>, and it is a read of
/// every owner's enabled searches because the scraper runs one configuration for the whole
/// platform. It returns the domain record, which carries no owner, so nothing downstream of it
/// can attribute a search to a person even by accident.
///
/// A save is a replace, like a profile save and for the same reason: the child rows are a set
/// the form submits whole, and a merge has no way to express "stop scraping LinkedIn".
/// </remarks>
public sealed class ScraperSearchRepository(JobsDbContext db)
{
    /// <summary>Everything the caller owns, by name, for their settings page.</summary>
    public async Task<IReadOnlyList<ScraperSearchView>> ListAsync(
        string subjectId, CancellationToken ct = default)
    {
        var entities = await db.ScraperSearches
            .AsNoTracking()
            .Where(s => s.OwnerSubjectId == subjectId)
            .Include(s => s.Sites)
            .Include(s => s.Filters)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        return [.. entities.Select(ToView)];
    }

    /// <summary>
    /// Every enabled search, whoever owns it. The publisher's query and nothing else's.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="ScraperSearch"/> rather than <see cref="ScraperSearchView"/> so the
    /// owner never travels with it. The published document is read on a NAS outside the tenant;
    /// a type with no field for the owner cannot leak one.
    /// </remarks>
    public async Task<IReadOnlyList<ScraperSearch>> ListForPublishAsync(CancellationToken ct = default)
    {
        var entities = await db.ScraperSearches
            .AsNoTracking()
            .Where(s => s.Enabled)
            .Include(s => s.Sites)
            .Include(s => s.Filters)
            .OrderBy(s => s.Slug)
            .ToListAsync(ct);

        return [.. entities.Select(ToDomain)];
    }

    /// <summary>
    /// Stores a new search, assigning it a free slug.
    /// </summary>
    /// <remarks>
    /// The slug is derived here rather than accepted from the request, because it is an identity
    /// the rest of the system keys on and a client that could choose one could attach its
    /// results to somebody else's search term. <see cref="SearchSlug.Unique"/> disambiguates
    /// against every slug in use, not just the caller's.
    /// </remarks>
    public async Task<ScraperSearchView> CreateAsync(
        string subjectId, ScraperSearch search, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(time);

        var taken = await db.ScraperSearches
            .AsNoTracking()
            .Select(s => s.Slug)
            .ToListAsync(ct);

        var now = time.GetUtcNow();

        var entity = new ScraperSearchEntity
        {
            OwnerSubjectId = subjectId,
            Slug = SearchSlug.Unique(search.Name, taken.ToHashSet(StringComparer.OrdinalIgnoreCase)),
            Name = search.Name.Trim(),
            SearchTerm = search.SearchTerm.Trim(),
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        ApplyScalars(entity, search);
        ApplyChildren(entity, search);

        db.ScraperSearches.Add(entity);
        await db.SaveChangesAsync(ct);

        return ToView(entity);
    }

    /// <summary>
    /// Replaces one of the caller's searches. Null where they do not own that slug.
    /// </summary>
    /// <remarks>
    /// <b>The slug is not editable.</b> Renaming a search is an edit; renaming its slug would
    /// orphan every posting attributed to the old one and start a new search term with no
    /// history - which is the concept vocabulary's key-versus-label rule applied to the thing it
    /// was written about.
    ///
    /// Null rather than an exception for "not yours": the endpoint answers 404 either way, and
    /// a caller must not be able to tell "no such search" from "somebody else's search".
    /// </remarks>
    public async Task<ScraperSearchView?> UpdateAsync(
        string subjectId,
        string slug,
        ScraperSearch search,
        TimeProvider time,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(time);

        var entity = await db.ScraperSearches
            .Include(s => s.Sites)
            .Include(s => s.Filters)
            .FirstOrDefaultAsync(s => s.OwnerSubjectId == subjectId && s.Slug == slug, ct);

        if (entity is null)
        {
            return null;
        }

        entity.Name = search.Name.Trim();
        entity.SearchTerm = search.SearchTerm.Trim();
        entity.UpdatedUtc = time.GetUtcNow();

        ApplyScalars(entity, search);

        // A replace, not a merge. Cleared through the tracked collections rather than with
        // ExecuteDelete, so the whole update is one SaveChanges - ExecuteDelete commits
        // immediately and leaves the change tracker holding what it deleted, which is the
        // asymmetry that cost the extraction writer a bug.
        entity.Sites.Clear();
        entity.Filters.Clear();
        ApplyChildren(entity, search);

        await db.SaveChangesAsync(ct);

        return ToView(entity);
    }

    /// <summary>Removes one of the caller's searches. False where they do not own that slug.</summary>
    /// <remarks>
    /// The postings it found stay. They are public adverts, the corpus is shared, and
    /// <c>JobPostingSearchTerms</c> is history - a row saying "this search saw this posting on
    /// this day" does not stop being true because nobody is running the search any more. What
    /// stops is the scraping.
    /// </remarks>
    public async Task<bool> DeleteAsync(string subjectId, string slug, CancellationToken ct = default)
    {
        var deleted = await db.ScraperSearches
            .Where(s => s.OwnerSubjectId == subjectId && s.Slug == slug)
            .ExecuteDeleteAsync(ct);

        return deleted > 0;
    }

    /// <summary>Whether the caller already has a search by this name, ignoring one slug.</summary>
    /// <remarks>
    /// Asked before a save so the endpoint can answer 409 with a reason, rather than letting the
    /// unique index surface as a 500 nobody can act on. <paramref name="exceptSlug"/> is what
    /// lets an update keep its own name.
    /// </remarks>
    public Task<bool> NameTakenAsync(
        string subjectId, string name, string? exceptSlug = null, CancellationToken ct = default)
        => db.ScraperSearches
            .AsNoTracking()
            .AnyAsync(
                s => s.OwnerSubjectId == subjectId
                     && s.Name == name
                     && (exceptSlug == null || s.Slug != exceptSlug),
                ct);

    private static void ApplyScalars(ScraperSearchEntity entity, ScraperSearch search)
    {
        entity.Enabled = search.Enabled;
        entity.Location = Trimmed(search.Location);
        entity.CountryIndeed = Trimmed(search.CountryIndeed);
        entity.IsRemote = search.IsRemote;
        entity.HoursOld = search.HoursOld;
        entity.ResultsWanted = search.ResultsWanted;
        entity.JobType = Trimmed(search.JobType);
    }

    private static void ApplyChildren(ScraperSearchEntity entity, ScraperSearch search)
    {
        foreach (var site in search.Sites.Distinct())
        {
            entity.Sites.Add(new ScraperSearchSiteEntity { Site = site.ToWireName() });
        }

        foreach (var (key, value) in search.FreehireFilters)
        {
            entity.Filters.Add(new ScraperSearchFilterEntity { Key = key, Value = value });
        }
    }

    private static ScraperSearchView ToView(ScraperSearchEntity entity)
        => new(ToDomain(entity), entity.CreatedUtc, entity.UpdatedUtc);

    /// <summary>
    /// The stored row as the domain sees it.
    /// </summary>
    /// <remarks>
    /// A site name the current build no longer offers is dropped rather than thrown on. The
    /// enum can shrink - a board breaking upstream is exactly why it would - and the stored rows
    /// do not shrink with it, so the alternative is a 500 on the settings page of whoever
    /// happened to name that board. Validation catches the resulting empty list on the next
    /// save, which is where the person can actually do something about it.
    /// </remarks>
    private static ScraperSearch ToDomain(ScraperSearchEntity entity)
        => new()
        {
            Slug = entity.Slug,
            Name = entity.Name,
            Enabled = entity.Enabled,
            SearchTerm = entity.SearchTerm,
            Sites =
            [
                .. entity.Sites
                    .Select(site => ScraperSites.TryParse(site.Site, out var parsed) ? parsed : (ScraperSite?)null)
                    .Where(site => site is not null)
                    .Select(site => site!.Value)
                    .Distinct()
                    .Order()
            ],
            Location = entity.Location,
            CountryIndeed = entity.CountryIndeed,
            IsRemote = entity.IsRemote,
            HoursOld = entity.HoursOld,
            ResultsWanted = entity.ResultsWanted,
            JobType = entity.JobType,
            FreehireFilters = entity.Filters
                .OrderBy(filter => filter.Key, StringComparer.Ordinal)
                .ToDictionary(filter => filter.Key, filter => filter.Value, StringComparer.OrdinalIgnoreCase),
        };

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
