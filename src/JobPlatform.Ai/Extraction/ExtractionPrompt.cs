using System.Text;
using System.Text.Json;
using JobPlatform.Core.Enrichment;

namespace JobPlatform.Ai.Extraction;

/// <summary>
/// The extraction prompt and the parsing of what comes back, shared by both extractors.
/// </summary>
/// <remarks>
/// <b>Extracted here the moment there were two callers, not before.</b> The synchronous path
/// packs many documents into one Semantic Kernel prompt; the batch path sends one document per
/// request to a provider's batch endpoint. They are different transports asking the same
/// question, and if each kept its own copy of the vocabulary, the schema and the rules, the two
/// would drift - and the drift would show up as a corpus where postings extracted in March
/// disagree with postings extracted in April for reasons nobody can reconstruct.
///
/// The vocabulary is the expensive half of every prompt and the load-bearing half of the whole
/// design: it is the model's allowed output set, and a key outside it must come back as a
/// mention rather than as an invention.
/// </remarks>
internal static class ExtractionPrompt
{
    /// <summary>
    /// How much of a document to send.
    /// </summary>
    /// <remarks>
    /// Descriptions run to several KB and the useful content is front-loaded - requirements come
    /// before the boilerplate about equal opportunities and the company's values. A ceiling
    /// keeps a pathological posting from dominating a batch's cost, and losing the tail of one
    /// costs less than truncating every posting to fit the worst.
    /// </remarks>
    public const int MaxDocumentChars = 12_000;

    private static readonly Lazy<string> VocabularyText =
        new(BuildVocabulary, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The skills and qualifications, as keys and labels.</summary>
    public static string Vocabulary => VocabularyText.Value;

    /// <summary>
    /// The rules both transports share, above whatever document framing each adds.
    /// </summary>
    /// <remarks>
    /// Written as one constant rather than assembled, so a change to what the model is asked is
    /// a single reviewable diff rather than a search across two files.
    /// </remarks>
    public const string Rules =
        """
        Rules:
        - Read each document independently. A skill named in one says nothing about another.
        - polarity "required" only where the text marks it essential, must-have, or equivalent.
          "preferred" for desirable, nice-to-have, bonus. "mentioned" when the text gives no
          indication either way. Do not guess.
        - For a candidate profile, polarity is how strongly the candidate holds the skill:
          "required" for expert or lead-level, "preferred" for working competence, "mentioned"
          for passing familiarity.
        - yearsMin/yearsMax attach to that concept specifically, not to the role overall. Leave
          them null unless the text ties a number to that skill.
        - salary must be annualised. A day rate multiplies by 260, an hourly rate by 2080, a
          month by 12, a week by 52. Return null rather than guessing a currency.
        - seniority and workArrangement: null unless the text says. Silence is not "onsite".
        - unknownSkills is for real technologies only, not for responsibilities or soft skills.
        """;

    /// <summary>The shape of one document's answer, without any batch wrapper around it.</summary>
    public const string DocumentSchema =
        """
        {
          "concepts": [
            {
              "key": "<a key from the vocabulary above>",
              "polarity": "required" | "preferred" | "mentioned",
              "yearsMin": <integer or null>,
              "yearsMax": <integer or null>,
              "evidence": "<the exact phrase from the text, at most 100 characters>",
              "confidence": <number between 0 and 1>
            }
          ],
          "unknownSkills": ["<a technology named in the text that has no key above>"],
          "seniority": "intern" | "junior" | "mid" | "senior" | "lead" | "principal" | "executive" | null,
          "workArrangement": "onsite" | "hybrid" | "remote" | null,
          "hybridDaysInOffice": <integer 1-5 or null>,
          "salary": { "min": <number or null>, "max": <number or null>, "currency": "<ISO code>", "confidence": <0-1> } | null
        }
        """;

    /// <summary>
    /// The whole instruction for a single document, for a transport that sends one at a time.
    /// </summary>
    /// <remarks>
    /// The vocabulary leads, deliberately. It is identical across every request in a corpus-wide
    /// pass and providers cache a repeated prefix, so putting the one variable part - the advert
    /// - last is what makes that cache reachable.
    /// </remarks>
    public static string ForSingleDocument(ExtractionRequest request)
    {
        var kind = request.Kind == DocumentKind.Profile ? "candidate profile" : "job advert";

        return $"""
            You are extracting structured data from a {kind} in the UK software job market.

            Return ONLY a JSON object.

            Use ONLY concept keys from this vocabulary. Never invent a key.
            {Vocabulary}

            Schema:
            {DocumentSchema}

            {Rules}

            Title: {request.Title ?? "(none)"}

            Text:
            {Truncate(request.Text, MaxDocumentChars)}
            """;
    }

    /// <summary>
    /// Turns one document's JSON object into an extraction.
    /// </summary>
    /// <remarks>
    /// <b>Every key is re-checked against the graph here</b>, whatever the prompt asked for. A
    /// hallucinated key is indistinguishable from a real one once it is in SQL and would quietly
    /// split a concept in two - which is the failure the whole concept graph exists to prevent -
    /// so a key the graph does not know is demoted to a mention. A prompt is a request, not a
    /// guarantee, and this is what makes "never invent a key" true rather than merely asked for.
    /// </remarks>
    public static DocumentExtraction Parse(JsonElement root, string? model)
    {
        var graph = ConceptGraph.Default;
        var concepts = new List<ConceptAssertion>();
        var mentions = new List<UnresolvedMention>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("concepts", out var conceptArray)
            && conceptArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in conceptArray.EnumerateArray())
            {
                var key = String(item, "key");

                if (key is null)
                {
                    continue;
                }

                if (!graph.TryGet(key, out _))
                {
                    mentions.Add(new UnresolvedMention(
                        Truncate(key, 120)!, MentionReason.UnknownModelSkill));
                    continue;
                }

                if (!seen.Add(key))
                {
                    continue;
                }

                concepts.Add(new ConceptAssertion(
                    key,
                    AssertionSource.Model,
                    ParsePolarity(String(item, "polarity")),
                    Int(item, "yearsMin"),
                    Int(item, "yearsMax"),
                    Truncate(String(item, "evidence"), 120),
                    Double(item, "confidence")));
            }
        }

