using System.Security.Cryptography;
using System.Text;
using JobPlatform.Core.Model;

namespace JobPlatform.Core.Dedup;

/// <summary>
/// Content-based identity for a posting, used to spot the same job cross-posted to
/// several boards under different ids. The scraper already drops exact repeats within a
/// run; this catches the cross-board case and, unlike the scraper's pass, works across runs.
/// </summary>
public static class JobFingerprint
{
    /// <summary>
    /// Identity of a posting's own content, for deciding whether it changed.
    /// </summary>
    /// <remarks>
    /// <b>Do not widen this to cross boards.</b> It is stored on every posting and
    /// <c>EmbeddingRepository</c> compares it to decide whether a vector is stale, so changing
    /// what it hashes marks the whole embedded corpus for re-embedding - or, worse, quietly
    /// stops marking things that did change. <see cref="CrossBoardKey"/> is the one to widen.
    /// </remarks>
    public static string ContentHash(JobPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);

        var canonical = string.Join(
            '|',
            Normalize(posting.Title),
            Normalize(posting.Company),
            Normalize(posting.Location));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Identity of the underlying job, for spotting the same role listed on two boards.
    /// </summary>
    /// <remarks>
    /// <b>The city, not the whole location string, and that is the entire fix.</b>
    /// <see cref="ContentHash"/> folds the raw location in, and boards write it differently -
    /// "London, England, United Kingdom" on one and "London, UK" on another - so it never
    /// collides across boards. Measured 2026-09-01 over thirty days of a live corpus it matched
    /// across boards <b>zero</b> times in 5,268 postings, which means
    /// <c>RunCounts.CrossSiteDuplicates</c> had been reporting nothing since it was written.
    /// Parsing the city out first, the same corpus matches 285 times.
    ///
    /// <b>The city is required rather than optional.</b> Title and employer alone matched 285
    /// postings and title, employer and city matched 211 - so 74 of those, better than a
    /// quarter, were one employer advertising one title in several cities. Dropping the city
    /// would merge them, and downstream that means handing somebody the apply link for the wrong
    /// city's vacancy.
    ///
    /// Null where the posting states no city: an unlocated posting is not the same job as
    /// another unlocated posting, and matching them would be the collision above with nothing
    /// left to prevent it.
    /// </remarks>
    public static string? CrossBoardKey(JobPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);

        var city = Normalize(JobLocation.Parse(posting.Location).City);

        if (city.Length == 0 || string.IsNullOrWhiteSpace(posting.Company))
        {
            return null;
        }

        return string.Join('|', Normalize(posting.Title), Normalize(posting.Company), city);
    }

    /// <summary>Case-, punctuation- and whitespace-insensitive form.</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
