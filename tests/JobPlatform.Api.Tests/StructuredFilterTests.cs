using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPlatform.Api.Features.Postings;
using JobPlatform.Core.Model;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The filters that only exist because of enrichment.
/// </summary>
/// <remarks>
/// The concept filter is the one worth the setup: asking for <c>area.backend</c> has to
/// return postings that never mention those words, because the match runs through the
/// closure. That is the whole payoff of materialising it, and it is not obvious from reading
/// the query.
/// </remarks>
public sealed class StructuredFilterTests : IAsyncLifetime
{
    private readonly ApiFactory _factory = new();
    private HttpClient _client = null!;

    private const string Term = "software-engineer";
    private static readonly DateOnly ScrapeDate = new(2026, 8, 18);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();

        await _factory.SeedAsync(Term, ScrapeDate,
            Posting("a1", "Senior Backend Engineer",
                "We build services in C# on .NET. Hybrid, 3 days a week in the office. "
                + "Paying GBP 90,000 - GBP 110,000 per annum."),
            Posting("a2", "Frontend Engineer",
                "React and TypeScript. Fully remote. GBP 60,000 per annum."),
            Posting("a3", "Data Engineer",
                "Python and Apache Spark pipelines. Outside IR35, GBP 550 per day."),
            Posting("a4", "Security Engineer",
                "You must hold active SC clearance. This is an office-based role."),
            Posting("a5", "Software Engineer", "A role. Nothing else is said."));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static JobPosting Posting(string id, string title, string description) => new()
    {
        ExternalId = id,
        Site = "indeed",
        Title = title,
        Company = "Northwind Labs",
        Location = "London, ENG, GB",
        Description = description,
    };

    private async Task<List<PostingSummary>> SearchAsync(string query)
    {
        var response = await _client.GetAsync($"/api/v1/postings?searchTerm={Term}&{query}");
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<PageResponse<PostingSummary>>(Json);
        return page!.Items.ToList();
    }

    [Fact]
    public async Task A_concept_filter_matches_the_concept_itself()
    {
        var items = await SearchAsync("concept=skill.csharp");

        Assert.Equal("Senior Backend Engineer", Assert.Single(items).Title);
    }

    [Fact]
    public async Task A_domain_filter_matches_everything_underneath_it()
    {
        // None of these descriptions contains the words "backend development". They match
        // because C#, .NET, Python and Spark all roll up to it - which is exactly the query
        // the closure exists to make cheap, and impossible to express without it.
        var items = await SearchAsync("concept=area.backend");
        var titles = items.Select(i => i.Title).ToList();

        Assert.Contains("Senior Backend Engineer", titles);
        Assert.Contains("Data Engineer", titles);
        Assert.DoesNotContain("Software Engineer", titles);
    }

    [Fact]
    public async Task Seniority_filters_on_the_ordinal_scale()
    {
        var senior = await SearchAsync("minSeniority=Senior");

        Assert.Equal("Senior Backend Engineer", Assert.Single(senior).Title);
    }

    [Fact]
    public async Task An_unknown_seniority_is_never_included_in_a_range()
    {
        // Unknown is 0, so "at least Junior" would sweep in every posting whose title says
        // nothing - which on this corpus is most of them.
        var items = await SearchAsync("minSeniority=Junior");

        Assert.DoesNotContain(items, i => i.Seniority == "Unknown");
    }

    [Fact]
    public async Task Role_family_and_work_arrangement_filter()
    {
        Assert.Equal("Frontend Engineer", Assert.Single(await SearchAsync("roleFamily=Frontend")).Title);
        Assert.Equal("Frontend Engineer", Assert.Single(await SearchAsync("workArrangement=Remote")).Title);
        Assert.Equal("Senior Backend Engineer", Assert.Single(await SearchAsync("workArrangement=Hybrid")).Title);
    }

    [Fact]
    public async Task Salary_filters_on_the_annualised_figure_not_the_scraped_one()
    {
        // Every one of these has an empty MinAmount - the salary exists only because it was
        // read out of prose. A filter on the raw column would return nothing at all.
        var items = await SearchAsync("minAnnualSalary=100000");
        var titles = items.Select(i => i.Title).ToList();

        Assert.Contains("Senior Backend Engineer", titles);

        // The contract at GBP 550 a day annualises to 143,000 and clears the threshold that
        // its headline figure never would. Comparing a day rate against a salary is only
        // possible because both were put on one scale - and SalaryStatedInterval is what
        // stops that being mistaken for a 143,000 salary.
        Assert.Contains("Data Engineer", titles);
        Assert.DoesNotContain("Frontend Engineer", titles);

        Assert.All(items, i => Assert.Null(i.MinAmount));
    }

    [Fact]
    public async Task Text_derived_salaries_can_be_excluded()
    {
        // Nothing in this corpus has a board-supplied salary, so asking for only those is
        // asking for nothing - which is the honest answer rather than an empty-looking bug.
        Assert.Empty(await SearchAsync("minAnnualSalary=1&includeTextSalary=false"));
    }

    [Fact]
    public async Task A_day_rate_is_annualised_but_still_identifiable()
    {
        var items = await SearchAsync("concept=skill.python");
        var data = Assert.Single(items);

        Assert.Equal(550m * 260m, data.AnnualSalaryMin);
        Assert.Equal("daily", data.SalaryStatedInterval);
    }

    [Fact]
    public async Task Clearance_and_ir35_filter()
    {
        Assert.Equal("Security Engineer", Assert.Single(await SearchAsync("securityClearance=true")).Title);
        Assert.Equal("Data Engineer", Assert.Single(await SearchAsync("ir35=outside")).Title);
    }

    [Theory]
    [InlineData("minSeniority=Enormous")]
    [InlineData("roleFamily=Wizardry")]
    [InlineData("workArrangement=Underwater")]
    [InlineData("ir35=sideways")]
    public async Task An_unrecognised_filter_value_is_rejected_rather_than_ignored(string query)
    {
        // Dropping the filter would return a plausible page of the wrong postings, and
        // nothing in the response would say so. That gets believed; a 400 does not.
        var response = await _client.GetAsync($"/api/v1/postings?searchTerm={Term}&{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Derived_fields_reach_the_client()
    {
        var posting = Assert.Single(await SearchAsync("concept=skill.csharp"));

        Assert.Equal("Senior", posting.Seniority);
        Assert.Equal("Backend", posting.RoleFamily);
        Assert.Equal("Hybrid", posting.WorkArrangement);
        Assert.Equal(3, posting.HybridDaysInOffice);
        Assert.True(posting.SalaryFromText);
    }
}
