using System.Security.Cryptography;
using System.Text;

namespace JobPlatform.Core.Submissions;

/// <summary>How widely an answer applies.</summary>
/// <remarks>
/// <b>Because most form questions do not have one answer.</b> "Do you require sponsorship to
/// work in the UK" does - it is a fact about the person and it is the same fact whoever asks.
/// "Why do you want to work here" is per-company at best and usually per-posting, and an answer
/// store with no scope would hand a recruiter at one employer the paragraph written about
/// another. That is not a small embarrassment; it is the single most legible way for an
/// application to announce that nobody read it.
///
/// The numbering ascends with specificity, and <see cref="AnswerPrecedence"/> reads that as a
/// question rather than as a comparison, for the reason <see cref="SubmissionEventTypes.IsTerminal"/>
/// does: a member inserted later must fail to resolve rather than quietly reorder the answers a
/// candidate has already stored.
///
/// <b>The narrow scopes are not a filing convenience, they are the safety property.</b> A
/// posting-scoped answer is only ever offered back for that posting, so the cost of storing
/// something specific is bounded to the place it was written for. Widening an answer is
/// therefore a deliberate act - the candidate records it again at <see cref="Global"/> - and
/// never something the resolver decides on their behalf.
/// </remarks>
public enum AnswerScope
{
    /// <summary>True wherever it is asked. Sponsorship, notice period, right to work.</summary>
    Global = 1,

    /// <summary>
    /// True of one employer. Keyed on the company id rather than the employer name.
    /// </summary>
    /// <remarks>
    /// <c>CompanyEntity</c> already folds spelling - lower-cased, punctuation collapsed, legal
    /// form stripped - so keying on the id inherits that folding for free and cannot drift from
    /// it. Keying on the string written on the advert would file the same answer twice under
    /// "Contoso" and "Contoso Ltd", which is the failure the company table was built to remove.
    /// </remarks>
    Company = 2,

    /// <summary>True of one posting. Where the "why this role" paragraph lives.</summary>
    Posting = 3,
}

/// <summary>Who asserted an answer.</summary>
/// <remarks>
/// <b>What a person asserted and what an agent inferred are different claims, and a store that
/// cannot tell them apart cannot be audited after one of them turns out to be wrong.</b> The
/// same reasoning as <see cref="SubmissionEventSource"/>, applied to a worse case: an event is a
/// record of something that already happened, where an answer is text that will be typed into an
/// employer's form and sent under the candidate's name.
///
/// <b>Derived from the token type, never from a tool argument.</b> A tool that took
/// <c>source: 'candidate' | 'client'</c> would let a model stamp its own inference as the
/// candidate's own words by filling in a parameter - and a model filling in a parameter
/// helpfully is exactly what the "no tool takes a profile id" rule already exists to prevent.
/// The write path reads <c>IsApplicationToken</c> and stamps <see cref="Client"/> for anything
/// arriving over MCP; <see cref="Candidate"/> is reachable only from the dashboard, where a
/// person typed it.
/// </remarks>
public enum FormAnswerSource
{
    /// <summary>The candidate typed it, in the dashboard.</summary>
    Candidate = 1,

    /// <summary>An MCP client asserted it. Recorded, and never mistakable for the sentence above.</summary>
    Client = 2,
}

/// <summary>Bounds shared by the schema, the API contract and the tools.</summary>
/// <remarks>
/// The <see cref="SubmissionLimits"/> precedent, applied to a second table: one place, so the
/// column width and the validation cannot disagree. It is a separate class rather than more
/// constants on <c>SubmissionLimits</c> because these bound a different table with a different
/// migration - the rule being followed is "one place per set of columns", not "one class".
///
/// <b>Nothing here truncates.</b> <c>DisclosureRecord</c> can cut an over-long detail down
/// because the worst case is a shortened audit line. The worst case here is a truncated sentence
/// typed into somebody's application and sent to an employer, which reads as a fact rather than
/// as a bug. <see cref="FormAnswer.Create"/> refuses instead, and the tool layer checks these
/// same constants first so the caller gets a structured refusal rather than an exception.
/// </remarks>
public static class FormAnswerLimits
{
    /// <summary>The stable key an answer may be filed under - <c>notice_period</c>.</summary>
    public const int MaxNameLength = 100;

