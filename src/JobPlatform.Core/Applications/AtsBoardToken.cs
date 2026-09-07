using System.Diagnostics.CodeAnalysis;

namespace JobPlatform.Core.Applications;

/// <summary>
/// Which of a vendor's endpoints an employer's board is published from.
/// </summary>
/// <remarks>
/// <b>A board token is not unique across a vendor's regions, and that is the whole reason this
/// field exists.</b> Lever's data-residency customers are served from <c>jobs.eu.lever.co</c>
/// rather than <c>jobs.lever.co</c>, and their listings are published by a separate API host.
/// Asking the ordinary one for such a token answers nothing, which reads as "this employer is not
/// on Lever" and is indistinguishable from the employer genuinely not being there - so the whole
/// recovery quietly stops working for one class of employer with no count that moves. The two
/// namespaces are also independent: nothing says a slug held in one is held by the same company
/// in the other, so guessing the wrong one is a way to attach one employer's postings to
/// another's board, which is precisely the failure <see cref="AtsBoardToken.FromUrl"/> answers
/// null to avoid everywhere else.
///
/// <b>The region is read off the link and never inferred from the employer.</b> A company with a
/// London address is not on the European endpoint - data residency is a contract with the vendor,
/// invisible from here - so the only honest source is the host the employer themselves published.
/// Where a link says nothing, <see cref="Default"/> is the answer and it is correct: all but a
/// handful of boards are there.
///
/// Zero is <see cref="Default"/> so an unset value reads as the ordinary endpoint, which is the
/// same argument <see cref="AtsVendor.Unknown"/> makes for its own zero - with the difference
/// that here the default is a real answer rather than an absent one, because a board with no
/// region label genuinely is on the vendor's main host.
/// </remarks>
public enum AtsBoardRegion
{
    /// <summary>The vendor's ordinary endpoint, which is where all but a handful of boards live.</summary>
    Default = 0,

    /// <summary>
    /// The vendor's European endpoint, named by an <c>eu</c> label on the board host.
    /// </summary>
    /// <remarks>
    /// Verified in the corpus for Lever, whose <c>jobs.eu.lever.co</c> already appears in
    /// <c>AtsVendorDetectorTests</c>. The label is read the same way for every vendor in the
    /// table rather than special-cased for the one that ships it today, because the alternative
    /// is a rule that has to be remembered when the next vendor offers data residency - and the
    /// cost of being early is a host that never occurs, which no URL will ever match.
    /// </remarks>
    Europe = 1,
}

/// <summary>
/// An employer's public board on one applicant tracking system: enough to name it, never enough
/// to fetch it.
/// </summary>
/// <remarks>
/// <b>It names a board and deliberately does not say where to get it.</b> There is no URL here,
/// no template and no host, because Core is pure: a type in this project holding an https address
/// is one small edit away from something in this project fetching it, and the assertability of
/// <c>MatchScorer</c> and <c>SubmissionState</c> rests on that never happening. The Ingestion
/// layer owns the endpoint, and the mapping from these three values to a request is the one place
/// a vendor's API shape is written down.
///
/// <b>Constructing one is a claim that the board can be read with no account</b>, so the vendor is
/// checked rather than accepted - see <see cref="AtsBoardToken.ServesPublicBoard"/>. Workday is
/// the case that motivates it: 69 employers in the corpus, more than any other named vendor, and
/// no clean public listing to read. A caller handed an <c>AtsBoard</c> for one would have to know
/// by hearsay not to try, and a type that cannot represent the unreadable case cannot be misread.
///
/// <b>The token's case is preserved, and that is a decision rather than an omission.</b> Some of
/// these vendors key their listings on a case-sensitive company id - SmartRecruiters writes
/// <c>Acme</c> - so folding case would turn a valid token into one that resolves to nothing,
/// which is a silent false negative. Keeping it means two spellings of one employer's board look
/// like two boards, which costs a duplicate request and never a wrong answer. Of the two mistakes
/// available, that is the recoverable one.
///
/// The properties are get-only, so <c>with</c> cannot put a value past the constructor. That is
/// the same argument <c>AiCallRecord.Create</c> makes for being the only constructor: a bound
/// that can be skipped is documentation.
/// </remarks>
public sealed record AtsBoard
{
    /// <summary>
    /// Names a board, refusing a vendor with no public listing and a token that is not a slug.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The vendor publishes no public, unauthenticated board listing.
    /// </exception>
    /// <exception cref="ArgumentException">The token is blank or is not a slug.</exception>
    public AtsBoard(AtsVendor vendor, string token, AtsBoardRegion region = AtsBoardRegion.Default)
    {
        if (!AtsBoardToken.ServesPublicBoard(vendor))
        {
            throw new ArgumentOutOfRangeException(
                nameof(vendor),
                vendor,
                "This vendor publishes no public, unauthenticated board listing.");
        }

        if (!AtsBoardToken.IsToken(token))
        {
            throw new ArgumentException(
                "A board token is a slug, not a path or a phrase.",
                nameof(token));
        }

        Vendor = vendor;
        Token = token;
        Region = region;
    }

