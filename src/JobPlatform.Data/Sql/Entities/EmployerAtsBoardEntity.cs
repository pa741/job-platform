using JobPlatform.Core.Applications;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// How a board came to be attached to an employer, and therefore what has to be true before a
/// link read off it may be used.
/// </summary>
/// <remarks>
/// <b>The two paths are not two spellings of one fact.</b> A <see cref="Learned"/> board is read
/// off a link the employer themselves published - <c>AtsBoardToken.FromUrl</c> over a
/// <c>JobUrlDirect</c> already held against one of their postings - so the claim that this board
/// is theirs was made by them, and 122 of the 309 link-less postings are reachable that way with
/// no request and no guess. A <see cref="Probed"/> board is a slug built from the employer's name
/// and tried against a vendor, and a 200 there proves only that <i>somebody</i> owns that token:
/// "Dex", "Kernel", "Fin" and "Orbital" are all real boards belonging to a company, not
/// necessarily the one on the advert.
///
/// <b><see cref="Probed"/> is zero, and the direction of that choice is the argument.</b> A row
/// whose discovery nobody set - a backfill, a deserialiser that saw no member, a tool written
/// against the column before there was a writer for it - then reads as the <i>weaker</i> claim,
/// and the cost of that mistake is a recovered link nobody used. The opposite numbering costs an
/// application sent to a stranger under somebody's real name, because <see cref="Learned"/> is
/// the one path that owes no confirmation at all. Of the two mistakes available this is the
/// recoverable one, which is the same argument <see cref="AtsBoardConfidence.Unconfirmed"/> makes
/// for its own zero.
///
/// <b>Nobody may renumber these, and the reason is the opposite of <c>ApplyUrlSource</c>'s.</b>
/// That enum may be renumbered freely because its values are persisted nowhere and derived on
/// read. These are stored in a column, so a renumber does not break a build, fail a test or need
/// a migration - it silently reinterprets every row already written, turning boards somebody
/// guessed at into boards the employer published. A new member takes the next free number.
///
/// <b>It is deliberately not the test for whether a board may be used.</b> That is
/// <see cref="EmployerAtsBoardEntity.ConfirmedAtUtc"/>, a column whose null cannot be manufactured
/// by an enum nobody set. See the remarks there.
///
/// <b>It lives in Data rather than in Core, unlike every other vocabulary in this feature.</b>
/// Core's types answer questions about boards and listings themselves - what a token is, whether
/// a name agrees - and none of them can ask how a stored row came to exist, because Core has no
/// stored rows. This value is a fact about the provenance of a database row, read by the pass
/// that decides which employers still need a request. If a rule in Core ever branches on it, move
/// it there the way <c>ApplyUrlSource</c> moved, and for the same reason.
/// </remarks>
public enum AtsBoardDiscovery
{
    /// <summary>
    /// Built from the employer's name and tried against a vendor. An inference, and useless until
    /// <see cref="EmployerAtsBoardEntity.ConfirmedAtUtc"/> is stamped.
    /// </summary>
    Probed = 0,

    /// <summary>
    /// Read off a link the employer published on one of their own postings.
    /// </summary>
    /// <remarks>
    /// The strong path, and the one that owes no confirmation: the token was not guessed, it was
    /// lifted out of a URL the board carrying the advert published as this employer's apply link.
    /// <c>AtsBoardCandidates.Confirm</c> says the same thing from the other side - a Lever board
    /// can never be confirmed by name, because Lever's public feed publishes none, so Lever
    /// employers are reachable only through this path.
    /// </remarks>
    Learned = 1,
}

