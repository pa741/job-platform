using JobPlatform.Core.Settings;

namespace JobPlatform.Api.Features.Pipeline;

/// <summary>
/// Between the pipeline record and the wire, outwards only.
/// </summary>
/// <remarks>
/// <b>There is no inward direction here, and that is the point of the file being this short.</b>
/// <c>SearchMapping</c> has two directions because a request names job boards as strings and an
/// unrecognised one has to be refused rather than dropped. Nothing on these nine needs translating:
/// the body deserialises into <see cref="PipelineSettings"/> itself, so "what does an omitted field
/// mean" is answered once, by the record's own initialisers, rather than a second time here. See
/// the remarks on <see cref="PipelineSettingsResponse"/> for why there is no request DTO to map
/// from.
///
/// <b>The nine are written out by hand, and this is the second place in the system where that
/// happens.</b> The first is <c>PipelineSettingsRepository.ToDomain</c> and its <c>Apply</c>, which
/// says the same thing for the same reason: a record and a row are two types and there is no
/// <c>with</c> that spans them, and a record and a response object are two types for the same
/// reason. Everywhere else a variant of these settings is built with
/// <c>PipelineSettings.Default with { ... }</c> precisely so nine values are never copied across by
/// hand; confining the copying to one method per boundary is the compromise, not an exception to
/// the rule.
///
/// <b>Nothing in the type system catches a forgotten line, so this is a test obligation rather than
/// a compiler one.</b> A member missed here reads back as <c>0</c> - or as <c>null</c> for the age
/// bound - where the candidate stored something else, and the failure is quiet in the worst way:
/// the save succeeded, the pipeline runs on the number that was stored, and the page shows a
/// different one. Nobody sees an error; they see a settings page that disagrees with their bill. A
/// round trip through this method over a record whose nine values are all distinct and none of them
/// the default is what holds it together.
/// </remarks>
public static class PipelineMapping
{
    /// <summary>
    /// The settings a candidate's pipeline will run on, plus when they chose them.
    /// </summary>
    /// <remarks>
    /// <b><paramref name="updatedUtc"/> is a separate argument because
    /// <see cref="PipelineSettings"/> has no clock and must not grow one.</b> Purity is what makes
    /// the defaults assertable exactly rather than through a database round trip, so the timestamp
    /// belongs to the stored row and is fetched by the endpoint. Null here is "nobody has ever
    /// saved", which is a complete answer and not a missing one - the nine values are still the
    /// ones the pipeline will use tonight.
    ///
    /// <b>It does not fall back to "now" when it is null, and it must never learn to.</b> A
    /// timestamp invented at read time would make an unconfigured candidate indistinguishable from
    /// one who saved a moment ago, which is the single distinction this field exists to carry.
    /// </remarks>
    public static PipelineSettingsResponse ToResponse(
        this PipelineSettings settings, DateTimeOffset? updatedUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new PipelineSettingsResponse
        {
            AssessmentsPerNight = settings.AssessmentsPerNight,
            AssessmentThreshold = settings.AssessmentThreshold,
            RecentSharePercent = settings.RecentSharePercent,
            RecentWindowDays = settings.RecentWindowDays,
            DraftsPerNight = settings.DraftsPerNight,
            DraftMinAssessmentScore = settings.DraftMinAssessmentScore,
            DraftPostedWithinDays = settings.DraftPostedWithinDays,
            DailySendCap = settings.DailySendCap,
            ChaseAfterDays = settings.ChaseAfterDays,
            UpdatedUtc = updatedUtc,
        };
    }
}
