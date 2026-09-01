using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using JobPlatform.Core.Model;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Core.Parsing;

/// <summary>
/// Reads a JobSpy CSV export into <see cref="JobPosting"/> records.
/// </summary>
/// <remarks>
/// Written against the real 34-column export. It has to survive things a strict parser
/// would reject: descriptions containing raw newlines and escaped quotes, Python-style
/// <c>True</c>/<c>False</c> booleans, columns that are empty in every single row, and
/// <c>job_type</c> holding several comma-separated values. A row that still cannot be
/// parsed is counted and skipped — one malformed posting must never cost us the run.
/// </remarks>
public sealed class JobCsvParser(ILogger<JobCsvParser>? logger = null)
{
    /// <summary>Columns whose fill rate is tracked. A column silently dropping to 0% is the
    /// earliest signal that a board changed its markup and the scraper degraded.</summary>
    public static readonly string[] TrackedColumns =
    [
        "id", "site", "job_url", "job_url_direct", "title", "company", "location",
        "date_posted", "job_type", "salary_source", "interval", "min_amount", "max_amount",
        "currency", "is_remote", "job_level", "job_function", "listing_type", "emails",
        "description", "company_industry", "company_url", "company_logo",
        "company_url_direct", "company_addresses", "company_num_employees",
        "company_revenue", "company_description", "skills", "experience_range",
        "company_rating", "company_reviews_count", "vacancy_count", "work_from_home_type",
        "applicants", "applicant_count",
        "source_board", "summary", "freshness_class", "posting_age_days",
        "repost_count", "fake_freshness",
        "experience_years_min", "experience_years_max", "attributes", "date_on_indeed",
    ];

