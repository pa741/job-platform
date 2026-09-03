using System.Text.Json;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The refusals, which are where this surface's safety actually lives.
/// </summary>
/// <remarks>
/// <b>Every one of these is an ordinary state of the system, and none of them may be an
/// exception.</b> A protocol-level error invites a retry; a sentence invites a different action,
/// and the reader is a model that will otherwise guess again. So the shape is asserted as well as
/// the fact - <c>{refused, reason}</c> and nothing else - because a refusal that grew a third
/// field would be a refusal some client started keying on.
///
/// <b>What is refused matters more here than on any other surface in this repository, because the
/// arguments are named by a model rather than by a router.</b> A posting id nobody matched, a park
/// reason that sounds right, an answer forty characters too long: each of them is a plausible
/// thing for a client to send and each of them, unrefused, writes something wrong into a log that
/// has no eraser or types something wrong into an employer's form.
///
/// <b>The sensitive cases are the ones to read first.</b> A sensitive answer is returned verbatim
/// from what the candidate typed or not at all - never mapped onto the nearest option, never
/// inferred from the profile - and the four tests at the end of this file are the difference
/// between those two sentences being a design note and being true.
/// </remarks>
public sealed class McpToolRefusalTests
{
    // -----------------------------------------------------------------------
    // Who is calling
    // -----------------------------------------------------------------------

    /// <summary>
    /// An app-only token nobody mapped gets its own refusal, not the empty-profile one.
    /// </summary>
    /// <remarks>
    /// "This deployment is not finished" and "this candidate has not filled the form in" produce
    /// the same empty answer and want opposite fixes. Telling them apart is the whole reason
    /// <c>McpOptions.AppPrincipals</c> is resolved in this feature rather than in
    /// <c>CallerIdentity</c>, so a test that only checked "it refused" would pass while the
    /// distinction was lost.
    /// </remarks>
    [Fact]
    public async Task An_unmapped_application_token_is_told_that_and_not_that_the_profile_is_missing()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(
            await harness.Tools().ListApplyableAsync(McpToolHarness.AsUnmappedApplication()));

        Assert.True(refused);
        Assert.Contains("identifies an application rather than a person", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no profile yet", reason, StringComparison.Ordinal);
    }

    /// <summary>A mapped application token acts for the candidate, and the audit says both.</summary>
    /// <remarks>
    /// The indirection an operator wrote, exercised end to end: the identity still arrives with
    /// the token and the disclosure records the principal and the candidate separately, because
    /// "whose data left" and "what took it" have stopped being the same answer.
    /// </remarks>
    [Fact]
    public async Task A_mapped_application_token_reads_the_candidate_it_acts_for_and_is_recorded_as_itself()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var answer = McpToolHarness.Read(await harness.Tools()
            .GetFormFieldAsync(McpToolHarness.AsMappedApplication(), "email"));

        Assert.Equal("ada@example.invalid", answer.GetProperty("value").GetString());

        var record = Assert.Single(harness.Disclosures.Records);