    /// <summary>Whose applicant tracking system publishes the board.</summary>
    public AtsVendor Vendor { get; }

    /// <summary>The employer's identifier within that vendor, spelled as the vendor spells it.</summary>
    public string Token { get; }

    /// <summary>Which of that vendor's endpoints the board is on.</summary>
    public AtsBoardRegion Region { get; }
}

/// <summary>
/// Recovers an employer's board identity from a link they have already published.
/// </summary>
/// <remarks>
/// <b>This exists because four in five good matches name an employer and give an agent nowhere to
/// go.</b> Measured 2026-09-07: of 382 applyable postings, 309 carry no employer apply link and
/// every one of those is LinkedIn, which stopped publishing apply URLs to signed-out clients. The
/// answer is to ask the employer's own applicant tracking system, which serves a documented,
/// unauthenticated board listing meant to be read by job seekers - and 122 of those 309 are at an
/// employer whose board is already knowable from a direct link held against another posting. That
/// join is what this function is for. See <c>mcp_handoff.md</c> 3.2 and 3.2a for why the
/// authenticated route is not on the table: no credential, no cookie and no session appears
/// anywhere in this feature, and nothing added here may take one.
///
/// <b>Pure and network-free, like <see cref="AtsVendorDetector"/> and for the same reason.</b> It
/// runs over every row that holds a link, so it may not fetch, follow a redirect or resolve a
/// shortener. Everything it knows is in the string, which is what makes its answers assertable
/// exactly against real corpus URLs.
///
/// <b>The vendor is asked of <see cref="AtsVendorDetector"/> rather than decided again here, and
/// that is worth more than the duplication it saves.</b> A second host table would need its own
/// copy of the aggregator list, and the entry missing from the copy would be the one that
/// mattered: <c>uk.whatjobs.com/r?u=https%3A%2F%2Fboards.greenhouse.io%2Facme</c> carries a
/// vendor's board host inside a query string, and reading a token out of it would attribute an
/// aggregator's re-listing to an employer. Asking the detector means every aggregator it learns
/// about later is refused here without this file changing.
///
/// <b>The hard half is knowing when it cannot answer.</b> Two shapes prove the vendor and hide the
/// employer: <c>grnh.se/{code}</c> is a shortener whose target is knowable only by following it,
/// and <c>careers.employer.com/jobs?gh_jid={id}</c> is Greenhouse embedded under the employer's
/// own domain, where the board token appears nowhere in the URL. Both answer null. A guess at
/// either would attach one employer's postings to another employer's board - and the resulting
/// apply link looks entirely ordinary, so nothing downstream would notice it was wrong. The probe
/// path deals with those, and a probed token is confirmed against a posting before it is trusted,
/// exactly as a cross-board link is marked as the inference it is.
/// </remarks>
public static class AtsBoardToken
{
    /// <summary>
    /// A sanity bound, not a specification.
    /// </summary>
    /// <remarks>
    /// No vendor here documents a maximum, and Workable's form has to fit inside a DNS label at
    /// 63. The bound sits well above that and exists only so a pathological path segment cannot
    /// become a "token" some later caller pastes into a request. A real board token longer than
    /// this would be left to the probe path rather than answered wrongly, which is the direction
    /// every refusal in this file leans.
    /// </remarks>
    private const int MaxTokenLength = 100;

    /// <summary>Where in a URL a vendor puts the employer's board token.</summary>
    private enum TokenPlace
    {
        /// <summary>The first path segment, under a named board subdomain.</summary>
        FirstPathSegment,