/// <summary>
/// One employer's board on one applicant tracking system: the whole identity Core defines, how it
/// was found, and how far it has been checked.
/// </summary>
/// <remarks>
/// <b>Read once per employer per pass, not once per posting.</b> Cloudflare's Greenhouse board
/// answers 333 jobs in a single request, so asking it per posting would be 333 requests for the
/// same bytes from an API that exists to serve that vendor's customers and their applicants. This
/// table is what makes the pass employer-shaped: the boards are read, each is fetched at most
/// once, and the listings are matched afterwards against every posting of that employer.
///
/// <b>All three parts of the identity are stored, because a board is not identified by fewer.</b>
/// <see cref="Vendor"/> and <see cref="Token"/> are the obvious two. <see cref="Region"/> is the
/// one that gets dropped, and dropping it fails silently rather than loudly: Lever's
/// data-residency tenants are published from a separate API host, so a European board stored
/// without its region is asked of the ordinary endpoint, answers nothing, and reads as "this
/// employer is not on Lever" - indistinguishable from the employer genuinely being absent, so no
/// count moves and nothing is reported. The two namespaces are independent, so the same token in
/// the other one may belong to a different company entirely. See <see cref="AtsBoardRegion"/>,
/// whose remarks are the source of this one.
///
/// <b>The row is the persisted half of <c>AtsBoard</c> and nothing more.</b> No URL, no host and
/// no template, for the reason Core refuses to hold one: a column in this schema holding an https
/// address is one small edit away from something in this repository fetching it, and the endpoint
/// table belongs to the layer that owns the request. Vendor, token and region are exactly what
/// <c>new AtsBoard(vendor, token, region)</c> takes, which is what
/// <c>EmployerAtsBoardSchemaTests</c> asserts by rebuilding one out of a stored row.
///
/// <b>Nothing on this table implies a credential.</b> The five vendors here publish these boards
/// to job seekers, unauthenticated and documented, which is the whole reason this feature exists
/// rather than the authenticated LinkedIn route - see <c>mcp_handoff.md</c> 3.2 and 3.2a. There
/// is no column for a cookie, a session or an account, and adding one would put the feature back
/// on the wrong side of a decision that has a legal record behind it.
///
/// <b>What it deliberately does not record.</b> A probe that answered 404 writes nothing: this
/// table holds boards that exist, and a row per token that did not answer would be a second,
/// larger table whose only reader is a request budget. Bounding repeat probes of a name that
/// never resolves stays the probe path's own problem, which is where the bound already lives -
/// <c>AtsBoardCandidates.MaxCandidates</c> sizes it exactly. Nor is the name the board reported
/// for itself stored: <c>AtsBoardCandidates.Confirm</c> reads that at the moment of the fetch and
/// the answer is <see cref="ConfirmedAtUtc"/>, and a stored copy of the input to a decision
/// already taken is a field free to disagree with the decision.
/// </remarks>
public sealed class EmployerAtsBoardEntity
{
    /// <summary>
    /// How wide a column holding a board token has to be.
    /// </summary>
    /// <remarks>
    /// <b>The number is Core's, and the constant is here only because Core's is private.</b>
    /// <c>AtsBoardToken.IsToken</c> accepts a slug up to 100 characters, so that is the width at
    /// which nothing this repository is willing to call a token can be truncated on the way in.
    /// <b>A truncated token is worse than a refused one</b>: it is still a slug, still shaped like
    /// a board, and it names the board of whoever registered the shorter name - the collision
    /// every refusal in <c>AtsBoardToken</c> is written to avoid, reintroduced by a column width.
    /// <c>AtsBoardCandidates.MaximumLength</c> is 64 and is deliberately not this number: that one
    /// bounds what is worth <i>probing</i>, where this has to hold anything that can be
    /// <i>learned</i> from a link an employer published.
    ///
    /// The two are pinned together by a test rather than by a reference, because there is nothing
    /// to reference - <c>Every_token_Core_accepts_fits_the_column_it_is_stored_in</c> fails if
    /// Core's bound is ever raised past this one.
    /// </remarks>
    public const int MaxTokenLength = 100;

    public int Id { get; set; }