    /// <summary>
    /// The question as the form asked it.
    /// </summary>
    /// <remarks>
    /// The same bound <see cref="SubmissionLimits.MaxNoteLength"/> carries and for the same
    /// reason: a paragraph, never a document. Forms do ask essay-length prompts, and this holds
    /// them; what it will not hold is a page of terms and conditions pasted in by a scraper that
    /// picked the wrong element.
    /// </remarks>
    public const int MaxQuestionTextLength = 1000;

    /// <summary>
    /// What the candidate wrote.
    /// </summary>
    /// <remarks>
    /// 4,000 characters is the widest <c>nvarchar</c> SQL Server will store in row before
    /// <c>nvarchar(max)</c>, and the bound is chosen there deliberately: an answer longer than
    /// this is a document, and documents are the CV path's business rather than this table's.
    /// </remarks>
    public const int MaxValueLength = 4000;

    /// <summary>
    /// Exactly the width of <see cref="QuestionKey.Hash"/>'s output, so <c>char(64)</c> and the
    /// validation are one decision.
    /// </summary>
    public const int QuestionHashLength = 64;
}

/// <summary>
/// One thing the candidate has said, stored so it can be said again without asking them.
/// </summary>
/// <remarks>
/// <b>This is the declared namespace, and it never mixes with the derived one.</b>
/// <see cref="FormFieldCatalog"/> reads the profile and answers a fixed allowlist of questions
/// the candidate has already answered structurally; this holds only what a person typed as an
/// answer to a question. That split is what makes the sensitive case safe without depending on a flag being
/// set correctly: an EEO question, a salary expectation, a date of birth - none of them are
/// reachable from the profile at all, so a sensitive value can exist here only because somebody
/// wrote it, and nowhere else because there is nowhere else. Marking catalogue fields
/// <c>sensitive: true</c> instead would have converted "cannot be answered" into "answered
/// unless a boolean was set right", which is a weaker guarantee wearing the same word.
///
/// <b>Superseded, never updated.</b> An answer store that overwrites cannot say what was
/// submitted last year, and "what did I tell them" is the question somebody asks after an
/// interview goes strangely. It is the same argument the event log rests on, and it is why
/// <see cref="SupersededAtUtc"/> exists rather than a <c>Current</c> flag - a timestamp says
/// when the person changed their mind, where a flag says only that they did.
///
/// <b>No profile id here, though the table carries one.</b> Every one of these is materialised
/// by a read already scoped to a candidate - the rule <c>CandidateProfileRepository</c> states
/// as a type, taking a subject id and never a profile id. A field on the record would be a
/// second copy of a fact the query has already established, and a second copy is free to
/// disagree with the first: the failure it invites is a caller reading the owner off the row it
/// is deciding whether to disclose.
///
/// <b><see cref="QuestionHash"/> and <see cref="NormalisedQuestion"/> are stored, not
/// recomputed on read.</b> The hash is what the unique index was built on and what the
/// resolution cache is keyed by; recomputing it on the way out would mean a later change to
/// <see cref="QuestionKey.Normalise"/> silently re-keys every answer already stored and the
/// candidate's answers stop being found, with nothing failing. So <see cref="Create"/> derives
/// them together for a new answer, and rehydrating a stored row uses the object initialiser and
/// keeps what is on disk.
/// </remarks>
public sealed record FormAnswer
{
    /// <summary>The row. Zero until it is written, because identity is the database's to assign.</summary>
    public long Id { get; init; }

