using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;

namespace JobPlatform.Core.Applications;

/// <summary>The posting a document is being tailored to, as the writer needs it.</summary>
/// <param name="PostingId">Correlation only. Never sent to the model.</param>
/// <param name="Title">The advert title, which the cover letter names.</param>
/// <param name="Company">Who is hiring. Null where the board did not say, and then not invented.</param>
/// <param name="Text">The advert body.</param>
/// <param name="SourceBoard">
/// The board this listing was scraped from, where it is known. Not written into any document -
/// it is what makes "how did you hear about us" answerable with the truth, and writing "LinkedIn"
/// on an advert found on Indeed is a small lie in a document carrying the candidate's name.
/// </param>
public sealed record PostingBrief(
    long PostingId,
    string Title,
    string? Company,
    string Text,
    string? SourceBoard = null);

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
/// A cover letter and the free text one posting asks for, as markdown.
/// </summary>
/// <remarks>
/// <b>Markdown, and never HTML.</b> The renderer walks a parsed markdown tree and emits a
/// document from a fixed set of node types, so there is no path by which model output becomes
/// markup that anything executes or styles. The layout belongs to this repository; the model
/// supplies words and structure only.
///
/// <b>The CV is no longer one of these, and the reason is a sentence a model wrote.</b> Asked what
/// else an employer should know, it gave the candidate's citizenship - correctly, out of their own
/// summary - and then added <i>"I am an AI and they should have seen this."</i> It was stored,
/// served through the pack, and would have been typed into an employer's form under a person's
/// name. A guard drops that class of sentence now, and a guard is a net under a trapeze. What
/// removes the failure rather than catching it is not writing the document at all: the candidate
/// authors a small library of CVs and <see cref="CvVariantSelector"/> chooses one, so the model
/// cannot invent a claim about work it is not writing about.
///
/// <b>What is left is what is genuinely per-posting.</b> The cover letter and the drafted free-text
/// answers are short, cheap, low-risk, and answerable only by something holding this advert - and
/// the advert is already in hand when they are written. The division to keep is that the model
/// <i>writes the bespoke short things and chooses among the curated long ones</i>, which is why
/// <see cref="IApplicationWriter"/> now carries both halves rather than one.
/// </remarks>
public sealed record ApplicationDraft
{
    /// <summary>
    /// Bumped when the prompt or the rendering changes what the same input would produce.
    /// </summary>
    /// <remarks>
    /// Three, because the prompt stopped asking for a CV. That is the largest change this constant
    /// has ever recorded: version 2 and version 3 of the same posting differ by a whole document,
    /// so a reader comparing two drafts without it would see a rendering fault where there is a
    /// deliberate absence.
    /// </remarks>
    public const int CurrentVersion = 3;

    /// <summary>
    /// The CV, where one was written. Null for anything drafted from version 3 onwards.
    /// </summary>
    /// <remarks>
    /// <b>Nullable rather than removed, and the column stays with it.</b> Applications made last
    /// year have to remain explicable: "what exactly did we send them" is a question about a
    /// document that went into somebody else's system, and it cannot be answered by a field that
    /// was dropped because nothing writes it any more. It is the same rule archiving follows on
    /// the other side of this feature - a retired variant is removed from selection and from
    /// nothing else - and it is the same rule for the same reason, which is that this system's
    /// record of what it sent is the only copy anybody here controls.
    ///
    /// <b>Null is therefore ordinary and not a failure.</b> Every consumer already treated a blank
    /// as "there is no file of that kind to offer" - the pack said so in its note and the download
    /// route answered 404 - because a render could always fail on its own. Nothing had to learn a
    /// new state; what changed is which state is the common one.
    /// </remarks>
    public string? CurriculumVitaeMarkdown { get; init; }

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

