using System.Globalization;
using System.Text;

namespace JobPlatform.Core.Applications;

/// <summary>
/// How much a board found by probing is believed to belong to the employer on the advert.
/// </summary>
/// <remarks>
/// <b>Zero refuses, and that is the whole reason the numbering starts there.</b> This value
/// travels between a probe and the decision to attach somebody's application to a URL, and the
/// characteristic bug in that journey is a field nobody set - a struct default, a row read before
/// the column existed, a deserialiser that saw no member. Every one of those produces
/// <see cref="Unconfirmed"/>, which is the answer that costs a recovered link rather than the one
/// that sends a covering letter to a stranger. <c>CvSelectionOutcome</c> numbers from one for the
/// opposite reason and it is worth knowing why the two differ: there, a zero would read as a real
/// decision about which document went out; here, zero <i>is</i> the decision to do nothing.
///
/// <b>Ordering is meaningful, unlike <see cref="AtsVendor"/>'s.</b> These are strictly increasing
/// strengths of evidence, so a caller may compare them - a threshold of "at least
/// <see cref="NameAgrees"/>" is a sentence somebody can read. That is a promise: a member inserted
/// later has to be numbered where its strength puts it, not appended.
///
/// <b>What is deliberately not here is a fourth value separating "the board named somebody else"
/// from "the board named nobody".</b> They are different facts - the first says stop probing this
/// employer, the second says this vendor cannot answer the question - and they collapse to one
/// value because nothing today does different things with them. Split them when a caller would
/// act on the difference, and not before: an enum member no reader switches on is a distinction
/// that rots.
/// </remarks>
public enum AtsBoardConfidence
{
    /// <summary>
    /// Nothing here ties this board to this employer. Do not attach a link to it.
    /// </summary>
    /// <remarks>
    /// Covers a board that names a different company, a board that publishes no name at all, and
    /// a name that agrees only on a word too common to carry the claim. All three refuse, and the
    /// refusal is the point: a probed token that cannot be confirmed is a guess, and a guess here
    /// is an application sent to the wrong company under somebody's real name.
    /// </remarks>
    Unconfirmed = 0,

    /// <summary>
    /// The board names this employer. Enough to use, and still an inference.
    /// </summary>
    /// <remarks>
    /// The ordinary confirmed answer, because most callers have no title to corroborate with - a
    /// vendor's board endpoint answers with the employer's name long before anybody enumerates its
    /// jobs. It is <i>not</i> a link the board published about this posting, so a caller storing
    /// the result owes it a provenance that says so, exactly as
    /// <c>ApplyUrlSource.MatchedOnAnotherBoard</c> does for the cross-board recovery.
    /// </remarks>
    NameAgrees = 1,

    /// <summary>
    /// The board names this employer and carries the posting the advert described.
    /// </summary>
    /// <remarks>
    /// Two independent facts agreeing: the employer's name and one of their vacancies. This is
    /// what the live verification of 2026-09-07 looked like - a Greenhouse board answering to
    /// "Cloudflare" that also listed <i>VoidZero Engineer</i>, the posting held here as 3020 and
    /// the one LinkedIn withheld a URL for.
    ///
    /// It is a stronger claim than <see cref="NameAgrees"/> and never a different one: a caller
    /// may raise its own bar to this, but nothing here requires it, because a board listing no job
    /// that matches the advert is usually a board that closed the vacancy rather than a board
    /// belonging to somebody else.
    /// </remarks>
    NameAndPostingAgree = 2,
}