    /// <summary>
    /// A stable key this answer is filed under, where it has one - <c>notice_period</c>.
    /// </summary>
    /// <remarks>
    /// The escape from phrasing. <see cref="QuestionHash"/> folds typography and nothing more, so
    /// two employers asking the same thing in genuinely different words produce two hashes; a
    /// name written once lets both resolve. It is deliberately free text rather than a
    /// <see cref="FormFieldCatalog"/> entry: the catalogue is the derived namespace and its
    /// contents are what this system will read <i>from the profile</i>, so restricting the
    /// declared namespace to catalogue names would put the two back in one bucket and leave
    /// exactly the questions that need this - the ones the catalogue refuses to hold - unable to
    /// carry a key at all. A name matching a catalogue entry does not make this a catalogue
    /// answer; resolution asks the catalogue first, and the catalogue reads the profile.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>The question as it was asked, verbatim. What a person reads when reviewing this.</summary>
    public required string QuestionText { get; init; }

    /// <summary>The lookup key: <see cref="QuestionKey.Hash"/> over the normalised question.</summary>
    public required string QuestionHash { get; init; }

    /// <summary>
    /// The normalised form the hash was taken over.
    /// </summary>
    /// <remarks>
    /// Stored beside the hash so that a collision or a miss can be explained by reading a row
    /// rather than by rerunning the normaliser against a guess at what the input was. A hash
    /// column with no readable preimage is a debugging session nobody can finish.
    /// </remarks>
    public required string NormalisedQuestion { get; init; }

    /// <summary>What the candidate wrote. Stored as typed, including "prefer not to say".</summary>
    public required string Value { get; init; }

    /// <summary>How widely it applies.</summary>
    public required AnswerScope Scope { get; init; }

    /// <summary>The employer, for <see cref="AnswerScope.Company"/>. Null at every other scope.</summary>
    public int? CompanyId { get; init; }

    /// <summary>The posting, for <see cref="AnswerScope.Posting"/>. Null at every other scope.</summary>
    public long? PostingId { get; init; }

    /// <summary>
    /// Whether this answer is one a person should see leave the system.
    /// </summary>
    /// <remarks>
    /// <b>It drives redaction and confirmation, never permission to infer.</b> The flag is not
    /// what keeps sensitive data safe - the declared/derived split above is - so a row with this
    /// wrong is a row that logs badly, not a row that leaks. What it does buy: the disclosure log
    /// names the question and not the value, and the dashboard asks before a sensitive answer is
    /// handed to an agent. Resolution returns a sensitive answer verbatim or abstains; there is
    /// no option-set transform and no near-match on one, because a near-miss on a
    /// right-to-work question is a false statement on an application.
    /// </remarks>
    public bool Sensitive { get; init; }

    /// <summary>Who asserted it. Derived from the token type, never from a tool argument.</summary>
    public required FormAnswerSource Source { get; init; }

    /// <summary>When it was given.</summary>
    public required DateTimeOffset AnsweredAtUtc { get; init; }

    /// <summary>When it stopped being what the candidate would say. Null while it stands.</summary>
    public DateTimeOffset? SupersededAtUtc { get; init; }

    /// <summary>Whether this is still what the candidate would say.</summary>
    public bool IsLive => SupersededAtUtc is null;

    /// <summary>
    /// The constructor for a new answer, so the hash cannot disagree with the question it is
    /// taken over.
    /// </summary>
    /// <remarks>
    /// The rule <c>AiCallRecord.Create</c> and <c>DisclosureRecord.Create</c> already follow: a
    /// guard written at the call sites survives exactly until somebody adds another call site,
    /// and there will be several here - the dashboard, <c>record_form_answer</c>, and whatever
    /// answers an <c>OpenQuestion</c>.
    ///
    /// <b>The scope and its id are validated together, and that pairing is the point.</b> A
    /// <see cref="AnswerScope.Company"/> answer with no company applies to every employer, which
    /// is the "why do you want to work here" failure with the safety removed; a
    /// <see cref="AnswerScope.Global"/> answer carrying a posting id looks scoped in the database
    /// and is not. <see cref="AnswerPrecedence.Applies"/> would refuse the first of those at read
    /// time, so this is the second line rather than the only one - but a row that can never be
    /// read is worse than a refused write, because nothing reports it.
    ///
    /// It throws rather than returning a refusal because reaching it with an over-long value
    /// means the tool skipped the bounds in <see cref="FormAnswerLimits"/> that it is supposed to
    /// check and refuse on. A structured refusal is for a caller asking for something it may not
    /// have; this is a caller that did not look.
    /// </remarks>
    public static FormAnswer Create(
        string questionText,
        string value,
        AnswerScope scope,
        FormAnswerSource source,
        DateTimeOffset answeredAtUtc,
        string? name = null,
        int? companyId = null,
        long? postingId = null,
        bool sensitive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionText);

