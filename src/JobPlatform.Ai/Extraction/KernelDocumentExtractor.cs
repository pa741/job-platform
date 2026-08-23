using System.Text;
using System.Text.Json;
using JobPlatform.Core.Enrichment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace JobPlatform.Ai.Extraction;

/// <summary>
/// The model pass, invoked through a Semantic Kernel prompt template.
/// </summary>
/// <remarks>
/// Asked only for what the deterministic pass genuinely cannot do: required versus
/// nice-to-have, years attached to a specific skill rather than to the role, a work
/// arrangement stated in prose, a seniority the title does not carry, and technologies the
/// vocabulary has not heard of. Everything a regex can already answer is left to the regex,
/// which is both cheaper and more consistent.
///
/// <b>The vocabulary is handed to the model as its allowed output set</b>, and anything
/// outside it must come back as a mention rather than as an invented key. An invented key
/// would be indistinguishable from a real one in the data and would quietly split a concept in
/// two — exactly the failure the whole concept graph exists to prevent. Keys the model returns
/// are checked against the graph on the way in regardless, because a prompt is a request and
/// not a guarantee.
///
/// Prompts go through the Kernel with <see cref="KernelArguments"/> rather than reaching past
/// it to the Anthropic SDK, or the abstraction buys nothing. The price is that Semantic
/// Kernel's execution settings are provider-neutral and cannot express Anthropic's
/// structured-output constraint, so a fenced or prose-wrapped body is expected rather than
/// exceptional — hence <see cref="AiJson.ExtractJsonObject"/>.
/// </remarks>
public sealed class KernelDocumentExtractor(
    Kernel kernel,
    IOptions<AiProviderOptions> options,
    ILogger<KernelDocumentExtractor>? logger = null) : IDocumentExtractor
{
    private readonly AiProviderOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// How much of a document to send.
    /// </summary>
    /// <remarks>
    /// Descriptions run to several KB and the useful content is front-loaded — requirements
    /// come before the boilerplate about equal opportunities and the company's values. A
    /// ceiling keeps a pathological posting from dominating a batch's cost, and losing the tail
    /// of one costs less than truncating every posting to fit the worst.
    /// </remarks>
    private const int MaxDocumentChars = 12_000;

    private static readonly Lazy<string> Vocabulary = new(BuildVocabulary, LazyThreadSafetyMode.ExecutionAndPublication);

    private const string PromptTemplate =
        """
        You are extracting structured data from a {{$documentKind}} in the UK software job market.

        Return ONLY a JSON object. No preamble, no explanation, no code fence.

        Use ONLY concept keys from this vocabulary. Never invent a key.
        {{$vocabulary}}

        Schema:
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

        Rules:
        - polarity "required" only where the text marks it essential, must-have, or equivalent.
          "preferred" for desirable, nice-to-have, bonus. "mentioned" when the text gives no
          indication either way. Do not guess.
        - yearsMin/yearsMax attach to that concept specifically, not to the role overall. Leave
          them null unless the text ties a number to that skill.
        - salary must be annualised. A day rate multiplies by 260, an hourly rate by 2080, a
          month by 12, a week by 52. Return null rather than guessing a currency.
        - seniority and workArrangement: null unless the text says. Silence is not "onsite".
        - unknownSkills is for real technologies only, not for responsibilities or soft skills.

        Title: {{$title}}

        Text:
        {{$text}}
        """;

    public async Task<DocumentExtraction?> ExtractAsync(
        ExtractionRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return null;
        }

        var arguments = new KernelArguments
        {
            ["documentKind"] = request.Kind == DocumentKind.Profile ? "candidate CV" : "job advert",
            ["vocabulary"] = Vocabulary.Value,
            ["title"] = request.Title ?? "(none)",
            ["text"] = Truncate(request.Text, MaxDocumentChars),
        };

        string response;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var result = await kernel.InvokePromptAsync(PromptTemplate, arguments, cancellationToken: timeout.Token);
            response = result.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("Extraction timed out after {Seconds}s.", _options.TimeoutSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider failure must not fail the queue message forever. The row simply has
            // no extraction and is picked up again by the backfill.
            logger?.LogWarning(ex, "Extraction call failed.");
            return null;
        }

        var json = AiJson.ExtractJsonObject(response);

        if (json is null)
        {
            logger?.LogWarning("Extraction returned no JSON object.");
            return null;
        }

        try
        {
            return Parse(json);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Extraction returned malformed JSON.");
            return null;
        }
    }

    private DocumentExtraction Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

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

                // The prompt says never to invent a key. This is what makes that true: a key
                // the graph does not know is recorded as a mention, so a hallucinated concept
                // cannot enter the data wearing the same shape as a real one.
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
            Model = _options.Model,
            PayloadJson = json,
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

    private static AssertionPolarity ParsePolarity(string? value) => value?.ToLowerInvariant() switch
    {
        "required" => AssertionPolarity.Required,
        "preferred" => AssertionPolarity.Preferred,
        "mentioned" => AssertionPolarity.Mentioned,
        _ => AssertionPolarity.Unspecified,
    };

    private static Seniority? ParseSeniority(string? value) => value?.ToLowerInvariant() switch
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

    private static WorkArrangement? ParseArrangement(string? value) => value?.ToLowerInvariant() switch
    {
        "onsite" => WorkArrangement.OnSite,
        "hybrid" => WorkArrangement.Hybrid,
        "remote" => WorkArrangement.Remote,
        _ => null,
    };

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    private static double? Double(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
                ? parsed
                : null;

    private static decimal? Decimal(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var parsed)
                ? parsed
                : null;

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
