namespace JobPlatform.Core.Settings;

/// <summary>
/// What a candidate's <see cref="PipelineSettings"/> must satisfy before they are allowed to cost
/// a night.
/// </summary>
/// <remarks>
/// Pure and Azure-free, like <c>ScraperSearchValidation</c>, <c>MetricsCalculator</c> and
/// <c>MatchScorer</c> - so every bound is assertable exactly rather than through an HTTP status.
/// It is modelled on <c>ScraperSearchValidation</c> deliberately and closely: public constants for
/// the bounds so a column, a validator and a form read the same number, and one function
/// returning every problem.
///
/// <b>Every bound here is a cost bound, not a taste one, and the cost is a nightly bill.</b> Six
/// of the nine settings decide how many calls go out to a language model between one scrape and
/// the next: judgements on the bulk deployment, drafts on the writing deployment priced roughly
/// twenty-five times it. The other three decide how much of that spending can ever turn into an
/// application. None of them is a preference about how the product looks.
///
/// <b>These numbers are per candidate, and nothing bounds the sum across candidates. There is no
/// coalescing and no cap.</b> This is the same hole <c>ScraperSearchValidation</c> writes down for
/// searches - it bounds one search and nothing bounds the total, so two users configuring the same
/// search scrape it twice and a run costs the sum of every enabled search - and it is repeated
/// here rather than left to be discovered, because the shape is identical and the bill is larger.
/// The nightly passes iterate profiles: the sweep's budget is per profile so that a second
/// candidate does not go unjudged because the first filled the batch, and the writing pass says
/// the same thing about documents. So ten candidates each configured at the ceilings on this
/// class is two thousand judgements, two hundred and fifty drafts on the expensive deployment and
/// a thousand applications recordable in a day, and <b>nothing in this file refuses that</b>.
/// <b>What actually bounds a run as a whole is the wall clock</b> - the timer's minutes, the HTTP
/// trigger's roughly 230 seconds - which is a bound the platform enforces by cutting the run off,
/// leaving verdicts null with nothing saying why. That is a poor way to find out.
///
/// Saying so explicitly is the point. It is a deliberate choice rather than an oversight: a
/// per-tenant total would need a notion of tenancy this system does not have, and the deployment
/// this runs on has one candidate. Anybody adding a second should read this paragraph as the
/// thing to fix rather than as reassurance that it was considered.
/// </remarks>
public static class PipelineSettingsValidation
{
    /// <summary>
    /// The most judgements one candidate may buy in a night.
    /// </summary>
    /// <remarks>
    /// Five times the shipped forty, and <b>it is not the real ceiling</b>. What actually stops a
    /// sweep is the wall clock: the timer gets minutes, an HTTP trigger gets roughly 230 seconds,
    /// and forty pairs at raised reasoning effort already does not fit the shorter one - the first
    /// real sweep after the corpus was extracted was cut off before the model ran at all, leaving
    /// every verdict null and nothing saying why. So read this as "past here nobody could have
    /// meant it" rather than as "up to here is fine". A candidate raising it towards this number
    /// is trading a bounded night for a night that may not finish.
    ///
    /// Two hundred is also where the arithmetic underneath stops behaving as designed for a
    /// different reason: the sweep reserves a share of the shortlist for recent postings by
    /// integer percentage, and the percent and the two-thirds fraction it replaces first disagree
    /// at a shortlist budget of one hundred. See
    /// <see cref="PipelineSettings.RecentSharePercent"/>.
    /// </remarks>
    public const int MaxAssessmentsPerNight = 200;

    /// <summary>
    /// The highest match score a candidate may demand before a judgement is bought.
    /// </summary>
    /// <remarks>
    /// The scorer's range, so this is a bound on the type rather than on the policy: at 100 only a
    /// perfect arithmetic match is ever judged, which is a way of switching the judgement layer
    /// off that reads as though it were switching it on -
    /// <see cref="PipelineSettings.AssessmentsPerNight"/> set to zero is the honest spelling of
    /// that.
    ///
    /// <b>A separate constant from <see cref="MaxDraftMinAssessmentScore"/> even though the number
    /// is identical</b>, the way <c>SubmissionLimits.MaxFinalUrlLength</c> is separate from
    /// <c>MaxApplyUrlLength</c>. Two bounds that agree today are not one decision - and here that
    /// rule matters more than usual, because these bound two of the four thresholds this system
    /// keeps apart on purpose. One constant behind both would be the collapse
    /// <see cref="PipelineSettings"/> exists to refuse, arriving through the validator instead of
    /// through the settings.
    /// </remarks>
    public const int MaxAssessmentThreshold = 100;