        // Whitespace is not an answer, and storing it as one would tell every later resolution
        // that this question is settled. "Prefer not to say" is a value; nothing is not.
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var question = questionText.Trim();
        var answer = value.Trim();

        if (question.Length > FormAnswerLimits.MaxQuestionTextLength)
        {
            throw new ArgumentException(
                $"Question text exceeds {FormAnswerLimits.MaxQuestionTextLength} characters.", nameof(questionText));
        }

        if (answer.Length > FormAnswerLimits.MaxValueLength)
        {
            throw new ArgumentException(
                $"Answer exceeds {FormAnswerLimits.MaxValueLength} characters.", nameof(value));
        }

        var key = name?.Trim();

        if (key is { Length: 0 })
        {
            key = null;
        }

        if (key is not null && key.Length > FormAnswerLimits.MaxNameLength)
        {
            throw new ArgumentException(
                $"Name exceeds {FormAnswerLimits.MaxNameLength} characters.", nameof(name));
        }

        var expectedCompany = scope == AnswerScope.Company;
        var expectedPosting = scope == AnswerScope.Posting;

        if (expectedCompany != companyId.HasValue)
        {
            throw new ArgumentException(
                $"Scope {scope} {(expectedCompany ? "requires" : "does not take")} a company id.", nameof(companyId));
        }

        if (expectedPosting != postingId.HasValue)
        {
            throw new ArgumentException(
                $"Scope {scope} {(expectedPosting ? "requires" : "does not take")} a posting id.", nameof(postingId));
        }

        return new FormAnswer
        {
            Name = key,
            QuestionText = question,
            QuestionHash = QuestionKey.Hash(question),
            NormalisedQuestion = QuestionKey.Normalise(question),
            Value = answer,
            Scope = scope,
            CompanyId = companyId,
            PostingId = postingId,
            Sensitive = sensitive,
            Source = source,
            AnsweredAtUtc = answeredAtUtc,
        };
    }
}

/// <summary>
/// The key two spellings of the same question have to agree on.
/// </summary>
/// <remarks>
/// <b>Every fold here is typography, and that boundary is the whole design.</b> Casing,
/// punctuation, an apostrophe's shape, a non-breaking space pasted out of a web form, a trailing
/// question mark - none of them change what is being asked, so folding them costs nothing.
/// Anything past that is interpretation, and interpretation is where a false merge comes from:
/// two questions folded together means one question's answer typed into the other's form, which
/// is a false statement made on somebody's behalf. A missed merge costs one interruption. The
/// asymmetry is the same one that makes the resolver abstain by default, and it decides every
/// judgement call below.
///
/// <b>Punctuation becomes a space rather than vanishing</b>, following
/// <c>JobFingerprint.Normalize</c>, because the common case is a separator: "full-time" and
/// "full time" are one question and deleting the hyphen would make them "fulltime" and
/// "full time", which is two. The apostrophe is the exception in the other direction - it sits
/// inside a word rather than between two, so "candidate's" folds to "candidates" and the curly
/// apostrophe a form pasted out of Word carries folds to the same thing as the straight one a
/// browser produced.
///
/// <b>Only a leading article is dropped, not every article.</b> Design section 5 describes this
/// step as "strip punctuation and articles"; stripping them everywhere is the more aggressive
/// reading and it is not typography - the function has no way to tell an ornamental "a" from a
/// load-bearing one, and each interior word removed is another pair of questions that can
/// collide. A question does not change because it starts with "The", so that one is free; the
/// rest are not, and the direction to err in is settled above.
/// </remarks>
public static class QuestionKey
{
    /// <summary>An apostrophe in each of the shapes a form actually produces.</summary>
    /// <remarks>
    /// Word's autocorrect emits U+2019 and a browser emits U+0027 for the same keystroke, so a
    /// question pasted from a document and the same question typed by hand are different bytes.
    /// U+02BC turns up in transliterated names. All three are dropped rather than spaced, for the
    /// reason in the class remarks.
    /// </remarks>
    private static readonly char[] Apostrophes = [(char)0x0027, (char)0x2019, (char)0x02bc];

