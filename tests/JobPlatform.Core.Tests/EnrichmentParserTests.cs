using JobPlatform.Core.Enrichment;
using Xunit;

namespace JobPlatform.Core.Tests;

public sealed class SeniorityClassifierTests
{
    [Theory]
    [InlineData("Senior Software Engineer", Seniority.Senior)]
    [InlineData("Junior Developer", Seniority.Junior)]
    [InlineData("Graduate Software Engineer", Seniority.Junior)]
    [InlineData("Lead Backend Engineer", Seniority.Lead)]
    [InlineData("Principal Engineer", Seniority.Principal)]
    [InlineData("Staff Engineer", Seniority.Lead)]
    [InlineData("Engineering Manager", Seniority.Lead)]
    [InlineData("Head of Engineering", Seniority.Principal)]
    [InlineData("Software Engineering Intern", Seniority.Intern)]
    [InlineData("VP of Engineering", Seniority.Executive)]
    public void Title_carries_the_level_where_the_board_does_not(string title, Seniority expected)
        => Assert.Equal(expected, SeniorityClassifier.Classify(title, null));

    [Fact]
    public void The_scale_is_ordinal_so_levels_can_be_compared()
    {
        // Staff sits between Senior and Principal on the usual ladder, and Head/Director sit
        // level with Principal on the management side of a dual ladder - Executive is kept
        // for VP and above. The exact rungs matter less than the ordering holding.
        Assert.True(SeniorityClassifier.Classify("Staff Engineer", null)
            > SeniorityClassifier.Classify("Senior Engineer", null));

        Assert.True(SeniorityClassifier.Classify("VP of Engineering", null)
            > SeniorityClassifier.Classify("Head of Engineering", null));
    }

    [Fact]
    public void A_plain_title_asserts_nothing()
    {
        // "Software Engineer" is the commonest title in the corpus and says nothing about
        // level. Mid would be a guess dressed as a measurement.
        Assert.Equal(Seniority.Unknown, SeniorityClassifier.Classify("Software Engineer", null));
    }

    [Fact]
    public void The_higher_of_board_and_title_wins()
    {
        // LinkedIn's "mid senior level" spans two of our levels; a title saying Senior is the
        // more specific claim and must not be dragged down by the coarser one.
        var level = SeniorityClassifier.Classify("Senior Backend Engineer", "mid senior level");

        Assert.Equal(Seniority.Senior, level);
    }

    [Fact]
    public void The_board_answers_when_the_title_is_silent()
        => Assert.Equal(Seniority.Intern, SeniorityClassifier.Classify("Software Engineer", "internship"));
}

public sealed class RoleFamilyClassifierTests
{
    [Theory]
    [InlineData("Senior Backend Engineer", RoleFamily.Backend)]
    [InlineData("React Developer", RoleFamily.Frontend)]
    [InlineData("Full Stack Engineer", RoleFamily.FullStack)]
    [InlineData("iOS Engineer", RoleFamily.Mobile)]
    [InlineData("Data Engineer", RoleFamily.Data)]
    [InlineData("Machine Learning Engineer", RoleFamily.MachineLearning)]
    [InlineData("Site Reliability Engineer", RoleFamily.Platform)]
    [InlineData("DevOps Engineer", RoleFamily.Platform)]
    [InlineData("QA Automation Engineer", RoleFamily.QA)]
    [InlineData("Embedded Software Engineer", RoleFamily.Embedded)]
    [InlineData("Engineering Manager", RoleFamily.Management)]
    [InlineData("Product Manager", RoleFamily.Product)]
    [InlineData("Product Designer", RoleFamily.Design)]
    public void Titles_sort_into_families(string title, RoleFamily expected)
        => Assert.Equal(expected, RoleFamilyClassifier.Classify(title));

    [Fact]
    public void A_generic_title_is_unknown_rather_than_backend()
    {
        // Classifying the corpus's most common title as Backend would inflate that family
        // with every listing that simply did not say.
        Assert.Equal(RoleFamily.Unknown, RoleFamilyClassifier.Classify("Software Engineer"));
    }

    [Theory]
    [InlineData("Cloud Security Engineer", RoleFamily.Security)]
    [InlineData("Machine Learning Data Engineer", RoleFamily.MachineLearning)]
    public void Precedence_decides_titles_that_carry_two_signals(string title, RoleFamily expected)
        => Assert.Equal(expected, RoleFamilyClassifier.Classify(title));
}

