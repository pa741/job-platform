using System.Text.Json;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Reads an Ashby job board.
/// </summary>
/// <remarks>
/// <b>The endpoint is <c>api.ashbyhq.com/posting-api/job-board/{token}</c>, verified live on
/// 2026-09-07 against <c>sampura</c> and <c>9fin</c>.</b> It is Ashby's own posting API, published
/// for job seekers and unauthenticated; <c>ATS-ENDPOINTS.md</c> is the record and it is not to be
/// re-derived by probing.
///
/// <b>The apply URL is recorded there by its container rather than by its name</b> - "in
/// <c>jobs[]</c>" - so this reads <c>applyUrl</c> first and falls back to <c>jobUrl</c>. The order
/// is the argument: <c>applyUrl</c> is the application form and <c>jobUrl</c> is the advert with the
/// form one click behind it, and both are pages a person can apply from, so the second is a weaker
/// answer rather than a wrong one. A listing carrying neither is dropped rather than guessed at.
///
/// <b>Nothing is filtered.</b> Ashby marks a posting's visibility on the row, and it is tempting to
/// use: a vacancy the employer has taken off the board is not one to send anybody to. It is left
/// alone because the field's exact semantics are not in the verified record, and the cost of being
/// wrong is asymmetric - a filter that misreads it silently removes recoveries, while leaving it in
/// costs at most a title that matches nothing. <c>AtsListingMatcher</c> also asks for the whole
/// board on its own account: two listings excluded before they arrive are two it cannot abstain
/// between.
///
/// <b>Ashby's board response does not name the employer either</b>, so a probed Ashby token is
/// <c>Unconfirmed</c> on names alone, exactly as Greenhouse and Lever are. See
/// <see cref="AtsBoardRead.BoardName"/> for what that costs and which path is unaffected.
/// </remarks>
public sealed class AshbyBoardClient : AtsBoardClient
{
    /// <summary>Creates the reader.</summary>
    public AshbyBoardClient(IHttpClientFactory httpClientFactory, ILogger<AshbyBoardClient> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override AtsVendor Vendor => AtsVendor.Ashby;

    /// <inheritdoc />
    protected override Task<AtsBoardRead> ReadBoardAsync(
        AtsBoard board,
        CancellationToken cancellationToken)
        => ReadOnceAsync(board, BoardEndpoint(board), Parse, cancellationToken);

    /// <summary>The verified listing endpoint. Ashby publishes one host.</summary>
    private static Uri? BoardEndpoint(AtsBoard board)
        => Endpoint(
            board.Region == AtsBoardRegion.Default ? "api.ashbyhq.com" : null,
            $"/posting-api/job-board/{Escaped(board.Token)}");

    /// <summary>
    /// <c>{"apiVersion":"1","jobs":[{"title":...,"location":...,"applyUrl":...,"jobUrl":...}]}</c>
    /// </summary>
    /// <remarks>
    /// <c>location</c> is a plain string here rather than Greenhouse's nested object, and it is
    /// passed through untouched for the reason <c>AtsListing</c> gives: the free-text place is the
    /// hardest question in the match and pre-parsing it settles that question where nothing tests
    /// it.
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
            var listing = Listing(
                Text(job, "title"),
                Text(job, "location"),
                FirstText(job, "applyUrl", "jobUrl"));

            if (listing is not null)
            {
                listings.Add(listing);
            }
        }

        return AtsBoardRead.For(listings);
    }
}
