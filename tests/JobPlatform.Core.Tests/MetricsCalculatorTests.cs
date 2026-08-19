using JobPlatform.Core.Metrics;
using JobPlatform.Core.Parsing;
using JobPlatform.Core.Tests.Fixtures;
using Xunit;

namespace JobPlatform.Core.Tests;

public sealed class MetricsCalculatorTests
{
    private const string BlobPath = "jobs/software-engineer_2026-08-18T20-30-01Z.csv";

    private static RunDigest Digest(UpsertOutcome? upsert = null)
    {
        using var stream = SampleCsv.Open();
        var parsed = new JobCsvParser().Parse(stream);
        var context = BlobNameParser.Parse(BlobPath, DateTimeOffset.UnixEpoch, "etag", sizeBytes: 4242);

        return new MetricsCalculator().Calculate(
            context, parsed, upsert ?? new UpsertOutcome(30, 0, 0), durationMs: 1234);
    }

    [Fact]
    public void Digest_carries_the_run_identity_from_the_blob_name()
    {
        var digest = Digest();

        Assert.Equal("software-engineer", digest.SearchTerm);
        Assert.Equal("2026-08-18", digest.ScrapeDate);
        Assert.Equal(BlobPath, digest.BlobPath);
        Assert.Equal(4242, digest.BlobSizeBytes);
        Assert.Equal("run-digest", digest.Type);
    }

    [Fact]
    public void Digest_counts_reconcile_with_what_the_parser_saw()
    {
        var digest = Digest(new UpsertOutcome(New: 25, Updated: 4, Unchanged: 1));

        Assert.Equal(SampleCsv.RowsInFile, digest.Counts.RowsInFile);
        Assert.Equal(SampleCsv.ParsedPostings, digest.Counts.Parsed);
        Assert.Equal(2, digest.Counts.Invalid);
        Assert.Equal(1, digest.Counts.InFileDuplicates);
        Assert.Equal(25, digest.Counts.New);
        Assert.Equal(4, digest.Counts.Updated);
        Assert.Equal(1, digest.Counts.Unchanged);
        Assert.Equal(SampleCsv.ParsedPostings, digest.Counts.New + digest.Counts.Updated + digest.Counts.Unchanged);
    }

    [Fact]
    public void Digest_spots_the_same_job_cross_posted_to_two_boards()
    {
        var digest = Digest();

        // in-0001 and li-0001 are one job under two ids.
        Assert.Equal(1, digest.Counts.CrossSiteDuplicates);
    }

    [Fact]
    public void Digest_breaks_postings_down_by_board()
    {
        var digest = Digest();

        Assert.Equal(18, digest.BySite["linkedin"]);
        Assert.Equal(10, digest.BySite["indeed"]);
        Assert.Equal(2, digest.BySite["google"]);
        Assert.Equal(SampleCsv.ParsedPostings, digest.BySite.Values.Sum());
    }

    [Fact]
    public void Digest_counts_a_multi_valued_job_type_toward_every_value_it_names()
    {
        var digest = Digest();

        Assert.Equal(26, digest.ByJobType["fulltime"]);
        Assert.Equal(3, digest.ByJobType["contract"]);
        Assert.Equal(1, digest.ByJobType["parttime"]);
        Assert.Equal(1, digest.ByJobType["unspecified"]);
    }

    [Fact]
    public void Digest_reports_the_remote_share()
    {
        var digest = Digest();

        Assert.Equal(6, digest.Remote.Remote);
        Assert.Equal(24, digest.Remote.OnSite);
        Assert.Equal(0.2, digest.Remote.RemoteShare);
    }

    [Fact]
    public void Digest_measures_freshness_against_the_scrape_date_not_today()
    {
        var digest = Digest();

        Assert.Equal(12, digest.Freshness.WithDatePosted);
        Assert.Equal(18, digest.Freshness.MissingDatePosted);
        Assert.Equal(0.4, digest.Freshness.Coverage);

        // Six postings carry the scrape date itself.
        Assert.Equal(6, digest.Freshness.PostedToday);

        // 2026-08-09 and 2026-08-10 are more than seven days before 2026-08-18.
        Assert.Equal(2, digest.Freshness.OlderThanSevenDays);
    }

    [Fact]
    public void Digest_reports_zero_salary_coverage_without_inventing_numbers()
    {
        var digest = Digest();

        Assert.Equal(0, digest.Salary.WithSalary);
        Assert.Equal(0, digest.Salary.Coverage);
        Assert.Null(digest.Salary.MinAnnual);
        Assert.Null(digest.Salary.MedianAnnual);
        Assert.Null(digest.Salary.MaxAnnual);
        Assert.Empty(digest.Salary.ByCurrency);
    }

    [Fact]
    public void Digest_ranks_companies_and_locations_by_volume()
    {
        var digest = Digest();

        Assert.Equal(new NamedCount("Northwind Labs", 6), digest.TopCompanies[0]);
        Assert.Equal(new NamedCount("Contoso Systems", 5), digest.TopCompanies[1]);

        // "London, ENG, GB" collapses to city + country for grouping.
        Assert.Equal("London, GB", digest.TopLocations[0].Name);
    }

    [Fact]
    public void Digest_surfaces_title_keywords_as_a_demand_signal()
    {
        var digest = Digest();

        var engineer = Assert.Single(digest.TitleKeywords, k => k.Name == "engineer");

        // All but "Solutions Architect", "Engineering Manager" and "Backend Developer".
        // Note "Engineering" is a distinct token, so it does not count toward "engineer".
        Assert.Equal(27, engineer.Count);

        // Noise words are filtered out.
        Assert.DoesNotContain(digest.TitleKeywords, k => k.Name is "to" or "the" or "and");
    }

    [Fact]
    public void Digest_carries_the_fill_rates_through_from_the_parser()
    {
        var digest = Digest();

        Assert.Equal(0.0, digest.FieldFillRates["min_amount"]);
        Assert.Equal(1.0, digest.FieldFillRates["site"]);
    }

    [Fact]
    public void Document_id_is_derived_from_the_blob_path_so_reprocessing_upserts()
    {
        var first = MetricsCalculator.DocumentId(BlobPath);
        var again = MetricsCalculator.DocumentId(BlobPath);
        var other = MetricsCalculator.DocumentId("jobs/other_2026-08-18T20-30-01Z.csv");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.StartsWith("run|", first);
    }

    [Fact]
    public void Digest_of_an_empty_run_produces_zeroes_rather_than_dividing_by_zero()
    {
        using var empty = new MemoryStream("id,site,title\n"u8.ToArray());
        var parsed = new JobCsvParser().Parse(empty);
        var context = BlobNameParser.Parse(BlobPath, DateTimeOffset.UnixEpoch);

        var digest = new MetricsCalculator().Calculate(context, parsed, UpsertOutcome.Empty, 0);

        Assert.Equal(0, digest.Counts.Parsed);
        Assert.Equal(0, digest.Remote.RemoteShare);
        Assert.Equal(0, digest.Freshness.Coverage);
        Assert.Null(digest.Freshness.MedianAgeDays);
        Assert.Equal(new LengthStats(0, 0, 0), digest.DescriptionLength);
    }
}
