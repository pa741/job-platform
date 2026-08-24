using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Model;
using JobPlatform.Core.Parsing;
using JobPlatform.Core.Tests.Fixtures;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The enricher against the whole fixture, rather than against hand-built postings.
/// </summary>
/// <remarks>
/// This is the closest thing to an end-to-end check that exists without Azure: a real CSV
/// through the real parser through the real enricher. The counts are known by construction
/// because the fixture is generated from a written specification, which is what makes them a
/// check rather than a restatement of whatever the code currently does.
///
/// It doubles as the evaluation gold set. With no interaction data there is no ground truth
/// for "is this a good match", and the standard mitigation is a small hand-labelled set - the
/// IR literature's practical floor is around 30-50 items. A fixture whose concepts are known
/// by construction already is one, at no extra cost.
/// </remarks>
public sealed class FixtureEnrichmentTests
{
    private static readonly IReadOnlyList<EnrichedPosting> Enriched = Load();

    private static IReadOnlyList<EnrichedPosting> Load()
    {
        using var stream = SampleCsv.Open();
        var parsed = new JobCsvParser().Parse(stream);

        return [.. parsed.Postings.Select(p => PostingEnricher.Enrich(p))];
    }

    private static EnrichedPosting ById(string externalId)
        => Enriched.Single(e => e.Posting.ExternalId == externalId);

    private static bool Has(EnrichedPosting posting, string conceptKey)
        => posting.Concepts.Any(c => c.ConceptKey == conceptKey);

    [Fact]
    public void Every_parsed_posting_enriches_without_throwing()
        => Assert.Equal(SampleCsv.ParsedPostings, Enriched.Count);

    [Fact]
    public void Salary_coverage_comes_entirely_from_the_description()
    {
        // The whole point of SalaryTextParser. Every salary column in the fixture is empty -
        // as it was in the real London run - so anything recovered here came from prose.
        var withSalary = Enriched.Where(e => e.AnnualSalaryMin is not null || e.AnnualSalaryMax is not null).ToList();

        Assert.NotEmpty(withSalary);
        Assert.All(withSalary, e => Assert.True(e.SalaryFromText));
        Assert.All(Enriched, e => Assert.Null(e.Posting.MinAmount));
    }

    [Fact]
    public void A_day_rate_is_annualised_and_still_identifiable_as_one()
    {
        // in-0005 is the data role: "Outside IR35. GBP 550 per day."
        var posting = ById("in-0005");

        Assert.Equal(550m * 260m, posting.AnnualSalaryMin);
        Assert.Equal("daily", posting.SalaryStatedInterval);
        Assert.Equal("outside", posting.Ir35);
    }

    [Fact]
    public void An_up_to_figure_is_read_as_a_ceiling()
    {
        // in-0002 is the Java role: "Paying up to GBP 88,000 per annum".
        var posting = ById("in-0002");

        Assert.Null(posting.AnnualSalaryMin);
        Assert.Equal(88_000m, posting.AnnualSalaryMax);
    }

    [Fact]
    public void The_stack_named_in_a_description_becomes_concepts()
    {
        var posting = ById("in-0001");

        Assert.True(Has(posting, "skill.csharp"));
        Assert.True(Has(posting, "skill.dotnet"));
        Assert.True(Has(posting, "skill.postgresql"));
        Assert.True(Has(posting, "skill.azure"));
        Assert.True(Has(posting, "skill.terraform"));
        Assert.True(Has(posting, "skill.kubernetes"));
    }

    [Fact]
    public void Hybrid_days_and_tags_come_out_of_the_same_description()
    {
        var posting = ById("in-0001");

        Assert.Equal(WorkArrangement.Hybrid, posting.WorkArrangement);
        Assert.Equal(3, posting.HybridDaysInOffice);
        Assert.Equal(5, posting.YearsExperienceMin);
        Assert.Contains(posting.Tags, t => t.Name == PostingTagNames.HolidayDays && t.Value == "25");
        Assert.Contains(posting.Tags, t => t.Name == PostingTagNames.PensionPercent && t.Value == "8");
    }

    [Fact]
    public void A_clearance_requirement_is_found_and_promoted()
    {
        // li-0008 is the security role: "Applicants must hold active SC clearance".
        var posting = ById("li-0008");

        Assert.True(Has(posting, "qual.sc-clearance"));
        Assert.True(Has(posting, "qual.cissp"));
        Assert.True(posting.RequiresSecurityClearance);
        Assert.Equal("inside", posting.Ir35);
    }

    [Fact]
    public void Board_supplied_skills_are_distinguishable_from_text_matches()
    {
        // Only freehire publishes structured skills, so every Board assertion comes from it.
        var withBoardSkills = Enriched
            .Where(e => e.Concepts.Any(c => c.Source == AssertionSource.Board))
            .ToList();

        Assert.Equal(SampleCsv.FreehireRows, withBoardSkills.Count);
        Assert.All(withBoardSkills, e => Assert.Equal("freehire", e.Posting.Site));
    }

