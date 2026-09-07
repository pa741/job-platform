using System.Net;
using System.Text.Json;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Everything four board readers must not each get wrong separately.
/// </summary>
/// <remarks>
/// <b>This is the only place in this system that holds a URL for somebody else's API, and the
/// mechanics around that URL live here rather than in the four vendor files.</b> Four rules have to
/// hold on every request - a 404 is an ordinary answer, nothing throws into the caller, nothing
/// carries a credential, and no listing leaves without a title and a web address to apply at - and
/// written four times one of them would be missing from one of them. Which one, and in which
/// vendor, would be discovered by an application arriving at the wrong company.
///
/// <b>A 404 is an answer and not a fault, and the logging has to say so.</b> It is how a probed
/// slug fails, and the measurement the probe path is built on has 99 of 120 employers landing
/// there - so a warning per miss is several hundred warnings a pass, which is a log nobody reads,
/// which is the same as no log at all on the day something is genuinely wrong. It is logged at
/// debug, in the words "no board", and it is a counted value rather than an exception.
///
/// <b>The severity ladder is chosen by whose fault it is.</b> A 5xx, a timeout or a refused
/// connection is the vendor having a bad afternoon and is information: the recovery count falls and
/// the reason is on the record. A 400, 401, 403 or 429 is <i>us</i> being wrong - a malformed
/// request, an endpoint that has started wanting an account, or a rate limit we should not have
/// reached - and each is a warning, because each needs somebody to look. <b>A 401 or 403 is never
/// answered by adding a credential.</b> These boards are published to job seekers unauthenticated;
/// an endpoint that starts asking for an account is an endpoint this feature stops reading, which
/// is the decision recorded in <c>mcp_handoff.md</c> 3.2a and not one this file may revisit.
///
/// <b>Nothing is ever sent but a GET, an Accept and a User-Agent.</b> No Authorization header, no
/// cookie jar - the handler is built with <c>UseCookies</c> off in
/// <see cref="AtsBoardRegistration"/>, so a Set-Cookie a vendor sends cannot come back on the next
/// request and there is no session to accumulate. That is a property of the transport rather than
/// of anybody's discipline, which is the only version of this rule worth having.
/// </remarks>
public abstract class AtsBoardClient : IAtsBoardClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Creates a reader over the shared, cookie-less, credential-less client.</summary>
    protected AtsBoardClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        Logger = logger;
    }

    /// <summary>
    /// The vendor reader's own logger, shared with this base so both speak with one voice.
    /// </summary>
    /// <remarks>
    /// Exposed rather than duplicated in a subclass that needs one. Two loggers on one reader means
    /// two categories for one vendor, and a filter written against the obvious one silences half
    /// of what it was meant to catch.
    /// </remarks>
    protected ILogger Logger { get; }

    /// <inheritdoc />
    public abstract AtsVendor Vendor { get; }

    /// <summary>
    /// Reads the board, and answers rather than throws whatever happens.
    /// </summary>
    /// <remarks>
    /// <b>The catch-all is deliberate and is the whole contract of the interface.</b> A pass reads
    /// hundreds of boards in one invocation, so one exception escaping here loses every employer
    /// after it - the same argument <c>AtsVendorDetector.Detect</c> and
    /// <c>PostingCluster.Strength</c> both make for answering a value where an exhaustive rule
    /// would have thrown. A cancelled pass answers <see cref="AtsBoardRead.Unavailable"/> too:
    /// stopping is not a fact about the employer, and a caller that cancelled already knows it did.
    ///
    /// <b>The vendor mismatch above it is the one thing that does throw</b>, because it is not a
    /// vendor answer at all - it is this client wired to the wrong board, which
    /// <see cref="AtsBoardReader"/> cannot produce and no retry can fix. A value there would be a
    /// wiring bug reported as an employer who is not on Greenhouse.
    /// </remarks>
    public async Task<AtsBoardRead> ReadAsync(
        AtsBoard board,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);

        if (board.Vendor != Vendor)
        {
            throw new ArgumentException(
                $"A {Vendor} board reader was handed a {board.Vendor} board.",
                nameof(board));
        }

        try
        {
            return await ReadBoardAsync(board, cancellationToken);
        }
        catch (Exception exception)
        {
            Logger.LogInformation(
                exception,
                "ATS board read failed for {Vendor}/{Token}; the pass recovers fewer links.",
                Vendor,
                board.Token);

            return AtsBoardRead.Unavailable;
        }
    }

    /// <summary>The vendor's own request-and-map, run inside the net above.</summary>
    protected abstract Task<AtsBoardRead> ReadBoardAsync(
        AtsBoard board,
        CancellationToken cancellationToken);

    /// <summary>
    /// The whole board in one request, which is what three of the four vendors serve.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> parse result means the body was well-formed JSON in a shape this does not
    /// recognise, which is <see cref="AtsBoardRead.Unavailable"/> and never an empty board: an
    /// unreadable answer says nothing about whether the employer has vacancies.
    /// </remarks>
    protected async Task<AtsBoardRead> ReadOnceAsync(
        AtsBoard board,
        Uri? endpoint,
        Func<JsonElement, AtsBoardRead?> parse,
        CancellationToken cancellationToken)
    {
        var (outcome, value) = await FetchAsync(board, endpoint, parse, cancellationToken);

        return outcome switch
        {
            AtsBoardReadOutcome.Read => value ?? Unreadable(board),
            AtsBoardReadOutcome.NotABoard => AtsBoardRead.NotABoard,

            // The catch-all is the safe direction rather than a throw, for the reason
            // PostingCluster.Strength gives for its own: this runs across a whole pass, and an
            // exhaustive switch would lose every later employer to a member nobody had mapped.
            _ => AtsBoardRead.Unavailable,
        };
    }

    /// <summary>
    /// One GET, classified, parsed, and never allowed to become an exception.
    /// </summary>
    /// <remarks>
    /// <b>A null <paramref name="endpoint"/> is a board this reader has no address for</b> - today
    /// that is a region the endpoint table does not name, and Lever's European tenants are the live
    /// case. It answers <see cref="AtsBoardReadOutcome.Unavailable"/> with a warning rather than
    /// asking the ordinary host anyway, which is the trap <c>AtsBoardRegion</c> was added to close:
    /// the wrong host answers nothing, that reads as "this employer is not on Lever", and the whole
    /// recovery stops working for one class of employer with no count that moves.
    ///
    /// <b>The response is buffered rather than streamed</b>, so the size bound in
    /// <see cref="AtsBoardOptions.MaxResponseBytes"/> actually holds - a streamed parse has nothing
    /// to enforce it against a chunked reply that never ends. Overrunning it raises inside
    /// <c>HttpClient</c> and is caught here, which is the right outcome: a truncated board must
    /// never be reported as a complete one.
    /// </remarks>
    protected async Task<(AtsBoardReadOutcome Outcome, T? Value)> FetchAsync<T>(
        AtsBoard board,
        Uri? endpoint,
        Func<JsonElement, T?> parse,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(parse);

        if (endpoint is null)
        {
            Logger.LogWarning(
                "No {Vendor} endpoint is written down for the {Region} region, so board {Token} "
                + "cannot be read. Verify the host and add it to ATS-ENDPOINTS.md rather than "
                + "asking the ordinary one, which answers nothing and reads as an absent employer.",
                Vendor,
                board.Region,
                board.Token);

            return (AtsBoardReadOutcome.Unavailable, null);
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(AtsBoardRegistration.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            // Nothing is added to this request and nothing may be. The board is published to job
            // seekers; an Authorization header, a cookie or a session here would put the feature
            // back on the wrong side of the line mcp_handoff.md 3.2a draws.
            using var response = await client.SendAsync(request, cancellationToken);

            // Null means "this is a body worth parsing". Anything else is an answer in itself.
            var refusal = Classify(response.StatusCode, board);

            if (refusal is not null)
            {
                return (refusal.Value, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(body);

            return (AtsBoardReadOutcome.Read, parse(document.RootElement));
        }
        catch (JsonException exception)
        {
            Logger.LogWarning(
                exception,
                "{Vendor} board {Token} answered 200 with a body that is not JSON.",
                Vendor,
                board.Token);

            return (AtsBoardReadOutcome.Unavailable, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            // A vendor being down, a socket refused, or the request outliving its timeout - which
            // HttpClient reports as a cancellation rather than as a timeout, so the two arrive
            // here together and are the same fact either way: nothing was learned this pass.
            Logger.LogInformation(
                exception,
                "{Vendor} did not answer for board {Token}; the pass recovers fewer links.",
                Vendor,
                board.Token);

            return (AtsBoardReadOutcome.Unavailable, null);
        }
    }

    /// <summary>The board endpoint, or null where none is written down for that region.</summary>
    /// <remarks>
    /// <b>The token is escaped even though it cannot need escaping</b>, and the belt and braces are
    /// the point: <c>AtsBoardToken.IsToken</c> already refuses anything but letters, digits, hyphen
    /// and underscore, so this changes no real token - but this is the single line in the system
    /// where a token becomes part of somebody else's URL, and a guarantee held two files away is
    /// one a later refactor can remove without anything failing here.
    ///
    /// <b>A <see cref="FormattableString"/> rather than a string, so the path is composed under the
    /// invariant culture.</b> A paged endpoint interpolates an offset, and a number formatted by
    /// whatever culture the host happens to be running under is a query parameter a vendor answers
    /// unpredictably - the sort of defect that appears on one deployment and not on the machine it
    /// was written on.
    /// </remarks>
    protected static Uri? Endpoint(string? host, FormattableString pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);

        return host is null
            ? null
            : new Uri("https://" + host + FormattableString.Invariant(pathAndQuery));
    }

    /// <summary>The token, escaped, ready to be interpolated into a path.</summary>
    protected static string Escaped(string token) => Uri.EscapeDataString(token);

    /// <summary>A listing, or null where there is nothing to apply through.</summary>
    /// <remarks>
    /// <b>The apply URL is required to be an absolute http or https address, and that check is the
    /// one in this file with teeth.</b> This string comes out of somebody else's JSON and ends up
    /// in a browser with a form and a candidate's real name at the far end of it, so a
    /// <c>javascript:</c>, a <c>mailto:</c> or a relative fragment must not become an apply link.
    /// The host is deliberately <i>not</i> checked against the vendor's: Greenhouse's
    /// <c>absolute_url</c> legitimately points at the employer's own careers domain when the board
    /// is embedded there, which is the commonest shape in the corpus and the one worth recovering.
    ///
    /// A missing title is refused for the same reason <c>AtsListingMatcher</c> refuses a blank one:
    /// a title that folds to nothing agrees with every other title that folds to nothing.
    /// </remarks>
    protected static AtsListing? Listing(string? title, string? location, string? applyUrl)
        => title is null || applyUrl is null || !IsWebAddress(applyUrl)
            ? null
            : new AtsListing(title, location, applyUrl);

    /// <summary>A trimmed string property, or null where it is absent, null or blank.</summary>
    /// <remarks>
    /// Absent, JSON null and empty all answer null, because in somebody else's document those are
    /// three spellings of the same silence - the reading <c>AtsListing</c> already anticipates when
    /// it says its non-nullable fields are still checked before use.
    /// </remarks>
    protected static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>The first of these properties that carries text, in the order given.</summary>
    /// <remarks>
    /// <b>Order is the whole content of this helper.</b> Two of the four verified endpoints are
    /// recorded in <c>ATS-ENDPOINTS.md</c> by the container the apply URL sits in rather than by
    /// the field's name - "in <c>jobs[]</c>", "in <c>content[]</c>" - so each vendor names the
    /// fields it publishes, likeliest first, and a listing carrying none of them is dropped rather
    /// than guessed at. What must never be added is a field that is not an apply page:
    /// SmartRecruiters' <c>ref</c> is an <c>api.smartrecruiters.com</c> address, and a candidate
    /// sent there finds JSON rather than a form.
    /// </remarks>
    protected static string? FirstText(JsonElement element, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        foreach (var name in names)
        {
            var text = Text(element, name);

            if (text is not null)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>Whether a string is an absolute address a browser would open.</summary>
    private static bool IsWebAddress(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The outcome a status decides on its own, or null where the body is worth parsing.
    /// </summary>
    /// <remarks>
    /// <b>Only a 200 is a body.</b> A plain <c>IsSuccessStatusCode</c> would take a 204 or a 206 as
    /// one too, and parsing an empty or partial document as an empty board reports an employer as
    /// having closed every vacancy - the wrong half of the distinction
    /// <see cref="AtsBoardRead"/> exists to keep.
    /// </remarks>
    private AtsBoardReadOutcome? Classify(HttpStatusCode status, AtsBoard board)
    {
        if (status == HttpStatusCode.OK)
        {
            return null;
        }

        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Debug, and phrased as an answer rather than a failure. This is how a probed slug
            // fails and it is the commonest outcome the probe path has: 99 of 120 employers on
            // the 2026-09-07 measurement. A warning here is several hundred warnings a pass.
            Logger.LogDebug(
                "No {Vendor} board under {Token}. This is how a probe fails, not a fault.",
                Vendor,
                board.Token);

            return AtsBoardReadOutcome.NotABoard;
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Deliberately loud, and deliberately not actionable by adding a header. A board that
            // has started wanting an account is a board this feature stops reading.
            Logger.LogWarning(
                "{Vendor} answered {Status} for board {Token}. These listings are published "
                + "unauthenticated; no credential, cookie or session may be added to reach them.",
                Vendor,
                (int)status,
                board.Token);

            return AtsBoardReadOutcome.Unavailable;
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            Logger.LogWarning(
                "{Vendor} rate limited board {Token}. Lower AtsBoards:MaxConcurrentFetches; this "
                + "API serves that vendor's customers rather than us.",
                Vendor,
                board.Token);

            return AtsBoardReadOutcome.Unavailable;
        }

        if ((int)status >= 500)
        {
            Logger.LogInformation(
                "{Vendor} answered {Status} for board {Token}; the pass recovers fewer links.",
                Vendor,
                (int)status,
                board.Token);

            return AtsBoardReadOutcome.Unavailable;
        }

        Logger.LogWarning(
            "{Vendor} answered {Status} for board {Token}, which is this reader asking wrongly "
            + "rather than the vendor being down.",
            Vendor,
            (int)status,
            board.Token);

        return AtsBoardReadOutcome.Unavailable;
    }

    /// <summary>A 200 whose body is JSON but not the documented shape.</summary>
    private AtsBoardRead Unreadable(AtsBoard board)
    {
        Logger.LogWarning(
            "{Vendor} board {Token} answered in a shape this reader does not recognise. "
            + "Re-verify the endpoint in ATS-ENDPOINTS.md before changing the parse.",
            Vendor,
            board.Token);

        return AtsBoardRead.Unavailable;
    }
}
