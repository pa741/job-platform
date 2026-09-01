using System.Text.RegularExpressions;

namespace JobPlatform.Core.Searches;

/// <summary>
/// Turns a search's display name into the slug that identifies it everywhere else.
/// </summary>
/// <remarks>
/// <b>The slug is the identity and the name is an attribute</b>, the same split as
/// <c>skill.kubernetes</c> against "Kubernetes" in the concept vocabulary, and for the same
/// reason: the slug is what the scraper writes into the blob name, what
/// <c>BlobNameParser</c> reads back out of it, what <c>JobPostingSearchTerms</c> keys
/// attribution on, what partitions the Cosmos metrics, and what names a curated Parquet
/// partition. Renaming a search is an edit; renaming a slug is a data migration.
///
/// <b>This rule has to match <c>slugify</c> in the scraper's <c>scrape_jobs.py</c> exactly.</b>
/// The scraper still slugifies when it falls back to its local <c>config.yaml</c>, so a name
/// producing two different slugs on the two sides would silently split one search term in two.
/// <c>SearchSlugTests</c> pins the cases that differ between plausible implementations -
/// leading and trailing punctuation, runs of separators, accented characters and the empty
/// result.
/// </remarks>
public static partial class SearchSlug
{
    /// <summary>What an all-punctuation name slugifies to, matching the scraper's fallback.</summary>
    public const string Fallback = "jobs";

    /// <summary>Longest slug a name may produce, matching the column.</summary>
    public const int MaxLength = 200;

    [GeneratedRegex("[^a-zA-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators { get; }

    /// <summary>
    /// <c>re.sub(r"[^a-zA-Z0-9]+", "-", text.strip().lower()).strip("-") or "jobs"</c>.
    /// </summary>
    /// <remarks>
    /// Non-ASCII is replaced rather than transliterated, exactly as the Python does. "Ingeniero
    /// de software" keeps its letters; "Développeur" becomes <c>d-veloppeur</c>. That is ugly
    /// and it is the point - the two sides agreeing matters far more than either being pretty,
    /// and transliterating on one side only is precisely how they would stop agreeing.
    /// </remarks>
    public static string Slugify(string? text)
    {
        var slug = Separators
            .Replace((text ?? string.Empty).Trim().ToLowerInvariant(), "-")
            .Trim('-');

        if (slug.Length > MaxLength)
        {
            slug = slug[..MaxLength].TrimEnd('-');
        }

        return slug.Length == 0 ? Fallback : slug;
    }

    /// <summary>
    /// The slug for <paramref name="name"/>, disambiguated against slugs already in use.
    /// </summary>
    /// <remarks>
    /// A numeric suffix rather than a hash, because it is deterministic and therefore
    /// assertable, and because the alternative to a suffix is refusing the save - which would
    /// tell one person that another person's search exists under that name, and make them think
    /// of a new one for no benefit they can see.
    ///
    /// <paramref name="taken"/> is every slug in the system rather than the caller's own: the
    /// slug namespace is global because everything downstream of the blob name is global.
    /// </remarks>
    public static string Unique(string? name, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var slug = Slugify(name);

        if (!taken.Contains(slug))
        {
            return slug;
        }

        // Bounded rather than a while(true): a caller passing a set that contains everything
        // would otherwise spin forever, and an exhausted namespace is a bug worth surfacing.
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{Trim(slug, suffix)}-{suffix}";

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not derive a free slug from '{slug}' after 998 attempts.");
    }

    /// <summary>Makes room for the suffix without exceeding the column.</summary>
    private static string Trim(string slug, int suffix)
    {
        var room = MaxLength - 1 - suffix.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

        return slug.Length <= room ? slug : slug[..room].TrimEnd('-');
    }
}
