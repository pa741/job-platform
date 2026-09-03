using JobPlatform.Core.Applications;
using JobPlatform.Core.Dedup;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// What one night of application writing is allowed to cost.
/// </summary>
/// <remarks>
/// <b>Configurable because it is the only number in this file that is a bill.</b> Every other
/// bound here protects a request or a provider; this one decides how many calls go to the
/// deployment priced roughly twenty-five times the bulk one. A constant would mean the answer to
/// "this is spending too much" is a deploy, and the answer to "the loop is starved" is also a
/// deploy - so it is settings, read the way <c>AiLedgerOptions</c> and <c>CosmosOptions</c> are.
///
/// <b>Both members default to something a clone can run unattended</b>, so a deployment that
/// binds nothing still gets a bounded pass rather than an unbounded one or a dead one.
/// </remarks>
public sealed class ApplicationGenerationOptions
{
    /// <summary>Configuration section: <c>Applications:Generation</c>.</summary>
    public const string SectionName = "Applications:Generation";

    /// <summary>
    /// How many drafts one nightly pass writes for one candidate.
    /// </summary>
    /// <remarks>
    /// <b>Ten, and the ceiling above it is not arbitrary either.</b>
    /// <see cref="SubmissionLimits.MaxSubmittedPerDay"/> is how many applications may be recorded
    /// as sent in a UTC day, so a pass that wrote thirty would be buying twenty-five days' worth
    /// of Sol calls for a queue that can only spend a day's - and the corpus is re-scraped
    /// nightly, so several of those documents would be tailored to adverts nothing will ever apply
    /// to. <see cref="GenerateApplicationsFunction"/> clamps to that ceiling rather than trusting
    /// this value, for the reason the daily cap lives in the repository and not at its call sites.
    ///
    /// <b>Per candidate rather than per run</b>, matching <c>MatchSweepFunction.MaxAssessments</c>:
    /// a second profile must not go without documents because the first one filled the batch. What
    /// bounds the run as a whole is the wall clock, which is the bound that actually holds when
    /// the number of profiles is not something this file controls.
    /// </remarks>
    public int DocumentsPerNight { get; set; } = 10;

    /// <summary>
    /// The assessment floor this pass generates above.
    /// </summary>
    /// <remarks>
    /// <b>It must be the floor the run pulls with, and that is the whole of its justification.</b>
    /// Set higher than the run's and the queue offers postings whose documents were never written,
    /// which is the state this pass exists to end; set lower and the pass buys drafts for postings
    /// the run will not look at. Eighty is what <c>list_applyable</c> is asked for by the apply
    /// loop, so eighty is what is written for.
    ///
    /// <b>This is a fourth number and not a fourth spelling of an existing one.</b>
    /// <c>MatchRanker.FusionFloor</c> is where the embedding earns its weight,
    /// <c>MatchSweepFunction.AssessmentThreshold</c> is where buying a judgement is worth it, and
    /// <c>ListApplyableAsync</c>'s <c>Verdict >= Possible</c> is what "worth applying to" means.
    /// This one asks where writing a document is worth the expensive deployment. Two of those were
    /// briefly collapsed into one constant on the grounds that they shared a value, and that was a
    /// mistake; this one is configuration precisely so nobody is tempted to reach for one of the
    /// three because it happens to read 80 today.
    /// </remarks>
    public int MinAssessmentScore { get; set; } = 80;
}

