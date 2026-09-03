using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The question queue against a real relational engine.
/// </summary>
/// <remarks>
/// Three things are pinned here, and the third is the one the apply loop turns on.
///
/// <b>One live question per wording.</b> A run meeting the same question on four adverts must put
/// it to a person once, and a person who has answered it must not be asked again next week. The
/// repository checks first so the ordinary case is an answer rather than an exception; the
/// filtered unique index is what holds when two runs race, so it is asserted at the database by
/// writing round the repository.
///
/// <b>The queue is append-only.</b> Answering closes a row and never removes it, so what was
/// asked survives being answered - and a second answer does not rewrite the first, because the
/// timestamp on that row is evidence about when somebody was asked.
///
/// <b>An answered question stops suppressing its posting.</b> That is what makes a
/// <see cref="ParkReason.MissingAnswer"/> park retryable rather than a loop: the queue predicate
/// reads the unanswered set, so a question that leaves it has to let its advert back in - and
/// <i>every</i> advert parked on it, not only the one whose name is on the row. One live question
/// per wording is what makes those two different sets, and it is why the read behind a parked
/// application's explanation is scoped by the park rather than by which advert did the asking.
/// </remarks>
public sealed class OpenQuestionQueueTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const long OtherProfileId = 2;

    /// <summary>A stored answer belonging to <see cref="ProfileId"/>.</summary>
    private const long AnswerId = 41;

    /// <summary>A stored answer belonging to somebody else. It must never close this queue.</summary>
    private const long OtherAnswerId = 42;

    private const string Question = "Do you require sponsorship to work in the UK?";

    public OpenQuestionQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(_connection).Options;

        using var db = new JobsDbContext(_options);
        db.Database.EnsureCreated();

        foreach (var (id, subject) in new[]
                 {
                     (ProfileId, "11111111-1111-1111-1111-111111111111"),
                     (OtherProfileId, "22222222-2222-2222-2222-222222222222"),
                 })
        {
            db.CandidateProfiles.Add(new CandidateProfileEntity
            {
                Id = id,
                SubjectId = subject,
                FullName = "Test Candidate",
                Email = "candidate@example.invalid",
                CreatedUtc = Now,
                UpdatedUtc = Now,
            });
        }

        for (var id = 1; id <= 3; id++)
        {
            db.JobPostings.Add(new JobPostingEntity
            {
                Id = id,
                SourceKey = $"linkedin:{id}",
                Site = "linkedin",
                ExternalId = id.ToString(),
                ContentHash = new string((char)('a' + id), 64),
                Title = $"Role {id}",
                Company = $"Company {id}",
                JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        foreach (var (id, profileId) in new[] { (AnswerId, ProfileId), (OtherAnswerId, OtherProfileId) })
        {
            db.FormAnswers.Add(new FormAnswerEntity
            {
                Id = id,
                ProfileId = profileId,
                QuestionText = Question,
                QuestionHash = QuestionKey.Hash(Question),
                NormalisedQuestion = QuestionKey.Normalise(Question),
                Value = "No",
                Scope = AnswerScope.Global,
                Source = FormAnswerSource.Candidate,
                AnsweredAtUtc = Now,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    /// <summary>
    /// Puts an advert down, writing the columns the queue predicate reads.
    /// </summary>
    /// <remarks>
    /// Round <c>SubmissionRepository</c> deliberately, the way the queue's own tests are: what is
    /// being asserted is what these columns mean to a read, not what a writer believes about
    /// them.
    /// </remarks>
    private async Task ParkAsync(long postingId, ParkReason reason)
    {
        await using var db = CreateContext();

        db.Submissions.Add(new SubmissionEntity
        {
            ProfileId = ProfileId,
            PostingId = postingId,
            Channel = SubmissionChannel.Unknown,
            CreatedAtUtc = Now,
            ParkedReason = reason,
            ParkedAtUtc = Now,
        });

        await db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------------
    // One live question per wording
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Asking_the_same_question_from_two_adverts_queues_it_once()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (first, created) = await repository.OpenAsync(
            ProfileId, Question, options: null, sensitive: false, postingId: 1, runId: null, Now);

        var (second, again) = await repository.OpenAsync(
            ProfileId, Question, options: null, sensitive: false, postingId: 2, runId: null, Now.AddMinutes(1));

        Assert.True(created);
        Assert.False(again);
        Assert.Equal(first.Id, second.Id);

        // The row keeps the advert that raised it. The second advert's waiting is recorded on its
        // own parked submission, which is where parking lives - not by a second queue entry that
        // would put the same question to a person twice.
        Assert.Equal(1, second.PostingId);
        Assert.Single(await repository.ListUnansweredAsync(ProfileId, limit: 50));
    }

    [Fact]
    public async Task Two_spellings_of_one_question_are_one_queue_entry()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (first, _) = await repository.OpenAsync(
            ProfileId, "Do you require sponsorship to work in the UK?", null, false, 1, null, Now);

        // Case, spacing and the trailing mark are typography rather than a different question,
        // and a queue that asked a person both is one nobody drains.
        var (second, again) = await repository.OpenAsync(
            ProfileId, "  do YOU require sponsorship to work in the uk  ", null, false, 2, null, Now);

        Assert.False(again);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task A_question_longer_than_the_column_is_keyed_by_what_was_stored()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var overlong = new string('a', FormAnswerLimits.MaxQuestionTextLength) + " and then some more";

        var (queued, _) = await repository.OpenAsync(ProfileId, overlong, null, false, 1, null, Now);

        // The stored text is the pre-image of the stored key. Hashing what was passed in would
        // file the row under a spelling of the question that appears nowhere in the table, and
        // nothing would ever find it again.
        Assert.Equal(FormAnswerLimits.MaxQuestionTextLength, queued.QuestionText.Length);
        Assert.Equal(QuestionKey.Hash(queued.QuestionText), queued.QuestionHash);
    }

    [Fact]
    public async Task The_database_refuses_a_second_unanswered_question_for_one_wording()
    {
        await using var db = CreateContext();
        await new OpenQuestionRepository(db).OpenAsync(ProfileId, Question, null, false, 1, null, Now);

        // Written round the repository deliberately. The repository's check turns the ordinary
        // second ask into an answer; the index is what holds when two runs race, and only the
        // index is a guarantee.
        await using var second = CreateContext();
        second.OpenQuestions.Add(new OpenQuestionEntity
        {
            ProfileId = ProfileId,
            PostingId = 2,
            QuestionText = Question,
            QuestionHash = QuestionKey.Hash(Question),
            AskedAtUtc = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_candidates_may_both_be_asked_the_same_question()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (_, mine) = await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);
        var (_, theirs) = await repository.OpenAsync(OtherProfileId, Question, null, false, 1, null, Now);

        Assert.True(mine);
        Assert.True(theirs);
        Assert.Single(await repository.ListUnansweredAsync(ProfileId, limit: 50));
        Assert.Single(await repository.ListUnansweredAsync(OtherProfileId, limit: 50));
    }

    [Fact]
    public async Task The_choices_the_form_offered_are_stored_with_the_question()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        // Stored so a person is asked the question the form actually asked: answering "three
        // months" to a dropdown offering "3+ months" produces a value that cannot be typed in.
        var (select, _) = await repository.OpenAsync(
            ProfileId, "What is your notice period?", ["1-2 months", "3+ months"], false, 1, null, Now);

        var (free, _) = await repository.OpenAsync(
            ProfileId, Question, options: null, sensitive: true, postingId: 1, runId: null, Now);

        Assert.Equal(new[] { "1-2 months", "3+ months" }, select.Options);
        Assert.Empty(free.Options);
        Assert.True(free.Sensitive);
    }

    // -----------------------------------------------------------------------
    // Answering, and what it releases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Answering_a_question_takes_it_out_of_the_queue_and_leaves_it_in_the_table()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (question, _) = await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);

        var result = await repository.AnswerAsync(ProfileId, question.Id, AnswerId, Now.AddHours(2));

        Assert.Equal(OpenQuestionAnswerResult.Answered, result);
        Assert.Empty(await repository.ListUnansweredAsync(ProfileId, limit: 50));

        // Closed, never deleted: what was asked survives being answered, the way every other
        // record in this pipeline does.
        var stored = await repository.GetAsync(ProfileId, question.Id);

        Assert.NotNull(stored);
        Assert.False(stored.IsOpen);
        Assert.Equal(Now.AddHours(2), stored.AnsweredAtUtc);
        Assert.Equal(AnswerId, stored.AnswerId);
    }

    [Fact]
    public async Task An_answered_question_stops_holding_its_advert_back()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (question, _) = await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);

        // This read is what a MissingAnswer park is held by. If answering did not empty it, the
        // posting would be parked for good on a question that has already been answered.
        Assert.Single(await repository.ListUnansweredForPostingAsync(ProfileId, 1));

        await repository.AnswerAsync(ProfileId, question.Id, AnswerId, Now.AddHours(2));

        Assert.Empty(await repository.ListUnansweredForPostingAsync(ProfileId, 1));
    }

    /// <summary>An advert parked on a question another advert raised can still say what it waits on.</summary>
    /// <remarks>
    /// <b>The read has to answer the same question the queue predicate answers.</b> One
    /// unanswered row per wording means the second advert to ask gets the row naming the first,
    /// so <c>ListApplyableAsync</c> cannot hold a park on <i>its</i> question and holds it while
    /// any answer is outstanding. A read still scoped to the questions this advert raised would
    /// answer nothing for exactly the postings the queue is holding - a park with no visible
    /// reason, which is the state somebody opens the dashboard to escape.
    /// </remarks>
    [Fact]
    public async Task A_second_advert_parked_on_a_shared_question_is_told_what_it_is_waiting_for()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (question, _) = await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);
        var (converged, created) = await repository.OpenAsync(
            ProfileId, Question, null, false, 2, null, Now.AddMinutes(1));

        Assert.False(created);
        Assert.Equal(question.Id, converged.Id);

        await ParkAsync(2, ParkReason.MissingAnswer);

        var waiting = Assert.Single(await repository.ListUnansweredForPostingAsync(ProfileId, 2));

        // The row still names the advert that raised it, which is right - that is the context a
        // person needs to answer it - and posting 2 is waiting on it all the same.
        Assert.Equal(question.Id, waiting.Id);
        Assert.Equal(1, waiting.PostingId);

        await repository.AnswerAsync(ProfileId, question.Id, AnswerId, Now.AddHours(2));

        Assert.Empty(await repository.ListUnansweredForPostingAsync(ProfileId, 2));
    }

    [Fact]
    public async Task An_advert_nobody_parked_sees_only_the_questions_it_raised()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);

        // The widening is bounded by the park, not by the queue being non-empty. An advert
        // nothing has stopped is waiting on nothing, and saying otherwise would put somebody
        // else's question on every posting in the pipeline.
        Assert.Empty(await repository.ListUnansweredForPostingAsync(ProfileId, 2));
        Assert.Single(await repository.ListUnansweredForPostingAsync(ProfileId, 1));
    }

    [Fact]
    public async Task A_question_asked_from_the_dashboard_holds_no_advert_back()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        await repository.OpenAsync(
            ProfileId, Question, null, false, postingId: null, runId: null, Now);

        // It is in the queue for a person to answer, and it suppresses nothing: a question nobody
        // raised on behalf of an advert is not what any advert is waiting for.
        Assert.Single(await repository.ListUnansweredAsync(ProfileId, limit: 50));
        Assert.Empty(await repository.ListUnansweredForPostingAsync(ProfileId, 1));
    }

    [Fact]
    public async Task A_dismissed_question_closes_without_an_answer_and_does_not_come_back()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (question, _) = await repository.OpenAsync(ProfileId, Question, null, true, 1, null, Now);

        // "I am not answering that" is a reply. It has to close the question, or the person is
        // asked the same thing on every run for the rest of time.
        var result = await repository.AnswerAsync(ProfileId, question.Id, answerId: null, Now.AddHours(1));

        Assert.Equal(OpenQuestionAnswerResult.Answered, result);

        var stored = await repository.GetAsync(ProfileId, question.Id);

        Assert.NotNull(stored);
        Assert.Equal(Now.AddHours(1), stored.AnsweredAtUtc);
        Assert.Null(stored.AnswerId);
        Assert.Empty(await repository.ListUnansweredAsync(ProfileId, limit: 50));
    }

    [Fact]
    public async Task Answering_a_question_twice_keeps_the_first_answer()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (question, _) = await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);
        await repository.AnswerAsync(ProfileId, question.Id, AnswerId, Now.AddHours(1));

        var again = await repository.AnswerAsync(ProfileId, question.Id, answerId: null, Now.AddHours(5));

        // AlreadyClosed rather than Answered, and nothing rewritten: this row records that
        // somebody was asked and when they replied, and a second write would erase it.
        Assert.Equal(OpenQuestionAnswerResult.AlreadyClosed, again);

        var stored = await repository.GetAsync(ProfileId, question.Id);

        Assert.NotNull(stored);
        Assert.Equal(Now.AddHours(1), stored.AnsweredAtUtc);
        Assert.Equal(AnswerId, stored.AnswerId);
    }

    [Fact]
    public async Task A_question_answered_once_may_be_asked_again_later()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (first, _) = await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);
        await repository.AnswerAsync(ProfileId, first.Id, AnswerId, Now.AddHours(1));

        // The uniqueness is over unanswered rows only. A question that stopped being answerable
        // from store - the candidate superseded the answer, say - can be raised again, and the
        // history of the first asking is still there beside it.
        var (second, created) = await repository.OpenAsync(ProfileId, Question, null, false, 2, null, Now.AddDays(7));

        Assert.True(created);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await db.OpenQuestions.CountAsync());
        Assert.Single(await repository.ListUnansweredAsync(ProfileId, limit: 50));
    }

    [Fact]
    public async Task Recording_an_answer_closes_the_question_it_answers()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (question, _) = await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);

        // The write path's own drain: keyed on the hash the stored answer already carries, so
        // recording an answer and clearing the queue entry it settles are one act.
        var closed = await repository.AnswerByHashAsync(
            ProfileId, QuestionKey.Hash(Question), AnswerId, Now.AddHours(3));

        Assert.NotNull(closed);
        Assert.Equal(question.Id, closed.Id);
        Assert.Equal(AnswerId, closed.AnswerId);
        Assert.Empty(await repository.ListUnansweredAsync(ProfileId, limit: 50));
    }

    [Fact]
    public async Task An_answer_nobody_asked_for_closes_nothing_and_is_not_an_error()
    {
        await using var db = CreateContext();

        // A candidate volunteering an answer is ordinary. Null says "there was nothing queued",
        // which is the explanatory absence this surface prefers to a refusal.
        var closed = await new OpenQuestionRepository(db).AnswerByHashAsync(
            ProfileId, QuestionKey.Hash(Question), AnswerId, Now);

        Assert.Null(closed);
    }

    // -----------------------------------------------------------------------
    // The authorisation boundary
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_stranger_can_neither_read_nor_answer_the_queue()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (question, _) = await repository.OpenAsync(ProfileId, Question, null, true, 1, null, Now);

        var refused = await repository.AnswerAsync(OtherProfileId, question.Id, OtherAnswerId, Now);

        // NotFound rather than a partial answer: a caller cannot tell "no such question" from
        // "not yours", which is what stops the id space being probeable. It matters more here
        // than elsewhere - the answer to a queued question is often the most sensitive thing
        // this system holds.
        Assert.Equal(OpenQuestionAnswerResult.NotFound, refused);
        Assert.Null(await repository.GetAsync(OtherProfileId, question.Id));
        Assert.Empty(await repository.ListUnansweredAsync(OtherProfileId, limit: 50));
        Assert.Single(await repository.ListUnansweredAsync(ProfileId, limit: 50));
    }

    [Fact]
    public async Task A_question_cannot_be_closed_with_somebody_elses_answer()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (question, _) = await repository.OpenAsync(ProfileId, Question, null, false, 1, null, Now);

        var refused = await repository.AnswerAsync(ProfileId, question.Id, OtherAnswerId, Now.AddHours(1));

        // The foreign key would have taken it - it names a real row - and the dashboard would
        // then show one person another person's answer as their own.
        Assert.Equal(OpenQuestionAnswerResult.NoSuchAnswer, refused);
        Assert.Single(await repository.ListUnansweredAsync(ProfileId, limit: 50));
    }

    // -----------------------------------------------------------------------
    // The queue as a queue
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_queue_is_drained_oldest_first_and_the_bound_is_applied_after_the_order()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        await repository.OpenAsync(ProfileId, "Question one?", null, false, 1, null, Now);
        await repository.OpenAsync(ProfileId, "Question two?", null, false, 2, null, Now.AddHours(1));
        await repository.OpenAsync(ProfileId, "Question three?", null, false, 3, null, Now.AddHours(2));

        var page = await repository.ListUnansweredAsync(ProfileId, limit: 2);

        // Oldest first, which inverts every other list here: those are histories and this is a
        // queue. The question that has been holding an application back longest is the one to
        // put in front of somebody.
        Assert.Equal(new[] { "Question one?", "Question two?" }, page.Select(q => q.QuestionText));
    }

    [Fact]
    public async Task The_advert_that_raised_a_question_is_named_on_it()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var run = await new RunRepository(db).StartAsync(ProfileId, Now);

        await repository.OpenAsync(ProfileId, Question, null, false, postingId: 2, runId: run.Id, Now);

        var queued = Assert.Single(await repository.ListUnansweredAsync(ProfileId, limit: 50));

        // A question with no context is one a person cannot safely answer, so the advert comes
        // back with it rather than as an id they would have to go and look up.
        Assert.Equal("Role 2", queued.PostingTitle);
        Assert.Equal("Company 2", queued.Company);
        Assert.Equal(run.Id, queued.RunId);
    }

    [Fact]
    public async Task The_count_and_the_list_agree_about_what_is_waiting()
    {
        await using var db = CreateContext();
        var repository = new OpenQuestionRepository(db);

        var (first, _) = await repository.OpenAsync(ProfileId, "Question one?", null, false, 1, null, Now);
        await repository.OpenAsync(ProfileId, "Question two?", null, false, 2, null, Now);
        await repository.AnswerAsync(ProfileId, first.Id, AnswerId, Now.AddHours(1));

        // The badge and the queue are two spellings of one number, and a badge that disagrees
        // with the list under it sends somebody looking for a question that is not there.
        Assert.Equal(1, await repository.CountUnansweredAsync(ProfileId));
        Assert.Single(await repository.ListUnansweredAsync(ProfileId, limit: 50));
    }
}
