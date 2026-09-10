using JobPlatform.Core.Settings;

namespace JobPlatform.Api.Features.Pipeline;

/// <summary>
/// The nine levers as they are read back, plus the one thing the record itself cannot say:
/// whether anybody ever chose them.
/// </summary>
/// <remarks>
/// <b>There is no request contract in this file, and the absence is the design rather than an
/// omission.</b> <c>PUT /pipeline-settings</c> binds <see cref="PipelineSettings"/> itself out of
/// the body. Three reasons, in the order they matter:
/// <list type="number">
///   <item><description>
///     <b>A second set of nine initialisers is a second copy of the defaults.</b> Property
///     initialisers are what make "an omitted field keeps today's behaviour" true - that is the
///     whole argument for the record being nine named <c>init</c> properties rather than a
///     positional one - so whichever type the body deserialises into is the type whose defaults a
///     partial document is resolved against. A DTO in between would move that resolution here,
///     leaving two copies of forty, forty-five, sixty-seven, three, ten, eighty, null, twenty-five
///     and fourteen with nothing that fails when they diverge. <c>model.md</c> and <c>CLAUDE.md</c>
///     both state the rule as "absence resolves to <see cref="PipelineSettings.Default"/> and never
///     to a <see cref="PipelineSettings"/> assembled field by field", and a DTO is exactly the
///     assembling by hand they refuse.
///   </description></item>
///   <item><description>
///     <b>There is nothing here for a DTO to withhold.</b> <c>ProfileRequest</c> exists because
///     <c>CandidateProfile</c> carries a <c>SubjectId</c> - an identity the platform assigns and a
///     client must not be able to choose - and <c>ScraperSearchRequest</c> exists because a slug is
///     the same kind of identity, and because a free-form parameter map would reach
///     <c>scrape_jobs(**params)</c>. <see cref="PipelineSettings"/> is nine numbers and no identity
///     at all: no field on it could name somebody else's pipeline, so the subject id comes from the
///     token and the body has nothing in it to tamper with.
///   </description></item>
///   <item><description>
///     <b>The wire names already agree.</b> The host runs the Web JSON defaults, so the nine
///     properties serialise camelCase - <c>assessmentsPerNight</c>, <c>draftPostedWithinDays</c> -
///     which is the contract <c>web/src/api/types.ts</c> was written against, character for
///     character. A DTO would have to reproduce those nine names in order to change nothing.
///   </description></item>
/// </list>
/// <b>What that costs, said out loud.</b> DataAnnotations attributes cannot be hung on a Core type
/// and would not be wanted on one, so the OpenAPI schema for the request body shows no ranges where
/// <c>ScraperSearchRequest</c>'s <c>[Range]</c> attributes show them. That is a smaller loss than
/// it reads: minimal APIs on this framework version do not evaluate those attributes, so they are
/// schema documentation rather than a check, and <c>ScraperSearchValidation</c> is what actually
/// refuses a bad search. Here <see cref="PipelineSettingsValidation"/> is what refuses, at the
/// endpoint, where a refusal can name every problem at once and reach the person who typed the
/// number - which is the only place a bound in this feature has ever been enforced.
///
/// <b>Flat rather than a settings object beside a timestamp, because a page hands this straight
/// back to a save.</b> The client contract is <c>PipelineSettingsResponse extends
/// PipelineSettingsRequest</c>, so what a form read is what a form sends; a nested shape would make
/// every save a re-assembly of nine values by hand, which is the operation this whole feature is
/// written to avoid.
///
/// <b>Nine <c>init</c> properties and not a positional record, for the reason
/// <see cref="PipelineSettings"/> gives.</b> Eight <c>int</c> and one <c>int?</c> in overlapping
/// small ranges means a positional constructor takes a transposed pair of arguments without a word
/// from the compiler, and a response that quietly swapped two of them would describe a pipeline
/// nobody is running.
///
/// <b>The reasoning for each number is on <see cref="PipelineSettings"/> and is deliberately not
/// restated here.</b> Those numbers were argued once, against the constants they replace, and
/// re-arguing them in a second file is how two spellings of one decision start to drift. The
/// summaries below say what a member is and point at the record; they do not say why it is forty.
///
/// <b>Two of this system's four thresholds appear here and the other two appear nowhere.</b>
/// <see cref="AssessmentThreshold"/> is the deterministic match score at which buying a judgement is
/// worth it; <see cref="DraftMinAssessmentScore"/> is the model's assessment score at which writing
/// a document is worth the expensive deployment. <c>MatchRanker.FusionFloor</c> and
/// <c>CvVariantSelector.SelectionFloor</c> are not settings, are on no route, in no DTO and on no
/// form, and must not become any of those. No two of the four carry the same word - here least of
/// all, because a response object is where a client's label comes from.
/// </remarks>
public sealed record PipelineSettingsResponse
{
    // ---- Judgement. Read by the nightly match sweep. ----

