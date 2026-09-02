namespace JobPlatform.Core.Submissions;

/// <summary>
/// What a message said about itself, reduced to the parts that could name an application.
/// </summary>
/// <remarks>
/// <b>There is no field for the message.</b> Not a body, not a snippet, not a quoted first line -
/// and that absence is the contract rather than an omission. A note in this system is bounded
/// free text a person reads, and a pasted recruiter message is somebody's name, direct line and
/// signature block written into a database that is otherwise careful never to hold one. A type
/// with nowhere to put a body is the only version of that rule a caller cannot get wrong by
/// accident, so body-derived evidence arrives already reduced to <c>CompanyMentions</c> - names,
/// not prose.
///
/// <b>Nothing here is a sender address.</b> The parse step throws recruiter addresses away
/// deliberately, because this repository is public, and that decision is not re-litigated here.
/// What survives is the sending <i>domain</i>, which names an organisation rather than a person.
/// An address where a domain is asked for is refused outright rather than trimmed down to one -
/// see <see cref="EmailSubmissionMatcher.Match"/> - because quietly accepting it would make this
/// type the route by which one came back.
/// </remarks>
/// <param name="ReceivedAtUtc">When the message arrived. Rules out applications that did not yet exist, and orders equals. Never scored.</param>
/// <param name="Subject">The subject line. Prose, and treated as such: a name found only here is weaker than one the sender put in its own.</param>
/// <param name="SenderDisplayName">The name the sender gave itself - "Acme Robotics Careers", "Greenhouse on behalf of Acme". The strongest thing a message says about who it is.</param>
/// <param name="SenderDomain">The domain the message came from, with no local part. Null where the transport did not say.</param>
/// <param name="SenderAtsVendor">
/// The applicant tracking vendor that sending domain belongs to, resolved by the caller, and null
/// unless it names a particular one. <c>Unknown</c>, <c>Other</c> and <c>Aggregator</c> are not
/// vendors and are read as null even when spelled out, because a caller wiring this up from an
/// enum will reach for <c>ToString()</c> and "everything unrecognised agrees with everything
/// unrecognised" is the worst available reading of that.
/// </param>
/// <param name="CompanyMentions">Employer names the caller read out of the message, in whatever spelling it used. Null or empty where there were none.</param>
public sealed record EmailIdentityTokens(
    DateTimeOffset ReceivedAtUtc,
    string? Subject,
    string? SenderDisplayName,
    string? SenderDomain,
    string? SenderAtsVendor,
    IReadOnlyList<string>? CompanyMentions);