    [Fact]
    public void A_board_skill_the_vocabulary_does_not_know_is_kept_as_a_mention()
    {
        // fh-006 lists "Contoso Internal Platform" among its skills. The employer really did
        // ask for it, so dropping it would understate the posting and hide the fact that the
        // vocabulary has a gap. This is where new vocabulary comes from.
        var mentions = ById("fh-006").Mentions;

        Assert.Contains(mentions, m => m.Reason == MentionReason.UnknownBoardSkill
            && m.SurfaceForm == "Contoso Internal Platform");
    }

    [Fact]
    public void A_freehire_row_that_states_no_work_mode_is_null_not_false()
    {
        // fh-004 leaves work_from_home_type empty as well, so nothing at all was said.
        var posting = ById("fh-004");

        Assert.Null(posting.Posting.IsRemote);
        Assert.Equal(SampleCsv.RemoteNotStated, Enriched.Count(e => e.Posting.IsRemote is null));
    }

    [Fact]
    public void The_boards_work_mode_field_recovers_what_is_remote_could_not_express()
    {
        // The three-way distinction the upstream library collapses into a single bool.
        Assert.Equal(WorkArrangement.Remote, ById("fh-001").WorkArrangement);
        Assert.Equal(WorkArrangement.Hybrid, ById("fh-002").WorkArrangement);
        Assert.Equal(WorkArrangement.OnSite, ById("fh-003").WorkArrangement);
    }

    [Fact]
    public void Company_spellings_fold_to_one_key()
    {
        // Northwind Labs appears six times across two boards under one key.
        var northwind = Enriched.Count(e => e.CompanyKey == "northwind labs");

        Assert.Equal(6, northwind);
    }

    [Fact]
    public void An_ambiguous_word_used_as_a_word_stays_a_mention()
    {
        // in-0006's description says "we go the extra mile". Nothing around it suggests the
        // language, so it is recorded rather than asserted - and recorded rather than
        // silently dropped, which is the whole reason mentions exist.
        var mentions = ById("in-0006").Mentions;

        Assert.Contains(mentions, m => m.Reason == MentionReason.Ambiguous
            && m.SurfaceForm.Equals("go", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_ambiguous_word_used_as_a_language_resolves()
    {
        // li-0008 says "review code in Python and Go, and help teams..." - a list whose
        // neighbour is a language the vocabulary knows. That settles it without a model.
        Assert.True(Has(ById("li-0008"), "skill.go"));
    }

    [Fact]
    public void A_sparse_description_produces_no_invented_detail()
    {
        // li-0015 is the one-line posting: "Software engineer required. Apply within."
        var posting = ById("li-0015");

        Assert.Null(posting.AnnualSalaryMin);
        Assert.Null(posting.YearsExperienceMin);
        Assert.Empty(posting.Tags);
        Assert.Empty(posting.Concepts);

        // Its work arrangement comes from the board's is_remote column, not from the text -
        // the description says nothing, and nothing is invented to fill the gap.
        Assert.Equal(WorkArrangement.Remote, posting.WorkArrangement);
        Assert.Null(posting.HybridDaysInOffice);
    }

    [Fact]
    public void Seniority_and_role_family_are_populated_where_the_title_says_so()
    {
        Assert.Equal(Seniority.Senior, ById("in-0001").Seniority);
        Assert.Equal(RoleFamily.Backend, ById("in-0001").RoleFamily);

        // li-0018 is "Graduate Software Engineer" at "entry level" - title and board agree.
        Assert.Equal(Seniority.Junior, ById("li-0018").Seniority);
        Assert.Equal(RoleFamily.MachineLearning, ById("in-0009").RoleFamily);
    }

    [Fact]
    public void Published_experience_numbers_beat_re_parsing_the_display_string()
    {
        // fh-003 publishes experience_years_min = 8 and renders it as "8+ Yrs". Its
        // description separately says "3-5 years", which the text parser would have taken.
        // The number the board actually had wins.
        Assert.Equal(8, ById("fh-003").YearsExperienceMin);
    }

    [Fact]
    public void Indeeds_attribute_list_survives_instead_of_being_reduced_to_a_job_type()
    {
        // Upstream read a job type out of this list and discarded the rest one line later.
        var attributes = ById("in-0001").Posting.Attributes;

        Assert.Contains("Health insurance", attributes);
        Assert.Contains("Hybrid work", attributes);
    }

    [Fact]
    public void A_repost_is_visible_as_a_gap_between_the_two_dates()
    {
        // in-0002 was first seen on Indeed weeks before the date it now advertises. Without
        // date_on_indeed that posting is indistinguishable from a genuinely new one.
        var posting = ById("in-0002").Posting;

        Assert.NotNull(posting.DateOnIndeed);
        Assert.True(posting.DateOnIndeed < posting.DatePosted);
    }

    [Fact]
    public void The_corpus_has_enough_signal_to_be_worth_analysing()
    {
        // A guard against the fixture regressing to placeholder text, which is what it was:
        // the same sentence four times, with nothing for any of this to find.
        var withConcepts = Enriched.Count(e => e.Concepts.Count > 0);
        var distinctConcepts = Enriched.SelectMany(e => e.Concepts.Select(c => c.ConceptKey)).Distinct().Count();

        Assert.True(withConcepts >= 30, $"only {withConcepts} postings carry any concept");
        Assert.True(distinctConcepts >= 40, $"only {distinctConcepts} distinct concepts across the corpus");
    }
}