/// <summary>
/// Writes the tailored documents for the postings the apply loop is about to reach, before it
/// reaches them.
/// </summary>
/// <remarks>
/// <b>This is the pass the loop was actually blocked on.</b> Measured on the live database on
/// 2026-09-02, <c>ApplicationDocuments</c> held exactly one row for the entire system - so the
/// queue an unattended run pulls, gated on documents existing and on an employer apply link and on
/// an assessment of 80, returned nothing at all. Every tool on the agent surface worked and there
/// was nothing for any of them to do. Documents were only ever written when a person pressed
/// generate on the dashboard, and a person pressing generate forty times is the work the loop was
/// built to remove.
///
/// <b>It is a timer for the same reason the sweep is.</b> A document costs tens of seconds and a
/// call on the writing deployment, so it cannot happen when a page is opened or when a tool is
/// called; and it wants the night's verdicts, which the sweep writes at 03:30. By the time a run
/// starts, the drafts, the drafted free text and - where a pack store is configured - the rendered
/// PDF and DOCX are already there, and the run's first action is a query rather than a wait.
///
/// <b>The set it writes for is <c>ApplyableQuery</c>'s and never its own.</b> Reusing the queue
/// repository is the single most important decision in this file: a second definition of "worth
/// applying to" would be a second thing to keep in step with parking, dismissal, cross-board
/// deduplication and the verdict gate, and the first time the two drifted this pass would write
/// documents for postings the run never sees while the ones it does see stay empty - which is
/// exactly today's failure with the effort spent. The filters are the ones a run passes, the
/// ordering is the one a run gets, and the floor is settings so the two can be kept equal without
/// a deploy.
///
/// <b>Bounded hard, and the bound is the point.</b> A runaway pass here is not a slow night, it is
/// a bill on the one deployment this architecture deliberately keeps off a schedule - so there is
/// a cap per candidate, a clamp above that cap, a wall-clock budget, and a stop after three
/// consecutive failures. It is resumable from the database rather than from a token: a posting
/// with a document row is not in the queue this pass reads, so a run cut short by the budget
/// resumes rather than restarts, exactly as the embedding pass does.
///
/// <b>Degraded rather than broken wherever a capability is absent.</b> No AI provider registers no
/// <c>IApplicationWriter</c> and the pass reports how far behind the corpus is instead of writing;
/// no pack store registers nothing and the markdown is stored with null paths, which is the state
/// <c>get_submission_pack</c> already answers for. Neither is an error, and neither takes the
/// ingest down with it.
/// </remarks>
public sealed class GenerateApplicationsFunction(
    JobsDbContext db,
    CandidateProfileRepository profiles,
    JobMatchRepository matches,
    ApplicationDocumentRepository documents,
    IOptions<ApplicationGenerationOptions> options,
    TimeProvider time,
    ILogger<GenerateApplicationsFunction> logger,
    IApplicationWriter? writer = null,
    IApplicationPackStore? packs = null)
{
    /// <summary>
    /// How many queue rows are read for each document the budget allows.
    /// </summary>
    /// <remarks>
    /// <b>An over-read, for the reason <c>ListApplyableAsync</c> over-reads for its clusters.</b>
    /// Two of this pass's rules cannot be expressed in the query - an aggregator is read off the
    /// URL by a static call with no SQL, and an advert with no body is a fact about a column the
    /// queue deliberately never projects - so both are applied after materialisation, and a filter
    /// applied after a bound silently shrinks it. Reading three times the budget means a head full
    /// of WhatJobs links does not leave the night's cap unspent with good postings sitting behind
    /// them.
    ///
    /// <b>It is a window and not a guarantee</b>, and that is said here rather than papered over:
    /// a queue whose first thirty rows are all aggregators writes fewer than ten documents. The
    /// alternative - re-querying with a widened page the way the embedding pass does - buys very
    /// little here, because a skipped row is skipped permanently rather than retried, so the next
    /// night's page starts in the same place with the same rows to step over.
    /// </remarks>
    private const int CandidateWindow = 3;

    /// <summary>
    /// How many writing calls may come back with nothing before the pass concludes the provider
    /// is down rather than merely unlucky.
    /// </summary>
    /// <remarks>
    /// Three, and the argument is <c>EmbedCorpusFunction.MaxConsecutiveFailures</c>'s: one failure
    /// cannot tell a provider that is gone from one having a bad minute, and a freshly created
    /// deployment answering 404 for one call in three has already happened here once. Consecutive
    /// is load-bearing - each attempt is a different posting, so three in a row is three
    /// independent samples of the provider rather than one advert asked about three times.
    ///
    /// <b>Stopping is worth more on this pass than on that one.</b> An embedding call is priced
    /// two orders of magnitude below a chat call; a writing call is the most expensive thing this
    /// system does, and a provider returning malformed drafts would otherwise burn the whole
    /// night's budget producing nothing storable.
    ///
    /// It stops the run and not merely the candidate. A provider that is down is down for
    /// everybody, and carrying on to the next profile would spend three more calls establishing
    /// the same fact.
    /// </remarks>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>Wall-clock budget for the nightly pass.</summary>
    /// <remarks>
    /// Comfortably inside Flex Consumption's 30-minute default, and sized against the writing
    /// call's own 180-second ceiling rather than against the batch: ten documents that each take
    /// their full timeout do not fit, and the ones that do not fit are simply written tomorrow
    /// from the same queue. A budget that assumed the happy path would be a budget the host
    /// enforces instead, by killing the invocation mid-write.
    /// </remarks>
    private static readonly TimeSpan TimerBudget = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How many documents one HTTP invocation may write.
    /// </summary>
    /// <remarks>
    /// <b>One, because a writing call is allowed 180 seconds and the gateway allows about 230.</b>
    /// Two would be four hundred seconds on the slow path, and the reprocess endpoint has already
    /// established what happens then: a 504 that carries nothing back, so the caller loses its
    /// place. Here losing a place costs only the draft in flight - the queue is the resume point
    /// and a posting with no document row is still in it - but paying for a call whose answer is
    /// discarded at the gateway is exactly the waste this pass is bounded to avoid.
    ///
    /// So the route is a nudge rather than a batch, the way <c>run-embed-corpus</c> is: calling it
    /// repeatedly is how a first set gets written by hand before 03:30 has ever run.
    /// </remarks>
    private const int MaxDocumentsPerRequest = 1;

    /// <summary>Wall-clock budget for the HTTP route, with margin under the gateway's ~230s.</summary>
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(200);

    [Function(nameof(GenerateApplicationsFunction))]
    public Task RunAsync(
        // 04:30 UTC: an hour after the sweep starts, so this morning's verdicts are written and
        // the postings judged overnight are in the queue this reads, and half an hour after the
        // curated export, so the two long passes do not share an instance. Still hours before
        // anybody in the UK opens the dashboard or an unattended run starts.
        //
        // The ordering is not enforced by anything and does not need to be: a posting the sweep
        // has not judged is not in this queue at all, and the next night picks it up.
        [TimerTrigger("0 30 4 * * *")] TimerInfo timer,
        CancellationToken ct)
        => RunNightlyAsync(ct);

    /// <summary>
    /// The nightly pass, separated from the trigger that fires it.
    /// </summary>
    /// <remarks>
    /// <b>The trigger returns nothing because a Functions return value is an output binding</b>,
    /// and this pass has no output binding to write to - it writes rows. The summary is still the
    /// only honest account of what a night cost, so the work is a method the trigger calls rather
    /// than the trigger itself, and what the log line reports is the same object a test can read.
    /// </remarks>
    public Task<GenerationSummary> RunNightlyAsync(CancellationToken ct = default)
        => GenerateAsync(
            profileId: null,
            NightlyLimit(options.Value.DocumentsPerNight),
            options.Value.MinAssessmentScore,
            TimerBudget,
            ct);

    /// <summary>
    /// The same pass, on demand.
    /// </summary>
    /// <remarks>
    /// Exists for the case the timer cannot serve, exactly as <c>run-match-sweep</c> and
    /// <c>run-embed-corpus</c> do: a queue that has never had a document written for it, and
    /// somebody who would rather not wait until tomorrow morning to find out whether the loop
    /// runs. An admin endpoint rather than a user-facing one, because it is the path that spends
    /// money and a route a client can call is a route a client can call repeatedly.
    ///
    /// Follows <c>ReprocessBlobFunction</c>: ASP.NET Core integration types because the host is
    /// built with <c>ConfigureFunctionsWebApplication</c>, and no <c>admin/</c> route prefix
    /// because the host reserves it and claiming it fails as a 404 rather than as an error.
    /// </remarks>
    [Function(nameof(RunGenerateApplicationsFunction))]
    public async Task<IActionResult> RunGenerateApplicationsFunction(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "run-generate-applications")]
        HttpRequest request,
        CancellationToken ct)
    {
        var body = await RequestBody.ReadAsync<GenerateRequest>(request, ct);

        var summary = await GenerateAsync(
            body?.ProfileId,
            // Clamped rather than defaulted from the options, because the bound here is the
            // gateway's and not the budget's: a caller asking for ten over HTTP is asking for a
            // 504, and answering with one document written is more use than answering with none.
            Math.Clamp(body?.Limit ?? MaxDocumentsPerRequest, 0, MaxDocumentsPerRequest),
            Math.Clamp(body?.MinAssessmentScore ?? options.Value.MinAssessmentScore, 0, 100),
            RequestBudget,
            ct);

        return new OkObjectResult(summary);
    }

    /// <param name="ProfileId">Restrict to one candidate. Null writes for every profile.</param>
    /// <param name="Limit">
    /// Documents to write, bounded by <see cref="MaxDocumentsPerRequest"/> whatever is asked for.
    /// </param>
    /// <param name="MinAssessmentScore">
    /// Floor on which pairs are written for. Defaults to the configured one.
    /// </param>
    /// <remarks>
    /// The floor is exposed here for one case: a queue that returns nothing at 80 and somebody
    /// establishing whether that is because the corpus is thin or because the pass is broken. It
    /// is deliberately not a way to run the whole corpus cheaply - the limit still applies, and a
    /// floor of zero writes one document for whatever the queue ranks first.
    /// </remarks>
    public sealed record GenerateRequest(long? ProfileId, int? Limit = null, int? MinAssessmentScore = null);

    /// <summary>
    /// The nightly cap, clamped to what a day could ever send.
    /// </summary>
    /// <remarks>
    /// Clamped here rather than trusted from settings, for the reason
    /// <c>SubmissionLimits.MaxSubmittedPerDay</c> is enforced in the repository and not at its
    /// call sites: a misconfigured value is a bill, and the number of documents worth writing in a
    /// night cannot exceed the number of applications that could be recorded as sent that day.
    /// A negative or absent value means the pass writes nothing, which is a legitimate way to
    /// switch it off without redeploying.
    /// </remarks>
    private static int NightlyLimit(int configured)
        => Math.Clamp(configured, 0, SubmissionLimits.MaxSubmittedPerDay);

    private async Task<GenerationSummary> GenerateAsync(
        long? profileId, int limit, int floor, TimeSpan budget, CancellationToken ct)
    {
        if (limit <= 0)
        {
            logger.LogInformation("Application generation: the cap is {Limit}; nothing to do.", limit);
            return GenerationSummary.Empty;
        }

        var profileIds = profileId is { } single
            ? [single]
            : await matches.GetProfileIdsAsync(ct);

        if (profileIds.Count == 0)
        {
            logger.LogInformation("Application generation: no profiles to write for.");
            return GenerationSummary.Empty;
        }

        if (writer is null)
        {
            // Not an error, and not silent either. A deployment with no provider is a shape this
            // system ships in, but "no documents exist" is also what a broken pass looks like, so
            // the log line says which - and says how many postings are waiting, because that
            // number is the whole argument for configuring a provider.
            var waiting = await WaitingAsync(profileIds, floor, ct);

            logger.LogInformation(
                "Application generation: no AI provider is configured, so no documents can be "
                + "written. {Waiting} posting(s) across {Profiles} profile(s) are in the apply "
                + "queue at {Floor}+ with nothing to send.",
                waiting, profileIds.Count, floor);

            return GenerationSummary.Empty with { Profiles = profileIds.Count, Waiting = waiting };
        }

        var started = time.GetTimestamp();
        var tally = GenerationSummary.Empty with { Profiles = profileIds.Count };

        foreach (var id in profileIds)
        {
            if (time.GetElapsedTime(started) >= budget)
            {
                break;
            }

            var written = await GenerateForProfileAsync(id, limit, floor, started, budget, ct);

            tally = Fold(tally, written);

            // A provider that is down is down for every candidate, so the run stops rather than
            // spending three more calls per profile learning the same thing.
            if (written.Aborted)
            {
                break;
            }
        }

        tally = tally with { Waiting = await WaitingAsync(profileIds, floor, ct) };

        // Considered beside written, and what is still waiting beside both. A written count on its
        // own cannot show a night that skipped everything it read, and neither count can answer
        // the question somebody actually has when a run finds an empty queue.
        logger.LogInformation(
            "Application generation complete: {Written} draft(s) written of {Considered} "
            + "considered, {Rendered} rendered, {Skipped} skipped, {Failed} failed. {Waiting} "
            + "posting(s) still have no documents at {Floor}+.",
            tally.Written, tally.Considered, tally.Rendered, tally.Skipped, tally.Failed,
            tally.Waiting, floor);

        return tally;
    }

    /// <summary>
    /// One candidate's batch: the queue's own top rows, written for in the queue's own order.
    /// </summary>
    /// <remarks>
    /// <b>The profile is read once for the whole batch and the advert once per posting.</b> The
    /// writer needs the candidate's entire record - it is reproducing employment history rather
    /// than summarising it - and that record does not change between two postings on the same
    /// night, where the advert does. Re-reading the profile graph per document would be several
    /// round trips per draft against a database billed on wall-clock time.
    ///
    /// <b>Skips are counted and never silent.</b> All three are ordinary - an aggregator behind a
    /// "direct" link, an advert the scraper never read the body of, and a match that disappeared
    /// between the queue and the write - and each is a posting this pass will never write for,
    /// which is a different fact from one it has not reached yet. Counting them is what keeps
    /// "the queue is empty" and "the queue is full of things we step over" distinguishable.
    /// </remarks>
    private async Task<GenerationTally> GenerateForProfileAsync(
        long profileId, int limit, int floor, long started, TimeSpan budget, CancellationToken ct)
    {
        var subjectId = await db.CandidateProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => p.SubjectId)
            .FirstOrDefaultAsync(ct);

        if (subjectId is null)
        {
            return GenerationTally.Empty;
        }

        var view = await profiles.GetAsync(subjectId, ct);

        if (view is null)
        {
            return GenerationTally.Empty;
        }

        // The queue an unattended run pulls, asked the complementary question: not "what can I
        // apply to" but "what would I be able to apply to if the documents existed". Every other
        // clause - the verdict gate, dismissal, a live application here or on another listing of
        // the same job, a permanent park, a park waiting on an answer - is the repository's, and
        // deliberately not restated here.
        var queue = await matches.ListApplyableAsync(
            profileId,
            new ApplyableQuery
            {
                // The definition of "already has documents", and the resume point. Nothing is
                // flagged and nothing tracks attempts: the presence of a row is what "done" means,
                // the way PostingExtractions' unique key is what "applied" means for an
                // extraction. A regeneration is the dashboard's business, not this pass's - it
                // would spend the expensive deployment producing the document that is already
                // there.
                DocumentsReady = false,

                // A link the board published as the employer's own. The other two provenances are
                // an inference and a board page; writing a tailored CV for a posting whose apply
                // route is "open the job board and look" spends the writing deployment on a
                // document nothing can attach.
                ApplyUrlSource = ApplyUrlSource.Posting,
                MinAssessmentScore = floor,

                // Left at the default deliberately. This is the ordering a run gets, and writing
                // in a different order would mean the documents exist for the postings the run
                // reaches last.
                Sort = ApplyableSort.Rank,
                Limit = limit * CandidateWindow,
            },
            ct);

        if (queue.Count == 0)
        {
            return GenerationTally.Empty;
        }

        var postings = await DescribeAsync(queue, ct);
        var tally = GenerationTally.Empty;
        var consecutiveFailures = 0;

        foreach (var row in queue)
        {
            if (tally.Written >= limit || time.GetElapsedTime(started) >= budget)
            {
                break;
            }

            var posted = postings.GetValueOrDefault(row.PostingId);

            // Two skips, both decided before a round trip is spent on the posting. An aggregator
            // behind a "direct" link has not reached an employer, and a CV tailored for one would
            // be spent arriving at a second search results page - the skip AtsVendor.Aggregator
            // exists for, applied where the money is rather than only where the tab would open.
            // An advert whose body the scraper never read is worse than useless: a document
            // written against it is a generic CV bought at the tailored price, and it would then
            // satisfy `DocumentsReady` and take the posting out of this pass's queue for good.
            if (!row.AtsVendor.IsEmployerAts() || !posted.HasAdvert)
            {
                tally = tally with { Skipped = tally.Skipped + 1 };
                continue;
            }

            var context = await matches.GetForWritingAsync(profileId, row.PostingId, ct);

            if (context is not { } pair || string.IsNullOrWhiteSpace(pair.Posting.Text))
            {
                // The match went away between the queue and the write. Rare, and the same answer
                // as a skip: there is no gap list to write against, and writing without one is
                // what generation is refused for on the dashboard too.
                tally = tally with { Skipped = tally.Skipped + 1 };
                continue;
            }

            tally = tally with { Considered = tally.Considered + 1 };

            var (match, assessment, posting) = pair;

            // No instructions. Those are what a candidate types into the dashboard to steer one
            // draft, and there is nobody at the keyboard at half past four in the morning -
            // sending a stored default instead would be this pass asserting a preference nobody
            // expressed.
            var draft = await writer!.WriteAsync(
                new ApplicationRequest(view.Profile, posting, match, assessment), ct);

            if (draft is null)
            {
                consecutiveFailures++;
                tally = tally with { Failed = tally.Failed + 1 };

                // The writer has already recorded why in the AI ledger and has already timed out
                // or retried. What is left to decide is whether this is one bad answer or a
                // provider that is gone, and the difference is worth an early stop here because
                // every further attempt is the most expensive call this system makes.
                logger.LogWarning(
                    "Application generation: the writer returned no usable draft for posting "
                    + "{PostingId} ({Failures} in a row). The AI ledger carries why.",
                    row.PostingId, consecutiveFailures);

                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    logger.LogError(
                        "Application generation: {Failures} consecutive writing calls returned "
                        + "nothing; stopping. {Written} draft(s) written this run.",
                        consecutiveFailures, tally.Written);

                    return tally with { Aborted = true };
                }

                continue;
            }

            consecutiveFailures = 0;

            var stored = await documents.AddAsync(
                profileId,
                row.PostingId,
                draft,
                instructions: null,
                Drafted(draft, posted.Site),
                time.GetUtcNow(),
                ct);

            tally = tally with { Written = tally.Written + 1 };

            if (await RenderAsync(view.Id, view.Profile.FullName, stored, ct))
            {
                tally = tally with { Rendered = tally.Rendered + 1 };
            }
        }

        return tally;
    }

    /// <summary>
    /// The free-text answers stored alongside this draft.
    /// </summary>
    /// <remarks>
    /// <b>Written in the same call as the documents, which is the only time some of them can be
    /// written at all.</b> <c>ApplicationDocumentRepository.AddAsync</c> takes them beside the
    /// draft rather than through a setter of its own: they are assertions made in the voice of
    /// this revision's CV, and a second write would leave a window in which a revision exists
    /// whose answers do not. Drafting at generation time is also what makes the posting-specific
    /// half affordable - the advert, the profile, the gap list and the emphasise list are already
    /// in the writer's prompt, so answers come out of tokens already paid for rather than out of a
    /// second call.
    ///
    /// <b>Two halves from two places, and neither could produce the other.</b>
    /// <c>DraftedAnswerCatalog.StableAnswers</c> needs no model at all - it names the board the
    /// posting actually came from, which is a stored fact - while the posting-specific prose can
    /// only come back from the writing call, whose prompt already holds the advert, the profile
    /// and the gap list. Merging them here rather than asking for either twice is what makes the
    /// persuasive half affordable: it costs output tokens on a call already being made.
    ///
    /// <b>The writer's half wins on a collision.</b> A canned answer exists because the question
    /// is the same whatever the posting; if the writer has answered one of those, it read the
    /// advert and the canned value did not.
    ///
    /// <b>The board is passed rather than left to default.</b> A referral answer is a statement
    /// somebody signs: writing "LinkedIn" on an advert found on Indeed is a small lie in a
    /// document with the candidate's name on it. A board this build cannot spell yields no canned
    /// answer at all, which the catalogue decides rather than this pass.
    /// </remarks>
    private static IReadOnlyList<DraftedAnswer> Drafted(ApplicationDraft draft, string? sourceBoard)
    {
        var drafted = draft.DraftedAnswers;

        return
        [
            .. drafted,
            .. DraftedAnswerCatalog.StableAnswers(sourceBoard)
                .Where(stable => !drafted.Any(written => string.Equals(
                    written.QuestionText, stable.QuestionText, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>
    /// The two facts about a queued posting that the queue row deliberately does not carry.
    /// </summary>
    /// <param name="Site">
    /// The board it came from, in the scraper's own spelling, because a referral answer names it.
    /// </param>
    /// <param name="HasAdvert">
    /// Whether the scraper read the advert body. <b>Asked as a boolean rather than answered by
    /// fetching the text</b>: this runs over a page of queue rows, and the description is the
    /// unbounded column every read in this codebase is written to avoid pulling in bulk.
    /// </param>
    /// <remarks>
    /// The default - no board, no advert - is what an id missing from the read maps to, and it is
    /// the safe direction: a posting that vanished between the queue and this read is stepped over
    /// rather than written for against nothing.
    /// </remarks>
    private readonly record struct QueuedPosting(string? Site, bool HasAdvert);

    /// <summary>
    /// One read describing every posting on the page.
    /// </summary>
    /// <remarks>
    /// <b>Here rather than as columns on <c>ApplyableRow</c>.</b> The queue row is deliberately
    /// narrow - it is a work queue and not a browse response - and these two facts are wanted by
    /// one caller, so widening a projection that every run reads would be the wrong trade. Asked
    /// posting by posting they would be a round trip each against a database billed on wall-clock
    /// time, which is the cost <c>GetAvailabilityAsync</c> exists to avoid on the other side of
    /// the same question.
    ///
    /// <b>It is also what makes the two in-memory skips cheap.</b> Deciding them from this
    /// dictionary means an aggregator or a bodyless advert costs nothing at all, where discovering
    /// the second from <c>GetForWritingAsync</c> would spend a query - and that is the one query
    /// here which pulls a whole description across.
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, QueuedPosting>> DescribeAsync(
        IReadOnlyList<ApplyableRow> queue, CancellationToken ct)
    {
        long[] ids = [.. queue.Select(row => row.PostingId).Distinct()];

        var rows = await db.JobPostings
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Site,
                HasAdvert = p.Description != null && p.Description.Trim() != "",
            })
            .ToListAsync(ct);

        return rows.ToDictionary(row => row.Id, row => new QueuedPosting(row.Site, row.HasAdvert));
    }

    /// <summary>
    /// Renders this draft, stores what rendered, and records where it went. False where nothing
    /// was stored.
    /// </summary>
    /// <remarks>
    /// <b>The same three steps <c>ApplicationEndpoints.RenderAsync</c> takes, written out a second
    /// time, and that is a known duplication rather than an oversight.</b> The shared halves are
    /// already shared and they are the halves that would actually hurt if they drifted:
    /// <c>ApplicationPackFile</c> owns the filename and the stored path on both sides, and
    /// <c>IApplicationPackStore</c> owns the upload. What is repeated is the glue - pick the
    /// markdown, render it, hand it over, write the references back - and the API's copy is a
    /// private method on an endpoint group, so sharing it means moving code in a file this pass
    /// does not own. Worth doing; not worth doing from here.
    ///
    /// <b>Every step fails on its own and none of them fails the pass.</b> The draft is stored
    /// before any of this runs, so a renderer that throws on a construct the AST maps onto
    /// nothing, or a role assignment that has not finished propagating, costs a re-render rather
    /// than a regeneration - the expensive half is already safe. <c>RenderedDocuments</c> reads a
    /// null member as "nothing to say about this file" and never as "clear the one on the row",
    /// which is what makes recording a partial result safe to repeat.
    ///
    /// <b>No pack store means no files and null paths, which is a capability this deployment does
    /// not have rather than a dependency it is missing.</b> The Functions host registers none
    /// today, so a nightly pass writes the markdown, the drafted answers and nothing else - and
    /// <c>get_submission_pack</c> already answers "no file is available" for exactly that state.
    /// </remarks>
    private async Task<bool> RenderAsync(
        long profileId, string? candidateName, StoredApplication stored, CancellationToken ct)
    {
        if (packs is null)
        {
            return false;
        }

        var cvPdf = await StoreAsync(profileId, candidateName, stored, PackDocument.CurriculumVitae, PackFormat.Pdf, ct);
        var cvDocx = await StoreAsync(profileId, candidateName, stored, PackDocument.CurriculumVitae, PackFormat.Docx, ct);
        var letterPdf = await StoreAsync(profileId, candidateName, stored, PackDocument.CoverLetter, PackFormat.Pdf, ct);

        var rendered = new RenderedDocuments
        {
            CvBlobPath = cvPdf?.BlobPath,
            CvDocxBlobPath = cvDocx?.BlobPath,
            CoverLetterBlobPath = letterPdf?.BlobPath,

            // Paired with the PDF and with nothing else. The hash sits beside CvBlobPath and
            // describes the bytes at it; carrying the DOCX's hash there when the PDF had failed
            // would leave a row asserting that the file at a path it does not have hashes to
            // something.
            CvSha256 = cvPdf?.Sha256,
        };

        if (rendered.IsEmpty)
        {
            return false;
        }

        try
        {
            await documents.RecordRenderedAsync(profileId, stored.Id, rendered, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Error rather than warning, and the only line in this path that is. The one above
            // reports a file that was never made; this reports files that exist, were paid for and
            // now have nothing pointing at them.
            logger.LogError(
                ex,
                "Rendered files for draft {DocumentId} were stored but could not be recorded "
                + "against it. The blobs exist and no row references them.",
                stored.Id);

            return false;
        }
    }

    /// <summary>Renders one document in one format and uploads it. Null where either half did not happen.</summary>
    /// <remarks>
    /// Only the render is wrapped, because only the render can throw:
    /// <c>IApplicationPackStore</c> answers null for every storage failure by contract. The
    /// failures a renderer has - a markdown construct the AST maps onto nothing, an OOXML part the
    /// SDK refused - are exactly the ones model output is most likely to produce and least likely
    /// to have been tested against, and one of them must not lose the other two files.
    ///
    /// The title is the document's own metadata rather than its filename: it is what a PDF reader
    /// puts in a window title. The filename is <c>ApplicationPackFile</c>'s, built from the
    /// candidate's name, because that is what a recruiter reads in a list of forty.
    /// </remarks>
    private async Task<StoredPackFile?> StoreAsync(
        long profileId,
        string? candidateName,
        StoredApplication stored,
        PackDocument document,
        PackFormat format,
        CancellationToken ct)
    {
        var markdown = document == PackDocument.CoverLetter
            ? stored.CoverLetterMarkdown
            : stored.CurriculumVitaeMarkdown;

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var kind = document == PackDocument.CoverLetter ? "Cover letter" : "CV";
        byte[] content;

        try
        {
            content = format == PackFormat.Docx
                ? MarkdownDocxRenderer.Render(markdown, $"{kind} - {stored.PostingTitle}")
                : MarkdownPdfRenderer.Render(markdown, $"{kind} - {stored.PostingTitle}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The draft id rather than the markdown. This line is read by somebody deciding
            // whether a renderer has a bug, and the markdown is a tailored CV that does not
            // belong in a log at all.
            logger.LogWarning(
                ex,
                "Could not render the {Kind} of draft {DocumentId} as {Format}. The draft is "
                + "saved and the markdown is the record.",
                kind, stored.Id, format);

            return null;
        }

        return await packs!.StoreAsync(
            new PackFileRequest
            {
                ProfileId = profileId,
                DocumentId = stored.Id,
                Document = document,
                Format = format,
                Content = content,
                CandidateName = candidateName,
            },
            ct);
    }

    /// <summary>
    /// How many postings across these profiles are applyable and have nothing to send.
    /// </summary>
    /// <remarks>
    /// <b>Reported rather than inferred, because it is the number that says whether this pass is
    /// keeping up.</b> "Wrote ten drafts" is the same log line on a night that cleared the backlog
    /// and on one that took ten off a queue of four hundred, and the difference decides whether
    /// the cap is right. It is also the figure that made this pass exist: one document in the
    /// whole database against a queue that was not empty.
    ///
    /// <b>It counts what this pass would write for rather than what the query returned</b>, and
    /// that is the difference between a backlog that reaches zero and one that never does: an
    /// aggregator link and an advert with no body are stepped over on every run for ever, so
    /// counting them would put a permanent floor under the figure and make a night that has
    /// caught up indistinguishable from one that is stuck.
    ///
    /// Counted through the same query the batch was drawn from, one page wide rather than
    /// unbounded - a count is not worth an unbounded read against a database billed on wall-clock
    /// time, and "at least this many" answers the question a cap is set against.
    /// </remarks>
    private async Task<int> WaitingAsync(IReadOnlyList<long> profileIds, int floor, CancellationToken ct)
    {
        var waiting = 0;

        foreach (var profileId in profileIds)
        {
            var rows = await matches.ListApplyableAsync(
                profileId,
                new ApplyableQuery
                {
                    DocumentsReady = false,
                    ApplyUrlSource = ApplyUrlSource.Posting,
                    MinAssessmentScore = floor,
                    Limit = WaitingCeiling,
                },
                ct);

            if (rows.Count == 0)
            {
                continue;
            }

            var postings = await DescribeAsync(rows, ct);

            waiting += rows.Count(row =>
                row.AtsVendor.IsEmployerAts() && postings.GetValueOrDefault(row.PostingId).HasAdvert);
        }

        return waiting;
    }

    /// <summary>How far the backlog count reads before it stops counting.</summary>
    /// <remarks>
    /// A ceiling rather than a total, so the figure is honest at its top: a night reporting the
    /// ceiling means "at least this many", which is all a cap needs to be set against and all an
    /// indexed page is worth paying for.
    /// </remarks>
    private const int WaitingCeiling = 100;

    /// <summary>
    /// Folds one candidate's batch into the run's summary.
    /// </summary>
    /// <remarks>
    /// A method rather than an <c>operator +</c> on the summary, which is what
    /// <c>MatchSweepFunction.AssessmentTally</c> uses: the two types here have different
    /// visibilities on purpose - the summary is the HTTP route's answer and the tally is this
    /// file's bookkeeping - and an operator would have to make the tally public to be declared at
    /// all. <see cref="GenerationTally.Aborted"/> is deliberately not folded in: it is an
    /// instruction to the loop, not a quantity, and a summary carrying it would invite a reader to
    /// add two of them.
    /// </remarks>
    private static GenerationSummary Fold(GenerationSummary summary, GenerationTally tally)
        => summary with
        {
            Considered = summary.Considered + tally.Considered,
            Written = summary.Written + tally.Written,
            Rendered = summary.Rendered + tally.Rendered,
            Skipped = summary.Skipped + tally.Skipped,
            Failed = summary.Failed + tally.Failed,
        };

    /// <summary>What one candidate's batch did, before it is folded into the run's summary.</summary>
    /// <param name="Considered">Postings a writing call was actually made for. This is what the run cost.</param>
    /// <param name="Written">Drafts stored. Below <paramref name="Considered"/> means a loss.</param>
    /// <param name="Rendered">Drafts that also have files behind them.</param>
    /// <param name="Skipped">Queue rows this pass will never write for, and stepped over.</param>
    /// <param name="Failed">Writing calls that came back with nothing usable.</param>
    /// <param name="Aborted">Whether the provider looked down and the run stopped.</param>
    private readonly record struct GenerationTally(
        int Considered, int Written, int Rendered, int Skipped, int Failed, bool Aborted)
    {
        public static GenerationTally Empty => default;
    }

    /// <param name="Profiles">How many candidates the pass considered.</param>
    /// <param name="Considered">Postings a writing call was made for, which is what the run cost.</param>
    /// <param name="Written">Drafts stored.</param>
    /// <param name="Rendered">Drafts with rendered files recorded against them. Zero where no pack store is registered.</param>
    /// <param name="Skipped">Queue rows stepped over: an aggregator link, an advert with no body, a match that went away.</param>
    /// <param name="Failed">Writing calls that returned nothing usable.</param>
    /// <param name="Waiting">
    /// Postings still in the apply queue with no documents, after this run and up to
    /// <see cref="WaitingCeiling"/> per profile.
    /// </param>
    /// <remarks>
    /// <see cref="Considered"/> and <see cref="Waiting"/> are reported rather than left to be
    /// derived because a caller cannot derive either: a night that wrote eight looks identical
    /// whether it made eight calls or eleven, and whether it left two postings behind or four
    /// hundred.
    /// </remarks>
    public sealed record GenerationSummary(
        int Profiles, int Considered, int Written, int Rendered, int Skipped, int Failed, int Waiting)
    {
        public static GenerationSummary Empty => new(0, 0, 0, 0, 0, 0, 0);
    }
}
