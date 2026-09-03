using JobPlatform.Core.Profiles;

namespace JobPlatform.Core.Submissions;

/// <summary>Which of the four stages produced an answer, or refused to.</summary>
/// <remarks>
/// <b>The numbering ascends with what the stage costs, and the walk stops at the first stage that
/// decides.</b> That ordering is the whole of B2: a canonical key is a dictionary lookup, a stored
/// answer is one index seek, a cached resolution is another, and only past all three is anybody
/// paying a model to think about a question this system has already thought about.
///
/// <b>It is reported rather than inferred, because "did that cost a model call" is the acceptance
/// criterion.</b> "The second occurrence of a question resolves without a model call" cannot be
/// asserted against a value and a confidence - they look identical whichever stage produced them -
/// so the stage travels with the answer and <see cref="FormFieldResolution.ConsultedModel"/> is
/// derived from it rather than being a second field free to disagree.
///
/// <see cref="None"/> is not a failure. It is the honest answer where the walk ran out before
/// anything could decide - no provider configured, or nothing stored that this question could
/// possibly be about - and it is distinct from <see cref="Model"/> abstaining, because the two
/// want different fixes: one is a deployment that has no AI, the other is a question worth putting
/// to a person.
/// </remarks>
public enum FormFieldStage
{
    /// <summary>An exact canonical key, answered from <see cref="FormFieldCatalog"/>.</summary>
    CanonicalField = 1,

    /// <summary>The candidate's own answer to this same question, in their own words.</summary>
    DeclaredAnswer = 2,

    /// <summary>What this question resolved to before. A hit here never reaches a model.</summary>
    Cache = 3,

    /// <summary>Judgement, bought only where the three above missed.</summary>
    Model = 4,

    /// <summary>Nothing reached it: no provider, or nothing stored this could be about.</summary>
    None = 5,
}

/// <summary>
/// What this question resolved to last time, as the resolver needs it.
/// </summary>
/// <remarks>
/// <b>A Core record rather than the repository's <c>CachedResolution</c>, because the resolver
/// takes records and never a database</b> - the rule <c>MatchScorer</c> sets and
/// <c>KernelApplicationWriter</c> follows. The caller reads the cache row, hands the parts of it
/// that are a judgement across, and keeps the parts that are keys: the question hash and the
/// options hash decided which row to read and have nothing left to say once it has been read.
///
/// <b>The value is not carried, and could not be.</b> <c>FormAnswerResolutions</c> stores which
/// answer applies and not what to type, which is right: what to type depends on the option set the
/// form offers, and that is re-derived by <see cref="FormFieldPolicy.ForForm"/> on every read. A
/// cached row that named a rendering would go stale the first time a form re-labelled its
/// dropdown.
///
/// <b><see cref="Confirmed"/> is the one thing here that can outrank the confidence floor.</b> A
/// person looked at this resolution and agreed with it; a floor over a number the model reported
/// about itself has nothing to add to that.
/// </remarks>
/// <param name="Answer">The stored answer it chose, or null where it refused to choose one.</param>
/// <param name="ResolvedName">The canonical name it resolved to, where it resolved to one.</param>
/// <param name="Confidence">What it reported at the time, 0-1.</param>
/// <param name="Rationale">Why it decided this. Shown again rather than re-derived.</param>
/// <param name="ResolvedAtUtc">When. It goes into the rationale, so an audit line says how old the reuse is.</param>
/// <param name="Confirmed">Whether a person has agreed with it.</param>
public sealed record PriorResolution(
    FormAnswer? Answer,
    string? ResolvedName,
    double Confidence,
    string Rationale,
    DateTimeOffset ResolvedAtUtc,
    bool Confirmed)
{
    /// <summary>Whether the cached outcome was a refusal. An abstention is cached like any other.</summary>
    public bool Abstained => Answer is null;
}

