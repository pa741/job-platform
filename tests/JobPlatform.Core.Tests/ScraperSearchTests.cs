using System.Text.Json;
using JobPlatform.Core.Searches;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The slug rule, which has to agree with a Python function in another repository.
/// </summary>
/// <remarks>
/// The scraper still slugifies when it falls back to its own <c>config.yaml</c>, so a name
/// producing two different slugs on the two sides would attach one search's postings to two
/// search terms and nothing would report it. These cases are the ones where plausible
/// implementations differ - leading and trailing punctuation, runs of separators, non-ASCII, and
/// the empty result - rather than the ones that obviously work.
/// </remarks>
public sealed class SearchSlugTests
{
    [Theory]
    [InlineData("software engineer", "software-engineer")]
    [InlineData("Software Engineer", "software-engineer")]
    [InlineData("  Software   Engineer  ", "software-engineer")]
    [InlineData("C# / .NET Developer", "c-net-developer")]
    [InlineData("---weird---", "weird")]
    [InlineData("data_engineer", "data-engineer")]
    [InlineData("Senior (Remote)", "senior-remote")]
    // Replaced, never transliterated - matching re.sub over [^a-zA-Z0-9]+ exactly. Prettier is
    // not the goal; agreeing with the other side is.
    [InlineData("Développeur", "d-veloppeur")]
    [InlineData("!!!", SearchSlug.Fallback)]
    [InlineData("", SearchSlug.Fallback)]
    [InlineData(null, SearchSlug.Fallback)]
    public void Slugify_matches_the_scrapers_rule(string? name, string expected)
        => Assert.Equal(expected, SearchSlug.Slugify(name));

    [Fact]
    public void A_free_slug_is_used_unchanged()
        => Assert.Equal(
            "software-engineer",
            SearchSlug.Unique("Software Engineer", new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// Two people may name a search the same thing; they cannot share a slug.
    /// </summary>
    /// <remarks>
    /// Suffixing rather than refusing is what stops one person learning that another person's
    /// search exists under that name - and the alternative, a 409, would make them invent a new
    /// name for a reason they cannot see.
    /// </remarks>
    [Fact]
    public void A_taken_slug_is_suffixed()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "software-engineer" };

        Assert.Equal("software-engineer-2", SearchSlug.Unique("Software Engineer", taken));
    }

    [Fact]
    public void Suffixes_continue_past_the_first_collision()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "software-engineer", "software-engineer-2", "software-engineer-3",
        };

        Assert.Equal("software-engineer-4", SearchSlug.Unique("Software Engineer", taken));
    }

    /// <summary>The slug has to fit the column it becomes a key in, suffix included.</summary>
    [Fact]
    public void A_long_name_is_truncated_to_the_column()
    {
        var slug = SearchSlug.Slugify(new string('a', SearchSlug.MaxLength + 50));

        Assert.Equal(SearchSlug.MaxLength, slug.Length);
    }

    [Fact]
    public void A_long_name_still_fits_once_suffixed()
    {
        var name = new string('a', SearchSlug.MaxLength + 50);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SearchSlug.Slugify(name) };

        Assert.True(SearchSlug.Unique(name, taken).Length <= SearchSlug.MaxLength);
    }
}

