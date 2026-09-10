using JobPlatform.Core.Model;
using JobPlatform.Core.Settings;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// That <see cref="PipelineSettings.Default"/> is the pipeline the shipped code already ran.
/// </summary>
/// <remarks>
/// <b>This is the assertion the whole feature is judged on and it is the reason this file
/// exists.</b> A deployment that configures nothing must not be able to tell the settings arrived,
/// and the only thing standing between that promise and a silent breach is that every default is a
/// transcription of the constant it replaces. Nothing in the type system checks it: a default is
/// an initialiser on a property, so moving one compiles, passes review and changes what every
/// unconfigured candidate's night costs - and the evidence arrives as a bill rather than as a red
/// build.
///
/// <b>Five of the nine are pinned against the real constant and four are pinned by value.</b>
/// <see cref="PostingAge"/>, <see cref="SubmissionLimits"/> and <see cref="SubmissionState"/> are
/// in Core, so those defaults are compared against the constant itself and a drift on either side
/// fails here. The judgement budget, the judgement threshold and the two drafting numbers live on
/// types inside the Ingestion host, which Core cannot reference and must not - so those are
/// asserted as literals with the constant they mirror named beside them. A literal is a weaker
/// guard than a reference and it is the strongest one available from here: it catches this side
/// moving, and the comment is what tells whoever moves the other side where to look.
/// </remarks>
public sealed class PipelineSettingsDefaultTests
{
    /// <summary>
    /// The four defaults whose constants are in Core, compared against those constants.
    /// </summary>
    /// <remarks>
    /// These are real references rather than repeated numbers, so either side moving fails the
    /// build. That is the standard the other five would be held to if Core could see them.
    /// </remarks>
    [Fact]
    public void The_defaults_that_can_reference_their_constant_do_reference_it()
    {
        // The reservation window defaults to the system-wide definition of a recent posting -
        // and only defaults to it. PostingAge.DailyWindowDays stays the age every other filter
        // in the system answers to; this is the sweep's own reservation window and the two are
        // free to diverge the moment somebody configures one.
        Assert.Equal(PostingAge.DailyWindowDays, PipelineSettings.Default.RecentWindowDays);

        // SubmissionLimits.MaxSubmittedPerDay is now only the default for this lever. The cap is
        // still enforced in SubmissionRepository and nowhere else; what a candidate configures is
        // which number arrives at that one check.
        Assert.Equal(SubmissionLimits.MaxSubmittedPerDay, PipelineSettings.Default.DailySendCap);

        // A day count here against a TimeSpan there, because a TimeSpan is a shape a form cannot
        // render and a wire format spells three ways. The conversion belongs to the consumer, so
        // this is the exact expression the consumer builds.
        Assert.Equal(SubmissionState.StaleAfter, TimeSpan.FromDays(PipelineSettings.Default.ChaseAfterDays));
    }

    /// <summary>
    /// The five defaults whose constants sit in the Ingestion host, pinned by value.
    /// </summary>
    /// <remarks>
    /// Core is pure - no EF, no Azure, no clock, and no reference to a Functions host - so these
    /// cannot be compared against the constants they transcribe. The names are written out so a
    /// search for either lands here, which is the same reason <c>MatchSweepFunction</c> writes out
    /// the names of the constants it no longer holds.
    /// </remarks>
    [Fact]
    public void The_defaults_that_mirror_a_constant_outside_core_are_pinned_by_value()
    {
        // MatchSweepFunction.MaxAssessments. Forty: briefly ninety for the measurement run on
        // 2026-08-28, settled back to forty, and a quarter of it goes to the stratified
        // measurement sample rather than to the shortlist.
        Assert.Equal(40, PipelineSettings.Default.AssessmentsPerNight);

        // MatchSweepFunction.AssessmentThreshold. Deliberately low: the arithmetic under-scores a
        // candidate whose relevant experience is in prose the extractor read cautiously, and the
        // model exists to catch exactly that. Not MatchRanker.FusionFloor, which is 80, is fitted,
        // and is not a setting.
        Assert.Equal(45, PipelineSettings.Default.AssessmentThreshold);

        // ApplicationGenerationOptions.DocumentsPerNight. The only number in the writing pass that
        // is a bill: these calls go to the deployment priced roughly twenty-five times the bulk
        // one.
        Assert.Equal(10, PipelineSettings.Default.DraftsPerNight);

        // ApplicationGenerationOptions.MinAssessmentScore. Eighty because eighty is what the
        // unattended run asks list_applyable for; the two have to be able to be equal without a
        // deploy, which is the whole reason it is configurable.
        Assert.Equal(80, PipelineSettings.Default.DraftMinAssessmentScore);

        // ApplicationGenerationOptions.PostedWithinDays. The one lever that may be absent, and
        // absent is not zero: null is no bound at all, where zero through PostingAge.Cutoff would
        // mean "posted since this instant" and select almost nothing.
        Assert.Null(PipelineSettings.Default.DraftPostedWithinDays);
    }