/// <summary>
/// One question from one form, and everything that may be used to answer it.
/// </summary>
/// <remarks>
/// <b>Every candidate answer that could apply arrives here, and the whole answer store never
/// does.</b> The caller reads the answers already scoped to this candidate - the boundary
/// <c>CandidateProfileRepository</c> states as a type and <c>FormAnswerRepository</c> restates -
/// and this record carries them into a decision. What the implementation must not do with them is
/// put them all in a prompt: the reason B2 runs inside the server at all is that pulling the
/// answer store into a client's context is the whole-profile exposure this design exists to
/// prevent, and shipping it to a model instead would be the same disclosure with an extra hop.
///
/// <b>There is no profile id and no subject id on it.</b> Both would be a second copy of a fact
/// the caller has already established, and a second copy is free to disagree with the first; the
/// failure it invites is a resolver reading the owner off the request it is deciding what to
/// disclose. <see cref="PostingId"/> is here for scope and for correlation, never for authority.
///
/// <b><see cref="Profile"/> is reachable only through <see cref="FormFieldCatalog"/>, and only by
/// an exact key.</b> It is on the request because the first stage is the allowlist and the
/// allowlist reads the profile; it is never described to a model, never summarised, and never
/// searched. That is the same bargain <c>get_form_field</c> already makes - a named field, one at
/// a time - held one layer lower so that no path through this resolver can widen it.
/// </remarks>
public sealed record FormFieldRequest
{
    /// <summary>The question as the form asked it. The only untrusted text here.</summary>
    public required string QuestionText { get; init; }

    /// <summary>
    /// The choices the form offers, or null for a free-text box.
    /// </summary>
    /// <remarks>
    /// Null and empty mean the same thing, as they do on <c>OpenQuestionRow.Options</c>: a form
    /// that offered no choices and a scrape that did not record them are indistinguishable, and a
    /// caller forced to tell them apart is being asked something nobody knows.
    /// </remarks>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>
    /// The form field's own name, where the caller has one - <c>email</c>, <c>notice_period</c>.
    /// </summary>
    /// <remarks>
    /// A hint and never an instruction. It is matched against <see cref="FormFieldCatalog"/> and
    /// against the names the candidate filed their own answers under, both exactly; a name that
    /// matches neither changes nothing. That matters because this argument is frequently named by
    /// a model, and a model naming <c>email</c> for "your referee's email address" must not be
    /// able to turn a wrong guess into a disclosure - which is why an exact key match is all it
    /// can buy, and why a question that looks sensitive is not answered from the catalogue at all.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>
    /// The caller's own judgement that this question is one only a person may answer.
    /// </summary>
    /// <remarks>
    /// Additive, never authoritative. <see cref="SensitiveQuestions.Looks"/> runs whatever this
    /// says, so a caller that leaves it false does not thereby unlock right-to-work and salary
    /// questions - the failure mode of every design where a flag is the guard. Setting it true is
    /// how a caller adds a question this build's list does not know about.
    /// </remarks>
    public bool Sensitive { get; init; }

    /// <summary>
    /// The candidate's stored answers that could apply, already scoped to them.
    /// </summary>
    /// <remarks>
    /// Applicability is re-decided here through <see cref="AnswerPrecedence"/> rather than trusted:
    /// the caller's query filters on scope in SQL, and Core owns what beats what. Passing more than
    /// applies is safe; passing another candidate's is not, and nothing here can tell.
    /// </remarks>
    public IReadOnlyList<FormAnswer> Answers { get; init; } = [];

    /// <summary>What this question resolved to before, or null the first time it is seen.</summary>
    public PriorResolution? Cached { get; init; }

    /// <summary>The candidate's record, read only through the allowlist. Null where none was loaded.</summary>
    public CandidateProfile? Profile { get; init; }

    /// <summary>The employer asking, for <see cref="AnswerScope.Company"/> answers.</summary>
    public int? CompanyId { get; init; }

    /// <summary>The advert asking, for <see cref="AnswerScope.Posting"/> answers and for the ledger.</summary>
    public long? PostingId { get; init; }
}