    /// <summary>
    /// The most of the shortlist that may be reserved for recent postings.
    /// </summary>
    /// <remarks>
    /// A percentage, so the bound is the type's. <b>100 is legal and is worth understanding before
    /// choosing it:</b> the whole shortlist goes to postings inside
    /// <see cref="PipelineSettings.RecentWindowDays"/>, and the top-down draw over the corpus fills
    /// only what the reservation could not. On any day with enough arrivals to fill it, the
    /// backlog stops draining entirely - which is the absorbing state the reservation was designed
    /// to avoid, reachable here only by asking for it in so many words. It is not refused, because
    /// "judge today's postings and nothing else" is a coherent thing for a candidate with no
    /// backlog to want.
    /// </remarks>
    public const int MaxRecentSharePercent = 100;

    /// <summary>The shortest window that can mean anything: one day.</summary>
    /// <remarks>
    /// Zero is refused rather than clamped. <c>PostingAge.Cutoff</c> treats a zero or negative
    /// window as "since now" - the honest answer to a nonsense request on a path that has to be
    /// total - but as a stored setting that would leave the reservation unfilled every night and
    /// falling back to the top-down draw, which is indistinguishable from the reservation being
    /// switched off. The setting that actually switches it off is
    /// <see cref="PipelineSettings.RecentSharePercent"/> at zero, and a person should have to type
    /// that one.
    /// </remarks>
    public const int MinRecentWindowDays = 1;

    /// <summary>
    /// The longest a posting may be and still count as "recent" for the reservation.
    /// </summary>
    /// <remarks>
    /// Thirty days, which is where the reservation has plainly stopped meaning "today's work". The
    /// scoring pass looks back forty-five days, so at a window of thirty most of the eligible
    /// corpus qualifies as recent, the reserved share and the top-down draw select from nearly the
    /// same rows, and the split quietly becomes the single draw it was built to replace. Nothing
    /// breaks; the feature just stops doing anything, which is the failure worth bounding because
    /// it is the one nobody notices.
    /// </remarks>
    public const int MaxRecentWindowDays = 30;

    /// <summary>
    /// The most drafts one candidate may buy in a night.
    /// </summary>
    /// <remarks>
    /// Twenty-five, which is the clamp <c>GenerateApplicationsFunction</c> already applies -
    /// <c>Math.Clamp(configured, 0, SubmissionLimits.MaxSubmittedPerDay)</c> - promoted from a
    /// silent correction into a refusal. <b>A setting that is silently clamped is a setting that
    /// lies to the person who typed it:</b> they asked for forty, the page saves, and the pass
    /// writes twenty-five every night with nothing anywhere saying why.
    ///
    /// <b>A separate constant from <c>SubmissionLimits.MaxSubmittedPerDay</c> even though it
    /// agrees with it today</b>, and the direction of the coupling is why. That constant is now
    /// only the <i>default</i> for <see cref="PipelineSettings.DailySendCap"/>, which a candidate
    /// may raise as far as <see cref="MaxDailySendCap"/>; if this bound were written as a
    /// reference to it, raising the shipped send cap would silently raise the ceiling on the
    /// expensive deployment's nightly bill. Sending is cheap and drafting is not, so the two
    /// ceilings move for different reasons and are two numbers.
    ///
    /// The effective ceiling on drafts is therefore the lower of this and the candidate's own
    /// <see cref="PipelineSettings.DailySendCap"/>, which the cross-field rule in
    /// <see cref="Validate"/> enforces.
    /// </remarks>
    public const int MaxDraftsPerNight = 25;

    /// <summary>
    /// The highest assessment score a candidate may demand before a document is written.
    /// </summary>
    /// <remarks>
    /// The assessor's range. At 100 only a posting the model scored perfectly is written for,
    /// which will usually be none of them -
    /// <see cref="PipelineSettings.DraftsPerNight"/> at zero is the honest way to stop the pass.
    ///
    /// <b>Separate from <see cref="MaxAssessmentThreshold"/> on purpose</b>; see the note there.
    /// The two bound different quantities that share a range - the model's assessment score and
    /// the deterministic match score - and this file must not be the place the distinction is
    /// lost.
    /// </remarks>
    public const int MaxDraftMinAssessmentScore = 100;