    /// <summary>
    /// The employer, as <c>Companies.Id</c> rather than as the name printed on the advert.
    /// </summary>
    /// <remarks>
    /// <b>The folding is the feature.</b> <c>Companies.CompanyKey</c> already folds "Contoso",
    /// "Contoso Ltd" and "Contoso Limited" into one row, and that is exactly the folding this
    /// table needs: an employer publishes one board while their postings spell their name three
    /// ways across three sites, so a board hung off <c>JobPostings.Company</c> would be learned
    /// under one spelling and invisible to every posting carrying the other two. The same
    /// arithmetic is already written down for the <c>topCompanies</c> chart, where the unfolded
    /// name splits one employer's demand across three lines and makes "who is hiring most" wrong.
    ///
    /// <b>What happens to a posting whose employer is not in that table: nothing, deliberately.</b>
    /// <c>JobPostings.CompanyId</c> is null wherever <c>CompanyNormalizer.Key</c> produced no key -
    /// an advert with no employer name, or a spelling that folds to nothing - and on any row not
    /// re-ingested since that column landed. Those postings join to no board, so they stay exactly
    /// where they were: no board, no request, no recovered link - and no wrong link either, which
    /// is the half that matters. The remedy is re-ingestion, which stamps <c>CompanyRef</c> on
    /// every posting whose name normalises, and it is a remedy rather than a workaround.
    ///
    /// <b>The workaround - a second, nullable <c>Company</c> string here for the unfolded case -
    /// was considered and is refused twice over.</b> It would put a nullable column in the
    /// identity index, where SQL Server treats two NULLs as equal and SQLite treats them as
    /// distinct, so the uniqueness this table rests on would be one rule in production and a
    /// different one in the tests - the rule <c>ConfigureFormAnswers</c> and
    /// <c>ConfigureCvLibrary</c> already refuse to break. And it would attach boards under an
    /// unfolded name, which is the failure this column exists to prevent, for exactly the
    /// employers where the evidence is thinnest.
    ///
    /// <b>No navigation property, on purpose.</b> <c>Companies.Description</c> is the employer
    /// blurb and is unbounded - deduplicating it out of every posting row is most of why that
    /// table exists - so a navigation here is one <c>Include</c> away from dragging a paragraph
    /// per employer into a pass that wanted three columns. The board read is by id and stays by
    /// id, the arrangement <c>Submissions.AwaitingQuestionId</c> and
    /// <c>Submissions.CvVariantId</c> settled on for the same reason.
    /// </remarks>
    public required int CompanyId { get; set; }

    /// <summary>Whose applicant tracking system publishes the board.</summary>
    /// <remarks>
    /// Required, because <c>AtsVendor.Unknown</c> is zero and a board with no vendor cannot be
    /// fetched, confirmed or reasoned about - <c>AtsBoard</c>'s own constructor refuses it, and a
    /// row that can express what the type cannot is a row that reaches code written on the
    /// assumption it could not exist. Only the five vendors <c>AtsBoardToken.ServesPublicBoard</c>
    /// names can honestly appear here; that is not enforced by a check constraint, because such a
    /// constraint rots the day a sixth board joins the list and the writer already has to build an
    /// <c>AtsBoard</c>, which refuses the other nine.
    /// </remarks>
    public required AtsVendor Vendor { get; set; }

    /// <summary>
    /// The employer's identifier within that vendor, spelled exactly as the vendor spells it.
    /// </summary>
    /// <remarks>
    /// <b>Case is preserved, and must stay preserved.</b> SmartRecruiters keys its listings on a
    /// case-sensitive company id - <c>BlueOptima</c>, verified live - so a lower-cased token
    /// resolves to nothing, which is a silent false negative rather than an error. That is Core's
    /// decision and this column only has to avoid undoing it.
    ///
    /// The consequence lands on the identity index: Azure SQL compares under a case-insensitive
    /// collation and SQLite under <c>BINARY</c>, so two spellings of one board are one row in
    /// production and two in the tests. <b>That difference costs a duplicate fetch and never a
    /// wrong answer</b>, which is why it is tolerated here and is not tolerated in
    /// <c>CvVariants</c>, where the fold was moved into Core precisely because the index there
    /// had to mean the same thing on both engines. Nothing may "fix" this by folding the token:
    /// the fold would be the false negative.
    /// </remarks>
    public required string Token { get; set; }