/// <summary>
/// What to type into one field, or the reason a person has to.
/// </summary>
/// <remarks>
/// <b>Refusing is a result, not an error.</b> Every abstention here is an ordinary state of the
/// system - a question nobody has answered, an option set the stored answer does not fit, a
/// sensitive field with nothing stored - and each is returned as a value carrying a sentence
/// somebody can act on. The same rule the tool surface states as "a refusal is a structured
/// answer": a thrown exception invites a retry where a sentence invites a different action.
///
/// <b><see cref="NeedsUser"/> and <see cref="Value"/> are one fact expressed twice, so they are
/// built together and cannot be set apart.</b> There is no constructor and no <c>with</c> to reach
/// past <see cref="Answered"/> and <see cref="Ask"/>: a resolution carrying a value that a caller
/// was told not to type is a value that gets typed, and one carrying neither is a blank field with
/// nothing saying why. <see cref="Field"/> is null on every abstention for the same reason.
///
/// <b><see cref="Rationale"/> is written for a person reading an audit trail months later</b>, not
/// for the model and not for a log grep. It names what was matched, when it was said, and where a
/// refusal is being reported, what to do instead. It is the only field that survives into
/// <c>FormAnswerResolutions</c> as prose, and it is what makes a cached decision explicable rather
/// than merely repeatable.
/// </remarks>
public sealed record FormFieldResolution
{
    private FormFieldResolution(
        FormFieldStage stage,
        string? field,
        string? value,
        double confidence,
        string rationale,
        long? answerId,
        bool sensitive,
        string? model)
    {
        Stage = stage;
        Field = field;
        Value = value;
        Confidence = confidence;
        Rationale = rationale;
        AnswerId = answerId;
        Sensitive = sensitive;
        Model = model;
    }

    /// <summary>Which stage decided. See <see cref="FormFieldStage"/> on why it is reported.</summary>
    public FormFieldStage Stage { get; }

    /// <summary>
    /// The canonical name this resolved to, where it has one. Always null on an abstention.
    /// </summary>
    /// <remarks>
    /// Null alongside a value is ordinary and means only that the candidate never filed this answer
    /// under a name - most questions have no canonical key and never need one. Null alongside
    /// <see cref="NeedsUser"/> is the guarantee: nothing names a field it declined to fill.
    /// </remarks>
    public string? Field { get; }

    /// <summary>What to type. Null exactly when <see cref="NeedsUser"/> is true.</summary>
    public string? Value { get; }

    /// <summary>How sure, 0-1. One for an exact match, the model's own number where it decided.</summary>
    public double Confidence { get; }

    /// <summary>Why, for the audit trail. Never empty, on an answer or on a refusal.</summary>
    public string Rationale { get; }

    /// <summary>Whether a person has to answer this. True exactly when there is no value.</summary>
    public bool NeedsUser => Value is null;

    /// <summary>The stored answer used, so the caller can cache the decision against it.</summary>
    public long? AnswerId { get; }

    /// <summary>
    /// Whether the value came from an answer only a person may assert.
    /// </summary>
    /// <remarks>
    /// It drives redaction in the disclosure log and a confirmation on the dashboard, never
    /// permission to infer - the rule <see cref="FormAnswer.Sensitive"/> states. A sensitive value
    /// reaches this record only by having been stored against this same question; nothing here can
    /// produce one by matching, mapping or reasoning.
    /// </remarks>
    public bool Sensitive { get; }

    /// <summary>Which deployment answered, where one was reached. Null for the first three stages.</summary>
    public string? Model { get; }

    /// <summary>Whether this cost a model call. Derived, so it cannot disagree with the stage.</summary>
    public bool ConsultedModel => Stage == FormFieldStage.Model;

    /// <summary>An answer to type, with the value the form will actually receive.</summary>
    public static FormFieldResolution Answered(
        FormFieldStage stage,
        string value,
        string rationale,
        double confidence,
        string? field = null,
        long? answerId = null,
        bool sensitive = false,
        string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);

        return new FormFieldResolution(
            stage,
            string.IsNullOrWhiteSpace(field) ? null : field.Trim(),
            value,
            Math.Clamp(confidence, 0, 1),
            FormFieldPolicy.Bounded(rationale),
            answerId,
            sensitive,
            model);
    }

    /// <summary>
    /// A refusal, naming what to do instead.
    /// </summary>
    /// <remarks>
    /// The confidence is kept rather than zeroed. A model that reported 0.6 and was refused by the
    /// floor is a different row from one that reported nothing at all, and the difference is what
    /// a later reader needs to decide whether the floor is in the right place.
    /// </remarks>
    public static FormFieldResolution Ask(
        FormFieldStage stage, string rationale, double confidence = 0, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);

        return new FormFieldResolution(
            stage, null, null, Math.Clamp(confidence, 0, 1), FormFieldPolicy.Bounded(rationale), null, false, model);
    }
}