/// <summary>
/// The board tokens worth asking an ATS about when an employer's name is all there is, and
/// whether the board that answers is plausibly theirs.
/// </summary>
/// <remarks>
/// <b>This produces candidates and confirms them; it never tries them.</b> Core is pure, so there
/// is no HTTP here and there must never be - the same rule that lets <c>MatchScorer</c> assert
/// exact numbers and <c>AtsVendorDetector</c> run over a whole queue projection. A peer does the
/// requests. The split matters beyond tidiness: what is worth probing is a judgement about
/// somebody else's API budget, and what is worth trusting is a judgement about somebody's job
/// application. Neither should be decided inside a retry loop.
///
/// <b>Nothing here takes a credential, a cookie or a session, and nothing downstream may add
/// one.</b> The whole reason this class exists is that the alternative - authenticated LinkedIn -
/// was researched and refused: see <c>mcp_handoff.md</c> 3.2 and 3.2a. Greenhouse, Ashby, Lever,
/// Workable and SmartRecruiters publish these boards <i>to job seekers</i>, unauthenticated and
/// documented, which is exactly what makes reading them a different kind of act from driving a
/// signed-in page. A probe that needed to log in would put this feature back on the wrong side of
/// that line.
///
/// <b>Generation and confirmation live in one class because they share one rule, and separating
/// them is how two spellings drift.</b> The distinctiveness floor below decides both what is worth
/// a request and what a name agreement may rest on, and this codebase has already paid for the
/// alternative twice - the shortlist's channel filter, written out in two places and held together
/// only by a test, and <c>ParkReasonPolicy</c>, which was deliberately built the other way so that
/// one function feeds every reader. One definition, two callers, nothing to drift.
///
/// <b>The measurement this is built from</b>, taken 2026-09-07 over the live corpus: of 309
/// applyable postings carrying no employer apply link - every one of them LinkedIn - 122 are at an
/// employer whose ATS is already known from another posting, and naive slugs probed from the name
/// resolved a further 21 of the remaining 120. The failures were the short common words. "Dex",
/// "Kernel", "Fin" and "Orbital" are all real boards belonging to <i>somebody</i>, and not
/// necessarily to the employer on the advert.
/// </remarks>
public static class AtsBoardCandidates
{
    /// <summary>
    /// The most tokens <see cref="For"/> will ever return for one employer.
    /// </summary>
    /// <remarks>
    /// <b>The bound protects the vendor, not us.</b> Every candidate is one request to an API that
    /// exists to serve that ATS's paying customers and their applicants; we are neither. Four
    /// tokens across five vendors is twenty requests to find one employer, and the 2026-09-07
    /// measurement had 120 employers left to resolve - so a fifth token is not one more request,
    /// it is six hundred more, and the difference between a courteous read and something a rate
    /// limiter is right to refuse.
    ///
    /// <b>It is a promise rather than a truncation.</b> The rules below cannot produce a fifth
    /// form, so nothing is ever cut off here - which is deliberate, because a cap that silently
    /// drops the last candidate is a cap that hides a rule change. A new spelling worth trying has
    /// to argue against this number in a diff, not append itself under it. Callers may size a
    /// budget on it: <c>MaxCandidates</c> times the vendors tried is the worst case for one
    /// employer, exactly.
    /// </remarks>
    public const int MaxCandidates = 4;

    /// <summary>Shortest token this will emit at all, counted without separators.</summary>
    /// <remarks>
    /// Three characters or fewer is an initialism, and initialisms collide by construction - there
    /// are a thousand three-letter companies and one <c>ibm</c> on each vendor. Probing one is not
    /// a weak signal, it is no signal.
    /// </remarks>
    public const int MinimumLength = 4;

