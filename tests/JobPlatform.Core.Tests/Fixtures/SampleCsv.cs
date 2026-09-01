namespace JobPlatform.Core.Tests.Fixtures;

/// <summary>
/// Access to the synthetic fixture. Every value in it is fabricated — no scraped company,
/// description, or contact data is committed to this public repository. The two email
/// addresses use example.com, which RFC 2606 reserves and nobody can own.
/// </summary>
/// <remarks>
/// The shape mirrors real JobSpy output: 46 columns, ragged rows, Python-style booleans,
/// multi-valued <c>job_type</c>, and — importantly — <b>empty salary columns throughout</b>.
/// That last one is not an oversight in the fixture; it is the situation this deployment is
/// actually in, and it is why the salary figures live in the description prose instead.
///
/// The six freehire rows all leave <c>is_remote</c> empty while filling
/// <c>work_from_home_type</c>. That pairing is the case the nullable column exists for, and
/// it is chosen so the remote counts and the 0.2 remote share are unchanged by their
/// addition — they contribute to "not stated" instead.
///
/// Counts here are derived from the fixture specification, not from running the parser over
/// it. That is what makes the metric assertions a check rather than a restatement.
/// </remarks>
internal static class SampleCsv
{
    /// <summary>Data rows in the file, including the duplicate and the two unparseable ones.</summary>
    public const int RowsInFile = 39;

    /// <summary>Rows that survive parsing and in-file deduplication.</summary>
    public const int ParsedPostings = 36;

    /// <summary>Rows contributed by the freehire board, which publishes structured skills.</summary>
    public const int FreehireRows = 6;

    /// <summary>Rows whose <c>is_remote</c> is empty — freehire says nothing unless it knows.</summary>
    public const int RemoteNotStated = 6;

    /// <summary>
    /// Parsed postings per site, and how many of each carry no <c>job_url_direct</c>.
    /// </summary>
    /// <remarks>
    /// The apply-link split, which is what says whether the employer or the board hosts the
    /// application. Derived from the fixture specification like every other count here: the
    /// three rows the parser drops are all <c>indeed</c> — one in-file duplicate and two
    /// unparseable — so indeed contributes 10 of its 13 rows and the other sites contribute
    /// all of theirs. The four numbers sum to <see cref="ParsedPostings"/>, which is the check.
    ///
    /// <b>The freehire figure is a property of the fixture, not of freehire.</b> The real
    /// scraper sets <c>job_url_direct</c> to the hit's own URL unconditionally, so a live
    /// freehire run is always 0 board-hosted. The fixture leaves the column empty, which is
    /// what makes it a useful third case here rather than a copy of the other two.
    /// </remarks>
    public const int LinkedInPostings = 18;
    public const int LinkedInBoardHosted = 10;
    public const int IndeedPostings = 10;
    public const int IndeedBoardHosted = 6;

    public static Stream Open()
        => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jobs-sample.csv"));
}
