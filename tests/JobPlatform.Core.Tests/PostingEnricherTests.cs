using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Model;
using Xunit;

namespace JobPlatform.Core.Tests;

public sealed class PostingEnricherTests
{
    private static JobPosting Posting(
        string title = "Software Engineer",
        string? description = null,
        string? company = null,
        string? jobType = null,
        IReadOnlyList<string>? skills = null,
        decimal? minAmount = null,
        decimal? maxAmount = null,
        string? currency = null,
        string? interval = null,
        string? numEmployees = null,
        string? experienceRange = null,
        bool? isRemote = null,
        string? workFromHomeType = null,
        string? jobLevel = null)
        => new()
        {
            ExternalId = "1",
            Site = "indeed",
            Title = title,
            Description = description,
            Company = company,
            JobType = jobType,
            Skills = skills ?? [],
            MinAmount = minAmount,
            MaxAmount = maxAmount,
            Currency = currency,
            SalaryInterval = interval,
            CompanyNumEmployees = numEmployees,
            ExperienceRange = experienceRange,
            IsRemote = isRemote,
            WorkFromHomeType = workFromHomeType,
            JobLevel = jobLevel,
        };

    [Fact]
    public void Board_skills_and_text_matches_are_kept_as_separate_evidence()
    {
        var enriched = PostingEnricher.Enrich(Posting(
            description: "You will work with Kubernetes daily.",
            skills: ["Kubernetes"]));

        var kubernetes = enriched.Concepts.Where(c => c.ConceptKey == "skill.kubernetes").ToList();

        // Two rows, not one. An employer tagging the role and a description mentioning it in
        // passing are different strengths of evidence, and collapsing them destroys the only
        // field that says which is which.
        Assert.Equal(2, kubernetes.Count);
        Assert.Contains(kubernetes, c => c.Source == AssertionSource.Board);
        Assert.Contains(kubernetes, c => c.Source == AssertionSource.Taxonomy);
    }

    [Fact]
    public void A_board_skill_the_vocabulary_does_not_know_becomes_a_mention()
    {
        var enriched = PostingEnricher.Enrich(Posting(skills: ["Contoso Internal Platform"]));

        Assert.Empty(enriched.Concepts);

        var mention = Assert.Single(enriched.Mentions);
        Assert.Equal("Contoso Internal Platform", mention.SurfaceForm);
        Assert.Equal(MentionReason.UnknownBoardSkill, mention.Reason);
    }

    [Fact]
    public void Clearance_requirements_are_derived_from_the_closure_not_a_key_list()
    {
        var enriched = PostingEnricher.Enrich(Posting(
            description: "Applicants must hold active SC clearance."));

        Assert.True(enriched.RequiresSecurityClearance);
        Assert.Contains(enriched.Concepts, c => c.ConceptKey == "qual.sc-clearance");
    }

    [Fact]
    public void A_posting_with_no_clearance_does_not_claim_one()
        => Assert.False(PostingEnricher.Enrich(Posting(description: "A great role.")).RequiresSecurityClearance);

    [Fact]
    public void Degree_requirements_are_derived_the_same_way()
    {
        var enriched = PostingEnricher.Enrich(Posting(
            description: "A BSc in a STEM subject is required."));

        Assert.True(enriched.RequiresDegree);
    }

    [Fact]
    public void The_boards_salary_columns_win_over_the_description()
    {
        var enriched = PostingEnricher.Enrich(Posting(
            description: "Also mentions £30,000 somewhere else per annum.",
            minAmount: 70_000m,
            currency: "GBP",
            interval: "yearly"));

        Assert.Equal(70_000m, enriched.AnnualSalaryMin);
        Assert.False(enriched.SalaryFromText);
    }

    [Fact]
    public void The_description_fills_the_gap_the_board_left()
    {
        // The 0%-coverage case: the upstream library never runs its own text extractor
        // outside the USA, so for this deployment the description is the only source there is.
        var enriched = PostingEnricher.Enrich(Posting(
            description: "Paying £65,000 - £85,000 per annum plus benefits."));

        Assert.Equal(65_000m, enriched.AnnualSalaryMin);
        Assert.Equal(85_000m, enriched.AnnualSalaryMax);
        Assert.True(enriched.SalaryFromText);
    }

