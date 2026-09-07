using System.Text;
using System.Text.Json;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Reads a SmartRecruiters careers board.
/// </summary>
/// <remarks>
/// <b>The endpoint is <c>api.smartrecruiters.com/v1/companies/{token}/postings</c>, verified live
/// on 2026-09-07 against <c>BlueOptima</c>, which answered 10 postings.</b>
/// <c>ATS-ENDPOINTS.md</c> is the record. The token is case-sensitive here - SmartRecruiters writes
/// <c>BlueOptima</c> rather than <c>blueoptima</c> - which is exactly why <c>AtsBoard</c> preserves
/// a token's case rather than folding it, and why nothing in this file lower-cases one.
///
/// <b>It is the only one of the four that pages, and the only one that names the employer.</b> The
/// two facts are the whole reason this reader is longer than its neighbours.
///
/// <b>Paging is done properly rather than truncated, and the reason is not completeness for its own
/// sake.</b> A board read short is a board missing listings, and a missing listing is a missing
/// <i>contender</i>: an employer advertising one title twice produces an abstention when both are
/// seen and a confident match on whichever survived when one is not. That turns the one safeguard
/// standing between a recovery and an application sent to the vacancy next to the right one into a
/// coin toss decided by a page boundary. So a partial board is never reported as a whole one -
/// every failure after the first page, and the page bound itself, answer
/// <see cref="AtsBoardReadOutcome.Unavailable"/>.
///
/// <b>The company name is read from <c>content[].company.name</c></b>, and it is the only board
/// name any of the four verified endpoints publishes. That makes SmartRecruiters the one vendor
/// where <c>AtsBoardCandidates.Confirm</c> can do its job on a probed token from this reader alone -
/// which is worth knowing when deciding where a probe budget goes.
///
/// <b>The apply URL is recorded by its container - "in <c>content[]</c>" - so the field names are
/// read in order</b>, <c>applyUrl</c> then <c>postingUrl</c>. <c>ref</c> is deliberately not in that
/// list although it is present on every row: it is an <c>api.smartrecruiters.com</c> address, and a
/// candidate sent there finds JSON rather than a form.
/// </remarks>
public sealed class SmartRecruitersBoardClient : AtsBoardClient
{
    /// <summary>
    /// How many postings one request asks for.
    /// </summary>
    /// <remarks>
    /// A hundred is this endpoint's documented maximum, so it is also the fewest requests a large
    /// board can be read in - which is the courtesy that matters here, one request per hundred
    /// vacancies rather than one per vacancy.
    /// </remarks>
    private const int PageSize = 100;

    /// <summary>
    /// The most requests one board may cost, whatever it answers.
    /// </summary>
    /// <remarks>
    /// <b>A stop, not a truncation.</b> Twenty pages is two thousand vacancies - far above any
    /// board in this corpus - so in practice it is never reached, and its real job is to bound a
    /// vendor that answers a full page for ever because it is ignoring the offset. Reaching it
    /// answers <see cref="AtsBoardReadOutcome.Unavailable"/> with a warning rather than handing
    /// back the two thousand read so far, because the two thousand are a partial catalogue and a
    /// partial catalogue is the failure this whole reader is careful about.
    /// </remarks>
    private const int MaxPages = 20;

    /// <summary>Creates the reader.</summary>
    public SmartRecruitersBoardClient(
        IHttpClientFactory httpClientFactory,
        ILogger<SmartRecruitersBoardClient> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override AtsVendor Vendor => AtsVendor.SmartRecruiters;

    /// <inheritdoc />
    protected override async Task<AtsBoardRead> ReadBoardAsync(
        AtsBoard board,
        CancellationToken cancellationToken)
    {
        var listings = new List<AtsListing>();
        string? boardName = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var offset = page * PageSize;

            var (outcome, value) = await FetchAsync(
                board,
                BoardEndpoint(board, offset),
                root => Parse(root, offset),
                cancellationToken);

            if (outcome != AtsBoardReadOutcome.Read || value is null)
            {
                // A failure on any later page is a board read short, and a board read short is
                // reported as unread rather than as complete.
                if (page > 0)
                {
                    return Truncated(board, "a page after the first did not answer");
                }

                // The first page decides for the board: a 404 there means no such company, which
                // is the answer a probe wants.
                return outcome == AtsBoardReadOutcome.NotABoard
                    ? AtsBoardRead.NotABoard
                    : AtsBoardRead.Unavailable;
            }

            listings.AddRange(value.Listings);
            boardName ??= value.BoardName;

            // Fewer rows than asked for is the last page, whatever the total claims. Zero rows on
            // the first page is an employer with no open vacancies, which is a real answer.
            if (value.Returned < PageSize)
            {
                return AtsBoardRead.For(listings, boardName);
            }

            if (value.TotalFound > 0 && offset + value.Returned >= value.TotalFound)
            {
                return AtsBoardRead.For(listings, boardName);
            }
        }

        return Truncated(board, $"the board exceeds the {MaxPages}-request bound");
    }