/// <summary>
/// The rules that decide what a stored answer may become, and what a number has to clear.
/// </summary>
/// <remarks>
/// Pure and free of every Azure type, like <c>SubmissionState.Fold</c> and <c>AnswerPrecedence</c>,
/// which is what makes these assertable exactly rather than approximately. Every one of them is a
/// rule the model is <i>not</i> trusted with.
/// </remarks>
public static class FormFieldPolicy
{
    /// <summary>
    /// Below this, a self-reported confidence is not an answer.
    /// </summary>
    /// <remarks>
    /// <b>The weakest of the four guards, and deliberately not the one doing the work.</b> A floor
    /// over a number a model reports about itself is a floor over an opinion, and models are
    /// cheerfully confident about near-misses - which is exactly the failure this whole stage is
    /// shaped against. What actually holds the line is structural: the model chooses an index and
    /// never a string, a sensitive answer never enters its prompt, and an option set is mapped by
    /// <see cref="ForForm"/> rather than by judgement. The floor catches the remaining case, which
    /// is real and commoner than it sounds - a prompt that makes refusing easy gets told "0.5, but
    /// these might be different questions" fairly often.
    ///
    /// 0.85 rather than a number fitted to data, because there is no data: nobody has labelled a
    /// corpus of form questions against this candidate's answers, and inventing a threshold from a
    /// dozen hand-written cases would be fitting the floor to the examples meant to test it. It is
    /// chosen from the cost asymmetry instead - a refusal costs one interruption, an error costs a
    /// false statement on an application - and it is a single constant so that moving it is one
    /// reviewable line rather than a search.
    /// </remarks>
    public const double ConfidenceFloor = 0.85;

    /// <summary>What an exact match reports. Nothing was judged, so there is nothing to be unsure of.</summary>
    public const double Certain = 1;

    /// <summary>Whether a confidence is good enough to act on, given who has seen it.</summary>
    /// <remarks>
    /// A person's agreement outranks the floor outright. They looked at the question and the answer
    /// together, which is strictly more than the number knows.
    /// </remarks>
    public static bool Meets(double confidence, bool confirmed = false)
        => confirmed || confidence >= ConfidenceFloor;

    /// <summary>
    /// What a stored answer becomes on this particular form, or null where it becomes nothing.
    /// </summary>
    /// <remarks>
    /// <b>Option mapping is typography or it does not happen, and that is the single most
    /// load-bearing rule in B2.</b> The design's own example is the whole argument: a stored
    /// "1 month" meeting <c>[Immediately, 2 weeks, 1 month, 3 months]</c> must map, and a stored
    /// "1 month" meeting <c>[Immediate, Less than a month, 1-3 months]</c> must not - because the
    /// second is a judgement about somebody's notice period and the difference between the two
    /// choices is a fortnight of a real person's life, typed into a real form under their name. So
    /// the fold here is <see cref="QuestionKey.Normalise"/>, the same casing-and-punctuation fold
    /// that decides two questions are one question, and anything past it is a refusal.
    ///
    /// <b>The option's own spelling is returned rather than the stored value.</b> A select takes
    /// the string it published; handing back "yes" where the form offered "Yes" is a field that
    /// silently fails to set, which reads to whoever looks at it afterwards as the answer being
    /// wrong rather than absent.
    ///
    /// <b>Two options that fold together are an ambiguity, not a choice.</b> A form offering both
    /// "1 month" and "1 Month." is broken, and picking the first is picking arbitrarily; the whole
    /// cost of refusing is one interruption on a form that is already wrong.
    ///
    /// <b>A sensitive answer is compared without the fold.</b> Verbatim or abstain is the rule
    /// <see cref="FormAnswer.Sensitive"/> states, so the only difference tolerated between what the
    /// candidate wrote and what the form offers is the case and the surrounding whitespace -
    /// nothing that could turn "No" into an option that merely resembles it.
    /// </remarks>
    public static string? ForForm(string? value, IReadOnlyList<string>? options, bool sensitive = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var answer = value.Trim();

        var offered = options?
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Select(option => option.Trim())
            .ToArray() ?? [];

        // A free-text box takes what the candidate wrote, which is the only case where this
        // function has nothing to decide.
        if (offered.Length == 0)
        {
            return answer;
        }

        var matches = sensitive
            ? offered.Where(option => string.Equals(option, answer, StringComparison.OrdinalIgnoreCase))
            : offered.Where(option => QuestionKey.Normalise(option) == QuestionKey.Normalise(answer));

        var chosen = matches.Distinct(StringComparer.Ordinal).Take(2).ToArray();

        return chosen.Length == 1 ? chosen[0] : null;
    }

