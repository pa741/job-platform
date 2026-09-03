using System.Text.Json;
using System.Text.Json.Serialization;
using JobPlatform.Core.Searches;

namespace JobPlatform.Core.Applications;

/// <summary>
/// How much of an answer belongs to one posting, and therefore where it can come from.
/// </summary>
/// <remarks>
/// <b>The split is by posting-specificity rather than by stakes, and that is the whole design.</b>
/// "How sensitive is this?" is the obvious axis and it answers a different question: it tells you
/// how careful to be, where this pipeline needs to know <i>when an answer can be produced and
/// where it lives</i>. Those are storage decisions, there are exactly three of them, and this
/// enum is all three.
///
/// <see cref="StableFact"/> is the same on every application, so it is written once and reused -
/// either as the candidate's own declared answer or as one of the short canned values in
/// <see cref="DraftedAnswerCatalog"/>. <see cref="PostingSpecific"/> cannot be written once at
/// all: an answer to "why this company" that is reusable is, by definition, an answer that names
/// no company. <see cref="Novel"/> is what nothing anticipated, and answering it is not this
/// type's job - it becomes a question for a person.
///
/// <b>Nothing sensitive is any of these.</b> Right to work, salary expectation, notice period,
/// date of birth and every EEO question are answered only where somebody typed them, and are
/// never drafted, never inferred and never canned. A drafted paragraph is an assertion made to an
/// employer with the candidate's name on it, so the set of assertions this system will make on
/// somebody's behalf is deliberately small - the same argument that keeps <c>FormFieldCatalog</c>
/// down to eleven entries.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<FreeTextCategory>))]
public enum FreeTextCategory
{
    /// <summary>The same answer whatever the posting. Written once and reused, never regenerated.</summary>
    /// <remarks>
    /// Numbered from one rather than zero deliberately. A stored zero then reads as "nothing was
    /// said" instead of quietly claiming this, and <see cref="DraftedAnswerCatalog.Deserialise"/>
    /// drops a category it cannot name rather than admitting an answer whose provenance is a
    /// default value.
    /// </remarks>
    StableFact = 1,

    /// <summary>Prose about this employer and this advert, generated alongside the documents.</summary>
    PostingSpecific = 2,

    /// <summary>A question nothing anticipated. Not drafted - asked.</summary>
    /// <remarks>
    /// Here so the fill loop has somewhere to put an answer a person supplied to a question this
    /// catalogue never listed, which is also how the catalogue grows: the wordings that keep
    /// arriving as novel are the ones worth curating. Nothing in
    /// <see cref="DraftedAnswerCatalog.PerPosting"/> is ever this, because a question already on
    /// the list is by construction not a new one.
    /// </remarks>
    Novel = 3,
}

/// <summary>
/// One free-text answer, drafted before the form that will ask for it has been seen.
/// </summary>
/// <remarks>
/// <b>The question text is this catalogue's wording, not the form's.</b> An employer asking
/// "What draws you to us?" and this catalogue asking "Why do you want to work at this company?"
/// are the same question and share no words, so matching a live field to a drafted answer is
/// <c>resolve_form_field</c>'s job - normalised text, then the resolution cache, then the model.
/// A string comparison here would look like it worked and would miss almost every real form.
/// </remarks>
/// <param name="QuestionText">The question, in the wording this repository curates.</param>
/// <param name="Answer">What to type. Prose for a posting-specific one; a word or two for a stable fact.</param>
/// <param name="Category">Where the answer came from, and whether it could have been reused.</param>
public sealed record DraftedAnswer(string QuestionText, string Answer, FreeTextCategory Category);

/// <summary>
/// A question worth drafting an answer to before a form asks it.
/// </summary>
/// <remarks>
/// <b><see cref="Guidance"/> is instruction to the writer, never text a candidate sends.</b> It is
/// the half that prevents the failure this whole feature exists for, so it names what the answer
/// must be grounded in and what it must not claim. A prompt whose guidance cannot be met from the
/// advert in hand produces no answer at all - abstention is the default here for the reason it is
/// the default in <c>resolve_form_field</c>: a confident near-miss on an application is worse than
/// a blank box somebody fills in themselves.
/// </remarks>
/// <param name="QuestionText">The canonical wording. See <see cref="DraftedAnswer"/> on why it is not the form's.</param>
/// <param name="Guidance">What the writer is told about grounding this particular answer.</param>
/// <param name="MaxWords">
/// A bound on the draft, because ATS boxes have them. Honouring it is the writer's job at
/// generation time; nothing truncates a finished answer afterwards, since a paragraph cut off
/// mid-sentence reads to an employer as carelessness rather than as a limit.
/// </param>
public sealed record FreeTextPrompt(string QuestionText, string Guidance, int MaxWords);