/// <summary>
/// Every bound a configured search has to clear, and why each one exists.
/// </summary>
public sealed class ScraperSearchValidationTests
{
    private static ScraperSearch Valid(Action<ScraperSearchBuilder>? change = null)
    {
        var builder = new ScraperSearchBuilder();
        change?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public void A_complete_search_has_no_problems()
        => Assert.Empty(ScraperSearchValidation.Validate(Valid()));

    [Fact]
    public void A_search_needs_a_name()
        => Assert.Contains(
            ScraperSearchValidation.Validate(Valid(b => b.Name = "   ")),
            problem => problem.Contains("name", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void A_search_needs_a_search_term()
        => Assert.Contains(
            ScraperSearchValidation.Validate(Valid(b => b.SearchTerm = "")),
            problem => problem.Contains("search term", StringComparison.OrdinalIgnoreCase));

    /// <summary>A search naming no board scrapes nothing and reports success doing it.</summary>
    [Fact]
    public void A_search_needs_at_least_one_board()
        => Assert.Contains(
            ScraperSearchValidation.Validate(Valid(b => b.Sites = [])),
            problem => problem.Contains("job board", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ScraperSearchValidation.MaxHoursOld + 1)]
    public void Hours_old_is_bounded(int hours)
        => Assert.Contains(
            ScraperSearchValidation.Validate(Valid(b => b.HoursOld = hours)),
            problem => problem.Contains("Hours old", StringComparison.Ordinal));

    /// <summary>
    /// The bound that stops a scheduled run from not finishing.
    /// </summary>
    /// <remarks>
    /// Searches run one after another and LinkedIn spends roughly one extra request per posting
    /// when descriptions are fetched, so this multiplies across the whole run rather than
    /// bounding one search.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(ScraperSearchValidation.MaxResultsWanted + 1)]
    public void Results_wanted_is_bounded(int wanted)
        => Assert.Contains(
            ScraperSearchValidation.Validate(Valid(b => b.ResultsWanted = wanted)),
            problem => problem.Contains("Results wanted", StringComparison.Ordinal));

    /// <summary>
    /// The bound that keeps this feature from being an injection surface.
    /// </summary>
    /// <remarks>
    /// These keys end up as dictionary keys inside a parameter the scraper forwards, so an open
    /// key set is an open parameter set by another name.
    /// </remarks>
    [Fact]
    public void An_unknown_freehire_filter_key_is_named_rather_than_dropped()
    {
        var problems = ScraperSearchValidation.Validate(
            Valid(b => b.FreehireFilters = new Dictionary<string, string> { ["proxies"] = "evil" }));

        Assert.Contains(problems, problem => problem.Contains("proxies", StringComparison.Ordinal));
    }

    [Fact]
    public void A_known_freehire_filter_key_is_accepted()
        => Assert.Empty(ScraperSearchValidation.Validate(
            Valid(b => b.FreehireFilters = new Dictionary<string, string> { ["seniority"] = "senior" })));

    [Fact]
    public void An_unrecognised_job_type_is_refused()
        => Assert.Contains(
            ScraperSearchValidation.Validate(Valid(b => b.JobType = "wizard")),
            problem => problem.Contains("wizard", StringComparison.Ordinal));

    /// <summary>Every problem at once: four empty fields is one save, not four.</summary>
    [Fact]
    public void All_problems_are_reported_together()
    {
        var problems = ScraperSearchValidation.Validate(Valid(b =>
        {
            b.Name = "";
            b.SearchTerm = "";
            b.Sites = [];
        }));

        Assert.Equal(3, problems.Count);
    }
}

/// <summary>
/// The contract with the scraper: exactly which jobspy keyword arguments a search becomes.
/// </summary>
/// <remarks>
/// Nothing else in this repository checks these strings, and they are consumed by a Python
/// process in another repository that will happily accept a misspelled keyword as an error at
/// 03:00 rather than at build time.
/// </remarks>
public sealed class ScraperConfigDocumentTests
{
    private static ScraperSearch Search(Action<ScraperSearchBuilder>? change = null)
    {
        var builder = new ScraperSearchBuilder();
        change?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public void The_always_present_parameters_are_named_exactly()
    {
        var parameters = ScraperConfigDocument.ToParams(Search());

        Assert.Equal("software engineer", parameters["search_term"]);
        Assert.Equal(new[] { "indeed", "linkedin" }, Assert.IsType<string[]>(parameters["site_name"]));
    }

    [Fact]
    public void Optional_parameters_are_named_exactly()
    {
        var parameters = ScraperConfigDocument.ToParams(Search(b =>
        {
            b.Location = "London, UK";
            b.CountryIndeed = "UK";
            b.IsRemote = true;
            b.HoursOld = 24;
            b.ResultsWanted = 500;
            b.JobType = "fulltime";
            b.FreehireFilters = new Dictionary<string, string> { ["seniority"] = "senior" };
        }));

        Assert.Equal("London, UK", parameters["location"]);
        Assert.Equal("UK", parameters["country_indeed"]);
        Assert.Equal(true, parameters["is_remote"]);
        Assert.Equal(24, parameters["hours_old"]);
        Assert.Equal(500, parameters["results_wanted"]);
        Assert.Equal("fulltime", parameters["job_type"]);
        Assert.Equal(
            new Dictionary<string, string> { ["seniority"] = "senior" },
            Assert.IsType<Dictionary<string, string>>(parameters["freehire_filters"]));
    }

    /// <summary>
    /// The distinction the whole merge on the scraper side rests on.
    /// </summary>
    /// <remarks>
    /// The scraper merges these over its own <c>defaults:</c> block, which holds the operational
    /// settings. A key present with a null value would overwrite one of those defaults with
    /// nothing, so "the person did not choose" has to be absent rather than null.
    /// </remarks>
    [Fact]
    public void An_unset_option_is_omitted_rather_than_written_as_null()
    {
        var parameters = ScraperConfigDocument.ToParams(Search());

        Assert.DoesNotContain("location", parameters.Keys);
        Assert.DoesNotContain("hours_old", parameters.Keys);
        Assert.DoesNotContain("is_remote", parameters.Keys);
        Assert.DoesNotContain("freehire_filters", parameters.Keys);
    }

    /// <summary>False is a choice; null is not. Collapsing them loses the difference.</summary>
    [Fact]
    public void Remote_false_is_written_and_remote_null_is_not()
    {
        Assert.Equal(false, ScraperConfigDocument.ToParams(Search(b => b.IsRemote = false))["is_remote"]);
        Assert.DoesNotContain("is_remote", ScraperConfigDocument.ToParams(Search(b => b.IsRemote = null)).Keys);
    }

    /// <summary>
    /// No owner reaches the document.
    /// </summary>
    /// <remarks>
    /// It is read on a NAS outside the tenant. The domain record has no field for an owner, so
    /// this cannot regress by somebody adding a property - but it is worth an assertion, because
    /// the thing that would break it is a well-meant "add the owner for debugging".
    /// </remarks>
    [Fact]
    public void The_published_document_carries_no_owner()
    {
        var json = ScraperConfigDocument.Build([Search()], DateTimeOffset.UnixEpoch).ToJson();

        Assert.DoesNotContain("subject", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_document_carries_its_version_and_the_slug_of_each_search()
    {
        var document = ScraperConfigDocument.Build([Search()], DateTimeOffset.UnixEpoch);

        using var parsed = JsonDocument.Parse(document.ToJson());
        var root = parsed.RootElement;

        Assert.Equal(ScraperConfigDocument.CurrentVersion, root.GetProperty("version").GetInt32());
        Assert.Equal(
            "software-engineer",
            root.GetProperty("searches")[0].GetProperty("slug").GetString());
        Assert.Equal(
            "software engineer",
            root.GetProperty("searches")[0].GetProperty("params").GetProperty("search_term").GetString());
    }

    /// <summary>
    /// An unchanged set of searches has to produce an identical document.
    /// </summary>
    /// <remarks>
    /// Otherwise a diff of two published files says everything changed, which is the same
    /// problem <c>JobTypeNormalizer</c>'s fixed ordering solves for a posting's hash.
    /// </remarks>
    [Fact]
    public void Searches_are_ordered_by_slug()
    {
        var document = ScraperConfigDocument.Build(
            [
                Search(b => { b.Slug = "zebra"; }),
                Search(b => { b.Slug = "aardvark"; }),
            ],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(["aardvark", "zebra"], document.Searches.Select(search => search.Slug));
    }
}

/// <summary>A valid search, with one thing changed. Keeps each test to its own subject.</summary>
internal sealed class ScraperSearchBuilder
{
    public string Slug { get; set; } = "software-engineer";
    public string Name { get; set; } = "Software Engineer";
    public bool Enabled { get; set; } = true;
    public string SearchTerm { get; set; } = "software engineer";
    public IReadOnlyList<ScraperSite> Sites { get; set; } = [ScraperSite.Indeed, ScraperSite.LinkedIn];
    public string? Location { get; set; }
    public string? CountryIndeed { get; set; }
    public bool? IsRemote { get; set; }
    public int? HoursOld { get; set; }
    public int? ResultsWanted { get; set; }
    public string? JobType { get; set; }
    public IReadOnlyDictionary<string, string> FreehireFilters { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ScraperSearch Build() => new()
    {
        Slug = Slug,
        Name = Name,
        Enabled = Enabled,
        SearchTerm = SearchTerm,
        Sites = Sites,
        Location = Location,
        CountryIndeed = CountryIndeed,
        IsRemote = IsRemote,
        HoursOld = HoursOld,
        ResultsWanted = ResultsWanted,
        JobType = JobType,
        FreehireFilters = FreehireFilters,
    };
}