        if (root.TryGetProperty("unknownSkills", out var unknown)
            && unknown.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in unknown.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && item.GetString() is { Length: > 0 } surfaceForm)
                {
                    mentions.Add(new UnresolvedMention(
                        Truncate(surfaceForm, 120)!, MentionReason.UnknownModelSkill));
                }
            }
        }

        var salary = root.TryGetProperty("salary", out var s) && s.ValueKind == JsonValueKind.Object
            ? s
            : (JsonElement?)null;

        return new DocumentExtraction
        {
            Concepts = concepts,
            Mentions = mentions,
            Seniority = ParseSeniority(String(root, "seniority")),
            WorkArrangement = ParseArrangement(String(root, "workArrangement")),
            HybridDaysInOffice = Int(root, "hybridDaysInOffice") is { } days and >= 1 and <= 5 ? days : null,
            AnnualSalaryMin = salary is null ? null : Decimal(salary.Value, "min"),
            AnnualSalaryMax = salary is null ? null : Decimal(salary.Value, "max"),
            SalaryCurrency = salary is null ? null : String(salary.Value, "currency"),
            SalaryConfidence = salary is null ? null : Double(salary.Value, "confidence"),
            Model = model,
            PayloadJson = root.GetRawText(),
        };
    }

    /// <summary>
    /// The skills and qualifications, as keys and labels. Domains are omitted: they are
    /// reached by rolling a concrete concept up the closure, not by being asserted directly.
    /// </summary>
    private static string BuildVocabulary()
    {
        var builder = new StringBuilder(16_000);

        foreach (var concept in ConceptGraph.Default.Concepts)
        {
            if (concept.Kind == ConceptKind.Domain)
            {
                continue;
            }

            builder.Append(concept.Key).Append(" = ").AppendLine(concept.Label);
        }

        return builder.ToString();
    }

    public static AssertionPolarity ParsePolarity(string? value) => value?.ToLowerInvariant() switch
    {
        "required" => AssertionPolarity.Required,
        "preferred" => AssertionPolarity.Preferred,
        "mentioned" => AssertionPolarity.Mentioned,
        _ => AssertionPolarity.Unspecified,
    };

    public static Seniority? ParseSeniority(string? value) => value?.ToLowerInvariant() switch
    {
        "intern" => Seniority.Intern,
        "junior" => Seniority.Junior,
        "mid" => Seniority.Mid,
        "senior" => Seniority.Senior,
        "lead" => Seniority.Lead,
        "principal" => Seniority.Principal,
        "executive" => Seniority.Executive,
        _ => null,
    };

    public static WorkArrangement? ParseArrangement(string? value) => value?.ToLowerInvariant() switch
    {
        "onsite" => WorkArrangement.OnSite,
        "hybrid" => WorkArrangement.Hybrid,
        "remote" => WorkArrangement.Remote,
        _ => null,
    };

    public static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    public static double? Double(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
                ? parsed
                : null;

    public static decimal? Decimal(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var parsed)
                ? parsed
                : null;

    public static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