    /// <summary>
    /// Shortest <i>one-word</i> name that may be probed at all.
    /// </summary>
    /// <remarks>
    /// <b>This is the constant that keeps the measured false positives out, and it costs real
    /// recoveries to do it.</b> The four names that resolved to strangers' boards were single
    /// words - "Dex" (3), "Fin" (3), "Kernel" (6) and "Orbital" (7) - and the single word that
    /// resolved correctly was "Eucalyptus" (10). Eight is drawn between them. Seven cases is not a
    /// distribution, so read this as the boundary the evidence permits rather than as one it
    /// implies.
    ///
    /// <b>The structural half is the load-bearing one.</b> A one-word name yields a token every
    /// company sharing that word has an equal claim to, and a compound does not: nobody registers
    /// <c>capitalontap</c> by coincidence. So the rule is not really "short names are risky", it is
    /// "a token that is a common noun belongs to whoever asked for it first", and length is the
    /// only proxy for that available without a dictionary this repository would then have to
    /// maintain.
    ///
    /// <b>What it costs is stated rather than hidden.</b> Monzo, Stripe and Revolut are all real
    /// employers this refuses to probe. They are not unreachable - the <i>learned</i> path, a token
    /// lifted from a direct link already held for that employer, covers 122 of the 309 and needs no
    /// guess at all - and a five-letter probe is precisely where a guess is least worth making.
    /// Raising the recall here means finding a second signal, never lowering this number.
    ///
    /// <b>The alternative was to probe them anyway and grade the answer down, and it was rejected
    /// on the signature.</b> <see cref="For"/> returns strings; a list of strings has nowhere to
    /// carry "this one is a guess", so a low-confidence token would arrive at the caller
    /// indistinguishable from a good one - which is the exact provenance failure
    /// <c>ApplyUrlSource</c> exists to prevent, reintroduced one layer lower. Confidence is
    /// therefore graded where there is somewhere to put it, in <see cref="Confirm"/>, and this list
    /// stays a list of tokens worth spending a request on.
    /// </remarks>
    public const int MinimumSingleWordLength = 8;

    /// <summary>Longest token this will emit, counted without separators.</summary>
    /// <remarks>
    /// A board token is a path segment somebody typed into a settings page, so nothing real is this
    /// long. Over-long names are dropped whole rather than truncated: a prefix of a company's name
    /// is a plausible <i>different</i> company's name, so truncating manufactures exactly the
    /// collision <see cref="MinimumSingleWordLength"/> refuses.
    /// </remarks>
    public const int MaximumLength = 64;

    /// <summary>Fewest title words that may corroborate a board. See <see cref="Confirm"/>.</summary>
    private const int MinimumTitleWords = 2;

    /// <summary>Apostrophes, in every spelling a name arrives in. Dropped rather than separated.</summary>
    /// <remarks>
    /// The same set and the same argument as <c>ApplicationPackFile</c> and
    /// <c>FormAnswerText.Normalise</c>: <c>Jerry's</c> is one word, and folding the apostrophe to a
    /// separator makes it two - <c>ben-and-jerry-s</c>, a token nobody has ever registered. Spelled
    /// numerically because the three are indistinguishable on screen, and a literal an editor had
    /// re-encoded would silently stop matching.
    /// </remarks>
    private static readonly char[] Apostrophes = [(char)0x0027, (char)0x2019, (char)0x02bc];

    /// <summary>
    /// Legal forms. Stripped, and never restored, because no board token contains one.
    /// </summary>
    /// <remarks>
    /// The same list as <c>CompanyNormalizer</c>'s, minus the spellings that carry punctuation -
    /// <c>s.a.r.l</c>, <c>a/s</c> and their kin have already been cut into pieces by the time
    /// tokenisation is done, so listing them here would be listing entries that can never match.
    /// Order is immaterial, unlike in <c>CompanyNormalizer</c>: that one strips a suffix
    /// <i>string</i> and so has to try "co ltd" before "co" or it leaves a stump behind, where this
    /// matches whole words and strips them repeatedly from the end. "Public Limited Company" loses
    /// both of its tails and keeps "public" without any entry saying so.
    ///
    /// <b>These are separated from <see cref="GenericTails"/> and the separation is the design.</b>
    /// Nobody registers <c>monzobankltd</c>, so a legal form is noise in every candidate and is
    /// removed for good. A descriptor is not: <c>orbitallabs</c> is a token a real company would
    /// choose. One list would have to pick a side, and would be wrong for half the corpus.
    /// </remarks>
    private static readonly string[] LegalForms =
    [
        "incorporated",
        "corporation",
        "limited",
        "company",
        "gmbh",
        "sarl",
        "corp",
        "plc",
        "llp",
        "llc",
        "ltd",
        "inc",
        "pty",
        "srl",
        "spa",
        "bv",
        "nv",
        "ag",
        "sa",
        "ab",
        "as",
        "oy",
        "co",
    ];