/// <summary>
/// The free text worth drafting per posting, the short answers that stay canned values, and how
/// both round-trip through the <c>DraftedAnswersJson</c> column.
/// </summary>
/// <remarks>
/// <b>A visibly generic "why do you want to work here" is worse than leaving the box empty.</b>
/// A paragraph that would fit any employer is detectable in one sentence, and it does not merely
/// fail to help - it gets the candidate remembered as the person who sent the template. The
/// answer that has to be in the box is one naming this employer and this advert, and nobody
/// writes forty of those by hand.
///
/// <b>So: store facts, generate prose.</b> At generation time the advert body, the profile, the
/// match's gap list and the assessment's emphasise list are all already in the writer's prompt,
/// so drafting three more answers in that same call costs a few hundred output tokens against
/// work already paid for. The alternative - storing the prose as a reusable answer - cannot work
/// even in principle: a paragraph that names a company is not reusable across companies, and one
/// that does not name a company is the template we are trying not to send.
///
/// <b>There is no daily generation pass today, and this does not add one.</b> Documents are
/// written when a person presses generate; the nightly pass scores and assesses, and nothing on
/// the agent surface can trigger a write. So drafted answers exist <i>only</i> for postings
/// somebody already generated documents for, which today is almost none of them. That is worth
/// stating plainly rather than leaving to read as an oversight: a scheduled writer would run on
/// the expensive deployment - the one cost this architecture has deliberately kept off a schedule
/// - and <c>model.md</c> names generation as happening on demand for a chosen posting, so adding
/// the pass is an amendment to a binding document rather than a new timer. Until that happens a
/// caller must read an empty list as "not generated yet", never as "this posting has nothing
/// worth saying".
///
/// <b>What is absent is considered, as it is in <c>FormFieldCatalog</c>.</b> Anything readable
/// from the profile belongs there and not here, or one question grows two answers free to
/// disagree; anything only a person may assert belongs in their declared answers and is never
/// drafted. What is left is prose about an employer, and one question about a job board.
/// </remarks>
public static class DraftedAnswerCatalog
{
    /// <summary>
    /// The questions drafted once per posting, with what the writer is told about each.
    /// </summary>
    /// <remarks>
    /// Curated rather than discovered, and short on purpose. These are the boxes that recur across
    /// Greenhouse, Lever, Workable and Ashby forms; a longer list would spend output tokens on
    /// every posting for questions most of them never ask, and each entry is also prose somebody
    /// may end up sending, so adding one should be a deliberate act with a diff. Every one is
    /// <see cref="FreeTextCategory.PostingSpecific"/> by construction - a question answerable
    /// without reading the advert has no business being generated per posting.
    /// </remarks>
    public static IReadOnlyList<FreeTextPrompt> PerPosting { get; } =
    [
        new("Why do you want to work at this company?",
            "Name the employer, and one specific thing the advert or the employer's own description "
            + "of itself actually says. No superlatives, and nothing the advert did not say - an "
            + "invented fact about a company is read by somebody who works there.",
            150),

        new("Why are you interested in this role?",
            "Tie two or three of the advert's stated requirements to work the candidate has actually "
            + "done. The match's gap list is the set of claims that must not be made.",
            150),

        // Kept despite being the narrowest of the five: where a company does name its products,
        // this is the box that separates an application from a mailshot - and where it names none,
        // the guidance is to draft nothing rather than to guess at a product line.
        new("Which of our products or areas of work interests you most, and why?",
            "Only where the advert or the employer names one. Where none is named, draft nothing "
            + "rather than inventing a product line.",
            120),

        new("What makes you a good fit for this role?",
            "The assessment's emphasise list, told through the candidate's own history, with dates "
            + "and outcomes rather than adjectives.",
            150),

        new("Is there anything else you would like us to know?",
            "Usually nothing. Draft one only where the match has something specific worth "
            + "pre-empting - a location, a career break the CV already shows - and never a "
            + "restatement of the CV.",
            100),
    ];

