using System.Text;

namespace JobPlatform.Core.Enrichment;

/// <summary>
/// Folds a company name to a key stable enough to group by.
/// </summary>
/// <remarks>
/// Boards do not agree on how to write a company's name. "Contoso Ltd", "Contoso Limited"
/// and "Contoso" are three rows in every ranking today, because the only folding that exists
/// is <c>JobFingerprint.Normalize</c>, which lowercases and strips punctuation but keeps the
/// legal suffix. That splits a single employer's demand across three lines of the
/// <c>topCompanies</c> chart and makes "who is hiring most" wrong.
///
/// Suffix stripping stops at the legal form. A geographic qualifier is left alone —
/// "Contoso UK" and "Contoso GmbH" plausibly are different hiring entities with different
/// pay and different offices, and merging them would destroy a distinction the data actually
/// contains. This folds spelling, not corporate structure.
/// </remarks>
public static class CompanyNormalizer
{
    /// <summary>
    /// Legal forms, longest first so "co ltd" is removed whole rather than leaving "co".
    /// Repeatedly stripped, because "Contoso Holdings Ltd" carries two.
    /// </summary>
    private static readonly string[] LegalSuffixes =
    [
        "public limited company",
        "limited liability partnership",
        "incorporated",
        "corporation",
        "holdings",
        "holding",
        "company",
        "limited",
        "group",
        "gmbh",
        "s.a.r.l",
        "sarl",
        "b.v",
        "bv",
        "n.v",
        "nv",
        "a/s",
        "oy",
        "ab",
        "as",
        "sa",
        "ag",
        "srl",
        "spa",
        "pty",
        "plc",
        "llp",
        "llc",
        "ltd",
        "inc",
        "corp",
        "co",
    ];

    /// <summary>
    /// A key suitable for grouping, or null when the name was absent or folded away entirely.
    /// </summary>
    public static string? Key(string? company)
    {
        if (string.IsNullOrWhiteSpace(company))
        {
            return null;
        }

        var folded = Fold(company);

        // Strip repeatedly: "Contoso Holdings Ltd" carries two legal forms, and one pass
        // would leave "contoso holdings".
        bool stripped;

        do
        {
            stripped = false;

            foreach (var suffix in LegalSuffixes)
            {
                if (folded.Length > suffix.Length + 1
                    && folded.EndsWith(' ' + suffix, StringComparison.Ordinal))
                {
                    folded = folded[..^(suffix.Length + 1)].TrimEnd();
                    stripped = true;
                    break;
                }
            }
        }
        while (stripped);

        return folded.Length == 0 ? null : folded;
    }

    /// <summary>
    /// Lower-cased, punctuation collapsed to single spaces.
    /// </summary>
    /// <remarks>
    /// Same shape as <c>JobFingerprint.Normalize</c> rather than a call to it: that one is
    /// the content hash's definition of equality and is load-bearing for cross-board
    /// matching, so the two must be free to diverge. Sharing the method would make a change
    /// to company grouping silently rewrite every posting's identity.
    /// </remarks>
    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;

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
