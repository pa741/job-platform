using JobPlatform.Core.Model;

namespace JobPlatform.Core.Parsing;

/// <param name="Postings">Rows that parsed cleanly, after within-file deduplication.</param>
/// <param name="RowsInFile">Data rows encountered, including ones that failed.</param>
/// <param name="InvalidRows">Rows dropped because they could not be parsed.</param>
/// <param name="DuplicateRows">Rows dropped as duplicates of an earlier row in the same file.</param>
/// <param name="FieldFillRates">Fraction of rows with a non-empty value, per CSV column.</param>
public sealed record CsvParseResult(
    IReadOnlyList<JobPosting> Postings,
    int RowsInFile,
    int InvalidRows,
    int DuplicateRows,
    IReadOnlyDictionary<string, double> FieldFillRates);
