using JobPlatform.Core.Text;

namespace JobPlatform.Core.Enrichment;

/// <summary>
/// Reconciles the boards' three different seniority vocabularies, and infers one from the
/// title where no board supplied it at all.
/// </summary>
/// <remarks>
/// Three inputs have to agree on one scale. LinkedIn publishes its own labels
/// (<c>entry level</c>, <c>mid senior level</c>, <c>associate</c>), freehire publishes a
/// facet value, and Indeed publishes nothing — which is most of the corpus, so the title
/// path is the common case rather than the fallback.
///
/// Where two signals disagree the higher wins. That is not arbitrary: LinkedIn's
/// <c>mid senior level</c> is a single label spanning two of our levels, so reading it as
/// <see cref="Seniority.Mid"/> alone would file every "Senior Engineer" carrying it one step
/// too low. Taking the maximum lets the title correct the label upward without a special
/// case for it.
/// </remarks>
public static class SeniorityClassifier
{
    /// <summary>
    /// Single tokens that name a level outright. Ordered by nothing — the highest match
    /// wins regardless of position, because a title naming two levels ("Senior/Lead
    /// Engineer") is offering a range and the top of it is the role being advertised.
    /// </summary>
    private static readonly Dictionary<string, Seniority> TokenLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["intern"] = Seniority.Intern,
        ["internship"] = Seniority.Intern,
        ["placement"] = Seniority.Intern,
        ["apprentice"] = Seniority.Intern,
        ["apprenticeship"] = Seniority.Intern,

        ["junior"] = Seniority.Junior,
        ["jnr"] = Seniority.Junior,
        ["jr"] = Seniority.Junior,
        ["graduate"] = Seniority.Junior,
        ["grad"] = Seniority.Junior,
        ["entry"] = Seniority.Junior,
        ["trainee"] = Seniority.Junior,

        ["mid"] = Seniority.Mid,
        ["midlevel"] = Seniority.Mid,
        ["intermediate"] = Seniority.Mid,
        ["associate"] = Seniority.Mid,
        ["ii"] = Seniority.Mid,

        ["senior"] = Seniority.Senior,
        ["snr"] = Seniority.Senior,
        ["sr"] = Seniority.Senior,
        ["iii"] = Seniority.Senior,

        ["lead"] = Seniority.Lead,
        ["leader"] = Seniority.Lead,
        ["staff"] = Seniority.Lead,

        ["principal"] = Seniority.Principal,
        ["distinguished"] = Seniority.Principal,
        ["architect"] = Seniority.Principal,
        ["head"] = Seniority.Principal,
        ["director"] = Seniority.Principal,

        ["vp"] = Seniority.Executive,
        ["svp"] = Seniority.Executive,
        ["evp"] = Seniority.Executive,
        ["chief"] = Seniority.Executive,
        ["cto"] = Seniority.Executive,
        ["ceo"] = Seniority.Executive,
        ["cio"] = Seniority.Executive,
        ["ciso"] = Seniority.Executive,
        ["executive"] = Seniority.Executive,
    };

    /// <summary>
    /// Pairs that mean something their parts do not.
    /// </summary>
    /// <remarks>
    /// <c>manager</c> is absent from <see cref="TokenLevels"/> for exactly this reason: on
    /// its own it is not a seniority at all. "Product Manager" and "Account Manager" are
    /// ordinary individual-contributor roles, and mapping the bare token would promote every
    /// one of them to <see cref="Seniority.Lead"/>. Only the engineering-management phrasings
    /// carry the level, so only those are listed.
    /// </remarks>
    private static readonly Dictionary<(string First, string Second), Seniority> PairLevels = new()
    {
        [("engineering", "manager")] = Seniority.Lead,
        [("software", "manager")] = Seniority.Lead,
        [("development", "manager")] = Seniority.Lead,
        [("technical", "manager")] = Seniority.Lead,
        [("delivery", "manager")] = Seniority.Lead,
        [("engineering", "director")] = Seniority.Principal,
        [("vice", "president")] = Seniority.Executive,
    };

    /// <summary>
    /// The boards' own labels. Values are matched against the whole field rather than
    /// tokenised, because they are controlled vocabularies rather than prose.
    /// </summary>
    private static readonly Dictionary<string, Seniority> BoardLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        // LinkedIn
        ["internship"] = Seniority.Intern,
        ["entry level"] = Seniority.Junior,
        ["associate"] = Seniority.Mid,
        // LinkedIn's single label spans two of our levels; the title raises it where it can.
        ["mid senior level"] = Seniority.Mid,
        ["mid-senior level"] = Seniority.Mid,
        ["director"] = Seniority.Principal,
        ["executive"] = Seniority.Executive,
        // "not applicable" is LinkedIn for "we did not say" and must stay Unknown.

        // freehire facet vocabulary
        ["intern"] = Seniority.Intern,
        ["junior"] = Seniority.Junior,
        ["mid"] = Seniority.Mid,
        ["senior"] = Seniority.Senior,
        ["lead"] = Seniority.Lead,
        ["staff"] = Seniority.Lead,
        ["principal"] = Seniority.Principal,
    };

    /// <summary>The higher of what the board said and what the title implies.</summary>
    public static Seniority Classify(string? title, string? boardJobLevel)
    {
        var fromBoard = FromBoard(boardJobLevel);
        var fromTitle = FromTitle(title);

        return fromBoard >= fromTitle ? fromBoard : fromTitle;
    }

    private static Seniority FromBoard(string? boardJobLevel)
        => !string.IsNullOrWhiteSpace(boardJobLevel)
            && BoardLevels.TryGetValue(boardJobLevel.Trim(), out var level)
            ? level
            : Seniority.Unknown;

    private static Seniority FromTitle(string? title)
    {
        var tokens = TitleTokenizer.Tokenize(title).ToArray();
        var best = Seniority.Unknown;

        foreach (var token in tokens)
        {
            if (TokenLevels.TryGetValue(token, out var level) && level > best)
            {
                best = level;
            }
        }

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (PairLevels.TryGetValue((tokens[i], tokens[i + 1]), out var level) && level > best)
            {
                best = level;
            }
        }

        return best;
    }
}
