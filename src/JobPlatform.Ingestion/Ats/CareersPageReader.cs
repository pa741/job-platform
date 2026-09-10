using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Reads an employer's own careers page and answers which applicant tracking board it names.
/// </summary>
/// <remarks>
/// <b>Why this exists: the two vendors that matter most hide the token from every URL a board ever
/// publishes.</b> Greenhouse and Ashby embed their forms under the employer's own domain - the live
/// corpus carries <c>https://careers.withwaymo.com/jobs?gh_jid=7852098</c> - so
/// <c>AtsBoardToken.FromUrl</c> answers null for that shape and says why. The employer's own site
/// is where the missing half is: the same page that embeds the form usually also links to the board
/// it is embedding, and that link names the token outright. Measured on the corpus this feature was
/// built against, the two paths that existed reached 122 of 309 link-less postings by learning from
/// a published link and 21 of 120 employers by guessing a slug from their name; this is the third
/// source, and it needs no guess.
///
/// <b>Every other host this repository fetches is one of five documented board APIs. This one is
/// an arbitrary third-party URL, and every bound here follows from that.</b> A vendor's board
/// endpoint exists to be read by job seekers and is a service to their own customers; an employer's
/// marketing site has agreed to nothing at all. So: <b>one request per employer per pass</b> and
/// never one per posting, a timeout of its own that is stricter than the board timeout, a hard byte
/// ceiling, a redirect limit inherited from the shared handler, a descriptive User-Agent that names
/// this repository rather than imitating a browser, and http or https only - a <c>file:</c>, a
/// <c>mailto:</c> or anything else is refused before a socket is opened, and the handler will not
/// follow a redirect out of those two schemes either, so the rule holds for the whole chain rather
/// than for its first hop. The cap on employers per run lives one layer up, in
/// <c>ApplyLinkRecoveryOptions.CareersPagesPerPass</c>, because it is a property of the pass rather
/// than of one read.
///
/// <b>It goes through the shared <c>ats-boards</c> client, and that is not for tidiness.</b> That
/// client's handler is where "no credential, no cookie, no session" stops being a promise and
/// becomes a fact: <c>UseCookies</c> is off so a <c>Set-Cookie</c> cannot come back on the next
/// request, <c>Credentials</c> is null so the host's managed identity is never offered, and
/// <c>PreAuthenticate</c> is off so nothing is volunteered ahead of a challenge. A reader that
/// newed up its own <c>HttpClient</c> to send an <c>Accept: text/html</c> would have dropped all
/// three, and nothing would have failed. See <see cref="AtsBoardRegistration"/>, and
/// <c>mcp_handoff.md</c> 3.2a for why the line is where it is.
///
/// <b>It never throws</b>, including on cancellation, for <c>AtsBoardClient</c>'s reason: a pass
/// reads a page per employer in one invocation, and one exception escaping loses every employer
/// after it. Everything - a refused connection, a body that is not a document, a page too large to
/// read, a cancelled pass - is one of the five values on <see cref="CareersPageRead"/>.
///
/// <b>It extracts nothing itself.</b> Every URL the page carries is handed to
/// <c>AtsBoardToken.FromUrl</c> and <c>AtsVendorDetector.Detect</c>, which already know the board
/// hosts, the embed parameters, the aggregator list, the reserved path segments and the label
/// boundary. A second extractor here would be a second copy of all of that, and the entry missing
/// from the copy would be the one that mattered - which is the argument <c>AtsBoardToken</c> itself
/// makes for asking the detector rather than keeping its own host table.
/// </remarks>
public sealed partial class CareersPageReader
{
    /// <summary>
    /// How many addresses one page is scanned for before the rest are ignored.
    /// </summary>
    /// <remarks>
    /// A sanity bound rather than a specification, like <c>AtsBoardToken</c>'s token length. The
    /// byte ceiling already bounds the work; this bounds the pathological case that ceiling does
    /// not - a page of minified script in which <c>//</c> occurs thousands of times - so that a
    /// careers page cannot turn one employer into a second of URL parsing. A board link on a page
    /// that carries two thousand addresses before it is a link this declines to find, which costs a
    /// recovery and never a wrong one.
    /// </remarks>
    private const int MaxAddresses = 2_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AtsBoardOptions _options;
    private readonly ILogger<CareersPageReader> _logger;

