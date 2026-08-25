using JobPlatform.Core.Enrichment;

namespace JobPlatform.Core.Matching;

/// <summary>
/// Scores one candidate against one posting, deterministically.
/// </summary>
/// <remarks>
/// <b>This runs on everything; the model runs on what survives it.</b> A corpus-wide pass has
/// tens of thousands of pairs in it and almost all of them are obvious rejections. Asking a
/// model to read each one would cost real money to be told what a join already knows, so the
/// arithmetic here does the elimination and the model is spent only on the shortlist - which is
/// also the only place a judgement is worth paying for.
///
/// Pure and Azure-free, like <c>MetricsCalculator</c>, and for the same reason: these rules are
/// the part most worth pinning down exactly, and they are only pinnable while running them
/// needs nothing but a graph and two records.
///
/// The scorer never invents evidence. Every point it awards traces to an assertion on one side
/// and an assertion or a curated edge on the other, which is what makes
/// <see cref="MatchResult.Matched"/> presentable to the candidate rather than merely
/// diagnostic. It also never awards points for a posting it cannot read: the concept axes are
/// the substance of a match, and a posting answering neither of them scores zero rather than
/// inheriting a perfect score from whichever peripheral axis happened to agree.
/// </remarks>
public static class MatchScorer
{
    // Nominal weights. What each axis actually carries for a given pair is in
    // MatchComponent.Weight - an axis the posting cannot answer drops to zero and the rest
    // renormalise. See MatchResult for why that is the right treatment of silence.
    private const double RequiredSkillsWeight = 0.40;
    private const double PreferredSkillsWeight = 0.15;
    private const double SeniorityWeight = 0.15;
    private const double ExperienceWeight = 0.10;
    private const double ArrangementWeight = 0.10;
    private const double SalaryWeight = 0.05;
    private const double LocationWeight = 0.05;

    /// <summary>
    /// What every axis would carry if a posting answered all of them.
    /// </summary>
    /// <remarks>
    /// The denominator for <see cref="MatchResult.Coverage"/>. Written as the sum rather than
    /// as a literal 1.0 so that adding an axis cannot silently make coverage exceed one.
    /// </remarks>
    private const double NominalWeight =
        RequiredSkillsWeight + PreferredSkillsWeight + SeniorityWeight + ExperienceWeight
        + ArrangementWeight + SalaryWeight + LocationWeight;

    /// <summary>Credit a held concept earns, by how it relates to the required one.</summary>
    private const double SpecialisationCredit = 1.00;
    private const double ImpliedCredit = 1.00;
    private const double GeneralisationCredit = 0.45;
    private const double RelatedCredit = 0.30;
    private const double SupersededCredit = 0.25;

    /// <summary>Lost per level of distance when the candidate holds something broader.</summary>
    private const double GeneralisationDecay = 0.15;