public sealed class WorkArrangementClassifierTests
{
    [Fact]
    public void The_boards_own_field_is_believed_over_everything_else()
    {
        var result = WorkArrangementClassifier.Classify(
            "hybrid", isRemote: true, "London", "Engineer", "fully remote role");

        Assert.Equal(WorkArrangement.Hybrid, result.Arrangement);
    }

    [Fact]
    public void Is_remote_false_is_not_treated_as_a_statement()
    {
        // On Indeed the flag is computed by searching the text for "remote", so false means
        // the word was absent - not that the employer said office-based.
        var result = WorkArrangementClassifier.Classify(
            null, isRemote: false, "London", "Software Engineer", "A great opportunity.");

        Assert.Equal(WorkArrangement.Unknown, result.Arrangement);
    }

    [Fact]
    public void Hybrid_is_tested_before_remote()
    {
        // Hybrid adverts routinely use the word "remote" for the other half of the week.
        var result = WorkArrangementClassifier.Classify(
            null, null, "London", "Engineer", "Hybrid working - 2 days remote, 3 in the office.");

        Assert.Equal(WorkArrangement.Hybrid, result.Arrangement);
    }

    [Theory]
    [InlineData("3 days a week in the office", 3)]
    [InlineData("in the office 2 days", 2)]
    [InlineData("hybrid, 4 days on-site", 4)]
    public void Office_days_are_read_where_a_number_is_given(string description, int expected)
    {
        var result = WorkArrangementClassifier.Classify(null, null, null, null, description);

        Assert.Equal(WorkArrangement.Hybrid, result.Arrangement);
        Assert.Equal(expected, result.HybridDaysInOffice);
    }

    [Fact]
    public void On_site_is_asserted_only_where_the_text_asserts_it()
    {
        var result = WorkArrangementClassifier.Classify(
            null, null, "London", "Engineer", "This is an office-based role.");

        Assert.Equal(WorkArrangement.OnSite, result.Arrangement);
    }
}

public sealed class SalaryTextParserTests
{
    [Fact]
    public void A_plain_annual_range_is_read_as_stated()
    {
        var salary = SalaryTextParser.Parse("Salary £65,000 - £85,000 per annum");

        Assert.NotNull(salary);
        Assert.Equal(65_000m, salary.Value.Min);
        Assert.Equal(85_000m, salary.Value.Max);
        Assert.Equal("GBP", salary.Value.Currency);
        Assert.Equal("yearly", salary.Value.StatedInterval);
    }

    [Fact]
    public void A_day_rate_annualises_with_the_librarys_multiplier()
    {
        // 260 comes from jobspy/util.py convert_to_annual. If these two ever disagree, a
        // salary parsed here and one parsed there are different measurements sharing a column.
        var salary = SalaryTextParser.Parse("£600 per day, outside IR35");

        Assert.NotNull(salary);
        Assert.Equal(600m * 260m, salary.Value.Min);
        Assert.Equal("daily", salary.Value.StatedInterval);
    }

    [Fact]
    public void An_hourly_rate_annualises_with_the_librarys_multiplier()
    {
        var salary = SalaryTextParser.Parse("£45 per hour");

        Assert.NotNull(salary);
        Assert.Equal(45m * 2080m, salary.Value.Min);
        Assert.Equal("hourly", salary.Value.StatedInterval);
    }

    [Fact]
    public void Up_to_is_a_ceiling_not_a_floor()
    {
        var salary = SalaryTextParser.Parse("Paying up to £80,000");

        Assert.NotNull(salary);
        Assert.Null(salary.Value.Min);
        Assert.Equal(80_000m, salary.Value.Max);
    }

    [Fact]
    public void K_suffixes_expand()
    {
        var salary = SalaryTextParser.Parse("£70k - £90k depending on experience");

        Assert.NotNull(salary);
        Assert.Equal(70_000m, salary.Value.Min);
        Assert.Equal(90_000m, salary.Value.Max);
    }

    [Fact]
    public void A_figure_with_no_currency_is_refused()
    {
        // 65,000 could be pounds or euros, and guessing from the posting's country would
        // invent data rather than recover it.
        Assert.Null(SalaryTextParser.Parse("Salary 65,000 - 85,000"));
    }