    /// <summary>
    /// Postings the model judges per night. See <see cref="PipelineSettings.AssessmentsPerNight"/>.
    /// </summary>
    public int AssessmentsPerNight { get; init; }

    /// <summary>
    /// The <b>deterministic match score</b> a pair must clear before a judgement is bought. See
    /// <see cref="PipelineSettings.AssessmentThreshold"/>.
    /// </summary>
    public int AssessmentThreshold { get; init; }

    /// <summary>
    /// The percentage of the shortlist reserved for recent postings. See
    /// <see cref="PipelineSettings.RecentSharePercent"/>.
    /// </summary>
    public int RecentSharePercent { get; init; }

    /// <summary>
    /// What that reservation counts as recent, in days. It is not
    /// <c>PostingAge.DailyWindowDays</c>, which stays the system-wide age definition every other
    /// filter answers to. See <see cref="PipelineSettings.RecentWindowDays"/>.
    /// </summary>
    public int RecentWindowDays { get; init; }

    // ---- Drafting. Read by the nightly application generation pass. ----

    /// <summary>
    /// Drafts one nightly pass writes. See <see cref="PipelineSettings.DraftsPerNight"/>.
    /// </summary>
    public int DraftsPerNight { get; init; }

    /// <summary>
    /// The <b>model's assessment score</b> a posting must carry before a document is written for
    /// it. See <see cref="PipelineSettings.DraftMinAssessmentScore"/>.
    /// </summary>
    public int DraftMinAssessmentScore { get; init; }

    /// <summary>
    /// Only write for postings posted within this many days, or null for every age.
    /// </summary>
    /// <remarks>
    /// <b>The only nullable member on the wire, and null is not zero.</b> Null is "no age bound"
    /// and is the shipped behaviour; zero through <c>PostingAge.Cutoff</c> would mean "posted since
    /// this instant" and select almost nothing, which is why
    /// <see cref="PipelineSettingsValidation.MinDraftPostedWithinDays"/> refuses it. A form's empty
    /// box has to arrive as null rather than as 0 and rather than omitted-as-zero - the same "did
    /// not choose and chose nothing are different bytes on the wire" rule the scraper configuration
    /// already runs under. It is on the response for that reason as much as any other: a client
    /// that reads back 0 where it sent nothing has found the fault before a night is spent on it.
    /// </remarks>
    public int? DraftPostedWithinDays { get; init; }

    // ---- Sending. Read by the submission write path and by the fold over the event log. ----

    /// <summary>
    /// Applications that may be recorded as sent in one UTC day. See
    /// <see cref="PipelineSettings.DailySendCap"/>.
    /// </summary>
    public int DailySendCap { get; init; }

    /// <summary>
    /// Days of silence before an application reads as stale. See
    /// <see cref="PipelineSettings.ChaseAfterDays"/>.
    /// </summary>
    public int ChaseAfterDays { get; init; }

    /// <summary>
    /// When these were last saved, or null where nothing has ever been stored.
    /// </summary>
    /// <remarks>
    /// <b>The whole of the "has anybody configured this" answer, and one field rather than two on
    /// purpose.</b> A boolean beside a timestamp is two things that have to agree, and a client
    /// would have to pick one to believe on the day they do not.
    ///
    /// <b>Null does not mean the read failed and it does not mean the nine values are missing.</b>
    /// An unconfigured candidate runs on <see cref="PipelineSettings.Default"/>, whose every value
    /// is the constant the shipped code already ran, so all nine numbers are answered and this
    /// timestamp says only whether anybody chose them. That is what lets a page say "these are the
    /// shipped defaults" instead of rendering an empty form, and it is why <c>GET</c> here has no
    /// 404 where the profile does: absence is a complete answer rather than a missing one.
    ///
    /// <b>It is also the one member that cannot be read off <see cref="PipelineSettings"/>.</b> The
    /// record is pure and carries no clock, deliberately - that is what makes its defaults
    /// assertable exactly - so the timestamp is a property of the stored row rather than of the
    /// configuration, and <c>PipelineEndpoints</c> reads it separately. Read the remarks there
    /// before adding a second caller.
    ///
    /// <b>A row of default values and no row at all read back identically apart from this
    /// field</b>, which is correct rather than a leak: resetting is
    /// <c>SaveAsync(subjectId, PipelineSettings.Default, ...)</c> and there is no delete, so "I
    /// chose the shipped behaviour" and "I never chose" differ by exactly this timestamp and by
    /// nothing the pipeline can observe.
    /// </remarks>
    public DateTimeOffset? UpdatedUtc { get; init; }
}