    public static MatchResult Score(CandidateFacts candidate, PostingFacts posting, ConceptGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(posting);

        graph ??= ConceptGraph.Default;

        var held = BuildHeldIndex(candidate.Concepts);

        var required = new List<ConceptAssertion>();
        var preferred = new List<ConceptAssertion>();

        foreach (var demand in Deduplicate(posting.Concepts))
        {
            // Unspecified is by far the most common polarity: only the model pass can tell
            // essential from desirable, and it has not necessarily run. Treating it as
            // required would make every text-matched mention a hard requirement and score
            // most of the corpus at zero, so it lands with the preferred set instead - a real
            // signal, weighted like the softer one it is.
            if (demand.Polarity == AssertionPolarity.Required)
            {
                required.Add(demand);
            }
            else
            {
                preferred.Add(demand);
            }
        }

        var matches = new List<ConceptMatch>();
        var gaps = new List<ConceptGap>();

        var requiredScore = ScoreConcepts(required, held, graph, matches, gaps);
        var preferredScore = ScoreConcepts(preferred, held, graph, matches, gaps);

        var components = new List<MatchComponent>
        {
            new(MatchComponent.RequiredSkills, requiredScore ?? 0, requiredScore is null ? 0 : RequiredSkillsWeight),
            new(MatchComponent.PreferredSkills, preferredScore ?? 0, preferredScore is null ? 0 : PreferredSkillsWeight),
        };

        Add(components, MatchComponent.Seniority, ScoreSeniority(candidate, posting), SeniorityWeight);
        Add(components, MatchComponent.Experience, ScoreExperience(candidate, posting), ExperienceWeight);
        Add(components, MatchComponent.WorkArrangement, ScoreArrangement(candidate, posting), ArrangementWeight);
        Add(components, MatchComponent.Location, ScoreLocation(candidate, posting), LocationWeight);

        // The salary axis carries less weight when the figure came out of prose, because it is
        // a weaker number - not because it matters less to the candidate.
        Add(
            components,
            MatchComponent.Salary,
            ScoreSalary(candidate, posting),
            posting.SalaryFromText ? SalaryWeight / 2 : SalaryWeight);

        var totalWeight = components.Sum(c => c.Weight);

        matches.Sort((a, b) => b.Credit.CompareTo(a.Credit));
        gaps.Sort((a, b) => b.Demand.CompareTo(a.Demand));

        // What share of a full assessment this posting actually supported. Reported rather
        // than multiplied into the score - see MatchResult.Coverage for why.
        var coverage = totalWeight / NominalWeight;

        // The floor under "silence drops an axis". Dropping axes is right until nothing
        // substantive is left: a posting with no readable requirements, scored on the city it
        // happens to be in, otherwise comes out at 100 and outranks roles the candidate
        // genuinely fits. Measured against the real corpus this was not an edge case - 44 of
        // the top 60 matches had no skills axis at all, and 13 rested on location alone.
        //
        // Zero rather than null, and deliberately: a posting nothing can be said about must
        // sort below every posting something can be said about, which is the same reason
        // Seniority.Unknown is zero. Coverage is what distinguishes "scored badly" from
        // "could not be scored", so the distinction survives rather than being collapsed.
        var hasConceptEvidence = components.Any(c =>
            c.Weight > 0
            && (c.Name == MatchComponent.RequiredSkills || c.Name == MatchComponent.PreferredSkills));

        var total = !hasConceptEvidence || totalWeight <= 0
            ? 0
            : components.Sum(c => c.Score * c.Weight) / totalWeight;

        return new MatchResult
        {
            Score = (int)Math.Round(Math.Clamp(total, 0, 1) * 100, MidpointRounding.AwayFromZero),
            Coverage = Math.Clamp(coverage, 0, 1),
            Components = components,
            Matched = matches,
            Gaps = gaps,
        };
    }

    private static void Add(List<MatchComponent> components, string name, double? score, double weight)
        => components.Add(new MatchComponent(name, score ?? 0, score is null ? 0 : weight));

    // -----------------------------------------------------------------------
    // Concepts
    // -----------------------------------------------------------------------

    /// <summary>
    /// The candidate's strongest claim per concept, indexed for lookup.
    /// </summary>
    /// <remarks>
    /// Strongest wins because the same concept legitimately arrives twice: declared on the form
    /// and extracted from the prose. Averaging them would let a model's cautious reading dilute
    /// an explicit claim, and keeping both would double-count a single skill against a single
    /// requirement.
    /// </remarks>
    private static Dictionary<string, ConceptAssertion> BuildHeldIndex(IReadOnlyList<ConceptAssertion> concepts)
    {
        var index = new Dictionary<string, ConceptAssertion>(StringComparer.Ordinal);

        foreach (var assertion in concepts)
        {
            if (!index.TryGetValue(assertion.ConceptKey, out var existing)
                || Strength(assertion) > Strength(existing))
            {
                index[assertion.ConceptKey] = assertion;
            }
        }

        return index;
    }

    /// <summary>
    /// Null when the posting asked for nothing at this strength, so the axis drops out.
    /// </summary>
    private static double? ScoreConcepts(
        List<ConceptAssertion> demands,
        Dictionary<string, ConceptAssertion> held,
        ConceptGraph graph,
        List<ConceptMatch> matches,
        List<ConceptGap> gaps)
    {
        if (demands.Count == 0)
        {
            return null;
        }

        var earned = 0.0;

        foreach (var demand in demands)
        {
            var best = BestMatch(demand, held, graph);

            if (best is { } match && match.Credit > 0)
            {
                matches.Add(match);
                earned += match.Credit;
            }
            else
            {
                gaps.Add(new ConceptGap(demand.ConceptKey, demand.Polarity, demand.YearsMin));
            }
        }

        return earned / demands.Count;
    }