    /// <summary>
    /// Answers to the free-text boxes an application form is likely to ask for.
    /// </summary>
    /// <remarks>
    /// <b>Drafted here because here is where the advert is.</b> These questions - why this
    /// company, why this role - are answerable only by somebody holding the posting, and the
    /// writer is already holding it along with the profile, the strengths and the gap list. Asked
    /// at apply time they stop an unattended run to wait for a person; answered from a canned
    /// paragraph they are worse than an empty box, because prose that would fit any employer is
    /// detectable in a sentence and is the kind of thing that gets a candidate remembered badly.
    ///
    /// <b>Empty is a legitimate answer and the prompt says so.</b> A question the advert gives
    /// nothing to answer with - a product line at a company that names no products - is left
    /// undrafted rather than invented, because an invented fact about a company is read by
    /// somebody who works there.
    ///
    /// The gap list bounds these exactly as it bounds the CV: nothing in it may be claimed here
    /// either. A paragraph is an easier place to overclaim than a bullet point, which is why the
    /// rule is repeated in the prompt rather than assumed to carry over.
    /// </remarks>
    public IReadOnlyList<DraftedAnswer> DraftedAnswers { get; init; } = [];

    public string? Model { get; init; }

    public int Version { get; init; } = CurrentVersion;
}

/// <summary>
/// The tie the arithmetic refused to settle, as the model is asked to settle it.
/// </summary>
/// <remarks>
/// <b>Labels and scores, never markdown.</b> The ballot carries what
/// <see cref="CvVariantSelector"/> already computed, which is a name the candidate chose and a
/// number this system derived - so the model is asked which of these finished documents to send
/// and is given no document to rewrite. That is the same narrowing <see cref="CvVariantFacts"/>
/// makes for the arithmetic, applied one layer further out: a caller that cannot reach the prose
/// cannot be tempted to ask for a better version of it.
///
/// <b>The advert is the whole of the new information.</b> The scores are what the graph could say
/// about this pair and they came out too close to separate; what a model adds is the half the
/// vocabulary cannot see - that an advert about payments infrastructure wants the backend CV
/// rather than the data one, in words neither concept list carries.
/// </remarks>
/// <param name="Posting">The advert, as the writer already receives it.</param>
/// <param name="Ballot">
/// The variants to choose between, best first. <b>Bounded by the caller</b>: an advert stating
/// nothing that discriminates ties the whole library, and a ballot of six is a question nobody
/// should be answering from a list of names.
/// </param>
public sealed record CvChoiceRequest(PostingBrief Posting, IReadOnlyList<CvVariantScore> Ballot);

/// <summary>
/// Writes the bespoke short things, and chooses among the curated long ones.
/// </summary>
/// <remarks>
/// The one path in this system that runs on the expensive deployment, and the one place that is
/// obviously right: extraction and assessment run across a corpus and are judged in aggregate,
/// while this runs once per application and is judged by a human being reading it. The cost
/// ratio between the two deployments is roughly twenty-five to one and the call ratio is
/// several thousand to one in the other direction.
///
/// <b>Two methods, and the pairing is the design rather than a convenience.</b> Writing a CV was
/// the largest thing this interface did and it is gone; what replaced it is a closed question over
/// documents somebody else wrote. Keeping both here says the division out loud - the model writes
/// what is genuinely per-posting and short, and it chooses among what is curated and long - and it
/// keeps the tie-break behind the same nullable registration, so a deployment with no provider
/// abstains on the choice exactly as it abstains on the prose. A separate interface would have
/// needed its own registration, and a service that is registered separately is one that can be
/// forgotten separately.
///
/// Registered <b>only</b> where a Kernel is, so consumers take <c>IApplicationWriter?</c>.
/// </remarks>
public interface IApplicationWriter
{
    /// <summary>Null when the model returned nothing usable. Never throws for a bad response.</summary>
    Task<ApplicationDraft?> WriteAsync(ApplicationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Which of these CVs to send. Null to abstain, which is always an available answer.
    /// </summary>
    /// <remarks>
    /// <b>The answer is a variant id off the ballot and nothing else.</b> An id outside the set,
    /// a malformed response, a timeout and an explicit refusal all come back as null, because the
    /// caller does the same thing with all four: it sends no CV. There is deliberately no free
    /// text - a rationale from a model is prose this system would then have to sanitise, and the
    /// sentence that started this whole feature was exactly that kind of prose. The audit line is
    /// written here, from the ballot and the answer, and says which id came back.
    ///
    /// <b>It never writes and it never invents.</b> The question is closed and checkable: the
    /// caller re-checks the returned id against the ballot it offered, on the rule
    /// <c>KernelDocumentExtractor</c> already follows for concept keys - a hallucinated id is
    /// indistinguishable from a real one once it is stored.
    /// </remarks>
    Task<long?> ChooseCurriculumVitaeAsync(CvChoiceRequest request, CancellationToken ct = default);
}
