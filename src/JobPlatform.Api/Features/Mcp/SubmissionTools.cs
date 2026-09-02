using System.ComponentModel;
using System.Security.Claims;
using JobPlatform.Api.Configuration;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JobPlatform.Api.Features.Mcp;

/// <summary>
/// The agent surface over the submission pipeline.
/// </summary>
/// <remarks>
/// <b>Four reads and two writes, and the writes record rather than act.</b>
/// <c>submit_application</c> will never exist: applying is irreversible and outward-facing, so
/// it stays outside this system entirely and no bug here can reach an employer. A person, or an
/// agent driving a browser somewhere else, does the applying; these tools write down that it
/// happened. <c>McpEndpointTests</c> asserts the surface is exactly these six, an equality
/// rather than a superset, so a seventh turns the build red.
///
/// <b>Both writes converge instead of duplicating, and one of them is capped.</b>
/// <c>create_submission</c> is idempotent per posting by the schema, and <c>record_event</c> on
/// a key the caller chooses. The daily cap on <c>Submitted</c> events lives in
/// <c>SubmissionRepository</c> rather than here - the reason <c>AiCallRecord.Create</c> is the
/// only constructor: a guard at the call sites survives until somebody adds another call site.
///
/// <b>Events written here carry <c>Source = Client</c>, never <c>Candidate</c>.</b> What a
/// person asserted and what an agent inferred are different kinds of claim, and a log that
/// cannot tell them apart cannot be audited after one turns out to be wrong.
///
/// <b>No tool takes a profile id, and none ever should.</b> Every one resolves the caller from
/// the token on the request and hands a subject id to a repository that has no overload
/// accepting anything else. That rule matters more here than on a route, because a route's
/// arguments are named by a router and these are named by a model: an unused <c>profileId</c>
/// parameter is exactly the kind of thing a model would helpfully fill in.
///
/// <b>An unattended client authenticates app-only, and that does not weaken the rule.</b> Its
/// token names a service principal rather than a person, so <c>McpOptions.AppPrincipals</c> says
/// which candidate that principal acts for. The identity still arrives with the token; the
/// indirection is written by whoever deployed the server and cannot be named by a caller. What
/// changes is the audit: a disclosure then records the candidate and the principal separately,
/// because "whose data left" and "what took it" stop being the same answer.
///
/// <b>Two of the reads disclose the candidate's own data and both are logged.</b> There is no
/// <c>get_profile</c> - a tool result is transcript content wherever the client runs - so
/// <see cref="GetFormFieldAsync"/> answers one allowlisted question at a time. But
/// <see cref="GetSubmissionPackAsync"/> returns the tailored CV, which is the profile rewritten
/// in prose, so it is recorded on the same terms rather than treated as a public-text read.
///
/// <b>These read and write Azure SQL</b>, which the architecture reserves for posting browse,
/// search and detail because it is billed on wall-clock time online against a monthly grant. The bound is
/// <c>RateLimitSetup.McpPolicy</c>, deliberately an order of magnitude below the dashboard's:
/// a client asking what changed once a day is what this is sized for, and a client polling every
/// minute is the failure that rule exists to prevent.
/// </remarks>
[McpServerToolType]
public sealed class SubmissionTools(
    CandidateProfileRepository profiles,
    JobMatchRepository matches,
    SubmissionRepository submissions,
    ApplicationDocumentRepository documents,
    TimeProvider time,
    IOptions<McpOptions> mcp,
    IDisclosureLog? disclosures = null)
{
    /// <summary>Hard ceiling regardless of what a caller asks for. Mirrors the matches endpoint.</summary>
    private const int MaxLimit = 100;

    [McpServerTool(Name = "list_applyable")]
    [Description(
        "The postings this candidate should apply to next: judged a credible fit by the "
        + "assessment pass, and with no submission recorded yet. Each carries the URL to apply "
        + "at and where the application is made: 'Ats' means the employer's own system, 'Board' "
        + "means the job board hosts it, and 'Unknown' means neither was established - the URL "
        + "is then the board's posting page and the candidate finds out there. An 'Ats' posting "
        + "may still have only the board's URL, because some boards say the apply is offsite "
        + "without saying where to. 'applyUrlSource' says where the URL came from: 'Posting' is "
        + "the employer's link as published, 'MatchedOnAnotherBoard' is the same job found on a "
        + "different board and is an inference, 'BoardPosting' means no direct link is known. "
        + "Ordered best first. This is a work queue, not a search: it never returns a posting "
        + "already applied to.")]
    public async Task<object> ListApplyableAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Restrict to 'Ats', 'Board' or 'Unknown'. Omit for all.")] string? channel = null,
        [Description("How many to return, 1-100. Default 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (!TryParseChannel(channel, out var parsed))
        {
            return Refused(
                $"'{channel}' is not a channel. Expected 'Ats' (the employer's own system), "
                + "'Board' (the job board hosts the application) or 'Unknown' (neither was "
                + "established), or omit it for all three.");
        }

        var rows = await matches.ListApplyableAsync(
            profileId!.Value, parsed, Math.Clamp(limit, 1, MaxLimit), ct);

        return new
        {
            items = rows.Select(row => new
            {
                postingId = row.PostingId,
                title = row.Title,
                company = row.Company,
                location = row.Location,
                channel = row.Channel.ToString(),
                applyUrl = row.ApplyUrl,

                // Where the URL came from, because one of the three is an inference. A caller
                // that treats a matched link as the employer's own has no way to notice when
                // the match was wrong.
                applyUrlSource = row.ApplyUrlSource.ToString(),
                verdict = row.Verdict?.ToString(),

                // Both numbers, and neither labelled the answer. The arithmetic says how much of
                // the posting the profile covers; the model's says whether the rest matters. They
                // disagree often and the disagreement is the informative part.
                score = row.Score,
                assessmentScore = row.AssessmentScore,
                rationale = row.Rationale,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "get_submission_pack")]
    [Description(
        "Everything needed to fill in one application: the advert's own text, where to apply, "
        + "and the tailored CV and cover letter already generated for it, as markdown. Returns "
        + "an explanation rather than an error where no documents have been generated yet.")]
    public async Task<object> GetSubmissionPackAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The posting to assemble a pack for. Must already be matched to this candidate.")] long postingId,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        var pair = await matches.GetForWritingAsync(profileId!.Value, postingId, ct);

        if (pair is null)
        {
            return Refused(
                $"Posting {postingId} has not been matched against this candidate's profile, so "
                + "there is no pack for it. Use list_applyable to see what is ready to send.");
        }

        var (_, _, posting) = pair.Value;
        var draft = await documents.GetLatestForPostingAsync(profileId.Value, postingId, ct);

        // Logged whether or not documents existed. This returns the advert alongside the CV, and
        // the CV is the profile rewritten in prose - the very thing get_profile is refused for.
        await RecordAsync(context, "get_submission_pack", $"posting {postingId}", draft is not null, ct);

        return new
        {
            postingId,
            title = posting.Title,
            company = posting.Company,
            advertText = posting.Text,
            curriculumVitaeMarkdown = draft?.CurriculumVitaeMarkdown,
            coverLetterMarkdown = draft?.CoverLetterMarkdown,
            revision = draft?.Revision,
            note = draft is null
                ? "No documents have been generated for this posting yet. They are written on "
                    + "request from the dashboard; this surface does not generate them."
                : null,
        };
    }

    [McpServerTool(Name = "get_form_field")]
    [Description(
        "One named answer about the candidate, from a fixed server-side allowlist - for filling "
        + "a single field on an application form. There is deliberately no tool that returns the "
        + "whole profile. Call with no name to see what may be asked for. Every call is recorded.")]
    public async Task<object> GetFormFieldAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The field to read, e.g. 'email'. Omit to list the allowed names.")] string? name = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        // Listing the catalogue is not a disclosure - it is the same fixed list for every
        // candidate and carries nobody's data - so it is not logged and needs no profile read.
        if (string.IsNullOrWhiteSpace(name))
        {
            return new
            {
                fields = FormFieldCatalog.All
                    .Select(entry => new { name = entry.Name, description = entry.Description })
                    .ToList(),
            };
        }

        if (!FormFieldCatalog.TryGet(name, out var requested))
        {
            // Refused without touching the profile, and the refusal names the whole set - the
            // caller is a model that will otherwise guess again.
            return Refused(
                $"'{name}' is not a field this system will answer. Allowed: "
                + string.Join(", ", FormFieldCatalog.Names) + ".");
        }

        // The mapped subject, not the token's principal: an app-only caller reads the
        // candidate it acts for, and ResolveAsync has already established there is one.
        var subjectId = Caller(context).SubjectId!;
        var view = await profiles.GetAsync(subjectId, ct);
        var value = view is null ? null : requested.Read(view.Profile);

        await RecordAsync(context, "get_form_field", requested.Name, value is not null, ct);

        return new
        {
            name = requested.Name,
            value,
            note = value is null
                ? "The profile does not carry this. Ask the candidate rather than inferring it."
                : null,
        };
    }

    [McpServerTool(Name = "create_submission")]
    [Description(
        "Records that an application was sent for one posting. Does NOT apply to anything - "
        + "nothing in this system can reach an employer; a person or a browser does the applying "
        + "and this records that it happened. Idempotent per posting: calling it twice returns "
        + "the submission that already exists rather than making a second, and never overwrites "
        + "where the first said the application went. The posting must already be matched to "
        + "this candidate.")]
    public async Task<object> CreateSubmissionAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The posting applied to. Must appear in list_applyable, or already be matched.")] long postingId,
        [Description("Where it was sent: 'Ats', 'Board' or 'Unknown'. Omit to take it from the posting.")] string? channel = null,
        [Description("The URL actually applied at. Omit to record the posting's own apply link.")] string? applyUrl = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (!TryParseChannel(channel, out var requested))
        {
            return Refused(
                $"'{channel}' is not a channel. Expected 'Ats' (the employer's own system), "
                + "'Board' (the job board hosts the application) or 'Unknown'.");
        }

        var target = await matches.ResolveApplyTargetAsync(profileId!.Value, postingId, ct);

        // The same rule the application writer follows. It is what stops an arbitrary posting id
        // - and these are named by a model - becoming a row in somebody's pipeline.
        if (target is null)
        {
            return Refused(
                $"Posting {postingId} has not been matched against this candidate's profile, so "
                + "there is nothing to record a submission against. Use list_applyable.");
        }

        var (row, created) = await submissions.CreateAsync(
            profileId.Value,
            postingId,
            requested ?? target.Channel,
            applyUrl ?? target.ApplyUrl,
            time.GetUtcNow(),
            ct);

        return new
        {
            submissionId = row.Id,
            postingId = row.PostingId,
            title = row.Title,
            channel = row.Channel.ToString(),
            applyUrl = row.ApplyUrl,

            // False on a retry. Worth returning rather than hiding: a client that cannot tell
            // "I made this" from "this already existed" will re-record events against it.
            created,
            note = created
                ? "Recorded. Add a 'Submitted' event with record_event once the application is "
                    + "actually sent - creating the submission does not assert that it was."
                : "A submission for this posting already existed and was returned unchanged.",
        };
    }

    [McpServerTool(Name = "record_event")]
    [Description(
        "Appends one event to a submission's log - that it was sent, acknowledged, rejected, an "
        + "interview booked, and so on. The log is append-only: nothing edits or deletes an "
        + "event, and withdrawing an application is a 'Withdrawn' event. Requires an "
        + "idempotencyKey the caller chooses, so a retry records the same thing once. There is a "
        + "daily cap on 'Submitted' events.")]
    public async Task<object> RecordEventAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The submission to append to, from create_submission or list_submissions.")] long submissionId,
        [Description("One of: Submitted, Acknowledged, ScreeningScheduled, InterviewScheduled, OfferReceived, Rejected, Withdrawn.")] string type,
        [Description("Your own key for this event, unique per submission. The same key twice records once.")] string idempotencyKey,
        [Description("When it happened, UTC. Omit for now - which is right only when it just did.")] DateTimeOffset? atUtc = null,
        [Description("The round or label inside the phase, e.g. 'Tech round 2'. Free text.")] string? stage = null,
        [Description("A sentence of context for the candidate. Never paste a message body.")] string? note = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Refused(
                "idempotencyKey is required. It is what makes a retry record the same event once, "
                + "and only the caller knows whether two requests are one event or two.");
        }

        if (!Enum.TryParse<SubmissionEventType>(type, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return Refused(
                $"'{type}' is not an event type. Expected one of: "
                + string.Join(", ", Enum.GetNames<SubmissionEventType>())
                + ". A specific interview round belongs in 'stage', not here.");
        }

        var result = await submissions.AddEventAsync(
            profileId!.Value,
            submissionId,
            // Client, not Candidate. What a person asserted and what an agent inferred are
            // different kinds of claim, and the log is only auditable if it says which.
            new SubmissionEvent(
                atUtc ?? time.GetUtcNow(), parsed, stage, SubmissionEventSource.Client, note),
            idempotencyKey,
            ct);

        return result switch
        {
            SubmissionEventResult.Recorded =>
                new { recorded = true, result = "Recorded", note = (string?)null },
            SubmissionEventResult.AlreadyRecorded => new
            {
                recorded = false,
                result = "AlreadyRecorded",
                note = (string?)("That key is already on this submission, so the earlier call "
                    + "landed. Nothing was duplicated and nothing needs retrying."),
            },
            SubmissionEventResult.NotFound => new
            {
                recorded = false,
                result = "NotFound",
                note = (string?)($"No submission {submissionId} for this candidate. Use "
                    + "list_submissions, or create_submission first."),
            },
            _ => new
            {
                recorded = false,
                result = "DailyLimitReached",
                note = (string?)($"This candidate has already recorded "
                    + $"{SubmissionLimits.MaxSubmittedPerDay} applications as sent for that day, "
                    + "which is the cap. Stop rather than retrying: the limit exists so a client "
                    + "looping cannot fill somebody's pipeline with applications never made."),
            },
        };
    }

    [McpServerTool(Name = "list_submissions")]
    [Description(
        "The candidate's applications and where each stands, most recently active first. The "
        + "status is folded from an append-only event log, so 'stale' means genuinely nothing "
        + "has happened for a fortnight rather than that a flag was left set. Closed "
        + "applications are never stale.")]
    public async Task<object> ListSubmissionsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Restrict to one phase, e.g. 'InterviewScheduled'. Omit for all.")] string? phase = null,
        [Description("Only those active since this UTC timestamp. Omit for all.")] DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (!string.IsNullOrWhiteSpace(phase)
            && (!Enum.TryParse<SubmissionEventType>(phase, ignoreCase: true, out _)))
        {
            return Refused(
                $"'{phase}' is not a phase. Expected one of: "
                + string.Join(", ", Enum.GetNames<SubmissionEventType>()) + ".");
        }

        var rows = await submissions.ListAsync(profileId!.Value, time.GetUtcNow(), ct);

        var filtered = rows
            .Where(row => since is null || row.Status.LastActivityUtc >= since)
            .Where(row => string.IsNullOrWhiteSpace(phase)
                || string.Equals(row.Status.Phase?.ToString(), phase, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new
        {
            items = filtered.Select(row => new
            {
                submissionId = row.Id,
                postingId = row.PostingId,
                title = row.Title,
                company = row.Company,
                channel = row.Channel.ToString(),

                // Null means nothing has happened yet, not that something went wrong. A default
                // name here would collapse "not sent" into "sent and we cannot say".
                phase = row.Status.Phase?.ToString(),
                stage = row.Status.Stage,
                lastActivityUtc = row.Status.LastActivityUtc,
                isStale = row.Status.IsStale,
                isClosed = row.Status.IsClosed,
                eventCount = row.Status.EventCount,
            }).ToList(),
        };
    }

    /// <summary>
    /// The caller's profile id, or the answer to give instead.
    /// </summary>
    /// <remarks>
    /// One place, called first by every tool. The principal comes from the transport rather than
    /// from an HTTP context accessor: the SDK populates it per message, which survives whatever
    /// async boundaries the transport introduces where an <c>AsyncLocal</c> may not.
    ///
    /// A missing profile is an explanation rather than an exception. A model that receives a
    /// stack trace retries; one that is told to go and fill in the form stops.
    /// </remarks>
    private async Task<(long? ProfileId, object? Failure)> ResolveAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var (actorId, subjectId) = Caller(context);

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            return (null, Refused(
                "This request carries no identified caller. The token must contain an 'oid' "
                + "claim; there is no way to answer without knowing whose data to read."));
        }

        // An app-only token that nothing mapped is a deployment that is not finished, not a
        // candidate who has not filled the form in. The two produce the same empty answer and
        // want opposite fixes, so they are told apart here rather than left to look alike.
        if (actorId == subjectId && IsApplicationToken(context))
        {
            return (null, Refused(
                "This token identifies an application rather than a person, and no candidate is "
                + "mapped to it. Whoever deployed this server maps an application principal to "
                + "the candidate it acts for; until that is done there is no pipeline to read."));
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        return profileId is null
            ? (null, Refused(
                "This candidate has no profile yet, so there is nothing matched and nothing to "
                + "apply to. The profile is filled in on the dashboard, not through this surface."))
            : (profileId, null);
    }

    /// <summary>
    /// The authenticated caller, from the message the transport delivered.
    /// </summary>
    /// <remarks>
    /// <c>oid</c> through <c>CallerIdentity</c>, never <c>ClaimTypes.NameIdentifier</c>, which
    /// resolves to <c>sub</c> - pairwise per application. A profile stored under one app
    /// registration would be invisible through another, and the failure would look like data loss
    /// rather than like a claim mix-up.
    /// </remarks>
    private (string? ActorId, string? SubjectId) Caller(RequestContext<CallToolRequestParams> context)
    {
        var actorId = context?.JsonRpcRequest?.Context?.User is { } user ? user.SubjectId() : null;

        if (string.IsNullOrWhiteSpace(actorId))
        {
            return (null, null);
        }

        return (actorId, AppPrincipalMap.Resolve(actorId, mcp.Value.AppPrincipals));
    }

    /// <summary>
    /// Whether the token was issued to software rather than to a person.
    /// </summary>
    /// <remarks>
    /// A role claim and no scope claim, which is what the client-credentials flow produces.
    /// <c>idtyp</c> would say so directly and is not read: it is an optional claim absent unless
    /// the registration asks for it, so a check resting on it would silently answer "person" for
    /// every app-only token in a tenant that had not configured it.
    /// </remarks>
    private static bool IsApplicationToken(RequestContext<CallToolRequestParams> context)
    {
        if (context?.JsonRpcRequest?.Context?.User is not { } user)
        {
            return false;
        }

        var hasScope = user.FindFirstValue("scp") is not null
            || user.FindFirstValue("http://schemas.microsoft.com/identity/claims/scope") is not null;

        var hasRole = user.FindFirstValue("roles") is not null
            || user.FindFirstValue(ClaimTypes.Role) is not null;

        return !hasScope && hasRole;
    }

    private async Task RecordAsync(
        RequestContext<CallToolRequestParams> context,
        string tool,
        string detail,
        bool answered,
        CancellationToken ct)
    {
        if (disclosures is null)
        {
            return;
        }

        var (actorId, subjectId) = Caller(context);

        if (subjectId is not { Length: > 0 } || actorId is not { Length: > 0 })
        {
            return;
        }

        // The record carries what was asked for and never what came back. An audit log holding
        // the data it audits has moved the problem rather than solved it. Both principals are
        // written: whose data left, and what took it.
        await disclosures.RecordAsync(
            DisclosureRecord.Create(time.GetUtcNow(), subjectId, actorId, tool, detail, answered), ct);
    }

    /// <summary>
    /// A refusal a model can act on.
    /// </summary>
    /// <remarks>
    /// A structured answer rather than a thrown exception. Every case here is an ordinary state
    /// of the system - no profile, a name outside the allowlist, a posting nobody matched - and
    /// a protocol-level error invites a retry where a sentence invites a different action.
    /// </remarks>
    private static object Refused(string reason) => new { refused = true, reason };

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
}