    /// <summary>
    /// The one default whose <i>spelling</i> changed, held to the arithmetic it replaced.
    /// </summary>
    /// <remarks>
    /// The sweep reserved <c>shortlistBudget * 2 / 3</c> of its shortlist for recent postings; the
    /// setting is a percent, because a form cannot sensibly ask anybody for a numerator and a
    /// denominator - two fields that must be read together are two fields that will be
    /// half-edited. 67 is the integer percent nearest two thirds and the rounding is upward, which
    /// is the safe direction: it can reserve one more row for today and never one fewer.
    ///
    /// <b>Behaviour-preserving at the shipped budget rather than identical arithmetic</b>, which
    /// is a claim worth checking rather than repeating. Both spellings floor, and this is the
    /// range over which they agree.
    /// </remarks>
    [Fact]
    public void The_recent_share_reserves_what_two_thirds_reserved_at_every_budget_below_a_hundred()
    {
        for (var shortlistBudget = 0; shortlistBudget < 100; shortlistBudget++)
        {
            Assert.Equal(
                shortlistBudget * 2 / 3,
                shortlistBudget * PipelineSettings.Default.RecentSharePercent / 100);
        }
    }

    /// <summary>
    /// Where the two spellings first disagree, and by how much.
    /// </summary>
    /// <remarks>
    /// One row, at a shortlist budget of a hundred - which needs
    /// <see cref="PipelineSettings.AssessmentsPerNight"/> at 110, because a quarter of the budget
    /// up to ten rows goes to the measurement sample first. That is nearly three times the shipped
    /// forty. Quote this rather than claiming the two spellings are the same.
    /// </remarks>
    [Fact]
    public void The_two_spellings_first_disagree_by_one_row_at_a_shortlist_budget_of_a_hundred()
    {
        const int shortlistBudget = 100;

        Assert.Equal(66, shortlistBudget * 2 / 3);
        Assert.Equal(67, shortlistBudget * PipelineSettings.Default.RecentSharePercent / 100);
    }

    /// <summary>
    /// The defaults are themselves a legal configuration.
    /// </summary>
    /// <remarks>
    /// Not circular, because the bounds were argued separately from the numbers: the drafting
    /// floor at 80 has to sit at or above the judgement threshold at 45, and ten drafts a night
    /// has to sit at or under a send cap of twenty-five. A default that its own validator refuses
    /// would mean a settings page that cannot save the state it opens on.
    /// </remarks>
    [Fact]
    public void The_shipped_defaults_pass_their_own_validation()
        => Assert.Empty(PipelineSettingsValidation.Validate(PipelineSettings.Default));

