using JobPlatform.Core.Applications;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// What reading an employer's own careers page established about which board they publish on.
/// </summary>
/// <remarks>
/// <b>Five answers rather than a board and a null, for the reason <see cref="AtsBoardRead"/> has
/// three.</b> A null would have to serve as "their page names no board", "their page names two and
/// we will not choose", "the string we hold is not an address anybody could fetch" and "we asked
/// and nothing came back" - and those want four different things done about them. The one this
/// system has already paid for is the last pair: "no apply URL" meant both "the board hosts it" and
/// "nobody opened the detail page" until a second column showed they were different facts.
///
/// <see cref="CareersPageOutcome.NotAnAddress"/> is evidence about <i>our own data</i> - the URL a
/// scraper lifted off an advert is junk, or is a job board's profile page for the company rather
/// than the company's own site - and no request was made, which is a different cost from every
/// other answer here. <see cref="CareersPageOutcome.NamedNothing"/> is evidence about <i>the
/// employer</i>: their site carries no board this can read, which is the ordinary answer and is
/// final until they change it. <see cref="CareersPageOutcome.NamedSeveral"/> is evidence about
/// <i>the page</i>, and it is an abstention rather than a failure - see
/// <c>CareersPageReader</c>. <see cref="CareersPageOutcome.Unavailable"/> is evidence about
/// <i>the pass</i>: nothing was learned and nothing is claimed.
///
/// <b>Built through the factories rather than by property initialisers</b>, so an outcome carrying
/// a board it should not have, or naming one with no board attached, cannot be expressed at all.
/// That is <see cref="AtsBoardRead"/>'s rule and <c>AtsListingMatch</c>'s before it: a constraint
/// enforced by there being no way to write the alternative outlives one enforced by everybody
/// remembering it.
/// </remarks>
public sealed record CareersPageRead
{
    private CareersPageRead(CareersPageOutcome outcome, AtsBoard? board, bool requested)
    {
        Outcome = outcome;
        Board = board;
        Requested = requested;
    }

    /// <summary>The stored URL is not something this may fetch. No request was made.</summary>
    /// <remarks>
    /// Blank, not a URL, a <c>mailto:</c>, a scheme no browser opens, a host that is not a host -
    /// and one case that is a perfectly good address and still refused: an aggregator's page about
    /// the company. A board link found on <c>uk.whatjobs.com</c> or on a LinkedIn company page
    /// belongs to whichever employer that page happens to be listing, which on a search page is not
    /// one employer at all. <c>AtsVendorDetector</c> already knows every aggregator this system has
    /// met, so the refusal is one call rather than a second list to keep in step.
    /// </remarks>
    public static CareersPageRead NotAnAddress { get; } =
        new(CareersPageOutcome.NotAnAddress, null, requested: false);

    /// <summary>The page was read and carries no board token. Final, not a fault.</summary>
    /// <remarks>
    /// The commonest answer worth having, and it includes the case that looks most like a near
    /// miss: a page carrying <c>gh_jid</c> links and nothing else proves the employer runs
    /// Greenhouse and names no board at all, because an embed under the employer's own domain puts
    /// the job id in the query and leaves the token nowhere in the URL. <c>AtsBoardToken.FromUrl</c>
    /// says so in its own remarks and answers null; guessing past that is how one employer's
    /// postings get attached to another employer's board.
    /// </remarks>
    public static CareersPageRead NamedNothing { get; } =
        new(CareersPageOutcome.NamedNothing, null, requested: true);

    /// <summary>The page names more than one board, and this declines to choose between them.</summary>
    /// <remarks>
    /// <b>An abstention, on the same argument <c>AtsListingMatcher</c> makes for its own.</b> A
    /// careers page that links to two employers' boards - a parent and a subsidiary, an agency and
    /// its client, a jobs page that lists a portfolio - offers nothing in the markup that says
    /// which one is the employer on the advert, and choosing the first or the nearest would dress a
    /// coin toss as arithmetic. What an abstention costs is an employer left exactly where they
    /// were; what a wrong choice costs is an application sent to a company nobody applied to.
    /// </remarks>
    public static CareersPageRead NamedSeveral { get; } =
        new(CareersPageOutcome.NamedSeveral, null, requested: true);

    /// <summary>Nothing was learned. Ask again another day; claim nothing about the employer.</summary>
    /// <remarks>
    /// A timeout, a refused connection, a 4xx or 5xx, a body that is not a document, or a page
    /// larger than the pass is willing to read. <b>A truncated page is here rather than in
    /// <see cref="NamedNothing"/></b>, and that is the same rule the board client keeps: reporting
    /// part of a page as the whole of it turns "we did not read far enough" into "this employer has
    /// no board", which is a conclusion that gets stored.
    /// </remarks>
    public static CareersPageRead Unavailable { get; } =
        new(CareersPageOutcome.Unavailable, null, requested: true);

    /// <summary>What was established. Read this before reading anything else.</summary>
    public CareersPageOutcome Outcome { get; }

    /// <summary>
    /// The one board the page named, and null on every other outcome.
    /// </summary>
    /// <remarks>
    /// <b>It is a candidate rather than a permission.</b> The token was published rather than
    /// guessed at, which is what makes this path stronger than a probe, but whose page it was is
    /// still an inference - so the caller owes it a confirmation before anything may be fetched
    /// through it, exactly as a probed token owes one. <c>AtsBoardDiscovery.CareersPage</c> carries
    /// that argument in full.
    /// </remarks>
    public AtsBoard? Board { get; }

    /// <summary>
    /// Whether a request was actually made for this answer.
    /// </summary>
    /// <remarks>
    /// <b>Reported because the pass's summary has to say what it cost somebody else</b>, and two of
    /// the answers here cost nothing: a URL refused before it was fetched, and a stored URL that is
    /// itself a board address and so names the token with no page to read. Folding those into the
    /// count of pages read would make a log line saying "twenty-five careers pages read" true of a
    /// pass that made ten requests.
    /// </remarks>
    public bool Requested { get; }

    /// <summary>The page named exactly one board, and this is it.</summary>
    /// <param name="board">The board the page published.</param>
    /// <param name="requested">
    /// False where the answer came out of the stored URL itself rather than out of a page.
    /// </param>
    public static CareersPageRead For(AtsBoard board, bool requested = true)
    {
        ArgumentNullException.ThrowIfNull(board);

        return new CareersPageRead(CareersPageOutcome.NamedABoard, board, requested);
    }
}

/// <summary>Which of the five answers a careers-page read came back with.</summary>
/// <remarks>
/// Numbered from one, like <see cref="AtsBoardReadOutcome"/> and <c>AtsListingMatchOutcome</c>: a
/// stored or defaulted zero must never be able to read as a decision somebody made. Nothing
/// compares two of these, so the numbering is fixed only so a member added later cannot renumber
/// the others.
/// </remarks>
public enum CareersPageOutcome
{
    /// <summary>The page named exactly one board. A candidate, not yet a permission.</summary>
    NamedABoard = 1,

    /// <summary>The page was read and names no board. The employer is the thing established.</summary>
    NamedNothing = 2,

    /// <summary>The page names more than one, and choosing between them would be a coin toss.</summary>
    NamedSeveral = 3,

    /// <summary>The stored URL is not one this may fetch. No request was made.</summary>
    NotAnAddress = 4,

    /// <summary>Nothing was learned. Nothing is claimed about the employer.</summary>
    Unavailable = 5,
}