        /// <summary>The subdomain itself, as Workable's per-account host does.</summary>
        Subdomain,
    }

    /// <summary>
    /// The board host shapes, one row per way a vendor writes a link.
    /// </summary>
    /// <remarks>
    /// <b>Every shape here occurs in the live corpus, and nothing is here for symmetry.</b> That
    /// is the rule <see cref="AtsVendorDetector"/>'s own tables follow, and it costs more here: a
    /// marketing page under the same domain - <c>www.greenhouse.io</c>, or
    /// <c>jobs.workable.com</c>, which is Workable's own search site rather than an employer's
    /// board - has a first path segment that looks exactly like a token. So the subdomain is
    /// matched against a named list rather than accepted, and a vendor host this table does not
    /// name answers null.
    ///
    /// <b>Order matters for Workable alone.</b> Its two forms are <c>apply.workable.com/{token}</c>
    /// and <c>{token}.workable.com</c>, and the second matches any single label - including
    /// <c>apply</c>. The path form is listed first so <c>apply.workable.com/acme/j/AB12CD34</c>
    /// yields <c>acme</c>: the first row whose host shape fits decides, and a row that fits but
    /// yields no usable token answers null rather than falling through to a looser one.
    ///
    /// Workday, iCIMS, Taleo, SuccessFactors, Teamtailor, BambooHR and Pinpoint are absent
    /// deliberately - see <see cref="ServesPublicBoard"/>.
    /// </remarks>
    private static readonly (AtsVendor Vendor, string Domain, string[] Sites, TokenPlace Place)[] Boards =
    [
        // boards.greenhouse.io/{token}/jobs/{id}. job-boards.greenhouse.io is the newer host and
        // both are live; greenhouse.io itself, and www under it, are the marketing site.
        (AtsVendor.Greenhouse, "greenhouse.io", ["boards", "job-boards"], TokenPlace.FirstPathSegment),

        // jobs.ashbyhq.com/{token}/{uuid}
        (AtsVendor.Ashby, "ashbyhq.com", ["jobs"], TokenPlace.FirstPathSegment),

        // jobs.lever.co/{token}/{uuid}, and jobs.eu.lever.co for a data-residency tenant.
        (AtsVendor.Lever, "lever.co", ["jobs"], TokenPlace.FirstPathSegment),

        // apply.workable.com/{token}/j/{code}. Listed before the subdomain form, see above.
        (AtsVendor.Workable, "workable.com", ["apply"], TokenPlace.FirstPathSegment),

        // {token}.workable.com/j/{code}. The account's own host, so the token is the label and
        // the Sites list is unused.
        (AtsVendor.Workable, "workable.com", [], TokenPlace.Subdomain),

        // jobs.smartrecruiters.com/{token}/{id}
        (AtsVendor.SmartRecruiters, "smartrecruiters.com", ["jobs"], TokenPlace.FirstPathSegment),
    ];