    [Fact]
    public void A_board_day_rate_is_annualised_rather_than_stored_raw()
    {
        var enriched = PostingEnricher.Enrich(Posting(
            minAmount: 600m, currency: "GBP", interval: "daily"));

        Assert.Equal(156_000m, enriched.AnnualSalaryMin);

        // Without this the annualised figure is indistinguishable from a real salary.
        Assert.Equal("daily", enriched.SalaryStatedInterval);
    }

    [Fact]
    public void Everything_deterministic_lands_in_one_pass()
    {
        var enriched = PostingEnricher.Enrich(Posting(
            title: "Senior Backend Engineer",
            description: "Hybrid working, 3 days a week in the office. Outside IR35. "
                + "You will have 5+ years of experience with C# and Kubernetes. "
                + "25 days holiday and share options.",
            company: "Contoso Ltd",
            jobType: "contract, fulltime",
            numEmployees: "51-200",
            jobLevel: "mid senior level"));

        Assert.Equal(Seniority.Senior, enriched.Seniority);
        Assert.Equal(RoleFamily.Backend, enriched.RoleFamily);
        Assert.Equal(WorkArrangement.Hybrid, enriched.WorkArrangement);
        Assert.Equal(3, enriched.HybridDaysInOffice);
        Assert.Equal(5, enriched.YearsExperienceMin);
        Assert.Equal(51, enriched.EmployeesMin);
        Assert.Equal(200, enriched.EmployeesMax);
        Assert.Equal("contoso", enriched.CompanyKey);
        Assert.Equal([JobTypeNormalizer.FullTime, JobTypeNormalizer.Contract], enriched.JobTypes);
        Assert.Equal("outside", enriched.Ir35);

        Assert.Contains(enriched.Concepts, c => c.ConceptKey == "skill.csharp");
        Assert.Contains(enriched.Concepts, c => c.ConceptKey == "skill.kubernetes");
        Assert.Contains(enriched.Tags, t => t.Name == PostingTagNames.HolidayDays && t.Value == "25");
        Assert.Contains(enriched.Tags, t => t.Name == PostingTagNames.Equity);
    }

    [Fact]
    public void Enrichment_is_stable_across_repeated_calls()
    {
        // Re-ingest must converge. An unstable concept list would look like a changed posting
        // on every run and defeat the change detection entirely.
        var posting = Posting(
            title: "Senior Backend Engineer",
            description: "C#, Kubernetes and Terraform. Hybrid, 2 days in the office.",
            skills: ["Docker", "Unknown Thing"]);

        var first = PostingEnricher.Enrich(posting);
        var second = PostingEnricher.Enrich(posting);

        Assert.Equal(
            first.Concepts.Select(c => (c.ConceptKey, c.Source)),
            second.Concepts.Select(c => (c.ConceptKey, c.Source)));

        Assert.Equal(
            first.Mentions.Select(m => m.SurfaceForm),
            second.Mentions.Select(m => m.SurfaceForm));
    }

    [Fact]
    public void An_empty_posting_produces_no_claims()
    {
        var enriched = PostingEnricher.Enrich(Posting());

        Assert.Empty(enriched.Concepts);
        Assert.Empty(enriched.Mentions);
        Assert.Empty(enriched.Tags);
        Assert.Empty(enriched.JobTypes);
        Assert.Null(enriched.AnnualSalaryMin);
        Assert.Equal(Seniority.Unknown, enriched.Seniority);
        Assert.Equal(RoleFamily.Unknown, enriched.RoleFamily);
        Assert.Equal(WorkArrangement.Unknown, enriched.WorkArrangement);
    }

    [Fact]
    public void The_source_posting_is_carried_through_unchanged()
    {
        // JobPosting is what the scraper said; EnrichedPosting is what we concluded. Keeping
        // the two apart is what stops an inferred value being mistaken for a scraped one.
        var posting = Posting(title: "Senior Backend Engineer");

        Assert.Same(posting, PostingEnricher.Enrich(posting).Posting);
        Assert.Equal(EnrichedPosting.CurrentVersion, PostingEnricher.Enrich(posting).Version);
    }
}
