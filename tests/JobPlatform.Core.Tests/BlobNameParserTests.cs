using JobPlatform.Core.Parsing;
using Xunit;

namespace JobPlatform.Core.Tests;

public sealed class BlobNameParserTests
{
    private static readonly DateTimeOffset Fallback = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("jobs/software-engineer_2026-08-18T20-30-01Z.csv", "software-engineer")]
    [InlineData("jobs/data-scientist_2026-08-18T20-30-01Z.csv", "data-scientist")]
    // The slug itself may contain underscores; only the last one separates the timestamp.
    [InlineData("jobs/senior_backend_dev_2026-08-18T20-30-01Z.csv", "senior_backend_dev")]
    public void Parse_recovers_the_search_term_from_the_scraper_naming_convention(
        string blobPath, string expectedTerm)
    {
        var context = BlobNameParser.Parse(blobPath, Fallback);

        Assert.Equal(expectedTerm, context.SearchTerm);
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 20, 30, 1, TimeSpan.Zero), context.ScrapedAtUtc);
        Assert.Equal(new DateOnly(2026, 8, 18), context.ScrapeDate);
        Assert.Equal(blobPath, context.BlobPath);
    }

    [Theory]
    [InlineData("jobs/no-timestamp-here.csv")]
    [InlineData("jobs/software-engineer_not-a-date.csv")]
    public void Parse_falls_back_instead_of_failing_when_the_name_is_unrecognised(string blobPath)
    {
        // An unexpected file name must not cost us the ingest.
        var context = BlobNameParser.Parse(blobPath, Fallback);

        Assert.Equal(Fallback, context.ScrapedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(context.SearchTerm));
    }

    [Fact]
    public void Parse_handles_a_blob_name_with_no_directory_prefix()
    {
        var context = BlobNameParser.Parse("software-engineer_2026-08-18T20-30-01Z.csv", Fallback);

        Assert.Equal("software-engineer", context.SearchTerm);
    }

    [Fact]
    public void Parse_rejects_a_blank_path()
    {
        Assert.Throws<ArgumentException>(() => BlobNameParser.Parse("  ", Fallback));
    }
}
