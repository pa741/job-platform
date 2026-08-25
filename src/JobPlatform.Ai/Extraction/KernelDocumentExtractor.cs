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
/// two - exactly the failure the whole concept graph exists to prevent. Keys the model returns
/// are checked against the graph on the way in regardless, because a prompt is a request and
/// not a guarantee.
///
/// <b>Several documents travel in one call.</b> The vocabulary below is several thousand tokens
/// and has to precede every extraction; sent per document it is the majority of what a
/// corpus-wide pass costs. Batching pays for it once per call instead of once per posting, and
/// that ratio - not the per-token price of the deployment - is what makes running this over
/// tens of thousands of postings affordable.
///
/// Prompts go through the Kernel with <see cref="KernelArguments"/> rather than reaching past
/// it to an SDK. Unlike the provider-neutral arrangement this replaced, the execution settings
/// here can express JSON mode, so a fenced or prose-wrapped body is no longer expected -
/// <see cref="AiJson.ExtractJsonObject"/> stays as a net rather than as the normal path.
/// </remarks>
public sealed class KernelDocumentExtractor(
    Kernel kernel,
    IOptions<AzureOpenAiOptions> options,
    ILogger<KernelDocumentExtractor>? logger = null) : IDocumentExtractor
{
    private readonly AzureOpenAiOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private const string PromptTemplate =
        """
        You are extracting structured data from documents in the UK software job market.

        Return ONLY a JSON object.

        Use ONLY concept keys from this vocabulary. Never invent a key.
        {{$vocabulary}}

        Schema:
        {
          "documents": [
            {
              "index": <the index given in the DOCUMENT heading, copied exactly>,
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
          ]
        }

        Rules:
        - Return exactly one entry per DOCUMENT below, in the same order, with the index copied
          from its heading. Never merge two documents and never omit one.
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

        {{$documents}}
        """;

    public async Task<DocumentExtraction?> ExtractAsync(
        ExtractionRequest request,
        CancellationToken ct = default)
    {
        var results = await ExtractBatchAsync([request], ct);
        return results.Count > 0 ? results[0] : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentExtraction?>> ExtractBatchAsync(
        IReadOnlyList<ExtractionRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var results = new DocumentExtraction?[requests.Count];
        var batchSize = Math.Max(1, _options.BatchSize);

        // Sequential rather than concurrent. The consumer is a queue-triggered function whose
        // own concurrency is what scales this; firing several calls per invocation as well
        // multiplies the two and is the shape that trips the deployment's tokens-per-minute
        // limit, which fails the whole batch rather than slowing it.
        //
        // Getting this half right is not enough, and the first real backfill proved it: the
        // host's own queue concurrency was left at a batch size of four with a new-batch
        // threshold of two, so six invocations ran at once and collected HTTP 429s instead of
        // extractions. The two settings have to be read together - see the queues block in
        // host.json, which is now sized against the deployment's capacity rather than against
        // nothing in particular.
        for (var offset = 0; offset < requests.Count; offset += batchSize)
        {
            var length = Math.Min(batchSize, requests.Count - offset);
            var slice = new ExtractionRequest[length];

            for (var i = 0; i < length; i++)
            {
                slice[i] = requests[offset + i];
            }

            var extracted = await ExtractOneBatchAsync(slice, ct);

            for (var i = 0; i < length; i++)
            {
                results[offset + i] = extracted[i];
            }
        }

        return results;
    }

    private async Task<DocumentExtraction?[]> ExtractOneBatchAsync(
        ExtractionRequest[] batch,
        CancellationToken ct)
    {
        var results = new DocumentExtraction?[batch.Length];

        var documents = new StringBuilder(4_000);

        for (var i = 0; i < batch.Length; i++)
        {
            var request = batch[i];

            if (string.IsNullOrWhiteSpace(request.Text))
            {
                continue;
            }

            documents
                .Append("DOCUMENT ").Append(i)
                .Append(" (").Append(request.Kind == DocumentKind.Profile ? "candidate profile" : "job advert")
                .AppendLine(")")
                .Append("Title: ").AppendLine(request.Title ?? "(none)")
                .AppendLine("Text:")
                .AppendLine(ExtractionPrompt.Truncate(request.Text, ExtractionPrompt.MaxDocumentChars))
                .AppendLine();
        }

        if (documents.Length == 0)
        {
            return results;
        }

        var arguments = new KernelArguments(AiPrompt.Bulk(_options))
        {
            ["vocabulary"] = ExtractionPrompt.Vocabulary,
            ["documents"] = documents.ToString(),
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
            logger?.LogWarning(
                "Extraction of {Count} document(s) timed out after {Seconds}s.",
                batch.Length, _options.TimeoutSeconds);
            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider failure must not fail the queue message forever. The rows simply have
            // no extraction and are picked up again by the backfill.
            logger?.LogWarning(ex, "Extraction call failed for {Count} document(s).", batch.Length);
            return results;
        }

        var json = AiJson.ExtractJsonObject(response);

        if (json is null)
        {
            logger?.LogWarning("Extraction returned no JSON object.");
            return results;
        }

        try
        {
            Distribute(json, batch, results);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Extraction returned malformed JSON.");
        }

        return results;
    }

    /// <summary>
    /// Places each returned document against the request it answers.
    /// </summary>
    /// <remarks>
    /// The index is checked rather than trusted, and a duplicate or out-of-range one is dropped
    /// rather than clamped. Writing one posting's requirements onto another is the worst failure
    /// this class can produce - the data would be wrong, self-consistent, and impossible to spot
    /// afterwards - so anything ambiguous is discarded and the affected postings are simply
    /// re-extracted by the backfill later.
    /// </remarks>
    private void Distribute(string json, ExtractionRequest[] batch, DocumentExtraction?[] results)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("documents", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            logger?.LogWarning("Extraction response carried no documents array.");
            return;
        }

        var seen = new HashSet<int>();
        var placed = 0;

        foreach (var item in array.EnumerateArray())
        {
            if (ExtractionPrompt.Int(item, "index") is not { } index || index < 0 || index >= batch.Length)
            {
                logger?.LogWarning("Extraction returned an out-of-range document index.");
                continue;
            }

            if (!seen.Add(index))
            {
                logger?.LogWarning("Extraction returned document index {Index} twice.", index);
                continue;
            }

            results[index] = ExtractionPrompt.Parse(item, _options.BulkDeployment);
            placed++;
        }

        if (placed != batch.Length)
        {
            // Not an error: the missing ones stay null, their postings keep no extraction row,
            // and the backfill picks them up. Logged because a persistent shortfall means the
            // batch size is past what the output token ceiling can hold.
            logger?.LogInformation(
                "Extraction returned {Placed} of {Expected} documents.", placed, batch.Length);
        }
    }

}
