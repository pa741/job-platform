using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPlatform.Core.Submissions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The question queue over HTTP: what a person is asked, and what answering it does.
/// </summary>
/// <remarks>
/// <b>These routes did not exist.</b> The dashboard called <c>/api/v1/questions</c> and
/// <c>/api/v1/questions/{id}/answer</c> and both answered 404, so the entire user-escalation half
/// of the apply loop had no surface: a run parked an advert for a missing answer, raised the
/// question, and there was nowhere for anybody to answer it. Every test here fails as a 404 when
/// the group is not mapped, which is the property that makes them worth having.
///
/// <b>The shape is asserted field for field, not sampled.</b> <c>web/src/api/types.ts</c> was
/// written first and is the contract; a response missing <c>companyId</c> costs the dashboard the
/// company scope with nothing failing, and a response missing <c>parked</c> costs it the only
/// indication that a question is holding an application back. So the property names are asserted
/// as a set rather than one at a time.
/// </remarks>
public sealed class QuestionEndpointTests
{
    /// <summary>Exactly what <c>OpenQuestion</c> declares, and nothing else.</summary>
    private static readonly string[] QuestionFields =
    [
        "askedAtUtc", "company", "companyId", "options", "parked", "postingId", "postingTitle",
        "questionId", "questionText", "runId", "sensitive",
    ];

    /// <summary>Exactly what <c>ParkedApplication</c> declares.</summary>
    private static readonly string[] ParkedFields =
    [
        "company", "parkedAtUtc", "postingId", "postingTitle", "submissionId",
    ];

    /// <summary>Exactly what <c>AnswerQuestionResponse</c> declares.</summary>
    private static readonly string[] ReceiptFields =
    [
        "answerId", "answeredAtUtc", "closedQuestionId", "created", "note", "returnedToQueue",
        "scope", "sensitive",
    ];

    [Fact]
    public async Task The_queue_lists_what_is_waiting_with_the_advert_that_raised_it()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var items = await QueueAsync(harness, QuestionQueueHarness.Ada);

        // Oldest first, which inverts every other list in this API: this is a queue to be
        // drained, and the question that has held an advert back longest is the one to put in
        // front of somebody.
        Assert.Equal(
            [harness.SalaryQuestion, harness.NoticeQuestion, harness.LooseQuestion, harness.UnfoldedQuestion],
            items.Select(item => item.GetProperty("questionId").GetInt64()));

        var salary = items[0];

        Assert.Equal(QuestionFields, Names(salary));
        Assert.Equal(QuestionQueueHarness.Parked, salary.GetProperty("postingId").GetInt64());
        Assert.Equal("Platform Engineer", salary.GetProperty("postingTitle").GetString());
        Assert.Equal("Contoso", salary.GetProperty("company").GetString());
        Assert.Equal(harness.Contoso, salary.GetProperty("companyId").GetInt32());
        Assert.Equal(JsonValueKind.Null, salary.GetProperty("runId").ValueKind);
        Assert.Equal("What are your salary expectations?", salary.GetProperty("questionText").GetString());
        Assert.Equal(
            ["Under 60k", "60-80k", "80k+"],
            salary.GetProperty("options").EnumerateArray().Select(option => option.GetString()));

        var parked = salary.GetProperty("parked");