    /// <summary>
    /// The best claim the candidate can make against one requirement.
    /// </summary>
    /// <remarks>
    /// Ordered by how honest the claim is, not by how much it scores: an exact match is taken
    /// over a specialisation of equal credit so the relation reported back to the candidate is
    /// the truest available one. Only the credit is compared after that.
    /// </remarks>
    private static ConceptMatch? BestMatch(
        ConceptAssertion demand,
        Dictionary<string, ConceptAssertion> held,
        ConceptGraph graph)
    {
        if (held.TryGetValue(demand.ConceptKey, out var exact))
        {
            return Build(demand, exact, MatchRelation.Exact, 1.0);
        }

        ConceptMatch? best = null;

        foreach (var (heldKey, heldAssertion) in held)
        {
            var (relation, baseCredit) = Relate(heldKey, demand.ConceptKey, graph);

            if (baseCredit <= 0)
            {
                continue;
            }

            var candidate = Build(demand, heldAssertion, relation, baseCredit);

            if (best is null || candidate.Credit > best.Value.Credit)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// How a held key relates to a required one, and what that relation is worth before the
    /// candidate's own strength is applied.
    /// </summary>
    private static (MatchRelation Relation, double Credit) Relate(
        string heldKey, string requiredKey, ConceptGraph graph)
    {
        // Narrower than what was asked for: the candidate holds PostgreSQL, the posting wants
        // SQL. Full credit - the specific case entails the general one, whatever the distance.
        var heldAncestors = graph.Ancestors(heldKey);

        if (heldAncestors.ContainsKey(requiredKey))
        {
            return (MatchRelation.Specialisation, SpecialisationCredit);
        }

        if (graph.TryGet(heldKey, out var heldConcept))
        {
            // A curated implication. Kubernetes implies containerisation, and the edge exists
            // because someone decided it does - which is better evidence than any distance
            // metric over the hierarchy.
            if (heldConcept.Implies.Contains(requiredKey, StringComparer.Ordinal))
            {
                return (MatchRelation.Implied, ImpliedCredit);
            }

            if (string.Equals(heldConcept.SucceededBy, requiredKey, StringComparison.Ordinal))
            {
                return (MatchRelation.Superseded, SupersededCredit);
            }

            if (heldConcept.Related.Contains(requiredKey, StringComparer.Ordinal))
            {
                return (MatchRelation.Related, RelatedCredit);
            }
        }

        // Related is curated as a single edge but means a symmetric thing, so it is read from
        // both ends. Succession is not read backwards on purpose: holding Angular says nothing
        // about AngularJS, and the direction is the entire point of the edge.
        if (graph.TryGet(requiredKey, out var requiredConcept)
            && requiredConcept.Related.Contains(heldKey, StringComparer.Ordinal))
        {
            return (MatchRelation.Related, RelatedCredit);
        }

        // Broader than what was asked for: the candidate holds SQL, the posting wants
        // PostgreSQL. Partial, and it decays, because two steps up the hierarchy is a much
        // weaker claim than one.
        var requiredAncestors = graph.Ancestors(requiredKey);

        if (requiredAncestors.TryGetValue(heldKey, out var depth))
        {
            var credit = GeneralisationCredit - (GeneralisationDecay * Math.Max(0, depth - 1));
            return (MatchRelation.Generalisation, Math.Max(0, credit));
        }

        return (MatchRelation.Exact, 0);
    }

    private static ConceptMatch Build(
        ConceptAssertion demand, ConceptAssertion heldAssertion, MatchRelation relation, double baseCredit)
    {
        var credit = baseCredit * StrengthFactor(heldAssertion.Polarity) * YearsFactor(demand, heldAssertion);

        return new ConceptMatch(
            demand.ConceptKey,
            heldAssertion.ConceptKey,
            relation,
            Math.Clamp(credit, 0, 1),
            demand.Polarity);
    }

    /// <summary>
    /// What the candidate's own stated strength does to the credit.
    /// </summary>
    /// <remarks>
    /// Expert does not exceed Proficient. A requirement can be met but not over-met: rewarding
    /// depth beyond what was asked for would rank a specialist above a good fit on every
    /// posting, which is not what a job match is measuring. Familiar is discounted because it
    /// is the candidate saying so themselves.
    /// </remarks>
    private static double StrengthFactor(AssertionPolarity polarity) => polarity switch
    {
        AssertionPolarity.Expert => 1.0,
        AssertionPolarity.Proficient => 1.0,
        AssertionPolarity.Familiar => 0.65,

        // Extracted from prose without a stated level. Neither claimed nor discounted into
        // uselessness - most of a real profile lands here.
        _ => 0.85,
    };

    /// <summary>
    /// Discount where the posting attached a number of years to this skill and the candidate
    /// falls short of it.
    /// </summary>
    /// <remarks>
    /// Proportional rather than a cliff. Five years asked and four held is very nearly a match;
    /// five asked and one held is not, and a threshold would have to call both the same. Where
    /// either side is silent this is a no-op, which is the usual case.
    /// </remarks>
    private static double YearsFactor(ConceptAssertion demand, ConceptAssertion heldAssertion)
    {
        if (demand.YearsMin is not { } wanted || wanted <= 0)
        {
            return 1.0;
        }

        if (heldAssertion.YearsMin is not { } has)
        {
            // The posting asked for a number and the profile does not give one. Not a gap -
            // the candidate may well have the years - but not a clean match either.
            return 0.9;
        }

        return has >= wanted ? 1.0 : Math.Max(0.3, (double)has / wanted);
    }

    private static int Strength(ConceptAssertion assertion) => assertion.Polarity switch
    {
        AssertionPolarity.Expert => 3,
        AssertionPolarity.Proficient => 2,
        AssertionPolarity.Familiar => 1,
        _ => 0,
    };

    /// <summary>
    /// One assertion per concept on the demand side, keeping the strongest.
    /// </summary>
    /// <remarks>
    /// The posting side stores a row per source, so a concept the board tagged and the
    /// description also mentioned arrives twice by design. Scoring both would weight that
    /// requirement double for no reason other than how thoroughly it was recorded.
    /// </remarks>
    private static IEnumerable<ConceptAssertion> Deduplicate(IReadOnlyList<ConceptAssertion> concepts)
        => concepts
            .GroupBy(c => c.ConceptKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => (int)c.Polarity).First());

    // -----------------------------------------------------------------------
    // Non-concept axes. Each returns null where the posting is silent.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Distance on the seniority ladder, asymmetric.
    /// </summary>
    /// <remarks>
    /// Being under-levelled costs more than being over-levelled, because the two failures are
    /// not alike: a mid engineer applying to a principal role is usually filtered out, while a
    /// principal applying to a senior role is usually just a step down. Neither is fatal, so
    /// neither floors at zero.
    /// </remarks>
    private static double? ScoreSeniority(CandidateFacts candidate, PostingFacts posting)
    {
        if (posting.Seniority == Seniority.Unknown || candidate.Seniority == Seniority.Unknown)
        {
            return null;
        }

        var distance = (int)candidate.Seniority - (int)posting.Seniority;

        return distance switch
        {
            0 => 1.0,
            > 0 => Math.Max(0.4, 1.0 - (0.15 * distance)),
            _ => Math.Max(0.1, 1.0 - (0.30 * -distance)),
        };
    }

    private static double? ScoreExperience(CandidateFacts candidate, PostingFacts posting)
    {
        if (posting.YearsExperienceMin is not { } wanted || candidate.YearsExperience is not { } has)
        {
            return null;
        }

        if (has >= wanted)
        {
            return 1.0;
        }

        // Same proportional shape as the per-skill years discount, and floored for the same
        // reason: a shortfall is a caveat, not a disqualification, and the model pass is what
        // decides whether this particular one matters.
        return wanted <= 0 ? 1.0 : Math.Max(0.2, (double)has / wanted);
    }

    /// <summary>
    /// Where the work happens against where the candidate will work.
    /// </summary>
    /// <remarks>
    /// Remote satisfies a hybrid preference (fewer days in, not more) but hybrid does not
    /// satisfy a remote one. Where both are hybrid, the posting's stated days in the office are
    /// checked against the candidate's ceiling - the one place this axis can fail two otherwise
    /// identical arrangements against each other.
    /// </remarks>
    private static double? ScoreArrangement(CandidateFacts candidate, PostingFacts posting)
    {
        if (posting.WorkArrangement == WorkArrangement.Unknown
            || candidate.PreferredArrangement == WorkArrangement.Unknown)
        {
            return null;
        }

        if (posting.WorkArrangement == candidate.PreferredArrangement)
        {
            return posting.WorkArrangement == WorkArrangement.Hybrid
                ? ScoreOfficeDays(candidate, posting)
                : 1.0;
        }

        return (candidate.PreferredArrangement, posting.WorkArrangement) switch
        {
            (WorkArrangement.Hybrid, WorkArrangement.Remote) => 0.9,
            (WorkArrangement.Hybrid, WorkArrangement.OnSite) => 0.4,
            (WorkArrangement.OnSite, WorkArrangement.Hybrid) => 0.8,
            (WorkArrangement.OnSite, WorkArrangement.Remote) => 0.3,
            (WorkArrangement.Remote, WorkArrangement.Hybrid) => 0.3,
            (WorkArrangement.Remote, WorkArrangement.OnSite) => 0.0,
            _ => 0.5,
        };
    }

    private static double ScoreOfficeDays(CandidateFacts candidate, PostingFacts posting)
    {
        if (candidate.MaxDaysInOffice is not { } ceiling || posting.HybridDaysInOffice is not { } days)
        {
            return 1.0;
        }

        return days <= ceiling ? 1.0 : Math.Max(0.2, 1.0 - (0.3 * (days - ceiling)));
    }

    /// <summary>
    /// The posting's ceiling against the candidate's floor.
    /// </summary>
    /// <remarks>
    /// Read against <see cref="PostingFacts.AnnualSalaryMax"/>, not the midpoint: the question
    /// is whether this role can pay what the candidate needs at all, and a band's top is the
    /// answer to that. Mismatched currencies drop the axis rather than guessing at a rate -
    /// converting silently would produce a confident number from an invented one.
    /// </remarks>
    private static double? ScoreSalary(CandidateFacts candidate, PostingFacts posting)
    {
        if (candidate.MinimumSalary is not { } floor || floor <= 0)
        {
            return null;
        }

        var offered = posting.AnnualSalaryMax ?? posting.AnnualSalaryMin;

        if (offered is not { } ceiling || ceiling <= 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(candidate.SalaryCurrency)
            && !string.IsNullOrWhiteSpace(posting.SalaryCurrency)
            && !string.Equals(candidate.SalaryCurrency, posting.SalaryCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (ceiling >= floor)
        {
            return 1.0;
        }

        return Math.Max(0, (double)(ceiling / floor));
    }

    /// <summary>
    /// Same city, same country, or neither.
    /// </summary>
    /// <remarks>
    /// Coarse on purpose: the posting's location is a free-text field a board filled in, and
    /// anything finer than these three answers would be precision the input cannot support.
    /// A remote posting drops the axis entirely - where the candidate lives is not a fact about
    /// a remote role - and willingness to relocate floors it rather than clearing it, because
    /// relocating is still a cost.
    /// </remarks>
    private static double? ScoreLocation(CandidateFacts candidate, PostingFacts posting)
    {
        if (posting.WorkArrangement == WorkArrangement.Remote)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(posting.LocationCountry) && string.IsNullOrWhiteSpace(posting.LocationCity))
        {
            return null;
        }

        if (Same(candidate.LocationCity, posting.LocationCity))
        {
            return 1.0;
        }

        if (Same(candidate.LocationCountry, posting.LocationCountry))
        {
            return 0.7;
        }

        // Nothing known about the candidate's location cannot be scored against, only guessed
        // at, so the axis drops out rather than penalising an incomplete profile.
        if (string.IsNullOrWhiteSpace(candidate.LocationCity) && string.IsNullOrWhiteSpace(candidate.LocationCountry))
        {
            return null;
        }

        return candidate.WillingToRelocate ? 0.5 : 0.1;
    }

    private static bool Same(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
