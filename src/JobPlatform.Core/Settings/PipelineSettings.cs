namespace JobPlatform.Core.Settings;

/// <summary>
/// The nine levers one candidate may set on their own pipeline: what gets judged, what gets
/// written, and what gets sent.
/// </summary>
/// <remarks>
/// <b>Pure and Azure-free, with no clock of its own</b>, like <c>MatchScorer</c>,
/// <c>MetricsCalculator</c> and <c>SubmissionQuota</c>. That is what lets every default be
/// asserted exactly rather than through a database round trip - and the defaults are the part of
/// this type that has to be right, because they are what an unconfigured deployment runs.
///
/// <b>Every default here is the constant the code already runs, and none of them is a new
/// opinion.</b> Each member carries the reasoning from the constant it replaces rather than a
/// fresh justification, because the numbers were argued once already and re-arguing them in a
/// second file is how two spellings of one decision start to drift. Where a member's default
/// differs in <i>spelling</i> from the constant it replaces - the recent share is a percent here
/// and a numerator over a denominator there - the member says so and says where the two first
/// disagree.
///
/// <b>Named members rather than a positional record, and that is a guard rather than a
/// preference.</b> Eight of the nine are <c>int</c> and the ninth is <c>int?</c>, all in
/// overlapping small ranges, so a positional constructor is one in which transposing any two
/// arguments compiles, passes review and quietly reconfigures somebody's night.
/// <c>ApplyableQuery</c> and <c>SubmissionEvidence</c> already make that argument about four
/// members; it is a stronger one with nine of a single type.
///
/// <b>Property initialisers rather than optional constructor parameters</b>, and that is what
/// makes "an unconfigured deployment is unchanged" true rather than merely intended. A JSON
/// document that omits a property leaves the initialiser standing under every serialiser and
/// every version of one; a positional record's defaults depend on the serialiser choosing to
/// honour C# default parameter values, which is a promise about somebody else's library rather
/// than about this code. A partial document - the shape a settings form actually sends - is
/// therefore a partial override by construction.
///
/// <b>These are levers on what the pipeline buys. None of them changes what a match means.</b>
/// A posting's age and its reachability are claims on the budget and never on the match, and
/// <see cref="RecentWindowDays"/> and <see cref="RecentSharePercent"/> keep exactly that shape:
/// they decide which rows are <i>eligible</i> for a reserved share of the judgement budget and
/// nothing else. No score, ranking, verdict or floor reads either, and the pressure to let a new
/// lever "just nudge" a score is the thing to refuse when a tenth one is proposed.
///
/// <b>Four thresholds exist in this system and this record surfaces exactly two of them.</b>
/// They answer four different questions, and two of them were briefly collapsed into one constant
/// on the grounds that they shared a value, which was a mistake:
/// <list type="bullet">
///   <item><description>
///     <c>MatchRanker.FusionFloor</c> (80) - where the embedding earns its weight. <b>Not a
///     setting, and not referenced anywhere in this record.</b> It is a fitted value: re-run over
///     a holdout at several floors, every value from 70 to 92 beats the score significantly and
///     45 does not, and 80 is taken from inside that range because it is the boundary the
///     original research already named rather than the argmax. A number a candidate can type is a
///     number no measurement can be held to.
///   </description></item>
///   <item><description>
///     <see cref="AssessmentThreshold"/> (45) - where buying a judgement is worth it.
///     <b>Surfaced here.</b>
///   </description></item>
///   <item><description>
///     <see cref="DraftMinAssessmentScore"/> (80) - where writing a document is worth the
///     expensive deployment. <b>Surfaced here.</b>
///   </description></item>
///   <item><description>
///     <c>CvVariantSelector.SelectionFloor</c> (50) - how much of an advert a CV must answer
///     before it is sent at all. <b>Not a setting, and not referenced anywhere in this
///     record.</b> It is derived from the partial-credit table - every adjacency relation is
///     priced below half, so a variant answering every requirement by transferable ground alone
///     tops out at 45 and fails - and a candidate lowering it would be sending a CV on
///     resemblance.
///   </description></item>
/// </list>
/// <b>No two of the four are labelled with the same word, and none of them may be.</b> The two
/// that are settings keep the wording of the constants they replace, so a reader searching for
/// either finds the same phrase in both places; the two that are not are named in full wherever
/// they are mentioned, so that nobody reaches for one because it happens to read 80 today.
///
/// <b>These settings belong to one candidate and nothing bounds their sum.</b> The bounds live in
/// <see cref="PipelineSettingsValidation"/>, and so does the cost argument for having them.
/// </remarks>
public sealed record PipelineSettings
{
    // ---- Judgement. Read by the nightly match sweep. ----

