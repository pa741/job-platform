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

        var city = CanonicalCity(Normalize(JobLocation.Parse(posting.Location).City));

        if (city.Length == 0 || string.IsNullOrWhiteSpace(posting.Company))
        {
            return null;
        }

        return string.Join('|', Normalize(posting.Title), Normalize(posting.Company), city);
    }

    /// <summary>
    /// <see cref="CrossBoardKey"/> as the fixed-width value the column stores.
    /// </summary>
    /// <remarks>
    /// <b>The key is stored hashed because the readable form cannot be indexed.</b> Title,
    /// employer and city against this schema's own widths reach 952 characters, which is 1,904
    /// bytes, and SQL Server caps a nonclustered index key at 1,700 - the same arithmetic already
    /// recorded on the <c>(Company, LocationCity)</c> index. So the column is <c>char(64)</c> and
    /// this is what may be written to it.
    ///
    /// <b>It exists so that the two writers cannot disagree.</b> Ingest stamps the key on every
    /// upsert and the operator backfill stamps it on the corpus that predates the column; those
    /// are different code paths reaching the same rows, and a second spelling of the hash would
    /// silently split one cluster into two. Nothing else may hash this key.
    ///
    /// Null propagates rather than hashing the empty string: a posting with no city and a posting
    /// with no employer are not the same job, and giving them a shared key would recreate exactly
    /// the collision <see cref="CrossBoardKey"/> answers null to avoid.
    /// </remarks>
    public static string? CrossBoardKeyHash(JobPosting posting)
    {
        var key = CrossBoardKey(posting);

        return key is null
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// One spelling for a city that boards write several ways.
    /// </summary>
    /// <remarks>
    /// <b>The same fault as the one that made this key parse a city at all, one level down.</b>
    /// <see cref="ContentHash"/> folded in the raw location and never collided across boards
    /// because "London, England, United Kingdom" and "London, UK" are different strings. Parsing
    /// the city fixed that and left a smaller version of it: the parsed city itself arrives as
    /// <c>London</c>, <c>London Area</c>, <c>Greater London</c> and <c>City Of London</c> - 4,323,
    /// 1,542, 322 and 66 postings of one place, with "London Area" a LinkedIn spelling and
    /// "Greater London" an Indeed one. Cloudflare's VoidZero Engineer sat in the shortlist twice
    /// on exactly that difference.
    ///
    /// <b>Three general rules rather than a list of cities.</b> "Greater X", "X Area" and "City of
    /// X" are how boards write a metropolitan area, not names anybody uses, so folding them needs
    /// no gazetteer and keeps working for Manchester and Birmingham. Measured over the corpus this
    /// merges 105 further groups whose employer and title are already byte-identical, which is the
    /// evidence for it: they differ in nothing but the spelling of one city.
    ///
    /// <b>What is deliberately NOT folded is the larger half.</b> Seniority stays part of the
    /// title. Harnham advertised requisition 197637 four times - Junior, plain, Senior and Lead -
    /// and the specification that asked for this read the middle two as one job listed twice.
    /// They are a ladder, and merging them hides a rung the candidate might have wanted. Corpus
    /// wide there are 127 "Senior X"/"X" pairs at one employer and city, against the 74 bad merges
    /// that were reason enough to keep the city required in the first place. Nor is a shared
    /// parenthesised number a requisition: EWOR's is <c>(100 % remote)</c>, and matching on it
    /// would merge a Fintech AI/ML Engineer with an AI Infrastructure Cloud Engineer.
    /// </remarks>
    private static string CanonicalCity(string city)
    {
        if (city.Length == 0)
        {
            return city;
        }

        // Normalize has already lowercased, stripped punctuation and collapsed whitespace, so
        // "City Of London" arrives as "city of london" and these compare without further work.
        if (city.StartsWith("greater ", StringComparison.Ordinal))
        {
            return city["greater ".Length..];
        }

        if (city.StartsWith("city of ", StringComparison.Ordinal))
        {
            return city["city of ".Length..];
        }

        return city.EndsWith(" area", StringComparison.Ordinal)
            ? city[..^" area".Length]
            : city;
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
