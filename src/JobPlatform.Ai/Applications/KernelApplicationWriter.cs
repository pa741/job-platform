using System.Globalization;
using System.Text;
using System.Text.Json;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace JobPlatform.Ai.Applications;

/// <summary>
/// Writes the cover letter and the posting's own free text, on the writing deployment.
/// </summary>
/// <remarks>
/// The only path in this system that runs on the expensive model, and the only one where that
/// is obviously right. Extraction and assessment sweep a corpus and are judged in aggregate;
/// this produces one document, for one person, that a hiring manager reads - and it runs once
/// per application rather than once per posting. The price ratio between the deployments runs
/// the opposite way to the call ratio, which is the whole argument for having two.
///
/// <b>It no longer writes the CV, and that is the change this class exists to record.</b> On the
/// apply loop's first real run the model answered the "anything else we should know" box with the
/// candidate's citizenship and then <i>"I am an AI and they should have seen this."</i> The
/// sentence was stored, served through the pack, and was one browser step from an employer's form.
/// A guard drops that class of sentence now; a guard is a net under a trapeze. The CV is the
/// document a human reads first and the one a candidate is judged on, so it is now chosen from a
/// library the candidate authored rather than produced here - which removes the failure instead of
/// catching it, and takes the largest per-application call off the expensive deployment at the
/// same time.
///
/// <b>The profile is still the only source of biographical fact.</b> The prompt is built so that
/// every claim the model can make has to come from a field the candidate filled in, and the
/// match's gap list is passed in explicitly as the set of things it must not claim. A letter that
/// invents a year of Kubernetes is not a better letter - it is one that falls apart in the
/// interview, and it is the candidate rather than this system that pays for it.
///
/// Output is markdown. The renderer walks a parsed tree and emits from a fixed set of node
/// types, so nothing the model returns is ever interpreted as markup.
/// </remarks>
public sealed class KernelApplicationWriter(
    Kernel kernel,
    IOptions<AzureOpenAiOptions> options,
    ILogger<KernelApplicationWriter>? logger = null,
    IAiCallLog? callLog = null,
    TimeProvider? time = null) : IApplicationWriter
{
    /// <summary>Names this pass in the AI call ledger.</summary>
    public const string LedgerOperation = "application-writing";

    /// <summary>
    /// Names the tie-break in the ledger. Separate, because it is a different bill and a
    /// different question.
    /// </summary>
    /// <remarks>
    /// Merged into <see cref="LedgerOperation"/> the two would be indistinguishable in the one
    /// place anybody looks at what this system spends - and they are not comparable: writing is a
    /// long call on the expensive deployment that happens once per application, and this is a
    /// short one on the bulk deployment that happens only when two CVs tie. A ledger showing one
    /// operation with a bimodal duration is a ledger nobody can read a regression out of.
    /// </remarks>
    public const string ChoiceLedgerOperation = "cv-variant-choice";

    /// <summary>How many variants a ballot may carry before the prompt stops being a question.</summary>
    /// <remarks>
    /// Three, which is the spec's "the top two or three". It is enforced here as well as at the
    /// call site, because the ballot is built from <c>CvSelection.Tied</c> and that list can be
    /// the whole library - a posting stating nothing that discriminates ties everything. A caller
    /// that forgot to bound it would send six names and get back a preference rather than a
    /// choice; this is the second lock on the same door, in the file that pays for the tokens.
    /// </remarks>
    public const int MaxBallot = 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly AzureOpenAiOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private const int MaxPostingChars = 8_000;

    private const string PromptTemplate =
        """
        You are writing a job application for the candidate described below.

        Return ONLY a JSON object.

        Schema:
        {
          "coverLetter": "<the cover letter, as markdown>",
          "emphasised": ["<what this draft leads with, one short sentence each>"],
          "draftedAnswers": [{ "question": "<the question, copied exactly>", "answer": "<the answer>" }]
        }

        You are NOT writing a CV. The candidate keeps their own, and one of theirs is chosen to
        go with this letter; do not offer one, and do not restate it in prose.

        THE ROLE
        Title: {{$title}}
        Company: {{$company}}
        Advert:
        {{$advert}}

        THE CANDIDATE
        {{$profile}}

        Skills the candidate states explicitly:
        {{$declared}}

        What this role asks for that the candidate already has:
        {{$strengths}}

        What this role asks for that the candidate's record does NOT show:
        {{$gaps}}

        What to lead with:
        {{$emphasise}}

        Candidate's own instructions: {{$instructions}}

        Rules that bind everything you write here:
        - Every employer, date, qualification and technology must come from THE CANDIDATE
          section. Invent nothing.
        - Nothing in the "does NOT show" list may be claimed, implied, or listed as a skill.
          Tailoring means choosing what to lead with, never adding what is not there.
        - British English.

        Rules for the drafted answers:
        - Answer each question below, in the same words it is given, and only those questions.
        - {{$freeText}}
        - OMIT a question entirely rather than answering it thinly. An empty box is better than a
          paragraph that would fit any employer, which is detectable in one sentence and is read
          as a mailshot.
        - A paragraph is an easier place to overclaim than a bullet point, so the "does NOT
          show" list is repeated here rather than assumed to carry over.
        - Say nothing about the company the advert does not say. An invented fact about an
          employer is read by somebody who works there.
        - Prose, first person, no markdown, no headings, no bullet points. British English.

        Rules for the cover letter:
        - Address the company by name where one is given; otherwise open without a salutation
          line rather than writing "Dear Hiring Manager" over an unknown recipient.
        - Four short paragraphs at most: why this role, the strongest relevant evidence, one
          honest note where a gap is worth naming, and a close.
        - Prose. No bullet points, no headings beyond the addressee, no reciting of the CV.
        - Never claim enthusiasm for something the advert does not describe.
        """;

    /// <summary>
    /// The tie-break. A closed question over documents this call cannot read and will not write.
    /// </summary>
    /// <remarks>
    /// <b>Names and an advert, and deliberately not the CVs themselves.</b> The arithmetic has
    /// already scored every variant against this posting's requirements over the shared
    /// vocabulary; what it could not do is read the advert's prose, which is where the difference
    /// between two documents a point apart actually lives. Handing over the markdown would buy a
    /// slightly better-informed choice and reopen the exact failure this feature closes, because a
    /// model holding a CV is a model that can be asked to improve it.
    ///
    /// <b>The answer is one of a fixed set, and the prompt says the set out loud.</b> That is what
    /// makes it checkable: the caller re-checks the id against the ballot it offered, so a
    /// hallucinated id is refused rather than stored - the rule <c>KernelDocumentExtractor</c>
    /// follows for concept keys, for the same reason. Null is stated as a legitimate answer rather
    /// than left to be discovered, because a model with no way to abstain invents a preference.
    /// </remarks>
    private const string ChoicePromptTemplate =
        """
        Two or more of this candidate's CVs fit the role below equally well by the numbers.
        Choose which one to send.

        Return ONLY a JSON object.

        Schema:
        { "variantId": <one of the ids listed below, or null> }

        THE ROLE
        Title: {{$title}}
        Company: {{$company}}
        Advert:
        {{$advert}}

        THE CVs, by id, with the share of this advert's requirements each one answers:
        {{$ballot}}

        Rules:
        - Answer with one of the ids listed above, exactly as it is written, and nothing else.
        - Choose the one whose name best matches what THIS advert is asking for. The scores are
          already tied, so they are not the reason to prefer one.
        - Answer null where the advert gives you nothing to tell them apart. That is a correct
          answer and no CV is sent; guessing is not, because a CV aimed at the wrong role is a
          rejection nobody ever hears the reason for.
        - Do not write, rewrite, summarise or suggest a CV. You are picking from a list.
        """;

    /// <summary>
    /// Writes one application, timed and recorded to the ledger whatever happens to it.
    /// </summary>
    /// <remarks>
    /// This is the only consumer of the <c>writing</c> deployment, and Semantic Kernel falls back
    /// to whichever chat service is present rather than throwing when a prompt names one that is
    /// not registered. A ledger showing CVs served by <c>bulk</c> is how that gets noticed, which
    /// is why the deployment is recorded rather than assumed.
    ///
    /// One document per call, so requested is always one - but the pairing still matters: a
    /// person pressed a button and either got a CV or did not, and until now the "did not" left
    /// no trace beyond a log line.
    /// </remarks>
    public async Task<ApplicationDraft?> WriteAsync(ApplicationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = _time.GetTimestamp();
        var (draft, reason, prompt, usage) = await WriteCoreAsync(request, ct);

        if (callLog is not null)
        {
            try
            {
                await callLog.RecordAsync(
                    AiCallRecord.Create(
                        _time.GetUtcNow(),
                        LedgerOperation,
                        _options.WritingDeployment,
                        draft is null ? AiCallOutcome.Failed : AiCallOutcome.Succeeded,
                        requested: 1,
                        returned: draft is null ? 0 : 1,
                        (long)_time.GetElapsedTime(started).TotalMilliseconds,
                        reason,
                        draft is null ? [request.Posting.PostingId] : [],
                        // Offered, not decided. This is the prompt most worth guarding: it
                        // contains the candidate's whole profile, and the sink keeps it only
                        // for a failed call on a deployment that asked for prompts.
                        prompt,
                        usage),
                    ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not record the application writing call to the AI ledger.");
            }
        }

        return draft;
    }

    private async Task<(ApplicationDraft? Draft, string? Reason, string? Prompt, AiTokenUsage Usage)>
        WriteCoreAsync(
        ApplicationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = new KernelArguments(AiPrompt.Writing(_options))
        {
            ["title"] = request.Posting.Title,
            ["company"] = request.Posting.Company ?? "(not stated)",
            ["advert"] = Truncate(request.Posting.Text, MaxPostingChars),
            ["profile"] = Describe(request.Profile),
            ["declared"] = DescribeDeclared(request.Profile),
            ["strengths"] = Bullets(Labels(request.Match.Matched.Select(m => m.RequiredKey))),
            ["gaps"] = Bullets(Labels(request.Match.Gaps.Select(g => g.RequiredKey))),
            ["emphasise"] = Bullets(request.Assessment?.Emphasise ?? []),
            ["instructions"] = string.IsNullOrWhiteSpace(request.Instructions)
                ? "(none)"
                : Truncate(request.Instructions, 2_000),

            // The questions come from the catalogue rather than from this file, so the list the
            // model is asked to answer and the list the parser will accept back are the same one.
            ["freeText"] = FreeTextQuestions(),
        };

        // The prompt as the model will see it, so a failure can be replayed rather than
        // reconstructed by hand.
        var prompt = arguments.Aggregate(
            PromptTemplate,
            (text, pair) => text.Replace(
                $"{{{{${pair.Key}}}}}", pair.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));

        string response;
        var usage = default(AiTokenUsage);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.WritingTimeoutSeconds));

            var result = await kernel.InvokePromptAsync(PromptTemplate, arguments, cancellationToken: timeout.Token);
            response = result.ToString();
            usage = AiUsage.From(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning(
                "Application writing timed out after {Seconds}s.", _options.WritingTimeoutSeconds);
            return (null, $"timed out after {_options.WritingTimeoutSeconds}s", prompt, usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Application writing failed.");
            return (null, $"{ex.GetType().Name}: {ex.Message}", prompt, usage);
        }

        var json = AiJson.ExtractJsonObject(response);

        if (json is null)
        {
            logger?.LogWarning("Application writing returned no JSON object.");
            return (null, "response carried no JSON object", prompt, usage);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var letter = String(root, "coverLetter");

            // "Half a draft is worse than none" used to mean a CV without a letter or a letter
            // without a CV: the caller stored it, the candidate opened it, and the missing half
            // read as a rendering fault rather than a model one. There is no second half any
            // more, and the argument narrows rather than disappearing - it was never about the
            // arithmetic of two documents but about what an absence looks like to the person who
            // asked for one.
            //
            // The letter is what is left of that, and it is the whole of it. A draft with no
            // letter is a row that exists, satisfies `documentsReady`, takes the posting out of
            // the generation pass's queue, and offers an employer nothing - which is worse than
            // no row at all, because the failure is invisible and the posting will not come back.
            // The drafted answers are deliberately NOT part of the test: they are boxes a
            // candidate fills in by hand when they are absent, which is where every application
            // stood before they existed, and refusing the letter over a missing one would throw
            // away the expensive half to protect the cheap one.
            if (string.IsNullOrWhiteSpace(letter))
            {
                logger?.LogWarning("Application writing returned no cover letter.");
                return (null, "draft carried no cover letter", prompt, usage);
            }

            return (
                new ApplicationDraft
                {
                    // No CV, and none is read back either. A model that ignored the schema and
                    // wrote one anyway must not have it stored: a "cv" key quietly honoured here
                    // is this whole feature undone by a response nobody diffed.
                    CoverLetterMarkdown = letter,
                    Emphasised = Strings(root, "emphasised"),

                    // Absent, empty or malformed all read as "nothing drafted" - see above on why
                    // that is not the same test as the letter's.
                    DraftedAnswers = DraftedAnswers(root),
                    Model = _options.WritingDeployment,
                },
                null,
                prompt,
                usage);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Application writing returned malformed JSON.");
            return (null, $"malformed JSON: {ex.Message}", prompt, usage);
        }
    }

    /// <summary>
    /// Settles a tie between finished CVs, or declines to.
    /// </summary>
    /// <remarks>
    /// <b>On the bulk deployment, and that is not a saving to be reversed later.</b> Writing needs
    /// the expensive model because a person reads the sentences it produces; this returns one
    /// integer from a list of three, having read an advert the bulk deployment already reads for
    /// every posting in the corpus. Paying Sol prices for a multiple-choice question would be the
    /// same mistake the two deployments exist to avoid, in the direction nobody notices, because
    /// it works.
    ///
    /// <b>Recorded in the ledger whatever happens to it, like every other call here.</b> An
    /// abstention is a successful call that returned no choice, so the outcome is
    /// <c>Succeeded</c> with <c>returned: 0</c> rather than <c>Failed</c> - the distinction that
    /// matters to somebody reading the ledger is "did the provider answer", and a model correctly
    /// declining to guess is not a fault. A failure is a timeout, a malformed response, or an id
    /// off the ballot.
    ///
    /// <b>Never throws, for the reason nothing on this interface does.</b> The caller's next step
    /// is to send no CV and say why, which is a state it already has to handle - the ballot only
    /// exists because the arithmetic had already declined to choose.
    /// </remarks>
    public async Task<long?> ChooseCurriculumVitaeAsync(
        CvChoiceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ballot = (request.Ballot ?? []).Take(MaxBallot).ToList();

        // Nothing to choose between. Answered without a call rather than with one, because a
        // ballot of one is a decision the arithmetic already made and a ballot of none is a
        // caller bug - and both would otherwise be a bill for a question with one answer.
        if (ballot.Count < 2)
        {
            return null;
        }

        var started = _time.GetTimestamp();

        var arguments = new KernelArguments(AiPrompt.Bulk(_options))
        {
            ["title"] = request.Posting.Title,
            ["company"] = request.Posting.Company ?? "(not stated)",
            ["advert"] = Truncate(request.Posting.Text, MaxPostingChars),
            ["ballot"] = string.Join(
                "\n",
                ballot.Select(entry =>
                    $"- id {entry.VariantId.ToString(CultureInfo.InvariantCulture)}: "
                    + $"\"{entry.Label}\" - answers {entry.Score} of 100")),
        };

        var (chosen, reason) = await ChooseCoreAsync(ballot, arguments, ct);

        if (callLog is not null)
        {
            try
            {
                await callLog.RecordAsync(
                    AiCallRecord.Create(
                        _time.GetUtcNow(),
                        ChoiceLedgerOperation,
                        _options.BulkDeployment,
                        reason is null ? AiCallOutcome.Succeeded : AiCallOutcome.Failed,
                        requested: 1,
                        returned: chosen is null ? 0 : 1,
                        (long)_time.GetElapsedTime(started).TotalMilliseconds,
                        reason,
                        [request.Posting.PostingId],

                        // No prompt. It carries an advert, a handful of the candidate's own CV
                        // names and nothing else - so unlike the writing prompt there is no whole
                        // profile in it to guard, and a sink keeping prompts would be storing the
                        // labels a person chose for their own documents to buy nothing.
                        prompt: null),
                    ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not record the CV choice call to the AI ledger.");
            }
        }

        return chosen;
    }

    private async Task<(long? Chosen, string? Reason)> ChooseCoreAsync(
        List<CvVariantScore> ballot, KernelArguments arguments, CancellationToken ct)
    {
        string response;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var result = await kernel.InvokePromptAsync(
                ChoicePromptTemplate, arguments, cancellationToken: timeout.Token);

            response = result.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("The CV tie-break timed out after {Seconds}s.", _options.TimeoutSeconds);
            return (null, $"timed out after {_options.TimeoutSeconds}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "The CV tie-break failed.");
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }

        var json = AiJson.ExtractJsonObject(response);

        if (json is null)
        {
            return (null, "response carried no JSON object");
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("variantId", out var value))
            {
                return (null, "response named no variantId");
            }

            // An explicit null is the abstention the prompt asks for, and it is a success. It is
            // separated from a missing property deliberately: "I could not tell them apart" and
            // "I ignored the schema" want different reading in the ledger, and only the second is
            // a reason to look at the prompt.
            if (value.ValueKind is JsonValueKind.Null)
            {
                return (null, null);
            }

            // Read as a number or as a quoted number. The assessment pass lost a whole batch to
            // exactly this - a prompt saying "copied exactly" is an invitation to answer in a
            // string - and the fix there was to accept both rather than to argue with the model.
            var chosen = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(
                    value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => (long?)null,
            };

            // Re-checked against the ballot this call offered, on the rule KernelDocumentExtractor
            // follows for concept keys: an invented id is indistinguishable from a real one the
            // moment it is written onto a submission, and it would say a document went to an
            // employer that never did. Refused rather than repaired - there is no nearest variant.
            if (chosen is not { } variantId || !ballot.Any(entry => entry.VariantId == variantId))
            {
                return (null, "answer was not one of the variants offered");
            }

            return (variantId, null);
        }
        catch (JsonException ex)
        {
            return (null, $"malformed JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// The candidate, as structured text rather than as the extractor's document.
    /// </summary>
    /// <remarks>
    /// <see cref="CandidateProfile.ToDocument"/> exists to be read for concepts and leaves out
    /// everything a CV is actually made of - names, dates, contact details, the order roles were
    /// held in. This writes the whole record, because the model is being asked to reproduce it
    /// faithfully rather than to summarise it.
    /// </remarks>
    private static string Describe(CandidateProfile profile)
    {
        var builder = new StringBuilder(8_000);

        Line(builder, "Name", profile.FullName);
        Line(builder, "Headline", profile.Headline);
        Line(builder, "Email", profile.Email);
        Line(builder, "Phone", profile.Phone);
        Line(builder, "Location", Join(profile.LocationCity, profile.LocationCountry));

        foreach (var link in profile.Links)
        {
            Line(builder, link.Label, link.Url);
        }

        if (profile.YearsExperience is { } years)
        {
            Line(builder, "Total experience", $"{years} years");
        }

        Line(builder, "Summary", profile.Summary);

        if (profile.Experiences.Count > 0)
        {
            builder.AppendLine().AppendLine("EXPERIENCE");

            foreach (var experience in profile.Experiences)
            {
                builder
                    .Append("- ").Append(experience.Title)
                    .Append(", ").Append(experience.Company)
                    .Append(" (").Append(experience.Period()).AppendLine(")");

                Line(builder, "  Location", Join(experience.LocationCity, experience.LocationCountry));
                Line(builder, "  What they did", experience.Description);
            }
        }

        if (profile.Education.Count > 0)
        {
            builder.AppendLine().AppendLine("EDUCATION");

            foreach (var education in profile.Education)
            {
                builder
                    .Append("- ").Append(education.Qualification)
                    .Append(education.FieldOfStudy is { Length: > 0 } field ? $" in {field}" : string.Empty)
                    .Append(", ").Append(education.Institution)
                    .Append(education.EndDate is { } end ? $" ({end.Year.ToString(CultureInfo.InvariantCulture)})" : string.Empty)
                    .AppendLine(education.Grade is { Length: > 0 } grade ? $" - {grade}" : string.Empty);

                Line(builder, "  Detail", education.Description);
            }
        }

        if (profile.Projects.Count > 0)
        {
            builder.AppendLine().AppendLine("PROJECTS");

            foreach (var project in profile.Projects)
            {
                builder.Append("- ").Append(project.Name)
                    .AppendLine(project.Url is { Length: > 0 } url ? $" ({url})" : string.Empty);

                Line(builder, "  Detail", project.Description);
            }
        }

        if (profile.Certifications.Count > 0)
        {
            builder.AppendLine().AppendLine("CERTIFICATIONS");

            foreach (var certification in profile.Certifications)
            {
                builder
                    .Append("- ").Append(certification.Name)
                    .Append(certification.Issuer is { Length: > 0 } issuer ? $", {issuer}" : string.Empty)
                    .AppendLine(certification.Year is { } year ? $" ({year.ToString(CultureInfo.InvariantCulture)})" : string.Empty);
            }
        }

        if (profile.Languages.Count > 0)
        {
            builder.AppendLine().AppendLine("LANGUAGES");

            foreach (var language in profile.Languages)
            {
                builder.Append("- ").Append(language.Name)
                    .AppendLine(language.Level is { Length: > 0 } level ? $" ({level})" : string.Empty);
            }
        }

        return builder.ToString();
    }

    private static string DescribeDeclared(CandidateProfile profile)
    {
        var graph = ConceptGraph.Default;
        var builder = new StringBuilder(1_000);

        foreach (var skill in profile.DeclaredSkills)
        {
            if (!graph.TryGet(skill.ConceptKey, out var concept))
            {
                continue;
            }

            builder.Append("- ").Append(concept.Label);

            if (skill.Years is { } years)
            {
                builder.Append(" (").Append(years).Append(" years)");
            }

            builder.AppendLine();
        }

        return builder.Length == 0 ? "(none stated)" : builder.ToString();
    }

    private static IEnumerable<string> Labels(IEnumerable<string> keys)
    {
        var graph = ConceptGraph.Default;

        return keys
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .Select(key => graph.TryGet(key, out var concept) ? concept.Label : key);
    }

    private static string Bullets(IEnumerable<string> values)
    {
        var builder = new StringBuilder(1_000);

        foreach (var value in values)
        {
            builder.Append("- ").AppendLine(value);
        }

        return builder.Length == 0 ? "(none)" : builder.ToString();
    }

    private static void Line(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(label).Append(": ").AppendLine(value.Trim());
        }
    }

    private static string? Join(string? city, string? country)
        => (city, country) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{city}, {country}",
            ({ Length: > 0 }, _) => city,
            (_, { Length: > 0 }) => country,
            _ => null,
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
                values.Add(value.Length <= 400 ? value : value[..400]);
            }
        }

        return values;
    }

    /// <summary>
    /// The drafted free-text answers, keeping only the questions that were actually asked.
    /// </summary>
    /// <remarks>
    /// <b>The question is matched against the catalogue rather than taken as given.</b> The model
    /// is told to copy each question exactly and mostly will, but a returned question that is not
    /// on the list is one nobody will ever be asked - and storing it would put prose in front of
    /// the candidate under a heading this system did not choose. Matching also repairs the
    /// ordinary case where the wording drifts by a word, since the catalogue's spelling is the one
    /// the form-answer store keys on.
    ///
    /// Everything else is dropped quietly. This is the half of a draft that is allowed to be
    /// missing, so a malformed entry costs a box the candidate fills in by hand rather than a
    /// regeneration.
    /// </remarks>
    /// <summary>The per-posting questions and their guidance, as the prompt states them.</summary>
    /// <remarks>
    /// Built from <see cref="DraftedAnswerCatalog.PerPosting"/> so that adding a question is one
    /// edit in one place. The word limit is stated per question because these are boxes with
    /// counters on real forms, and an answer cut off mid-sentence reads to an employer as
    /// carelessness rather than as a limit.
    /// </remarks>
    private static string FreeTextQuestions()
        => string.Join(
            "\n        - ",
            DraftedAnswerCatalog.PerPosting.Select(prompt =>
                $"\"{prompt.QuestionText}\" - {prompt.Guidance} At most {prompt.MaxWords} words."));

    private static IReadOnlyList<DraftedAnswer> DraftedAnswers(JsonElement element)
    {
        if (!element.TryGetProperty("draftedAnswers", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var answers = new List<DraftedAnswer>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var question = String(item, "question");
            var answer = String(item, "answer");

            if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            {
                continue;
            }

            // Dropped here rather than stored and filtered on the way out, so the sentence never
            // reaches the database at all. On the first real run the model answered the
            // "anything else" box with the candidate's citizenship and then "I am an AI and they
            // should have seen this" - which was stored, served through the pack, and would have
            // been typed into an employer's form under somebody's name.
            if (!DraftedAnswerCatalog.IsCandidateVoice(answer))
            {
                continue;
            }

            var known = DraftedAnswerCatalog.PerPosting.FirstOrDefault(prompt => string.Equals(
                prompt.QuestionText, question.Trim(), StringComparison.OrdinalIgnoreCase));

            if (known is null || answers.Any(existing => string.Equals(
                    existing.QuestionText, known.QuestionText, StringComparison.Ordinal)))
            {
                continue;
            }

            answers.Add(new DraftedAnswer(
                known.QuestionText,
                answer.Trim(),
                FreeTextCategory.PostingSpecific));
        }

        return answers;
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string? value, int max)
        => value is null ? string.Empty : value.Length <= max ? value : value[..max];
}