    /// <summary>
    /// How many postings the model judges per night for this candidate. Default 40.
    /// </summary>
    /// <remarks>
    /// Replaces <c>MatchSweepFunction.MaxAssessments</c>.
    ///
    /// <b>Forty, and its history is worth carrying rather than restating.</b> It was briefly
    /// ninety, for the run on 2026-08-28, to build an assessed set worth measuring a
    /// verdict-aware ranking against; that run sent ninety pairs in nine batches of ten, four
    /// batches came back usable, five were discarded whole, forty of the ninety were written and
    /// nothing failed. Forty is what it settled back to.
    ///
    /// <b>Per candidate rather than per run, exactly as the constant it replaces is.</b> A second
    /// profile must not go without judgements because the first one filled the batch. What bounds
    /// a run as a whole is the wall clock, which is not something this record controls - read the
    /// remarks on <see cref="PipelineSettingsValidation.MaxAssessmentsPerNight"/> before taking a
    /// large value here as permission.
    ///
    /// <b>A quarter of it is not spent on this candidate at all.</b> The sweep takes
    /// <c>MatchSweepFunction.MeasurementAssessments</c> - ten, itself capped at a quarter of the
    /// budget - for a stratified sample drawn across the bands below the shortlist, because
    /// selecting top-down produces labels that describe the top of the score range and nothing
    /// else. So forty is thirty of shortlist and ten of measurement, and lowering this lowers
    /// both. The sample is not overhead to be tuned away: those rows are merged into the same
    /// batches and the assessor sends the profile - the larger half of the prompt - once per
    /// batch, so ten rows riding along cost ten adverts' worth of tokens.
    ///
    /// <b>Zero is a real value and it is the off switch.</b> The sweep's scoring pass needs no
    /// model at all and still writes ranked matches, so zero here keeps the shortlist and stops
    /// buying verdicts - the same control <c>ApplicationGenerationOptions.DocumentsPerNight</c>
    /// gives over the writing pass, and like that one it needs no deploy.
    /// </remarks>
    public int AssessmentsPerNight { get; init; } = 40;

    /// <summary>
    /// The match score a pair must clear before the model is asked about it. Default 45.
    /// </summary>
    /// <remarks>
    /// Replaces <c>MatchSweepFunction.AssessmentThreshold</c>.
    ///
    /// <b>The single most important number in the sweep, because it is the one that decides what
    /// gets paid for.</b> Deliberately not high: the arithmetic under-scores a candidate whose
    /// relevant experience is in prose the extractor read cautiously, and the model exists
    /// precisely to catch that. A threshold tuned to look efficient would filter out the cases
    /// worth judging.
    ///
    /// <b>Not <c>MatchRanker.FusionFloor</c>, and tying the two together was a mistake.</b> They
    /// were briefly one constant on the reasoning that "the band the model is spent on and the
    /// band the embedding re-orders are the same band". They are not the same question. This one
    /// asks where a judgement is worth buying, and the answer is "wherever the arithmetic might
    /// be wrong", which is low by design. The other asks where the embedding carries signal, and
    /// the holdout put that at 80 - so coupling them silently stopped the model from ever looking
    /// below 80, and with it removed the only source of labels that could show whether the score
    /// works down there. <b>Two constants that happened to share a value are not one constant</b>,
    /// which is why <c>MatchRanker.FusionFloor</c> is absent from this record altogether rather
    /// than present with a warning attached to it.
    ///
    /// <b>It reads the deterministic match score, where
    /// <see cref="DraftMinAssessmentScore"/> reads the model's.</b> Two numbers on 0..100 that
    /// mean different things, which is the whole reason the ordering between them has to be
    /// written down in <see cref="PipelineSettingsValidation"/> rather than left to look obvious.
    ///
    /// <b>Zero does not mean "judge everything".</b> It means every scored pair is a candidate and
    /// the budget alone decides which are drawn, so it changes what the shortlist is drawn
    /// <i>from</i> rather than what a night costs. <see cref="AssessmentsPerNight"/> is the number
    /// that costs money.
    ///
    /// <b>Lowering it does not lower the measurement sample's floor.</b> The sweep's bands start
    /// at 45 because below it no pair was a candidate for judgement at all; a threshold set below
    /// 45 opens a band the stratified sample does not cover, which is a gap in the evidence rather
    /// than a fault in the run - but it is a consequence worth knowing before somebody reads the
    /// resulting labels as describing the whole range.
    /// </remarks>
    public int AssessmentThreshold { get; init; } = 45;