    /// <summary>
    /// Segments and labels that are a vendor's own URL furniture rather than an employer.
    /// </summary>
    /// <remarks>
    /// <b>Without this the commonest wrong answer is a token named after the page.</b>
    /// <c>boards.greenhouse.io/jobs/4012345</c>, <c>apply.workable.com/j/AB12CD34</c> and
    /// <c>jobs.smartrecruiters.com/oneclick-ui/company/Acme/publication/1</c> all put a word that
    /// is not an employer where a token usually sits, and every one of them passes the slug shape
    /// check. A board called <c>jobs</c> or <c>j</c> would then be asked for its vacancies, and
    /// the answer - from whoever does hold that slug - would be published as this employer's.
    ///
    /// <b>It is deliberately over-broad, because the two mistakes do not cost the same.</b>
    /// Refusing a real employer whose token happens to be one of these words loses a link the
    /// probe path can still recover from their name; accepting a word that is not an employer
    /// produces an apply link that looks entirely ordinary and belongs to somebody else. So
    /// generic careers-site vocabulary is listed alongside the segments these five vendors emit.
    ///
    /// <b>It is a rule about a position in a URL and not about the token</b>, which is why
    /// <see cref="IsToken"/> does not consult it: <see cref="FromUrl"/> is guessing which segment
    /// is the employer and has to be timid, while a probed token is confirmed against a posting
    /// before it is trusted and may be anything a vendor accepts.
    /// </remarks>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "j", "o", "p", "jobs", "job", "careers", "career", "apply", "application", "applications",
        "embed", "widget", "search", "login", "signin", "signup", "account", "about", "company",
        "companies", "board", "boards", "posting", "postings", "positions", "openings",
        "vacancies", "oneclick-ui", "static", "assets", "api", "www", "index", "home", "help",
        "support", "blog", "status", "resources",
    };

    /// <summary>
    /// The vendors whose board listings can be read without an account.
    /// </summary>
    /// <remarks>
    /// <b>Five, and the list is the reach of this feature rather than a fact about the vendor.</b>
    /// Greenhouse, Ashby, Lever, Workable and SmartRecruiters each serve a public, documented,
    /// unauthenticated listing that exists to be read by job seekers - verified live on
    /// 2026-09-07, where Greenhouse answered 200 with 333 jobs for one employer and carried the
    /// <c>absolute_url</c> LinkedIn had withheld. Workday is the notable absence, at 69 employers
    /// the largest single vendor in the corpus, and it stays out because it has no clean public
    /// board listing; iCIMS, Taleo and SuccessFactors are out for the same reason, which is the
    /// same reason <c>AtsVendors.RequiresAccount</c> already warns about three of them.
    ///
    /// <b>It lives here rather than on <c>AtsVendors</c> deliberately.</b> The predicates there
    /// are facts about a vendor's software that hold whatever this repository does; this one
    /// changes the day a sixth board joins <see cref="Boards"/>, and it belongs beside the table
    /// it has to agree with. One definition and two readers - this file's own refusal, and the
    /// probe path deciding which employers are worth a request - which is the arrangement
    /// <c>ParkReasonPolicy</c> settled on for the same reason.
    /// </remarks>
    public static bool ServesPublicBoard(AtsVendor vendor)
        => vendor is AtsVendor.Greenhouse
            or AtsVendor.Ashby
            or AtsVendor.Lever
            or AtsVendor.Workable
            or AtsVendor.SmartRecruiters;

    /// <summary>
    /// Whether a string is shaped like a board token at all.
    /// </summary>
    /// <remarks>
    /// <b>Shape only, and the reserved words are not consulted here on purpose</b> - see
    /// <see cref="Reserved"/>. Letters, digits, hyphen and underscore, because that is the
    /// alphabet all five vendors slug a company name into and one of them, Workable, has to fit
    /// the result into a DNS label. A dot is excluded: in a first path segment it means a file -
    /// <c>careers.html</c> - far more often than it means a company. A percent sign is excluded
    /// with everything else, so an escaped segment is refused rather than decoded; a token that
    /// needs an escape is not a token, and decoding one is how <c>..%2f</c> becomes a path.
    /// </remarks>
    public static bool IsToken([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTokenLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The employer's board where a direct apply link names one, and null wherever it does not.
    /// </summary>
    /// <remarks>
    /// <b>Null is a real answer here, and the most common one.</b> It covers a string that is not
    /// a URL, a link that leads to another job board, a vendor that publishes no readable
    /// listing, a shortener, an embed under the employer's own domain, and a board host whose
    /// path holds the vendor's furniture instead of a company. Every one of those hands the
    /// posting to the probe path, which builds a slug from the employer's name and confirms it
    /// against a posting before trusting it. <b>Nothing is guessed here</b>, because a guess at
    /// this layer is indistinguishable downstream from a fact.
    ///
    /// <b>It never throws.</b> The input is a string a scraper lifted off somebody's page: blank,
    /// truncated, a <c>mailto:</c>, or not a URL in any sense. This runs across a corpus of them
    /// in one pass, where a single exception loses every other row with it - the same argument
    /// <see cref="AtsVendorDetector.Detect"/> makes, and the reason the parse below is a
    /// <c>TryCreate</c> rather than a constructor.
    /// </remarks>
    public static AtsBoard? FromUrl(string? url)
    {
        var vendor = AtsVendorDetector.Detect(url);

        if (!ServesPublicBoard(vendor) || !TryReadWebAddress(url, out var address))
        {
            return null;
        }

        var host = address.Host.TrimEnd('.').ToLowerInvariant();

        foreach (var (candidate, domain, sites, place) in Boards)
        {
            if (candidate != vendor || !TrySplitHost(host, domain, out var labels, out var region))
            {
                continue;
            }

            // One label and nothing else. None of these vendors puts a board two levels down, and
            // a deeper host under their domain is something other than an employer's board.
            if (labels.Length != 1)
            {
                continue;
            }

            if (place == TokenPlace.Subdomain)
            {
                return Board(vendor, labels[0], region);
            }

            if (!sites.Contains(labels[0], StringComparer.Ordinal))
            {
                continue;
            }

            // The host shape fits, so this row answers. Falling through to a looser row is how
            // apply.workable.com/... would end up reported as the board of an employer called
            // "apply".
            return Board(vendor, FirstPathSegment(address), region);
        }

        return null;
    }

    /// <summary>A board, or null where the segment cannot honestly be read as an employer.</summary>
    private static AtsBoard? Board(AtsVendor vendor, string? token, AtsBoardRegion region)
        => IsToken(token) && !Reserved.Contains(token)
            ? new AtsBoard(vendor, token, region)
            : null;

    /// <summary>
    /// Splits a host into the labels sitting in front of a vendor's registrable domain.
    /// </summary>
    /// <remarks>
    /// <b>The label boundary is the check, and it has to hold on both sides.</b>
    /// <c>Contains("lever.co")</c> is true of <c>clever.com</c> and <c>EndsWith</c> alone is true
    /// of <c>mylever.co</c>, so the character in front of the domain must be a dot; and anybody
    /// may register <c>greenhouse.io.example.com</c>, so the domain must end the host rather than
    /// appear in it. That is the same reading <see cref="AtsVendorDetector"/> applies, restated
    /// here because that one answers "which vendor" and this one has to hand back the labels.
    ///
    /// <b>An <c>eu</c> label sitting directly on the domain is read as the region and removed</b>,
    /// so <c>jobs.eu.lever.co</c> and <c>jobs.lever.co</c> reach the rules as one shape. One rule
    /// for every vendor rather than a special case for the one that ships it today: a region a
    /// vendor does not offer is a host that never occurs and costs nothing, where forgetting the
    /// rule for the next vendor costs a silent false negative.
    /// </remarks>
    private static bool TrySplitHost(
        string host,
        string domain,
        out string[] labels,
        out AtsBoardRegion region)
    {
        labels = [];
        region = AtsBoardRegion.Default;

        if (host.Equals(domain, StringComparison.Ordinal))
        {
            return true;
        }

        if (host.Length <= domain.Length + 1
            || host[host.Length - domain.Length - 1] != '.'
            || !host.EndsWith(domain, StringComparison.Ordinal))
        {
            return false;
        }

        labels = host[..(host.Length - domain.Length - 1)].Split('.');

        if (labels[^1].Equals("eu", StringComparison.Ordinal))
        {
            labels = labels[..^1];
            region = AtsBoardRegion.Europe;
        }

        return true;
    }

    /// <summary>The first non-empty path segment, or null where there is none.</summary>
    /// <remarks>
    /// Read off <see cref="Uri.AbsolutePath"/>, which leaves percent escapes intact, so an escaped
    /// segment fails <see cref="IsToken"/> instead of being decoded into one. A board root with a
    /// trailing slash and one without are the same URL here; a board root with no segment at all
    /// answers null, which is the shortener's situation spelled differently - the link proves the
    /// vendor and names no employer.
    /// </remarks>
    private static string? FirstPathSegment(Uri address)
    {
        foreach (var segment in address.AbsolutePath.Split('/'))
        {
            if (segment.Length > 0)
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>The parsed address, or false where there is nothing to read a path out of.</summary>
    /// <remarks>
    /// Lenient about the scheme for the reason <see cref="AtsVendorDetector"/> is: boards publish
    /// apply links without one, and <c>boards.greenhouse.io/acme/jobs/1</c> names an employer as
    /// clearly as the same string with <c>https://</c> in front of it. It needs no host validation
    /// of its own, because it runs only after the detector has already named a board vendor from
    /// that host; what is left is to obtain a <see cref="Uri"/> this code parsed itself, since a
    /// path cannot be read from one it did not.
    /// </remarks>
    private static bool TryReadWebAddress(string? url, out Uri address)
    {
        address = null!;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var candidate = url.Trim();

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            && !Uri.TryCreate("https://" + candidate, UriKind.Absolute, out parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        address = parsed;

        return true;
    }
}