        Assert.Equal(McpToolHarness.Subject, record.SubjectId);
        Assert.Equal(McpToolHarness.MappedApplication, record.ActorId);
    }

    /// <summary>A token with no object id is refused before anything is read.</summary>
    [Fact]
    public async Task A_token_with_no_object_id_cannot_be_answered_at_all()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(
            await harness.Tools().ListSubmissionsAsync(McpToolHarness.AsNobody()));

        Assert.True(refused);
        Assert.Contains("'oid'", reason, StringComparison.Ordinal);
        Assert.Empty(harness.Disclosures.Records);
    }

    // -----------------------------------------------------------------------
    // A posting this candidate was never matched to
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every tool taking a posting id checks it against this candidate's own matches.
    /// </summary>
    /// <remarks>
    /// <b>One test over four tools deliberately.</b> The rule is a property of the surface rather
    /// than of any one tool, and the way it breaks is a fifth tool taking a posting id and
    /// forgetting - so the assertion is written where the fifth one will be added. Posting 99 is a
    /// real row, so this is not "no such posting": it is a posting that exists and is not this
    /// candidate's business, which is the case an id space can be probed with.
    /// </remarks>
    [Fact]
    public async Task A_posting_nobody_matched_to_this_candidate_is_refused_by_every_tool_that_takes_one()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var results = new[]
        {
            await harness.Tools().GetSubmissionPackAsync(McpToolHarness.AsCandidate(), McpToolHarness.Unmatched),
            await harness.Tools().CreateSubmissionAsync(McpToolHarness.AsCandidate(), McpToolHarness.Unmatched),
            await harness.Tools().ParkApplicationAsync(McpToolHarness.AsCandidate(), McpToolHarness.Unmatched, "Captcha"),
            await harness.Tools().RecordFormAnswerAsync(
                McpToolHarness.AsCandidate(), "Why this role?", "Because.", "Posting", McpToolHarness.Unmatched),
        };

        Assert.All(results, result =>
        {
            var (refused, reason) = McpToolHarness.Refusal(result);

            Assert.True(refused);
            Assert.Contains("has not been matched", reason, StringComparison.Ordinal);

            // Every refusal names what to do instead, because the reader is a model that will
            // otherwise try the same call again with a different id.
            Assert.Contains("list_applyable", reason, StringComparison.Ordinal);
        });

        // Nothing was written on the way to any of those refusals.
        var submissions = McpToolHarness.Read(
            await harness.Tools().ListSubmissionsAsync(McpToolHarness.AsCandidate()));

        Assert.Empty(submissions.GetProperty("items").EnumerateArray());
    }

    /// <summary>A refusal is an object with two properties and no more.</summary>
    /// <remarks>
    /// Pinned because a refusal is the one answer every tool can give and therefore the one shape
    /// every client parses. A field added here would be a field some client starts depending on,
    /// and the next tool to refuse without it would look like a different kind of failure.
    /// </remarks>
    [Fact]
    public async Task A_refusal_is_a_structured_object_and_never_an_exception()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var refusal = McpToolHarness.Read(
            await harness.Tools().GetSubmissionPackAsync(McpToolHarness.AsCandidate(), McpToolHarness.Unmatched));

        Assert.Equal(["reason", "refused"], McpToolHarness.Keys(refusal));
        Assert.True(refusal.GetProperty("refused").GetBoolean());
        Assert.Equal(JsonValueKind.String, refusal.GetProperty("reason").ValueKind);
    }

    // -----------------------------------------------------------------------
    // Parking
    // -----------------------------------------------------------------------

    /// <summary>
    /// A park reason outside the enum is refused, and the refusal lists the whole set.
    /// </summary>
    /// <remarks>
    /// <b>The reason decides whether the posting ever comes back</b>, so a near miss is not a
    /// spelling problem: <c>Expired</c> and <c>Duplicate</c> retire a posting for good, and
    /// everything else returns it. Defaulting a bad value to any member would silently pick one of
    /// those two behaviours on a caller's behalf.
    /// </remarks>
    [Fact]
    public async Task An_unknown_park_reason_is_refused_and_the_refusal_names_every_reason_there_is()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools()
            .ParkApplicationAsync(McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments, "Blocked"));

        Assert.True(refused);

        foreach (var name in Enum.GetNames<ParkReason>())
        {
            Assert.Contains(name, reason, StringComparison.Ordinal);
        }

        // Nothing was parked on the way to the refusal, so the posting is still work to do.
        var submissions = McpToolHarness.Read(
            await harness.Tools().ListSubmissionsAsync(McpToolHarness.AsCandidate()));

        Assert.Empty(submissions.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// A number that names no member is refused, which <c>Enum.TryParse</c> alone would accept.
    /// </summary>
    /// <remarks>
    /// <c>TryParse</c> reads any integer as the enum's underlying type and answers true, so
    /// <c>"99"</c> would arrive as a <see cref="ParkReason"/> nobody wrote and be stored as one -
    /// a column holding a value the enum cannot name, which every reader afterwards has to guess
    /// about. <c>Enum.IsDefined</c> is what catches it.
    ///
    /// <b>It does not catch a number that happens to name one</b>, and it cannot: <c>"4"</c> is
    /// <see cref="ParkReason.Captcha"/> by every test the runtime can apply. That residue is worth
    /// knowing about rather than papering over - the protection here is against an undefined
    /// value reaching the database, not against a model sending digits.
    /// </remarks>
    [Fact]
    public async Task A_park_reason_that_names_no_member_is_refused_even_when_it_is_a_number()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, _) = McpToolHarness.Refusal(await harness.Tools()
            .ParkApplicationAsync(McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments, "99"));

        Assert.True(refused);
    }

    /// <summary>Parking for a missing answer without the question is refused.</summary>
    /// <remarks>
    /// The question is what gets put to the candidate and what lets the posting come back when
    /// they answer it. Parked without one, the posting waits on nothing and returns next run to be
    /// blocked by the same field.
    /// </remarks>
    [Fact]
    public async Task Parking_for_a_missing_answer_without_the_question_is_refused()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools()
            .ParkApplicationAsync(McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments, "MissingAnswer"));

        Assert.True(refused);
        Assert.Contains("needs the question", reason, StringComparison.Ordinal);

        var waiting = McpToolHarness.Read(
            await harness.Tools().ListOpenQuestionsAsync(McpToolHarness.AsCandidate()));

        Assert.Empty(waiting.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// An application that already carries events cannot be parked.
    /// </summary>
    /// <remarks>
    /// <b>The worst outcome on this surface, and the reason this refusal is worth its round
    /// trip.</b> A park sets columns on whatever submission exists for the pair, so parking a sent
    /// application would make it read as a posting nobody attempted <i>and</i> - for every reason
    /// but Expired and Duplicate - hand the posting back to the queue for a second application to
    /// the same vacancy. The recruiter sees both.
    /// </remarks>
    [Fact]
    public async Task An_application_that_was_already_sent_cannot_be_parked()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().CreateSubmissionAsync(
            McpToolHarness.AsCandidate(),
            McpToolHarness.WithDocuments,
            sent: true,
            idempotencyKey: "run-1:10:Submitted");

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools()
            .ParkApplicationAsync(McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments, "FormError"));

        Assert.True(refused);
        Assert.Contains("record_event", reason, StringComparison.Ordinal);

        var submissions = McpToolHarness.Read(
            await harness.Tools().ListSubmissionsAsync(McpToolHarness.AsCandidate()));

        var row = Assert.Single(submissions.GetProperty("items").EnumerateArray().ToList());

        Assert.False(row.GetProperty("parked").GetBoolean());
        Assert.Equal("Submitted", row.GetProperty("phase").GetString());
    }

    // -----------------------------------------------------------------------
    // Answers, and their bounds
    // -----------------------------------------------------------------------

    /// <summary>
    /// An answer past its bound is refused rather than shortened.
    /// </summary>
    /// <remarks>
    /// <b>A truncated sentence typed into an employer's form reads as a statement rather than as a
    /// bug.</b> Checked at the tool so the caller gets a refusal it can act on rather than the
    /// exception <c>FormAnswer.Create</c> throws for the same bound - the two agree on the number
    /// and disagree on what a caller can do about it.
    /// </remarks>
    [Fact]
    public async Task An_answer_longer_than_its_column_is_refused_and_never_shortened()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var essay = new string('x', FormAnswerLimits.MaxValueLength + 1);

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools()
            .RecordFormAnswerAsync(McpToolHarness.AsCandidate(), "Tell us about yourself", essay));

        Assert.True(refused);
        Assert.Contains("refused rather than shortened", reason, StringComparison.Ordinal);

        // And nothing was stored, which is the half a caller cannot see from the refusal.
        var resolved = McpToolHarness.Read(await harness.Tools()
            .ResolveFormFieldAsync(McpToolHarness.AsCandidate(), "Tell us about yourself"));

        Assert.True(resolved.GetProperty("needsUser").GetBoolean());
    }

    /// <summary>A question longer than a form's label is refused on the same terms.</summary>
    [Fact]
    public async Task A_question_longer_than_a_form_label_is_refused_by_both_tools_that_take_one()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var page = new string('q', FormAnswerLimits.MaxQuestionTextLength + 1);

        Assert.True(McpToolHarness.Refusal(
            await harness.Tools().RecordFormAnswerAsync(McpToolHarness.AsCandidate(), page, "Yes")).Refused);

        Assert.True(McpToolHarness.Refusal(
            await harness.Tools().ResolveFormFieldAsync(McpToolHarness.AsCandidate(), page)).Refused);
    }

    /// <summary>Blank is not an answer, and storing it would settle the question forever.</summary>
    /// <remarks>
    /// "Prefer not to say" is a value a candidate can type into a box; nothing is not. An empty
    /// answer stored as one tells every later resolution this question is answered.
    /// </remarks>
    [Fact]
    public async Task A_blank_answer_is_refused_because_a_stored_blank_would_read_as_settled()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools()
            .RecordFormAnswerAsync(McpToolHarness.AsCandidate(), "Do you need sponsorship?", "   "));

        Assert.True(refused);
        Assert.Contains("Prefer not to say", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A company-scoped answer cannot be written through this surface at all.
    /// </summary>
    /// <remarks>
    /// Nothing here names an employer's row, so the only way to file one would be for a model to
    /// pick the employer - and an answer filed against the wrong employer is the failure the
    /// scoping exists to prevent. The refusal names both scopes that <i>are</i> available and says
    /// where the third one is done.
    /// </remarks>
    [Fact]
    public async Task A_company_scoped_answer_is_refused_because_nothing_here_names_an_employer()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools()
            .RecordFormAnswerAsync(McpToolHarness.AsCandidate(), "Why us?", "Because.", "Company"));

        Assert.True(refused);
        Assert.Contains("dashboard", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scope and a posting id that disagree are refused in both directions.
    /// </summary>
    /// <remarks>
    /// A globally scoped row carrying a posting id looks narrowed in the database and is not,
    /// which is worse than either honest shape.
    /// </remarks>
    [Fact]
    public async Task A_scope_and_a_posting_that_disagree_are_refused_whichever_way_round_they_are()
    {
        using var harness = await McpToolHarness.CreateAsync();

        Assert.True(McpToolHarness.Refusal(await harness.Tools().RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(), "Why this role?", "Because.", "Posting")).Refused);

        Assert.True(McpToolHarness.Refusal(await harness.Tools().RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(), "Notice period?", "One month", "Global", McpToolHarness.WithDocuments)).Refused);
    }

    // -----------------------------------------------------------------------
    // Arguments a model half-remembered
    // -----------------------------------------------------------------------

    /// <summary>An assessment floor outside 0-100 is refused rather than clamped.</summary>
    /// <remarks>
    /// A floor of 120 returns nothing, and a run told "no postings today" would read that as a
    /// fact about the market rather than about its own argument.
    /// </remarks>
    [Fact]
    public async Task An_impossible_assessment_floor_is_refused_rather_than_clamped_into_an_empty_queue()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools()
            .ListApplyableAsync(McpToolHarness.AsCandidate(), minAssessmentScore: 120));

        Assert.True(refused);
        Assert.Contains("0", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable enum argument is refused rather than silently becoming a filter.
    /// </summary>
    /// <remarks>
    /// A silent <c>default(TEnum)</c> for <c>applyUrlSource</c> would restrict the queue to board
    /// pages - the queue would come back shorter and correct-looking, and nothing would say why.
    /// </remarks>
    [Fact]
    public async Task A_mistyped_filter_is_refused_rather_than_quietly_narrowing_the_queue()
    {
        using var harness = await McpToolHarness.CreateAsync();

        Assert.True(McpToolHarness.Refusal(await harness.Tools()
            .ListApplyableAsync(McpToolHarness.AsCandidate(), channel: "Employer")).Refused);

        Assert.True(McpToolHarness.Refusal(await harness.Tools()
            .ListApplyableAsync(McpToolHarness.AsCandidate(), orderBy: "Best")).Refused);

        Assert.True(McpToolHarness.Refusal(await harness.Tools()
            .ListApplyableAsync(McpToolHarness.AsCandidate(), applyUrlSource: "Direct")).Refused);
    }

    /// <summary>Recording a send needs a key only the caller can choose.</summary>
    [Fact]
    public async Task Recording_a_send_without_an_idempotency_key_is_refused()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools().CreateSubmissionAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments, sent: true));

        Assert.True(refused);
        Assert.Contains("idempotencyKey", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run's account of itself is refused whole rather than stored with an entry missing.
    /// </summary>
    /// <remarks>
    /// A summary is written once and never rewritten, so dropping the entry it could not read
    /// would be a permanent record of a run that parked fewer postings than it did.
    /// </remarks>
    [Fact]
    public async Task A_run_summary_with_an_unreadable_park_reason_is_refused_whole()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var run = McpToolHarness.Read(await harness.Tools().StartRunAsync(McpToolHarness.AsCandidate()));
        var runId = run.GetProperty("runId").GetInt64();

        var (refused, _) = McpToolHarness.Refusal(await harness.Tools().FinishRunAsync(
            McpToolHarness.AsCandidate(), runId, considered: 3, submitted: 1, parked: ["Captcha", "Blocked"]));

        Assert.True(refused);

        // The run is still open, which is what "nothing was recorded" has to mean here: a run
        // closed by a refused call could never be given its real account afterwards.
        var second = McpToolHarness.Read(await harness.Tools().FinishRunAsync(
            McpToolHarness.AsCandidate(), runId, considered: 3, submitted: 1, parked: ["Captcha"]));

        Assert.True(second.GetProperty("finished").GetBoolean());

        // Three considered, one sent, one parked: the third is the gap the tallying exists to
        // surface, and it is computed here rather than taken from the client's own arithmetic.
        Assert.Equal(1, second.GetProperty("summary").GetProperty("unaccounted").GetInt32());
    }

    /// <summary>A run belonging to nobody is refused rather than opened.</summary>
    [Fact]
    public async Task Finishing_a_run_that_is_not_this_candidates_is_refused()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(
            await harness.Tools().FinishRunAsync(McpToolHarness.AsCandidate(), 4242));

        Assert.True(refused);
        Assert.Contains("start_run", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// An email address where a domain was asked for is refused, not trimmed down to one.
    /// </summary>
    /// <remarks>
    /// Recruiter addresses are discarded at parse time on purpose, so quietly trimming one would
    /// make this tool the route by which one came back into a database that is careful never to
    /// hold one.
    /// </remarks>
    [Fact]
    public async Task A_recruiters_address_is_refused_where_a_domain_was_asked_for()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var (refused, reason) = McpToolHarness.Refusal(await harness.Tools().MatchEmailToSubmissionAsync(
            McpToolHarness.AsCandidate(),
            McpToolHarness.Now,
            senderDomain: "no-reply@greenhouse.io"));

        Assert.True(refused);
        Assert.Contains("not stored anywhere in this system", reason, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Sensitive fields: verbatim from the store, or nothing
    // -----------------------------------------------------------------------

    /// <summary>
    /// A sensitive question with nothing stored is not answered from the profile.
    /// </summary>
    /// <remarks>
    /// <b>This is H5 as it was actually built, and it is stronger than the flag the spec asked
    /// for.</b> The allowlist contains no salary expectation, no right to work and no EEO
    /// question, so there is nothing sensitive for a resolver to read even if it tried - and the
    /// declared store holds only what a person typed. A sensitive value can therefore exist only
    /// because somebody wrote it, which does not depend on a boolean being set correctly.
    ///
    /// The profile here <i>does</i> carry a minimum salary. That is the point: it is on the
    /// record, it is not on the allowlist, and it must not come back.
    /// </remarks>
    [Fact]
    public async Task A_sensitive_question_with_nothing_stored_is_not_answered_from_the_profile()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var resolved = McpToolHarness.Read(await harness.Tools().ResolveFormFieldAsync(
            McpToolHarness.AsCandidate(), "What is your expected salary?"));

        Assert.True(resolved.GetProperty("needsUser").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("value").ValueKind);
        Assert.False(resolved.GetProperty("consultedModel").GetBoolean());
        Assert.Contains("park the application", resolved.GetProperty("note").GetString(), StringComparison.Ordinal);

        // The rationale, not the flag: 'sensitive' on a resolution describes the answer that came
        // back and an abstention has none, so the only thing that can say <i>why</i> nothing came
        // back is the sentence. Asserting it is what separates "this question may not be guessed
        // at" from "this deployment has no model", which are the same empty answer and want
        // opposite fixes.
        Assert.Contains(
            "only the candidate may state",
            resolved.GetProperty("rationale").GetString(),
            StringComparison.Ordinal);

        // The number the profile does hold, in every spelling the pack would print it in.
        var whole = resolved.GetRawText();

        Assert.DoesNotContain("75000", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("75,000", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sensitivity of a question is read from its wording, not only from the flag.
    /// </summary>
    /// <remarks>
    /// A caller may only tighten this. A question that reads as sensitive is treated as one
    /// whatever the argument says, which is the half of the guarantee that survives a client
    /// forgetting to set it.
    /// </remarks>
    [Fact]
    public async Task A_question_that_reads_as_sensitive_is_treated_as_one_whatever_the_caller_said()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var stored = McpToolHarness.Read(await harness.Tools().RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(),
            "Do you require visa sponsorship to work in the UK?",
            "No",
            sensitive: false));

        Assert.True(stored.GetProperty("sensitive").GetBoolean());
    }

    /// <summary>
    /// A sensitive answer comes back verbatim from what the candidate typed.
    /// </summary>
    /// <remarks>
    /// Stage two, with no model anywhere near it: the store is the only source a sensitive answer
    /// may have, and this is the case where that source has one.
    /// </remarks>
    [Fact]
    public async Task A_sensitive_answer_the_candidate_stored_is_returned_verbatim()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(), "Do you require visa sponsorship to work in the UK?", "No");

        var resolved = McpToolHarness.Read(await harness.Tools().ResolveFormFieldAsync(
            McpToolHarness.AsCandidate(),
            "Do you require visa sponsorship to work in the UK?",
            ["Yes", "No", "Prefer not to say"]));

        Assert.Equal("No", resolved.GetProperty("value").GetString());
        Assert.Equal("DeclaredAnswer", resolved.GetProperty("stage").GetString());
        Assert.False(resolved.GetProperty("consultedModel").GetBoolean());
    }

    /// <summary>
    /// And it is never mapped onto an option that merely resembles it.
    /// </summary>
    /// <remarks>
    /// <b>The failure this whole design is arranged around.</b> "No" against
    /// <c>[Yes I require sponsorship, No I do not]</c> is a judgement about somebody's immigration
    /// status, and the confident near-miss is the characteristic failure of a matcher. Abstaining
    /// costs one interruption; guessing costs a declaration the candidate cannot take back.
    /// </remarks>
    [Fact]
    public async Task A_sensitive_answer_is_never_mapped_onto_an_option_that_merely_resembles_it()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(), "Do you require visa sponsorship to work in the UK?", "No");

        var resolved = McpToolHarness.Read(await harness.Tools().ResolveFormFieldAsync(
            McpToolHarness.AsCandidate(),
            "Do you require visa sponsorship to work in the UK?",
            ["Yes, I require sponsorship", "No, I do not require sponsorship"]));

        Assert.True(resolved.GetProperty("needsUser").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("value").ValueKind);
    }
}
