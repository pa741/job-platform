using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;

namespace JobPlatform.Core.Applications;

/// <summary>The posting a document is being tailored to, as the writer needs it.</summary>
/// <param name="PostingId">Correlation only. Never sent to the model.</param>
/// <param name="Title">The advert title, which the cover letter names.</param>
/// <param name="Company">Who is hiring. Null where the board did not say, and then not invented.</param>
/// <param name="Text">The advert body.</param>
public sealed record PostingBrief(
    long PostingId,
    string Title,
    string? Company,
    string Text);

/// <summary>What to write, and everything that has already been decided about the pair.</summary>
/// <param name="Profile">The candidate's own record. The only source of biographical fact.</param>
/// <param name="Posting">What they are applying to.</param>
/// <param name="Match">
/// The deterministic breakdown. Its gaps are the list of things the document must not claim.
/// </param>
/// <param name="Assessment">
/// The bulk model's judgement, where one exists. Its <c>Emphasise</c> list is what the document
/// leads with, so the CV a candidate receives argues the case they were already told they had.
/// </param>
/// <param name="Instructions">
/// Anything the candidate wants to steer. Free text, appended as guidance and never as
/// permission - it cannot license a claim the profile does not support.
/// </param>
public sealed record ApplicationRequest(
    CandidateProfile Profile,
    PostingBrief Posting,
    MatchResult Match,
    CandidacyAssessment? Assessment = null,
    string? Instructions = null);

/// <summary>
/// A tailored CV and cover letter, as markdown.
/// </summary>
/// <remarks>
/// <b>Markdown, and never HTML.</b> The renderer walks a parsed markdown tree and emits a
/// document from a fixed set of node types, so there is no path by which model output becomes
/// markup that anything executes or styles. The layout belongs to this repository; the model
/// supplies words and structure only.
/// </remarks>
public sealed record ApplicationDraft
{
    /// <summary>
    /// Bumped when the prompt or the rendering changes what the same input would produce.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>The CV, tailored to this posting. Headings, dates, bullet points.</summary>
    public required string CurriculumVitaeMarkdown { get; init; }

    /// <summary>The cover letter. Prose, no headings beyond the addressee.</summary>
    public required string CoverLetterMarkdown { get; init; }

    /// <summary>
    /// What the writer chose to lead with, in its own words.
    /// </summary>
    /// <remarks>
    /// Returned so the candidate can see the argument the document is making before reading
    /// the document, and so a regeneration with different instructions is comparable to the
    /// one before it.
    /// </remarks>
    public IReadOnlyList<string> Emphasised { get; init; } = [];

    public string? Model { get; init; }

    public int Version { get; init; } = CurrentVersion;
}

/// <summary>
/// Writes the documents a candidate actually sends.
/// </summary>
/// <remarks>
/// The one path in this system that runs on the expensive deployment, and the one place that is
/// obviously right: extraction and assessment run across a corpus and are judged in aggregate,
/// while this runs once per application and is judged by a human being reading it. The cost
/// ratio between the two deployments is roughly twenty-five to one and the call ratio is
/// several thousand to one in the other direction.
///
/// Registered <b>only</b> where a Kernel is, so consumers take <c>IApplicationWriter?</c>.
/// </remarks>
public interface IApplicationWriter
{
    /// <summary>Null when the model returned nothing usable. Never throws for a bad response.</summary>
    Task<ApplicationDraft?> WriteAsync(ApplicationRequest request, CancellationToken ct = default);
}
