using JobPlatform.Core.Curated;
using Parquet.Serialization;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// That the curated row shapes survive a Parquet round trip.
/// </summary>
/// <remarks>
/// Worth pinning because the failure is remote and slow to find. These files are written by a
/// timer function into a container nothing else reads, and are consumed by DuckDB or pandas
/// weeks later — a nullable type the serialiser cannot express would surface as a column of
/// nulls in someone's notebook, long after the data that produced it stopped existing.
///
/// The nullable columns are the point. Almost every interesting field here is optional:
/// <c>is_remote</c> is null when the board said nothing, salary is null for most of the
/// corpus, and reading either back as a default would reintroduce exactly the "silence is a
/// verdict" bug the schema was changed to remove.
/// </remarks>
public sealed class CuratedRowTests
{
    private static async Task<IReadOnlyList<T>> RoundTripAsync<T>(params T[] rows) where T : class, new()
    {
        using var buffer = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, buffer);

        buffer.Position = 0;

        // Parquet.Net 6 returns a DeserializationResult<T>, which carries the rows plus the
        // file metadata. Only the rows matter here.
        var result = await ParquetSerializer.DeserializeAsync<T>(buffer);

        return [.. result.Data];
    }

    [Fact]
    public async Task A_fully_populated_posting_row_survives()
    {
        var written = new CuratedPosting
        {
            source_key = "indeed:in-0001",
            site = "indeed",
            source_board = "greenhouse",
            title = "Senior Backend Engineer",
            company = "Northwind Labs Ltd",
            company_key = "northwind labs",
            location_city = "London",
            location_country = "GB",
            date_posted = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc),
            first_seen_utc = new DateTime(2026, 8, 18, 20, 30, 0, DateTimeKind.Utc),
            last_seen_utc = new DateTime(2026, 8, 20, 20, 30, 0, DateTimeKind.Utc),
            seen_count = 3,
            seniority = 4,
            seniority_name = "Senior",
            role_family = 1,
            role_family_name = "Backend",
            work_arrangement = 2,
            work_arrangement_name = "Hybrid",
            hybrid_days_in_office = 3,
            is_remote = false,
            years_experience_min = 5,
            annual_salary_min = 75_000m,
            annual_salary_max = 95_000m,
            annual_salary_currency = "GBP",
            salary_from_text = true,
            salary_stated_interval = "yearly",
            visa_sponsorship = true,
            requires_security_clearance = false,
            requires_degree = true,
            ir35 = "outside",
            job_types = "fulltime|contract",
            concept_keys = "skill.csharp|skill.kubernetes",
            domain_keys = "area.backend|type.language",
            tags = "holiday-days=25|equity",
            description_length = 2400,
            has_contact_email = true,
            enrichment_version = 1,
            search_term = "software-engineer",
        };

        var read = Assert.Single(await RoundTripAsync(written));

        Assert.Equal(written.source_key, read.source_key);
        Assert.Equal(written.company_key, read.company_key);
        Assert.Equal(written.annual_salary_min, read.annual_salary_min);
        Assert.Equal(written.hybrid_days_in_office, read.hybrid_days_in_office);
        Assert.Equal(written.is_remote, read.is_remote);
        Assert.Equal(written.concept_keys, read.concept_keys);
        Assert.Equal(written.domain_keys, read.domain_keys);
        Assert.Equal(written.ir35, read.ir35);
        Assert.Equal(written.last_seen_utc, read.last_seen_utc);
    }

    [Fact]
    public async Task Nulls_come_back_as_nulls_rather_than_defaults()
    {
        // The distinction the whole nullable-IsRemote change exists to preserve. If Parquet
        // read this back as false, the curated zone would quietly reassert that silence means
        // "not remote" - the exact bug, reintroduced one layer further out.
        var written = new CuratedPosting
        {
            source_key = "freehire:fh-004",
            site = "freehire",
            title = "Python Developer",
            search_term = "software-engineer",
            seniority_name = "Unknown",
            role_family_name = "Unknown",
            work_arrangement_name = "Unknown",
            is_remote = null,
            annual_salary_min = null,
            annual_salary_max = null,
            hybrid_days_in_office = null,
            visa_sponsorship = null,
            date_posted = null,
            concept_keys = null,
        };

        var read = Assert.Single(await RoundTripAsync(written));

        Assert.Null(read.is_remote);
        Assert.Null(read.annual_salary_min);
        Assert.Null(read.hybrid_days_in_office);
        Assert.Null(read.visa_sponsorship);
        Assert.Null(read.date_posted);
        Assert.Null(read.concept_keys);
    }

    [Fact]
    public async Task A_pair_row_survives()
    {
        var written = new CuratedPair
        {
            source_key = "indeed:in-0001",
            title = "Senior Backend Engineer",
            seniority = 4,
            role_family_name = "Backend",
            concept_key = "skill.kubernetes",
            concept_label = "Kubernetes",
            concept_kind = "Skill",
            source = "Taxonomy",
            polarity = 3,
            years_min = 2,
            confidence = 0.85,
            evidence = "k8s",
            last_seen_utc = new DateTime(2026, 8, 20, 20, 30, 0, DateTimeKind.Utc),
        };

        var read = Assert.Single(await RoundTripAsync(written));

        Assert.Equal(written.concept_key, read.concept_key);
        Assert.Equal(written.source, read.source);
        Assert.Equal(written.polarity, read.polarity);
        Assert.Equal(written.years_min, read.years_min);
        Assert.Equal(written.confidence, read.confidence);
        Assert.Equal(written.evidence, read.evidence);
        Assert.Null(read.years_max);
    }

    [Fact]
    public async Task Many_rows_round_trip_in_one_file()
    {
        // A partition is thousands of rows, not one. Parquet writes in row groups and a
        // single-row test would not exercise that at all.
        var rows = Enumerable.Range(0, 5_000)
            .Select(i => new CuratedPair
            {
                source_key = $"indeed:in-{i:D5}",
                title = "Software Engineer",
                concept_key = "skill.python",
                concept_label = "Python",
                concept_kind = "Skill",
                source = "Taxonomy",
                polarity = i % 4,
                years_min = i % 2 == 0 ? i % 10 : null,
                last_seen_utc = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
            })
            .ToArray();

        var read = await RoundTripAsync(rows);

        Assert.Equal(rows.Length, read.Count);
        Assert.Equal(rows[4_999].source_key, read[4_999].source_key);
        Assert.Null(read[1].years_min);
    }
}
