namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// What one pass over somebody else's server is allowed to cost them.
/// </summary>
/// <remarks>
/// <b>Every value here is a courtesy to a vendor rather than a tuning knob for us</b>, which is why
/// they are settings and not constants: the answer to "we are being noisy" has to be a
/// configuration change rather than a deploy, and the answer to "we are being slow" must not be a
/// code edit somebody makes at speed. The same argument <c>ApplicationGenerationOptions</c> makes
/// for the number that is a bill, applied to the number that is somebody else's rate limiter.
///
/// <b>Every member defaults to something a clone can run unattended</b>, so a deployment that binds
/// nothing gets a bounded, polite pass rather than an unbounded one.
/// </remarks>
public sealed class AtsBoardOptions
{
    /// <summary>Configuration section: <c>AtsBoards</c>.</summary>
    public const string SectionName = "AtsBoards";

    /// <summary>
    /// How this identifies itself to the vendor.
    /// </summary>
    /// <remarks>
    /// <b>Descriptive, and it names a repository rather than a person or a browser.</b> A vendor
    /// noticing this traffic should be able to find out in one search what it is, that it reads
    /// only the board listings they publish for job seekers, and where to complain - which is
    /// worth more than the requests it might save us. Impersonating a browser is the alternative
    /// and it is the opposite of the position this feature is built on: nothing here is
    /// circumventing anything, so nothing here needs to look like something else.
    ///
    /// It is settings because a vendor may ask for it to change, and because a fork of this
    /// repository claiming to be this repository helps nobody.
    /// </remarks>
    public string UserAgent { get; set; } =
        "JobPlatform-ApplyLinkRecovery/1.0 (+https://github.com/pa741/job-platform)";

    /// <summary>
    /// How long one board request may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <b>Ten seconds, and the bound is on our side rather than theirs.</b> A board is one JSON
    /// document a vendor serves from a cache; a request still running after ten seconds is one
    /// that is not going to be answered usefully, and holding it open costs an invocation that is
    /// billed by the second on Flex Consumption and a slot from
    /// <see cref="MaxConcurrentFetches"/> that another employer could have used.
    ///
    /// A timeout answers <see cref="AtsBoardReadOutcome.Unavailable"/> and never
    /// <see cref="AtsBoardReadOutcome.NotABoard"/>: a slow vendor must not read as an employer who
    /// is not there, or one bad afternoon takes a real board out of the probe path for good.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many board requests may be in flight at once, across every vendor.
    /// </summary>
    /// <remarks>
    /// <b>Four, and it is deliberately low.</b> The bound protects the vendor, which is the same
    /// argument <c>AtsBoardCandidates.MaxCandidates</c> makes one layer up: these APIs exist to
    /// serve that ATS's customers and their applicants, and we are neither. A pass over 120
    /// unresolved employers at four candidate tokens each is already several hundred requests, and
    /// the difference between a courteous read and something a rate limiter is right to refuse is
    /// how many of them arrive at once.
    ///
    /// <b>It is global rather than per vendor, deliberately.</b> A per-vendor bound multiplies by
    /// however many vendors happen to be registered, so the number a reader sets would not be the
    /// number anybody is subjected to - and the day a fifth client lands, the load on the first
    /// four would not change while the load on the pass would. One number, one meaning.
    /// </remarks>
    public int MaxConcurrentFetches { get; set; } = 4;

    /// <summary>
    /// The largest board body that will be read at all.
    /// </summary>
    /// <remarks>
    /// <b>A guard on our memory, not on their catalogue.</b> Some of these endpoints return the
    /// full advert text per vacancy, so a large employer's board is megabytes, and this host runs
    /// on Flex Consumption instances sized for CSV ingest. Sixteen mebibytes is far above the
    /// largest board measured and far below anything that would trouble the instance.
    ///
    /// Overrunning it answers <see cref="AtsBoardReadOutcome.Unavailable"/>, which is the honest
    /// value: a truncated board is not an empty one, and reporting a partial catalogue as a
    /// complete one is how an abstention turns into a match on the vacancy next to the right one.
    /// </remarks>
    public long MaxResponseBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// How long one employer's careers page may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <b>Five seconds, and it is half <see cref="RequestTimeout"/> because the two are waiting on
    /// different kinds of promise.</b> A board endpoint is a vendor serving a document they publish
    /// for exactly this purpose, and waiting for it is reasonable. A careers page is an arbitrary
    /// third-party site that has agreed to nothing, may be rendering, redirecting or advertising,
    /// and is being read on the off chance it names a token - so the pass is not entitled to wait
    /// on it, and giving up costs one employer's stamp rather than anything else.
    ///
    /// <b>It bounds the whole read rather than the headers.</b> <c>HttpClient.Timeout</c> stops
    /// applying once the response headers arrive, which on this path is where the reading starts;
    /// <c>CareersPageReader</c> therefore enforces this with a linked cancellation source, or a
    /// host that answered instantly and then dripped a body would never be abandoned at all.
    ///
    /// Overrunning it answers <c>CareersPageOutcome.Unavailable</c> and never
    /// <c>CareersPageOutcome.NamedNothing</c>: a page nobody finished reading is not a page with no
    /// board on it.
    /// </remarks>
    public TimeSpan CareersPageTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The largest careers page that will be read at all.
    /// </summary>
    /// <remarks>
    /// <b>One mebibyte, sixteen times below <see cref="MaxResponseBytes"/>, and the ratio is the
    /// argument.</b> A board is an employer's whole vacancy list and some vendors put the full
    /// advert text in it, so megabytes there are the documented shape of the thing. A careers page
    /// is one document, and this is reading it for the addresses in its markup - so a page that
    /// does not fit is a page doing something other than describing an employer's vacancies, and
    /// buffering it would be an arbitrary host deciding how much of a Flex Consumption instance to
    /// occupy.
    ///
    /// <b>Overrunning it is <c>Unavailable</c> rather than "no board here"</b>, because the board
    /// link may be in the part nobody read. That is the rule <see cref="MaxResponseBytes"/> already
    /// keeps for a truncated board, and it matters more here: a truncated board reports an employer
    /// as having closed every vacancy, where this would report them as having no board at all and
    /// the pass would stop asking.
    /// </remarks>
    public long MaxCareersPageBytes { get; set; } = 1024L * 1024;
}
