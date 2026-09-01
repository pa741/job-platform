using System.Text;
using JobPlatform.Core.Enrichment;
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

    /// <summary>The same digest, with the enrichment the pipeline would have supplied.</summary>
    private static RunDigest EnrichedDigest()
    {
        using var stream = SampleCsv.Open();
        var parsed = new JobCsvParser().Parse(stream);
        var context = BlobNameParser.Parse(BlobPath, DateTimeOffset.UnixEpoch, "etag", sizeBytes: 4242);

        return new MetricsCalculator().Calculate(
            context, parsed, new UpsertOutcome(36, 0, 0), durationMs: 1234,
            [.. parsed.Postings.Select(p => PostingEnricher.Enrich(p))]);
    }

    [Fact]
    public void Enrichment_is_empty_rather_than_absent_when_none_is_supplied()
    {
        // The dashboard reads these directly, so "not computed" and "computed as zero" have
        // to look the same - a null section would be a second code path on every chart.
        var enrichment = Digest().Enrichment;

        Assert.Empty(enrichment.BySeniority);
        Assert.Empty(enrichment.TopConcepts);
        Assert.Equal(0, enrichment.SalaryCoverage);
    }

    [Fact]
    public void Enrichment_counts_every_posting_on_every_axis()
    {
        var enrichment = EnrichedDigest().Enrichment;

        // Unknown included, so a share on this axis reads against a visible denominator.
        Assert.Equal(SampleCsv.ParsedPostings, enrichment.BySeniority.Values.Sum());
        Assert.Equal(SampleCsv.ParsedPostings, enrichment.ByWorkArrangement.Values.Sum());
        Assert.Equal(SampleCsv.ParsedPostings, enrichment.ByRoleFamily.Values.Sum());
    }

    [Fact]
    public void Enrichment_reports_the_salary_the_boards_did_not()
    {
        var digest = EnrichedDigest();

        // The fixture's salary columns are empty throughout, exactly as the real corpus is.
        Assert.Equal(0, digest.Salary.Coverage);

        // Everything here came out of a description, which is the whole point.
        Assert.True(digest.Enrichment.SalaryCoverage > 0.5);
        Assert.Equal(1, digest.Enrichment.SalaryFromTextShare);
        Assert.NotNull(digest.Enrichment.MedianAnnualSalary);
    }

    [Fact]
    public void Domains_are_ranked_separately_from_the_concepts_beneath_them()
    {
        var enrichment = EnrichedDigest().Enrichment;

        Assert.All(enrichment.TopConcepts, c => Assert.DoesNotContain("area.", c.Name));
        Assert.All(enrichment.TopDomains, d => Assert.StartsWith("area.", d.Name));

        // The rollup is never smaller than its largest member: every posting naming a
        // backend skill counts toward the area, and most name several.
        var backend = enrichment.TopDomains.FirstOrDefault(d => d.Name == "area.backend");
        Assert.NotNull(backend);
        Assert.True(backend.Count > 0);
    }

    [Fact]
    public void Unresolved_mentions_are_reported_rather_than_hidden()
    {
        // The size of the vocabulary's blind spot, knowable only because unresolved forms
        // are recorded instead of dropped.
        var enrichment = EnrichedDigest().Enrichment;

        Assert.True(enrichment.UnresolvedMentions > 0);

        // And what is in it. Asserting only the count is how a dashboard ends up announcing
        // a number it cannot show - the count says how big the gap is, the list says what is
        // in it, and only the second one can be acted on.
        Assert.NotEmpty(enrichment.TopUnresolved);
        Assert.All(enrichment.TopUnresolved, u =>
        {
            Assert.False(string.IsNullOrWhiteSpace(u.Form));
            Assert.True(u.Count > 0);
        });

        // Ranked, because the list exists to be read in priority order.
        Assert.Equal(
            enrichment.TopUnresolved.OrderByDescending(u => u.Count).Select(u => u.Count),
            enrichment.TopUnresolved.Select(u => u.Count));
    }

    [Fact]
    public void One_word_written_two_ways_is_one_entry()
    {
        // "Go" and "go" are the same problem written twice. Splitting them would put the
        // same word in two rows at half the weight each, which is how a to-do list stops
        // looking urgent.
        var forms = EnrichedDigest().Enrichment.TopUnresolved.Select(u => u.Form).ToList();

        Assert.Equal(forms.Count, forms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void An_unresolved_form_says_whether_it_needs_context_or_vocabulary()
    {
        // The two halves need opposite responses: an ambiguous form is a word the vocabulary
        // already knows and distrusts, and adding an entry for it would be exactly wrong.
        var reasons = EnrichedDigest().Enrichment.TopUnresolved
            .Select(u => u.Reason)
            .Distinct()
            .ToList();

        Assert.All(reasons, r => Assert.Contains(
            r, new[] { "Ambiguous", "UnknownBoardSkill", "UnknownModelSkill" }));

        Assert.Contains("Ambiguous", reasons);
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
        var digest = Digest(new UpsertOutcome(New: 31, Updated: 4, Unchanged: 1));

        Assert.Equal(SampleCsv.RowsInFile, digest.Counts.RowsInFile);
        Assert.Equal(SampleCsv.ParsedPostings, digest.Counts.Parsed);
        Assert.Equal(2, digest.Counts.Invalid);
        Assert.Equal(1, digest.Counts.InFileDuplicates);
        Assert.Equal(31, digest.Counts.New);
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
        Assert.Equal(SampleCsv.FreehireRows, digest.BySite["freehire"]);
        Assert.Equal(SampleCsv.ParsedPostings, digest.BySite.Values.Sum());
    }

    [Fact]
    public void Digest_counts_a_multi_valued_job_type_toward_every_value_it_names()
    {
        var digest = Digest();

        Assert.Equal(32, digest.ByJobType["fulltime"]);
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

        // The freehire rows state no work mode at all, so they land here rather than being
        // counted as office-based. That is the distinction the nullable column exists for.
        Assert.Equal(SampleCsv.RemoteNotStated, digest.Remote.NotStated);

        // Six of the thirty that stated one - the share must not be diluted by the six that
        // said nothing, or it would move whenever coverage moved.
        Assert.Equal(0.2, digest.Remote.RemoteShare);
    }

    [Fact]
    public void Digest_measures_freshness_against_the_scrape_date_not_today()
    {
        var digest = Digest();

        Assert.Equal(18, digest.Freshness.WithDatePosted);
        Assert.Equal(18, digest.Freshness.MissingDatePosted);
        Assert.Equal(0.5, digest.Freshness.Coverage);

        // Six scraped postings carry the scrape date itself, and all six freehire rows do -
        // that board publishes a date on everything, which is most of why its coverage is
        // worth measuring separately.
        Assert.Equal(12, digest.Freshness.PostedToday);

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

    /// <summary>
    /// The apply-link split, per site, which is the only place a broken selector shows up.
    /// </summary>
    /// <remarks>
    /// Asserted against counts derived from the fixture rather than read back out of the
    /// calculator, so this is a check on the arithmetic rather than a restatement of it. The
    /// per-site shape is the point: a whole-run rate would average boards that behave
    /// differently and would never reach the boundary the digest warns on.
    /// </remarks>
    [Fact]
    public void Apply_links_are_counted_per_site()
    {
        var links = Digest().ApplyLinks;

        var linkedin = Assert.Single(links, l => l.Site == "linkedin");
        Assert.Equal(SampleCsv.LinkedInPostings, linkedin.Postings);
        Assert.Equal(SampleCsv.LinkedInBoardHosted, linkedin.BoardHosted);

        var indeed = Assert.Single(links, l => l.Site == "indeed");
        Assert.Equal(SampleCsv.IndeedPostings, indeed.Postings);
        Assert.Equal(SampleCsv.IndeedBoardHosted, indeed.BoardHosted);

        // Every parsed posting is accounted for on exactly one site, which is what stops a
        // site being silently dropped from the breakdown the warning reads.
        Assert.Equal(SampleCsv.ParsedPostings, links.Sum(l => l.Postings));

        // Largest first, so two runs over the same sites produce the same document.
        Assert.Equal(links.OrderByDescending(l => l.Postings).Select(l => l.Site), links.Select(l => l.Site));
    }

    /// <summary>
    /// The number the digest warns on, and why it is not the board-hosted share.
    /// </summary>
    /// <remarks>
    /// <b>Written after the first version of that warning turned out to be permanent noise.</b>
    /// It alarmed at a 98% board-hosted share, which was right on the day LinkedIn's apply-URL
    /// selector broke and wrong forever afterwards: LinkedIn stopped publishing apply URLs at
    /// all, so that share is pinned at 100% there and the warning would have fired on every
    /// ingest for the rest of time.
    ///
    /// A missing URL is now an ordinary state. Saying nothing either way is not - so the alarm
    /// moved to the route being unestablished, which a working board never reports.
    /// </remarks>
    [Fact]
    public void A_posting_with_no_url_but_a_known_route_is_not_counted_as_unknown()
    {
        const string csv = """
            id,site,title,company,job_url,job_url_direct,offsite_apply
            li-1,linkedin,Backend Engineer,Northwind,https://li/1,,True
            li-2,linkedin,Frontend Engineer,Contoso,https://li/2,,False
            li-3,linkedin,Platform Engineer,Fabrikam,https://li/3,,
            """;

        var parsed = new JobCsvParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        var context = BlobNameParser.Parse(BlobPath, DateTimeOffset.UnixEpoch);

        var link = Assert.Single(
            new MetricsCalculator().Calculate(context, parsed, UpsertOutcome.Empty, 0).ApplyLinks);

        // All three lack a URL, which is now permanent on LinkedIn and must not alarm.
        Assert.Equal(3, link.BoardHosted);
        Assert.Equal(1.0, link.BoardHostedShare);

        // Only the third says nothing at all about how to apply. That is the one worth warning on.
        Assert.Equal(1, link.RouteUnknown);
        Assert.Equal(1.0 / 3, link.RouteUnknownShare, precision: 6);
    }

    [Fact]
    public void A_site_that_answers_nothing_is_entirely_route_unknown()
    {
        // The state the corpus is in today, before the scraper that emits offsite_apply reaches
        // the NAS: no links and no flags. The warning should fire here, and stop once it ships.
        const string csv = """
            id,site,title,company,job_url
            li-1,linkedin,Backend Engineer,Northwind,https://li/1
            li-2,linkedin,Frontend Engineer,Contoso,https://li/2
            """;

        var parsed = new JobCsvParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        var context = BlobNameParser.Parse(BlobPath, DateTimeOffset.UnixEpoch);

        var link = Assert.Single(
            new MetricsCalculator().Calculate(context, parsed, UpsertOutcome.Empty, 0).ApplyLinks);

        Assert.Equal(1.0, link.RouteUnknownShare);
    }

    /// <summary>
    /// A share, not a count, because that is what the digest's warning is keyed on.
    /// </summary>
    /// <remarks>
    /// Zero postings has to answer zero rather than throw. An empty site cannot occur in a run
    /// the parser produced, but the record is public and the digest is read back out of Cosmos
    /// by code that did not build it.
    /// </remarks>
    [Fact]
    public void Board_hosted_share_is_derived_and_survives_an_empty_site()
    {
        Assert.Equal(0.5, new ApplyLinkCount("linkedin", 20, 10, 0).BoardHostedShare);
        Assert.Equal(0, new ApplyLinkCount("linkedin", 0, 0, 0).BoardHostedShare);
        Assert.Equal(0.25, new ApplyLinkCount("linkedin", 20, 20, 5).RouteUnknownShare);
        Assert.Equal(0, new ApplyLinkCount("linkedin", 0, 0, 0).RouteUnknownShare);
    }

    /// <summary>
    /// The same job on two boards, written the way two boards actually write it.
    /// </summary>
    /// <remarks>
    /// <b>The shared fixture cannot catch this and that is why the bug survived.</b> Its one
    /// cross-board duplicate carries an identical location string on both rows, so the old
    /// metric - which hashed the raw location - matched it and the assertion passed. Real boards
    /// are not that tidy: "London, England, United Kingdom" against "London, UK" is the ordinary
    /// case, and against thirty days of the live corpus the old key matched across boards
    /// <b>zero</b> times in 5,268 postings.
    ///
    /// Inline rather than in the fixture, because adding a row there moves every count the other
    /// assertions are built on.
    /// </remarks>
    [Fact]
    public void The_same_job_on_two_boards_counts_once_even_when_they_write_the_location_differently()
    {
        const string csv = """
            id,site,title,company,location,job_url
            li-1,linkedin,Senior Backend Engineer,Northwind Labs,"London, England, United Kingdom",https://li/1
            in-1,indeed,Senior Backend Engineer,Northwind Labs,"London, UK",https://in/1
            in-2,indeed,Data Engineer,Contoso,"Manchester, UK",https://in/2
            """;

        var parsed = new JobCsvParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        var context = BlobNameParser.Parse(BlobPath, DateTimeOffset.UnixEpoch);

        var digest = new MetricsCalculator().Calculate(context, parsed, UpsertOutcome.Empty, 0);

        Assert.Equal(3, digest.Counts.Parsed);
        Assert.Equal(1, digest.Counts.CrossSiteDuplicates);
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
