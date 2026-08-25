using System.Globalization;
using System.Text;
using System.Text.Json;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace JobPlatform.Ai.Applications;

/// <summary>
/// Writes the tailored CV and cover letter, on the writing deployment.
/// </summary>
/// <remarks>
/// The only path in this system that runs on the expensive model, and the only one where that
/// is obviously right. Extraction and assessment sweep a corpus and are judged in aggregate;
/// this produces one document, for one person, that a hiring manager reads - and it runs once
/// per application rather than once per posting. The price ratio between the deployments runs
/// the opposite way to the call ratio, which is the whole argument for having two.
///
/// <b>The profile is the only source of biographical fact.</b> The prompt is built so that
/// every claim the model can make has to come from a field the candidate filled in, and the
/// match's gap list is passed in explicitly as the set of things it must not claim. A CV that
/// invents a year of Kubernetes is not a better CV - it is one that falls apart in the
/// interview, and it is the candidate rather than this system that pays for it.
///
/// Output is markdown. The renderer walks a parsed tree and emits from a fixed set of node
/// types, so nothing the model returns is ever interpreted as markup.
/// </remarks>
public sealed class KernelApplicationWriter(
    Kernel kernel,
    IOptions<AzureOpenAiOptions> options,
    ILogger<KernelApplicationWriter>? logger = null) : IApplicationWriter
{
    private readonly AzureOpenAiOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private const int MaxPostingChars = 8_000;

    private const string PromptTemplate =
        """
        You are writing a job application for the candidate described below.

        Return ONLY a JSON object.

        Schema:
        {
          "cv": "<the tailored CV, as markdown>",
          "coverLetter": "<the cover letter, as markdown>",
          "emphasised": ["<what this draft leads with, one short sentence each>"]
        }

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

        Rules for the CV:
        - Every employer, date, qualification and technology must come from THE CANDIDATE
          section. Invent nothing. If a section would be empty, omit the section.
        - Nothing in the "does NOT show" list may be claimed, implied, or listed as a skill.
          Tailoring means choosing what to lead with, never adding what is not there.
        - Reorder and rewrite what is there so the parts this role wants come first. Rewriting
          a bullet point to foreground the relevant part of real work is the job; adding a
          bullet point that did not happen is not.
        - Structure: an H1 with the candidate's name, a contact line, then "## Summary",
          "## Skills", "## Experience", "## Education", and "## Projects" where each has
          content. Under Experience use "### Title, Company" and an italic date line, then
          bullet points.
        - Bullet points lead with what was done and name the outcome where the profile gives
          one. Three to five per recent role, fewer for older ones.
        - Markdown only: headings, bold, italics, bullet lists, links. No tables, no HTML, no
          images, no horizontal rules.
        - British English.

        Rules for the cover letter:
        - Address the company by name where one is given; otherwise open without a salutation
          line rather than writing "Dear Hiring Manager" over an unknown recipient.
        - Four short paragraphs at most: why this role, the strongest relevant evidence, one
          honest note where a gap is worth naming, and a close.
        - Prose. No bullet points, no headings beyond the addressee, no reciting of the CV.
        - Never claim enthusiasm for something the advert does not describe.
        """;

    public async Task<ApplicationDraft?> WriteAsync(ApplicationRequest request, CancellationToken ct = default)
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
        };

        string response;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.WritingTimeoutSeconds));

            var result = await kernel.InvokePromptAsync(PromptTemplate, arguments, cancellationToken: timeout.Token);
            response = result.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning(
                "Application writing timed out after {Seconds}s.", _options.WritingTimeoutSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Application writing failed.");
            return null;
        }

        var json = AiJson.ExtractJsonObject(response);

        if (json is null)
        {
            logger?.LogWarning("Application writing returned no JSON object.");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var cv = String(root, "cv");
            var letter = String(root, "coverLetter");

            // Half a draft is worse than none: the caller would store it, the candidate would
            // open it, and the missing half would look like a rendering fault rather than a
            // model one.
            if (string.IsNullOrWhiteSpace(cv) || string.IsNullOrWhiteSpace(letter))
            {
                logger?.LogWarning("Application writing returned an incomplete draft.");
                return null;
            }

            return new ApplicationDraft
            {
                CurriculumVitaeMarkdown = cv,
                CoverLetterMarkdown = letter,
                Emphasised = Strings(root, "emphasised"),
                Model = _options.WritingDeployment,
            };
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Application writing returned malformed JSON.");
            return null;
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

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string? value, int max)
        => value is null ? string.Empty : value.Length <= max ? value : value[..max];
}
