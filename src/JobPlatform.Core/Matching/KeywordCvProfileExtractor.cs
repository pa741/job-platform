using System.Text.RegularExpressions;
using JobPlatform.Core.Text;

namespace JobPlatform.Core.Matching;

/// <summary>
/// Extracts a profile without calling anything. Deliberately unclever: it recognises skills
/// from a lexicon and years from a regex, and is honest about knowing nothing else.
/// </summary>
/// <remarks>
/// This is the default because a prototype that cannot parse a CV without credentials is a
/// prototype nobody can run. A model-backed extractor implementing the same interface can
/// replace it without the pipeline noticing.
/// </remarks>
public sealed partial class KeywordCvProfileExtractor : ICvProfileExtractor
{
    /// <summary>
    /// Multi-word entries are matched as phrases before tokenisation, because "machine
    /// learning" tokenises into two words that individually mean much less.
    /// </summary>
    private static readonly string[] SkillLexicon =
    [
        "c#", ".net", "asp.net", "dotnet", "f#", "java", "kotlin", "scala", "python", "go",
        "golang", "rust", "typescript", "javascript", "node", "nodejs", "react", "angular",
        "vue", "svelte", "php", "ruby", "rails", "swift", "objective-c", "c++", "elixir",
        "sql", "t-sql", "postgres", "postgresql", "mysql", "sqlite", "oracle", "mongodb",
        "cosmos db", "redis", "cassandra", "elasticsearch", "kafka", "rabbitmq",
        "azure", "aws", "gcp", "kubernetes", "docker", "terraform", "bicep", "ansible",
        "helm", "serverless", "microservices", "ci/cd", "devops", "linux", "bash",
        "powershell", "git", "github actions", "jenkins", "gitlab",
        "entity framework", "ef core", "dapper", "hibernate", "spring", "django", "flask",
        "fastapi", "graphql", "rest", "grpc", "signalr", "openapi",
        "machine learning", "deep learning", "nlp", "pytorch", "tensorflow", "pandas",
        "numpy", "spark", "databricks", "airflow", "etl", "data engineering",
        "agile", "scrum", "kanban", "tdd", "ddd", "solid",
        "html", "css", "sass", "tailwind", "figma", "accessibility",
    ];

    [GeneratedRegex(@"(\d{1,2})\+?\s*(?:years|yrs|år|años)", RegexOptions.IgnoreCase, 500)]
    private static partial Regex YearsPattern { get; }

    [GeneratedRegex(@"\b(remote|hybrid|work from home|wfh)\b", RegexOptions.IgnoreCase, 500)]
    private static partial Regex RemotePattern { get; }

    public CvProfile Extract(string cvText)
    {
        ArgumentNullException.ThrowIfNull(cvText);

        var lowered = cvText.ToLowerInvariant();
        var tokens = TitleTokenizer.Tokenize(cvText).ToList();
        var tokenSet = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);

        var skills = SkillLexicon
            .Where(skill => skill.Contains(' ') || skill.Contains('.') || skill.Contains('#') || skill.Contains('+')
                // Punctuated and multi-word skills survive tokenisation poorly, so they are
                // matched against the raw text; plain words go through the token set to
                // avoid "go" matching inside "google".
                ? lowered.Contains(skill, StringComparison.Ordinal)
                : tokenSet.Contains(skill))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var yearsMatch = YearsPattern.Matches(cvText)
            .Select(m => double.TryParse(m.Groups[1].Value, out var y) ? y : (double?)null)
            .Where(y => y is > 0 and < 60)
            .DefaultIfEmpty(null)
            .Max();

        return new CvProfile
        {
            RawText = cvText,
            Skills = skills,
            Tokens = tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            YearsExperience = yearsMatch,
            PrefersRemote = RemotePattern.IsMatch(cvText) ? true : null,
        };
    }
}