    /// <summary>
    /// The answers short and stable enough to be a stored value rather than a paragraph.
    /// </summary>
    /// <remarks>
    /// <b>One entry, and the shortness is the finding rather than an omission.</b> Working through
    /// the short boxes a real ATS form asks, almost every one is either posting-specific - which
    /// belongs in <see cref="PerPosting"/> - or something only a person may assert: sponsorship,
    /// salary, notice period, every EEO question. Those belong in their declared answers and must
    /// never be canned here, because a canned answer is one nobody was asked for. What survives
    /// both tests is where they heard about the job.
    ///
    /// <b>It names the board the posting actually came from</b>, because a form answer is an
    /// assertion: writing "LinkedIn" on an advert found on Indeed is a small lie in a document
    /// somebody signs. A board this build cannot spell therefore yields <i>no</i> canned answer
    /// rather than a plausible one, and the spelling is taken from <see cref="ScraperSites"/>
    /// rather than from a second list here, so the two cannot drift.
    /// </remarks>
    /// <param name="sourceBoard">
    /// The posting's board, in the scraper's wire spelling. Null where the caller does not know,
    /// which falls back to the board this pipeline measurably runs on - all 4,470 postings of one
    /// recent week came from LinkedIn - rather than dropping the answer for want of an argument.
    /// </param>
    public static IReadOnlyList<DraftedAnswer> StableAnswers(string? sourceBoard = null)
    {
        var source = ReferralSource(sourceBoard);

        return source is null
            ? []
            : [new DraftedAnswer(ReferralQuestion, source, FreeTextCategory.StableFact)];
    }