    /// <summary>Dropped only at the front, with the space that follows.</summary>
    private static readonly string[] LeadingArticles = ["the ", "an ", "a "];

    /// <summary>
    /// What <see cref="OptionsHash"/> joins options on. U+001F, which
    /// <see cref="Normalise"/> cannot emit - see the remarks there.
    /// </summary>
    private const char UnitSeparator = (char)0x001f;

    /// <summary>
    /// The canonical form of a question: what the hash is taken over, and what is stored
    /// beside it so a miss can be read rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Unicode is composed to Form C first, because "café" written with a precomposed é and
    /// "café" written with a combining acute render identically and would otherwise be two
    /// questions - and worse, the combining mark is not a letter, so the decomposed spelling
    /// would fold to "cafe" with a space in the middle of the word.
    ///
    /// The trailing question mark falls out of punctuation folding rather than being a step of
    /// its own; it is pinned by a test anyway, because it is the rule most likely to be reasoned
    /// about in isolation and "it is already handled" is not something a reader can see.
    ///
    /// Empty in, empty out, deterministically. A normaliser that threw on a blank question would
    /// put a guard in front of every call site to prevent something no call site can act on;
    /// the guard that matters is in <see cref="FormAnswer.Create"/>, which refuses to store one.
    /// </remarks>
    public static string Normalise(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return string.Empty;
        }

        var composed = question.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var builder = new StringBuilder(composed.Length);

        // Starts true so a leading mark emits nothing rather than a space that then has to be
        // trimmed back off.
        var lastWasSpace = true;

        foreach (var ch in composed)
        {
            if (Array.IndexOf(Apostrophes, ch) >= 0)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        var folded = builder.ToString().TrimEnd();

        foreach (var article in LeadingArticles)
        {
            if (folded.StartsWith(article, StringComparison.Ordinal))
            {
                return folded[article.Length..];
            }
        }

        return folded;
    }

    /// <summary>
    /// The stable key for a question: lowercase hex SHA-256 over its normalised form, exactly
    /// <see cref="FormAnswerLimits.QuestionHashLength"/> characters.
    /// </summary>
    /// <remarks>
    /// A hash rather than the normalised text as the key, for the reason every hash column in
    /// this repository is one: it is a fixed-width value a unique index and a cache key can both
    /// be built on, where the text is unbounded and would put a paragraph in an index. The text
    /// is kept alongside it rather than thrown away - see
    /// <see cref="FormAnswer.NormalisedQuestion"/>.
    /// </remarks>
    public static string Hash(string? question)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(question))));

    /// <summary>
    /// The key for a set of options, insensitive to the order they were listed in.
    /// </summary>
    /// <remarks>
    /// <b>Order-insensitive because the order is the form's, not the question's.</b> The same
    /// dropdown re-rendered with its choices shuffled is the same question, and a cache keyed on
    /// the listed order would miss on it and buy a model call to reach the answer it already
    /// had - which is the one thing the resolution cache exists to prevent.
    ///
    /// <b>Joined on a character normalisation cannot emit.</b> A space would let
    /// <c>["b", "a c"]</c> and <c>["a", "c b"]</c> produce the same key, since both sort to the
    /// same three words; the unit separator cannot appear in a normalised option, so the
    /// boundaries between options survive into the hash.
    ///
    /// Null for nothing to key on, and that includes an option set that normalises away to
    /// nothing. A free-text question and a select with no choices are the same question as far as
    /// this can tell, and both want the null that <c>FormAnswerResolutions.OptionsHash</c> holds
    /// for "no options" rather than a hash of emptiness that reads like a real one.
    /// </remarks>
    public static string? OptionsHash(IEnumerable<string>? options)
    {
        if (options is null)
        {
            return null;
        }

        var canonical = options
            .Select(Normalise)
            .Where(option => option.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (canonical.Length == 0)
        {
            return null;
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(UnitSeparator, canonical))));
    }
}