    /// <summary>
    /// Trims audit prose to the column that will hold it.
    /// </summary>
    /// <remarks>
    /// The one place in this feature where truncating beats refusing, and for the reason
    /// <c>FormAnswerRepository</c> gives: the worst case of a shortened rationale is a shortened
    /// audit line, where the worst case of a shortened <i>answer</i> is a half-sentence sent to an
    /// employer. Nothing a candidate wrote passes through here.
    /// </remarks>
    public static string Bounded(string rationale)
    {
        var trimmed = rationale.Trim();

        return trimmed.Length <= SubmissionLimits.MaxNoteLength
            ? trimmed
            : trimmed[..SubmissionLimits.MaxNoteLength];
    }
}

/// <summary>
/// Questions that may be answered from what a person typed, and never from what a model concluded.
/// </summary>
/// <remarks>
/// <b>This is not a permission flag wearing a different hat.</b> <see cref="FormAnswer.Sensitive"/>
/// is set by whoever recorded the answer, and the design is explicit that nothing may depend on it
/// having been set correctly - so this reads the question instead, on both sides. A question this
/// recognises is answered only from an exact match; a stored answer whose <i>own</i> question this
/// recognises is never offered to a model at all, whatever flag it carries.
///
/// <b>That second half is what kills the failure the design names.</b> "Do you hold a full UK
/// driving licence?" is not a sensitive question and never will be, but the stored answer a model
/// would reach for is "Do you require sponsorship to work in the UK?" - and a sponsorship answer
/// mapped onto a right-to-work question is not merely a near-miss, it inverts: "sponsorship: yes"
/// means "right to work: no". Keeping the sponsorship answer out of the prompt means no wording of
/// the licence question can reach it, which is a stronger guarantee than any instruction to a model
/// and does not depend on the model reading it.
///
/// <b>A false positive costs one interruption and a false negative costs a false statement</b>, so
/// the list is written to over-match: <c>age</c> catches "What is your age?" and would catch a
/// question about an age limit, and that is the direction to be wrong in. It is matched as whole
/// words over the normalised question - <c>sex</c> must not fire on "Sussex", <c>age</c> must not
/// fire on "manage", <c>race</c> must not fire on "embrace" - which is the one piece of care a
/// substring test would get wrong on real form wordings.
///
/// <b>Notice period is deliberately absent</b>, though it is the sort of thing people expect to see
/// here. It is the design's own example of an answer that <i>must</i> map onto an option set, and
/// listing it would make that case unreachable. What is here is the set nothing may guess at:
/// immigration status, money, health, identity, and record.
/// </remarks>
public static class SensitiveQuestions
{
    /// <summary>
    /// The phrases, each a word or an ordered run of words in a normalised question.
    /// </summary>
    /// <remarks>
    /// Curated and short, like <c>DraftedAnswerCatalog.PerPosting</c> and for the same reason:
    /// every entry is a class of question this system will refuse to answer on somebody's behalf,
    /// so adding one should be a deliberate act with a diff rather than a regex nobody can
    /// enumerate. They are stored pre-normalised so that the fold applied to a question and the
    /// fold applied to the list cannot drift.
    /// </remarks>
    private static readonly string[][] Phrases =
    [
        // Immigration and the right to work. The pair that inverts, and the reason this exists.
        .. Split("right to work", "sponsorship", "sponsor", "visa", "work permit", "immigration",
            "legally authorised", "legally authorized", "eligible to work", "settled status"),

        // Money. A number typed into the wrong box is an offer nobody made.
        .. Split("salary", "compensation", "pay expectation", "expected pay", "current pay",
            "rate expectation", "day rate", "bonus"),

        // Identity and the protected characteristics every EEO section asks about.
        .. Split("date of birth", "birth date", "age", "gender", "sex", "sexual orientation",
            "ethnic", "ethnicity", "race", "racial", "religion", "religious", "disability",
            "disabled", "marital", "pregnant", "pregnancy", "national insurance", "social security"),

        // Record and clearance. Answered wrongly in either direction it is a false declaration.
        .. Split("criminal", "conviction", "convicted", "offence", "dbs", "security clearance",
            "background check", "veteran", "military service"),
    ];

