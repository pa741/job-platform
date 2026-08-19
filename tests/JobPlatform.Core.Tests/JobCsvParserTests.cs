using JobPlatform.Core.Parsing;
using JobPlatform.Core.Tests.Fixtures;
using Xunit;

namespace JobPlatform.Core.Tests;

public sealed class JobCsvParserTests
{
    private static CsvParseResult ParseFixture()
    {
        using var stream = SampleCsv.Open();
        return new JobCsvParser().Parse(stream);
    }

    [Fact]
    public void Parse_counts_every_data_row_including_the_ones_it_drops()
    {
        var result = ParseFixture();

        Assert.Equal(SampleCsv.RowsInFile, result.RowsInFile);
        Assert.Equal(SampleCsv.ParsedPostings, result.Postings.Count);
    }

    [Fact]
    public void Parse_drops_rows_that_cannot_be_keyed_without_failing_the_run()
    {
        var result = ParseFixture();

        // One row has no id, one has no title (and is ragged, stopping at column 20).
        Assert.Equal(2, result.InvalidRows);
    }

    [Fact]
    public void Parse_drops_exact_repeats_of_a_posting_already_seen_in_the_file()
    {
        var result = ParseFixture();

        Assert.Equal(1, result.DuplicateRows);
        Assert.Equal(
            result.Postings.Count,
            result.Postings.Select(p => p.SourceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Parse_keeps_descriptions_containing_newlines_quotes_and_commas_intact()
    {
        var result = ParseFixture();

        var tricky = Assert.Single(result.Postings, p => p.ExternalId == "li-0017");

        Assert.Contains('\n', tricky.Description!);
        Assert.Contains("\"reliability\"", tricky.Description);
        Assert.Contains("comma, separated, prose", tricky.Description);

        // The embedded newlines must not have been read as row terminators.
        Assert.Equal(SampleCsv.ParsedPostings, result.Postings.Count);
    }

    [Fact]
    public void Parse_reads_python_style_booleans()
    {
        var result = ParseFixture();

        Assert.Equal(6, result.Postings.Count(p => p.IsRemote));
        Assert.Equal(24, result.Postings.Count(p => !p.IsRemote));
    }

    [Fact]
    public void Parse_leaves_absent_optional_values_null_rather_than_guessing()
    {
        var result = ParseFixture();

        Assert.Equal(12, result.Postings.Count(p => p.DatePosted is not null));
        Assert.All(result.Postings, p =>
        {
            Assert.Null(p.MinAmount);
            Assert.Null(p.MaxAmount);
            Assert.Null(p.Currency);
        });
    }

    [Fact]
    public void Parse_reports_fill_rates_that_expose_a_silently_degraded_scraper()
    {
        var result = ParseFixture();

        // Every row identifies its board.
        Assert.Equal(1.0, result.FieldFillRates["site"]);

        // Salary was empty in every row of the real London run too. A column at 0.0 is
        // exactly the signal this metric exists to surface.
        Assert.Equal(0.0, result.FieldFillRates["min_amount"]);
        Assert.Equal(0.0, result.FieldFillRates["currency"]);

        // No scraped contact data in the fixture, by design.
        Assert.Equal(0.0, result.FieldFillRates["emails"]);

        Assert.Equal(JobCsvParser.TrackedColumns.Length, result.FieldFillRates.Count);
    }

    [Fact]
    public void Parse_returns_empty_for_a_headerless_stream_instead_of_throwing()
    {
        using var empty = new MemoryStream();

        var result = new JobCsvParser().Parse(empty);

        Assert.Empty(result.Postings);
        Assert.Equal(0, result.RowsInFile);
    }

    [Fact]
    public void SourceKey_combines_board_and_site_local_id()
    {
        var result = ParseFixture();

        var posting = Assert.Single(result.Postings, p => p.ExternalId == "in-0001");
        Assert.Equal("indeed:in-0001", posting.SourceKey);
    }
}
