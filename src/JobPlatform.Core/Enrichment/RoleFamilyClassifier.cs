using JobPlatform.Core.Text;

namespace JobPlatform.Core.Enrichment;

/// <summary>
/// Sorts a title into one <see cref="RoleFamily"/>.
/// </summary>
/// <remarks>
/// Rules are tried in order and the first match wins, so the order encodes which signal
/// dominates when a title carries several. "Cloud Security Engineer" is a security role that
/// happens to be in the cloud, not a platform role; "Machine Learning Data Engineer" is an ML
/// role. Getting that precedence right is most of the work here — the individual keyword
/// lists are the easy part.
///
/// Only the title is read. Descriptions mention every adjacent discipline, so classifying a
/// role from one produces a plausible-looking answer that is wrong more often than
/// <see cref="RoleFamily.Unknown"/> would be.
/// </remarks>
public static class RoleFamilyClassifier
{
    private sealed record Rule(
        RoleFamily Family,
        string[] Tokens,
        (string First, string Second)[] Pairs);

    private static readonly Rule[] Rules =
    [
        // Management first: "Engineering Manager" must not fall through to Backend on the
        // strength of some other word in the title.
        new(RoleFamily.Management,
            [],
            [("engineering", "manager"), ("engineering", "director"), ("head", "engineering"),
             ("director", "engineering"), ("development", "manager"), ("software", "manager"),
             ("delivery", "manager"), ("head", "technology"), ("engineering", "lead")]),

        new(RoleFamily.Design,
            ["designer", "ux", "ui/ux", "design"],
            [("product", "designer"), ("user", "experience")]),

        new(RoleFamily.Product,
            ["po"],
            [("product", "manager"), ("product", "owner"), ("product", "lead"),
             ("technical", "product")]),

        new(RoleFamily.Security,
            ["security", "appsec", "infosec", "cybersecurity", "cyber", "pentester",
             "penetration", "cryptography", "soc"],
            [("penetration", "tester"), ("security", "engineer")]),

        new(RoleFamily.MachineLearning,
            ["ml", "mlops", "nlp", "llm", "ai"],
            [("machine", "learning"), ("deep", "learning"), ("data", "scientist"),
             ("research", "scientist"), ("computer", "vision"), ("applied", "scientist")]),

        new(RoleFamily.Data,
            ["analytics", "analyst", "etl", "dba", "warehouse", "warehousing", "bi"],
            [("data", "engineer"), ("data", "engineering"), ("data", "analyst"),
             ("data", "platform"), ("business", "intelligence"), ("database", "administrator"),
             ("analytics", "engineer")]),

        new(RoleFamily.QA,
            ["qa", "sdet", "tester"],
            [("test", "engineer"), ("test", "automation"), ("quality", "assurance"),
             ("quality", "engineer"), ("automation", "tester")]),

        new(RoleFamily.Embedded,
            ["embedded", "firmware", "fpga", "rtos", "verilog", "vhdl"],
            [("embedded", "software")]),

        new(RoleFamily.Mobile,
            ["mobile", "ios", "android", "flutter"],
            [("react", "native")]),

        new(RoleFamily.Platform,
            ["devops", "sre", "platform", "infrastructure", "infra", "kubernetes", "cloud",
             "systems", "network", "sysadmin"],
            [("site", "reliability"), ("cloud", "engineer"), ("build", "engineer")]),

        // Full stack before either half, or "Full Stack Frontend-leaning Engineer" would
        // resolve to whichever half is named first.
        new(RoleFamily.FullStack,
            ["fullstack"],
            [("full", "stack")]),

        new(RoleFamily.Frontend,
            ["frontend", "react", "angular", "vue", "svelte", "javascript", "typescript", "web"],
            [("front", "end"), ("ui", "engineer")]),

        // "go" is absent deliberately, and so is a bare "Software Engineer". The first is
        // an ordinary English word; the second is the most common title in the corpus and
        // says nothing about the work, so classifying it as Backend would inflate that
        // family with every generic listing. Unknown is the honest answer for both.
        new(RoleFamily.Backend,
            ["backend", "java", "python", "golang", "ruby", "php", "scala", "kotlin",
             "rust", "elixir", "c#", ".net", "dotnet", "node.js", "nodejs", "api",
             "microservices"],
            [("back", "end"), ("server", "side")]),
    ];

    public static RoleFamily Classify(string? title)
    {
        var tokens = TitleTokenizer.Tokenize(title).ToArray();

        if (tokens.Length == 0)
        {
            return RoleFamily.Unknown;
        }

        var tokenSet = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);

        foreach (var rule in Rules)
        {
            if (rule.Tokens.Any(tokenSet.Contains) || MatchesPair(tokens, rule.Pairs))
            {
                return rule.Family;
            }
        }

        return RoleFamily.Unknown;
    }

    private static bool MatchesPair(string[] tokens, (string First, string Second)[] pairs)
    {
        if (pairs.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            foreach (var (first, second) in pairs)
            {
                if (string.Equals(tokens[i], first, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(tokens[i + 1], second, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