    /// <summary>
    /// Whether this question is one only the candidate may answer.
    /// </summary>
    /// <remarks>
    /// Blank in, false out. A blank question is refused long before it reaches here, and a guard
    /// that answered "sensitive" for one would send every caller down a path it cannot act on.
    /// </remarks>
    public static bool Looks(string? questionText)
    {
        if (string.IsNullOrWhiteSpace(questionText))
        {
            return false;
        }

        var words = QuestionKey.Normalise(questionText).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return Phrases.Any(phrase => Contains(words, phrase));
    }

    /// <summary>
    /// Whether a stored answer is one a model may be shown.
    /// </summary>
    /// <remarks>
    /// The flag or the question, never the flag alone. An answer the candidate marked sensitive is
    /// sensitive because they said so; an answer to a question this recognises is sensitive whether
    /// or not anybody ticked a box, which is the half that does not depend on a boolean being right.
    /// </remarks>
    public static bool Guards(FormAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return answer.Sensitive || Looks(answer.QuestionText);
    }

    /// <summary>Whole-word containment: the phrase's words, in order, somewhere in the question.</summary>
    /// <remarks>
    /// <b>A run of whole words rather than a substring, and rather than a prefix.</b> "Sussex"
    /// contains "sex", "manage" contains "age" and "embrace" contains "race", so a substring test
    /// would refuse to answer where a candidate lives. A prefix test fixes those three and buys
    /// "agent" for "age" and "payment" for "pay", which is the same fault one letter later. So
    /// inflections are listed - <c>pregnant</c> beside <c>pregnancy</c>, <c>convicted</c> beside
    /// <c>conviction</c> - because a list somebody can read is worth more here than a rule nobody
    /// can enumerate, which is the argument <see cref="FormFieldCatalog"/> already makes.
    /// </remarks>
    private static bool Contains(string[] words, string[] phrase)
    {
        for (var start = 0; start + phrase.Length <= words.Length; start++)
        {
            var matched = true;

            for (var offset = 0; offset < phrase.Length && matched; offset++)
            {
                matched = string.Equals(words[start + offset], phrase[offset], StringComparison.Ordinal);
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string[]> Split(params string[] phrases)
        => phrases.Select(phrase =>
            QuestionKey.Normalise(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Answers one form field from what this system already knows, or says why a person must.
/// </summary>
/// <remarks>
/// <b>Four stages, and the model is the last resort rather than the mechanism.</b> An exact
/// canonical key reads the allowlist; the question's normalised text finds what the candidate has
/// already typed; the resolution cache says what this question resolved to before; and only past
/// all three does anything cost a model call. The stage is reported so the third of those can be
/// held to its acceptance criterion - the second occurrence of a question resolves without a model
/// call - by a test rather than by a claim.
///
/// <b>Abstention is the default and refusing is meant to be easy.</b> The characteristic failure of
/// a matcher like this is the confident near-miss, and a wrong answer on an application is worse
/// than an interruption: the candidate cannot take it back, and it is read as a statement they
/// made rather than as a bug in a tool they were using. So a refusal is an ordinary result carrying
/// a sentence somebody can act on, and every implementation is expected to reach for it first.
///
/// <b>Registered unconditionally, unlike every other AI service in this system.</b> The others are
/// registered inside <c>AddAiProvider</c>'s provider check and resolved as nullable, because a
/// deployment with no provider has nothing for them to do. This one has three stages that need no
/// provider at all, so a null resolver would take the candidate's own stored answers down with the
/// model - the same reason <c>MatchSweepFunction</c> is registered unconditionally and
/// <c>ICandidacyAssessor</c> is the nullable half.
///
/// <b>Never throws for anything a form can do to it.</b> An unanswerable question, an option set
/// nothing fits, a provider that timed out - all of them are resolutions with
/// <c>NeedsUser</c> set. The only exceptions are argument faults, which mean a caller skipped a
/// step rather than asked for something it may not have.
/// </remarks>
public interface IFormFieldResolver
{
    /// <summary>What to type into this field, or the reason a person has to.</summary>
    Task<FormFieldResolution> ResolveAsync(FormFieldRequest request, CancellationToken ct = default);
}
