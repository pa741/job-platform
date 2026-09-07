using System.Text.Json;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Reads a Greenhouse job board.
/// </summary>
/// <remarks>
/// <b>The endpoint is <c>boards-api.greenhouse.io/v1/boards/{token}/jobs</c> and it is settled
/// rather than derived here.</b> Verified live on 2026-09-07 against <c>cloudflare</c>, which
/// answered 200 with 333 jobs and carried <c>absolute_url</c> for the exact posting held here as
/// 3020 - the one LinkedIn withheld a URL for. <c>ATS-ENDPOINTS.md</c> is the record; do not probe
/// to re-derive it, because the probing is a cost to somebody else's service and the shape does not
/// need re-establishing.
///
/// <b>The whole board arrives in one document, which is what makes the per-board rule affordable.</b>
/// There is no paging parameter to get wrong and no second request to make: 333 vacancies for the
/// price of one GET, against 333 GETs if this were asked per posting.
///
/// <b>Greenhouse's listing endpoint does not name the employer, and that is a real cost worth
/// stating here rather than discovering downstream.</b> <c>AtsBoardCandidates.Confirm</c> confirms a
/// probed token by the name the board reports for itself, so a Greenhouse token guessed from a
/// company name comes back <c>Unconfirmed</c> on the evidence this reader can supply - exactly as
/// Lever already does, and for the same reason. It costs nothing on the learned path, where the
/// token was lifted from a link the employer themselves published and no confirmation is owed, and
/// that path is 122 of the 309 link-less postings on its own. Closing the gap means a second
/// verified endpoint in <c>ATS-ENDPOINTS.md</c>, not a guess added here.
///
/// <b><c>absolute_url</c> is read and nothing else is.</b> It is frequently the employer's own
/// careers domain rather than a greenhouse.io host - Greenhouse's embed keeps the employer's domain
/// and carries the job id in <c>gh_jid</c>, which is 1,259 of 2,006 direct apply URLs in this
/// corpus - so the host is deliberately not checked against the vendor's.
/// </remarks>
public sealed class GreenhouseBoardClient : AtsBoardClient
{
    /// <summary>Creates the reader.</summary>
    public GreenhouseBoardClient(
        IHttpClientFactory httpClientFactory,
        ILogger<GreenhouseBoardClient> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override AtsVendor Vendor => AtsVendor.Greenhouse;

    /// <inheritdoc />
    protected override Task<AtsBoardRead> ReadBoardAsync(
        AtsBoard board,
        CancellationToken cancellationToken)
        => ReadOnceAsync(board, BoardEndpoint(board), Parse, cancellationToken);

    /// <summary>The verified listing endpoint, per region.</summary>
    /// <remarks>
    /// Greenhouse publishes one board host, so anything but
    /// <see cref="AtsBoardRegion.Default"/> has no address written down and answers null - which
    /// <see cref="AtsBoardClient.FetchAsync{T}"/> turns into a warning rather than into a request
    /// to the wrong host. That case cannot arise from a Greenhouse link today, since
    /// <c>AtsBoardToken</c> only reads a region off an <c>eu</c> label and only Lever ships one; it
    /// is written this way so the next vendor to offer data residency is a table entry rather than
    /// a rule somebody has to remember.
    /// </remarks>
    private static Uri? BoardEndpoint(AtsBoard board)
        => Endpoint(
            board.Region == AtsBoardRegion.Default ? "boards-api.greenhouse.io" : null,
            $"/v1/boards/{Escaped(board.Token)}/jobs");

    /// <summary>
    /// <c>{"jobs":[{"title":...,"location":{"name":...},"absolute_url":...}],"meta":{...}}</c>
    /// </summary>
    /// <remarks>
    /// A body with no <c>jobs</c> array answers null - unreadable, not empty. An empty
    /// <c>jobs</c> array is a real and ordinary answer: this employer's board exists and has no
    /// open vacancies today.
    /// </remarks>
    private static AtsBoardRead? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("jobs", out var jobs)
            || jobs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var listings = new List<AtsListing>(jobs.GetArrayLength());

        foreach (var job in jobs.EnumerateArray())
        {
            var listing = Listing(Text(job, "title"), Place(job), Text(job, "absolute_url"));

            if (listing is not null)
            {
                listings.Add(listing);
            }
        }

        return AtsBoardRead.For(listings);
    }

    /// <summary>
    /// <c>location.name</c>, which is free text a recruiter typed.
    /// </summary>
    /// <remarks>
    /// Passed through exactly as published and never pre-parsed - <c>AtsListing</c> asks for it
    /// that way, because "Hybrid", "London, UK", "Remote (UK)" and "New York, NY (HQ)" are all real
    /// shapes and settling which of them is a city is the hardest question in
    /// <c>AtsListingMatcher</c>. Doing it here would settle it somewhere nothing tests it.
    /// </remarks>
    private static string? Place(JsonElement job)
        => job.TryGetProperty("location", out var location) ? Text(location, "name") : null;
}