    /// <summary>The shortest posted-within bound that selects anything: one day.</summary>
    /// <remarks>
    /// Zero is refused for the reason <see cref="MinRecentWindowDays"/> refuses it, and the
    /// distinction that matters here is against <b>null</b>, which is not the same value: null is
    /// "no bound at all" and is the default, where zero through <c>PostingAge.Cutoff</c> would
    /// mean "posted since this instant" and select almost nothing. Two states that a form renders
    /// as an empty box have to be different bytes on the wire, exactly as the scraper config
    /// insists an option nobody chose is omitted rather than sent as null.
    /// </remarks>
    public const int MinDraftPostedWithinDays = 1;

    /// <summary>The longest posted-within bound worth storing.</summary>
    /// <remarks>
    /// Ninety days, and it is a typo guard rather than a policy. The scoring pass looks back
    /// forty-five days, so no bound beyond that selects a single extra posting - there is no match
    /// row older than the lookback for it to admit. Ninety is twice the lookback, comfortably past
    /// the point where the setting stops selecting, which is what makes it a safe place to refuse
    /// a mistyped 900.
    /// </remarks>
    public const int MaxDraftPostedWithinDays = 90;

    /// <summary>
    /// The most applications a candidate may record as sent in one UTC day.
    /// </summary>
    /// <remarks>
    /// A hundred, and it is the far end of the sentence that justifies the shipped twenty-five:
    /// set well above what a person does in a day and well below what a loop does in a minute.
    /// Around a hundred those two stop being distinguishable - a hundred applications in a day is
    /// no longer a person having a productive week, and it is still nothing at all to a client
    /// stuck in a retry loop, which is the failure the cap exists to bound. Past here the number
    /// has stopped being a cap and become a formality.
    ///
    /// A hundred is also what <c>SubmissionLimits.MaxSubmittedFieldCount</c> chose for a
    /// list-length bound on the same reasoning, and the two are unrelated numbers that happen to
    /// agree; neither should be written in terms of the other.
    /// </remarks>
    public const int MaxDailySendCap = 100;

    /// <summary>The shortest silence that can make an application stale: one day.</summary>
    /// <remarks>
    /// Zero is refused because it is not a short window, it is a broken one: the fold asks whether
    /// the elapsed time since the last activity exceeds the threshold, so at zero every
    /// application is stale the instant after it is recorded and the chase list becomes the whole
    /// list. That is precisely the failure a fortnight was chosen to avoid - a warning that fires
    /// on the ordinary case is one people learn to scroll past - arrived at from the other
    /// direction.
    /// </remarks>
    public const int MinChaseAfterDays = 1;

    /// <summary>The longest silence still described as staleness rather than as an ending.</summary>
    /// <remarks>
    /// A year. Past it "chase this up" is not an instruction anybody acts on, and an application
    /// nobody has heard anything about for a year is over rather than quiet - it is just that no
    /// employer sends the message that would close it, which is why staleness is derived at all.
    /// A candidate who wants no chasing should set the number high and read the list; there is no
    /// null here, because staleness is a fold over every submission and the fold has to answer.
    /// </remarks>
    public const int MaxChaseAfterDays = 365;

