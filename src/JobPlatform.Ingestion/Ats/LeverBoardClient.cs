using System.Text.Json;
using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Reads a Lever postings feed.
/// </summary>
/// <remarks>
/// <b>The endpoint is <c>api.lever.co/v0/postings/{token}?mode=json</c>, verified live on
/// 2026-09-07 against <c>apolloresearch</c>, which answered 14 postings.</b> <c>ATS-ENDPOINTS.md</c>
/// is the record. The <c>mode=json</c> is not decoration: without it the same path serves the HTML
/// board, and a reader that dropped the parameter would be parsing a web page as JSON and reporting
/// every Lever employer as unreadable.
///
/// <b>Lever is the one of the four that answers a bare array</b>, and the one that says "no such
/// board" in the body as well as in the status. <c>ATS-ENDPOINTS.md</c> records
/// <c>{"ok":false,"error":"Document not found"}</c> as a well-formed answer rather than an empty
/// list, so an object where an array was expected is read as
/// <see cref="AtsBoardReadOutcome.NotABoard"/> - the same fact a 404 states, spelled differently,
/// and it must reach the caller as the same value or a probe against Lever would look like a vendor
/// outage and be retried for ever.
///
/// <b>Its titles are in <c>text</c> and its place is in <c>categories.location</c></b>, which is
/// the whole reason this layer exists: nothing above it should have to know that Lever spells a
/// title differently from the other three.
///
/// <b>Lever publishes no company name in this feed</b>, which <c>AtsBoardCandidates.Confirm</c>
/// already records: on the evidence this reader can supply, a probed Lever token is always
/// <c>Unconfirmed</c>. That is a statement about the vendor rather than a gap here, and the learned
/// path - a token lifted from a link the employer published - owes no confirmation at all.
///
/// <b>The European tenants are the one region case in this feature, and this reader refuses to
/// guess at their host.</b> Lever's data-residency customers are served from
/// <c>jobs.eu.lever.co</c> and their listings come from a separate API host, which is exactly why
/// <c>AtsBoardRegion</c> exists - but that host is not in the verified record, and asking the
/// ordinary one would answer nothing, which reads as "this employer is not on Lever" and is
/// indistinguishable from the employer genuinely not being there. So
/// <see cref="AtsBoardRegion.Europe"/> answers null here, which surfaces as one warning naming the
/// region rather than as a silent false negative. Verify the host, add it to
/// <c>ATS-ENDPOINTS.md</c>, then add the row.
/// </remarks>
public sealed class LeverBoardClient : AtsBoardClient
{
    /// <summary>Creates the reader.</summary>
    public LeverBoardClient(IHttpClientFactory httpClientFactory, ILogger<LeverBoardClient> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override AtsVendor Vendor => AtsVendor.Lever;

    /// <inheritdoc />
    protected override Task<AtsBoardRead> ReadBoardAsync(
        AtsBoard board,
        CancellationToken cancellationToken)
        => ReadOnceAsync(board, BoardEndpoint(board), Parse, cancellationToken);

    /// <summary>The verified listing endpoint. Only the ordinary region has one written down.</summary>
    private static Uri? BoardEndpoint(AtsBoard board)
        => Endpoint(
            board.Region == AtsBoardRegion.Default ? "api.lever.co" : null,
            $"/v0/postings/{Escaped(board.Token)}?mode=json");

    /// <summary>
    /// <c>[{"text":...,"categories":{"location":...},"applyUrl":...,"hostedUrl":...}]</c>
    /// </summary>
    /// <remarks>
    /// An object rather than an array is Lever's own "not found", and is reported as such. Anything
    /// else that is not an array - a number, a string, a null - is a body this does not recognise
    /// and answers null, which is <see cref="AtsBoardRead.Unavailable"/> rather than a claim about
    /// the employer.
    /// </remarks>
    private static AtsBoardRead? Parse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            return AtsBoardRead.NotABoard;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var listings = new List<AtsListing>(root.GetArrayLength());

        foreach (var posting in root.EnumerateArray())
        {
            var listing = Listing(
                Text(posting, "text"),
                Place(posting),
                FirstText(posting, "applyUrl", "hostedUrl"));

            if (listing is not null)
            {
                listings.Add(listing);
            }
        }

        return AtsBoardRead.For(listings);
    }

    /// <summary><c>categories.location</c>, free text, passed through as published.</summary>
    private static string? Place(JsonElement posting)
        => posting.TryGetProperty("categories", out var categories)
            ? Text(categories, "location")
            : null;
}
