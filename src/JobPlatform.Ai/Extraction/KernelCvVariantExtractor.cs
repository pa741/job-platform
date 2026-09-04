using System.Text.Json;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace JobPlatform.Ai.Extraction;

/// <summary>
/// Reads one CV variant's markdown for the concepts a selection pass scores it on.
/// </summary>
/// <remarks>
/// <b>The same pass as <see cref="KernelDocumentExtractor"/>, pointed at a document the candidate
/// wrote, and narrowed on the way out.</b> The vocabulary is handed to the model as its allowed
/// output set; a key outside it comes back as a mention rather than as an invention; every key is
/// re-checked against the graph regardless, because a prompt is a request and not a guarantee. That
/// re-checking is <see cref="ExtractionPrompt.Parse"/> and it is <i>called</i> rather than
/// reimplemented here, for the reason <c>CvVariantSelector</c> calls <c>MatchScorer.BestRelation</c>:
/// two spellings of "which keys may enter the data" would drift, and the drift would show up as a
/// hallucinated key living in <c>CvVariantConcepts</c> and nowhere else, quietly splitting a concept
/// in two on the one side of the join nobody audits.
///
/// <b>What that shared reader returns is deliberately not what this returns.</b>
/// <see cref="ExtractionPrompt.Parse"/> produces a <c>DocumentExtraction</c>, which is the object
/// <c>CandidateProfileRepository.ApplyExtractionAsync</c> accepts. It exists here as a local for the
/// length of one method and is projected onto <see cref="CvVariantExtraction"/> before anything can
/// hold it, so the guarantee that a CV's concepts never reach <c>ProfileConcepts</c> is a property
/// of what this method's signature can return rather than a note in a review. The seniority, work
/// arrangement and salary that reader also fills in are dropped at the same boundary - a CV states
/// all three and none of them may widen what the candidate is judged to have.
///
/// <b>One document per call, and no packing.</b> See <see cref="ICvVariantExtractor"/> for the
/// arithmetic: a capped library is six documents saved one at a time, so batching would save five
/// copies of the vocabulary per candidate and would import the misaligned-index failure that, on
/// this side of the system, uploads the wrong CV to an employer. The consequence for this file is
/// that it has no counterpart to <c>Distribute</c> and no partial outcome - one document either
/// came back or it did not.
///
/// <b>It reports to the AI call ledger like every other model call site here</b>, because the AI
/// paths in this system degrade silently by design and that has already cost real work three times.
/// The loss this one hides is specific and slow: a CV whose extraction failed keeps no concepts,
/// scores zero against every posting, and is simply never chosen - so the symptom is an application
/// that never happens, reported to the candidate as "no CV fits" while a perfectly good CV sits in
/// the library. A count in the ledger is the only thing that separates that from a genuine gap.
/// </remarks>
public sealed class KernelCvVariantExtractor(
    Kernel kernel,
    IOptions<AzureOpenAiOptions> options,
    ILogger<KernelCvVariantExtractor>? logger = null,
    IAiCallLog? callLog = null,
    TimeProvider? time = null) : ICvVariantExtractor
{
    /// <summary>
    /// Names this pass in the AI call ledger.
    /// </summary>
    /// <remarks>
    /// A third name beside <c>posting-extraction</c> and <c>profile-extraction</c>, on the same
    /// argument that split those two: different volumes, different costs and different failure
    /// consequences, and averaging them hides all three. This one runs once when somebody saves a
    /// document, so a rate limit cannot be its problem the way it was the corpus backfill's - what
    /// it is worth watching for is a persistent zero, which means a library that will never win a
    /// selection.
    /// </remarks>
    public const string LedgerOperation = "cv-variant-extraction";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly AzureOpenAiOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// What the model is asked, and what it is asked not to answer.
    /// </summary>
    /// <remarks>
    /// <b>The final rule is the type's guard restated where the model can act on it.</b>
    /// <see cref="CvVariantExtraction"/> has no field for a seniority, an arrangement or a salary,
    /// so asking for them would only pay for tokens that are thrown away - and, worse, would leave
    /// a reader of this prompt believing the pass reads them. Refusing in both places costs one
    /// sentence and removes the question.
    ///
    /// <b>"Read only what this CV says" is the load-bearing instruction.</b> A model reading "Senior
    /// Platform Engineer, AWS" will helpfully offer Terraform and Kubernetes, because that is what
    /// the role usually implies - and every one of those inferences is a claim the candidate did not
    /// make, scored as though they had, on the document about to be sent to an employer. This is the
    /// same class of invention that put "I am an AI and they should have seen this" into an
    /// application, arriving through a narrower door.
    ///
    /// The vocabulary leads and the document is last, so the constant prefix a provider can cache is
    /// the expensive half - the arrangement <see cref="ExtractionPrompt.ForSingleDocument"/> already
    /// makes for the same reason.
    /// </remarks>
    private const string PromptTemplate =
        """
        You are reading a CV that a candidate in the UK software job market wrote about
        themselves.

        Return ONLY a JSON object.

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
              "evidence": "<the exact phrase from the CV, at most 100 characters>",
              "confidence": <number between 0 and 1>
            }
          ],
          "unknownSkills": ["<a technology named in the CV that has no key above>"]
        }

        Rules:
        - Read only what this CV says. Never add a skill because a role, an employer or another
          skill usually implies it. A skill this document does not name is one the candidate did
          not claim.
        - polarity is how strongly this CV presents the skill: "required" where it is led on or
          described at expert or lead level, "preferred" for working competence, "mentioned" for
          something named in passing. Do not guess.
        - yearsMin/yearsMax attach to that skill specifically, not to the career overall. Leave
          them null unless the CV ties a number to that skill.
        - evidence is a phrase copied from the CV, never a summary of one.
        - unknownSkills is for real technologies only, not for responsibilities or soft skills.
        - Report nothing about salary, seniority, location or working arrangement. This pass reads
          a CV only to decide which CV to send, and nothing else it says is wanted.

        CV: {{$label}}

        {{$markdown}}
        """;

    /// <inheritdoc />
    public async Task<CvVariantExtraction?> ExtractAsync(
        CvVariantExtractionRequest request, CancellationToken ct = default)
    {
        var started = _time.GetTimestamp();
        var (extraction, reason, prompt, usage) = await ReadAsync(request, ct);

        if (callLog is not null)
        {
            try
            {
                await callLog.RecordAsync(
                    AiCallRecord.Create(
                        _time.GetUtcNow(),
                        LedgerOperation,
                        _options.BulkDeployment,
                        // Two outcomes and no PartiallyDiscarded, because there is no batch to be
                        // partly right about. One document either came back readable or it did not.
                        extraction is null ? AiCallOutcome.Failed : AiCallOutcome.Succeeded,
                        requested: 1,
                        returned: extraction is null ? 0 : 1,
                        (long)_time.GetElapsedTime(started).TotalMilliseconds,
                        reason,
                        // The variant that kept no concepts, which is the one a person will
                        // eventually be told no CV fits for. Naming it is the difference between
                        // knowing that and guessing.
                        extraction is null ? [request.VariantId] : [],
                        // Offered, not decided: the sink keeps a prompt only where the deployment
                        // asked for prompts and only where the call lost something. It carries the
                        // candidate's CV verbatim, which is the same posture the profile pass holds
                        // for the same reason, and the reason those three rules live in the sink.
                        prompt,
                        usage),
                    ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not record the CV variant extraction to the AI ledger.");
            }
        }

        return extraction;
    }

    private async Task<(CvVariantExtraction? Extraction, string? Reason, string? Prompt, AiTokenUsage Usage)>
        ReadAsync(CvVariantExtractionRequest request, CancellationToken ct)
    {
        var usage = default(AiTokenUsage);

        if (string.IsNullOrWhiteSpace(request.Markdown))
        {
            // Unreachable through CvVariant.Create, which refuses a blank CV outright - a blank
            // variant renders to a blank PDF and a blank PDF can be uploaded to an employer. It is
            // reachable through a row rehydrated by an older build, so it is answered rather than
            // asserted, and it is recorded so that a library quietly scoring nothing has a reason
            // attached to it.
            return (null, "nothing to send", null, usage);
        }

        // The prompt as the model will see it, so a failure can be replayed rather than
        // reconstructed by hand.
        var prompt = PromptTemplate
            .Replace("{{$vocabulary}}", ExtractionPrompt.Vocabulary, StringComparison.Ordinal)
            .Replace("{{$label}}", Label(request), StringComparison.Ordinal)
            .Replace("{{$markdown}}", Markdown(request), StringComparison.Ordinal);

        var arguments = new KernelArguments(AiPrompt.Bulk(_options))
        {
            ["vocabulary"] = ExtractionPrompt.Vocabulary,
            ["label"] = Label(request),
            ["markdown"] = Markdown(request),
        };

        string response;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var result = await kernel.InvokePromptAsync(PromptTemplate, arguments, cancellationToken: timeout.Token);
            response = result.ToString();
            usage = AiUsage.From(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning(
                "Reading CV variant {VariantId} timed out after {Seconds}s.",
                request.VariantId, _options.TimeoutSeconds);

            return (null, $"timed out after {_options.TimeoutSeconds}s", prompt, usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider failure must not fail the save that triggered it. The candidate's document
            // is already stored - it is the record - and losing its concepts costs a variant that is
            // not chosen until something reads it again, where a throw here would cost somebody the
            // CV they just typed. The same trade ProfileEndpoints makes on the other document.
            logger?.LogWarning(ex, "Reading CV variant {VariantId} failed.", request.VariantId);

            return (null, $"{ex.GetType().Name}: {ex.Message}", prompt, usage);
        }

        var json = AiJson.ExtractJsonObject(response);

        if (json is null)
        {
            logger?.LogWarning("Reading CV variant {VariantId} returned no JSON object.", request.VariantId);
            return (null, "response carried no JSON object", prompt, usage);
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            // The one narrowing in this file, and the only place a DocumentExtraction exists on the
            // variant path. It is a local, it is read twice, and it is gone before this method
            // returns - see the class remarks on why it may not be what the caller receives.
            var read = ExtractionPrompt.Parse(document.RootElement, _options.BulkDeployment);

            return (
                new CvVariantExtraction { Concepts = read.Concepts, Mentions = read.Mentions },
                null,
                prompt,
                usage);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Reading CV variant {VariantId} returned malformed JSON.", request.VariantId);
            return (null, $"malformed JSON: {ex.Message}", prompt, usage);
        }
    }

    /// <summary>
    /// The candidate's own heading for their document, or a stand-in where they gave none.
    /// </summary>
    /// <remarks>
    /// Bounded at the width the label column is, so a rehydrated row from a build with a wider bound
    /// cannot push the document itself past what the model reads. The stand-in names the id rather
    /// than being blank, for the reason <c>CvVariantSelector.Name</c> does the same: an unlabelled
    /// heading reads to the model as a document with no subject, and it will invent one.
    /// </remarks>
    private static string Label(CvVariantExtractionRequest request)
        => string.IsNullOrWhiteSpace(request.Label)
            ? $"variant {request.VariantId}"
            : ExtractionPrompt.Truncate(request.Label.Trim(), CvVariantLimits.MaxLabelLength)!;

    /// <summary>
    /// The document, bounded at the length the store already refuses to exceed.
    /// </summary>
    /// <remarks>
    /// <b>Bounded at <see cref="CvVariantLimits.MaxMarkdownLength"/> rather than at
    /// <see cref="ExtractionPrompt.MaxDocumentChars"/>, and the difference is the point.</b> That
    /// ceiling is 12,000 characters because a job advert front-loads its requirements and buries
    /// them under boilerplate about equal opportunities, so losing the tail of one costs nothing. A
    /// CV is the opposite shape: the older roles are at the bottom, which is where the concepts that
    /// distinguish one variant from another live, and a truncation would silently narrow a document
    /// until it stopped winning the postings it was written for. Nobody would see it - a variant
    /// that is not chosen looks exactly like a variant nobody needed.
    ///
    /// So the bound here is the one <c>CvVariant.Create</c> already enforces by refusing, which
    /// makes this a no-op for every variant written through it and a backstop for a row from an
    /// older build. Twenty thousand characters is roughly ten pages and a few thousand tokens - one
    /// of these travels beside the vocabulary comfortably, which is the other half of why nothing
    /// here needs to truncate.
    /// </remarks>
    private static string Markdown(CvVariantExtractionRequest request)
        => ExtractionPrompt.Truncate(request.Markdown, CvVariantLimits.MaxMarkdownLength)!;
}
