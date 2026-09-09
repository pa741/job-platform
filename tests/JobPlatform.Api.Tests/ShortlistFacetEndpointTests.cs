using System.Net.Http.Json;
using System.Text.Json;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Matching;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The shortlist's aggregator facet, over HTTP.
/// </summary>
/// <remarks>
/// <b>The repository's own tests pin the filter; this pins that the route reaches it.</b> A query
/// parameter that binds to nothing is the quiet half of this kind of change: the page renders a
/// checkbox, the checkbox sends a value, the value is ignored, and the only symptom is a facet
/// that appears not to do anything - which reads as an empty market rather than as a broken
/// binding. Minimal-API parameter binding is by name, so the name is the contract and this is
/// what holds it.
///
/// The vendor is asserted on the row as well as in the filter, because the dashboard shows it: a
/// person needs to be able to see <i>which</i> rows the facet would take away before they decide
/// to take them away.
/// </remarks>
public sealed class ShortlistFacetEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset Scored = new(2026, 9, 9, 3, 30, 0, TimeSpan.Zero);

    /// <summary>A LinkedIn posting page and nothing else. What the facet is for.</summary>
    private const long JobBoardOnly = 601;

    /// <summary>The employer's own form. What the facet is protecting.</summary>
    private const long EmployerForm = 602;

    [Fact]
    public async Task The_facet_is_off_by_default_and_every_row_names_its_vendor()
    {
        using var harness = await CvLibraryHarness.CreateAsync(rendering: false);

        await SeedAsync(harness, JobBoardOnly, AtsVendor.Aggregator);
        await SeedAsync(harness, EmployerForm, AtsVendor.Greenhouse);

        var items = await ShortlistAsync(harness, query: string.Empty);

        // Off unless asked for, like every other filter on this route: a shortlist quietly showing
        // a subset reads as a market that has gone quiet rather than as a filter.
        Assert.Equal([JobBoardOnly, EmployerForm], items.Select(Id).Order().ToArray());

        Assert.Equal("Aggregator", Vendor(items, JobBoardOnly));
        Assert.Equal("Greenhouse", Vendor(items, EmployerForm));
    }

    [Fact]
    public async Task Asking_for_it_drops_the_postings_that_only_link_to_a_job_board()
    {
        using var harness = await CvLibraryHarness.CreateAsync(rendering: false);

        await SeedAsync(harness, JobBoardOnly, AtsVendor.Aggregator);
        await SeedAsync(harness, EmployerForm, AtsVendor.Greenhouse);

        var items = await ShortlistAsync(harness, "?excludeAggregators=true");

        Assert.Equal([EmployerForm], items.Select(Id).ToArray());
    }

    [Fact]
    public async Task A_posting_nobody_has_derived_a_vendor_for_survives_the_facet()
    {
        using var harness = await CvLibraryHarness.CreateAsync(rendering: false);

        await SeedAsync(harness, JobBoardOnly, vendor: null);

        var items = await ShortlistAsync(harness, "?excludeAggregators=true");

        // Null reaches the client as null rather than as "Unknown". Unknown is a verdict - there
        // is no address to open - and a client that read the two the same way would tell somebody
        // a link is dead when nothing has looked at it.
        var row = Assert.Single(items);

        Assert.Equal(JobBoardOnly, Id(row));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("applyVendor").ValueKind);
    }

    private static long Id(JsonElement row) => row.GetProperty("postingId").GetInt64();

    private static string? Vendor(JsonElement[] items, long postingId)
        => items.Single(row => Id(row) == postingId).GetProperty("applyVendor").GetString();

    private static async Task<JsonElement[]> ShortlistAsync(CvLibraryHarness harness, string query)
    {
        var body = await harness.As(CvLibraryHarness.Ada)
            .GetFromJsonAsync<JsonElement>($"/api/v1/matches{query}", Json);

        return [.. body.GetProperty("items").EnumerateArray()];
    }

    /// <summary>A posting with a stored vendor, scored against the harness's candidate.</summary>
    private static async Task SeedAsync(CvLibraryHarness harness, long postingId, AtsVendor? vendor)
    {
        await using var db = harness.Database();

        db.JobPostings.Add(new JobPostingEntity
        {
            Id = postingId,
            SourceKey = $"test:{postingId}",
            Site = "test",
            ExternalId = postingId.ToString(),
            ContentHash = new string('a', 64),
            Title = $"Role {postingId}",
            Company = "Northwind",
            Description = "An advert.",
            DescriptionLength = 10,
            ApplyVendor = vendor,
            FirstSeenUtc = Scored,
            LastSeenUtc = Scored,
        });

        await db.SaveChangesAsync();

        await new JobMatchRepository(db).UpsertScoresAsync(
            harness.AdaProfile,
            [(new PostingFacts { PostingId = postingId }, new MatchResult { Score = 80, Coverage = 1 })],
            [],
            Scored);
    }
}