    /// <summary>
    /// Every problem with <paramref name="settings"/>, or an empty list.
    /// </summary>
    /// <remarks>
    /// All of them rather than the first: a form with four bad fields should say so once, not four
    /// saves in a row. This is the same contract <c>ScraperSearchValidation.Validate</c> keeps and
    /// for the same reason.
    ///
    /// <b>The two cross-field rules are checked only over values that are individually in
    /// range</b>, which is the one place this deviates from "report everything". A rule derived
    /// from a number already reported as impossible tells the reader nothing they can act on and
    /// pushes the message that matters further down the list. Every field's own problem is still
    /// reported, so nothing is hidden - only a conclusion drawn from a value that was refused a
    /// line earlier.
    /// </remarks>
    public static IReadOnlyList<string> Validate(PipelineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = new List<string>();

        // The four values the cross-field rules compare, tested for their own bounds first. A
        // comparison between two numbers, one of which has already been refused, produces a
        // second message that cannot be acted on separately from the first.
        var assessmentThresholdInRange =
            settings.AssessmentThreshold is >= 0 and <= MaxAssessmentThreshold;
        var draftFloorInRange =
            settings.DraftMinAssessmentScore is >= 0 and <= MaxDraftMinAssessmentScore;
        var draftsPerNightInRange =
            settings.DraftsPerNight is >= 0 and <= MaxDraftsPerNight;
        var sendCapInRange =
            settings.DailySendCap is >= 0 and <= MaxDailySendCap;

        if (settings.AssessmentsPerNight is < 0 or > MaxAssessmentsPerNight)
        {
            problems.Add(
                $"Assessments per night must be between 0 and {MaxAssessmentsPerNight}; " +
                $"got {settings.AssessmentsPerNight}.");
        }

        if (!assessmentThresholdInRange)
        {
            problems.Add(
                $"The assessment threshold must be between 0 and {MaxAssessmentThreshold}; " +
                $"got {settings.AssessmentThreshold}.");
        }

        if (settings.RecentSharePercent is < 0 or > MaxRecentSharePercent)
        {
            problems.Add(
                $"The recent share must be between 0 and {MaxRecentSharePercent} percent; " +
                $"got {settings.RecentSharePercent}.");
        }

        if (settings.RecentWindowDays is < MinRecentWindowDays or > MaxRecentWindowDays)
        {
            problems.Add(
                $"The recent window must be between {MinRecentWindowDays} and " +
                $"{MaxRecentWindowDays} days; got {settings.RecentWindowDays}.");
        }

        if (!draftsPerNightInRange)
        {
            problems.Add(
                $"Drafts per night must be between 0 and {MaxDraftsPerNight}; " +
                $"got {settings.DraftsPerNight}.");
        }

        if (!draftFloorInRange)
        {
            problems.Add(
                $"The drafting assessment floor must be between 0 and " +
                $"{MaxDraftMinAssessmentScore}; got {settings.DraftMinAssessmentScore}.");
        }

        // Null is not a value out of range, it is the absence of a bound and the default. Only a
        // number that was actually supplied is checked.
        if (settings.DraftPostedWithinDays is { } within
            and (< MinDraftPostedWithinDays or > MaxDraftPostedWithinDays))
        {
            problems.Add(
                $"Drafting may only be limited to postings from the last " +
                $"{MinDraftPostedWithinDays} to {MaxDraftPostedWithinDays} days; got {within}. " +
                $"Leave it unset for postings of every age.");
        }

        if (!sendCapInRange)
        {
            problems.Add(
                $"The daily send cap must be between 0 and {MaxDailySendCap}; " +
                $"got {settings.DailySendCap}.");
        }

        if (settings.ChaseAfterDays is < MinChaseAfterDays or > MaxChaseAfterDays)
        {
            problems.Add(
                $"Chasing must start between {MinChaseAfterDays} and {MaxChaseAfterDays} days " +
                $"after the last activity; got {settings.ChaseAfterDays}.");
        }

        // Drafting for a band that is never judged buys prose for postings that can never carry a
        // verdict. The two numbers are read against different columns - the threshold against the
        // deterministic match score, the floor against the model's assessment score - which is
        // exactly why this has to be a stated rule rather than an obvious one. A posting whose
        // match score never reaches the threshold is never sent to the assessor, so it holds no
        // assessment score at all; and a null clears no floor at any value, because reading "not
        // judged" as "let it through" turns a safety rail into a way past one. So a floor set
        // below the threshold reads as widening the drafting band and widens nothing - the rows it
        // means to admit are the rows nothing has judged. It is refused rather than tolerated
        // because a setting that does nothing is worse than one that is rejected: the candidate
        // believes they have asked for more drafts and gets the same ones.
        if (assessmentThresholdInRange
            && draftFloorInRange
            && settings.DraftMinAssessmentScore < settings.AssessmentThreshold)
        {
            problems.Add(
                $"The drafting assessment floor ({settings.DraftMinAssessmentScore}) must be at " +
                $"least the assessment threshold ({settings.AssessmentThreshold}). Postings below " +
                $"the threshold are never judged, so they carry no assessment score for the " +
                $"drafting floor to read.");
        }

        // The reasoning already written on ApplicationGenerationOptions.DocumentsPerNight: a night
        // that writes more letters than a day can send is buying prose for adverts nobody will
        // reach. The corpus is re-scraped nightly, so the surplus is not merely early - several of
        // those documents are tailored to adverts that will have gone by the time the queue could
        // have reached them. The unit cost of a draft has fallen since that was written, because
        // the CV is chosen from the candidate's library rather than generated, and the rule is
        // unchanged for the reason given there: the argument was never about what one draft costs.
        if (draftsPerNightInRange
            && sendCapInRange
            && settings.DraftsPerNight > settings.DailySendCap)
        {
            problems.Add(
                $"Drafts per night ({settings.DraftsPerNight}) may not exceed the daily send cap " +
                $"({settings.DailySendCap}). A night that writes more letters than a day can send " +
                $"is buying prose for adverts nobody will reach.");
        }

        return problems;
    }
}