    /// <summary>
    /// Generic descriptors. Stripped for the best candidate, and kept for a lesser one.
    /// </summary>
    /// <remarks>
    /// <b>Both spellings are tried because both occur, and which is right is not knowable from the
    /// name.</b> "Deliveroo Group" is <c>deliveroo</c> on its board and "Orbital Labs" is plausibly
    /// <c>orbitallabs</c> on its own; a rule that stripped unconditionally would lose the second,
    /// and one that never stripped would lose the first. So the stripped form leads and the
    /// unstripped form follows it, which costs one extra request where the lead is right and
    /// recovers the employer entirely where it is not.
    ///
    /// <b>Only a listed word is a tail.</b> "Industries" is absent deliberately: the corpus
    /// resolved "Orbital Industries" as <c>orbitalindustries</c>, and a rule that dropped an
    /// arbitrary trailing noun would have produced <c>orbital</c> - one of the four measured false
    /// positives, manufactured by the very code meant to avoid them. Stripping is only ever from a
    /// list somebody wrote down.
    /// </remarks>
    private static readonly string[] GenericTails =
    [
        "technologies",
        "technology",
        "holdings",
        "holding",
        "group",
        "labs",
        "tech",
        "lab",
    ];

    /// <summary>
    /// The board tokens worth trying for this employer, likeliest first, at most
    /// <see cref="MaxCandidates"/>.
    /// </summary>
    /// <remarks>
    /// <b>Empty is a real answer and the commonest one worth having.</b> A blank name, a name that
    /// is nothing but a legal form, a name in a script with no ASCII fold, and above all a name too
    /// short or too generic to distinguish from a common noun all return nothing rather than a
    /// guess - see <see cref="MinimumSingleWordLength"/> for what that costs and why it is still
    /// the right trade. A caller handed an empty list has learned something: this employer is not
    /// reachable by probing, and the answer is a link held elsewhere or no link at all.
    ///
    /// <b>The order is a prior, not a measurement, and it is written down so it can be tested
    /// later.</b> The stripped form leads the unstripped one because companies drop descriptors
    /// from their slugs more often than they keep them, and the concatenated form leads the
    /// hyphenated one because that is the shape Greenhouse and Ashby tokens overwhelmingly take. If
    /// a corpus of resolved tokens ever exists, re-derive both claims from it rather than defending
    /// them.
    ///
    /// <b>It never returns a proper prefix of the name's own words.</b> "Orbital Industries" yields
    /// <c>orbitalindustries</c> and <c>orbital-industries</c> and never <c>orbital</c>. That is not
    /// an omission to be helpfully filled in: <c>orbital</c> is a board, it belongs to somebody, and
    /// the entire difference between a recovery and a misdirected application is whether this list
    /// contains it.
    ///
    /// Duplicates are folded, so a one-word name yields exactly one candidate - the hyphenated and
    /// concatenated spellings of a single word are the same string, and a caller should not spend a
    /// request discovering that.
    /// </remarks>
    public static IReadOnlyList<string> For(string? company)
    {
        var named = Tokenise(company);
        var full = StripTail(named, LegalForms);
        var core = StripTail(full, GenericTails);

        var candidates = new List<string>(MaxCandidates);

        if (IsDistinctive(core))
        {
            Offer(candidates, string.Concat(core));
            Offer(candidates, string.Join('-', core));
        }

        // Only where a descriptor was actually removed. Otherwise these are the same two strings
        // as above, and code that reads as though it were trying something else is worse than no
        // code at all.
        if (core.Count != full.Count && IsDistinctive(full))
        {
            Offer(candidates, string.Concat(full));
            Offer(candidates, string.Join('-', full));
        }

        return candidates;
    }