    /// <summary>The verified listing endpoint, one page of it.</summary>
    /// <remarks>
    /// <b><c>limit</c> is stated rather than left to the default</b>, so the arithmetic deciding
    /// whether a page was the last one is arithmetic about a number this code chose. A default that
    /// moved would otherwise turn "fewer rows than asked for" into a stop that fires a page early
    /// or never fires at all.
    /// </remarks>
    private static Uri? BoardEndpoint(AtsBoard board, int offset)
        => Endpoint(
            board.Region == AtsBoardRegion.Default ? "api.smartrecruiters.com" : null,
            $"/v1/companies/{Escaped(board.Token)}/postings?limit={PageSize}&offset={offset}");

    /// <summary>
    /// <c>{"offset":0,"limit":100,"totalFound":10,"content":[{"name":...,"location":{...},
    /// "company":{"name":...},"applyUrl":...}]}</c>
    /// </summary>
    /// <remarks>
    /// <b>The echoed <c>offset</c> is checked against the one asked for</b>, and a mismatch answers
    /// null. That is the guard against a vendor - or a proxy in front of one - that ignores the
    /// parameter: the loop would otherwise read page one repeatedly, count its rows towards the
    /// total, and stop believing it had read a whole board when it had read the first hundred
    /// several times. An absent <c>offset</c> is not a mismatch; only a present and different one
    /// is.
    /// </remarks>
    private static Page? Parse(JsonElement root, int offset)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (root.TryGetProperty("offset", out var echoed)
            && echoed.ValueKind == JsonValueKind.Number
            && echoed.TryGetInt32(out var echoedOffset)
            && echoedOffset != offset)
        {
            return null;
        }

        var listings = new List<AtsListing>(content.GetArrayLength());
        var returned = 0;
        string? boardName = null;

        foreach (var posting in content.EnumerateArray())
        {
            returned++;
            boardName ??= Employer(posting);

            var listing = Listing(
                Text(posting, "name"),
                Place(posting),
                FirstText(posting, "applyUrl", "postingUrl"));

            if (listing is not null)
            {
                listings.Add(listing);
            }
        }

        return new Page(listings, boardName, returned, Total(root));
    }

    /// <summary><c>company.name</c>, the one board name of the four endpoints.</summary>
    private static string? Employer(JsonElement posting)
        => posting.TryGetProperty("company", out var company) ? Text(company, "name") : null;

    /// <summary>
    /// The place, composed from the structured fields SmartRecruiters splits it into.
    /// </summary>
    /// <remarks>
    /// <b>Composed rather than reduced to a city, which looks like the wrong way round and is
    /// not.</b> <c>AtsListingMatcher</c> wants the free text as published and cuts it on commas
    /// itself, comparing each segment whole - so joining city, region and country with commas hands
    /// it exactly the shape it already handles, and the region and country segments can never
    /// falsely equal a city. Picking the city here would be this file settling the hardest question
    /// in the match, in the one place with no fixture to check it against.
    ///
    /// A row with no place at all but marked remote answers "Remote", which the matcher reads as an
    /// arrangement and therefore as the place being unstated - the honest answer, and one that
    /// costs the confirmation rather than the match.
    /// </remarks>
    private static string? Place(JsonElement posting)
    {
        if (!posting.TryGetProperty("location", out var location)
            || location.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var place = new StringBuilder();

        foreach (var name in new[] { "city", "region", "country" })
        {
            var part = Text(location, name);

            if (part is null)
            {
                continue;
            }

            if (place.Length > 0)
            {
                place.Append(", ");
            }

            place.Append(part);
        }

        if (place.Length > 0)
        {
            return place.ToString();
        }

        return location.TryGetProperty("remote", out var remote)
            && remote.ValueKind == JsonValueKind.True
                ? "Remote"
                : null;
    }

    /// <summary>How many postings the board says it has, or zero where it does not say.</summary>
    private static int Total(JsonElement root)
        => root.TryGetProperty("totalFound", out var total)
            && total.ValueKind == JsonValueKind.Number
            && total.TryGetInt32(out var found)
                ? found
                : 0;

    /// <summary>A board read short. Never handed back as though it were whole.</summary>
    private AtsBoardRead Truncated(AtsBoard board, string reason)
    {
        Logger.LogWarning(
            "SmartRecruiters board {Token} was read short: {Reason}. Reporting it unread rather "
            + "than partial, because a missing listing is a missing contender and an abstention "
            + "lost is an application sent to the wrong vacancy.",
            board.Token,
            reason);

        return AtsBoardRead.Unavailable;
    }

    /// <summary>One page, with the two numbers the loop needs and the shared type does not carry.</summary>
    private sealed record Page(
        IReadOnlyList<AtsListing> Listings,
        string? BoardName,
        int Returned,
        int TotalFound);
}