    private static readonly CsvConfiguration Configuration = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        // Real exports have ragged rows and columns we do not model; neither is an error.
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = null,
        TrimOptions = TrimOptions.Trim,
        DetectColumnCountChanges = false,
    };

    public CsvParseResult Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, leaveOpen: true);
        using var csv = new CsvReader(reader, Configuration);

        var postings = new List<JobPosting>();
        var seenSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fillCounts = TrackedColumns.ToDictionary(c => c, _ => 0, StringComparer.OrdinalIgnoreCase);

        int rowsInFile = 0, invalid = 0, duplicates = 0;

        if (!csv.Read() || !csv.ReadHeader())
        {
            logger?.LogWarning("CSV had no header record; treating as empty.");
            return new CsvParseResult([], 0, 0, 0, TrackedColumns.ToDictionary(c => c, _ => 0d));
        }

        var presentColumns = new HashSet<string>(
            csv.HeaderRecord ?? [], StringComparer.OrdinalIgnoreCase);

        while (csv.Read())
        {
            rowsInFile++;

            foreach (var column in TrackedColumns)
            {
                if (presentColumns.Contains(column) && !string.IsNullOrWhiteSpace(Field(csv, column)))
                {
                    fillCounts[column]++;
                }
            }

            try
            {
                var posting = ReadPosting(csv);
                if (posting is null)
                {
                    invalid++;
                    continue;
                }

                if (!seenSourceKeys.Add(posting.SourceKey))
                {
                    duplicates++;
                    continue;
                }

                postings.Add(posting);
            }
            catch (Exception ex) when (ex is CsvHelperException or FormatException or OverflowException)
            {
                invalid++;
                logger?.LogWarning(ex, "Skipping unparseable CSV row {Row}.", rowsInFile);
            }
        }

        var fillRates = fillCounts.ToDictionary(
            kvp => kvp.Key,
            kvp => rowsInFile == 0 ? 0d : Math.Round((double)kvp.Value / rowsInFile, 4));

        logger?.LogInformation(
            "Parsed {Valid} posting(s) from {Rows} row(s); {Invalid} invalid, {Duplicate} in-file duplicate(s).",
            postings.Count, rowsInFile, invalid, duplicates);

        return new CsvParseResult(postings, rowsInFile, invalid, duplicates, fillRates);
    }

    private static JobPosting? ReadPosting(IReaderRow csv)
    {
        var externalId = Field(csv, "id");
        var site = Field(csv, "site");
        var title = Field(csv, "title");

        // Without these three a row cannot be keyed or displayed, so it is not salvageable.
        if (string.IsNullOrWhiteSpace(externalId) ||
            string.IsNullOrWhiteSpace(site) ||
            string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return new JobPosting
        {
            ExternalId = externalId,
            Site = site.ToLowerInvariant(),
            Title = title,
            Company = Field(csv, "company"),
            Location = Field(csv, "location"),
            DatePosted = ParseDate(Field(csv, "date_posted")),
            JobType = Field(csv, "job_type"),
            IsRemote = ParseNullableBool(Field(csv, "is_remote")),
            SalarySource = Field(csv, "salary_source"),
            SalaryInterval = Field(csv, "interval"),
            MinAmount = ParseDecimal(Field(csv, "min_amount")),
            MaxAmount = ParseDecimal(Field(csv, "max_amount")),
            Currency = Field(csv, "currency"),
            JobLevel = Field(csv, "job_level"),
            JobFunction = Field(csv, "job_function"),
            CompanyIndustry = Field(csv, "company_industry"),
            JobUrl = Field(csv, "job_url"),
            JobUrlDirect = Field(csv, "job_url_direct"),
            OffsiteApply = ParseNullableBool(Field(csv, "offsite_apply")),
            CompanyUrl = Field(csv, "company_url"),
            Description = Field(csv, "description"),
            CompanyNumEmployees = Field(csv, "company_num_employees"),
            ExperienceRange = Field(csv, "experience_range"),
            // Added by the fork in v1.1.82-fh5. Tracked for fill rate from the same
            // release the scraper is pinned to, never before it: a tracked column sitting at
            // 0% is warned about on every run, and "not shipped yet" is not the regression
            // that warning exists to catch.
            ExperienceYearsMin = ParseInt(Field(csv, "experience_years_min")),
            ExperienceYearsMax = ParseInt(Field(csv, "experience_years_max")),
            Attributes = ParseList(Field(csv, "attributes")),
            DateOnIndeed = ParseDate(Field(csv, "date_on_indeed")),
            Summary = Field(csv, "summary"),
            FreshnessClass = Field(csv, "freshness_class"),
            PostingAgeDays = ParseInt(Field(csv, "posting_age_days")),
            RepostCount = ParseInt(Field(csv, "repost_count")),
            FakeFreshness = ParseNullableBool(Field(csv, "fake_freshness")),
            Skills = ParseList(Field(csv, "skills")),
            SourceBoard = Field(csv, "source_board"),
            Applicants = Field(csv, "applicants"),
            ApplicantCount = ParseInt(Field(csv, "applicant_count")),
            WorkFromHomeType = Field(csv, "work_from_home_type"),
            ListingType = Field(csv, "listing_type"),
            VacancyCount = ParseInt(Field(csv, "vacancy_count")),
            // The addresses themselves are deliberately not read - see JobPosting.HasContactEmail.
            HasContactEmail = Field(csv, "emails") is not null,
            CompanyDescription = Field(csv, "company_description"),
            CompanyRevenue = Field(csv, "company_revenue"),
            CompanyRating = ParseDouble(Field(csv, "company_rating")),
            CompanyReviewsCount = ParseInt(Field(csv, "company_reviews_count")),
        };
    }

    private static string? Field(IReaderRow csv, string name)
        => csv.TryGetField<string>(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    /// <summary>Comma-joined list column, e.g. <c>skills</c>, back into its parts.</summary>
    private static IReadOnlyList<string> ParseList(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Accepts Python's <c>True</c>/<c>False</c> as well as the usual spellings, and
    /// distinguishes "checked, and it looked fine" from "nobody checked".
    /// </summary>
    /// <remarks>
    /// A false-for-everything default is wrong for every column a board may simply not
    /// answer: it turns silence into a verdict. <c>fake_freshness</c> is only populated by
    /// freehire, and <c>is_remote</c> is null there whenever the work mode could not be
    /// resolved - so both need the third state.
    /// </remarks>
    private static bool? ParseNullableBool(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => null,
    };

    private static double? ParseDouble(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? ParseInt(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static DateOnly? ParseDate(string? raw)
        => DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static decimal? ParseDecimal(string? raw)
        => decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
