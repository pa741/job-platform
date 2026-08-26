using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using JobPlatform.Core.Enrichment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Batch;
using OpenAI.Files;

namespace JobPlatform.Ai.Extraction;

/// <summary>
/// Extraction through OpenAI's Batch API: submit now, collect within twenty-four hours.
/// </summary>
/// <remarks>
/// <b>One document per request, and deliberately no packing.</b> The synchronous path packs ten
/// adverts into a single prompt because the concept vocabulary has to precede every extraction
/// and sending it ten times is most of what a corpus-wide pass costs. That packing is also the
/// riskiest thing in this codebase: an answer landing against the wrong posting would be wrong,
/// internally consistent and undetectable afterwards, which is why that extractor polices the
/// indices coming back. A batch API gives every request its own <c>custom_id</c> and echoes it,
/// so correlation becomes the platform's problem. Packing here would trade that guarantee away
/// to save roughly a pound across the entire corpus, which is not a trade worth making.
///
/// The vocabulary leads every prompt for a second reason: it is byte-identical across a whole
/// submission, and a repeated prefix is what a provider's prompt cache can recognise. Whether
/// caching applies to batched requests is not something the documentation states, so it is a
/// hope rather than a plan - but it costs nothing to order the prompt so that it could.
///
/// Nothing here throws for a provider failure. A batch that is not accepted leaves its postings
/// unextracted and the next backfill picks them up; a collection that fails is retried on the
/// next timer tick. The alternative - an admin endpoint returning 500 because a third party is
/// having a bad afternoon - is worse in every case.
/// </remarks>
/// <remarks>
/// The batch surface is marked experimental by the OpenAI SDK (OPENAI001) and is suppressed at
/// this declaration rather than through a project-wide NoWarn, the same way the one experimental
/// Semantic Kernel property is. A blanket suppression would silently accept the next
/// experimental API somebody reaches for; this one is a deliberate, bounded acceptance of a
/// surface whose shape may move, and the whole implementation is behind IBatchDocumentExtractor
/// so that a move costs one file.
/// </remarks>
#pragma warning disable OPENAI001
public sealed class OpenAiBatchExtractor(
    BatchClient batches,
    OpenAIFileClient files,
    IOptions<OpenAiBatchOptions> options,
    ILogger<OpenAiBatchExtractor>? logger = null) : IBatchDocumentExtractor
{
    private readonly OpenAiBatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<BatchSubmission?> SubmitAsync(
        IReadOnlyList<BatchExtractionItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var usable = items.Where(i => !string.IsNullOrWhiteSpace(i.Request.Text)).ToList();

        if (usable.Count == 0)
        {
            return null;
        }

        // The ceiling was declared and documented as a bound and then never applied, which the
        // first corpus-sized submission exposed: 2,459 documents went out under a stated limit
        // of 2,000. It was harmless - the provider's own ceiling is fifty thousand - but an
        // option that reads as a safety limit and enforces nothing is worse than no option.
        //
        // Truncating rather than splitting, deliberately. One call submits one batch; the
        // caller decides what to do with the remainder, and it already knows how, because the
        // backfill it is called from is a bounded endpoint that reports `more`.
        if (usable.Count > _options.MaxBatchSize)
        {
            logger?.LogInformation(
                "Trimming a submission of {Count} to the configured ceiling of {Max}.",
                usable.Count, _options.MaxBatchSize);

            usable = usable.Take(_options.MaxBatchSize).ToList();
        }

        try
        {
            var jsonl = OpenAiBatchFormat.BuildRequestFile(usable, _options);

            // Uploaded as a file rather than posted inline: the API takes a file id, and a
            // corpus-sized submission is tens of megabytes of JSONL.
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonl));

            var upload = await files.UploadFileAsync(
                stream,
                $"extraction-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.jsonl",
                FileUploadPurpose.Batch,
                ct);

            using var request = BinaryContent.Create(BinaryData.FromObjectAsJson(new
            {
                input_file_id = upload.Value.Id,
                endpoint = "/v1/chat/completions",

                // The only value the API accepts today. Named rather than defaulted so that a
                // second option appearing is a visible choice rather than a silent one.
                completion_window = "24h",
            }));

            // waitUntilCompleted: false - the whole point is not to wait. The returned id is
            // stored and polled by a timer, because this outlives the process that submitted it.
            //
            // The cancellation token travels as a RequestOptions rather than as its own
            // parameter: this surface is the SDK's protocol layer, which is also why it takes
            // and returns BinaryContent rather than typed models.
            var created = await batches.CreateBatchAsync(
                request, waitUntilCompleted: false, new RequestOptions { CancellationToken = ct });

            var id = ReadId(created.GetRawResponse());

            if (id is null)
            {
                logger?.LogWarning("OpenAI accepted a batch but returned no id.");
                return null;
            }

            logger?.LogInformation(
                "Submitted batch {BatchId} with {Count} document(s) on {Model}.",
                id, usable.Count, _options.Model);

            return new BatchSubmission(id, usable.Count, _options.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Submitting a batch of {Count} document(s) failed.", usable.Count);
            return null;
        }
    }

    public async Task<BatchOutcome?> CollectAsync(string providerBatchId, CancellationToken ct = default)
    {
        try
        {
            var response = await batches.GetBatchAsync(
                providerBatchId, new RequestOptions { CancellationToken = ct });

            using var document = JsonDocument.Parse(response.GetRawResponse().Content.ToMemory());
            var root = document.RootElement;

            var status = ExtractionPrompt.String(root, "status");
            var state = OpenAiBatchFormat.ParseState(status);

            if (state == BatchState.Running)
            {
                return new BatchOutcome(state, []);
            }

            if (state != BatchState.Completed)
            {
                return new BatchOutcome(state, [], $"The provider reported '{status}'.");
            }

            var results = new List<BatchResult>();

            // Successes and failures arrive in two separate files. Reading only the first would
            // leave a failed item indistinguishable from one the provider never answered, and
            // the caller needs to tell those apart to decide whether to retry.
            if (ExtractionPrompt.String(root, "output_file_id") is { Length: > 0 } outputFileId)
            {
                results.AddRange(await ReadResultsAsync(outputFileId, ct));
            }

            if (ExtractionPrompt.String(root, "error_file_id") is { Length: > 0 } errorFileId)
            {
                results.AddRange(await ReadErrorsAsync(errorFileId, ct));
            }

            return new BatchOutcome(state, results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Collecting batch {BatchId} failed.", providerBatchId);
            return null;
        }
    }

    private async Task<List<BatchResult>> ReadResultsAsync(string fileId, CancellationToken ct)
    {
        var results = new List<BatchResult>();
        var content = await files.DownloadFileAsync(fileId, ct);

        foreach (var line in OpenAiBatchFormat.Lines(content.Value.ToString()))
        {
            if (OpenAiBatchFormat.ReadOutputLine(line, _options.Model) is { } result)
            {
                results.Add(result);
            }
            else
            {
                // No correlation id, so there is nothing to attach the answer to. Guessing is
                // the failure this whole design exists to avoid.
                logger?.LogWarning("A batch result line carried no custom_id; dropping it.");
            }
        }

        return results;
    }

    private async Task<List<BatchResult>> ReadErrorsAsync(string fileId, CancellationToken ct)
    {
        var results = new List<BatchResult>();
        var content = await files.DownloadFileAsync(fileId, ct);

        foreach (var line in OpenAiBatchFormat.Lines(content.Value.ToString()))
        {
            if (OpenAiBatchFormat.ReadErrorLine(line) is { } result)
            {
                results.Add(result);
            }
        }

        return results;
    }

    private static string? ReadId(PipelineResponse response)
    {
        using var document = JsonDocument.Parse(response.Content.ToMemory());
        return ExtractionPrompt.String(document.RootElement, "id");
    }
}
#pragma warning restore OPENAI001