    /// <summary>
    /// The percentage of the shortlist reserved for postings inside
    /// <see cref="RecentWindowDays"/>. Default 67.
    /// </summary>
    /// <remarks>
    /// Replaces <c>MatchSweepFunction.RecentShareNumerator</c> over
    /// <c>MatchSweepFunction.RecentShareDenominator</c> - two thirds.
    ///
    /// <b>A reservation rather than an ordering, and that distinction is the whole design.</b>
    /// The system exists to answer a day's postings on the day they appear: an application sent a
    /// week after the advert went up is competing against a shortlist the employer has already
    /// drawn, so a judgement bought a week late has bought very little. Selecting top-down by
    /// score alone does not deliver that - pairs above the threshold accumulate, the highest are
    /// judged first whatever their age, and a corpus with a backlog spends every night on the
    /// backlog, which is exactly the state a first sweep over forty-five days of postings starts
    /// in.
    ///
    /// <b>Ordering by age instead is the obvious thing to reach for and it is worse.</b> Age says
    /// nothing about whether the candidate fits: a fresh posting scoring 46 is not a better use of
    /// a judgement than a three-day-old one scoring 97, and sorting by age puts it first anyway.
    /// Worse, it is absorbing - once daily arrivals exceed the budget, nothing older is ever
    /// judged again. So the budget splits, the recent draw is ordered by score like every other
    /// draw, and whatever the reservation cannot fill goes back to the top-down draw over the
    /// whole corpus: a quiet day costs nothing and a backlog still drains at the remaining share a
    /// night.
    ///
    /// <b>A percent rather than a fraction, and 67 rather than 66, because a person configures
    /// this.</b> A settings surface cannot sensibly ask somebody for a numerator and a
    /// denominator, and two fields that must be read together are two fields that will be
    /// half-edited. 67 is the integer percent nearest two thirds, and the rounding is upward,
    /// which is the safe direction: it can reserve one more row for today and never one fewer.
    ///
    /// <b>The default is behaviour-preserving at the shipped budget, and it is worth being exact
    /// about where it stops being so.</b> The consumer computes
    /// <c>reserved = shortlistBudget * RecentSharePercent / 100</c> in integer arithmetic, which
    /// floors, exactly as <c>shortlistBudget * 2 / 3</c> does today. At the shipped forty
    /// assessments the shortlist budget is thirty after the measurement sample, and both spellings
    /// give twenty. They first disagree at a shortlist budget of one hundred - 66 against 67 -
    /// which needs <see cref="AssessmentsPerNight"/> at 110, nearly three times the shipped forty,
    /// and the disagreement is one row.
    ///
    /// <b>Zero is a real value: no reservation, pure top-down.</b> It is what a candidate with no
    /// backlog to drain would choose, and the sweep already handles it - a reserved budget of zero
    /// skips the recent draw rather than issuing a query that can only answer nothing.
    /// </remarks>
    public int RecentSharePercent { get; init; } = 67;

