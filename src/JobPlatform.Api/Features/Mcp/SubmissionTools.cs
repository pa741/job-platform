using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using JobPlatform.Api.Configuration;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Dedup;
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
/// <b>Fourteen tools, and not one of them applies to anything.</b> <c>submit_application</c> will
/// never exist: applying is irreversible and outward-facing, so it stays outside this system
/// entirely and no bug here can reach an employer. A person, or an agent driving a browser
/// somewhere else, does the applying; these tools decide what to apply to, hand over what is
/// needed to fill the form in, and write down what happened. <c>McpEndpointTests</c> asserts the
/// surface is exactly these fourteen, an equality rather than a superset, so a fifteenth turns
/// the build red.
///
/// <b>Seven reads, six writes, and one that decides without writing.</b> The reads are the queue,
/// the pack, the two allowlist reads, the resolver, the pipeline and the question queue. The
/// writes record an answer, a submission, an event, a park, and a run's start and end.
/// <see cref="MatchEmailToSubmissionAsync"/> sits with the writes because it is the step before
/// one and its failure mode is a write's, but it stores nothing itself.
///
/// <b>What the writes may do is narrower than what a pipeline usually allows.</b> They append to
/// the event log, set the park columns on a submission, store an answer by superseding the one it
/// replaces, open and close a question, and open and close a run. <b>None of them deletes,
/// none of them edits, and none of them sets a status</b> - the status is a fold over the event
/// log, and the one eraser in the system is a console command that needs a connection string.
/// Withdrawing an application is a <c>Withdrawn</c> event; correcting a claim is another event
/// carrying what was actually seen.
///
/// <b>Parking is an attribute of the submission and never an event, and the fold is the
/// reason.</b> <c>SubmissionEventType</c> is what the status is derived from and what the
/// dashboard groups by; a <c>Blocked</c> member would have to be ordered against the others, and
/// there is no numbering under which "the captcha beat us" advances or fails to advance an
/// application without lying about one case or the other. So a park writes columns the fold never
/// reads, the queue predicate reads those columns to decide whether the posting comes back, and
/// <c>ParkReasonPolicy</c> - one pure function in Core - is the whole of the retry policy. A
/// parked row is not a sent application and every reader that counts them has to know it.
///
/// <b>The daily cap stays exactly where it was, and is now visible.</b> It is enforced in
/// <c>SubmissionRepository</c>, on <c>Submitted</c> events, counted by the event's own
/// <c>AtUtc</c> - a guard written at the call sites survives until somebody adds another call
/// site, which is <c>AiCallRecord.Create</c>'s argument. What changed is that the burn-down is
/// reported by <see cref="ListApplyableAsync"/> and by <see cref="RecordEventAsync"/>, the two
/// points where a run can still act on it. Discovering the cap by being refused means discovering
/// it at <c>record_event</c>, which by the loop's design runs <i>after</i> the browser has sent
/// the form - an application that exists in the world and cannot be recorded, which is the worst
/// state this system has, because every later decision reads the log rather than the world.
///
/// <b>Field resolution runs on this side of the tool call, deliberately.</b>
/// <see cref="ResolveFormFieldAsync"/> hands the question to <c>IFormFieldResolver</c> rather
/// than handing the candidate's stored answers to the client to choose between: shipping the
/// answer store into a model's context is the whole-profile disclosure this surface exists
/// instead of, and it would be that disclosure with an extra hop and a bill attached. Three of
/// the resolver's four stages need no provider at all, so a deployment with no AI still answers
/// from the allowlist, from what the candidate has typed, and from what the same question
/// resolved to before; the fourth abstains rather than failing. <b>Abstention is a first-class
/// answer</b> everywhere on this surface: the characteristic failure of a matcher is the
/// confident near-miss, and a wrong answer on an application is read as a statement the candidate
/// made rather than as a bug in a tool they were using.
///
/// <b>Events and answers written here carry <c>Client</c>, never <c>Candidate</c>.</b> What a
/// person asserted and what an agent inferred are different kinds of claim, and a log that cannot
/// tell them apart cannot be audited after one turns out to be wrong. It is read from the token
/// and there is deliberately no <c>source</c> parameter: a tool argument naming the source would
/// let a model stamp its own inference as the candidate's own words, and a model filling in a
/// parameter helpfully is exactly what the rule below already exists to prevent.
/// <c>Candidate</c> is reachable only from the dashboard, where a person typed it.
///
/// <b>No tool takes a profile id, and none ever should.</b> Every one resolves the caller from
/// the token on the request and hands a subject id to a repository that has no overload accepting
/// anything else. That rule matters more here than on a route, because a route's arguments are
/// named by a router and these are named by a model: an unused <c>profileId</c> parameter is
/// exactly the kind of thing a model would helpfully fill in. The same reasoning bounds the two
/// ids that <i>are</i> accepted - a posting id is checked against this candidate's matches before
/// anything is written against it, and a submission id is resolved through their profile.
///
/// <b>An unattended client authenticates app-only, and that does not weaken the rule.</b> Its
/// token names a service principal rather than a person, so <c>McpOptions.AppPrincipals</c> says
/// which candidate that principal acts for. The identity still arrives with the token; the
/// indirection is written by whoever deployed the server and cannot be named by a caller. What
/// changes is the audit: a disclosure then records the candidate and the principal separately,
/// because "whose data left" and "what took it" stop being the same answer.
///
/// <b>Four reads disclose the candidate's own data and all four are logged.</b> There is still no
/// <c>get_profile</c> - a tool result is transcript content wherever the client runs - so
/// <see cref="GetFormFieldAsync"/> answers one allowlisted question at a time and
/// <see cref="GetFormFieldsAsync"/> answers several named ones in a round trip.
/// <b>The batch is a saving in round trips and not in audit</b>: it writes one disclosure per
/// field, exactly as the singular tool does, so a review of what left this system does not have
/// to know which shape the caller happened to use. <see cref="GetSubmissionPackAsync"/> returns
/// the tailored CV, which is the profile rewritten in prose, and the allowlist entries beside it;
/// <see cref="ResolveFormFieldAsync"/> returns whatever it decided to type. All are recorded on
/// the same terms, and a record names what was asked for and never what came back.
///
/// <b>The pack carries named entries and never a profile object, and that is what keeps the
/// paragraph above true.</b> An employment history handed over as a structure would put the shape
/// of the disclosure in the caller's hands: a field added to the underlying record would start
/// leaving the system with no diff saying so. <c>FormFieldCatalog</c> is the list, expansion of
/// its repeated groups included, and what is absent from it is as considered as what is present.
///
/// <b>These read and write Azure SQL</b>, which the architecture reserves for posting browse,
/// search and detail because it is billed on wall-clock time online against a monthly grant. The
/// bound is <c>RateLimitSetup.McpPolicy</c>, deliberately an order of magnitude below the
/// dashboard's: a client working through a day's applications is what this is sized for, and a
/// client polling every minute is the failure that rule exists to prevent.
/// </remarks>
[McpServerToolType]
public sealed class SubmissionTools(
    CandidateProfileRepository profiles,
    JobMatchRepository matches,
    SubmissionRepository submissions,
    ApplicationDocumentRepository documents,
    FormAnswerRepository answers,
    OpenQuestionRepository questions,
    RunRepository runs,
    IFormFieldResolver resolver,
    TimeProvider time,
    IOptions<McpOptions> mcp,
    IApplicationPackStore? packs = null,
    IDisclosureLog? disclosures = null)
{
    /// <summary>Hard ceiling regardless of what a caller asks for. Mirrors the matches endpoint.</summary>
    private const int MaxLimit = 100;

    /// <summary>
    /// How many names one batch read may carry.
    /// </summary>
    /// <remarks>
    /// The catalogue's own size, so the bound cannot be reached by asking for everything that
    /// exists and cannot drift when a repeated group grows. A caller sending more names than
    /// there are fields has not read the allowlist, and the refusal says so rather than answering
    /// the first page of a list nobody meant to write.
    /// </remarks>
    private static readonly int MaxFieldBatch = FormFieldCatalog.All.Count;

    [McpServerTool(Name = "list_applyable")]
    [Description(
        "The postings this candidate should apply to next: judged a credible fit by the "
        + "assessment pass, not dismissed, and with no live application and no standing block "
        + "against them. Each carries the URL to apply at and where the application is made: "
        + "'Ats' means the employer's own system, 'Board' means the job board hosts it, and "
        + "'Unknown' means neither was established - the URL is then the board's posting page and "
        + "the candidate finds out there. An 'Ats' posting may still have only the board's URL, "
        + "because some boards say the apply is offsite without saying where to. 'applyUrlSource' "
        + "says where the URL came from: 'Posting' is the employer's link as published, "
        + "'MatchedOnAnotherBoard' is the same job found on a different board and is an "
        + "inference, 'BoardPosting' means no direct link is known. 'atsVendor' says whose form "
        + "is at the end of it - 'Aggregator' is another job board rather than an employer and is "
        + "worth skipping. Duplicate listings of one job collapse into one row, with the others "
        + "in 'alternatePostings'. Ordered best first. Read the 'quota' block before planning a "
        + "batch: it says how many more applications may be recorded as sent today. This is a "
        + "work queue, not a search: it never returns a posting already applied to.")]
    public async Task<object> ListApplyableAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Restrict to 'Ats', 'Board' or 'Unknown'. Omit for all.")] string? channel = null,
        [Description("How many jobs to return, 1-100. Default 20. Jobs, not rows: duplicate listings collapse.")] int limit = 20,
        [Description("Only postings first seen at or after this UTC instant - 'what has arrived since my last run'.")] DateTimeOffset? since = null,
        [Description("Only pairs judged at or after this UTC instant. Different from 'since': the nightly pass judges postings that arrived weeks ago.")] DateTimeOffset? assessedSince = null,
        [Description("true for postings that already have a generated CV and cover letter, false for those still waiting on one. Omit for both.")] bool? documentsReady = null,
        [Description("A floor on the model's assessment score, 0-100. Enforced here: a pair the model scored no number for never clears it.")] int? minAssessmentScore = null,
        [Description("'Rank' (default, the fused ordering), 'Score' (the deterministic score) or 'AssessmentScore' (the model's judgement, unscored last).")] string? orderBy = null,
        [Description("Restrict to links of one provenance: 'Posting', 'MatchedOnAnotherBoard' or 'BoardPosting'. Ask for 'Posting' to get only employer links.")] string? applyUrlSource = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (!TryParseOptional<SubmissionChannel>(channel, out var parsedChannel))
        {
            return Refused(
                $"'{channel}' is not a channel. Expected 'Ats' (the employer's own system), "
                + "'Board' (the job board hosts the application) or 'Unknown' (neither was "
                + "established), or omit it for all three.");
        }

        if (!TryParseOptional<ApplyableSort>(orderBy, out var parsedSort))
        {
            return Refused(
                $"'{orderBy}' is not an ordering. Expected 'Rank' - the fused key this queue is "
                + "ordered by unless asked otherwise - or 'Score', or 'AssessmentScore'.");
        }

        if (!TryParseOptional<ApplyUrlSource>(applyUrlSource, out var parsedSource))
        {
            return Refused(
                $"'{applyUrlSource}' is not an apply-URL provenance. Expected 'Posting' (the "
                + "employer's link as the board published it), 'MatchedOnAnotherBoard' (the same "
                + "job found elsewhere, which is an inference) or 'BoardPosting' (no direct link "
                + "known), or omit it for all three.");
        }

        // Refused rather than clamped. A floor of 120 returns nothing, and a run told "no
        // postings today" would read that as a fact about the market rather than about its own
        // argument - the difference between an empty queue and a mistyped one is worth a sentence.
        if (minAssessmentScore is < 0 or > 100)
        {
            return Refused(
                $"minAssessmentScore is {minAssessmentScore}, and an assessment score runs from 0 "
                + "to 100. Omit it for no floor.");
        }

        var query = new ApplyableQuery
        {
            Channel = parsedChannel,
            ApplyUrlSource = parsedSource,
            Since = since,
            AssessedSince = assessedSince,
            DocumentsReady = documentsReady,
            MinAssessmentScore = minAssessmentScore,
            Sort = parsedSort ?? ApplyableSort.Rank,
            Limit = Math.Clamp(limit, 1, MaxLimit),
        };

        var rows = await matches.ListApplyableAsync(profileId!.Value, query, ct);

        var now = time.GetUtcNow();
        var quota = await submissions.GetQuotaAsync(profileId.Value, now, ct);
        var waiting = await questions.CountUnansweredAsync(profileId.Value, ct);

        var notes = new List<string>();

        if (quota.IsExhausted)
        {
            notes.Add(
                $"The day's cap of {quota.DailyCap} is spent, so nothing sent now could be "
                + "recorded as sent. Stop rather than applying: an application made and not "
                + "recorded is worse than one not made, because every later decision reads the "
                + "log rather than the world.");
        }

        if (waiting > 0)
        {
            notes.Add(
                $"{waiting} question(s) are waiting on the candidate - list_open_questions. "
                + "Answering one with record_form_answer closes it, and any posting parked for a "
                + "missing answer returns to this queue with it.");
        }

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

                // Read off the URL rather than stored. 'Aggregator' is the value that changes
                // what a run does: a "direct" link into another job board is another board, and
                // following it spends a day's cap discovering that by hand.
                atsVendor = row.AtsVendor.ToString(),
                verdict = row.Verdict?.ToString(),

                // Both numbers, and neither labelled the answer. The arithmetic says how much of
                // the posting the profile covers; the model's says whether the rest matters. They
                // disagree often and the disagreement is the informative part.
                score = row.Score,
                assessmentScore = row.AssessmentScore,
                rationale = row.Rationale,

                // RankScore is deliberately absent. It is min-maxed over one profile's eligible
                // pool, so it is not comparable between candidates or between nights and the top
                // of any pool is always exactly 100 - an ordering key, not a score, and no client
                // may display one.
                assessedAtUtc = row.AssessedAtUtc,
                firstSeenUtc = row.FirstSeenUtc,
                hasDocuments = row.HasDocuments,

                // Null means this posting has no cross-board identity, never that it shares an
                // empty one with every other unlocated row.
                dedupeKey = row.DedupeKey,
                alternatePostings = row.AlternatePostings.Select(member => new
                {
                    postingId = member.PostingId,
                    applyUrlSource = member.ApplyUrlSource.ToString(),
                    assessmentScore = member.AssessmentScore,
                    hasDocuments = member.HasDocuments,
                }).ToList(),
            }).ToList(),

            // Planning, never a reservation: nothing is held back and two clients sharing a
            // candidate can each be told six. The cap in the repository remains the authority.
            quota = new
            {
                dailyCap = quota.DailyCap,
                submittedOnDay = quota.SubmittedOnDay,
                remaining = quota.Remaining,
                day = quota.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                plan = SubmissionQuota.Plan(quota, rows.Count),
            },
            questionsWaiting = waiting,
            note = notes.Count == 0 ? null : string.Join(" ", notes),
        };
    }

    [McpServerTool(Name = "get_submission_pack")]
    [Description(
        "Everything needed to fill in one application: the advert's own text, where to apply, the "
        + "tailored CV and cover letter as markdown, short-lived download links to the rendered "
        + "PDF and DOCX where they exist, the free-text answers drafted for this posting, and the "
        + "allowlisted profile answers a form asks for by name. Returns an explanation rather "
        + "than an error where no documents have been generated yet - they are written on request "
        + "from the dashboard and this surface does not generate them. The document links are "
        + "minted per request and expire; fetch the pack again rather than storing one.")]
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

        // The defect this fixes: the description has always promised "where to apply" and the
        // response has never carried it, so a client that had dropped its queue row had to go
        // back to list_applyable for a fact the pack already knew. The link is resolved by the
        // repository rather than re-derived here - exactly one place decides that a missing
        // JobUrlDirect means the board hosts it, and this would have been the third spelling.
        var target = await matches.ResolveApplyTargetAsync(profileId.Value, postingId, ct);

        var draft = await documents.GetLatestForPostingAsync(profileId.Value, postingId, ct);

        // The mapped subject, not the token's principal: an app-only caller reads the candidate
        // it acts for, and ResolveAsync has already established there is one.
        var subjectId = Caller(context).SubjectId!;
        var view = await profiles.GetAsync(subjectId, ct);

        // Named entries, never a profile object. The catalogue decides what exists - repeated
        // groups expanded, bounded and individually named - so a field added to CandidateProfile
        // cannot start leaving the system without a diff here saying so. Absent values are
        // dropped rather than sent as nulls: "the profile does not carry this" is not an answer a
        // form should be filled in with.
        var fields = FormFieldCatalog.All
            .Select(field => new { name = field.Name, value = view is null ? null : field.Read(view.Profile) })
            .Where(entry => entry.value is not null)
            .ToList();

        var rendered = draft?.Rendered;

        // Typed rather than left to var, so the empty case has a target: a posting whose
        // documents were never written has no drafted answers rather than a missing list.
        IReadOnlyList<DraftedAnswer> drafted = draft?.DraftedAnswers ?? [];

        // Minted per request and never stored: the URL is the authority, so a stored one extends
        // that authority to whoever reads the store, and an expired one is a dead pointer that
        // still looks live. LinkAsync answers null for a null path and for any storage failure,
        // so a deployment with no pack store simply has no links to offer.
        var cvPdf = packs is null ? null : await packs.LinkAsync(rendered?.CvBlobPath, ct);
        var cvDocx = packs is null ? null : await packs.LinkAsync(rendered?.CvDocxBlobPath, ct);
        var coverLetter = packs is null ? null : await packs.LinkAsync(rendered?.CoverLetterBlobPath, ct);

        // Logged whether or not anything came back, and it names what was asked for rather than
        // what was returned. This is the read that hands over the CV - the profile rewritten in
        // prose - alongside the allowlist entries, so it is recorded on the same terms as
        // get_form_field rather than treated as a public-text read.
        await RecordAsync(
            context,
            "get_submission_pack",
            $"posting {postingId}; {fields.Count} profile field(s)",
            draft is not null || fields.Count > 0,
            ct);

        var notes = new List<string>();

        if (draft is null)
        {
            notes.Add(
                "No documents have been generated for this posting yet. They are written on "
                + "request from the dashboard; this surface does not generate them.");
        }
        else if (cvPdf is null && cvDocx is null && coverLetter is null)
        {
            notes.Add(
                "The documents exist as markdown but no rendered file is available to link to - "
                + "either they were written before rendering was stored, or this deployment has "
                + "no document storage configured. The markdown is the record and is complete.");
        }

        if (target?.Channel is SubmissionChannel.Ats)
        {
            notes.Add(
                "The employer takes this application, and the link is this posting's own: it is "
                + "either the employer's published link or the board's page where the board said "
                + "the apply is offsite without saying where to. list_applyable is the read that "
                + "distinguishes those and the only one that borrows a link from another listing "
                + "of the same job.");
        }

        return new
        {
            postingId,
            title = posting.Title,
            company = posting.Company,
            advertText = posting.Text,

            // Where to go, and where the application is made when you get there.
            applyUrl = target?.ApplyUrl,
            channel = target?.Channel.ToString(),

            // Settled only where no direct link can exist: the repository coalesces the posting's
            // own link with its board page, so an 'Ats' posting could be carrying either and this
            // read cannot tell which. Said as null rather than guessed - a caller that cannot
            // tell an inference from a published fact has no way to notice when it was wrong.
            applyUrlSource = target is null || target.Channel is SubmissionChannel.Ats
                ? null
                : ApplyUrlSource.BoardPosting.ToString(),
            atsVendor = AtsVendorDetector.Detect(target?.ApplyUrl).ToString(),

            curriculumVitaeMarkdown = draft?.CurriculumVitaeMarkdown,
            coverLetterMarkdown = draft?.CoverLetterMarkdown,
            revision = draft?.Revision,
            documentUrls = new
            {
                curriculumVitaePdf = cvPdf?.ToString(),
                curriculumVitaeDocx = cvDocx?.ToString(),
                coverLetterPdf = coverLetter?.ToString(),

                // Stated rather than left to be discovered. A client that does not know a link
                // expires will store it, retry with it an hour later, and report the failure as
                // a missing document.
                expiresInMinutes = packs is null ? null : (int?)packs.LinkLifetime.TotalMinutes,

                // Over the rendered bytes, so a file can be checked against this row afterwards -
                // a path alone cannot say whether what is at the end of it is still what was sent.
                cvSha256 = rendered?.CvSha256,
            },

            // Written when the documents were, from the advert and the match: prose about this
            // employer, which is the one kind of answer that cannot be stored and reused. Empty
            // means "not generated yet", never "this posting has nothing worth saying".
            draftedAnswers = drafted.Select(answer => new
            {
                questionText = answer.QuestionText,
                answer = answer.Answer,
                category = answer.Category.ToString(),
            }).ToList(),

            profileFields = fields,
            note = notes.Count == 0 ? null : string.Join(" ", notes),
        };
    }

    [McpServerTool(Name = "get_form_field")]
    [Description(
        "One named answer about the candidate, from a fixed server-side allowlist - for filling "
        + "a single field on an application form. There is deliberately no tool that returns the "
        + "whole profile. Call with no name to see what may be asked for. Use get_form_fields to "
        + "read several at once. Every call is recorded.")]
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

    [McpServerTool(Name = "get_form_fields")]
    [Description(
        "Several named answers about the candidate in one call, from the same fixed allowlist "
        + "get_form_field answers from - for a form asking fifteen things a person would type "
        + "once. Names outside the allowlist are refused one by one, with the rest still "
        + "answered, so a single wrong name does not cost the whole read. This is not a profile "
        + "read: the allowlist bounds what can be asked for, and every field answered is recorded "
        + "separately, exactly as if it had been asked for on its own.")]
    public async Task<object> GetFormFieldsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The fields to read, e.g. ['email','work_history[0].employer']. Call get_form_field with no name to see what may be asked for.")] string[] names,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        // Case folded and duplicates dropped before anything is counted or read. A form that
        // asks for the same field twice is a form, not an error, and answering it twice would
        // also write the disclosure twice for one question.
        string[] given = names ?? [];

        var requested = given
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count == 0)
        {
            return Refused(
                "No field names were given. Pass the names the form asks for, or call "
                + "get_form_field with no name to see what may be asked for.");
        }

        if (requested.Count > MaxFieldBatch)
        {
            return Refused(
                $"{requested.Count} names were asked for and the whole allowlist is only "
                + $"{MaxFieldBatch}. Ask for the fields this form actually has; get_form_field "
                + "with no name lists them.");
        }

        // One profile read for the whole batch, which is the entire saving this tool exists for.
        // The disclosure below is still per field: what left the system is the same either way,
        // and an audit that had to know which shape the caller used would be a worse audit.
        var subjectId = Caller(context).SubjectId!;
        var view = await profiles.GetAsync(subjectId, ct);

        var items = new List<object>(requested.Count);

        foreach (var name in requested)
        {
            if (!FormFieldCatalog.TryGet(name, out var field))
            {
                // Refused per name, in the same words the singular tool uses, and without
                // touching the profile. The rest of the batch is still answered: a model that
                // guessed one name wrong should learn that from one entry rather than from an
                // empty response.
                items.Add(new
                {
                    name,
                    value = (string?)null,
                    refused = true,
                    reason = (string?)($"'{name}' is not a field this system will answer. Allowed: "
                        + string.Join(", ", FormFieldCatalog.Names) + "."),
                    note = (string?)null,
                });

                continue;
            }

            var value = view is null ? null : field.Read(view.Profile);

            await RecordAsync(context, "get_form_fields", field.Name, value is not null, ct);

            items.Add(new
            {
                name = field.Name,
                value,
                refused = false,
                reason = (string?)null,
                note = value is null
                    ? "The profile does not carry this. Ask the candidate rather than inferring it."
                    : null,
            });
        }

        return new { items };
    }

    [McpServerTool(Name = "resolve_form_field")]
    [Description(
        "What to type into one form field, or the reason a person has to. Four stages run "
        + "server-side and stop at the first that decides: an exact allowlist name, the "
        + "candidate's own stored answer to this same question, what this question resolved to "
        + "before, and only then a model - which is asked to choose between the candidate's own "
        + "answers and never to compose one. 'needsUser: true' with no value is an ordinary and "
        + "frequent answer: a wrong answer on an application reads as a statement the candidate "
        + "made, so refusing is the cheap outcome and guessing is not. Anything only the "
        + "candidate may state - sponsorship, right to work, salary, an EEO question - is "
        + "answered verbatim from what they have stored or not at all, never mapped onto the "
        + "nearest option. Where a person must answer, park the application with reason "
        + "'MissingAnswer' and the question text, which puts it in front of them.")]
    public async Task<object> ResolveFormFieldAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The question exactly as the form asks it, wording and punctuation included.")] string questionText,
        [Description("The choices the form offers, in the form's own words. Omit for a free-text box.")] string[]? options = null,
        [Description("The form's own field name where it has one, e.g. 'notice_period'. Used as a second key, never as the answer.")] string? name = null,
        [Description("The posting whose form is being filled in, where there is one. Lets an answer written for that advert be used.")] long? postingId = null,
        [Description("Set where the question asks for something only the candidate may state. It can only tighten: a question that reads as sensitive is treated as one whatever this says.")] bool sensitive = false,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(questionText))
        {
            return Refused(
                "No question was given. Send the question as the form asks it, including its "
                + "wording and any choices it offers - the wording is the key this system stores "
                + "and finds answers by.");
        }

        if (questionText.Trim().Length > FormAnswerLimits.MaxQuestionTextLength)
        {
            return Refused(
                $"The question is longer than {FormAnswerLimits.MaxQuestionTextLength} "
                + "characters, which is more than a form field's label. Send the question and not "
                + "the page around it.");
        }

        var now = time.GetUtcNow();
        var subjectId = Caller(context).SubjectId!;
        var view = await profiles.GetAsync(subjectId, ct);

        // Superseded answers are included deliberately. A live answer beats a retracted one at
        // every scope, so nothing older can win; what the extra rows buy is the resolver's
        // ability to say "they answered this and then took it back, ask them again" instead of
        // finding nothing and putting the question to a model. The model stage shortlists live
        // answers only, so a retracted sentence cannot be chosen and typed.
        var stored = await answers.ListAsync(profileId!.Value, now, includeSuperseded: true, ct);

        var choices = options is { Length: > 0 } ? options : null;
        var cached = await answers.GetResolutionAsync(profileId.Value, questionText, choices, now, ct);

        var resolution = await resolver.ResolveAsync(
            new FormFieldRequest
            {
                QuestionText = questionText,
                Options = choices,
                Name = name,
                Sensitive = sensitive,
                Answers = [.. stored.Select(entry => entry.Answer)],
                Cached = cached is null
                    ? null
                    : new PriorResolution(
                        cached.Answer?.Answer,
                        cached.ResolvedName,
                        cached.Confidence,
                        cached.Rationale,
                        cached.ResolvedAtUtc,
                        cached.Confirmed),

                // The profile is handed over for the allowlist stage alone; the resolver refuses
                // to read it at all for a question that looks sensitive, which is why nothing
                // sensitive can be answered from it by any wording.
                Profile = view?.Profile,

                // No company id. Nothing on this surface can name an employer's row - a
                // company-scoped answer is written and read from the dashboard, where the
                // employer is chosen from the pipeline rather than typed by a model.
                CompanyId = null,
                PostingId = postingId,
            },
            ct);

        // Cached only where a model was actually consulted, which is the one thing the cache
        // exists to prevent happening twice. Writing a stage-one or stage-two decision here would
        // buy nothing - both stages run before the cache is even read - and would clear the
        // Confirmed flag a person may have set on the row it overwrote.
        if (resolution.ConsultedModel)
        {
            await answers.RecordResolutionAsync(
                profileId.Value,
                new ResolutionOutcome(
                    questionText,
                    choices,
                    resolution.Confidence,
                    resolution.Rationale,
                    resolution.AnswerId,
                    resolution.Field,
                    resolution.Model),
                now,
                ct);
        }

        // The question, never the answer. This is a disclosure like any other read here: what
        // comes back is typed into somebody else's form under the candidate's name.
        await RecordAsync(context, "resolve_form_field", questionText, resolution.Value is not null, ct);

        return new
        {
            value = resolution.Value,
            needsUser = resolution.NeedsUser,

            // Which of the four stages decided, so "the second occurrence of a question resolves
            // without a model call" is something a caller can see rather than take on trust.
            stage = resolution.Stage.ToString(),
            consultedModel = resolution.ConsultedModel,
            field = resolution.Field,
            confidence = resolution.Confidence,
            rationale = resolution.Rationale,
            sensitive = resolution.Sensitive,
            model = resolution.Model,
            note = resolution.NeedsUser
                ? "Nothing was answered. Do not compose one: park the application with reason "
                    + "'MissingAnswer' and this question text, or ask the candidate directly and "
                    + "record what they say with record_form_answer."
                : null,
        };
    }

    [McpServerTool(Name = "list_submissions")]
    [Description(
        "The candidate's applications and where each stands, most recently active first. The "
        + "status is folded from an append-only event log, so 'stale' means genuinely nothing "
        + "has happened for a fortnight rather than that a flag was left set. Closed "
        + "applications are never stale. A row with 'parked: true' is NOT a sent application - it "
        + "is a posting something got in the way of, and 'parkedReason' says what.")]
    public async Task<object> ListSubmissionsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Restrict to one phase, e.g. 'InterviewScheduled'. Omit for all.")] string? phase = null,
        [Description("Only those active since this UTC timestamp. Omit for all.")] DateTimeOffset? since = null,
        [Description("true for only parked rows, false for only live applications. Omit for both.")] bool? parked = null,
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

            // The pair, never ParkedReason alone: nothing on this table is cleared, so a row
            // parked in March and applied to in April still carries the reason it was parked for.
            .Where(row => parked is null || row.IsParked == parked)
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

                // The one fact about a submission the fold cannot answer, because a park is
                // deliberately not an event. A reader counting sent applications has to subtract
                // these, and cannot do it from the phase alone.
                parked = row.IsParked,
                parkedReason = row.ParkedReason?.ToString(),
                parkedAtUtc = row.ParkedAtUtc,
                unparkedAtUtc = row.UnparkedAtUtc,

                // Which draft was actually sent, and which unattended pass sent it.
                documentRevision = row.DocumentRevision,
                runId = row.RunId,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "list_open_questions")]
    [Description(
        "The questions this system could not answer and has put to the candidate, oldest first. "
        + "Each is one wording asked once, however many adverts asked it. An application parked "
        + "for 'MissingAnswer' is held until its question is answered, so this queue is what a "
        + "run is waiting on rather than a log of what went wrong. Answer one with "
        + "record_form_answer, using the question text exactly as it appears here: that closes it "
        + "and returns the postings parked on it to list_applyable.")]
    public async Task<object> ListOpenQuestionsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("How many to return, 1-100. Default 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        var rows = await questions.ListUnansweredAsync(
            profileId!.Value, Math.Clamp(limit, 1, MaxLimit), ct);

        return new
        {
            items = rows.Select(row => new
            {
                questionId = row.Id,

                // The advert that raised it, and null for a question asked from the dashboard.
                // Context rather than identity: one wording is one row whichever advert hit it
                // first, and the other adverts' waiting is recorded on their own parked rows.
                postingId = row.PostingId,
                postingTitle = row.PostingTitle,
                company = row.Company,
                runId = row.RunId,
                questionText = row.QuestionText,

                // Empty means a free-text box or that the options were never recorded, and those
                // are the same fact here: the form did not answer that question either.
                options = row.Options,

                // Something only the candidate may state. It drives redaction and a confirmation
                // step, never permission to infer an answer to it.
                sensitive = row.Sensitive,
                askedAtUtc = row.AskedAtUtc,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "record_form_answer")]
    [Description(
        "Stores what the candidate answers to a form question, so the next form asking it is "
        + "filled in without asking again. Store the answer in the words that would be typed into "
        + "a form. There is deliberately no 'source' parameter: everything written through this "
        + "surface is recorded as a client's assertion, never as the candidate's own - only the "
        + "dashboard can say a person typed it. Over-long text is refused rather than shortened: "
        + "a truncated sentence typed into an application reads as a statement rather than as a "
        + "bug. If the answer matches a question waiting in list_open_questions, that question is "
        + "closed and any application parked on it returns to the queue.")]
    public async Task<object> RecordFormAnswerAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The question exactly as the form asked it, or exactly as it appears in list_open_questions.")] string questionText,
        [Description("The answer, in the words that would be typed into the form. Stored verbatim.")] string value,
        [Description("'Global' (true wherever it is asked - notice period, sponsorship) or 'Posting' (true only of one advert). Default 'Global'.")] string? scope = null,
        [Description("The advert this answer is only true of. Required for scope 'Posting', and refused for 'Global'.")] long? postingId = null,
        [Description("A canonical name to file it under where the question has one, e.g. 'notice_period'.")] string? name = null,
        [Description("Set where the answer is something only the candidate may state. It can only tighten: a question that reads as sensitive is stored as sensitive whatever this says.")] bool sensitive = false,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(questionText))
        {
            return Refused(
                "questionText is required. It is the key this answer is stored and found under, "
                + "so an answer with no question is one nothing can ever offer back.");
        }

        // Whitespace is not an answer, and storing it as one would tell every later resolution
        // that this question is settled. "Prefer not to say" is a value; nothing is not.
        if (string.IsNullOrWhiteSpace(value))
        {
            return Refused(
                "value is required. If the candidate declined to answer, store what they would "
                + "actually put in the box - 'Prefer not to say' is an answer; blank is not.");
        }

        // Refused, never truncated, and checked here so the caller gets a structured answer
        // rather than the exception FormAnswer.Create throws for the same bound.
        if (questionText.Trim().Length > FormAnswerLimits.MaxQuestionTextLength)
        {
            return Refused(
                $"The question is longer than {FormAnswerLimits.MaxQuestionTextLength} "
                + "characters. Send the question the form asks, not the page around it.");
        }

        if (value.Trim().Length > FormAnswerLimits.MaxValueLength)
        {
            return Refused(
                $"The answer is longer than {FormAnswerLimits.MaxValueLength} characters and is "
                + "refused rather than shortened - a truncated answer is typed into an employer's "
                + "form and reads as a statement rather than as a bug. Shorten it deliberately.");
        }

        if (name is { Length: > 0 } && name.Trim().Length > FormAnswerLimits.MaxNameLength)
        {
            return Refused(
                $"The name is longer than {FormAnswerLimits.MaxNameLength} characters. A name is "
                + "a key, e.g. 'notice_period'; the wording a person reads is the question.");
        }

        if (!TryParseOptional<AnswerScope>(scope, out var parsedScope))
        {
            return Refused(
                $"'{scope}' is not a scope. Expected 'Global' (true wherever it is asked) or "
                + "'Posting' (true of one advert only).");
        }

        var answerScope = parsedScope ?? AnswerScope.Global;

        if (answerScope is AnswerScope.Company)
        {
            return Refused(
                "A company-scoped answer cannot be recorded through this surface, because "
                + "nothing here names an employer's row and an answer filed against the wrong "
                + "employer is the one this scoping exists to prevent. Record it as 'Posting' "
                + "against the advert that asked, or as 'Global' if it is true wherever it is "
                + "asked; the dashboard is where an answer is widened to one employer.");
        }

        if (answerScope is AnswerScope.Posting && postingId is null)
        {
            return Refused(
                "Scope 'Posting' needs the posting it is true of. Pass postingId, or record it as "
                + "'Global' if the answer holds wherever it is asked.");
        }

        if (answerScope is AnswerScope.Global && postingId is not null)
        {
            return Refused(
                "A 'Global' answer carries no posting: a row scoped globally with a posting id on "
                + "it looks narrowed in the database and is not. Use scope 'Posting' to file it "
                + "against that advert, or drop postingId.");
        }

        if (postingId is { } posting)
        {
            // The same rule create_submission follows, and it is checked because this write puts
            // a posting id from a model's argument into a stored row. An answer filed against an
            // advert nobody matched is one nothing will ever read back.
            var target = await matches.ResolveApplyTargetAsync(profileId!.Value, posting, ct);

            if (target is null)
            {
                return Refused(
                    $"Posting {posting} has not been matched against this candidate's profile, so "
                    + "an answer cannot be scoped to it. Use list_applyable.");
            }
        }

        var now = time.GetUtcNow();

        var answer = FormAnswer.Create(
            questionText,
            value,
            answerScope,
            // Client, never Candidate, and read from the token rather than from an argument. A
            // model filling in a `source` parameter is how an agent's inference gets stamped as
            // the candidate's own words; `Candidate` belongs to the dashboard, where a person
            // typed it, and an app-only token and a person's own client are both clients here.
            FormAnswerSource.Client,
            now,
            name,
            companyId: null,
            postingId,
            // The flag or the question, never the flag alone - the half of the guarantee that
            // does not depend on a boolean being right. A caller may only tighten this.
            sensitive: sensitive || SensitiveQuestions.Looks(questionText));

        var (recorded, created) = await answers.RecordAsync(profileId!.Value, answer, now, ct);

        // Recording an answer and closing the question it answers are one act from the
        // candidate's point of view. Split across two calls the second is the one that gets
        // forgotten, leaving a question in the queue the system can already answer and a posting
        // parked on it forever.
        var closed = await questions.AnswerByHashAsync(
            profileId.Value, recorded.Answer.QuestionHash, recorded.Answer.Id, now, ct);

        var notes = new List<string>();

        if (!created)
        {
            notes.Add(
                "That answer was already stored, word for word, so nothing was written and "
                + "nothing was superseded. The answer that stands is the one returned.");
        }

        if (closed is not null)
        {
            notes.Add(
                $"This closed open question {closed.Id}. Any application parked for a missing "
                + "answer to it returns to list_applyable.");
        }

        return new
        {
            answerId = recorded.Answer.Id,
            created,
            scope = recorded.Answer.Scope.ToString(),
            postingId = recorded.Answer.PostingId,
            name = recorded.Answer.Name,
            sensitive = recorded.Answer.Sensitive,
            answeredAtUtc = recorded.Answer.AnsweredAtUtc,
            closedQuestionId = closed?.Id,
            note = notes.Count == 0 ? null : string.Join(" ", notes),
        };
    }

    [McpServerTool(Name = "create_submission")]
    [Description(
        "Records that an application exists for one posting, and - when 'sent' is true - that it "
        + "was actually sent, in one write. Does NOT apply to anything: nothing in this system "
        + "can reach an employer; a person or a browser does the applying and this records that "
        + "it happened. Pass sent: true in the call you make immediately after the form has gone, "
        + "with an idempotencyKey and whatever the confirmation page showed. That is what the "
        + "parameter is for: a submission created by one call and evidenced by a second has a "
        + "window in which the application exists in the world and not in the log, which is the "
        + "one state this pipeline cannot recover from. Read the result: 'created' says whether "
        + "this call made the submission, 'result' says what happened to the event - 'Recorded' "
        + "it went in, 'AlreadyRecorded' your retry converged and nothing needs doing, "
        + "'DailyLimitReached' NOTHING was written at all, not even the submission, and "
        + "'NoEventRequested' you did not ask for one. Idempotent "
        + "per posting: calling it twice returns the submission that already exists rather than "
        + "making a second, and never overwrites where the first said the application went.")]
    public async Task<object> CreateSubmissionAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The posting applied to. Must appear in list_applyable, or already be matched.")] long postingId,
        [Description("Where it was sent: 'Ats', 'Board' or 'Unknown'. Omit to take it from the posting.")] string? channel = null,
        [Description("The URL actually applied at. Omit to record the posting's own apply link.")] string? applyUrl = null,
        [Description("true only where the application has actually just been sent. It records a 'Submitted' event in the same write and spends one of the day's quota.")] bool sent = false,
        [Description("Your own key for that event, unique per submission. Required when sent is true. A run uses '<runId>:<postingId>:Submitted'.")] string? idempotencyKey = null,
        [Description("When it was sent, UTC. Omit for now - which is right only when it just was.")] DateTimeOffset? atUtc = null,
        [Description("A sentence of context for the candidate. Never paste a message body.")] string? note = null,
        [Description("The reference the employer's system showed, e.g. 'Application #4417290'.")] string? confirmationRef = null,
        [Description("Where the browser ended up - the confirmation page, not where the attempt started.")] string? finalUrl = null,
        [Description("A pointer to a stored screenshot. A path, never an image and never a signed URL.")] string? screenshotRef = null,
        [Description("The NAMES of the fields that were filled in. Names only - never the answers given to them.")] string[]? submittedFields = null,
        [Description("Which revision of the generated documents was sent, from get_submission_pack.")] int? documentRevision = null,
        [Description("The run doing this, from start_run. Omit outside a run.")] long? runId = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (!TryParseOptional<SubmissionChannel>(channel, out var requested))
        {
            return Refused(
                $"'{channel}' is not a channel. Expected 'Ats' (the employer's own system), "
                + "'Board' (the job board hosts the application) or 'Unknown'.");
        }

        if (sent && string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Refused(
                "idempotencyKey is required when sent is true. It is what makes a retry record "
                + "the same send once, and only the caller knows whether two requests are one "
                + "application or two. Inside a run, use '<runId>:<postingId>:Submitted'.");
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

        var now = time.GetUtcNow();

        if (!sent)
        {
            var (row, created) = await submissions.CreateAsync(
                profileId.Value,
                postingId,
                requested ?? target.Channel,
                applyUrl ?? target.ApplyUrl,
                now,
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
                recorded = false,
                result = "NoEventRequested",
                quota = (object?)null,
                note = created
                    ? "Recorded that this application exists, and nothing about it having been "
                        + "sent. Add a 'Submitted' event with record_event once it actually is - "
                        + "or call this with sent: true at that moment, which is one write "
                        + "instead of two."
                    : "A submission for this posting already existed and was returned unchanged.",
            };
        }

        var result = await submissions.CreateWithEventAsync(
            profileId.Value,
            postingId,
            requested ?? target.Channel,
            applyUrl ?? target.ApplyUrl,
            // Client, not Candidate. What a person asserted and what an agent inferred are
            // different kinds of claim, and the log is only auditable if it says which.
            new SubmissionEvent(
                atUtc ?? now, SubmissionEventType.Submitted, null, SubmissionEventSource.Client, note)
            {
                Evidence = Captured(confirmationRef, finalUrl, screenshotRef, submittedFields),
            },
            idempotencyKey!,
            now,
            documentRevision,
            runId,
            ct);

        // Counted on the event's own day, because that is the day the cap is enforced on: a
        // backdated send spends the quota of the day it claims, not of today. The design's rule
        // is that a call spending no quota reports none - this arm spends it, so it reports the
        // burn-down for the same reason record_event does.
        var quota = await submissions.GetQuotaAsync(profileId.Value, atUtc ?? now, ct);

        return new
        {
            submissionId = result.Row?.Id,
            postingId,
            title = result.Row?.Title,
            channel = (result.Row?.Channel ?? requested ?? target.Channel).ToString(),
            applyUrl = result.Row?.ApplyUrl ?? applyUrl ?? target.ApplyUrl,
            created = result.Created,
            recorded = result.Event is SubmissionEventResult.Recorded,
            result = result.Event.ToString(),
            quota = (object?)new
            {
                dailyCap = quota.DailyCap,
                submittedOnDay = quota.SubmittedOnDay,
                remaining = quota.Remaining,
                day = quota.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            note = result.Event switch
            {
                SubmissionEventResult.Recorded => result.Created
                    ? "The submission and the send went in together, so there is no window in "
                        + "which one exists without the other."
                    : "The submission already existed and this send was added to it, which is the "
                        + "ordinary second claim about one application rather than a failure.",
                SubmissionEventResult.AlreadyRecorded =>
                    "That key is already on this submission, so the earlier call landed. Nothing "
                    + "was duplicated and nothing needs retrying.",
                _ => $"This candidate has already recorded {SubmissionLimits.MaxSubmittedPerDay} "
                    + "applications as sent for that day, which is the cap, and NOTHING was "
                    + "written - not even the submission, deliberately: a row whose send was "
                    + "refused would take this posting out of the queue for good while asserting "
                    + "nothing about an application. If the application really was sent, park it "
                    + "with reason 'OutOfQuota' so the attempt is visible, and stop for today.",
            },
        };
    }

    [McpServerTool(Name = "record_event")]
    [Description(
        "Appends one event to a submission's log - that it was sent, acknowledged, rejected, an "
        + "interview booked, and so on. The log is append-only: nothing edits or deletes an "
        + "event, and withdrawing an application is a 'Withdrawn' event. Requires an "
        + "idempotencyKey the caller chooses, so a retry records the same thing once. Evidence is "
        + "optional and never blocks the write - a send that could not be screenshotted is still "
        + "a send. There is a daily cap on 'Submitted' events, and the 'quota' block on the "
        + "answer is the burn-down: watch it fall rather than discovering the cap by being "
        + "refused, which happens after the form has already gone.")]
    public async Task<object> RecordEventAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The submission to append to, from create_submission or list_submissions.")] long submissionId,
        [Description("One of: Submitted, Acknowledged, ScreeningScheduled, InterviewScheduled, OfferReceived, Rejected, Withdrawn.")] string type,
        [Description("Your own key for this event, unique per submission. The same key twice records once.")] string idempotencyKey,
        [Description("When it happened, UTC. Omit for now - which is right only when it just did.")] DateTimeOffset? atUtc = null,
        [Description("The round or label inside the phase, e.g. 'Tech round 2'. Free text.")] string? stage = null,
        [Description("A sentence of context for the candidate. Never paste a message body.")] string? note = null,
        [Description("The reference the employer's system showed, e.g. 'Application #4417290'.")] string? confirmationRef = null,
        [Description("Where the browser ended up - the confirmation page, not where the attempt started.")] string? finalUrl = null,
        [Description("A pointer to a stored screenshot. A path, never an image and never a signed URL.")] string? screenshotRef = null,
        [Description("The NAMES of the fields that were filled in. Names only - never the answers given to them.")] string[]? submittedFields = null,
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

        var at = atUtc ?? time.GetUtcNow();

        var result = await submissions.AddEventAsync(
            profileId!.Value,
            submissionId,
            // Client, not Candidate. What a person asserted and what an agent inferred are
            // different kinds of claim, and the log is only auditable if it says which.
            new SubmissionEvent(at, parsed, stage, SubmissionEventSource.Client, note)
            {
                Evidence = Captured(confirmationRef, finalUrl, screenshotRef, submittedFields),
            },
            idempotencyKey,
            ct);

        // Against the event's own day rather than today's, because that is the window the cap
        // counts over: an event backdated into last Tuesday spends last Tuesday's quota, and a
        // burn-down describing a different day would disagree with the bound in force.
        var quota = await submissions.GetQuotaAsync(profileId.Value, at, ct);

        return new
        {
            recorded = result is SubmissionEventResult.Recorded,
            result = result.ToString(),
            quota = new
            {
                dailyCap = quota.DailyCap,
                submittedOnDay = quota.SubmittedOnDay,
                remaining = quota.Remaining,
                day = quota.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            note = result switch
            {
                SubmissionEventResult.Recorded => (string?)null,
                SubmissionEventResult.AlreadyRecorded =>
                    "That key is already on this submission, so the earlier call landed. Nothing "
                    + "was duplicated and nothing needs retrying.",
                SubmissionEventResult.NotFound =>
                    $"No submission {submissionId} for this candidate. Use list_submissions, or "
                    + "create_submission first.",
                _ => $"This candidate has already recorded {SubmissionLimits.MaxSubmittedPerDay} "
                    + "applications as sent for that day, which is the cap. Stop rather than "
                    + "retrying: the limit exists so a client looping cannot fill somebody's "
                    + "pipeline with applications never made. If the application really was sent, "
                    + "park the posting with reason 'OutOfQuota' so the attempt is visible.",
            },
        };
    }

    [McpServerTool(Name = "park_application")]
    [Description(
        "Puts a posting down without applying to it, and says why. This is how a run reports what "
        + "stopped it - a captcha, a login wall, an account it will not create, a form that broke, "
        + "a question nobody has answered, a spent daily quota, an advert that has expired, a "
        + "duplicate of one already applied to. It is not an event and it does not claim an "
        + "application: the posting leaves the queue and 'requeue' says whether it comes back - "
        + "'NextRun' for the blocks that pass, 'WhenAnswered' for a missing answer, 'Never' for "
        + "an expired or duplicate listing. Parking with 'MissingAnswer' also puts the question "
        + "to the candidate, so pass the question exactly as the form asked it. Park rather than "
        + "skipping silently: a posting nobody parked is offered again next run with no record of "
        + "what happened last time. Refused for a posting whose application already carries "
        + "events - that one was made rather than blocked, and what happened to it afterwards is "
        + "record_event's job.")]
    public async Task<object> ParkApplicationAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The posting being put down. Must already be matched to this candidate.")] long postingId,
        [Description("Why: Expired, Duplicate, LoginRequired, Captcha, AccountRequired, MissingAnswer, FormError or OutOfQuota.")] string reason,
        [Description("The question the form asked that nothing could answer. Required for MissingAnswer; it opens a question for the candidate.")] string? questionText = null,
        [Description("The choices that question offered, where it offered any.")] string[]? options = null,
        [Description("Set where that question asks for something only the candidate may state.")] bool sensitive = false,
        [Description("The run doing this, from start_run. Omit outside a run.")] long? runId = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        if (!Enum.TryParse<ParkReason>(reason, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return Refused(
                $"'{reason}' is not a park reason. Expected one of: "
                + string.Join(", ", Enum.GetNames<ParkReason>())
                + ". The reason decides whether the posting comes back, so pick the one that "
                + "actually happened rather than the nearest.");
        }

        if (parsed is ParkReason.MissingAnswer && string.IsNullOrWhiteSpace(questionText))
        {
            return Refused(
                "Parking for 'MissingAnswer' needs the question that could not be answered: it is "
                + "what gets put to the candidate, and it is what lets this posting come back "
                + "when they answer. Send it as the form asked it. If the form asked nothing and "
                + "something else stopped the run, park under the reason that did.");
        }

        var target = await matches.ResolveApplyTargetAsync(profileId!.Value, postingId, ct);

        if (target is null)
        {
            return Refused(
                $"Posting {postingId} has not been matched against this candidate's profile, so "
                + "there is nothing to park. Use list_applyable.");
        }

        var now = time.GetUtcNow();

        // An application that has already claimed something must not be parked, and this is the
        // one refusal on this tool that is worth the round trip it costs. Parking sets columns on
        // whatever submission exists for the pair, so a park landing on a sent application would
        // do two things at once: make a sent application read as a posting nobody attempted, and
        // - for every reason but Expired and Duplicate - stop it counting as a live application,
        // which puts the posting back in the queue for a second application to the same vacancy.
        // Applying twice is worse than not applying at all and the recruiter sees both.
        var claimed = (await submissions.ListAsync(profileId.Value, now, ct))
            .FirstOrDefault(row => row.PostingId == postingId);

        if (claimed is { Status.Phase: not null })
        {
            return Refused(
                $"Submission {claimed.Id} for posting {postingId} already has events on it - it "
                + $"stands at '{claimed.Status.Phase}' - so this application was made rather than "
                + "blocked, and parking it would say the opposite. Append what actually happened "
                + "with record_event; if the application should not have been made, that is a "
                + "'Withdrawn' event, which keeps the posting out of the queue where a park would "
                + "hand it back for a second application to the same vacancy.");
        }

        OpenQuestionRow? question = null;
        var questionCreated = false;

        if (parsed is ParkReason.MissingAnswer)
        {
            // The question is opened before the posting is put down, and the order is the safe
            // one either way round: a park that failed after this leaves a question a person can
            // still answer, where a question that failed after the park would leave a posting
            // held for an answer nobody was ever asked for - except that the queue reads an
            // unanswered question rather than the park, so even that returns the posting next run
            // rather than losing it.
            //
            // Bounded by the repository rather than refused here, unlike record_form_answer. The
            // rule this tool runs under is that a park never fails: a run that cannot put a
            // posting down keeps meeting it, and a question shortened by forty characters is a
            // far smaller harm than a loop nobody recorded. The stored text is what the hash is
            // taken over, so the key still matches the wording on the row.
            (question, questionCreated) = await questions.OpenAsync(
                profileId.Value,
                questionText!,
                options is { Length: > 0 } ? options : null,
                sensitive || SensitiveQuestions.Looks(questionText),
                postingId,
                runId,
                now,
                ct);
        }

        var (row, created) = await submissions.ParkAsync(
            profileId.Value, postingId, parsed, now, target.ApplyUrl, runId, ct);

        var requeue = ParkReasonPolicy.Requeue(parsed);

        var notes = new List<string>
        {
            requeue switch
            {
                ParkRequeue.Never =>
                    "This posting will not be offered again: that reason means it is gone rather "
                    + "than blocked.",
                ParkRequeue.WhenAnswered =>
                    "It returns to list_applyable once the question is answered - "
                    + "record_form_answer with that wording closes it.",
                _ => "It returns to list_applyable on the next run, so there is nothing to retry "
                    + "now.",
            },
        };

        if (question is not null && !questionCreated)
        {
            notes.Add(
                $"That wording was already waiting as question {question.Id}, raised for another "
                + "advert, so one question stands for both. This posting is still parked on it.");
        }

        if (parsed is not ParkReason.MissingAnswer && !string.IsNullOrWhiteSpace(questionText))
        {
            notes.Add(
                "questionText was ignored: a question is only put to the candidate for "
                + "'MissingAnswer', because that is the only reason whose fix is an answer.");
        }

        return new
        {
            submissionId = row.Id,
            postingId = row.PostingId,
            title = row.Title,
            parked = row.IsParked,
            reason = row.ParkedReason?.ToString(),
            parkedAtUtc = row.ParkedAtUtc,

            // Whether this park brought the row into existence. A park on a posting already
            // applied to is a client that has lost track of itself rather than an error, and it
            // is visible here rather than swallowed.
            created,
            requeue = requeue.ToString(),
            questionId = question?.Id,
            questionCreated,
            note = string.Join(" ", notes),
        };
    }

    [McpServerTool(Name = "start_run")]
    [Description(
        "Opens an unattended pass over the applyable queue and returns its id. Pass that runId to "
        + "create_submission and park_application so everything the pass did is attributable to "
        + "it, and derive each event's idempotencyKey as '<runId>:<postingId>:<event type>' - "
        + "then a client that crashes and resumes converges on the same writes instead of "
        + "duplicating them. A run is attribution, not a lock: starting a second does not close "
        + "the first, nothing is reserved, and the daily cap is counted across the day rather "
        + "than per run. Call finish_run when the pass ends; a run nobody finishes is read as "
        + "abandoned after twelve hours and costs the account of what it did, never the work "
        + "itself.")]
    public async Task<object> StartRunAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        var run = await runs.StartAsync(profileId!.Value, time.GetUtcNow(), ct);

        return new
        {
            runId = run.Id,
            startedAtUtc = run.StartedAtUtc,

            // The convention spelled out rather than assumed. The server neither parses nor
            // validates a key, so a client that gets the shape wrong loses convergence quietly -
            // which is exactly why the shape is stated here rather than left to be guessed.
            idempotencyKeyFormat = $"{run.Id}:<postingId>:<event type>",
            note = "Pass runId to create_submission and park_application. Call finish_run with "
                + "what the pass did when it ends, including the postings it parked and why.",
        };
    }

    [McpServerTool(Name = "finish_run")]
    [Description(
        "Closes a run and stores its account of itself: how many postings it considered, how many "
        + "it recorded as sent, how many questions it had to put to a person, and one entry per "
        + "parked posting saying why. Send the parks as they happened, one entry each - the "
        + "tallying is done here so a client's own arithmetic cannot drift from the parks it "
        + "actually made. The answer carries 'unaccounted': considered minus sent minus parked, "
        + "which is the number that catches a run that dropped postings somewhere it did not "
        + "report. The first finish stands - a second call answers 'AlreadyFinished' and rewrites "
        + "nothing, because an account somebody may already have read must not be replaced by one "
        + "they cannot compare it to. Omitting every count says the run will not report, which is "
        + "not the same as reporting zeroes.")]
    public async Task<object> FinishRunAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The run to close, from start_run.")] long runId,
        [Description("How many postings the pass looked at.")] int? considered = null,
        [Description("How many applications it recorded as sent.")] int? submitted = null,
        [Description("How many questions it had to put to a person.")] int? questionsAsked = null,
        [Description("One ParkReason per parked posting, in any order - e.g. ['Captcha','Captcha','MissingAnswer'].")] string[]? parked = null,
        [Description("A sentence about the pass as a whole. Never a log.")] string? note = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        string[] reported = parked ?? [];

        var parks = new List<ParkReason>(reported.Length);

        foreach (var entry in reported)
        {
            if (!Enum.TryParse<ParkReason>(entry, ignoreCase: true, out var reason) || !Enum.IsDefined(reason))
            {
                // Refused whole rather than dropping the entry it could not read. A summary is
                // written once and never rewritten, so a silently shortened breakdown is a
                // permanent record of a run that parked fewer postings than it did.
                return Refused(
                    $"'{entry}' is not a park reason, and the summary is stored once and never "
                    + "rewritten - so nothing was recorded rather than a breakdown missing an "
                    + "entry. Expected one of: " + string.Join(", ", Enum.GetNames<ParkReason>()) + ".");
            }

            parks.Add(reason);
        }

        // Null and Empty are different answers and a reader must not fold them together: "looked
        // and found nothing" is a queue to go and fill, "would not say" is a client to go and
        // restart. A call that names no counts at all is the second.
        var summary = considered is null && submitted is null && questionsAsked is null && parks.Count == 0
            ? null
            : RunSummary.From(considered ?? 0, submitted ?? 0, questionsAsked ?? 0, parks);

        var now = time.GetUtcNow();
        var (run, outcome) = await runs.FinishAsync(profileId!.Value, runId, summary, note, now, ct);

        if (run is null)
        {
            return Refused(
                $"No run {runId} for this candidate. start_run returns the id to close, and a run "
                + "belongs to the candidate whose token opened it.");
        }

        // What the rows say, against what the run says. RunSummary.Submitted can be checked
        // against the submissions carrying this run's id; Considered can be checked against
        // nothing at all, which is why the pair is worth returning together.
        var recorded = await runs.CountSubmissionsAsync(profileId.Value, runId, ct);

        var notes = new List<string>();

        if (outcome is RunFinishResult.AlreadyFinished)
        {
            notes.Add(
                "This run was already finished, so nothing was rewritten and the account returned "
                + "is the first one. If the counts differ from what you just sent, the run was "
                + "closed by something else - do not try again.");
        }

        if (run.Summary is { } stored && stored.Unaccounted != 0)
        {
            notes.Add(
                $"{stored.Unaccounted} of the {stored.Considered} postings considered were "
                + "neither sent nor parked. That is a gap in the run's own account rather than a "
                + "figure to correct here: park what stopped a posting, so the next reader can "
                + "see why.");
        }

        return new
        {
            runId = run.Id,
            finished = outcome is RunFinishResult.Finished,
            result = outcome.ToString(),
            startedAtUtc = run.StartedAtUtc,
            finishedAtUtc = run.FinishedAtUtc,
            summary = run.Summary is null
                ? null
                : new
                {
                    considered = run.Summary.Considered,
                    submitted = run.Summary.Submitted,
                    questions = run.Summary.Questions,

                    // Summed from the breakdown rather than stored beside it: a stored total is a
                    // second copy of a fact the breakdown already carries, free to disagree.
                    parked = run.Summary.Parked,
                    parkedByReason = run.Summary.ParkedByReason
                        .ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
                    unaccounted = run.Summary.Unaccounted,
                },
            submissionsRecorded = recorded,
            note = notes.Count == 0 ? null : string.Join(" ", notes),
        };
    }

    [McpServerTool(Name = "match_email_to_submission")]
    [Description(
        "Which of this candidate's open applications a recruiter message is about, decided from "
        + "identifying tokens alone - when it arrived, the subject line, the name and DOMAIN the "
        + "sender gave itself, and any employer names read out of it. Send tokens, never the "
        + "message: there is nowhere here to put a body, and a pasted recruiter message is "
        + "somebody's name and direct line written into a database that is careful never to hold "
        + "one. This records NOTHING. It answers which application, and recording what the "
        + "message said is a separate record_event call. Abstention is a real answer and comes "
        + "back as 'NoCandidates', 'NoEvidence', 'NotConfident' or 'Ambiguous' with a ranked "
        + "shortlist to put in front of a person: two applications to one employer cannot be told "
        + "apart by anything a message carries. Act on 'Matched' only. A wrong match writes a "
        + "rejection onto the wrong application, in a log that has no eraser and where the event "
        + "recording it is itself true history.")]
    public async Task<object> MatchEmailToSubmissionAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("When the message arrived, UTC. Applications made after it are never matched.")] DateTimeOffset receivedAtUtc,
        [Description("The subject line, as it was written.")] string? subject = null,
        [Description("The name the sender gave itself - 'Acme Robotics Careers', 'Greenhouse on behalf of Acme'.")] string? senderDisplayName = null,
        [Description("The sending DOMAIN with no local part - 'greenhouse.io', not an address. An address is refused.")] string? senderDomain = null,
        [Description("Employer names read out of the message, in whatever spelling it used. Names only, never sentences.")] string[]? companyMentions = null,
        CancellationToken ct = default)
    {
        var (profileId, failure) = await ResolveAsync(context, ct);

        if (failure is not null)
        {
            return failure;
        }

        // Refused here rather than left to throw inside the matcher. Recruiter addresses are
        // discarded at parse time on purpose, and quietly trimming one down to its domain would
        // make this tool the route by which one came back.
        if (senderDomain?.Contains('@', StringComparison.Ordinal) == true)
        {
            return Refused(
                "senderDomain must be a domain and never an address - 'greenhouse.io', not "
                + "'no-reply@greenhouse.io'. Recruiter addresses are deliberately not stored "
                + "anywhere in this system, so send the domain and drop the rest.");
        }

        var now = time.GetUtcNow();
        var rows = await submissions.ListAsync(profileId!.Value, now, ct);

        var candidates = rows
            // The caller's shortlist, not the whole history. Handing the matcher every
            // application ever made invites an abstention it could have avoided: two
            // applications to one employer cannot be separated by anything a message carries, so
            // last year's rejection would make this year's interview invitation ambiguous.
            .Where(row => !row.IsParked && !row.Status.IsClosed)

            // A row with no employer name is dropped rather than passed with a blank one. An
            // empty name matches every message trivially, which would turn the strongest signal
            // the matcher has into one that fires on everything.
            .Where(row => !string.IsNullOrWhiteSpace(row.Company))
            .Select(row => new EmailMatchCandidate(
                row.Id,
                row.Company!,
                Host(row.ApplyUrl),
                // Read off the apply URL rather than stored. The matcher treats Unknown, Other
                // and Aggregator as naming no vendor, so an unrecognised host agrees with
                // nothing rather than with everything unrecognised.
                AtsVendorDetector.Detect(row.ApplyUrl).ToString(),
                row.CreatedAtUtc))
            .ToList();

        var match = EmailSubmissionMatcher.Match(
            new EmailIdentityTokens(
                receivedAtUtc,
                subject,
                senderDisplayName,
                senderDomain,
                // Derived from the domain rather than taken as an argument, so it cannot
                // disagree with the vendor each candidate is scored on: one detector, both sides.
                senderDomain is { Length: > 0 }
                    ? AtsVendorDetector.Detect($"https://{senderDomain.Trim()}/").ToString()
                    : null,
                companyMentions),
            candidates);

        return new
        {
            outcome = match.Outcome.ToString(),
            matched = !match.Abstained,

            // Null on every abstention, and that is the right way round: a caller that ignores
            // the outcome gets nothing to act on rather than a plausible wrong answer.
            submissionId = match.Match?.SubmissionId,
            confidence = match.Match?.Confidence,
            signals = match.Match?.Signals.Select(signal => signal.ToString()).ToList(),

            // Populated whatever the outcome, because it is what makes an abstention useful:
            // this is the shortlist to put in front of a person.
            ranked = match.Ranked.Select(scored => new
            {
                submissionId = scored.SubmissionId,
                confidence = scored.Confidence,
                signals = scored.Signals.Select(signal => signal.ToString()).ToList(),
            }).ToList(),
            candidatesConsidered = candidates.Count,
            note = match.Outcome switch
            {
                EmailMatchOutcome.Matched =>
                    "Nothing was recorded. If the message says something happened, append it with "
                    + "record_event against this submission.",
                EmailMatchOutcome.NoCandidates =>
                    "There is no open application this message could be about. Parked and closed "
                    + "applications are not considered, and neither is one recorded after the "
                    + "message arrived.",
                EmailMatchOutcome.NoEvidence =>
                    "Nothing in the message named any of these applications. Do not pick the "
                    + "likeliest: ask the candidate, or leave it.",
                EmailMatchOutcome.NotConfident =>
                    "Something pointed, but not enough to write on somebody's application. The "
                    + "ranked list is what to show a person.",
                _ => "Two or more applications fit this message equally well, which is what "
                    + "applying twice to one employer looks like from the outside. Ask rather "
                    + "than choosing.",
            },
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
    ///
    /// <b>It decides who a caller is and never what a write claims.</b> Every event and every
    /// answer written through this surface is <c>Client</c> whichever answer this gives - a
    /// delegated token means a person started the client, not that they typed the sentence - and
    /// <c>Candidate</c> is reachable only from the dashboard. What this changes is the audit: an
    /// unmapped app-only token gets its own refusal, and a disclosure records the principal and
    /// the candidate separately.
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
    /// of the system - no profile, a name outside the allowlist, a posting nobody matched, a
    /// scope this surface will not write - and a protocol-level error invites a retry where a
    /// sentence invites a different action. Every reason says what to do instead, because the
    /// reader is a model that will otherwise guess again.
    /// </remarks>
    private static object Refused(string reason) => new { refused = true, reason };

    /// <summary>
    /// What was captured while a claim was made, or nothing where nothing was.
    /// </summary>
    /// <remarks>
    /// <b>One function for both write paths</b>, so an event inlined into a create and one
    /// appended later carry evidence built the same way - the difference would otherwise show up
    /// only in the rows, months later.
    ///
    /// <b>Null where the block is empty, asked through <c>IsEmpty</c> rather than by a null check
    /// per argument.</b> Blank counts as nothing there: a selector that matched an empty element
    /// yields <c>""</c> and a list of blanks is what enumerating a half-rendered page produces,
    /// so a plain null check would hang a block of nulls off every event and put proof on the
    /// dashboard that does not exist.
    ///
    /// Nothing here refuses. The evidence is gathered by something driving a browser through
    /// somebody else's form, the interesting runs are the ones that go wrong, and refusing to
    /// record that an application was sent because the screenshot failed loses the fact in order
    /// to protect the proof of it. The repository bounds each part to its column.
    /// </remarks>
    private static SubmissionEvidence? Captured(
        string? confirmationRef, string? finalUrl, string? screenshotRef, string[]? submittedFields)
    {
        var evidence = new SubmissionEvidence
        {
            ConfirmationRef = confirmationRef,
            FinalUrl = finalUrl,
            ScreenshotRef = screenshotRef,
            SubmittedFields = submittedFields is { Length: > 0 } ? submittedFields : null,
        };

        return evidence.IsEmpty ? null : evidence;
    }

    /// <summary>
    /// The host an apply URL points at, or null where there is nothing to read.
    /// </summary>
    /// <remarks>
    /// A host and never a whole URL, because that is what the matcher compares: a path carries a
    /// job id, and two applications to one employer would stop agreeing on the one fact that
    /// should make them agree. Anything unparseable is null rather than an exception - the stored
    /// value is a string a scraper lifted off somebody's page.
    /// </remarks>
    private static string? Host(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var address) ? address.Host : null;

    /// <summary>
    /// Reads an optional enum argument, where absent and unrecognised are different answers.
    /// </summary>
    /// <remarks>
    /// <b>Blank is "no filter" and anything else must be a member.</b> Written once rather than
    /// five times: a channel, an ordering, an apply-URL provenance and a scope all arrive as
    /// strings a model chose, and each of the five copies this replaces was a place where a
    /// silent <c>default(TEnum)</c> would have turned a typo into a filter nobody asked for -
    /// which for <see cref="ApplyUrlSource"/> would mean quietly restricting the queue to board
    /// pages.
    ///
    /// <c>Enum.IsDefined</c> as well as <c>TryParse</c>, because <c>TryParse</c> happily accepts
    /// "7" and any digits a model might send for a member it half-remembers.
    /// </remarks>
    private static bool TryParseOptional<TEnum>(string? value, out TEnum? parsed)
        where TEnum : struct, Enum
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var read) || !Enum.IsDefined(read))
        {
            return false;
        }

        parsed = read;

        return true;
    }
}
