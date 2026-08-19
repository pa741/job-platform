namespace JobPlatform.Core.Tests.Fixtures;

/// <summary>
/// Access to the synthetic fixture. Every value in it is fabricated — no scraped
/// company, description, or contact data is committed to this public repository.
/// The shape (34 columns, empty salary columns, 40% date coverage) mirrors real
/// JobSpy output; see <c>jobs-sample.csv</c>.
/// </summary>
internal static class SampleCsv
{
    /// <summary>Data rows in the file, including the duplicate and the two unparseable ones.</summary>
    public const int RowsInFile = 33;

    /// <summary>Rows that survive parsing and in-file deduplication.</summary>
    public const int ParsedPostings = 30;

    public static Stream Open()
        => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jobs-sample.csv"));
}