    /// <summary>
    /// What the reservation above counts as "recent", in days. Default 3.
    /// </summary>
    /// <remarks>
    /// <b>This does not replace <c>PostingAge.DailyWindowDays</c>, and must never be read as
    /// doing so.</b> That constant is the system-wide definition of "posted about twenty-four
    /// hours ago" - the shortlist, the corpus search and the apply queue all answer to it, and
    /// every age filter in the system goes through it or one of its two relational transcriptions
    /// - and it stays exactly where it is. This is the sweep's own reservation window, which is a
    /// different question: <i>how far back may a posting be and still have a claim on tonight's
    /// reserved share</i>. The default equals <c>PostingAge.DailyWindowDays</c> because that is
    /// what the sweep passes today, so an unconfigured deployment is unchanged; the two are free
    /// to diverge afterwards and nothing here couples them.
    ///
    /// <b>Three, and the reasoning is the one <c>PostingAge.DailyWindowDays</c> already
    /// carries.</b> A day is the smallest unit either date column can be compared in, and
    /// <c>DatePosted</c> is a date rather than an instant - so "the last twenty-four hours" is
    /// already "today or yesterday", which is two - and the third day absorbs a scrape that did
    /// not run, a NAS that was off, or an ingest that drained after the sweep. The cost of the
    /// extra day is that a posting stays eligible for the reserved share for two nights after the
    /// one it should have been judged on, which is paid in duplicate consideration rather than in
    /// a missed job.
    ///
    /// <b>It decides eligibility and never rank.</b> The recent draw is ordered by score like
    /// every other draw, so widening this window admits more rows to the reserved share and
    /// promotes none of them; and it is not a way past <see cref="AssessmentThreshold"/> - a fresh
    /// posting the arithmetic rejected stays rejected.
    ///
    /// <b>Its floor is one where the underlying function tolerates zero.</b>
    /// <c>PostingAge.Cutoff</c> clamps a negative window at zero and reads it as "since now",
    /// which is the honest answer to a nonsense request on a path that is otherwise total. As a
    /// stored setting it would be worse than nonsense: the reservation would go unfilled every
    /// night and fall back to the top-down draw, which looks exactly like the feature having been
    /// switched off. The thing that actually means "no reservation" already exists and is
    /// <see cref="RecentSharePercent"/> set to zero.
    /// </remarks>
    public int RecentWindowDays { get; init; } = 3;

    // ---- Drafting. Read by the nightly application generation pass. ----

    /// <summary>
    /// How many drafts one nightly pass writes for this candidate. Default 10.
    /// </summary>
    /// <remarks>
    /// Replaces <c>ApplicationGenerationOptions.DocumentsPerNight</c>.
    ///
    /// <b>Ten, and it is the only number in the writing pass that is a bill.</b> Every other bound
    /// there protects a request or a provider; this one decides how many calls go to the
    /// deployment priced roughly twenty-five times the bulk one.
    ///
    /// <b>The ceiling above it is not arbitrary either, and it is the reason for the cross-field
    /// rule.</b> <see cref="DailySendCap"/> is how many applications may be recorded as sent in a
    /// UTC day, so a pass that wrote thirty would be buying twenty-five days' worth of calls on
    /// the writing deployment for a queue that can only spend a day's - and the corpus is
    /// re-scraped nightly, so several of those documents would be tailored to adverts nothing will
    /// ever apply to. <c>GenerateApplicationsFunction</c> clamps to that ceiling today rather than
    /// trusting the configured value; <see cref="PipelineSettingsValidation"/> refuses the
    /// combination instead, because a setting that is silently clamped is a setting that lies to
    /// the person who typed it.
    ///
    /// <b>Per candidate rather than per run</b>, matching <see cref="AssessmentsPerNight"/>: a
    /// second profile must not go without documents because the first one filled the batch.
    ///
    /// <b>What this buys has shrunk and the bound has deliberately not moved.</b> The CV is no
    /// longer written per posting - it is chosen from a library the candidate authored, which
    /// costs arithmetic rather than a model call - so a draft is now a covering letter and a
    /// handful of short answers. The bound stays where it is because the argument for it was never
    /// the unit cost: a night that writes more letters than a day can send is buying prose for
    /// adverts nobody will reach, whatever each one now costs.
    ///
    /// <b>Zero switches the pass off without a deploy</b>, which is the control worth having when
    /// the number is a bill.
    /// </remarks>
    public int DraftsPerNight { get; init; } = 10;

