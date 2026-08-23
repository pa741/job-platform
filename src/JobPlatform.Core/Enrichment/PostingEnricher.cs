using JobPlatform.Core.Model;

namespace JobPlatform.Core.Enrichment;

/// <summary>
/// Runs every deterministic classifier over one posting.
/// </summary>
/// <remarks>
/// Pure and Azure-free, the same shape as <c>MetricsCalculator</c> — which is why the metric
/// surface is fully unit-testable, and why this one is too. It is called from the ingestion
/// pipeline <b>before</b> the upsert: it is in-memory CPU work, so it adds no round trip and
/// does not lengthen the SQL connection.
///
/// The classifiers stay separate rather than being folded in here so each keeps its own test
/// file and its own reasons. This type only decides the <i>order</i> and the <i>precedence</i>
/// between them, which is the part worth reading in one place.
/// </remarks>
public static class PostingEnricher
{
    private const string ClearanceDomain = "type.clearance";
    private const string DegreeDomain = "type.degree";

    public static EnrichedPosting Enrich(JobPosting posting, ConceptGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(posting);

        graph ??= ConceptGraph.Default;

        var (concepts, mentions) = ResolveConcepts(posting, graph);
        var experience = ResolveExperience(posting);
        var employees = EmployeeBandParser.Parse(posting.CompanyNumEmployees);
        var salary = ResolveSalary(posting);

        var arrangement = WorkArrangementClassifier.Classify(
            posting.WorkFromHomeType,
            posting.IsRemote,
            posting.Location,
            posting.Title,
            posting.Description);

        return new EnrichedPosting
        {
            Posting = posting,

            Seniority = SeniorityClassifier.Classify(posting.Title, posting.JobLevel),
            RoleFamily = RoleFamilyClassifier.Classify(posting.Title),

            WorkArrangement = arrangement.Arrangement,
            HybridDaysInOffice = arrangement.HybridDaysInOffice,

            YearsExperienceMin = experience.Min,
            YearsExperienceMax = experience.Max,

            EmployeesMin = employees.Min,
            EmployeesMax = employees.Max,

            AnnualSalaryMin = salary.Min,
            AnnualSalaryMax = salary.Max,
            SalaryCurrency = salary.Currency,
            SalaryFromText = salary.FromText,
            SalaryStatedInterval = salary.Interval,

            CompanyKey = CompanyNormalizer.Key(posting.Company),
            JobTypes = JobTypeNormalizer.Normalize(posting.JobType),

            Concepts = concepts,
            Mentions = mentions,
            Tags = PostingTagExtractor.Extract(posting.Description),

            RequiresSecurityClearance = HasAnyUnder(concepts, graph, ClearanceDomain),
            RequiresDegree = HasAnyUnder(concepts, graph, DegreeDomain),
        };
    }

    /// <summary>
    /// Board-published skills and text matches, kept as separate assertions.
    /// </summary>
    /// <remarks>
    /// A concept found both ways produces two rows, not one, because
    /// <see cref="AssertionSource"/> is part of the assertion's identity. That is deliberate:
    /// an employer tagging a role "Kubernetes" and a description happening to mention it are
    /// different strengths of evidence, and collapsing them would throw away the only thing
    /// that says which. Queries that do not care can group them away; queries that do care
    /// have no way to recover the distinction once it is gone.
    /// </remarks>
    private static (IReadOnlyList<ConceptAssertion> Concepts, IReadOnlyList<UnresolvedMention> Mentions)
        ResolveConcepts(JobPosting posting, ConceptGraph graph)
    {
        var fromText = graph.Resolve(AssertionSource.Taxonomy, posting.Title, posting.Description, posting.Summary);

        if (posting.Skills.Count == 0)
        {
            return (fromText.Assertions, fromText.Mentions);
        }

        var assertions = new List<ConceptAssertion>(fromText.Assertions);
        var mentions = new List<UnresolvedMention>(fromText.Mentions);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var skill in posting.Skills)
        {
            if (string.IsNullOrWhiteSpace(skill))
            {
                continue;
            }

            if (graph.TryResolve(skill, out var concept))
            {
                if (seen.Add(concept.Key))
                {
                    assertions.Add(new ConceptAssertion(
                        concept.Key,
                        AssertionSource.Board,
                        EvidenceText: skill.Trim()));
                }
            }
            else if (seen.Add("?" + skill.Trim()))
            {
                // The employer typed this into the board's own skills field, so it is real
                // evidence even though the vocabulary has no concept for it. Recording it is
                // how the vocabulary finds out what it is missing.
                mentions.Add(new UnresolvedMention(skill.Trim(), MentionReason.UnknownBoardSkill));
            }
        }

        return (assertions, mentions);
    }

    /// <summary>
    /// Published numbers first, then the board's display string, then the description.
    /// </summary>
    /// <remarks>
    /// Three sources in descending order of directness. <c>experience_years_min</c> is what
    /// the board actually had; <c>"3+ Yrs"</c> is that number rendered for a human and parsed
    /// back; the description is a guess from prose. Preferring the first costs nothing and
    /// avoids a round trip that can only lose information.
    /// </remarks>
    private static ExperienceRange ResolveExperience(JobPosting posting)
        => posting.ExperienceYearsMin is not null || posting.ExperienceYearsMax is not null
            ? new ExperienceRange(posting.ExperienceYearsMin, posting.ExperienceYearsMax)
            : ExperienceParser.Parse(posting.ExperienceRange, posting.Description);

    /// <summary>
    /// The board's own salary columns where it filled them, otherwise the description text.
    /// </summary>
    /// <remarks>
    /// Board first, always. A structured field the employer filled in is better evidence than
    /// a figure recovered from prose, and <see cref="EnrichedPosting.SalaryFromText"/> records
    /// which happened so an average can refuse to mix them.
    ///
    /// This fallback is why the class exists: a real London run had <b>0%</b> salary coverage,
    /// because the upstream library gates its own description-based extractor on the country
    /// being the USA. The fork fixes that at source; this covers what has already reached us,
    /// and what still arrives from boards the fix does not touch.
    /// </remarks>
    private static (decimal? Min, decimal? Max, string? Currency, bool FromText, string? Interval)
        ResolveSalary(JobPosting posting)
    {
        var min = SalaryTextParser.Annualise(posting.MinAmount, posting.SalaryInterval);
        var max = SalaryTextParser.Annualise(posting.MaxAmount, posting.SalaryInterval);

        if (min is not null || max is not null)
        {
            return (min, max, posting.Currency, false, posting.SalaryInterval);
        }

        var parsed = SalaryTextParser.Parse(posting.Description);

        return parsed is null
            ? (null, null, null, false, null)
            : (parsed.Value.Min, parsed.Value.Max, parsed.Value.Currency, true, parsed.Value.StatedInterval);
    }

    /// <summary>
    /// Whether any asserted concept sits under the given domain.
    /// </summary>
    /// <remarks>
    /// Asks the closure rather than testing a list of keys, so adding a clearance or a degree
    /// to the vocabulary needs no change here. The alternative — a hardcoded key set in C# —
    /// is a second copy of something the vocabulary already knows, and the two would
    /// eventually disagree.
    /// </remarks>
    private static bool HasAnyUnder(
        IReadOnlyList<ConceptAssertion> concepts,
        ConceptGraph graph,
        string domainKey)
    {
        foreach (var assertion in concepts)
        {
            if (graph.Ancestors(assertion.ConceptKey).ContainsKey(domainKey))
            {
                return true;
            }
        }

        return false;
    }
}