    /// <summary>Renders answers for the <c>DraftedAnswersJson</c> column.</summary>
    /// <remarks>
    /// Follows <c>EmphasisedJson</c> exactly - camelCase, a plain array, no envelope - so the two
    /// JSON columns on the same table read the same way. The category is written by <i>name</i>,
    /// which the numbering deliberately is not: a member inserted later must not silently
    /// reinterpret every answer already stored.
    ///
    /// Empty in, <c>"[]"</c> out. A caller may store that or leave the column null; both read back
    /// as no answers, so there is no third state to get wrong.
    /// </remarks>
    /// <summary>
    /// Whether a drafted answer is safe to keep, which here means: does it talk about itself.
    /// </summary>
    /// <remarks>
    /// <b>Written against something that actually happened, on the first real generation run.</b>
    /// Asked whether there was anything else the employer should know, the model answered with the
    /// candidate's citizenship - correctly, from their own summary - and then added "I am an AI and
    /// they should have seen this." That sentence was stored, served through the pack, and would
    /// have been typed into Cloudflare's form under somebody's name.
    ///
    /// <b>The asymmetry is the whole argument.</b> Every other guard in this system protects
    /// against a wrong answer; this protects against an answer that is not the candidate's voice at
    /// all. A recruiter reading it does not conclude that a tool misbehaved, they conclude the
    /// applicant sent it - and unlike a bad match or a clumsy sentence, there is no reading of it
    /// that is merely weak. The gap list bounds what may be <i>claimed</i> and cannot see this,
    /// because it is not a claim about the candidate's experience.
    ///
    /// <b>Narrow on purpose, and it does not pretend to be a content filter.</b> It catches
    /// first-person self-reference to being a model, an AI, an assistant or a language model, which
    /// is the specific failure mode observed and a well-known one. It cannot catch prose that is
    /// merely bad, and nothing here should suggest it can: the honest bound on this feature is that
    /// a drafted answer is a draft, and <c>park_application</c> plus the question queue exist so
    /// that an unattended run can decline to answer rather than improvise.
    ///
    /// Dropping rather than editing, because a sentence removed from a paragraph leaves prose that
    /// reads as though something is missing - and an omitted answer is a box a person fills in,
    /// which is the state every application was in before any of this existed.
    /// </remarks>
    public static bool IsCandidateVoice(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var folded = answer.ToLowerInvariant();

        foreach (var subject in SelfReference)
        {
            if (folded.Contains(subject, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// First-person self-reference, in the spellings a model actually reaches for.
    /// </summary>
    /// <remarks>
    /// Matched as substrings against the folded answer rather than as words, so "I'm an AI" and
    /// "I am an AI assistant" both go. The apostrophe is spelled both ways because a model emits
    /// the typographic one as often as the ASCII one, and only one of those is on a keyboard.
    ///
    /// It deliberately does not match "AI" alone: this candidate's own summary says they build AI
    /// systems, half the adverts in the corpus are for AI roles, and a rule that struck those would
    /// delete the best answers on the page to prevent a sentence nobody has written yet.
    /// </remarks>
    private static readonly string[] SelfReference =
    [
        "i am an ai", "i'm an ai", "i’m an ai",
        "i am an artificial", "i'm an artificial", "i’m an artificial",
        "i am a language model", "i'm a language model", "i’m a language model",
        "i am an ai language", "as an ai", "as a language model",
        "i am a large language", "i'm a large language", "i’m a large language",
        "i am an assistant", "i am a chatbot", "i am a bot",
    ];

    public static string Serialise(IEnumerable<DraftedAnswer>? answers)
        => JsonSerializer.Serialize(Clean(answers), Json);

    /// <summary>
    /// Reads the column back. Empty for anything unreadable, never an exception.
    /// </summary>
    /// <remarks>
    /// <b>Stored JSON is not input, it is history</b>, and history that no longer parses is not a
    /// reason to fail the request that read it. The submission pack is assembled from several
    /// sources, and a candidate looking at their own documents should not get a 500 because one
    /// column was written by a build that has since changed shape - the rule
    /// <c>ApplicationDocumentRepository</c> already follows for <c>EmphasisedJson</c>.
    ///
    /// Well-formed JSON carrying a malformed <i>entry</i> is a different case: that entry is
    /// dropped and the rest survive. A blank answer typed into a form reads to an employer as an
    /// answer, and a category outside this enum is an answer whose provenance nothing established
    /// - neither is worth keeping, and neither is a reason to discard the answers around it.
    /// </remarks>
    public static IReadOnlyList<DraftedAnswer> Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return Clean(JsonSerializer.Deserialize<List<DraftedAnswer>>(json, Json));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private const string ReferralQuestion = "How did you hear about us?";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Keeps the entries that could actually be typed into a form.</summary>
    /// <remarks>
    /// Applied on the way in as well as on the way out, so an answer that would be dropped on read
    /// is never written. Trimming and not truncating: surrounding whitespace is noise, where a
    /// bounded answer is the writer's job at generation time.
    /// </remarks>
    private static List<DraftedAnswer> Clean(IEnumerable<DraftedAnswer>? answers)
        => answers is null
            ? []
            : [.. answers
                .Where(answer => answer is not null
                    && !string.IsNullOrWhiteSpace(answer.QuestionText)
                    && !string.IsNullOrWhiteSpace(answer.Answer)
                    && Enum.IsDefined(answer.Category))
                .Select(answer => answer with
                {
                    QuestionText = answer.QuestionText.Trim(),
                    Answer = answer.Answer.Trim(),
                })];

    /// <summary>What to write in the referral box, or null where nothing honest can be written.</summary>
    private static string? ReferralSource(string? sourceBoard)
        => string.IsNullOrWhiteSpace(sourceBoard)
            ? DisplayName(ScraperSite.LinkedIn)
            : ScraperSites.TryParse(sourceBoard, out var site) ? DisplayName(site) : null;

    /// <summary>
    /// The board as a person would write it, rather than as jobspy spells it.
    /// </summary>
    /// <remarks>
    /// <c>ScraperSites.ToWireName</c> is a contract with the scraper and throws on an unknown
    /// member, because a search that cannot be published is a bug. This is the opposite direction
    /// with the opposite obligation - it goes into a form somebody sends - so an unmapped member
    /// yields no canned answer rather than a lowercase slug in a covering note.
    /// </remarks>
    private static string? DisplayName(ScraperSite site) => site switch
    {
        ScraperSite.Indeed => "Indeed",
        ScraperSite.LinkedIn => "LinkedIn",
        ScraperSite.Freehire => "Freehire",
        _ => null,
    };
}