    /// <summary>
    /// The model's assessment score a posting must carry before a document is written for it.
    /// Default 80.
    /// </summary>
    /// <remarks>
    /// Replaces <c>ApplicationGenerationOptions.MinAssessmentScore</c>.
    ///
    /// <b>It must be the floor the unattended run pulls with, and that is the whole of its
    /// justification.</b> Set it higher than the run's and the queue offers postings whose
    /// documents were never written, which is the state that pass exists to end; set it lower and
    /// the pass buys drafts for postings the run will not look at. Eighty is what
    /// <c>list_applyable</c> is asked for by the apply loop, so eighty is what is written for. It
    /// is configurable for exactly that reason: it has to be able to equal what the run asks for,
    /// without a deploy.
    ///
    /// <b>This is a fourth number and not a fourth spelling of an existing one.</b>
    /// <c>MatchRanker.FusionFloor</c> is where the embedding earns its weight,
    /// <see cref="AssessmentThreshold"/> is where buying a judgement is worth it, and
    /// <c>CvVariantSelector.SelectionFloor</c> is how much of an advert a CV must answer before it
    /// is sent. This one asks where writing a document is worth the expensive deployment. Two of
    /// those were briefly collapsed into one constant on the grounds that they shared a value and
    /// that was a mistake - so <c>MatchRanker.FusionFloor</c> and
    /// <c>CvVariantSelector.SelectionFloor</c> appear nowhere in this record, are not settings,
    /// and must not become ones.
    ///
    /// <b>It is read against a different column from <see cref="AssessmentThreshold"/>, which is
    /// why the cross-field rule needs stating rather than assuming.</b> That one compares the
    /// deterministic match score the scorer computes for every pair; this one compares
    /// <c>JobMatches.AssessmentScore</c>, the number the model wrote after judging. Both are
    /// 0..100 and they are not the same quantity, so "80 is above 45" is arithmetic over two
    /// different scales - see <see cref="PipelineSettingsValidation"/> for what the ordering
    /// between them actually buys.
    ///
    /// <b>Zero is not "no floor", which is why it is allowed rather than refused.</b> A pair the
    /// model scored no number for does not clear a floor at any value, zero included - reading
    /// "not judged" as "let it through" turns a safety rail into a way past one - so zero means
    /// "anything the model has actually judged", which is a real request rather than a disabled
    /// check.
    /// </remarks>
    public int DraftMinAssessmentScore { get; init; } = 80;

    /// <summary>
    /// Only write for postings posted within this many days. Null - the default - for every age.
    /// </summary>
    /// <remarks>
    /// Replaces <c>ApplicationGenerationOptions.PostedWithinDays</c>.
    ///
    /// <b>Configurable for the same reason <see cref="DraftMinAssessmentScore"/> is: it has to be
    /// able to equal what the run asks for.</b> The pass writes for the set the apply queue
    /// returns and never for one of its own, and an unattended run that pulls
    /// <c>postedWithinDays: 1</c> against a pass that wrote for every age gets a queue of today's
    /// postings with no documents and a pile of drafts for adverts it will not ask about. The
    /// floor already had that failure mode; this is the second axis of it.
    ///
    /// <b>Null by default, which is the behaviour the pass has always had.</b> A default of one or
    /// three would be this record deciding the cadence for every deployment, and the cadence
    /// belongs to whoever configures the run. <see cref="RecentWindowDays"/> is a different
    /// question and that is why it is not nullable: it decides where a judgement budget goes and
    /// has to answer that every night whatever anybody configured.
    ///
    /// <b>Null and zero are different bytes and only one of them is allowed.</b> Null means "no
    /// bound"; zero through <c>PostingAge.Cutoff</c> would mean "since this instant", which
    /// selects almost nothing and reads as the pass being broken. So it is one to ninety when set
    /// and absent otherwise - the same "did not choose and chose nothing are different bytes on
    /// the wire" rule the scraper config already runs under.
    /// </remarks>
    public int? DraftPostedWithinDays { get; init; }

    // ---- Sending. Read by the submission write path and by the fold over the event log. ----

