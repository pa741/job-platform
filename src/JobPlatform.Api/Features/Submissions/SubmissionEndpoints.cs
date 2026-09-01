using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Submissions;

/// <summary>
/// What the candidate actually sent, and what happened to it.
/// </summary>
/// <remarks>
/// <b>The server records that something was submitted; it never submits.</b> Applying is
/// irreversible and outward-facing, so nothing in this repository can reach an employer. There
/// is no endpoint here that sends anything and there must never be one.
///
/// <b>Never <see cref="AuthSetup.PublicReadPolicy"/>.</b> <c>Api:AllowAnonymousReads</c> exists
/// to open the posting corpus, which is public text. Which jobs a particular person applied to,
/// and how each is going, is the opposite of that. <c>AuthorizationTests</c> pins every verb,
/// because nothing else in the suite fails when this regresses.
///
/// <b>There is no status column and no endpoint that sets one.</b> The status is a fold over the
/// events - see <c>SubmissionState</c> - so the only write here is an append. Withdrawing is a
/// <c>Withdrawn</c> event, and there is no delete: an append-only log with no eraser is the only
/// version worth auditing.
///
/// <b>These routes read and write Azure SQL</b>, which the architecture otherwise reserves for
/// posting browse, search and detail. Bounded exactly like the profile's and the searches':
/// fetched when a page opens, written when something happened. Never a polling path, and nothing
/// here may join a client's bootstrap sequence.
///
/// No output cache, deliberately. Per-principal and mutable, and a shared cache keyed on a URL
/// with no user in it is how one person is served another's pipeline.
/// </remarks>
public sealed class SubmissionEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/submissions")
            .WithTags("Submissions")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListSubmissions")
            .WithSummary("The calling principal's applications, most recently active first.");

        group.MapPost("/", CreateAsync)
            .WithName("CreateSubmission")
            .WithSummary("Records that an application was sent for one matched posting.");

        group.MapGet("/{id:long}/events", ListEventsAsync)
            .WithName("ListSubmissionEvents")
            .WithSummary("One application's whole log, oldest first.");

        group.MapPost("/{id:long}/events", RecordEventAsync)
            .WithName("RecordSubmissionEvent")
            .WithSummary("Appends one event. Idempotent on the key the caller supplies.");
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] SubmissionRepository submissions,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        // An empty list rather than a 404, unlike the matches endpoint. Nothing has to send
        // anybody anywhere: a person with no profile has no submissions, and that is a complete
        // and unsurprising answer.
        if (profileId is null)
        {
            return TypedResults.Ok(new { items = Array.Empty<SubmissionResponse>() });
        }

        var rows = await submissions.ListAsync(profileId.Value, time.GetUtcNow(), ct);

        return TypedResults.Ok(new { items = rows.Select(ToResponse).ToList() });
    }

    /// <summary>
    /// Records a submission, or returns the one already recorded.
    /// </summary>
    /// <remarks>
    /// 201 on the first call and 200 on a retry, which is the distinction a caller needs and the
    /// only one this endpoint makes. It never fails for being sent twice: the unique index on
    /// <c>(ProfileId, PostingId)</c> makes convergence the schema's guarantee rather than this
    /// method's, and a second call must not rewrite where the first said the application went.
    /// </remarks>
    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal user,
        CreateSubmissionRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        [FromServices] SubmissionRepository submissions,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        if (!TryParseChannel(request.Channel, out var requested))
        {
            return Invalid<SubmissionChannel>("channel", request.Channel);
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.Problem(
                detail: "No profile exists for this principal, so there is nothing to record a submission against.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var target = await matches.ResolveApplyTargetAsync(profileId.Value, request.PostingId, ct);

        // Requiring a match is the same rule the application writer follows, and it is what stops
        // an arbitrary posting id - supplied by a route or, later, by a model - becoming a row in
        // somebody's pipeline.
        if (target is null)
        {
            return TypedResults.Problem(
                detail: "This posting has not been matched against your profile, so there is nothing to submit against.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var (row, created) = await submissions.CreateAsync(
            profileId.Value,
            request.PostingId,
            requested ?? target.Channel,
            request.ApplyUrl ?? target.ApplyUrl,
            time.GetUtcNow(),
            ct);

        var response = ToResponse(row);

        return created
            ? TypedResults.Created($"/api/v1/submissions/{row.Id}", response)
            : TypedResults.Ok(response);
    }

    private static async Task<IResult> ListEventsAsync(
        ClaimsPrincipal user,
        long id,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] SubmissionRepository submissions,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        // The repository is scoped to the profile id, so an id from the route can never reach a
        // stranger's log - and "not yours" is indistinguishable from "does not exist".
        var events = await submissions.ListEventsAsync(profileId.Value, id, ct);

        return TypedResults.Ok(new
        {
            items = events.Select(e => new SubmissionEventResponse
            {
                AtUtc = e.AtUtc,
                Type = e.Type.ToString(),
                Stage = e.Stage,
                Source = e.Source.ToString(),
                Note = e.Note,
            }).ToList(),
        });
    }

    /// <summary>
    /// Appends one event.
    /// </summary>
    /// <remarks>
    /// 201 when it was recorded and 200 when that key was already present. Never a conflict: a
    /// client retrying a write it is not sure landed has to be able to send it again and get the
    /// same outcome, which is the contract the whole ingestion side of this system runs on.
    /// </remarks>
    private static async Task<IResult> RecordEventAsync(
        ClaimsPrincipal user,
        long id,
        RecordSubmissionEventRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] SubmissionRepository submissions,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return TypedResults.Problem(
                detail: "idempotencyKey is required. It is what makes a retried write converge "
                    + "rather than recording the same event twice, and only the caller knows "
                    + "whether two requests are one event or two.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Enum.TryParse<SubmissionEventType>(request.Type, ignoreCase: true, out var type)
            || !Enum.IsDefined(type))
        {
            return Invalid<SubmissionEventType>("type", request.Type);
        }

        if (!TryParseSource(request.Source, out var source))
        {
            return Invalid<SubmissionEventSource>("source", request.Source);
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var recorded = await submissions.AddEventAsync(
            profileId.Value,
            id,
            new SubmissionEvent(
                request.AtUtc ?? time.GetUtcNow(),
                type,
                request.Stage,
                source ?? SubmissionEventSource.Candidate,
                request.Note),
            request.IdempotencyKey,
            ct);

        return recorded switch
        {
            SubmissionEventResult.NotFound => TypedResults.NotFound(),
            SubmissionEventResult.Recorded =>
                TypedResults.Created($"/api/v1/submissions/{id}/events", new { recorded = true }),
            SubmissionEventResult.AlreadyRecorded => TypedResults.Ok(new { recorded = false }),

            // 429, not 400. The request is well formed and would be accepted tomorrow; the
            // caller has spent a budget rather than made a mistake, and the status should say so.
            SubmissionEventResult.DailyLimitReached => TypedResults.Problem(
                detail: $"This profile has already recorded {SubmissionLimits.MaxSubmittedPerDay} "
                    + "applications as sent for that day, which is the cap. Nothing was recorded. "
                    + "The limit exists so that a client looping cannot fill somebody's pipeline "
                    + "with applications they never made.",
                statusCode: StatusCodes.Status429TooManyRequests),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static bool TryParseChannel(string? value, out SubmissionChannel? channel)
    {
        channel = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<SubmissionChannel>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return false;
        }

        channel = parsed;

        return true;
    }

    private static bool TryParseSource(string? value, out SubmissionEventSource? source)
    {
        source = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<SubmissionEventSource>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return false;
        }

        source = parsed;

        return true;
    }

    /// <summary>
    /// A 400 that names what was accepted.
    /// </summary>
    /// <remarks>
    /// The allowed set is listed rather than described, because the caller may be a model reading
    /// the error and retrying, and "type must be a valid SubmissionEventType" tells it nothing it
    /// can act on.
    /// </remarks>
    private static IResult Invalid<TEnum>(string field, string? value)
        where TEnum : struct, Enum
        => TypedResults.Problem(
            detail: $"'{value}' is not a valid {field}. Expected one of: "
                + string.Join(", ", Enum.GetNames<TEnum>()) + ".",
            statusCode: StatusCodes.Status400BadRequest);

    private static SubmissionResponse ToResponse(SubmissionRow row)
        => new()
        {
            Id = row.Id,
            PostingId = row.PostingId,
            PostingTitle = row.Title,
            Company = row.Company,
            Channel = row.Channel.ToString(),
            ApplyUrl = row.ApplyUrl,
            CreatedAtUtc = row.CreatedAtUtc,
            Phase = row.Status.Phase?.ToString(),
            Stage = row.Status.Stage,
            LastActivityUtc = row.Status.LastActivityUtc,
            IsStale = row.Status.IsStale,
            IsClosed = row.Status.IsClosed,
            EventCount = row.Status.EventCount,
        };
}