/// <summary>
/// One application a message might be about, as the caller already knows it.
/// </summary>
/// <remarks>
/// <b>The caller chooses the candidate set, and that choice is part of the answer.</b> Handing
/// this every submission ever made invites an abstention it could have avoided: two applications
/// to one employer cannot be told apart by anything a message carries, so a rejection from last
/// year will happily make this year's interview invitation ambiguous. Pass the live ones.
///
/// <b>What is absent is deliberate.</b> There is no job title and no posting id, because nothing
/// in a message reliably carries either - a subject line naming the role is the exception rather
/// than the rule. That absence is what forces the same-employer abstention, and it is honest:
/// this function holds employer-level evidence and must not pretend to posting-level answers.
/// </remarks>
/// <param name="SubmissionId">The application's id, which is what a match returns.</param>
/// <param name="Company">The employer, as the posting named them.</param>
/// <param name="ApplyUrlHost">The host of the apply URL where one is known. A host, not a whole URL.</param>
/// <param name="AtsVendor">The vendor that host implies, or null where it implies no particular one.</param>
/// <param name="CreatedAtUtc">When the application was recorded.</param>
public sealed record EmailMatchCandidate(
    long SubmissionId,
    string Company,
    string? ApplyUrlHost,
    string? AtsVendor,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// One thing the matcher noticed about a candidate.
/// </summary>
/// <remarks>
/// <b>Recorded so that a wrong answer can be argued with afterwards.</b> A confidence on its own
/// is unfalsifiable - it says how sure something was without saying what it was sure of - and
/// these decisions end up as events written against somebody's application. The reasoning that
/// makes <see cref="SubmissionEventSource"/> a stored fact rather than an implementation detail
/// applies here too: the question asked after a mistake is always which evidence carried it.
/// </remarks>
public enum EmailMatchSignal
{
    /// <summary>The employer's name was in the name the sender gave itself.</summary>
    CompanyInSenderName = 1,

    /// <summary>The employer's name was in the subject line.</summary>
    CompanyInSubject = 2,

    /// <summary>The employer's name was among the names the caller read out of the message.</summary>
    CompanyInMention = 3,

    /// <summary>
    /// The employer's name is an ordinary English word, so finding it in prose proves less.
    /// </summary>
    /// <remarks>
    /// A qualifier on the company signals above rather than evidence of its own. "Next", "Box"
    /// and "Monday" turn up in a message about somebody else's application every day of the week.
    /// Beside <see cref="CompanyInSubject"/> it means the name was seen and deliberately not
    /// counted, which is worth saying out loud: otherwise the only trace of that decision is a
    /// confidence lower than a reader expected.
    /// </remarks>
    CompanyIsOrdinaryWord = 4,

    /// <summary>The sending domain and the apply URL's host are the same organisation.</summary>
    SenderHostMatchesApplyHost = 5,

    /// <summary>The sending domain belongs to the vendor this application was made through.</summary>
    SenderVendorMatchesApplyVendor = 6,

    /// <summary>
    /// Another candidate carries the same sender evidence, so it separates neither of them.
    /// </summary>
    /// <remarks>
    /// Every Greenhouse application is reached from <c>greenhouse.io</c>. That a message came
    /// from there is real evidence about the <i>channel</i> and almost none about <i>which</i>
    /// application, and this is what says so on the record.
    /// </remarks>
    SenderEvidenceSharedWithOtherCandidates = 7,
}

/// <summary>
/// What the matcher concluded. <b>Four of the five members are abstentions.</b>
/// </summary>
/// <remarks>
/// Spelled out by reason rather than collapsed into a null, because the four want four different
/// responses: nothing to match against is a bug in the caller's query, no evidence is a message
/// about something else entirely, weak evidence is worth showing a person, and ambiguity is worth
/// <i>asking</i> one. Collapsed, a client could not tell the message it should file away from the
/// message it should raise a question about.
/// </remarks>
public enum EmailMatchOutcome
{
    /// <summary>One application, above the confidence floor, with no rival near it.</summary>
    Matched = 1,

    /// <summary>Nothing was offered to match against.</summary>
    NoCandidates = 2,

    /// <summary>Nothing in the message pointed at any of them.</summary>
    NoEvidence = 3,

    /// <summary>Something pointed, but not hard enough to write on an application.</summary>
    NotConfident = 4,

    /// <summary>Two applications the evidence cannot separate. The archetype is two live applications to one employer.</summary>
    Ambiguous = 5,
}

/// <summary>How well one candidate fits the message, and on what grounds.</summary>
/// <param name="SubmissionId">The application scored.</param>
/// <param name="Confidence">Nought to <see cref="EmailSubmissionMatcher.Ceiling"/>, in hundredths, so that equal evidence compares equal.</param>
/// <param name="Signals">Everything that fired, in a fixed order, the qualifiers that weakened it included.</param>
public sealed record EmailMatchScore(
    long SubmissionId,
    double Confidence,
    IReadOnlyList<EmailMatchSignal> Signals);

/// <summary>
/// The matcher's answer.
/// </summary>
/// <remarks>
/// <b><see cref="Match"/> is null unless <see cref="Outcome"/> is
/// <see cref="EmailMatchOutcome.Matched"/>.</b> A caller that ignores the outcome gets a null
/// reference rather than a plausible wrong answer, which is the right way round: a rejection
/// written onto the wrong application is not recoverable by an append-only log, because the event
/// recording it is itself true history.
///
/// <see cref="Ranked"/> is populated whatever the outcome, and it is what makes an abstention
/// useful - it is the shortlist to put in front of a person.
/// </remarks>
/// <param name="Outcome">What was concluded.</param>
/// <param name="Match">The application, where there is one. Null on every abstention.</param>
/// <param name="Ranked">Every candidate that carried any evidence at all, best first.</param>
public sealed record EmailMatch(
    EmailMatchOutcome Outcome,
    EmailMatchScore? Match,
    IReadOnlyList<EmailMatchScore> Ranked)
{
    /// <summary>Whether the matcher declined to answer.</summary>
    public bool Abstained => Outcome != EmailMatchOutcome.Matched;
}

/// <summary>
/// Which application a recruiter message is about, decided from identifying tokens alone.
/// </summary>
/// <remarks>
/// Pure and free of every Azure type, like <c>MatchScorer</c> and <see cref="SubmissionState"/> -
/// no mailbox, no model, no clock of its own. That is what makes the ambiguous cases assertable
/// exactly, and the ambiguous cases are the whole point of the file.
///
/// <b>The evidence is thinner than it looks, and the design starts there.</b> There is no stored
/// employer sender domain to compare against, because recruiter addresses are discarded at parse
/// time. What is left is the employer's name, the host of the apply URL, the vendor that host
/// implies, and when things happened. Everything below follows from that being all there is.
///
/// <b>A signal every candidate carries picks none of them out.</b> This is the rule doing most of
/// the work, and it is measured against the candidate set rather than against a list of
/// known-generic domains. A no-reply address at a bulk mail provider matches no apply host and so
/// scores nothing without having to be recognised; <c>greenhouse.io</c> matches every Greenhouse
/// application at once, so it is weighted as the corroboration it is rather than the
/// identification it resembles. Nothing to maintain, and it stays right for vendors nobody has
/// heard of yet.
///
/// <b>Vendor agreement is real evidence and cannot carry a match by itself.</b> Applicant
/// tracking systems send from their own domains, so a message from one genuinely came from the
/// system the application went into - and so did every other application to that system. The
/// sender host and the sender vendor are also <i>one fact</i> rather than two: they are taken as
/// a maximum and never a sum, because <c>boards.greenhouse.io</c> agreeing with a Greenhouse
/// sender is the same observation counted twice, and summing it would let the channel alone clear
/// the floor.
///
/// <b>An employer whose name is an ordinary word has to be named rather than merely mentioned.</b>
/// "Next", "Box" and "Monday" appear in ordinary recruiting prose about somebody else's
/// application, so a subject line containing one is seen, recorded and scored at nothing - see
/// <see cref="SubjectOrdinaryPoints"/> for the specific failure that zero prevents. The sender's
/// own name and a company the caller extracted are claims about who the message is from and about
/// rather than words in a sentence, so those still count.
///
/// <b>Recency is never scored.</b> It rules out an application that did not exist when the message
/// arrived, and it orders candidates the evidence has already tied. That is all. Letting it break
/// a tie would defeat the abstention this function exists for - two live applications to one
/// employer are precisely the case where the more recent one is the tempting wrong answer.
///
/// <b>Two applications to one employer end in <see cref="EmailMatchOutcome.Ambiguous"/>, whatever
/// the margin.</b> Nothing a message carries is about a posting: the company, the domain and the
/// vendor are all facts about the employer, so a candidate that is ahead is ahead on evidence its
/// sibling shares. Guessing there writes a rejection onto the wrong application, and asking a
/// person one question is cheaper than that by a wide margin.
///
/// <b>The ceiling is <see cref="Ceiling"/> rather than 1.0, deliberately.</b> Nothing this
/// function can see justifies certainty.
/// </remarks>
public static class EmailSubmissionMatcher
{
    /// <summary>The confidence a match must reach. Below it the answer is a shortlist, not an answer.</summary>
    /// <remarks>
    /// Set so that an employer naming itself in the sender's own name is enough on its own and
    /// nothing else is. A subject line, a body mention, a vendor and a host each need company
    /// evidence beside them to get here, which is the arithmetic saying what the remarks above
    /// say in prose: the channel corroborates and the employer identifies.
    /// </remarks>
    public const double MatchFloor = MatchFloorPoints / 100d;

    /// <summary>How far clear of the runner-up the leader must be before it is an answer.</summary>
    /// <remarks>
    /// Two candidates within this of each other are two candidates the evidence did not separate,
    /// whichever way the arithmetic happened to fall. Scoring is in whole points out of a hundred
    /// so that identical evidence compares identical rather than nearly so.
    /// </remarks>
    public const double AmbiguityMargin = AmbiguityMarginPoints / 100d;

    /// <summary>The most this function will ever claim: the employer's own name and its own domain.</summary>
    public const double Ceiling = (SenderNamePoints + HostPoints) / 100d;

    /// <summary>
    /// How long after a message a submission may be created and still be what it was about.
    /// </summary>
    /// <remarks>
    /// A message cannot be a reply to an application that did not exist, and one recorded a week
    /// later is a different application. The grace is for sloppy clocks and date-only timestamps
    /// rather than for backfilled history: somebody importing last year's applications today does
    /// get their old messages ruled out, and that is the safe direction - ruling out too much
    /// costs an abstention, ruling out too little costs a rejection filed against the wrong job.
    /// </remarks>
    public static readonly TimeSpan CreationGrace = TimeSpan.FromDays(1);

    private const int SenderNamePoints = 55;
    private const int SenderNameOrdinaryPoints = 35;
    private const int SubjectPoints = 45;
    private const int MentionPoints = 40;
    private const int MentionOrdinaryPoints = 25;

    /// <summary>
    /// An ordinary word in a subject line is worth nothing, and the zero is the rule.
    /// </summary>
    /// <remarks>
    /// <b>Not a small number - none.</b> The failure this prevents is specific: one application
    /// went through a vendor nobody else here used, a message arrives from that vendor, and the
    /// subject happens to contain the word "next". The vendor is worth thirty and the floor is
    /// fifty, so any positive value for the coincidence is the value that decides it, and the
    /// coincidence would have picked the application. A word in somebody's prose is not a claim
    /// that the message is about an employer; the sender's own name and a caller's extracted
    /// mention both are, which is why those keep a weight when the name is ordinary.
    ///
    /// It is still <i>recorded</i> - <see cref="EmailMatchSignal.CompanyIsOrdinaryWord"/> fires
    /// beside <see cref="EmailMatchSignal.CompanyInSubject"/> - so the reason a message that
    /// visibly names the employer did not match is on the record rather than inferred from a
    /// missing signal.
    /// </remarks>
    private const int SubjectOrdinaryPoints = 0;
    private const int HostPoints = 35;
    private const int SharedHostPoints = 15;
    private const int VendorPoints = 30;
    private const int SharedVendorPoints = 10;
    private const int MatchFloorPoints = 50;
    private const int AmbiguityMarginPoints = 10;

    /// <summary>
    /// How much of a free-text field is read.
    /// </summary>
    /// <remarks>
    /// A subject line is one line. Something arriving here at a thousand characters is a caller
    /// pasting a message into the only string-shaped hole this type has, and the bound makes that
    /// a bounded scan rather than an unbounded one. It costs no evidence: an employer's name first
    /// appearing nine hundred characters into a "subject" was not in the subject.
    /// </remarks>
    private const int MaxScannedCharacters = 512;

    /// <summary>How many extracted names are read. Past this a caller is listing rather than naming.</summary>
    private const int MaxCompanyMentions = 32;

    /// <summary>
    /// Words that are also company names, which is why finding one in prose proves little.
    /// </summary>
    /// <remarks>
    /// <b>Membership asks for corroboration; it never refuses.</b> An over-inclusive list costs
    /// abstentions and an under-inclusive one costs a rejection written against the wrong
    /// application, so it errs towards inclusion - ordinary nouns and verbs first, then the
    /// vocabulary recruiting messages are built out of. It is not a lexicon and does not need to
    /// become one: a name of two or more words is distinctive by combination, so this only ever
    /// decides single-word employers.
    /// </remarks>
    private static readonly HashSet<string> OrdinaryWords = new(StringComparer.Ordinal)
    {
        // Ordinary words that are also employers.
        "apple", "block", "bloom", "box", "canvas", "circle", "cloud", "core", "delta", "drive",
        "echo", "edge", "element", "field", "flow", "focus", "forge", "form", "front", "future",
        "grid", "ground", "hive", "hub", "impact", "index", "insight", "key", "layer", "leaf",
        "level", "light", "link", "live", "loop", "mark", "mesh", "method", "mind", "mission",
        "moment", "monday", "motion", "next", "north", "notion", "ocean", "one", "open", "orbit",
        "order", "pace", "palm", "path", "peak", "pillar", "pivot", "pixel", "place", "plan",
        "point", "prime", "pulse", "quest", "range", "rate", "real", "relay", "rise", "river",
        "rocket", "root", "round", "route", "scale", "scope", "sense", "shape", "shift", "side",
        "signal", "simple", "sky", "slack", "smart", "snap", "solid", "source", "space", "spark",
        "sphere", "spring", "square", "stack", "stage", "star", "state", "step", "stone", "storm",
        "stream", "stripe", "summit", "sun", "swift", "switch", "table", "target", "tempo",
        "thread", "three", "tide", "time", "tone", "top", "torch", "touch", "track", "trail",
        "tree", "trend", "true", "trust", "turn", "two", "unity", "valley", "value", "vector",
        "view", "vision", "voice", "wave", "way", "wire", "wise", "work", "world", "zone",

        // The vocabulary a recruiting message is written in, where an employer's name would be
        // indistinguishable from the boilerplate around it.
        "application", "apply", "bank", "candidate", "capital", "care", "career", "careers",
        "city", "consulting", "digital", "energy", "first", "global", "health", "hiring",
        "interview", "job", "jobs", "labs", "life", "london", "media", "new", "offer", "partners",
        "people", "position", "recruiting", "recruitment", "role", "services", "solutions",
        "studio", "systems", "talent", "team", "tech", "technology", "update", "ventures",
        "works",
    };

    /// <summary>
    /// Tokens that name a legal form rather than an employer.
    /// </summary>
    /// <remarks>
    /// Dropped before matching, because the posting says "Acme Robotics Ltd" and the message says
    /// "Acme Robotics", and dropped before the words are counted, because "Monday Group" is as
    /// ordinary a word as "Monday". Where a name is <i>nothing but</i> these, the original tokens
    /// are kept: an employer called Group exists somewhere, and matching on an empty name would
    /// match every message ever sent.
    /// </remarks>
    private static readonly HashSet<string> LegalForms = new(StringComparer.Ordinal)
    {
        "ltd", "limited", "inc", "incorporated", "llc", "llp", "plc", "gmbh", "ag", "bv", "nv",
        "sa", "sas", "srl", "spa", "oy", "ab", "aps", "pty", "co", "corp", "corporation",
        "company", "holdings", "holding", "group",
    };

    /// <summary>
    /// Vendor names that name no vendor.
    /// </summary>
    /// <remarks>
    /// The vendor arrives as a string precisely so this file need not own the catalogue of them,
    /// and the cost of that is a caller writing <c>Unknown</c> into it. Two applications whose
    /// vendor is unknown do not share a vendor, and reading them as agreeing would make the least
    /// informative fact in the system its most decisive one.
    /// </remarks>
    private static readonly HashSet<string> NonVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "other", "none", "aggregator",
    };

    /// <summary>
    /// Second-level labels that are part of a public suffix rather than part of a name.
    /// </summary>
    /// <remarks>
    /// Without this, "the shorter domain is a suffix of the longer" reads <c>co.uk</c> as an
    /// organisation and agrees every British employer with every other one. Short and specific
    /// rather than a public suffix list: the only domains reaching that test are ones a message
    /// was actually sent from or an application actually made through, and neither is a bare
    /// public suffix.
    /// </remarks>
    private static readonly HashSet<string> PublicSuffixLabels = new(StringComparer.Ordinal)
    {
        "co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "govt",
    };

    /// <summary>
    /// Decides which application a message is about, or declines to.
    /// </summary>
    /// <remarks>
    /// The passes run in this order and each is here for a reason:
    ///
    /// <b>Applications that did not exist yet are dropped before anything is scored</b>, so a
    /// message can never be filed against an application made after it arrived.
    ///
    /// <b>Signals are counted across the surviving set before they are weighted</b>, because
    /// whether the sending system separates these candidates is a fact about the set rather than
    /// about any one of them.
    ///
    /// <b>The abstentions are checked in the order a caller can act on them</b> - nothing to match
    /// against, nothing pointing, nothing convincing, nothing separating.
    /// </remarks>
    /// <param name="tokens">What the message said about itself.</param>
    /// <param name="candidates">The applications it might be about. The caller's shortlist, not the whole history.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="tokens"/> carries an email address where a domain was asked for. Refused
    /// rather than trimmed: the address is discarded at parse time on purpose, and a matcher that
    /// quietly accepted one would be the route by which it came back.
    /// </exception>
    public static EmailMatch Match(EmailIdentityTokens tokens, IReadOnlyList<EmailMatchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(candidates);

        if (tokens.SenderDomain?.Contains('@', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException(
                "SenderDomain must be a domain and never an address; recruiter addresses are discarded at parse time.",
                nameof(tokens));
        }

        if (candidates.Count == 0)
        {
            return new EmailMatch(EmailMatchOutcome.NoCandidates, Match: null, Ranked: []);
        }

        var senderName = Tokenize(tokens.SenderDisplayName);
        var subject = Tokenize(tokens.Subject);
        var mentions = (tokens.CompanyMentions ?? [])
            .Take(MaxCompanyMentions)
            .Select(CompanyTokens)
            .Where(name => name.Length > 0)
            .ToList();

        var senderDomain = Host(tokens.SenderDomain);
        var senderVendor = Vendor(tokens.SenderAtsVendor);

        var evidence = candidates
            .Where(candidate => candidate.CreatedAtUtc - tokens.ReceivedAtUtc <= CreationGrace)
            .Select(candidate =>
            {
                var company = CompanyTokens(candidate.Company);

                return new Evidence(
                    Candidate: candidate,
                    CompanyKey: string.Join(' ', company),
                    IsOrdinaryWord: company.Length == 1 && OrdinaryWords.Contains(company[0]),
                    InSenderName: Contains(senderName, company),
                    InSubject: Contains(subject, company),
                    InMention: mentions.Any(mention => Contains(mention, company) || Contains(company, mention)),
                    HostAgrees: DomainsAgree(senderDomain, Host(candidate.ApplyUrlHost)),
                    VendorAgrees: senderVendor is not null && senderVendor == Vendor(candidate.AtsVendor));
            })
            .ToList();

        // Shared means shared with somebody else, so a candidate is never counted as sharing with
        // itself: the one application that went through a vendor nobody else here used is exactly
        // the case where the vendor does identify it.
        var sharedHost = evidence.Count(e => e.HostAgrees) > 1;
        var sharedVendor = evidence.Count(e => e.VendorAgrees) > 1;

        var scored = evidence
            .Select(e => (Evidence: e, Points: Points(e, sharedHost, sharedVendor)))
            .Where(row => row.Points > 0)
            .OrderByDescending(row => row.Points)
            .ThenByDescending(row => row.Evidence.Candidate.CreatedAtUtc)
            .ThenBy(row => row.Evidence.Candidate.SubmissionId)
            .ToList();

        var ranked = scored
            .Select(row => new EmailMatchScore(
                row.Evidence.Candidate.SubmissionId,
                row.Points / 100d,
                Signals(row.Evidence, sharedHost, sharedVendor)))
            .ToList();

        if (ranked.Count == 0)
        {
            return new EmailMatch(EmailMatchOutcome.NoEvidence, Match: null, Ranked: []);
        }

        if (scored[0].Points < MatchFloorPoints)
        {
            return new EmailMatch(EmailMatchOutcome.NotConfident, Match: null, ranked);
        }

        if (scored.Count > 1 && scored[0].Points - scored[1].Points < AmbiguityMarginPoints)
        {
            return new EmailMatch(EmailMatchOutcome.Ambiguous, Match: null, ranked);
        }

        var leader = scored[0].Evidence;

        // Asked of everything that survived rather than of the ranking, because a sibling
        // application at the same employer is a reason to stop whether or not the message
        // happened to say anything that scored against it.
        var sameEmployer = leader.CompanyKey.Length > 0 && evidence.Any(other =>
            other.Candidate.SubmissionId != leader.Candidate.SubmissionId
            && other.CompanyKey == leader.CompanyKey);

        return sameEmployer
            ? new EmailMatch(EmailMatchOutcome.Ambiguous, Match: null, ranked)
            : new EmailMatch(EmailMatchOutcome.Matched, ranked[0], ranked);
    }

    /// <summary>What fired for one candidate, before any weight is applied to it.</summary>
    private sealed record Evidence(
        EmailMatchCandidate Candidate,
        string CompanyKey,
        bool IsOrdinaryWord,
        bool InSenderName,
        bool InSubject,
        bool InMention,
        bool HostAgrees,
        bool VendorAgrees);

    /// <summary>
    /// One candidate's score, out of a hundred.
    /// </summary>
    /// <remarks>
    /// Whole points rather than doubles, so that equal evidence is equal and
    /// <see cref="AmbiguityMargin"/> is a comparison rather than an approximation. The company
    /// term is the maximum over the places the name was found rather than their sum: a name in
    /// the subject and in the sender's name is one employer named twice, not two facts. A maximum
    /// and not a precedence chain, because the strongest <i>place</i> is not the strongest
    /// <i>evidence</i> once <see cref="SubjectOrdinaryPoints"/> is nothing - an ordinary word in
    /// both the subject and an extracted mention is worth the mention.
    /// </remarks>
    private static int Points(Evidence e, bool sharedHost, bool sharedVendor)
    {
        var company = Math.Max(
            e.InSenderName ? (e.IsOrdinaryWord ? SenderNameOrdinaryPoints : SenderNamePoints) : 0,
            Math.Max(
                e.InSubject ? (e.IsOrdinaryWord ? SubjectOrdinaryPoints : SubjectPoints) : 0,
                e.InMention ? (e.IsOrdinaryWord ? MentionOrdinaryPoints : MentionPoints) : 0));

        var host = e.HostAgrees ? (sharedHost ? SharedHostPoints : HostPoints) : 0;
        var vendor = e.VendorAgrees ? (sharedVendor ? SharedVendorPoints : VendorPoints) : 0;

        // The maximum and never the sum. A Greenhouse sender agreeing with a Greenhouse host and
        // with the Greenhouse vendor is one observation, and adding it to itself would let the
        // sending system alone clear the floor.
        return company + Math.Max(host, vendor);
    }

    private static IReadOnlyList<EmailMatchSignal> Signals(Evidence e, bool sharedHost, bool sharedVendor)
    {
        var signals = new List<EmailMatchSignal>(4);

        if (e.InSenderName)
        {
            signals.Add(EmailMatchSignal.CompanyInSenderName);
        }

        if (e.InSubject)
        {
            signals.Add(EmailMatchSignal.CompanyInSubject);
        }

        if (e.InMention)
        {
            signals.Add(EmailMatchSignal.CompanyInMention);
        }

        // Recorded only where it changed something. On a candidate the message never named, the
        // employer's name being an ordinary word is a fact about the employer and not a finding.
        if (e.IsOrdinaryWord && (e.InSenderName || e.InSubject || e.InMention))
        {
            signals.Add(EmailMatchSignal.CompanyIsOrdinaryWord);
        }

        if (e.HostAgrees)
        {
            signals.Add(EmailMatchSignal.SenderHostMatchesApplyHost);
        }

        if (e.VendorAgrees)
        {
            signals.Add(EmailMatchSignal.SenderVendorMatchesApplyVendor);
        }

        if ((e.HostAgrees && sharedHost) || (e.VendorAgrees && sharedVendor))
        {
            signals.Add(EmailMatchSignal.SenderEvidenceSharedWithOtherCandidates);
        }

        return signals;
    }

    /// <summary>Whether two domains name the same organisation.</summary>
    /// <remarks>
    /// Label-boundary containment in either direction, so <c>monzo.com</c> agrees with
    /// <c>jobs.monzo.com</c> and <c>careers.monzo.com</c> agrees with <c>monzo.com</c>, while
    /// <c>mail.greenhouse.io</c> and <c>boards.greenhouse.io</c> - siblings rather than one inside
    /// the other - do not. That last one is deliberate and not a gap: vendor agreement is the
    /// signal that covers it, weighted for what it actually says.
    /// </remarks>
    private static bool DomainsAgree(string sender, string host)
    {
        if (sender.Length == 0 || host.Length == 0)
        {
            return false;
        }

        var (shorter, longer) = sender.Length <= host.Length ? (sender, host) : (host, sender);

        return Registrable(shorter)
            && (shorter == longer || longer.EndsWith('.' + shorter, StringComparison.Ordinal));
    }

    /// <summary>Whether a domain names an organisation rather than a suffix everybody sits under.</summary>
    private static bool Registrable(string domain)
    {
        var labels = domain.Split('.');

        return labels.Length >= 2
            && labels.All(label => label.Length > 0)
            && !(labels.Length == 2 && PublicSuffixLabels.Contains(labels[0]));
    }

    /// <summary>
    /// A host, from something that ought to have been one.
    /// </summary>
    /// <remarks>
    /// A whole URL where a host was asked for would otherwise fail as a silent never-matches,
    /// which is the worst way for it to fail: nothing errors, the answer is simply an abstention
    /// every time. Trimmed rather than refused, because - unlike an address - a URL carries
    /// nothing that should not be here.
    /// </remarks>
    private static string Host(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var host = value.Trim().ToLowerInvariant();
        var scheme = host.IndexOf("://", StringComparison.Ordinal);

        if (scheme >= 0)
        {
            host = host[(scheme + 3)..];
        }

        host = host.Split('/')[0].Split(':')[0].Trim('.');

        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>A vendor name, or null where it names no particular vendor.</summary>
    private static string? Vendor(string? value)
        => string.IsNullOrWhiteSpace(value) || NonVendors.Contains(value.Trim())
            ? null
            : value.Trim().ToLowerInvariant();

    /// <summary>An employer's name as words, with the legal form dropped.</summary>
    private static string[] CompanyTokens(string? value)
    {
        var tokens = Tokenize(value);
        var named = tokens.Where(token => !LegalForms.Contains(token)).ToArray();

        return named.Length > 0 ? named : tokens;
    }

    /// <summary>Case-, punctuation- and whitespace-insensitive words, bounded.</summary>
    private static string[] Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var text = value.Length > MaxScannedCharacters ? value[..MaxScannedCharacters] : value;
        var folded = new char[text.Length];

        for (var i = 0; i < text.Length; i++)
        {
            folded[i] = char.IsLetterOrDigit(text[i]) ? char.ToLowerInvariant(text[i]) : ' ';
        }

        return new string(folded).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Whether <paramref name="needle"/> appears in <paramref name="haystack"/> as consecutive words.</summary>
    /// <remarks>
    /// Consecutive, so "Acme Robotics" is not found in "Acme is hiring for Robotics". A name split
    /// across a sentence is a coincidence, and coincidences are what this file exists to refuse.
    /// </remarks>
    private static bool Contains(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matched = true;

            for (var i = 0; i < needle.Length && matched; i++)
            {
                matched = string.Equals(haystack[start + i], needle[i], StringComparison.Ordinal);
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