    /// <summary>
    /// How many applications may be recorded as <i>sent</i> in one UTC day. Default 25.
    /// </summary>
    /// <remarks>
    /// Replaces <c>SubmissionLimits.MaxSubmittedPerDay</c>.
    ///
    /// <b>A bound on the blast radius of a client that loops.</b> The server never submits
    /// anything, so the damage is a pipeline full of applications nobody made - but the whole
    /// point of the pipeline is that a person can trust it, and four hundred phantom rows destroys
    /// that as surely as four hundred real emails would. Set well above what a person does in a
    /// day and well below what a loop does in a minute.
    ///
    /// <b>It bounds <c>Submitted</c> events alone.</b> Recording that a hundred applications exist
    /// is fine - somebody may be importing a history - and claiming a hundred were sent today is
    /// not. They are counted by the event's own <c>AtUtc</c> rather than by when the row was
    /// written, so backdating a hundred events into one day is the same assertion and is capped
    /// the same way.
    ///
    /// <b>Making it configurable does not move where it is enforced.</b> It fires in
    /// <c>SubmissionRepository</c> and nowhere else, for the reason a rule enforced at the call
    /// sites survives exactly until somebody adds another call site, and there are already two.
    ///
    /// <b>Lowering it is safe and leaves a visible edge.</b> <c>SubmissionQuota.Remaining</c> is
    /// floored at zero precisely because lowering the cap leaves every day already past the new
    /// bound permanently past it - "minus three left" is not a state this system has, and handing
    /// a negative number to a model that is about to multiply it by something is how a bound
    /// becomes a suggestion.
    ///
    /// <b>Zero is a real value and it pauses the loop, but only for a run that plans first.</b>
    /// The quota is answered on <c>list_applyable</c>, where a run picks its batch before opening
    /// the first tab, and as a burn-down on <c>record_event</c>. A run that plans against a cap of
    /// zero does nothing and says so. A run that ignores the plan discovers the cap by being
    /// refused at <c>record_event</c>, which by the loop's design runs <i>after</i> the browser
    /// has sent the form - an application that exists in the world and cannot be recorded, which
    /// is the worst state this system has, because every later decision reads the log rather than
    /// the world.
    /// </remarks>
    public int DailySendCap { get; init; } = 25;

    /// <summary>
    /// Silence for this many days makes an application stale. Default 14.
    /// </summary>
    /// <remarks>
    /// Replaces <c>SubmissionState.StaleAfter</c>, which is <c>TimeSpan.FromDays(14)</c>.
    ///
    /// <b>A fortnight, and the length is a judgement rather than a measurement.</b> Shorter would
    /// flag every application in its first week, which is the normal state of a live one, and a
    /// warning that fires on the ordinary case is one people learn to scroll past - the same
    /// reason the digest's board-hosted alarm sits at a near-total share rather than at a
    /// suspicion.
    ///
    /// <b>A day count rather than a <c>TimeSpan</c>, and the conversion belongs to the
    /// consumer.</b> A <c>TimeSpan</c> is a shape a form cannot render and a wire format spells
    /// several ways - "14.00:00:00", "P14D", 1209600000 - so it would be three contracts wearing
    /// one type. The fold keeps its <c>TimeSpan</c> and the consumer builds it with
    /// <c>TimeSpan.FromDays</c>; this is the number a person sets.
    ///
    /// <b>Staleness stays derived and is never stored.</b> Making the threshold configurable does
    /// not make the answer a column: storing it would mean a timer to write it, a race between
    /// that timer and a real event, and a row that is wrong between the two. Nor does it change
    /// the two rules around it - staleness is measured from the last activity of any kind,
    /// including an event that did not move the phase, and a closed application is never stale. An
    /// employer who has stopped replying has gone quiet; one who has said no has not.
    ///
    /// <b>Changing it re-reads history rather than rewriting it.</b> Because the answer is a fold,
    /// lowering this number makes older quiet applications stale immediately and raising it makes
    /// them live again, with nothing migrated and nothing to migrate. That is the property the
    /// event log was chosen for, and it is what makes this one safe to expose.
    /// </remarks>
    public int ChaseAfterDays { get; init; } = 14;

    /// <summary>
    /// The pipeline exactly as it runs with nothing configured.
    /// </summary>
    /// <remarks>
    /// <b>Every value on it is the constant the shipped code already runs</b>, so a candidate with
    /// no stored row behaves identically to one on a build from before this record existed. That
    /// is the property the whole feature is judged on: a deployment that configures nothing must
    /// not be able to tell the feature arrived, and the way to keep that true is for the defaults
    /// to be a transcription of the constants rather than a fresh set of opinions that happen to
    /// look similar.
    ///
    /// <b>A single cached instance rather than a new one per call.</b> The record is immutable, so
    /// sharing it is free, and a caller wanting one lever moved writes
    /// <c>PipelineSettings.Default with { DraftsPerNight = 4 }</c> - which is also how a partial
    /// override should be built from a form, because it cannot lose a field the way copying eight
    /// values across by hand can.
    ///
    /// <b>It is deliberately not called <c>Empty</c>.</b> This is a full configuration that nobody
    /// typed, not the absence of one, and anybody deciding whether to persist a row would read
    /// those two words differently.
    /// </remarks>
    public static PipelineSettings Default { get; } = new();
}
