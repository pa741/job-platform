using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPlatform.Api.Features.Postings;
using JobPlatform.Core.Model;
using Xunit;

namespace JobPlatform.Api.Tests;

public sealed class PostingEndpointTests : IAsyncLifetime
{
    private readonly ApiFactory _factory = new();
    private HttpClient _client = null!;

    private static readonly DateOnly ScrapeDate = new(2026, 8, 18);
    private const string Term = "software-engineer";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();

        await _factory.SeedAsync(
            Term, ScrapeDate,
            Posting("indeed", "a1", "Backend Engineer", "Northwind", remote: false),
            Posting("linkedin", "b1", "Site Reliability Engineer", "Contoso", remote: true),
            Posting("indeed", "c1", "Frontend Developer", "Fabrikam", remote: true, min: 70000));
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static JobPosting Posting(
        string site, string id, string title, string company,
        bool remote = false, decimal? min = null, string description = "Some description text.")
        => new()
        {
            ExternalId = id,
            Site = site,
            Title = title,
            Company = company,
            Location = "London, ENG, GB",
            IsRemote = remote,
            MinAmount = min,
            Currency = min is null ? null : "GBP",
            Description = description,
        };

    [Fact]
    public async Task Search_returns_every_seeded_posting()
    {
        var page = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}", Json);

        Assert.NotNull(page);
        Assert.Equal(3, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.Null(page.Total);
    }

    [Fact]
    public async Task Search_filters_narrow_the_result()
    {
        var remote = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}&remote=true", Json);
        Assert.Equal(2, remote!.Items.Count);
        Assert.All(remote.Items, p => Assert.True(p.IsRemote));

        var site = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}&site=indeed", Json);
        Assert.Equal(2, site!.Items.Count);
        Assert.All(site.Items, p => Assert.Equal("indeed", p.Site));

        var salaried = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}&hasSalary=true", Json);
        Assert.Single(salaried!.Items);
        Assert.Equal("Frontend Developer", salaried.Items[0].Title);

        var text = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}&q=Reliability", Json);
        Assert.Single(text!.Items);
        Assert.Equal("Contoso", text.Items[0].Company);
    }

    [Fact]
    public async Task Paging_reports_more_without_returning_the_extra_row()
    {
        var page = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}&limit=2", Json);

        Assert.Equal(2, page!.Items.Count);
        Assert.True(page.HasMore);

        var last = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}&limit=2&offset=2", Json);

        Assert.Single(last!.Items);
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task Total_is_returned_only_when_asked_for()
    {
        var page = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}&limit=2&includeTotal=true", Json);

        Assert.Equal(3, page!.Total);
        Assert.True(page.HasMore);
    }

    /// <summary>
    /// The regression this guards is a response-size one, not a correctness one: descriptions
    /// are unbounded nvarchar(max), so a summary that carried them would turn a 100-row page
    /// into megabytes. It fails silently in every other respect.
    /// </summary>
    [Fact]
    public async Task List_responses_carry_no_description_but_detail_does()
    {
        var raw = await _client.GetStringAsync($"/api/v1/postings?searchTerm={Term}");

        Assert.DoesNotContain("Some description text.", raw, StringComparison.Ordinal);
        Assert.Contains("descriptionLength", raw, StringComparison.Ordinal);

        var page = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}", Json);

        var detail = await _client.GetFromJsonAsync<PostingDetail>(
            $"/api/v1/postings/{page!.Items[0].Id}", Json);

        Assert.Equal("Some description text.", detail!.Description);
    }

    [Fact]
    public async Task Sorting_by_title_ascending_orders_by_title()
    {
        var page = await _client.GetFromJsonAsync<PageResponse<PostingSummary>>(
            $"/api/v1/postings?searchTerm={Term}&sort=title&order=asc", Json);

        var titles = page!.Items.Select(p => p.Title).ToList();
        Assert.Equal(titles.OrderBy(t => t, StringComparer.Ordinal), titles);
    }

    [Fact]
    public async Task An_unknown_sort_is_a_problem_response_not_a_silent_default()
    {
        var response = await _client.GetAsync($"/api/v1/postings?searchTerm={Term}&sort=nonsense");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task A_missing_posting_is_a_404()
    {
        var response = await _client.GetAsync("/api/v1/postings/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Facets_describe_the_filter_vocabulary()
    {
        var facets = await _client.GetFromJsonAsync<FacetsResponse>(
            $"/api/v1/postings/facets?searchTerm={Term}", Json);

        Assert.Equal(3, facets!.Total);
        Assert.Equal(2, facets.RemoteCount);
        Assert.Equal(1, facets.WithSalaryCount);
        Assert.Equal(2, facets.Sites.Single(s => s.Name == "indeed").Count);
        Assert.Contains(facets.Cities, c => c.Name == "London");
    }

    [Fact]
    public async Task Search_terms_report_posting_and_run_counts()
    {
        var terms = await _client.GetFromJsonAsync<List<SearchTermResponse>>(
            "/api/v1/search-terms", Json);

        var term = Assert.Single(terms!);
        Assert.Equal(Term, term.SearchTerm);
        Assert.Equal(3, term.PostingCount);
        Assert.Equal(1, term.RunCount);
        Assert.Equal(ScrapeDate, term.LastScrapeDate);
    }
}