    [Fact]
    public void A_bare_small_figure_with_no_period_is_refused()
    {
        // "£450" is a day rate as often as anything else and nothing in the text settles it.
        Assert.Null(SalaryTextParser.Parse("A £450 training budget each year"));
    }

    [Fact]
    public void Years_of_experience_are_not_mistaken_for_money()
        => Assert.Null(SalaryTextParser.Parse("You will have 5 years of experience"));

    [Theory]
    [InlineData(600, "daily", 156_000)]
    [InlineData(45, "hourly", 93_600)]
    [InlineData(5_000, "monthly", 60_000)]
    [InlineData(1_500, "weekly", 78_000)]
    [InlineData(80_000, "yearly", 80_000)]
    public void Board_figures_annualise_with_the_same_multipliers(
        decimal amount, string interval, decimal expected)
        => Assert.Equal(expected, SalaryTextParser.Annualise(amount, interval));

    [Fact]
    public void An_unknown_interval_is_left_alone_rather_than_rejected()
    {
        // enforce_annual_salary has already assumed annual by the time the value reaches us.
        Assert.Equal(80_000m, SalaryTextParser.Annualise(80_000m, null));
    }
}

public sealed class ExperienceParserTests
{
    [Theory]
    [InlineData("3+ Yrs", 3, null)]
    [InlineData("5-8 years", 5, 8)]
    [InlineData("2 to 4 years", 2, 4)]
    public void The_boards_own_range_is_preferred(string boardRange, int? min, int? max)
    {
        var range = ExperienceParser.Parse(boardRange, "10+ years of experience");

        Assert.Equal(min, range.Min);
        Assert.Equal(max, range.Max);
    }

    [Fact]
    public void The_description_answers_when_the_board_is_silent()
    {
        var range = ExperienceParser.Parse(null, "At least 4 years of commercial experience.");

        Assert.Equal(4, range.Min);
    }

    [Fact]
    public void The_largest_floor_wins_when_several_are_named()
    {
        // An advert listing several thresholds is asking for all of them, so the binding
        // requirement is the highest. Taking the first would make the answer depend on
        // sentence order.
        var range = ExperienceParser.Parse(
            null, "5+ years engineering experience, 2+ years with Kubernetes.");

        Assert.Equal(5, range.Min);
    }

    [Fact]
    public void An_implausible_figure_is_refused()
        => Assert.Null(ExperienceParser.Parse(null, "Founded 1995 years ago").Min);
}

public sealed class EmployeeBandParserTests
{
    [Theory]
    [InlineData("51-200", 51, 200)]
    [InlineData("1,001-5,000", 1001, 5000)]
    [InlineData("11 to 50", 11, 50)]
    public void A_range_becomes_two_numbers(string band, int min, int max)
    {
        var parsed = EmployeeBandParser.Parse(band);

        Assert.Equal(min, parsed.Min);
        Assert.Equal(max, parsed.Max);
    }

    [Fact]
    public void An_open_band_has_no_ceiling()
    {
        var parsed = EmployeeBandParser.Parse("10,000+ employees");

        Assert.Equal(10_000, parsed.Min);
        Assert.Null(parsed.Max);
    }

    [Fact]
    public void A_ceiling_is_not_read_as_a_floor()
    {
        var parsed = EmployeeBandParser.Parse("fewer than 50 employees");

        Assert.Null(parsed.Min);
        Assert.Equal(50, parsed.Max);
    }

    [Fact]
    public void Numbers_order_where_the_strings_did_not()
    {
        // The point of the whole class: "1,001-5,000" sorts before "51-200" lexically.
        var small = EmployeeBandParser.Parse("51-200");
        var large = EmployeeBandParser.Parse("1,001-5,000");

        Assert.True(large.Min > small.Min);
    }
}

public sealed class JobTypeNormalizerTests
{
    [Fact]
    public void A_multi_valued_column_becomes_a_set()
    {
        var types = JobTypeNormalizer.Normalize("parttime, fulltime");

        Assert.Equal([JobTypeNormalizer.FullTime, JobTypeNormalizer.PartTime], types);
    }

