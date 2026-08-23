using System.Globalization;
using System.Text.RegularExpressions;

namespace JobPlatform.Core.Enrichment;

/// <summary>
/// Pulls the sparse facts out of a description — the ones that decide whether someone can
/// take the job at all.
/// </summary>
/// <remarks>
/// These are facts about the <i>offer</i>, not about the candidate. Anything the employer asks
/// the candidate to hold is a concept (see <see cref="ConceptGraph"/>); clearances and degrees
/// live there, not here.
///
/// <b>IR35 earns its place.</b> In the London contract market, inside-IR35 and outside-IR35 are
/// materially different jobs at the same headline day rate, and nothing else we collect
/// distinguishes them. It is the single highest-value thing in this file.
///
/// Every pattern here is deliberately narrow. A tag that fires on a passing mention is worse
/// than a missing tag, because a filter built on it silently returns the wrong postings and
/// nothing about the result looks wrong.
/// </remarks>
public static class PostingTagExtractor
{
    private static readonly (Regex Pattern, string Tag)[] Flags =
    [
        // "we sponsor" and "cannot sponsor" both contain "sponsor", so the negative has to be
        // excluded explicitly rather than left to the positive not matching.
        (New(@"\b(?:visa\s+sponsorship\s+(?:is\s+)?available|we\s+(?:can\s+)?sponsor|sponsorship\s+(?:is\s+)?"
             + @"(?:available|offered|provided)|will\s+sponsor|skilled\s+worker\s+visa)\b"),
         PostingTagNames.VisaSponsorship),

        (New(@"\b(?:share\s+options?|stock\s+options?|equity|rsus?|esop)\b"),
         PostingTagNames.Equity),

        (New(@"\b(?:on[- ]call|out[- ]of[- ]hours\s+support|call[- ]out\s+rota)\b"),
         PostingTagNames.OnCall),

        (New(@"\b(?:shift\s+(?:work|pattern|rota)|rotating\s+shifts?|night\s+shifts?)\b"),
         PostingTagNames.ShiftWork),

        (New(@"\b(?:relocation\s+(?:package|assistance|support|allowance)|"
             + @"we(?:'ll| will)\s+help\s+you\s+relocate)\b"),
         PostingTagNames.RelocationSupport),

        (New(@"\b(?:sign[- ]?on\s+bonus|signing\s+bonus|joining\s+bonus|golden\s+hello)\b"),
         PostingTagNames.SignOnBonus),

        (New(@"\b(?:four[- ]day\s+(?:week|working\s+week)|4[- ]day\s+(?:week|working\s+week)|"
             + @"9[- ]day\s+fortnight)\b"),
         PostingTagNames.FourDayWeek),

        (New(@"\b(?:travel\s+(?:to\s+client\s+sites?|required|is\s+required)|willing(?:ness)?\s+to\s+travel|"
             + @"regular\s+travel)\b"),
         PostingTagNames.TravelRequired),
    ];

    /// <summary>
    /// Inside or outside IR35. The words appear in either order and with either spelling of
    /// the number, which is most of why this is a pattern rather than a substring check.
    /// </summary>
    private static readonly Regex Ir35Pattern = New(
        @"\b(?<a>inside|outside)\s+(?:of\s+)?ir[\s-]?35\b|\bir[\s-]?35[\s:]*(?<b>inside|outside)\b");

    /// <summary>"5% pension", "pension contribution of up to 10%", "matched to 6%".</summary>
    private static readonly Regex PensionPattern = New(
        @"(?:pension[^.]{0,40}?(?<a>\d{1,2}(?:\.\d)?)\s*%)|(?:(?<b>\d{1,2}(?:\.\d)?)\s*%[^.]{0,25}?pension)");

    /// <summary>"25 days holiday", "holiday: 28 days", "25 days annual leave".</summary>
    private static readonly Regex HolidayPattern = New(
        @"(?:(?<a>\d{1,2})\s*days?[^.]{0,25}?(?:holiday|annual\s+leave|paid\s+leave|pto))"
        + @"|(?:(?:holiday|annual\s+leave)[^.]{0,20}?(?<b>\d{1,2})\s*days?)");

    public static IReadOnlyList<PostingTag> Extract(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return [];
        }

        var tags = new List<PostingTag>();

        foreach (var (pattern, tag) in Flags)
        {
            if (pattern.IsMatch(description))
            {
                tags.Add(new PostingTag(tag));
            }
        }

        if (Ir35Pattern.Match(description) is { Success: true } ir35)
        {
            var side = First(ir35, "a", "b");

            if (side is not null)
            {
                tags.Add(new PostingTag(PostingTagNames.Ir35, side.ToLowerInvariant()));
            }
        }

        // Bounded because the pattern can reach across a clause: a 40% bonus mentioned near
        // the word pension is not a pension contribution.
        AddNumeric(tags, PensionPattern, description, PostingTagNames.PensionPercent, 1, 30);

        // 20 is the UK statutory minimum excluding bank holidays; 45 is beyond any real offer.
        AddNumeric(tags, HolidayPattern, description, PostingTagNames.HolidayDays, 18, 45);

        return tags;
    }

    private static void AddNumeric(
        List<PostingTag> tags,
        Regex pattern,
        string description,
        string tag,
        double min,
        double max)
    {
        var match = pattern.Match(description);

        if (!match.Success)
        {
            return;
        }

        var raw = First(match, "a", "b");

        if (raw is not null
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value >= min
            && value <= max)
        {
            tags.Add(new PostingTag(tag, raw));
        }
    }

    private static string? First(Match match, params string[] groups)
    {
        foreach (var name in groups)
        {
            if (match.Groups[name].Success)
            {
                return match.Groups[name].Value;
            }
        }

        return null;
    }

    private static Regex New(string pattern) => new(
        pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