    /// <summary>
    /// Which of the vendor's endpoints the board is on. Part of the identity, not a decoration.
    /// </summary>
    /// <remarks>
    /// Not <c>required</c>, unlike the two above, because <c>AtsBoardRegion.Default</c> is a real
    /// answer rather than an absent one: all but a handful of boards are on the vendor's ordinary
    /// host, and a board whose link said nothing about a region genuinely is there. That is the
    /// zero Core chose, and the argument for it is Core's own.
    /// </remarks>
    public AtsBoardRegion Region { get; set; }

    /// <summary>How this board was attached to this employer. See <see cref="AtsBoardDiscovery"/>.</summary>
    /// <remarks>
    /// Required, so a writer has to say which path made the row: the two paths are written by
    /// different code, and a row that does not say cannot be graded by anything downstream. The
    /// zero still leans the safe way for rows no writer touched, and both halves of that are
    /// argued on the enum.
    /// </remarks>
    public required AtsBoardDiscovery Discovery { get; set; }

    /// <summary>When this board was first attached to this employer.</summary>
    /// <remarks>
    /// Every row has one, which is the point: a probed board that has never been confirmed and
    /// never been fetched carries no other date, and "when did we start believing this" is the
    /// first question asked about a board that turns out to belong to somebody else.
    /// </remarks>
    public DateTimeOffset DiscoveredAtUtc { get; set; }

    /// <summary>
    /// When the board was confirmed to be this employer's, and null while it has not been.
    /// </summary>
    /// <remarks>
    /// <b>This is the trust test, and it is a timestamp rather than a flag or an enum member on
    /// purpose.</b> A probed token that was never confirmed must never produce a link, and the
    /// characteristic way that rule fails is a value nobody set reading as permission - a default
    /// enum member, a bool that is false for both "not confirmed yet" and "confirmed as somebody
    /// else's", a column added ahead of its writer. A null timestamp cannot be manufactured by any
    /// of those, and it cannot be mistaken for a confirmation by a reader who has forgotten the
    /// rule.
    ///
    /// <b>A <see cref="AtsBoardDiscovery.Learned"/> board is stamped here the moment it is
    /// learned, and that is not a loophole.</b> The confirmation for a learned board is the link
    /// the employer published: the token was lifted out of a URL their own board carried as the
    /// apply link for one of their postings, so the evidence exists and its date is the date it
    /// was read. Which makes the usable-board test one clause - <c>ConfirmedAtUtc != null</c> -
    /// rather than one clause per discovery path, and a reader who forgets that learned boards
    /// are exempt cannot get it wrong in the direction that matters.
    ///
    /// <b>An unconfirmed row is still worth storing</b>, which is why this is nullable rather than
    /// the row being withheld until it confirms. A token that answered 200 and named a different
    /// company is a request already spent, and remembering it is how the next pass avoids spending
    /// it again on an API that is doing us a favour by answering at all.
    /// </remarks>
    public DateTimeOffset? ConfirmedAtUtc { get; set; }

    /// <summary>
    /// When the board's listings were last read, and null where they never have been.
    /// </summary>
    /// <remarks>
    /// What bounds the pass to one fetch per employer: a board already fetched this pass is not
    /// fetched again, and a board fetched recently enough need not be. <b>Distinct from
    /// <see cref="ConfirmedAtUtc"/> because the two nulls mean different things and want opposite
    /// work</b> - "never fetched" is an employer still to be asked about, "never confirmed" is a
    /// board that may not be used whatever it answers. This schema has already paid once for
    /// letting two nulls share one representation, which is the fault
    /// <c>JobPostings.OffsiteApply</c> exists to undo.
    /// </remarks>
    public DateTimeOffset? LastFetchedUtc { get; set; }
}