    [Fact]
    public void Order_does_not_change_the_result()
    {
        // Two rows saying the same thing must produce the same list, or every re-ingest looks
        // like a change.
        Assert.Equal(
            JobTypeNormalizer.Normalize("parttime, fulltime"),
            JobTypeNormalizer.Normalize("fulltime, parttime"));
    }

    [Theory]
    [InlineData("Full-Time")]
    [InlineData("permanent")]
    [InlineData("FULLTIME")]
    public void Spellings_of_one_type_agree(string spelling)
        => Assert.Equal([JobTypeNormalizer.FullTime], JobTypeNormalizer.Normalize(spelling));

    [Fact]
    public void Unrecognised_values_are_dropped_rather_than_passed_through()
    {
        // A stray value in a facet looks like a finding.
        Assert.Empty(JobTypeNormalizer.Normalize("something else entirely"));
    }
}

public sealed class CompanyNormalizerTests
{
    [Theory]
    [InlineData("Contoso Ltd")]
    [InlineData("Contoso Limited")]
    [InlineData("CONTOSO LTD.")]
    [InlineData("Contoso")]
    public void Legal_forms_fold_to_one_key(string company)
        => Assert.Equal("contoso", CompanyNormalizer.Key(company));

    [Fact]
    public void Several_suffixes_are_stripped()
        => Assert.Equal("contoso", CompanyNormalizer.Key("Contoso Holdings Ltd"));

    [Fact]
    public void A_geographic_qualifier_is_left_alone()
    {
        // "Contoso UK" and "Contoso GmbH" plausibly are different hiring entities with
        // different pay. This folds spelling, not corporate structure.
        Assert.NotEqual(CompanyNormalizer.Key("Contoso"), CompanyNormalizer.Key("Contoso UK"));
    }

    [Fact]
    public void An_absent_name_has_no_key()
        => Assert.Null(CompanyNormalizer.Key("   "));
}

public sealed class PostingTagExtractorTests
{
    private static string? Value(string description, string tag)
        => PostingTagExtractor.Extract(description).FirstOrDefault(t => t.Name == tag).Value;

    private static bool Has(string description, string tag)
        => PostingTagExtractor.Extract(description).Any(t => t.Name == tag);

    [Theory]
    [InlineData("This role is inside IR35.", "inside")]
    [InlineData("Outside IR35 contract.", "outside")]
    [InlineData("IR35: outside", "outside")]
    [InlineData("Working outside of IR-35", "outside")]
    public void Ir35_status_is_captured_either_way_round(string description, string expected)
        => Assert.Equal(expected, Value(description, PostingTagNames.Ir35));

    [Fact]
    public void Visa_sponsorship_is_only_claimed_where_it_is_offered()
    {
        Assert.True(Has("Visa sponsorship is available for the right candidate.",
            PostingTagNames.VisaSponsorship));

        Assert.False(Has("We are unable to offer sponsorship for this role.",
            PostingTagNames.VisaSponsorship));
    }

    [Theory]
    [InlineData("25 days holiday plus bank holidays", "25")]
    [InlineData("Annual leave: 28 days", "28")]
    public void Holiday_days_are_read(string description, string expected)
        => Assert.Equal(expected, Value(description, PostingTagNames.HolidayDays));

    [Fact]
    public void An_implausible_holiday_figure_is_refused()
        => Assert.Null(Value("Interviews within 3 days of applying", PostingTagNames.HolidayDays));

    [Fact]
    public void Pension_percentage_is_read()
        => Assert.Equal("8", Value("Pension contribution matched up to 8%", PostingTagNames.PensionPercent));

    [Fact]
    public void A_large_percentage_near_the_word_pension_is_refused()
        => Assert.Null(Value("40% bonus, plus pension", PostingTagNames.PensionPercent));

    [Theory]
    [InlineData("Share options for all employees", PostingTagNames.Equity)]
    [InlineData("Participation in an on-call rota", PostingTagNames.OnCall)]
    [InlineData("We offer a four-day week", PostingTagNames.FourDayWeek)]
    [InlineData("Relocation package available", PostingTagNames.RelocationSupport)]
    [InlineData("Regular travel to client sites", PostingTagNames.TravelRequired)]
    public void Benefit_and_condition_flags_fire(string description, string tag)
        => Assert.True(Has(description, tag));

    [Fact]
    public void An_empty_description_yields_nothing()
        => Assert.Empty(PostingTagExtractor.Extract(null));
}
