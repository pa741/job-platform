using System.Text;
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

        Assert.Equal(6, result.Postings.Count(p => p.IsRemote == true));
        Assert.Equal(24, result.Postings.Count(p => p.IsRemote == false));
    }

    [Fact]
    public void Parse_leaves_absent_optional_values_null_rather_than_guessing()
    {
        var result = ParseFixture();

        // 12 from the scraped boards, plus all six freehire rows, which always date theirs.
        Assert.Equal(18, result.Postings.Count(p => p.DatePosted is not null));
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

        // Two rows carry a synthetic address at example.com, which RFC 2606 reserves so it
        // can never belong to a real person. Enough to prove the column is read; the parser
        // keeps only the boolean derived from it, never the address.
        Assert.Equal(2d / SampleCsv.RowsInFile, result.FieldFillRates["emails"], 4);

        // The fork columns are populated only on the freehire rows, which is what a
        // board-specific signal looks like: low but non-zero, not silently absent.
        Assert.Equal(
            (double)SampleCsv.FreehireRows / SampleCsv.RowsInFile,
            result.FieldFillRates["source_board"],
            4);

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

    /// <summary>
    /// Two rows in the shape the scraper actually writes: freehire fills the freshness
    /// columns, a scraped board leaves them empty. Written inline rather than added to
    /// the shared fixture, which the row-count and fill-rate assertions above pin.
    /// </summary>
    /// <summary>
    /// The apply route, which is three-state and has to stay that way.
    /// </summary>
    /// <remarks>
    /// Inline rather than in the shared fixture: that file's counts are known by construction and
    /// every metric assertion reads them, so a column added to it is a change to unrelated tests.
    ///
    /// The empty cell is the case that matters. <c>offsite_apply</c> is absent for every posting
    /// scraped before the scraper emitted it and for every board that does not say, and reading
    /// that as "the board hosts it" is precisely the fault the column was added to fix - on a
    /// live corpus it labelled 4,470 LinkedIn postings Easy Apply.
    /// </remarks>
    [Fact]
    public void Offsite_apply_is_read_as_three_states_and_an_empty_cell_stays_null()
    {
        const string csv = """
            id,site,title,company,job_url,job_url_direct,offsite_apply
            li-a,linkedin,Backend Engineer,Northwind,https://li/1,,True
            li-b,linkedin,Frontend Engineer,Contoso,https://li/2,,False
            li-c,linkedin,Platform Engineer,Fabrikam,https://li/3,,
            in-d,indeed,Data Engineer,Adventure,https://in/4,https://ats.example.invalid/4,
            """;

        var result = new JobCsvParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        var byId = result.Postings.ToDictionary(p => p.ExternalId);

        Assert.True(byId["li-a"].OffsiteApply);
        Assert.False(byId["li-b"].OffsiteApply);
        Assert.Null(byId["li-c"].OffsiteApply);

        // A direct URL and no flag: the URL is the older, stronger signal and the flag stays
        // absent rather than being back-filled from it. Deciding what that pair means belongs to
        // the consumer, not to the parser.
        Assert.Null(byId["in-d"].OffsiteApply);
        Assert.Equal("https://ats.example.invalid/4", byId["in-d"].JobUrlDirect);
    }

    /// <summary>A column the scraper has not shipped yet must not break the parse.</summary>
    /// <remarks>
    /// The parser reads by name and ignores what it does not model, which is what lets a fork
    /// add columns without a coordinated release. The reverse has to hold too: the deployed
    /// scraper does not emit <c>offsite_apply</c> yet, and every posting already in the corpus
    /// was written without it.
    /// </remarks>
    [Fact]
    public void A_csv_without_the_offsite_column_parses_with_the_flag_absent()
    {
        const string csv = """
            id,site,title,company,job_url
            li-a,linkedin,Backend Engineer,Northwind,https://li/1
            """;

        var result = new JobCsvParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv)));

        Assert.Null(Assert.Single(result.Postings).OffsiteApply);
    }

    private static CsvParseResult ParseFreehireRows()
    {
        const string csv = """
            id,site,title,company,summary,freshness_class,posting_age_days,repost_count,fake_freshness,experience_range,company_num_employees
            fh-a,freehire,Backend Engineer,Northwind,Builds the ledger.,stale,65,3,False,3+ Yrs,51-200
            in-b,indeed,Frontend Developer,Contoso,,,,,,,
            """;

        return new JobCsvParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
    }

    [Fact]
    public void Parse_reads_the_freehire_freshness_columns()
    {
        var posting = Assert.Single(ParseFreehireRows().Postings, p => p.Site == "freehire");

        Assert.Equal("stale", posting.FreshnessClass);
        Assert.Equal(65, posting.PostingAgeDays);
        Assert.Equal(3, posting.RepostCount);
        Assert.Equal("Builds the ledger.", posting.Summary);
        Assert.Equal("3+ Yrs", posting.ExperienceRange);
        Assert.Equal("51-200", posting.CompanyNumEmployees);
    }

    [Fact]
    public void Parse_keeps_a_stated_false_distinct_from_an_unchecked_posting()
    {
        var postings = ParseFreehireRows().Postings;

        // freehire looked and found nothing suspect.
        Assert.False(Assert.Single(postings, p => p.Site == "freehire").FakeFreshness);

        // Indeed never looked. Reading this as false would let every scraped row assert
        // a verdict it never made - the whole reason the column is nullable.
        Assert.Null(Assert.Single(postings, p => p.Site == "indeed").FakeFreshness);
    }

    [Fact]
    public void SourceKey_combines_board_and_site_local_id()
    {
        var result = ParseFixture();

        var posting = Assert.Single(result.Postings, p => p.ExternalId == "in-0001");
        Assert.Equal("indeed:in-0001", posting.SourceKey);
    }
}
