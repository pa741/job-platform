using System.Globalization;
using System.Text;
using System.Text.Json;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace JobPlatform.Ai.Matching;

/// <summary>
/// The judgement pass over a shortlist the arithmetic has already produced.
/// </summary>
/// <remarks>
/// <b>The model is never asked to score from scratch.</b> It is handed what
/// <see cref="MatchScorer"/> concluded - the number, the matched concepts, the gaps - and asked
/// the one question the arithmetic cannot answer: whether the gaps matter. A missing Kubernetes
/// on a role that mentions it once in a nice-to-have list is not the same fact as a missing
/// Kubernetes on a platform role, and no weighting scheme distinguishes them because the
/// difference is in the prose.
///
/// <b>The profile is sent once for the whole batch.</b> It is the larger half of the prompt and
/// it is identical for every posting in the list, so batching here saves proportionally more
/// than it does on extraction. That is why <see cref="ICandidacyAssessor"/> offers no
/// single-posting method: it would make the saving inexpressible.
///
/// Runs on the bulk deployment at raised reasoning effort. This is the only high-volume pass in
/// the system that is genuinely a judgement rather than a reading, and paying `low` for it would
/// buy a plausible-sounding restatement of the score it was already given.
/// </remarks>
public sealed class KernelCandidacyAssessor(
    Kernel kernel,
    IOptions<AzureOpenAiOptions> options,
    ILogger<KernelCandidacyAssessor>? logger = null,
    IAiCallLog? callLog = null,
    TimeProvider? time = null) : ICandidacyAssessor
{
    /// <summary>Names this pass in the AI call ledger.</summary>
    public const string LedgerOperation = "candidacy-assessment";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly AzureOpenAiOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>How much of an advert to send. Requirements are front-loaded; boilerplate is not.</summary>
    private const int MaxPostingChars = 6_000;

    /// <summary>How much of the candidate's own document to send.</summary>
    private const int MaxProfileChars = 12_000;

    private const string PromptTemplate =
        """
        You are advising a candidate on whether to apply for roles in the UK software job market.

        Return ONLY a JSON object.

        CANDIDATE
        {{$profile}}

        The candidate states these skills explicitly:
        {{$declared}}

        Schema:
        {
          "assessments": [
            {
              "index": <integer: the number in the ROLE heading, unquoted>,
              "verdict": "strong" | "possible" | "weak",
              "score": <integer 0-100>,
              "rationale": "<2-3 sentences addressed to the candidate>",
              "strengths": ["<something in the profile this role genuinely wants>"],
              "gaps": ["<something this role wants that the profile does not show>"],
              "emphasise": ["<what to lead with if they apply>"]
            }
          ]
        }

        Rules:
        - Return exactly one entry per ROLE below, in the same order, with the index copied
          from its heading. Never merge two roles and never omit one.
        - Each role carries a pre-computed score and a list of unmet requirements. Your job is
          to decide whether those gaps matter for this role, not to recount them. Disagreeing
          with the score is expected where the prose justifies it; say so in the rationale.
        - "weak" means a genuine blocker: a hard requirement with nothing in the profile that
          substitutes for it. Do not use it for a role that is merely a stretch.
        - Never assert a skill, employer or qualification that is not in the CANDIDATE section.
          If the profile is thin on something the role wants, that is a gap, not an inference.
        - Address the candidate as "you". Do not open with a summary of the advert.
        - strengths, gaps and emphasise: at most four each, one short sentence each.

        {{$roles}}
        """;

    public async Task<IReadOnlyList<CandidacyAssessment?>> AssessAsync(
        CandidateProfile profile,
        IReadOnlyList<CandidacyRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(requests);

        var results = new CandidacyAssessment?[requests.Count];

        if (requests.Count == 0)
        {
            return results;
        }

        var profileText = Truncate(profile.ToDocument(), MaxProfileChars);
        var declared = DescribeDeclared(profile);

        var batchSize = Math.Max(1, _options.BatchSize);

        for (var offset = 0; offset < requests.Count; offset += batchSize)
        {
            var length = Math.Min(batchSize, requests.Count - offset);
            var slice = new CandidacyRequest[length];

            for (var i = 0; i < length; i++)
            {
                slice[i] = requests[offset + i];
            }

            var assessed = await AssessOneBatchAsync(profileText, declared, slice, ct);

            for (var i = 0; i < length; i++)
            {
                results[offset + i] = assessed[i];
            }
        }

        return results;
    }

    /// <summary>
    /// One batch, timed and recorded to the ledger whatever happens to it.
    /// </summary>
    /// <remarks>
    /// The recording wraps the call rather than sitting inside it because every exit has to be
    /// accounted for - a throw, a timeout, an unparseable body and a partially usable answer are
    /// all things somebody needs to be able to see, and it was the last of those that went
    /// unnoticed for a night.
    /// </remarks>
    private async Task<CandidacyAssessment?[]> AssessOneBatchAsync(
        string profileText,
        string declared,
        CandidacyRequest[] batch,
        CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        var (results, reason) = await RunOneBatchAsync(profileText, declared, batch, ct);

        if (callLog is null)
        {
            return results;
        }

        var returned = results.Count(r => r is not null);

        var outcome = returned == batch.Length
            ? AiCallOutcome.Succeeded
            : returned == 0 ? AiCallOutcome.Failed : AiCallOutcome.PartiallyDiscarded;

        // Guarded even though IAiCallLog says implementations must not throw. A comment saying
        // "must not" is the kind of guarantee this session spent a day disproving, and the cost
        // of being wrong here is losing the assessment the call just paid for.
        try
        {
            await callLog.RecordAsync(
                AiCallRecord.Create(
                    _time.GetUtcNow(),
                    LedgerOperation,
                    _options.BulkDeployment,
                    outcome,
                    batch.Length,
                    returned,
                    (long)_time.GetElapsedTime(started).TotalMilliseconds,
                    reason,
                    // The postings that went unassessed, which is what a reader needs in order
                    // to know what was lost rather than merely that something was.
                    [.. batch.Where((_, i) => results[i] is null).Select(r => r.PostingId)]),
                ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not record the candidacy assessment to the AI ledger.");
        }

        return results;
    }

    private async Task<(CandidacyAssessment?[] Results, string? Reason)> RunOneBatchAsync(
        string profileText,
        string declared,
        CandidacyRequest[] batch,
        CancellationToken ct)
    {
        var results = new CandidacyAssessment?[batch.Length];
        string? reason = null;
        var roles = new StringBuilder(8_000);

        for (var i = 0; i < batch.Length; i++)
        {
            var request = batch[i];

            roles
                .Append("ROLE ").Append(i).AppendLine()
                .Append("Title: ").AppendLine(request.Title)
                .Append("Company: ").AppendLine(request.Company ?? "(not stated)")
                .Append("Pre-computed match score: ").Append(request.Match.Score).AppendLine("/100")
                .Append("Requirements the profile already meets: ")
                .AppendLine(Describe(request.Match.Matched))
                .Append("Requirements the profile does not meet: ")
                .AppendLine(Describe(request.Match.Gaps))
                .AppendLine("Advert:")
                .AppendLine(Truncate(request.Text, MaxPostingChars))
                .AppendLine();
        }

        var arguments = new KernelArguments(AiPrompt.Bulk(_options, reasoningEffort: "medium"))
        {
            ["profile"] = profileText,
            ["declared"] = declared,
            ["roles"] = roles.ToString(),
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
                "Candidacy assessment of {Count} role(s) timed out after {Seconds}s.",
                batch.Length, _options.TimeoutSeconds);
            return (results, $"timed out after {_options.TimeoutSeconds}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The deterministic score is already stored and is what the UI falls back to, so a
            // failure here degrades the shortlist rather than losing it.
            logger?.LogWarning(ex, "Candidacy assessment failed for {Count} role(s).", batch.Length);
            return (results, $"{ex.GetType().Name}: {ex.Message}");
        }

        var json = AiJson.ExtractJsonObject(response);

        if (json is null)
        {
            logger?.LogWarning("Candidacy assessment returned no JSON object.");
            return (results, "response carried no JSON object");
        }

        try
        {
            reason = Distribute(json, batch, results);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Candidacy assessment returned malformed JSON.");
            reason = $"malformed JSON: {ex.Message}";
        }

        return (results, reason);
    }

    /// <summary>
    /// Places each returned assessment against the role it answers.
    /// </summary>
    /// <remarks>
    /// Checked, not trusted, for the same reason the extractor checks its own indices: an
    /// assessment attached to the wrong posting reads as entirely plausible and there is nothing
    /// downstream that can catch it. Anything ambiguous is dropped and re-assessed on the next
    /// sweep.
    /// </remarks>
    private string? Distribute(string json, CandidacyRequest[] batch, CandidacyAssessment?[] results)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("assessments", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            logger?.LogWarning("Candidacy assessment carried no assessments array.");
            return "response carried no assessments array";
        }

        var seen = new HashSet<int>();
        var rejected = new List<string>();

        foreach (var item in array.EnumerateArray())
        {
            if (Int(item, "index") is not { } index || index < 0 || index >= batch.Length || !seen.Add(index))
            {
                // Says which of the three it was. "Unusable" on its own cost a night's
                // diagnosis: a wrong type, an out-of-range number and a repeat are different
                // faults with different fixes, and the log could not tell them apart.
                var described = DescribeIndex(item);

                logger?.LogWarning(
                    "Candidacy assessment returned an unusable role index: {Index} against a "
                    + "batch of {BatchSize}.",
                    described,
                    batch.Length);

                rejected.Add(described);
                continue;
            }

            results[index] = new CandidacyAssessment
            {
                Verdict = ParseVerdict(String(item, "verdict")),
                Score = Math.Clamp(Int(item, "score") ?? 0, 0, 100),
                Rationale = String(item, "rationale") is { Length: > 0 } rationale
                    ? Truncate(rationale, 2_000)
                    : null,
                Strengths = Strings(item, "strengths"),
                Gaps = Strings(item, "gaps"),
                Emphasise = Strings(item, "emphasise"),
                Model = _options.BulkDeployment,
                PayloadJson = item.GetRawText(),
            };
        }

        // Summarised rather than listed one per line: the ledger wants "why", and six identical
        // rejections are one fault, not six.
        return rejected.Count == 0
            ? null
            : $"{rejected.Count} of {batch.Length} role indices unusable: "
                + string.Join(", ", rejected.Distinct().Take(5));
    }

    /// <summary>
    /// The candidate's explicit claims, as labels rather than keys.
    /// </summary>
    /// <remarks>
    /// Labels, because the model is reading prose and a key like <c>skill.kubernetes</c> is not
    /// what an advert says. The keys matter to the join, not to the judgement - this pass
    /// returns sentences, never concept keys, so nothing it says can enter the vocabulary.
    /// </remarks>
    private static string DescribeDeclared(CandidateProfile profile)
    {
        if (profile.DeclaredSkills.Count == 0)
        {
            return "(none stated)";
        }

        var graph = ConceptGraph.Default;
        var builder = new StringBuilder(1_000);

        foreach (var skill in profile.DeclaredSkills)
        {
            if (!graph.TryGet(skill.ConceptKey, out var concept))
            {
                continue;
            }

            builder.Append("- ").Append(concept.Label).Append(" (").Append(Level(skill.Polarity)).Append(')');

            if (skill.Years is { } years)
            {
                builder.Append(", ").Append(years).Append(" years");
            }

            builder.AppendLine();
        }

        return builder.Length == 0 ? "(none stated)" : builder.ToString();
    }

    private static string Level(AssertionPolarity polarity) => polarity switch
    {
        AssertionPolarity.Expert => "expert",
        AssertionPolarity.Proficient => "proficient",
        AssertionPolarity.Familiar => "familiar",
        _ => "unstated",
    };

    private static string Describe(IReadOnlyList<ConceptMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "(none)";
        }

        var graph = ConceptGraph.Default;

        return string.Join(", ", matches
            .Take(30)
            .Select(m => graph.TryGet(m.RequiredKey, out var concept)
                ? m.Relation == MatchRelation.Exact
                    ? concept.Label
                    : $"{concept.Label} (via {Held(graph, m.HeldKey)})"
                : m.RequiredKey));
    }

    private static string Describe(IReadOnlyList<ConceptGap> gaps)
    {
        if (gaps.Count == 0)
        {
            return "(none)";
        }

        var graph = ConceptGraph.Default;

        return string.Join(", ", gaps
            .Take(30)
            .Select(g => graph.TryGet(g.RequiredKey, out var concept)
                ? g.Demand == AssertionPolarity.Required
                    ? $"{concept.Label} (stated as essential)"
                    : concept.Label
                : g.RequiredKey));
    }

    private static string Held(ConceptGraph graph, string key)
        => graph.TryGet(key, out var concept) ? concept.Label : key;

    private static CandidacyVerdict ParseVerdict(string? value) => value?.ToLowerInvariant() switch
    {
        "strong" => CandidacyVerdict.Strong,
        "possible" => CandidacyVerdict.Possible,
        "weak" => CandidacyVerdict.Weak,
        _ => CandidacyVerdict.Unknown,
    };

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
            {
                values.Add(Truncate(value, 400));
            }
        }

        return values;
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// An integer property, whether the model quoted it or not.
    /// </summary>
    /// <remarks>
    /// A JSON string holding a number is accepted deliberately. This used to demand
    /// <see cref="JsonValueKind.Number"/>, and on 2026-08-28 five of nine batches were discarded
    /// whole - every role in them - which is the signature of a response that is well formed and
    /// typed differently, not one that is wrong. The prompt asking for the index to be "copied
    /// exactly" from its heading is an invitation to copy it as text.
    ///
    /// This concedes nothing that matters. The guarantee worth having is that an answer lands
    /// against the role it was written for, and that is enforced by the range and duplicate
    /// checks in <see cref="Distribute"/>, which are untouched. Reading "3" as 3 is not trusting
    /// the model; it is parsing it.
    /// </remarks>
    private static int? Int(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// What an unusable index actually held, for the warning. Never the whole item.
    /// </summary>
    /// <remarks>
    /// The rest of the entry carries a rationale written about the candidate, so logging the
    /// item wholesale would put profile-derived prose in telemetry. The index alone is a number
    /// or a short token and says everything needed to tell a type problem from a range one.
    /// </remarks>
    private static string DescribeIndex(JsonElement item)
    {
        if (!item.TryGetProperty("index", out var value))
        {
            return "absent";
        }

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

        return $"{value.ValueKind}:{Truncate(raw, 20)}";
    }

    private static string Truncate(string? value, int max)
        => value is null ? string.Empty : value.Length <= max ? value : value[..max];
}
