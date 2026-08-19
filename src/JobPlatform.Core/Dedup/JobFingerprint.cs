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
