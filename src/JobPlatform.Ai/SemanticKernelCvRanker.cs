using System.Text.Json;
using JobPlatform.Core.Matching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace JobPlatform.Ai;

/// <summary>
/// Reranks a shortlist through Semantic Kernel, returning a score, a rationale, and the
/// skills a posting matched or is missing.
/// </summary>
/// <remarks>
/// Semantic Kernel owns the abstraction here: the prompt is a Kernel prompt template with
/// named arguments, invoked through the Kernel, so swapping the model provider is a
/// registration change and nothing in this class moves. Underneath it, the chat service is
/// the official Anthropic SDK adapted via Microsoft.Extensions.AI - see
/// <see cref="MatchingRegistration"/> for why that rather than an SK Anthropic connector.
///
/// This is the second stage only. <see cref="CvMatchingService"/> has already narrowed the
/// candidate set with the keyword ranker, so what arrives is a shortlist whose size is set by
/// configuration rather than by how many postings exist - which is what keeps the cost of a
/// match request bounded and predictable.
/// </remarks>
public sealed class SemanticKernelCvRanker : ICvRanker
{
    /// <summary>
    /// The instruction half of the prompt. Kept apart from the data half so it is
    /// byte-identical on every request, which is what makes it worth caching upstream.
    /// </summary>
    private const string SystemPrompt =
        """
        You rank job postings against a candidate's CV for a job-market analytics platform.

        For each posting you are given, judge how well it fits the candidate and return:
          - postingId: the id exactly as given. Never invent one.
          - score: 0-100. 100 means the candidate is an obvious fit. Use the full range;
            if every posting is mediocre, say so with low scores rather than compressing
            them into a narrow band at the top.
          - rationale: one or two sentences, concrete and specific to this posting. Name the
            actual overlap or the actual gap. Never restate the job title back as a reason.
          - matchedSkills: skills the CV evidences that this posting asks for.
          - missingSkills: skills the posting asks for that the CV does not evidence.

        Judge on evidence in the CV text, not on inference from job titles. Seniority
        mismatch in either direction is a real gap and should lower the score. Scraped
        postings are frequently truncated or vague: when a posting says little, score it
        moderately and say the posting was thin rather than inventing requirements for it.

        Return every posting you are given, ranked best first, as JSON of the form
        {"matches":[{"postingId":123,"score":87,"rationale":"...","matchedSkills":[],"missingSkills":[]}]}
        Return the JSON object alone, with no prose or code fences around it.
        """;

    private const string PromptTemplate =
        """
        {{$systemPrompt}}

        Rank these {{$candidateCount}} postings for this CV.

        {{$payload}}
        """;

    private readonly Kernel _kernel;
    private readonly SemanticKernelOptions _options;
    private readonly ILogger<SemanticKernelCvRanker> _logger;

    public SemanticKernelCvRanker(
        Kernel kernel,
        IOptions<SemanticKernelOptions> options,
        ILogger<SemanticKernelCvRanker> logger)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(options);

        _kernel = kernel;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => _options.ProviderName;

    public async Task<IReadOnlyList<PostingMatch>> RankAsync(
        CvProfile profile,
        IReadOnlyList<MatchCandidate> candidates,
        int topN,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var arguments = new KernelArguments(new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["max_tokens"] = _options.MaxTokens,
            },
        })
        {
            ["systemPrompt"] = SystemPrompt,
            ["candidateCount"] = candidates.Count,
            ["payload"] = BuildPayload(profile, candidates),
        };

        var result = await _kernel.InvokePromptAsync(
            PromptTemplate, arguments, cancellationToken: timeout.Token);

        var text = result.GetValue<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Ranking response carried no text content.");
            return [];
        }

        return Parse(text, candidates, topN);
    }

    private string BuildPayload(CvProfile profile, IReadOnlyList<MatchCandidate> candidates)
        => JsonSerializer.Serialize(
            new
            {
                cv = new
                {
                    text = profile.RawText,
                    detectedSkills = profile.Skills,
                    yearsExperience = profile.YearsExperience,
                },
                postings = candidates.Select(c => new
                {
                    id = c.PostingId,
                    title = c.Title,
                    company = c.Company,
                    location = c.Location,
                    remote = c.IsRemote,
                    jobType = c.JobType,
                    salary = c.MinAmount is null && c.MaxAmount is null
                        ? null
                        : $"{c.MinAmount}-{c.MaxAmount} {c.Currency}".Trim(),
                    description = c.Description,
                }),
            },
            SerializeOptions);

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Maps the model's output back onto the candidates it was given.
    /// </summary>
    /// <remarks>
    /// The JSON is located inside the response rather than assumed to be the whole of it.
    /// Semantic Kernel's execution settings are provider-neutral, so they cannot express the
    /// Anthropic-native structured-output constraint that would guarantee a bare JSON body -
    /// that is the concrete price of routing through the abstraction, and tolerating a code
    /// fence or a sentence of preamble is what it costs to pay it.
    ///
    /// Ids are validated against the shortlist rather than trusted; a model can invent one.
    /// <see cref="CvMatchingService"/> enforces the same rule for every ranker, so this is
    /// the inner of two nets, kept because it can name which ids were wrong.
    /// </remarks>
    private IReadOnlyList<PostingMatch> Parse(
        string response, IReadOnlyList<MatchCandidate> candidates, int topN)
    {
        var json = ExtractJsonObject(response);

        if (json is null)
        {
            _logger.LogError("Ranking response contained no JSON object.");
            return [];
        }

        RankingResult? result;

        try
        {
            result = JsonSerializer.Deserialize<RankingResult>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Ranking response was not valid JSON.");
            return [];
        }

        if (result?.Matches is null)
        {
            return [];
        }

        var known = candidates.Select(c => c.PostingId).ToHashSet();
        var seen = new HashSet<long>();
        var matches = new List<PostingMatch>();

        foreach (var match in result.Matches)
        {
            if (!known.Contains(match.PostingId) || !seen.Add(match.PostingId))
            {
                _logger.LogWarning(
                    "Discarding ranked posting {PostingId}: not in the shortlist, or a duplicate.",
                    match.PostingId);
                continue;
            }

            matches.Add(new PostingMatch
            {
                PostingId = match.PostingId,
                Score = Math.Clamp(match.Score, 0, 100),
                Rationale = match.Rationale,
                MatchedSkills = match.MatchedSkills ?? [],
                MissingSkills = match.MissingSkills ?? [],
            });
        }

        return matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.PostingId)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// The outermost <c>{...}</c> span, so a code fence or a line of preamble does not
    /// defeat parsing. Internal to this class and exposed for its tests.
    /// </summary>
    internal static string? ExtractJsonObject(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var start = response.IndexOf('{', StringComparison.Ordinal);
        var end = response.LastIndexOf('}');

        return start >= 0 && end > start ? response[start..(end + 1)] : null;
    }

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record RankingResult(IReadOnlyList<RankedPosting>? Matches);

    private sealed record RankedPosting(
        long PostingId,
        double Score,
        string? Rationale,
        IReadOnlyList<string>? MatchedSkills,
        IReadOnlyList<string>? MissingSkills);
}
