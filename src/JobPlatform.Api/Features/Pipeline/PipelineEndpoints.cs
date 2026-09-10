using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Settings;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Api.Features.Pipeline;

/// <summary>
/// What the calling principal's pipeline is allowed to spend. Read and replace.
/// </summary>
/// <remarks>
/// <b>Never <see cref="AuthSetup.PublicReadPolicy"/>.</b> These two routes decide how many calls a
/// scheduled pass makes to a language model on somebody's behalf, and how many applications may be
/// recorded as sent in their name, so they require a principal unconditionally - the
/// <c>Api:AllowAnonymousReads</c> switch that opens the posting corpus during development must not
/// reach them, exactly as it must not reach the profile, the searches or <c>/me</c>. The corpus is
/// public text; a person's nightly budget is not. <c>AuthorizationTests</c> is where that is pinned,
/// because nothing else in the suite fails when it regresses.
///
/// <b>There is no route parameter naming whose settings these are, and there must never be one.</b>
/// The subject id comes from the token through <see cref="CallerIdentity.TryGetSubjectId"/> -
/// <c>oid</c>, and deliberately never a fallback to <c>ClaimTypes.NameIdentifier</c>, which
/// resolves to <c>sub</c> and is pairwise per application, so settings stored under it would be
/// invisible to the same person arriving through a second app registration and the symptom would
/// look like data loss rather than like a claim mix-up. <see cref="PipelineSettingsRepository"/>'s
/// person-facing methods take a subject id and have no overload that takes a profile id, so there
/// is nothing here a route parameter could be handed to even by mistake. Its two profile-id methods
/// exist for the unattended nightly passes and are unreachable from anything in this file.
///
/// <b>No output cache, deliberately, and the absence of a line is not self-documenting - hence this
/// paragraph.</b> These responses are per-principal and mutable. The output cache is keyed on the
/// URL, and this URL contains no user, so a cached <c>GET /pipeline-settings</c> is one person's
/// nightly budget served to the next caller. That is the same rule <c>/profile</c>, <c>/searches</c>
/// and <c>/submissions</c> state, and it is worse here than on any of them: the value served would
/// not merely be shown, it would be typed over and saved back, so one person's cap could be written
/// onto another person's row by somebody who never saw a thing wrong.
///
/// <b>These routes read and write Azure SQL</b>, which the architecture otherwise reserves for
/// posting browse and search. Bounded exactly like the profile's and the searches': read when a
/// settings page opens, written when somebody presses save. Never a polling path, and <b>nothing
/// here may join a client's bootstrap sequence</b> - that is the rule that keeps opening the
/// dashboard from waiting on a database that pauses when idle. The nightly passes do not come
/// through here at all; they read every candidate's settings in one query through
/// <c>PipelineSettingsRepository.GetForProfilesAsync</c>.
///
/// <b><c>GET</c> never answers 404 and <c>PUT</c> can, and the asymmetry is the feature rather than
/// an inconsistency.</b> A candidate who has stored nothing is not missing an answer - they run on
/// <see cref="PipelineSettings.Default"/>, whose every value is the constant the shipped code
/// already ran - so the read is always a complete configuration with a null timestamp. A save needs
/// somewhere to put a row, and the row is keyed to the profile, so a subject with no profile yet
/// gets a 404 that says so. Creating a profile as a side effect of visiting a settings page would
/// leave an empty one for the extraction sweep to pick up.
///
/// <b>Validation runs here on every save whatever the client checked, and a refused save names
/// every problem at once.</b> A bound rendered in a form is a hint; <see cref="PipelineSettingsValidation"/>
/// is the enforcement, and it is the only enforcement - there is no check constraint on the table
/// and no clamp in the repository, both deliberately. The 400 is an RFC 9457 document whose
/// <c>detail</c> carries every problem joined into one string, exactly as <c>SearchEndpoints</c>
/// answers a rejected search: a form with four bad fields should say so once rather than over four
/// saves. <b>Refusals rather than clamps</b>, for the reason the writing pass's silent
/// <c>Math.Clamp</c> was promoted into a rule - a setting that is silently corrected is a setting
/// that lies to the person who typed it, who asked for forty drafts, saw the page save, and gets
/// twenty-five every night with nothing anywhere saying why.
///
/// <b>Two of this system's four thresholds are reachable from this file and two are not.</b>
/// <see cref="PipelineSettings.AssessmentThreshold"/> (the deterministic match score at which
/// buying a judgement is worth it) and <see cref="PipelineSettings.DraftMinAssessmentScore"/> (the
/// model's assessment score at which writing a document is worth the expensive deployment) are
/// surfaced. <c>MatchRanker.FusionFloor</c> is fitted and <c>CvVariantSelector.SelectionFloor</c> is
/// reasoned from the partial-credit table; neither is a setting, neither is named anywhere in this
/// feature, and neither may ever acquire a route. Two of the four were once collapsed into a single
/// constant because they shared a value, and a settings surface is the easiest place in the system
/// to make that mistake a second time.
/// </remarks>
public sealed class PipelineEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // RequireRateLimiting on the group rather than per verb, and the read policy on the write
        // too, exactly as SearchEndpoints does. Both routes are opened and pressed by a person on
        // one page; neither is a bulk path, and a second policy here would be a second number to
        // keep in step with nothing asking for it.
        //
        // No .CacheOutput anywhere in this method. See the remarks on the class: the absence is
        // deliberate, and a shared cache keyed on a URL with no user in it is how one person is
        // served - and then saves back - another person's record.
        var group = routes.MapGroup("/pipeline-settings")
            .WithTags("Pipeline")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", GetAsync)
            .WithName("GetPipelineSettings")
            .WithSummary("The calling principal's nine pipeline levers. Defaults where nothing was ever saved.");

        group.MapPut("/", SaveAsync)
            .WithName("SavePipelineSettings")
            .WithSummary("Replaces the calling principal's nine pipeline levers, or refuses and says why.");
    }

    /// <summary>
    /// What this candidate's pipeline will run on tonight.
    /// </summary>
    /// <remarks>
    /// <b>Never null, never 404, and no branch here for "unconfigured".</b>
    /// <see cref="PipelineSettingsRepository.GetAsync"/> answers
    /// <see cref="PipelineSettings.Default"/> for an absent row and for a subject with no profile
    /// at all, so this method has nothing to decide - which is the point of that decision living in
    /// one place. A client forced to tell "no settings" from "a failure" by reading a status code is
    /// a client that eventually shows an error to somebody whose pipeline is working perfectly.
    /// </remarks>
    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user,
        [FromServices] PipelineSettingsRepository pipeline,
        [FromServices] JobsDbContext db,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var settings = await pipeline.GetAsync(subjectId, ct);

        return TypedResults.Ok(settings.ToResponse(await ChosenAtAsync(db, subjectId, ct)));
    }

    /// <summary>
    /// Stores the whole record, or refuses it with every problem listed.
    /// </summary>
    /// <remarks>
    /// <b>A replace and not a merge, like a profile save and a search save.</b> A partial document
    /// is still a partial <i>override</i>: the body deserialises into
    /// <see cref="PipelineSettings"/> itself, so a property the client omitted keeps its
    /// initialiser and therefore keeps the constant the shipped code already ran. That is resolved
    /// once, by the record, and never again by the repository - two places a partial document is
    /// resolved would be two answers to "what does an omitted field mean" and one of them would be
    /// wrong.
    ///
    /// <b>Validated before it is stored, and validated here whatever the client checked.</b> A
    /// number typed into a form reaches this method through a client that may be a browser, an
    /// old build of one, or <c>curl</c>. The bounds are cost bounds - six of the nine decide how
    /// many model calls go out between one scrape and the next - so the enforcement has to sit on
    /// the server side of the wire, and this is the only place in the feature that has it: the
    /// table carries no check constraint and the repository neither validates nor clamps, both by
    /// design.
    ///
    /// <b>Every problem, joined into one <c>detail</c>, exactly as <c>SearchEndpoints</c> answers a
    /// rejected search.</b> Two of the rules are cross-field, so a save can be refused with all nine
    /// numbers individually in range - the drafting floor may not sit below the assessment
    /// threshold, and a night may not write more letters than a day can send - which is precisely
    /// the case a client-side check is most likely to miss and a person most needs the sentence
    /// for.
    ///
    /// <b>404 rather than an empty success where the subject has no profile.</b> The row is keyed to
    /// the profile, and <see cref="PipelineSettingsRepository.SaveAsync"/> answers null rather than
    /// inventing one - a profile created as a side effect of visiting a settings page is a row the
    /// extraction sweep would then pick up. It carries a detail because the 404 is otherwise
    /// surprising: nothing in the URL names a resource that could be missing.
    /// </remarks>
    private static async Task<IResult> SaveAsync(
        ClaimsPrincipal user,
        PipelineSettings request,
        [FromServices] PipelineSettingsRepository pipeline,
        [FromServices] JobsDbContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var problems = PipelineSettingsValidation.Validate(request);

        if (problems.Count > 0)
        {
            return TypedResults.Problem(
                detail: string.Join(" ", problems),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var stored = await pipeline.SaveAsync(subjectId, request, time, ct);

        if (stored is null)
        {
            return TypedResults.Problem(
                detail: "These settings belong to a candidate profile and you do not have one yet, "
                        + "so there is no pipeline to configure. Save a profile first; until then "
                        + "nothing is scored, judged, drafted or sent on your behalf and the "
                        + "shipped defaults are what would run.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Read back through the same pair of calls the GET uses, rather than reusing the settings
        // that went in. What was stored is what is answered - so a mapping that has lost a field
        // shows up on the save the person is watching rather than on the next page load.
        return TypedResults.Ok(stored.ToResponse(await ChosenAtAsync(db, subjectId, ct)));
    }

    /// <summary>
    /// When this candidate last saved their settings, or null where they never have.
    /// </summary>
    /// <remarks>
    /// <b>A scalar read against the context rather than a method on the repository, and it is worth
    /// being honest about which of those it should be.</b> <see cref="PipelineSettings"/> is pure
    /// and carries no clock - that purity is what makes its defaults assertable exactly rather than
    /// through a database round trip - so a stored row's timestamp is not on the record and cannot
    /// be, and <see cref="PipelineSettingsRepository"/> currently returns the record alone. The
    /// clean shape is for that repository to answer a view carrying both, the way
    /// <c>CandidateProfileRepository</c> and <c>ScraperSearchRepository</c> answer views rather than
    /// bare domain records; until it does, this is the narrowest way to get the one column the wire
    /// contract requires.
    ///
    /// <b>Narrow deliberately: a projection to one nullable column, and no second copy of the
    /// nine-field mapping.</b> The temptation is to read the whole entity here and map it, which
    /// would be quicker by a query and would put a second transcription of the nine values in a
    /// second project - the thing <c>PipelineSettingsRepository.ToDomain</c> exists to confine to
    /// one file. A scalar costs one round trip and can drift from nothing.
    /// <c>ProfileEndpoints.DeleteAsync</c> reaches for <see cref="JobsDbContext"/> on the same
    /// terms.
    ///
    /// <b><c>AsNoTracking</c> because nothing here is mutated</b>, which the host's global tracking
    /// default makes a statement rather than a formality: tracking is on by default precisely so
    /// that a forgotten <c>AsTracking</c> cannot silently lose a write, and every read path opts out
    /// explicitly. A projection to a scalar is never tracked whatever this says; it says it anyway,
    /// so the read paths look alike.
    ///
    /// <b>Null is the answer for a row that does not exist and for a subject with no profile</b>,
    /// which are the same answer - nobody has chosen - and the same one
    /// <see cref="PipelineSettingsRepository.GetAsync"/> gives to the values beside it. The join is
    /// on <c>SubjectId</c> and never on a profile id from a request, so there is no id here for a
    /// caller to substitute.
    /// </remarks>
    private static async Task<DateTimeOffset?> ChosenAtAsync(
        JobsDbContext db, string subjectId, CancellationToken ct)
        => await db.PipelineSettings
            .AsNoTracking()
            .Where(row => row.Profile!.SubjectId == subjectId)
            .Select(row => (DateTimeOffset?)row.UpdatedUtc)
            .FirstOrDefaultAsync(ct);
}