    /// <summary>
    /// An absent field is a field left at the default, which is what a partial document is.
    /// </summary>
    /// <remarks>
    /// The property initialisers are what make this true under every serialiser, and
    /// <c>Default with { ... }</c> is the only way an override should ever be built - nine values
    /// copied across by hand is a transposition nothing catches.
    /// </remarks>
    [Fact]
    public void An_override_moves_one_lever_and_leaves_the_other_eight_where_they_were()
    {
        var configured = PipelineSettings.Default with { DraftsPerNight = 4 };

        Assert.Equal(4, configured.DraftsPerNight);
        Assert.Equal(PipelineSettings.Default.AssessmentsPerNight, configured.AssessmentsPerNight);
        Assert.Equal(PipelineSettings.Default.AssessmentThreshold, configured.AssessmentThreshold);
        Assert.Equal(PipelineSettings.Default.RecentSharePercent, configured.RecentSharePercent);
        Assert.Equal(PipelineSettings.Default.RecentWindowDays, configured.RecentWindowDays);
        Assert.Equal(PipelineSettings.Default.DraftMinAssessmentScore, configured.DraftMinAssessmentScore);
        Assert.Equal(PipelineSettings.Default.DraftPostedWithinDays, configured.DraftPostedWithinDays);
        Assert.Equal(PipelineSettings.Default.DailySendCap, configured.DailySendCap);
        Assert.Equal(PipelineSettings.Default.ChaseAfterDays, configured.ChaseAfterDays);
    }

    /// <summary>
    /// A record with nothing set is the defaults, and equals them by value.
    /// </summary>
    /// <remarks>
    /// What the storage layer relies on when it reports that an absent row and a row of default
    /// values read back identically - and what lets every test here compare whole records rather
    /// than nine fields.
    /// </remarks>
    [Fact]
    public void A_record_nobody_configured_equals_the_default()
        => Assert.Equal(PipelineSettings.Default, new PipelineSettings());
}

/// <summary>
/// Every bound one candidate's settings have to clear before they are allowed to cost a night.
/// </summary>
/// <remarks>
/// Modelled on <c>ScraperSearchValidationTests</c>, because the validator is modelled on
/// <c>ScraperSearchValidation</c>: public constants so a column, a validator and a form read the
/// same number, and one function that returns every problem rather than the first.
///
/// Both ends of every bound are exercised, in both directions - refused just outside, accepted at
/// the edge. A bound written with the wrong comparison operator is off by exactly one value, so a
/// test that only pushes a wildly out-of-range number through it passes either way.
/// </remarks>
public sealed class PipelineSettingsValidationTests
{
    private static IReadOnlyList<string> Validate(PipelineSettings settings)
        => PipelineSettingsValidation.Validate(settings);

    [Fact]
    public void A_default_configuration_has_no_problems()
        => Assert.Empty(Validate(PipelineSettings.Default));

    /// <summary>
    /// Null is a caller's bug and never an empty problem list.
    /// </summary>
    /// <remarks>
    /// An empty list means "valid", so answering it for null would report a configuration nobody
    /// supplied as fit to spend money on.
    /// </remarks>
    [Fact]
    public void Null_settings_are_refused_rather_than_reported_as_valid()
        => Assert.Throws<ArgumentNullException>(() => PipelineSettingsValidation.Validate(null!));

    // ---- Judgement ----