        Assert.Equal(ParkedFields, Names(parked));
        Assert.Equal(harness.ParkedSubmission, parked.GetProperty("submissionId").GetInt64());
        Assert.Equal(QuestionQueueHarness.Parked, parked.GetProperty("postingId").GetInt64());
        Assert.Equal("Platform Engineer", parked.GetProperty("postingTitle").GetString());
        Assert.Equal("Contoso", parked.GetProperty("company").GetString());
    }

    /// <summary>
    /// A question with no advert has nothing to file against and nothing to release.
    /// </summary>
    /// <remarks>
    /// Both nulls matter to the dashboard, which offers only the scopes a question can actually
    /// carry: an employer-wide answer filed with no employer applies to everybody, which is the
    /// failure scoping exists to prevent arriving through the interface instead.
    /// </remarks>
    [Fact]
    public async Task A_question_with_no_advert_behind_it_carries_no_employer_and_nothing_parked()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var items = await QueueAsync(harness, QuestionQueueHarness.Ada);
        var loose = Find(items, harness.LooseQuestion);

        Assert.Equal(JsonValueKind.Null, loose.GetProperty("postingId").ValueKind);
        Assert.Equal(JsonValueKind.Null, loose.GetProperty("postingTitle").ValueKind);
        Assert.Equal(JsonValueKind.Null, loose.GetProperty("companyId").ValueKind);
        Assert.Equal(JsonValueKind.Null, loose.GetProperty("parked").ValueKind);

        // An advert whose employer was never folded into a company row: the advert is there, the
        // employer id is not, and the difference is what decides whether a company scope exists.
        var unfolded = Find(items, harness.UnfoldedQuestion);

        Assert.Equal(QuestionQueueHarness.WithoutEmployer, unfolded.GetProperty("postingId").GetInt64());
        Assert.Equal(JsonValueKind.Null, unfolded.GetProperty("companyId").ValueKind);
        Assert.Equal(JsonValueKind.Null, unfolded.GetProperty("parked").ValueKind);
    }

    /// <summary>
    /// Somebody else's question does not exist as far as this caller is concerned.
    /// </summary>
    /// <remarks>
    /// 404 rather than 403, because a 403 confirms that a question with that id exists and belongs
    /// to somebody - which is a fact about another person's job search. The repository is scoped
    /// to the profile id, so "not yours" and "no such question" are one answer by construction.
    /// </remarks>
    [Fact]
    public async Task Another_candidates_question_is_invisible_rather_than_forbidden()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var hers = await QueueAsync(harness, QuestionQueueHarness.Grace);

        Assert.Equal([harness.GraceQuestion], hers.Select(item => item.GetProperty("questionId").GetInt64()));

        var response = await AnswerAsync(
            harness, QuestionQueueHarness.Grace, harness.SalaryQuestion, "60-80k", "Global");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);

        // And it is still waiting on the person it was actually asked of.
        var his = await QueueAsync(harness, QuestionQueueHarness.Ada);

        Assert.Contains(his, item => item.GetProperty("questionId").GetInt64() == harness.SalaryQuestion);
    }

    /// <summary>
    /// The one write in the system that may say a person typed it.
    /// </summary>
    /// <remarks>
    /// Everything arriving over the tool surface is stored as <see cref="FormAnswerSource.Client"/>.
    /// The source is read from the token and there is no field in the body for it, so this is
    /// asserted against the stored column rather than against what the route said it did.
    /// </remarks>
    [Fact]
    public async Task An_answer_is_stored_as_the_candidates_own_assertion()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var receipt = await ReceiptAsync(
            harness, QuestionQueueHarness.Ada, harness.NoticeQuestion, "One month", "Global");

        Assert.Equal(ReceiptFields, Names(receipt));
        Assert.True(receipt.GetProperty("created").GetBoolean());
        Assert.Equal("Global", receipt.GetProperty("scope").GetString());
        Assert.Equal(harness.NoticeQuestion, receipt.GetProperty("closedQuestionId").GetInt64());

        using var db = harness.Database();

        var stored = await db.FormAnswers.AsNoTracking().SingleAsync();

        Assert.Equal(FormAnswerSource.Candidate, stored.Source);
        Assert.Equal("One month", stored.Value);
        Assert.Equal(AnswerScope.Global, stored.Scope);
        Assert.Equal(harness.AdaProfile, stored.ProfileId);
    }

    /// <summary>
    /// The causal link the queue exists for, asserted through the predicate that reads it.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, because the first answer must not release the advert.</b> A posting parked
    /// for a missing answer is held while anything this candidate owes is outstanding, so
    /// reporting a release on the first answer would have somebody waiting for a run to pick up
    /// an advert that is still held, with nothing anywhere saying why.
    ///
    /// <b>Asserted as "exactly one answer released it" rather than "the second one did".</b>
    /// Which answer frees the advert is <c>ListApplyableAsync</c>'s business and it has already
    /// changed once - it holds a parked posting on every advert-raised question, not only on the
    /// ones raised by that advert, because the deduplication leaves no way to tell which. This
    /// route delegates that reading rather than restating it, so the claim worth pinning is that
    /// the release and the queue agree, not the number of answers it took.
    ///
    /// <b>And the park is not lifted.</b> The row still reads as parked; what changed is that the
    /// questions holding it are answered, which is the only fact the queue predicate consults.
    /// Clearing <c>UnparkedAtUtc</c> here would assert that an attempt had been made again.
    /// </remarks>
    [Fact]
    public async Task Answering_the_last_question_returns_the_parked_posting_to_the_queue()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        Assert.DoesNotContain(QuestionQueueHarness.Parked, await harness.ApplyableAsync());

        var first = await ReceiptAsync(
            harness, QuestionQueueHarness.Ada, harness.NoticeQuestion, "One month", "Global");

        Assert.Empty(first.GetProperty("returnedToQueue").EnumerateArray());
        Assert.Contains("still waiting on", first.GetProperty("note").GetString());
        Assert.DoesNotContain(QuestionQueueHarness.Parked, await harness.ApplyableAsync());

        var later = new[]
        {
            await ReceiptAsync(harness, QuestionQueueHarness.Ada, harness.SalaryQuestion, "80k+", "Global"),
            await ReceiptAsync(harness, QuestionQueueHarness.Ada, harness.UnfoldedQuestion, "Four years", "Global"),
        };

        var released = later
            .SelectMany(receipt => receipt.GetProperty("returnedToQueue").EnumerateArray())
            .ToList();

        var row = Assert.Single(released);

        Assert.Equal(ParkedFields, Names(row));
        Assert.Equal(harness.ParkedSubmission, row.GetProperty("submissionId").GetInt64());
        Assert.Equal(QuestionQueueHarness.Parked, row.GetProperty("postingId").GetInt64());
        Assert.Contains(QuestionQueueHarness.Parked, await harness.ApplyableAsync());

        using var db = harness.Database();
        var submission = await db.Submissions.AsNoTracking().SingleAsync();

        Assert.Equal(ParkReason.MissingAnswer, submission.ParkedReason);
        Assert.Null(submission.UnparkedAtUtc);
    }

    /// <summary>
    /// A sensitive question is marked by its wording, not only by whatever raised it.
    /// </summary>
    /// <remarks>
    /// The row was opened with the flag off, so a queue reading the column alone would show a
    /// salary question with no confirmation and then store the answer as sensitive - the queue and
    /// the store disagreeing about the same question. It buys redaction and a confirmation and
    /// never permission to infer: nothing in the derived namespace can produce a salary
    /// expectation at all.
    /// </remarks>
    [Fact]
    public async Task A_sensitive_question_is_marked_by_its_wording_and_stored_that_way()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var items = await QueueAsync(harness, QuestionQueueHarness.Ada);

        Assert.True(Find(items, harness.SalaryQuestion).GetProperty("sensitive").GetBoolean());
        Assert.False(Find(items, harness.NoticeQuestion).GetProperty("sensitive").GetBoolean());

        var receipt = await ReceiptAsync(
            harness, QuestionQueueHarness.Ada, harness.SalaryQuestion, "80k+", "Global");

        Assert.True(receipt.GetProperty("sensitive").GetBoolean());

        using var db = harness.Database();

        Assert.True((await db.FormAnswers.AsNoTracking().SingleAsync()).Sensitive);
    }

    /// <summary>
    /// An over-long answer is refused rather than shortened, and nothing is stored.
    /// </summary>
    /// <remarks>
    /// A truncated sentence typed into an employer's form reads as a statement rather than as a
    /// bug, which is why this bound refuses where the submission log's bounds trim. The question
    /// staying open is the other half: a refused write that had closed it would leave somebody
    /// with an advert parked on a question nothing will ever ask again.
    /// </remarks>
    [Fact]
    public async Task An_answer_over_its_bound_is_refused_rather_than_shortened()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var response = await AnswerAsync(
            harness,
            QuestionQueueHarness.Ada,
            harness.NoticeQuestion,
            new string('x', FormAnswerLimits.MaxValueLength + 1),
            "Global");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var db = harness.Database();

        Assert.Empty(await db.FormAnswers.AsNoTracking().ToListAsync());

        var items = await QueueAsync(harness, QuestionQueueHarness.Ada);

        Assert.Contains(items, item => item.GetProperty("questionId").GetInt64() == harness.NoticeQuestion);
    }

    /// <summary>
    /// Blank is not an answer, and storing it as one would settle the question.
    /// </summary>
    /// <remarks>
    /// "Prefer not to say" is a value and closes the question; nothing is not, and a stored blank
    /// would tell every later resolution that this question was answered - so the advert would be
    /// released on an answer that cannot be typed into anything.
    /// </remarks>
    [Fact]
    public async Task A_blank_answer_is_refused_so_the_question_stays_open()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var response = await AnswerAsync(
            harness, QuestionQueueHarness.Ada, harness.NoticeQuestion, "   ", "Global");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var db = harness.Database();

        Assert.Empty(await db.FormAnswers.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// An id nothing issued is not found, and the route it was sent to exists.
    /// </summary>
    /// <remarks>
    /// The second half is why the known id is answered first. A 404 is also what an unmapped
    /// route returns, so this is the one assertion in the file that a missing group would
    /// satisfy - and a missing group is exactly what was wrong. Proving the route answers before
    /// proving it refuses is what stops this passing against nothing at all.
    /// </remarks>
    [Fact]
    public async Task An_unknown_question_id_is_not_found()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        await ReceiptAsync(harness, QuestionQueueHarness.Ada, harness.NoticeQuestion, "One month", "Global");

        var response = await AnswerAsync(
            harness, QuestionQueueHarness.Ada, questionId: 999_999, "One month", "Global");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The first close stands, and the second call converges rather than failing.
    /// </summary>
    /// <remarks>
    /// The row records that somebody was asked and what came back, and a second write would erase
    /// the timestamp the first is evidence of. 409 rather than a silent success, because the
    /// dashboard reads it as "somebody already answered this" and shows what was recorded instead
    /// of claiming the second person did it.
    /// </remarks>
    [Fact]
    public async Task Answering_a_question_twice_converges_on_the_first_answer()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        await ReceiptAsync(harness, QuestionQueueHarness.Ada, harness.NoticeQuestion, "One month", "Global");

        var again = await AnswerAsync(
            harness, QuestionQueueHarness.Ada, harness.NoticeQuestion, "Two months", "Global");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        using var db = harness.Database();
        var stored = await db.FormAnswers.AsNoTracking().SingleAsync();

        Assert.Equal("One month", stored.Value);
    }

    /// <summary>
    /// A company-scoped answer is filed against the employer's row, which no caller names.
    /// </summary>
    /// <remarks>
    /// The company table already folds "Contoso" and "Contoso Ltd" into one employer, so keying on
    /// the name printed on the advert would file the same answer twice. The id is read from the
    /// question's own advert rather than from the body: an id a caller could name is one a caller
    /// could name wrongly, and there would be nothing to check it against.
    /// </remarks>
    [Fact]
    public async Task A_company_scoped_answer_is_filed_against_the_employer_row()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var receipt = await ReceiptAsync(
            harness, QuestionQueueHarness.Ada, harness.NoticeQuestion, "One month", "Company");

        Assert.Equal("Company", receipt.GetProperty("scope").GetString());

        using var db = harness.Database();
        var stored = await db.FormAnswers.AsNoTracking().SingleAsync();

        Assert.Equal(harness.Contoso, stored.CompanyId);
        Assert.Null(stored.PostingId);
    }

    /// <summary>
    /// No employer row, no company scope - refused rather than quietly widened.
    /// </summary>
    /// <remarks>
    /// An employer-wide answer filed against no employer applies to every employer, which is
    /// exactly the failure scoping exists to prevent. Storing it as global instead would be
    /// silently wider than what was asked for, which is worse than saying no.
    /// </remarks>
    [Fact]
    public async Task A_company_scope_is_refused_where_the_advert_names_no_employer_row()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var response = await AnswerAsync(
            harness, QuestionQueueHarness.Ada, harness.UnfoldedQuestion, "Four years", "Company");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var db = harness.Database();

        Assert.Empty(await db.FormAnswers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_posting_scope_is_refused_on_a_question_that_names_no_advert()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var response = await AnswerAsync(
            harness, QuestionQueueHarness.Ada, harness.LooseQuestion, "London", "Posting");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>An unrecognised scope names the ones that exist, rather than describing them.</summary>
    [Fact]
    public async Task An_unrecognised_scope_names_the_scopes_that_exist()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var response = await AnswerAsync(
            harness, QuestionQueueHarness.Ada, harness.NoticeQuestion, "One month", "Everywhere");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var detail = problem.GetProperty("detail").GetString();

        Assert.Contains("Global", detail);
        Assert.Contains("Company", detail);
        Assert.Contains("Posting", detail);
    }

    /// <summary>
    /// A principal with no profile has nothing waiting on them, which is a complete answer.
    /// </summary>
    /// <remarks>
    /// An empty list rather than a 404, following the submission list: somebody who has not filled
    /// the form in is not an error, and a 404 here would send the dashboard down its "something is
    /// wrong" path on the ordinary first visit.
    /// </remarks>
    [Fact]
    public async Task A_principal_with_no_profile_has_an_empty_queue_rather_than_a_404()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();

        var response = await harness.As("99999999-9999-9999-9999-999999999999")
            .GetAsync("/api/v1/questions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// The anonymous-reads switch must not open this group.
    /// </summary>
    /// <remarks>
    /// <c>Api:AllowAnonymousReads</c> exists to open the posting corpus, which is public text.
    /// What an employer asked this person and what they answered is the opposite of that - it is
    /// the most sensitive thing the system holds, because a sensitive value can exist only where
    /// somebody typed it. The harness turns the switch on precisely so this can be asserted.
    /// </remarks>
    [Fact]
    public async Task The_queue_stays_closed_even_when_anonymous_reads_are_allowed()
    {
        using var harness = await QuestionQueueHarness.CreateAsync();
        var anonymous = harness.As(subject: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/questions")).StatusCode);

        using var content = JsonContent.Create(new { value = "One month", scope = "Global" });
        var write = await anonymous.PostAsync($"/api/v1/questions/{harness.NoticeQuestion}/answer", content);

        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    /// <summary>
    /// The policy on the group, asserted as metadata rather than through a response.
    /// </summary>
    /// <remarks>
    /// The behavioural version above cannot pin it on its own: both handlers also call
    /// <c>CallerIdentity.TryGetSubjectId</c>, which answers 401 for a token with no <c>oid</c>, so
    /// an anonymous request answers 401 whichever policy is on the group. That is defence in depth
    /// working and a test measuring the second layer while describing the first - the mistake
    /// <c>AuthorizationTests</c> already made once on the submissions.
    /// </remarks>
    [Fact]
    public void Every_question_route_requires_the_authenticated_policy()
    {
        foreach (var route in QuestionRoutes())
        {
            var policies = route.Metadata
                .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Select(data => data.Policy)
                .ToList();

            Assert.Contains(JobPlatform.Api.Infrastructure.AuthSetup.AuthenticatedPolicy, policies);
        }
    }

    /// <summary>
    /// Nothing here is output cached, and that is a safety property rather than a tuning choice.
    /// </summary>
    /// <remarks>
    /// The queue is per-principal and mutable, and the cache is keyed on a URL with no user in it
    /// - so caching <c>/api/v1/questions</c> is how one person is served another's queue, and how
    /// an answered question keeps being offered back to the person who answered it.
    /// </remarks>
    [Fact]
    public void No_question_route_is_output_cached()
    {
        foreach (var route in QuestionRoutes())
        {
            Assert.DoesNotContain(
                route.Metadata,
                item => item.GetType().Name.Contains("OutputCache", StringComparison.Ordinal));
        }
    }

    private static List<RouteEndpoint> QuestionRoutes()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };

        // Forces the host to build; the endpoint data source is not populated before it does.
        using var client = factory.CreateClient();

        var routes = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?
                .StartsWith("/api/v1/questions", StringComparison.Ordinal) == true)
            .ToList();

        // The queue and the answer. Asserting the count stops the loops above passing vacuously
        // against a group that was never mapped - which is the state this whole file was written
        // for.
        Assert.Equal(2, routes.Count);

        return routes;
    }

    private static async Task<List<JsonElement>> QueueAsync(QuestionQueueHarness harness, string subject)
    {
        var response = await harness.As(subject).GetAsync("/api/v1/questions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private static async Task<HttpResponseMessage> AnswerAsync(
        QuestionQueueHarness harness, string subject, long questionId, string value, string scope)
    {
        using var content = JsonContent.Create(new { value, scope });

        return await harness.As(subject).PostAsync($"/api/v1/questions/{questionId}/answer", content);
    }

    private static async Task<JsonElement> ReceiptAsync(
        QuestionQueueHarness harness, string subject, long questionId, string value, string scope)
    {
        var response = await AnswerAsync(harness, subject, questionId, value, scope);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Find(IEnumerable<JsonElement> items, long questionId)
        => items.Single(item => item.GetProperty("questionId").GetInt64() == questionId);

    /// <summary>The property names of one object, sorted, so a contract can be asserted as a set.</summary>
    private static IReadOnlyList<string> Names(JsonElement element)
        => [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];
}