    /// <summary>Creates a reader over the shared, cookie-less, credential-less client.</summary>
    public CareersPageReader(
        IHttpClientFactory httpClientFactory,
        IOptions<AtsBoardOptions> options,
        ILogger<CareersPageReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Reads one employer's careers page, and answers rather than throws whatever happens.
    /// </summary>
    /// <remarks>
    /// <b>Two answers are given before any request is made, and both are worth having.</b> A URL
    /// that is not an address this may fetch costs nothing and is reported as a fact about our own
    /// data rather than about the employer. And a stored URL that is <i>already</i> a board address
    /// names the token with no page to read at all - an employer whose <c>company_url</c> is their
    /// Greenhouse board is uncommon and free, and spending a request to be told what the string in
    /// hand already said would be the "asking per posting" mistake in miniature.
    /// </remarks>
    /// <param name="url">The employer's own address, as scraped. Anything at all, including null.</param>
    /// <param name="ct">Cancellation. A cancelled read is <c>Unavailable</c>, never an exception.</param>
    public async Task<CareersPageRead> ReadAsync(string? url, CancellationToken ct = default)
    {
        if (!TryReadFetchableAddress(url, out var address))
        {
            return CareersPageRead.NotAnAddress;
        }

        // The string in hand is the answer. Free, and the same rule the pass runs on everywhere
        // else: never spend a request to learn something already held.
        if (AtsBoardToken.FromUrl(url) is { } published)
        {
            return CareersPageRead.For(published, requested: false);
        }

        var page = await FetchAsync(address, ct);

        return page is null ? CareersPageRead.Unavailable : Read(page, address);
    }

    /// <summary>
    /// Whether a scraped string is an address this pass is willing to open, and what it parses to.
    /// </summary>
    /// <remarks>
    /// <b>Three refusals, and the third is the one that is not obvious.</b> The scheme must be http
    /// or https, because a <c>mailto:</c> parses perfectly well into a host and there is no page at
    /// the end of it. The host must look like a host, because a bare word prefixed with a scheme
    /// parses happily too and "unemployed" is not a careers site. And an aggregator is refused
    /// outright: <c>company_url</c> is very often a job board's own page about the company -
    /// LinkedIn's, Indeed's - and a board link found on somebody else's listing page belongs to
    /// whichever employer that page is about, which on a search page is not one employer at all.
    ///
    /// <b>The first two are asked of <c>AtsVendorDetector</c> rather than decided again here.</b>
    /// It answers <c>Unknown</c> for exactly the strings that are not web addresses - blank, a
    /// non-web scheme, a host with no dot or with whitespace in it - so one call covers both, and
    /// the aggregator list it already maintains covers the third. The <see cref="Uri"/> is parsed
    /// separately only because the detector does not hand one back and a request needs one; the
    /// scheme-less form is accepted for the same reason the detector accepts it, since boards
    /// publish addresses without one.
    /// </remarks>
    private static bool TryReadFetchableAddress(string? url, out Uri address)
    {
        address = null!;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var vendor = AtsVendorDetector.Detect(url);

        if (vendor is AtsVendor.Unknown or AtsVendor.Aggregator)
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

    /// <summary>
    /// One GET, bounded four ways, and never allowed to become an exception.
    /// </summary>
    /// <remarks>
    /// <b>The deadline is a linked token rather than the client's own timeout, because the client's
    /// does not cover what is being read here.</b> <c>HttpClient.Timeout</c> stops applying once
    /// the response headers are in hand, and the headers are exactly where this read stops waiting
    /// and starts streaming - so a host that answers instantly and then drips a body forever would
    /// sit inside the client's timeout indefinitely. The linked source bounds the whole read,
    /// headers and body together.
    ///
    /// <b><see cref="AtsBoardOptions.CareersPageTimeout"/> is separate from the board timeout and
    /// stricter, because the two are waiting on different kinds of promise.</b> A board API is a
    /// vendor serving a document they publish for this purpose; a careers page is somebody's site
    /// which may be rendering, redirecting or advertising, and this pass is not entitled to wait
    /// for it. Nothing is lost by giving up: the employer is stamped as asked and the pass moves to
    /// the next one.
    ///
    /// <b>The size ceiling is enforced by reading a fixed buffer rather than by trusting a
    /// header.</b> <c>Content-Length</c> is absent on a chunked reply and is a claim in any case.
    /// Overrunning it answers <c>Unavailable</c> and never "no board here": a page read half way is
    /// a page whose board link may be in the half nobody read, and reporting that as an employer
    /// with no board is a conclusion that gets stored. It is the rule <c>AtsBoardClient</c> keeps
    /// for a truncated board, applied to a truncated document.
    ///
    /// <b>Only a 200 with a document body is read.</b> A 404 is not "this employer has no board" -
    /// no page was seen at all - so unlike a probed board token, where a 404 is the answer, here
    /// every status but 200 is <c>Unavailable</c>. A 401 or 403 is never answered by adding a
    /// credential; it is answered by not reading that site, which is the decision behind this whole
    /// feature rather than one this file may revisit.
    /// </remarks>
    /// <returns>The page's text, or null where nothing was learned.</returns>
    private async Task<string?> FetchAsync(Uri address, CancellationToken ct)
    {
        var timeout = _options.CareersPageTimeout > TimeSpan.Zero
            ? _options.CareersPageTimeout
            : TimeSpan.FromSeconds(1);

        // Clamped rather than trusted, exactly as the shared client's own two numbers are: these
        // are read while a request is being made, inside a never-throw net, so a mistyped setting
        // would otherwise turn into every employer answering "unavailable" with a log blaming their
        // hosts. A floor is the legible failure.
        var ceiling = (int)Math.Clamp(_options.MaxCareersPageBytes, 1024, 32L * 1024 * 1024);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(timeout);

        var buffer = ArrayPool<byte>.Shared.Rent(ceiling + 1);

        try
        {
            using var client = _httpClientFactory.CreateClient(AtsBoardRegistration.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);

            // Set on the request rather than on the client, which is the whole reason this is a
            // request header at all: the shared client asks for JSON because four board APIs answer
            // JSON, and a header present on the message wins over the client's default. Nothing
            // else is added to this request and nothing may be - see the class remarks.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                _logger.LogDebug(
                    "Careers page {Host} answered {Status}, so nothing was learned about that "
                    + "employer's board. Not a fault and not an answer about them.",
                    address.Host,
                    (int)response.StatusCode);

                return null;
            }

            if (!IsDocument(response.Content.Headers.ContentType))
            {
                _logger.LogDebug(
                    "Careers page {Host} answered {MediaType}, which is not a document to read "
                    + "addresses out of.",
                    address.Host,
                    response.Content.Headers.ContentType?.MediaType);

                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);

            var read = 0;

            while (read < buffer.Length)
            {
                var got = await stream.ReadAsync(buffer.AsMemory(read), deadline.Token);

                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            if (read > ceiling)
            {
                _logger.LogInformation(
                    "Careers page {Host} is larger than the {Ceiling} bytes this pass reads, so it "
                    + "is unread rather than reported as carrying no board.",
                    address.Host,
                    ceiling);

                return null;
            }

            // Decoded as UTF-8 whatever the page declares, and mis-decoding one is harmless here:
            // a byte sequence that is not valid UTF-8 becomes a replacement character, and every
            // character of every URL this is scanning for is ASCII. Honouring a charset would be a
            // second encoding table to keep, to change no answer.
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // A host being down, a socket refused, a TLS handshake nobody completed, a body that
            // stopped arriving, or the read outliving its deadline - which arrives as a
            // cancellation rather than as a timeout. Information rather than a warning: it is
            // somebody else's site having a bad afternoon, and the only consequence is that the
            // pass recovers fewer links.
            _logger.LogInformation(
                exception,
                "Careers page {Host} was not read; the pass recovers fewer links.",
                address.Host);

            return null;
        }
        catch (Exception exception)
        {
            // The net, and it is deliberately wider than the list above. This runs over a whole
            // batch of employers in one invocation, so an exception nobody anticipated - from a URI
            // the handler refuses to redirect to, from a decompressor meeting a malformed body -
            // must cost this employer rather than every employer after them. Warned rather than
            // logged quietly, because an unanticipated failure here is a bug in this file.
            _logger.LogWarning(
                exception,
                "Careers page {Host} failed in a way this reader does not anticipate; the pass "
                + "continues.",
                address.Host);

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The one board a page names, or the reason it names none.
    /// </summary>
    /// <remarks>
    /// <b>Two distinct boards is an abstention and so is two distinct vendors, and the second rule
    /// is the one that earns its place.</b> A page carrying <c>gh_jid</c> links proves the employer
    /// runs Greenhouse and names no Greenhouse token; if the only token on that page is a Lever one
    /// - a link to a partner's board, a job an agency is advertising - then taking it would attach
    /// this employer to a board their own page says is not the one they use. Reading the embed
    /// markers is what makes that visible, and <c>AtsVendorDetector</c> already reads them as whole
    /// parameter names, so <c>utm_content=gh_jid</c> is not Greenhouse.
    ///
    /// <b>A vendor with no public board is not counted as a second vendor.</b> An employer running
    /// Workday for graduate roles and Greenhouse for engineering is ordinary, and Workday publishes
    /// no readable listing at all, so its presence says nothing about which board the recoverable
    /// half of their vacancies is on. <c>AtsBoardToken.ServesPublicBoard</c> is the same list this
    /// whole feature's reach is defined by, asked rather than copied.
    ///
    /// <b>The same board named a hundred times is one board.</b> <c>AtsBoard</c> is a record with
    /// value equality over vendor, token and region, so the set does the whole of the counting -
    /// which is what <c>AtsBoardReader</c> already relies on to fetch one board once.
    /// </remarks>
    private CareersPageRead Read(string page, Uri address)
    {
        var boards = new HashSet<AtsBoard>();
        var vendors = new HashSet<AtsVendor>();

        foreach (var candidate in Addresses(page))
        {
            var vendor = AtsVendorDetector.Detect(candidate);

            if (AtsBoardToken.ServesPublicBoard(vendor))
            {
                vendors.Add(vendor);
            }

            if (AtsBoardToken.FromUrl(candidate) is { } board)
            {
                boards.Add(board);
            }
        }

        if (boards.Count == 0)
        {
            _logger.LogDebug(
                "Careers page {Host} names no board token. {Vendors} vendor(s) are embedded on it, "
                + "which proves whose software takes the application and not where the board is.",
                address.Host,
                vendors.Count);

            return CareersPageRead.NamedNothing;
        }

        if (boards.Count > 1 || vendors.Count > 1)
        {
            _logger.LogInformation(
                "Careers page {Host} names {Boards} board(s) across {Vendors} vendor(s), so no "
                + "token is taken from it. An abstention costs the employer nothing; choosing "
                + "between them would be a coin toss with somebody's application on it.",
                address.Host,
                boards.Count,
                vendors.Count);

            return CareersPageRead.NamedSeveral;
        }

        return CareersPageRead.For(boards.Single());
    }

    /// <summary>Every http, https or protocol-relative address the page's text contains.</summary>
    /// <remarks>
    /// <b>Scanned as text rather than parsed as HTML, deliberately.</b> A board link reaches a page
    /// as an <c>href</c>, as a <c>script src</c>, inside a JSON blob a framework rendered into the
    /// markup and inside a string a bundle builds a URL out of, so a parser walking anchors would
    /// find a strict subset of what this finds while adding a dependency that has to be kept safe
    /// against hostile markup. Nothing downstream trusts what comes out of here - every candidate
    /// goes through <c>AtsBoardToken.FromUrl</c>, which refuses anything that is not a board host
    /// with a slug-shaped segment under it - so the scan is allowed to be generous.
    ///
    /// <b>Trailing sentence punctuation is trimmed and nothing else is decoded.</b> A URL written
    /// in prose ends in a full stop that would otherwise become part of the first path segment and
    /// be refused; an <c>&amp;amp;</c> inside a query is left exactly as it is, because the token
    /// is read from the host and the path and no answer here depends on a query's spelling.
    ///
    /// The pattern cannot backtrack - one greedy character class with nothing after it - so it is
    /// linear in the page whatever the page is trying to be. It is enumerated into a list rather
    /// than yielded, because the allocation-free enumerator this uses cannot be held across a
    /// <c>yield</c>, and matching first is the cheaper half in any case: the bound below is on how
    /// many addresses are <i>examined</i>, and examining one is a parse where finding one is a scan.
    /// </remarks>
    private static List<string> Addresses(string page)
    {
        var addresses = new List<string>();

        foreach (var match in AddressPattern().EnumerateMatches(page))
        {
            if (addresses.Count >= MaxAddresses)
            {
                break;
            }

            var value = page.AsSpan(match.Index, match.Length).TrimEnd(".,;:!?'\"");

            // A protocol-relative address, with the slashes taken off rather than a scheme guessed
            // at: FromUrl and Detect both accept a bare host, which is the same leniency boards
            // publishing scheme-less apply links already need.
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                value = value[2..];
            }

            if (!value.IsEmpty)
            {
                addresses.Add(value.ToString());
            }
        }

        return addresses;
    }

    /// <summary>Whether the response body is a document rather than an image, a file or JSON.</summary>
    /// <remarks>
    /// A missing content type is read as a document rather than refused: some servers send none,
    /// and the cost of scanning bytes that turn out not to be text is a page that names no board,
    /// which is already an answer this handles. What is refused is a type that is positively
    /// something else - a PDF, an image, a download - because reading a megabyte of it to find no
    /// URLs is a megabyte of somebody else's bandwidth spent on a certainty.
    /// </remarks>
    private static bool IsDocument(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;

        return mediaType is null
            || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"(?:https?:)?//[^\s""'<>()\\\]}]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();
}