    /// <summary>
    /// Whether a board that answered a probe plausibly belongs to the employer on the advert.
    /// </summary>
    /// <remarks>
    /// <b>This is the function that stops a slug collision attaching one employer's postings to
    /// another employer's board</b>, and it is the reason a probed token may be used at all. The
    /// probe itself proves nothing: a vendor's board endpoint answers 200 for whoever owns that
    /// token, and a 200 read as a confirmation is how an application reaches a company the
    /// candidate never applied to.
    ///
    /// <b>The name decides and the title may only corroborate.</b> A title cannot rescue a name
    /// that disagrees, because titles are generic - "Software Engineer" is on every board there
    /// is - so a title match on a stranger's board is not weak evidence, it is the collision
    /// wearing evidence's clothes. Conversely a missing title never lowers the answer: a board that
    /// has closed the vacancy is still that employer's board, and the advert this system holds may
    /// be days older than the listing.
    ///
    /// <b>The token is deliberately not a parameter.</b> Comparing the token back against the
    /// company name would be circular - <see cref="For"/> produced it from that name - so it would
    /// confirm every probe, including every wrong one, while looking like a check.
    ///
    /// <b>A board that will not name itself cannot be confirmed here, and that is a real cost.</b>
    /// Lever's public postings feed carries no company name, so on the evidence this function takes
    /// a Lever board is always <see cref="AtsBoardConfidence.Unconfirmed"/>. The fix is to pass the
    /// name from wherever that vendor does publish one - never the token, for the reason above - or
    /// to leave Lever to the learned path, where no confirmation is owed because the link came from
    /// the employer in the first place.
    /// </remarks>
    /// <param name="company">The employer name the advert carried.</param>
    /// <param name="boardName">The name the board reports for itself.</param>
    /// <param name="postingTitle">Optional: a title that ought to appear on the board.</param>
    /// <param name="boardTitles">Optional: the titles the board actually lists.</param>
    public static AtsBoardConfidence Confirm(
        string? company,
        string? boardName,
        string? postingTitle = null,
        IEnumerable<string>? boardTitles = null)
    {
        if (!NamesAgree(company, boardName))
        {
            return AtsBoardConfidence.Unconfirmed;
        }

        return CarriesTitle(postingTitle, boardTitles)
            ? AtsBoardConfidence.NameAndPostingAgree
            : AtsBoardConfidence.NameAgrees;
    }

