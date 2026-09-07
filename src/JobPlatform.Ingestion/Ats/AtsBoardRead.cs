using JobPlatform.Core.Applications;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// What asking an employer's applicant tracking system for its board actually established.
/// </summary>
/// <remarks>
/// <b>Three answers rather than a list and a null, because two of them are facts about different
/// things and this repository has already paid for letting such a pair share one representation.</b>
/// "No apply URL" once meant either "the board hosts the application" or "nobody opened the detail
/// page", and that was written down as indistinguishable until a second column showed it was not.
/// The same trap is here: an empty list would have to serve as "this employer has no open
/// vacancies", "this token belongs to nobody" and "the vendor did not answer", and the three want
/// opposite work.
///
/// <see cref="AtsBoardReadOutcome.NotABoard"/> is evidence about the <i>token</i> - stop probing
/// it, and never attach a link to it. <see cref="AtsBoardReadOutcome.Unavailable"/> is evidence
/// about the <i>pass</i> - nothing was learned, ask again next time, and the recovery count falling
/// has a reason. <see cref="AtsBoardReadOutcome.Read"/> with no listings is evidence about the
/// <i>employer</i> - this is their board and it is empty today, which is a real and unremarkable
/// state for a company that has just closed its vacancies.
///
/// <b>Built through the factories rather than by property initialisers</b>, for the reason
/// <c>AtsListingMatch</c> is: a <see cref="AtsBoardReadOutcome.NotABoard"/> carrying listings, or a
/// <see cref="AtsBoardReadOutcome.Read"/> carrying none because nobody set them, cannot be
/// expressed at all. A rule enforced by there being no way to write the alternative outlives one
/// enforced by everybody remembering it.
/// </remarks>
public sealed record AtsBoardRead
{
    private AtsBoardRead(
        AtsBoardReadOutcome outcome,
        IReadOnlyList<AtsListing> listings,
        string? boardName)
    {
        Outcome = outcome;
        Listings = listings;
        BoardName = boardName;
    }

    /// <summary>
    /// The vendor says no board is published under this token.
    /// </summary>
    /// <remarks>
    /// <b>The ordinary answer to a probe, and emphatically not an error.</b> A slug built from an
    /// employer's name is a guess; a 404 is that guess being answered, and it is how the guess is
    /// meant to fail. Logged at debug and counted, never raised - the measurement behind the probe
    /// path has 99 of 120 employers landing here, so anything that treats this as a fault turns the
    /// pass into a log nobody reads.
    /// </remarks>
    public static AtsBoardRead NotABoard { get; } = new(AtsBoardReadOutcome.NotABoard, [], null);

    /// <summary>
    /// The vendor did not answer, or answered something this cannot read.
    /// </summary>
    /// <remarks>
    /// It covers a timeout, a refused connection, a 5xx, a rate limit, a body that is not the
    /// documented shape, and a board this reader has no endpoint for. <b>Nothing about the
    /// employer is claimed</b>: in particular this is not "the token is wrong", because reading it
    /// that way would stop a real employer being probed again over one bad afternoon at a vendor.
    /// </remarks>
    public static AtsBoardRead Unavailable { get; } = new(AtsBoardReadOutcome.Unavailable, [], null);

    /// <summary>What was established. Read this before reading anything else.</summary>
    public AtsBoardReadOutcome Outcome { get; }

    /// <summary>
    /// Everything the board publishes that can be applied through, in the order the vendor
    /// returned it. Empty on the other two outcomes.
    /// </summary>
    /// <remarks>
    /// <b>The whole board and never a subset chosen here.</b> <c>AtsListingMatcher</c> asks for it
    /// that way and says why: two listings excluded before they arrive are two listings it cannot
    /// abstain between, and the abstention is the thing standing between a recovered link and an
    /// application sent to the vacancy next to the right one. So nothing is filtered by title, by
    /// place, by department or by how recently it was posted.
    ///
    /// <b>The one exclusion is a listing with no title or no apply URL</b>, and it changes no
    /// outcome: <c>AtsListingMatcher</c> already skips both - a blank apply URL by an explicit
    /// <c>continue</c>, a blank title by folding to nothing - so dropping them here leaves every
    /// match, every abstention and every refusal exactly where it was, and makes this count mean
    /// "listings you could actually apply through".
    ///
    /// The order is the vendor's own, because any ordering imposed here would be a ranking of a set
    /// this layer has no business ranking.
    /// </remarks>
    public IReadOnlyList<AtsListing> Listings { get; }

    /// <summary>
    /// The name the board reports for itself, where the verified endpoint publishes one.
    /// </summary>
    /// <remarks>
    /// <b>This is the evidence <c>AtsBoardCandidates.Confirm</c> runs on, and it is the reason this
    /// field exists on a type whose job is otherwise listings.</b> A probed token that answers 200
    /// has proved only that <i>somebody</i> owns that slug - "Dex", "Kernel", "Fin" and "Orbital"
    /// are all real boards - and the name is what separates that somebody from the employer on the
    /// advert.
    ///
    /// <b>Null is common and is not a failure.</b> Of the four verified endpoints only
    /// SmartRecruiters names the company, so a Greenhouse, Ashby or Lever board read here is
    /// <c>Unconfirmed</c> on names alone - which <c>AtsBoardCandidates</c> already records for Lever
    /// and which is a real cost of the probe path rather than a gap in this file. The learned path,
    /// where the token came off a link the employer themselves published, owes no confirmation and
    /// covers 122 of the 309 link-less postings on its own.
    /// </remarks>
    public string? BoardName { get; }

    /// <summary>The board answered, with these listings and optionally its own name.</summary>
    public static AtsBoardRead For(IReadOnlyList<AtsListing> listings, string? boardName = null)
    {
        ArgumentNullException.ThrowIfNull(listings);

        return new AtsBoardRead(
            AtsBoardReadOutcome.Read,
            listings,
            string.IsNullOrWhiteSpace(boardName) ? null : boardName.Trim());
    }
}

/// <summary>Which of the three answers a board read came back with.</summary>
/// <remarks>
/// Numbered from one, like <c>AtsListingMatchOutcome</c> and <c>AtsMatchConfidence</c>: a stored or
/// defaulted zero must never be able to read as a decision that was made. The strength ordering
/// carries no meaning here - nothing compares two of these - so the numbering is fixed only so a
/// member inserted later cannot renumber the others.
/// </remarks>
public enum AtsBoardReadOutcome
{
    /// <summary>The board answered. It may still list nothing, which is a real state.</summary>
    Read = 1,

    /// <summary>No board is published under that token. The token is the thing to re-check.</summary>
    NotABoard = 2,

    /// <summary>Nothing was learned. Ask again; claim nothing about the employer meanwhile.</summary>
    Unavailable = 3,
}