/// <summary>
/// Which of several stored answers to a question is the one to use.
/// </summary>
/// <remarks>
/// Pure and free of every Azure type, like <c>SubmissionState.Fold</c> and <c>MatchScorer</c>,
/// which is what makes the ordering assertable exactly rather than approximately. The caller has
/// already matched on the hash or the name; this decides between the matches.
/// </remarks>
public static class AnswerPrecedence
{
    /// <summary>
    /// Whether an answer is a candidate at all for a question asked in this context.
    /// </summary>
    /// <remarks>
    /// <b>Applicability is part of precedence, and leaving it to the caller is how the wrong
    /// company's answer gets used.</b> A repository that has fetched every answer with this hash
    /// holds one written for a different employer, and ranking without filtering first would let
    /// it beat the global answer purely for being more specific - the exact failure
    /// <see cref="AnswerScope"/> exists to prevent, reintroduced one layer up.
    ///
    /// An unrecognised scope applies to nothing. A member added later and not taught to this
    /// function must fail closed, because failing open means it applies everywhere.
    /// </remarks>
    public static bool Applies(FormAnswer answer, int? companyId, long? postingId)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return answer.Scope switch
        {
            AnswerScope.Global => true,
            AnswerScope.Company => companyId is not null && answer.CompanyId == companyId,
            AnswerScope.Posting => postingId is not null && answer.PostingId == postingId,
            _ => false,
        };
    }

    /// <summary>
    /// The answer to use, or null where none of them applies here.
    /// </summary>
    /// <remarks>
    /// Three rules, in this order:
    ///
    /// <b>An answer that does not apply here is not ranked at all</b> - see
    /// <see cref="Applies"/>. With no posting in hand, a posting-scoped answer is not a weaker
    /// candidate, it is not a candidate.
    ///
    /// <b>A live answer beats a superseded one whatever the scope.</b> This is the rule that
    /// stops a retracted per-posting paragraph outranking the global answer the candidate
    /// replaced it with - specificity decides between answers that both still stand, and it must
    /// not resurrect one that does not.
    ///
    /// <b>Then the narrowest scope, then the most recent.</b> The scope order is asked as a
    /// question rather than read off the enum's values, for the reason
    /// <see cref="SubmissionEventTypes.IsTerminal"/> is: tying it to the numbering would break
    /// silently the first time a member is inserted. The final tie-break on
    /// <see cref="FormAnswer.Id"/> is there so two answers written in the same tick do not make
    /// the result depend on the order the database happened to return them in.
    ///
    /// <b>A superseded answer is still returned when it is all there is</b>, rather than nothing.
    /// It is the last thing the person actually said, which beats a blank - but a caller filling
    /// a form should read <see cref="FormAnswer.IsLive"/> and treat a superseded winner as
    /// grounds to confirm rather than to type, because the candidate retracted it on purpose.
    /// </remarks>
    public static FormAnswer? Best(IEnumerable<FormAnswer> answers, int? companyId = null, long? postingId = null)
    {
        ArgumentNullException.ThrowIfNull(answers);

        return answers
            .Where(answer => Applies(answer, companyId, postingId))
            .OrderBy(answer => answer.IsLive ? 0 : 1)
            .ThenByDescending(answer => Specificity(answer.Scope))
            .ThenByDescending(answer => answer.AnsweredAtUtc)
            .ThenByDescending(answer => answer.Id)
            .FirstOrDefault();
    }

    private static int Specificity(AnswerScope scope) => scope switch
    {
        AnswerScope.Posting => 3,
        AnswerScope.Company => 2,
        AnswerScope.Global => 1,
        _ => 0,
    };
}
