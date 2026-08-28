using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Ai;
using JobPlatform.Core.Ai;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.AiCalls;

/// <summary>
/// What the model was asked to do, and what came back.
/// </summary>
/// <remarks>
/// <b>The failures are the subject.</b> Every AI path in this system degrades silently by
/// design - a provider failure must not take down endpoints with nothing to do with AI - and for
/// a long time that was implemented as recording nothing at all. A sweep discarded five batches
/// of ten while reporting success; a backfill spent its calls on HTTP 429s and extracted almost
/// nothing. Both showed up as a count nobody was comparing to anything.
///
/// So <c>failuresOnly</c> defaults to true. A list of calls that worked is a list nobody reads.
///
/// Cosmos, like every other dashboard read. SQL is billed on wall-clock time online against a
/// monthly grant and is reserved for posting browse, search and detail - and this is precisely
/// the kind of endpoint somebody leaves open on a second monitor.
/// </remarks>
public sealed class AiCallEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/ai-calls")
            .WithTags("AI calls")
            .RequireAuthorization(AuthSetup.PublicReadPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListAiCalls")
            .WithSummary("Recent model calls, newest first. Failures only unless asked otherwise.");

        group.MapGet("/summary", SummaryAsync)
            .WithName("SummariseAiCalls")
            .WithSummary("What the last few days cost and what was lost, per pass.");

        // Its own route, behind the authenticated policy rather than the public read one.
        //
        // Api:AllowAnonymousReads relaxes PublicReadPolicy to anonymous so a frontend can be
        // built against real data, and a stored prompt carries the candidate's employment
        // history, contact details and salary expectations. Putting the prompt on the list
        // response would make one config flag the difference between a dashboard and a
        // published CV. AuthenticatedPolicy ignores that flag entirely, which is exactly why
        // /me uses it.
        group.MapGet("/{id}/replay", ReplayAsync)
            .WithName("ReplayAiCall")
            .WithSummary("The prompt of a failed call, and how to send it again.")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy);
    }

    /// <summary>
    /// Everything needed to reproduce one failed call outside the platform.
    /// </summary>
    /// <remarks>
    /// A failure that cannot be replayed is a failure somebody theorises about. The parsing
    /// faults this ledger exists to catch are exactly the kind that need the actual bytes: the
    /// difference between an index quoted as a string and one out of range is invisible from a
    /// count, and was guessed at for a day before the log said which.
    ///
    /// Returns 404 rather than 403 where no prompt was kept, because "not recorded" and "does
    /// not exist" are the same answer to a caller and distinguishing them only advertises the
    /// setting.
    /// </remarks>
    private static async Task<IResult> ReplayAsync(
        [FromServices] IAiCallSource repository,
        [FromServices] IOptions<AzureOpenAiOptions> ai,
        string id,
        CancellationToken ct)
    {
        var record = await repository.GetAsync(id, ct);

        if (record?.Prompt is null)
        {
            return TypedResults.NotFound();
        }

        var endpoint = ai.Value.Endpoint?.TrimEnd('/') ?? "<endpoint>";
        var deployment = record.Deployment ?? "<deployment>";

        return TypedResults.Ok(new AiCallReplayResponse
        {
            Operation = record.Operation,
            Deployment = record.Deployment,
            OccurredAtUtc = record.OccurredAtUtc,
            Reason = record.Reason,
            Prompt = record.Prompt,
            // Written out rather than described, because the point is to paste it. The token is
            // left as a substitution: the deployment authenticates with a managed identity and
            // there is no key to embed, which is the whole architecture rather than an oversight.
            Curl = string.Join(" \\\n  ", [
                $"curl -X POST '{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21'",
                "-H \"Content-Type: application/json\"",
                "-H \"Authorization: Bearer $(az account get-access-token "
                    + "--resource https://cognitiveservices.azure.com --query accessToken -o tsv)\"",
                "-d @prompt.json",
            ]),
        });
    }

    private static async Task<IResult> ListAsync(
        [FromServices] IAiCallSource repository,
        CancellationToken ct,
        int days = 7,
        bool failuresOnly = true,
        int limit = 100)
    {
        var records = await repository.ListAsync(days, failuresOnly, limit, ct);

        return TypedResults.Ok(new { items = records.Select(ToResponse).ToList() });
    }

    private static async Task<IResult> SummaryAsync(
        [FromServices] IAiCallSource repository,
        CancellationToken ct,
        int days = 7)
    {
        var totals = await repository.SummariseAsync(days, ct);

        return TypedResults.Ok(new
        {
            days,
            items = totals.Select(t => new AiCallTotalsResponse
            {
                Operation = t.Operation,
                Calls = t.Calls,
                FailedCalls = t.FailedCalls,
                Requested = t.Requested,
                Returned = t.Returned,
                Discarded = t.Discarded,
            }).ToList(),
        });
    }

    private static AiCallResponse ToResponse(AiCallRecord record)
        => new()
        {
            OccurredAtUtc = record.OccurredAtUtc,
            Operation = record.Operation,
            Deployment = record.Deployment,
            Outcome = record.Outcome.ToString(),
            Requested = record.Requested,
            Returned = record.Returned,
            Discarded = record.Discarded,
            DurationMs = record.DurationMs,
            Reason = record.Reason,
            AffectedIds = record.AffectedIds,
        };
}

/// <summary>One model call, as the dashboard reads it.</summary>
/// <remarks>
/// A contract rather than the stored record, for the usual reason and one specific one: the
/// stored type carries its own partition key and discriminator, which are storage details and
/// would become part of the API surface the moment a client saw them.
/// </remarks>
public sealed record AiCallResponse
{
    public DateTimeOffset OccurredAtUtc { get; init; }

    public string Operation { get; init; } = string.Empty;

    public string? Deployment { get; init; }

    /// <summary><c>Succeeded</c>, <c>PartiallyDiscarded</c> or <c>Failed</c>.</summary>
    public string Outcome { get; init; } = string.Empty;

    public int Requested { get; init; }

    public int Returned { get; init; }

    /// <summary>Paid for and thrown away. The number that used to be invisible.</summary>
    public int Discarded { get; init; }

    public long DurationMs { get; init; }

    /// <summary>Why, in a few words. Never a prompt or a response body.</summary>
    public string? Reason { get; init; }

    /// <summary>The postings the call was about, so a failure names what it lost.</summary>
    public IReadOnlyList<long> AffectedIds { get; init; } = [];
}

/// <summary>One failed call, with what it takes to send it again.</summary>
public sealed record AiCallReplayResponse
{
    public string Operation { get; init; } = string.Empty;

    public string? Deployment { get; init; }

    public DateTimeOffset OccurredAtUtc { get; init; }

    public string? Reason { get; init; }

    /// <summary>
    /// The exact text sent to the model.
    /// </summary>
    /// <remarks>
    /// Reachable only through this route, which the anonymous-reads flag cannot open. It carries
    /// whatever the prompt carried, which for an assessment or a profile extraction is somebody's
    /// employment history.
    /// </remarks>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>
    /// A ready-made request, with the prompt to be written to <c>prompt.json</c> alongside.
    /// </summary>
    public string Curl { get; init; } = string.Empty;
}

public sealed record AiCallTotalsResponse
{
    public string Operation { get; init; } = string.Empty;

    public int Calls { get; init; }

    public int FailedCalls { get; init; }

    public int Requested { get; init; }

    public int Returned { get; init; }

    public int Discarded { get; init; }
}