    /// <summary>
    /// Whether the advert's employer and the board's own name are the same company.
    /// </summary>
    /// <remarks>
    /// <b>Equal agrees; a prefix agrees only if the shared part could have been probed on its
    /// own.</b> Equality is the easy half - "Capital on Tap" against "Capital on Tap Ltd" is one
    /// company written twice. The prefix rule is where the collisions live, and it cuts both ways:
    /// "Acme Technologies" against a board calling itself "Acme" is one company shortening its own
    /// name, while "Orbital Industries" against a board calling itself "Orbital" is very probably
    /// two companies sharing a word. Nothing in the strings separates those, so the separator is
    /// the shared part itself - <c>acme</c> after descriptor stripping is the whole of both names,
    /// where <c>orbital</c> is a common noun that fails the same floor <see cref="For"/> refuses to
    /// probe on.
    ///
    /// <b>Both sides are folded identically, tails included.</b> A board writes "Ltd" as often as an
    /// advert omits it, and an asymmetric fold would refuse a match over a suffix neither company
    /// chose. Accents fold on both sides too, so "Societe Generale" spelled with them and without
    /// them is one employer rather than two.
    ///
    /// <b>The residual is a distinctive first word two companies both use</b> - an advert saying
    /// "Cloudflare" against a board calling itself "Cloudflare Capital Partners". This agrees, and
    /// it is not fixable from the names: the same shape is "Monzo" against "Monzo Bank", which is
    /// one company. That is what the posting title in <see cref="Confirm"/> is for, and why
    /// <see cref="AtsBoardConfidence.NameAndPostingAgree"/> exists as a bar a cautious caller can
    /// raise to rather than as decoration on the ordinary answer.
    /// </remarks>
    private static bool NamesAgree(string? company, string? boardName)
    {
        var advert = Canonical(company);
        var board = Canonical(boardName);

        if (advert.Count == 0 || board.Count == 0)
        {
            return false;
        }

        var shorter = advert.Count <= board.Count ? advert : board;
        var longer = advert.Count <= board.Count ? board : advert;

        for (var index = 0; index < shorter.Count; index++)
        {
            if (!string.Equals(shorter[index], longer[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return shorter.Count == longer.Count || IsDistinctive(shorter);
    }

    /// <summary>Whether one of the board's titles is the advert's title.</summary>
    /// <remarks>
    /// <b>Whole words, either direction, and never on one word alone.</b> Boards and adverts
    /// decorate the same role differently - "VoidZero Engineer" here, "VoidZero Engineer, London"
    /// there, "Senior Engineer (Remote)" on a third - so containment on a word boundary is what
    /// matches them, and a plain substring test is what would match "Engineer" inside "Engineering
    /// Manager". Requiring <see cref="MinimumTitleWords"/> stops the shorter side from being a
    /// single generic noun that every board on earth satisfies.
    ///
    /// This is deliberately looser than the title-and-place match a caller does to attach a
    /// <i>particular</i> posting to a <i>particular</i> board entry, and its looseness is bounded by
    /// where it is used: it can only raise a confidence the names already earned, so the worst it
    /// can do is report <see cref="AtsBoardConfidence.NameAndPostingAgree"/> where
    /// <see cref="AtsBoardConfidence.NameAgrees"/> was the honest answer - never confirm a board
    /// that would otherwise have been refused.
    /// </remarks>
    private static bool CarriesTitle(string? postingTitle, IEnumerable<string>? boardTitles)
    {
        if (boardTitles is null)
        {
            return false;
        }

        var wanted = Tokenise(postingTitle);

        if (wanted.Count == 0)
        {
            return false;
        }

        foreach (var title in boardTitles)
        {
            var offered = Tokenise(title);

            if (Math.Min(wanted.Count, offered.Count) < MinimumTitleWords)
            {
                continue;
            }

            if (ContainsWords(offered, wanted) || ContainsWords(wanted, offered))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="needle"/>'s words appear, in order, inside
    /// <paramref name="haystack"/>.
    /// </summary>
    /// <remarks>
    /// Padded at both ends so the comparison lands on word boundaries and cannot half-match a
    /// word - the same discipline as <c>AtsVendorDetector.IsAtOrUnder</c>, where an unguarded
    /// substring test makes <c>clever.com</c> into Lever.
    /// </remarks>
    private static bool ContainsWords(IReadOnlyList<string> haystack, IReadOnlyList<string> needle)
        => (' ' + string.Join(' ', haystack) + ' ')
            .Contains(' ' + string.Join(' ', needle) + ' ', StringComparison.Ordinal);

    /// <summary>A name reduced to the words a board token could be built from.</summary>
    /// <remarks>
    /// <b>An ASCII fold rather than <c>JobFingerprint</c>'s or <c>CompanyNormalizer</c>'s, because
    /// the output is a URL path segment and theirs are not.</b> Both of those keep any letter -
    /// <c>char.IsLetterOrDigit</c> is true of an accented one - which is right for a content hash
    /// and for a chart's grouping key and wrong here, where the vendors issue ASCII slugs and
    /// <c>societe</c> is the token somebody typed. Decomposing to Form D first is what lets the
    /// accent be dropped on its own rather than taking the letter with it, exactly as
    /// <c>ApplicationPackFile.Ascii</c> does.
    ///
    /// <b>Not a call to <c>CompanyNormalizer.Key</c>, deliberately.</b> That key groups rows in the
    /// <c>topCompanies</c> chart and is free to change for charting reasons; this decides which
    /// employer receives an application. Sharing it would let a change made to fix a bar chart
    /// silently move where somebody's CV goes - the same argument <c>CompanyNormalizer</c> itself
    /// makes for not calling <c>JobFingerprint.Normalize</c>.
    ///
    /// <b>An ampersand becomes the word it is spoken as.</b> Folding it to a separator is what every
    /// other normaliser here does, and it is wrong for this one output: "B&amp;Q" would become
    /// <c>bq</c> and "Ben &amp; Jerry's" <c>benjerrys</c>, neither of which anybody has registered,
    /// where <c>bandq</c> and <c>benandjerrys</c> are what a person typing the token writes.
    ///
    /// A name that folds away entirely - written in a script with no ASCII form - yields no words
    /// and therefore no candidates. That is the correct answer rather than a failure: there is no
    /// token to guess at, and half of one is worse than none.
    /// </remarks>
    private static List<string> Tokenise(string? value)
    {
        var words = new List<string>();

        if (string.IsNullOrWhiteSpace(value))
        {
            return words;
        }

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark
                || Array.IndexOf(Apostrophes, character) >= 0)
            {
                continue;
            }

            if (character == '&')
            {
                Flush(words, builder);
                words.Add("and");

                continue;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));

                continue;
            }

            Flush(words, builder);
        }

        Flush(words, builder);

        return words;
    }

    /// <summary>Both tail lists applied, which is the form two names are compared in.</summary>
    private static List<string> Canonical(string? value)
        => StripTail(StripTail(Tokenise(value), LegalForms), GenericTails);

    /// <summary>
    /// The words with every trailing entry of <paramref name="tails"/> removed.
    /// </summary>
    /// <remarks>
    /// Repeatedly, because "Contoso Holdings Group" carries two and one pass would leave the first.
    /// Only from the end: a tail word inside a name is part of the name - "Group Nine Media" is not
    /// "Nine Media" - so stripping from anywhere would rewrite employers rather than normalise them.
    /// </remarks>
    private static List<string> StripTail(List<string> words, string[] tails)
    {
        var removed = 0;

        while (words.Count - removed > 0
            && Array.IndexOf(tails, words[words.Count - removed - 1]) >= 0)
        {
            removed++;
        }

        return removed == 0 ? words : words.GetRange(0, words.Count - removed);
    }

    /// <summary>
    /// Whether these words name a company specifically enough to spend a request on.
    /// </summary>
    /// <remarks>
    /// <b>One rule with two readers</b>, which is the whole reason confirmation lives in this
    /// class: it decides what <see cref="For"/> will probe and what a prefix agreement in
    /// <see cref="NamesAgree"/> may rest on. Written twice those would drift, and the drift would be
    /// silent - a token this refuses to emit would still be accepted by a confirmation relaxed
    /// independently of it, which is a hole with nothing to fail.
    ///
    /// Two words are enough at any length, because a compound is self-disambiguating; one word has
    /// to earn it by being long enough not to be a common noun. See
    /// <see cref="MinimumSingleWordLength"/> for the measurement behind that and for what it costs.
    /// </remarks>
    private static bool IsDistinctive(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return false;
        }

        var length = 0;

        foreach (var word in words)
        {
            length += word.Length;
        }

        return length >= MinimumLength
            && length <= MaximumLength
            && (words.Count > 1 || words[0].Length >= MinimumSingleWordLength);
    }

    /// <summary>Appends a candidate unless it is already offered or the bound is reached.</summary>
    private static void Offer(List<string> candidates, string candidate)
    {
        if (candidates.Count < MaxCandidates
            && !candidates.Contains(candidate, StringComparer.Ordinal))
        {
            candidates.Add(candidate);
        }
    }

    /// <summary>Moves the pending characters into a word, if there are any.</summary>
    private static void Flush(List<string> words, StringBuilder builder)
    {
        if (builder.Length > 0)
        {
            words.Add(builder.ToString());
            builder.Clear();
        }
    }
}