    [Theory]
    [InlineData(-1)]
    [InlineData(PipelineSettingsValidation.MaxAssessmentsPerNight + 1)]
    public void Assessments_per_night_is_bounded(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { AssessmentsPerNight = value }),
            problem => problem.Contains("Assessments per night", StringComparison.Ordinal));

    /// <summary>
    /// Zero is the off switch rather than a nonsense value, and the ceiling is legal.
    /// </summary>
    /// <remarks>
    /// The scoring pass needs no model, so zero here keeps the shortlist and stops buying
    /// verdicts. Two hundred is legal and is not the real ceiling - what stops a sweep is the wall
    /// clock - which is a thing to know rather than a thing to refuse.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(PipelineSettingsValidation.MaxAssessmentsPerNight)]
    public void Assessments_per_night_accepts_both_ends_of_its_range(int value)
        => Assert.Empty(Validate(PipelineSettings.Default with { AssessmentsPerNight = value }));

    [Theory]
    [InlineData(-1)]
    [InlineData(PipelineSettingsValidation.MaxAssessmentThreshold + 1)]
    public void The_assessment_threshold_is_bounded(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { AssessmentThreshold = value }),
            problem => problem.Contains("assessment threshold", StringComparison.Ordinal));

    /// <summary>
    /// Both ends of the threshold, with the drafting floor moved to keep the pair coherent.
    /// </summary>
    /// <remarks>
    /// The floor has to come with it at the top end, because the cross-field rule refuses a floor
    /// below the threshold and the shipped floor is 80. That the two have to move together is the
    /// rule working rather than the test working around it.
    /// </remarks>
    [Fact]
    public void The_assessment_threshold_accepts_both_ends_of_its_range()
    {
        Assert.Empty(Validate(PipelineSettings.Default with
        {
            AssessmentThreshold = 0,
        }));

        Assert.Empty(Validate(PipelineSettings.Default with
        {
            AssessmentThreshold = PipelineSettingsValidation.MaxAssessmentThreshold,
            DraftMinAssessmentScore = PipelineSettingsValidation.MaxDraftMinAssessmentScore,
        }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(PipelineSettingsValidation.MaxRecentSharePercent + 1)]
    public void The_recent_share_is_bounded(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { RecentSharePercent = value }),
            problem => problem.Contains("recent share", StringComparison.Ordinal));

    /// <summary>
    /// Zero is "no reservation, pure top-down"; a hundred is "today and nothing else".
    /// </summary>
    /// <remarks>
    /// A hundred is legal on purpose. It stops the backlog draining on any day with enough
    /// arrivals to fill the shortlist, which is the absorbing state the reservation was designed
    /// to avoid - reachable only by asking for it in so many words, and a coherent thing for a
    /// candidate with no backlog to want.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(PipelineSettingsValidation.MaxRecentSharePercent)]
    public void The_recent_share_accepts_both_ends_of_its_range(int value)
        => Assert.Empty(Validate(PipelineSettings.Default with { RecentSharePercent = value }));

    /// <summary>
    /// Zero days is refused rather than clamped, which is where this differs from
    /// <c>PostingAge.Cutoff</c>.
    /// </summary>
    /// <remarks>
    /// That function reads a zero window as "since now", which is the honest answer to a nonsense
    /// request on a path that has to be total. Stored as a setting it would leave the reservation
    /// unfilled every night and falling back to the top-down draw - indistinguishable from the
    /// feature being switched off, which is what <see cref="PipelineSettings.RecentSharePercent"/>
    /// at zero is for.
    /// </remarks>
    [Theory]
    [InlineData(PipelineSettingsValidation.MinRecentWindowDays - 1)]
    [InlineData(PipelineSettingsValidation.MaxRecentWindowDays + 1)]
    public void The_recent_window_is_bounded(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { RecentWindowDays = value }),
            problem => problem.Contains("recent window", StringComparison.Ordinal));

    [Theory]
    [InlineData(PipelineSettingsValidation.MinRecentWindowDays)]
    [InlineData(PipelineSettingsValidation.MaxRecentWindowDays)]
    public void The_recent_window_accepts_both_ends_of_its_range(int value)
        => Assert.Empty(Validate(PipelineSettings.Default with { RecentWindowDays = value }));

    // ---- Drafting ----

    [Theory]
    [InlineData(-1)]
    [InlineData(PipelineSettingsValidation.MaxDraftsPerNight + 1)]
    public void Drafts_per_night_is_bounded(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { DraftsPerNight = value }),
            problem => problem.Contains("Drafts per night", StringComparison.Ordinal));

    /// <summary>
    /// Twenty-five is legal only because the send cap defaults to twenty-five as well.
    /// </summary>
    /// <remarks>
    /// The effective ceiling on drafts is the lower of
    /// <see cref="PipelineSettingsValidation.MaxDraftsPerNight"/> and the candidate's own
    /// <see cref="PipelineSettings.DailySendCap"/>; see the cross-field tests below.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(PipelineSettingsValidation.MaxDraftsPerNight)]
    public void Drafts_per_night_accepts_both_ends_of_its_range(int value)
        => Assert.Empty(Validate(PipelineSettings.Default with { DraftsPerNight = value }));

    [Theory]
    [InlineData(-1)]
    [InlineData(PipelineSettingsValidation.MaxDraftMinAssessmentScore + 1)]
    public void The_drafting_floor_is_bounded(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { DraftMinAssessmentScore = value }),
            problem => problem.Contains("drafting assessment floor", StringComparison.Ordinal));

    /// <summary>
    /// Both ends of the drafting floor, with the threshold moved to keep the pair coherent.
    /// </summary>
    /// <remarks>
    /// Zero is a real request - "anything the model has actually judged" - rather than a disabled
    /// check, because a pair the model scored no number for clears no floor at any value. It needs
    /// the threshold at zero with it for the same reason the threshold's top end needs the floor.
    /// </remarks>
    [Fact]
    public void The_drafting_floor_accepts_both_ends_of_its_range()
    {
        Assert.Empty(Validate(PipelineSettings.Default with
        {
            DraftMinAssessmentScore = 0,
            AssessmentThreshold = 0,
        }));

        Assert.Empty(Validate(PipelineSettings.Default with
        {
            DraftMinAssessmentScore = PipelineSettingsValidation.MaxDraftMinAssessmentScore,
        }));
    }

    /// <summary>
    /// Zero is refused on the age bound, and null is not.
    /// </summary>
    /// <remarks>
    /// The distinction the whole field exists to carry: null is "no bound at all" and is the
    /// default; zero through <c>PostingAge.Cutoff</c> would mean "posted since this instant" and
    /// select almost nothing. Two states a form renders as an empty box have to be different bytes
    /// on the wire.
    /// </remarks>
    [Theory]
    [InlineData(PipelineSettingsValidation.MinDraftPostedWithinDays - 1)]
    [InlineData(PipelineSettingsValidation.MaxDraftPostedWithinDays + 1)]
    public void The_drafting_age_bound_is_bounded_when_it_is_set(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { DraftPostedWithinDays = value }),
            problem => problem.Contains("postings from the last", StringComparison.Ordinal));

    [Theory]
    [InlineData(PipelineSettingsValidation.MinDraftPostedWithinDays)]
    [InlineData(PipelineSettingsValidation.MaxDraftPostedWithinDays)]
    [InlineData(null)]
    public void The_drafting_age_bound_accepts_both_ends_of_its_range_and_absence(int? value)
        => Assert.Empty(Validate(PipelineSettings.Default with { DraftPostedWithinDays = value }));

    // ---- Sending ----

    [Theory]
    [InlineData(-1)]
    [InlineData(PipelineSettingsValidation.MaxDailySendCap + 1)]
    public void The_daily_send_cap_is_bounded(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { DailySendCap = value }),
            problem => problem.Contains("daily send cap", StringComparison.Ordinal));

    /// <summary>
    /// Zero pauses the loop; a hundred is where the number stops being a cap.
    /// </summary>
    /// <remarks>
    /// Zero needs the drafting budget down with it, because a night that writes more letters than
    /// a day can send is refused - which is the cross-field rule saying that a paused loop should
    /// not still be buying prose.
    /// </remarks>
    [Fact]
    public void The_daily_send_cap_accepts_both_ends_of_its_range()
    {
        Assert.Empty(Validate(PipelineSettings.Default with
        {
            DailySendCap = 0,
            DraftsPerNight = 0,
        }));

        Assert.Empty(Validate(PipelineSettings.Default with
        {
            DailySendCap = PipelineSettingsValidation.MaxDailySendCap,
        }));
    }

    /// <summary>
    /// Zero days of silence is refused because it is a broken window rather than a short one.
    /// </summary>
    /// <remarks>
    /// The fold asks whether the elapsed time since the last activity exceeds the threshold, so at
    /// zero every application is stale the instant after it is recorded and the chase list becomes
    /// the whole list.
    /// </remarks>
    [Theory]
    [InlineData(PipelineSettingsValidation.MinChaseAfterDays - 1)]
    [InlineData(PipelineSettingsValidation.MaxChaseAfterDays + 1)]
    public void The_chase_window_is_bounded(int value)
        => Assert.Contains(
            Validate(PipelineSettings.Default with { ChaseAfterDays = value }),
            problem => problem.Contains("Chasing must start", StringComparison.Ordinal));

    [Theory]
    [InlineData(PipelineSettingsValidation.MinChaseAfterDays)]
    [InlineData(PipelineSettingsValidation.MaxChaseAfterDays)]
    public void The_chase_window_accepts_both_ends_of_its_range(int value)
        => Assert.Empty(Validate(PipelineSettings.Default with { ChaseAfterDays = value }));

    /// <summary>
    /// The six levers that floor at zero, all off at once.
    /// </summary>
    /// <remarks>
    /// Zero is a meaningful value on every one of them - the off switch, or "let the budget
    /// decide" - which is why only the three whose floor is one carry a <c>Min</c> constant. A
    /// candidate who has switched the whole pipeline off has configured something coherent rather
    /// than something to refuse.
    /// </remarks>
    [Fact]
    public void Every_lever_that_floors_at_zero_may_be_zero_at_the_same_time()
        => Assert.Empty(Validate(PipelineSettings.Default with
        {
            AssessmentsPerNight = 0,
            AssessmentThreshold = 0,
            RecentSharePercent = 0,
            DraftsPerNight = 0,
            DraftMinAssessmentScore = 0,
            DailySendCap = 0,
        }));

    // ---- Reporting ----

    /// <summary>
    /// Every problem at once: four bad fields is one save, not four.
    /// </summary>
    /// <remarks>
    /// The same contract <c>ScraperSearchValidation.Validate</c> keeps. A validator that stops at
    /// the first problem turns a form with four mistakes into four rejected saves, and the person
    /// filling it in learns about them one at a time.
    /// </remarks>
    [Fact]
    public void All_problems_are_reported_together_rather_than_the_first()
    {
        var problems = Validate(PipelineSettings.Default with
        {
            AssessmentsPerNight = -1,
            RecentWindowDays = 0,
            DraftPostedWithinDays = 900,
            ChaseAfterDays = 4_000,
        });

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("Assessments per night", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("recent window", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("postings from the last", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Chasing must start", StringComparison.Ordinal));
    }

    /// <summary>The refused number is named, so the person can see what was read.</summary>
    [Fact]
    public void A_refused_value_is_quoted_back_in_its_own_message()
        => Assert.Contains(
            Validate(PipelineSettings.Default with { AssessmentsPerNight = 900 }),
            problem => problem.Contains("900", StringComparison.Ordinal));

    // ---- The two cross-field rules ----

    /// <summary>
    /// A drafting floor below the judgement threshold is refused rather than tolerated.
    /// </summary>
    /// <remarks>
    /// A posting under the threshold is never judged, so it carries no assessment score for the
    /// floor to read - a floor lowered to admit it admits nothing. It is refused because a setting
    /// that does nothing is worse than one that is rejected: the candidate believes they have
    /// asked for more drafts and gets the same ones.
    ///
    /// The message names both numbers, because the reader has to be able to see which pair
    /// disagreed - the two are read against different columns, so neither is wrong on its own.
    /// </remarks>
    [Fact]
    public void The_drafting_floor_may_not_sit_below_the_assessment_threshold()
    {
        var problem = Assert.Single(Validate(PipelineSettings.Default with
        {
            AssessmentThreshold = 60,
            DraftMinAssessmentScore = 50,
        }));

        Assert.Contains("50", problem, StringComparison.Ordinal);
        Assert.Contains("60", problem, StringComparison.Ordinal);
    }

    /// <summary>Equal is allowed: the rule is "at least", not "above".</summary>
    /// <remarks>
    /// The shipped pair is 80 over 45 and the pair a candidate is most likely to type is two
    /// copies of one number, which has to save.
    /// </remarks>
    [Fact]
    public void A_drafting_floor_equal_to_the_threshold_is_allowed()
        => Assert.Empty(Validate(PipelineSettings.Default with
        {
            AssessmentThreshold = 80,
            DraftMinAssessmentScore = 80,
        }));

    /// <summary>
    /// A night may not write more letters than a day may send.
    /// </summary>
    /// <remarks>
    /// The corpus is re-scraped nightly, so the surplus is not merely early: several of those
    /// documents are tailored to adverts that will have gone before the queue could reach them.
    /// <c>GenerateApplicationsFunction</c> clamped this silently, and a setting that is silently
    /// clamped is a setting that lies to the person who typed it - they ask for forty, the page
    /// saves, and the pass writes twenty-five every night with nothing anywhere saying why.
    /// </remarks>
    [Fact]
    public void Drafts_per_night_may_not_exceed_the_daily_send_cap()
    {
        var problem = Assert.Single(Validate(PipelineSettings.Default with
        {
            DraftsPerNight = 20,
            DailySendCap = 5,
        }));

        Assert.Contains("20", problem, StringComparison.Ordinal);
        Assert.Contains("5", problem, StringComparison.Ordinal);
    }

    /// <summary>The effective ceiling on drafts is the lower of the constant and the send cap.</summary>
    [Fact]
    public void The_send_cap_is_the_ceiling_on_drafts_wherever_it_is_the_lower_of_the_two()
    {
        // Ten is inside MaxDraftsPerNight and outside this candidate's own cap.
        Assert.NotEmpty(Validate(PipelineSettings.Default with { DraftsPerNight = 10, DailySendCap = 9 }));

        Assert.Empty(Validate(PipelineSettings.Default with { DraftsPerNight = 10, DailySendCap = 10 }));
    }

    /// <summary>
    /// A refused number does not also generate a second message derived from it.
    /// </summary>
    /// <remarks>
    /// The one place the validator deviates from "report everything", and deliberately: a
    /// conclusion drawn from a value that was refused a line earlier tells the reader nothing they
    /// can act on and pushes the message that matters further down the list. Every field's own
    /// problem is still reported - only the derived one is withheld.
    ///
    /// Each of these is a value that would satisfy both halves of a cross-field rule's
    /// preconditions if the rule ran over it: 26 drafts exceeds a cap of 25, and a floor of -1
    /// sits below a threshold of 45.
    /// </remarks>
    [Fact]
    public void A_value_already_refused_does_not_produce_a_second_cross_field_message()
    {
        var drafts = Assert.Single(
            Validate(PipelineSettings.Default with { DraftsPerNight = PipelineSettingsValidation.MaxDraftsPerNight + 1 }));
        Assert.Contains("Drafts per night", drafts, StringComparison.Ordinal);

        var floor = Assert.Single(Validate(PipelineSettings.Default with { DraftMinAssessmentScore = -1 }));
        Assert.Contains("drafting assessment floor", floor, StringComparison.Ordinal);

        var cap = Assert.Single(Validate(PipelineSettings.Default with { DailySendCap = -1 }));
        Assert.Contains("daily send cap", cap, StringComparison.Ordinal);

        var threshold = Assert.Single(
            Validate(PipelineSettings.Default with { AssessmentThreshold = PipelineSettingsValidation.MaxAssessmentThreshold + 1 }));
        Assert.Contains("assessment threshold", threshold, StringComparison.Ordinal);
    }
}
