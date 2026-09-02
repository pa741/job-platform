using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The declared-answer store and the resolution cache, against a real relational engine.
/// </summary>
/// <remarks>
/// Four things are pinned here, and none of them is a query plan.
///
/// <b>Superseding rather than overwriting</b>, in one transaction. An answer store that
/// overwrites cannot say what was submitted last year, and a replacement written as two saves
/// leaves a window in which the candidate has either two live answers to one question or none.
///
/// <b>Scope precedence including the context</b>, because applicability is part of precedence:
/// an answer written for one employer must not be offered to another, and a read that fetched
/// every row carrying a question's hash would be holding it. The filter is
/// <c>AnswerPrecedence.Applies</c> written a second time in SQL - there is no way to have one
/// spelling of it - so one test runs every stored answer past both and fails when they drift.
///
/// <b>The cache hit</b>, which is B2's acceptance criterion rather than an optimisation: the
/// second occurrence of a question must resolve without a model call. That is asserted with a
/// counter standing in for the model, because "it was fast" is not the claim.
///
/// <b>That nothing here can answer from the profile.</b> The candidate in this fixture has an
/// email address on their profile and the store will not produce it, because this table holds
/// only what somebody typed. That split is what makes a sensitive answer safe without depending
/// on a flag being set correctly, and it is worth a test that would fail if a convenience join
/// were ever added.
/// </remarks>
public sealed class FormAnswerStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private const long ProfileId = 1;
    private const long OtherProfileId = 2;

    private const int CompanyId = 10;
    private const int OtherCompanyId = 11;

    private const string ProfileEmail = "on-the-profile@example.invalid";

    private const string Question = "Do you require sponsorship to work in the UK?";

    /// <summary>The same question with the typography a second employer happens to use.</summary>
    /// <remarks>
    /// Casing, an ornamental article and a missing question mark. <c>QuestionKey</c> folds all
    /// three, so this must reach the answer stored under the wording above - if it stopped
    /// doing so, every assertion that uses it would still pass for the wrong reason, which is
    /// why one test asserts the fold on its own.
    /// </remarks>
    private const string SameQuestionReworded = "the Do you require sponsorship to work in the UK";

    public FormAnswerStoreTests()
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
                // The value the store must not be able to produce. It is a real column on a real
                // row, so a join added later would find it.
                Email = ProfileEmail,
                CreatedUtc = Now,
                UpdatedUtc = Now,
            });
        }

        foreach (var id in new[] { CompanyId, OtherCompanyId })
        {
            db.Companies.Add(new CompanyEntity
            {
                Id = id,
                CompanyKey = $"company {id}",
                DisplayName = $"Company {id}",
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
                LocationCity = "London",
                JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private JobsDbContext CreateContext() => new(_options);

    private static FormAnswer Answer(
        string value,
        AnswerScope scope = AnswerScope.Global,
        int? companyId = null,
        long? postingId = null,
        string? name = null,
        bool sensitive = false,
        FormAnswerSource source = FormAnswerSource.Candidate,
        DateTimeOffset? answeredAtUtc = null,
        string question = Question)
        => FormAnswer.Create(
            question,
            value,
            scope,
            source,
            answeredAtUtc ?? Now,
            name,
            companyId,
            postingId,
            sensitive);

    // -----------------------------------------------------------------------
    // Superseding, not overwriting
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Replacing_an_answer_supersedes_the_old_one_rather_than_overwriting_it()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        var first = await repository.RecordAsync(ProfileId, Answer("No", answeredAtUtc: Now.AddDays(-30)), Now);
        var second = await repository.RecordAsync(ProfileId, Answer("Yes"), Now);

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.NotEqual(first.Answer.Answer.Id, second.Answer.Answer.Id);

        // What the candidate would say now, and only that.
        var live = await repository.ListAsync(ProfileId, Now);
        Assert.Equal("Yes", Assert.Single(live).Answer.Value);

        // And what they said before, still readable. This is the whole argument for the column:
        // "what did I tell them" is the question somebody asks after an interview goes strangely.
        var history = await repository.ListAsync(ProfileId, Now, includeSuperseded: true);
        Assert.Equal(2, history.Count);

        var old = history.Single(a => a.Answer.Id == first.Answer.Answer.Id);
        Assert.False(old.Answer.IsLive);

        // Stamped with the replacement's timestamp rather than with the wall clock, so the two
        // rows are contiguous: the old answer stood until the new one was given.
        Assert.Equal(second.Answer.Answer.AnsweredAtUtc, old.Answer.SupersededAtUtc);
    }

    [Fact]
    public async Task The_supersede_and_the_insert_are_one_transaction()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(ProfileId, Answer("No"), Now);

        var saves = 0;
        db.SavedChanges += (_, _) => saves++;

        await repository.RecordAsync(ProfileId, Answer("Yes"), Now);

        // One save, so one transaction. Written as two, the gap between them holds either two
        // live answers to one question - which the filtered unique index would reject - or none,
        // which is worse: the next resolution reads a blank and interrupts somebody for an
        // answer they have already given.
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Recording_the_identical_answer_again_does_not_make_a_second_row()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        var first = await repository.RecordAsync(ProfileId, Answer("No"), Now);
        var again = await repository.RecordAsync(ProfileId, Answer("No"), Now.AddDays(1));

        // The ordinary shape of a retry, and of a run re-reading a form it has seen before.
        // Superseding a live answer with a copy of itself would leave the history a column of
        // duplicates with nothing to say.
        Assert.False(again.Created);
        Assert.Equal(first.Answer.Answer.Id, again.Answer.Answer.Id);
        Assert.Single(await repository.ListAsync(ProfileId, Now, includeSuperseded: true));
    }

    [Fact]
    public async Task A_candidate_re_asserting_what_a_client_said_is_recorded_as_a_change()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        var asserted = await repository.RecordAsync(
            ProfileId, Answer("No", source: FormAnswerSource.Client), Now);
        var confirmed = await repository.RecordAsync(
            ProfileId, Answer("No", source: FormAnswerSource.Candidate), Now.AddDays(1));

        // Same words, different claim. What a person asserted and what an agent inferred are
        // different things, and convergence must not quietly turn the second into the first.
        Assert.True(confirmed.Created);
        Assert.NotEqual(asserted.Answer.Answer.Id, confirmed.Answer.Answer.Id);
        Assert.Equal(FormAnswerSource.Candidate, confirmed.Answer.Answer.Source);
    }

    [Fact]
    public async Task An_answer_too_long_for_its_column_is_refused_rather_than_shortened()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        // Built by an initialiser, because FormAnswer.Create refuses this first. The worst case
        // of truncating is a half-sentence typed into somebody's application and sent to an
        // employer, where it reads as a statement rather than as a bug.
        var overlong = new FormAnswer
        {
            QuestionText = Question,
            QuestionHash = QuestionKey.Hash(Question),
            NormalisedQuestion = QuestionKey.Normalise(Question),
            Value = new string('x', FormAnswerLimits.MaxValueLength + 1),
            Scope = AnswerScope.Global,
            Source = FormAnswerSource.Candidate,
            AnsweredAtUtc = Now,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.RecordAsync(ProfileId, overlong, Now));

        Assert.Empty(await repository.ListAsync(ProfileId, Now, includeSuperseded: true));
    }

    // -----------------------------------------------------------------------
    // Scope precedence, in the context the question was asked in
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_answer_written_for_one_employer_is_never_offered_to_another()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(
            ProfileId, Answer("Because of their compiler work", AnswerScope.Company, companyId: CompanyId), Now);

        // Not a weaker candidate for the other employer - not a candidate. This is the single
        // most legible way for an application to announce that nobody read it.
        Assert.Null(await repository.FindAsync(ProfileId, Question, Now, companyId: OtherCompanyId));
        Assert.Null(await repository.FindAsync(ProfileId, Question, Now));

        var mine = await repository.FindAsync(ProfileId, Question, Now, companyId: CompanyId);
        Assert.Equal("Because of their compiler work", mine?.Answer.Value);
    }

    [Fact]
    public async Task The_narrowest_applicable_answer_wins()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(ProfileId, Answer("global"), Now);
        await repository.RecordAsync(
            ProfileId, Answer("company", AnswerScope.Company, companyId: CompanyId), Now);
        await repository.RecordAsync(
            ProfileId, Answer("posting", AnswerScope.Posting, postingId: 2), Now);

        Assert.Equal("global", (await repository.FindAsync(ProfileId, Question, Now))?.Answer.Value);
        Assert.Equal(
            "company",
            (await repository.FindAsync(ProfileId, Question, Now, companyId: CompanyId))?.Answer.Value);
        Assert.Equal(
            "posting",
            (await repository.FindAsync(ProfileId, Question, Now, CompanyId, postingId: 2))?.Answer.Value);

        // A posting the answer was not written for falls back rather than borrowing it.
        Assert.Equal(
            "company",
            (await repository.FindAsync(ProfileId, Question, Now, CompanyId, postingId: 3))?.Answer.Value);
    }

    [Fact]
    public async Task The_scope_filter_in_SQL_agrees_with_the_precedence_rule_in_Core()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(ProfileId, Answer("global"), Now);
        await repository.RecordAsync(
            ProfileId, Answer("company-10", AnswerScope.Company, companyId: CompanyId), Now);
        await repository.RecordAsync(
            ProfileId, Answer("company-11", AnswerScope.Company, companyId: OtherCompanyId), Now);
        await repository.RecordAsync(
            ProfileId, Answer("posting-2", AnswerScope.Posting, postingId: 2), Now);

        // Superseded on purpose: the rule that a live answer beats a retracted one whatever the
        // scope is part of what the two spellings have to agree about.
        await repository.RecordAsync(
            ProfileId, Answer("posting-1 (retracted)", AnswerScope.Posting, postingId: 1), Now.AddDays(-2));
        await repository.RecordAsync(
            ProfileId, Answer("posting-1", AnswerScope.Posting, postingId: 1), Now.AddDays(-1));

        var stored = (await repository.ListAsync(ProfileId, Now, includeSuperseded: true))
            .Select(a => a.Answer)
            .ToList();

        (int? Company, long? Posting)[] contexts =
        [
            (null, null),
            (CompanyId, null),
            (OtherCompanyId, null),
            (null, 1),
            (null, 2),
            (CompanyId, 1),
            (CompanyId, 2),
            (CompanyId, 3),
            (OtherCompanyId, 2),
            (99, 99),
        ];

        foreach (var (companyId, postingId) in contexts)
        {
            // Core over every stored answer, against the repository over the ones its WHERE
            // clause let through. The filter has to be written twice - a static call over a
            // column has no SQL - and a drift between them is invisible: an answer that stops
            // being offered is not something anybody notices.
            var expected = AnswerPrecedence.Best(stored, companyId, postingId);
            var actual = await repository.FindAsync(ProfileId, Question, Now, companyId, postingId);

            Assert.Equal(expected?.Id, actual?.Answer.Id);
        }
    }

    [Fact]
    public async Task A_retracted_answer_is_returned_when_it_is_all_there_is()
    {
        await using var db = CreateContext();

        // Written round the repository: superseding without a replacement is what the dashboard
        // does when somebody withdraws an answer, and the read has to keep working afterwards.
        db.FormAnswers.Add(new FormAnswerEntity
        {
            ProfileId = ProfileId,
            QuestionText = Question,
            QuestionHash = QuestionKey.Hash(Question),
            NormalisedQuestion = QuestionKey.Normalise(Question),
            Value = "No",
            Scope = AnswerScope.Global,
            Source = FormAnswerSource.Candidate,
            AnsweredAtUtc = Now.AddDays(-10),
            SupersededAtUtc = Now.AddDays(-1),
        });

        await db.SaveChangesAsync();

        var found = await new FormAnswerRepository(db).FindAsync(ProfileId, Question, Now);

        // The last thing the person actually said beats a blank - but it comes back flagged, so
        // a form-filling path treats it as grounds to confirm rather than to type.
        Assert.Equal("No", found?.Answer.Value);
        Assert.False(found?.Answer.IsLive);
    }

    [Fact]
    public async Task Two_wordings_of_one_question_reach_the_same_answer()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        var recorded = await repository.RecordAsync(ProfileId, Answer("No"), Now);
        var found = await repository.FindAsync(ProfileId, SameQuestionReworded, Now);

        // Typography and a leading article, which QuestionKey folds. Nothing beyond that is
        // folded, because a false merge is one question's answer typed into another's form.
        Assert.Equal(recorded.Answer.Answer.Id, found?.Answer.Id);
    }

    [Fact]
    public async Task An_answer_is_found_under_its_name_whatever_case_it_was_filed_in()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(
            ProfileId,
            Answer("Three months", name: "Notice_Period", question: "What is your notice period?"),
            Now);

        // The escape from phrasing: two employers wording a question differently produce two
        // hashes, and a name written once lets both resolve. Folded at both ends, because SQL
        // Server's collation would match this and SQLite's comparison would not - a difference
        // that would make the test suite and production disagree about what is stored.
        var found = await repository.FindByNameAsync(ProfileId, " notice_period ", Now);

        Assert.Equal("Three months", found?.Answer.Value);
        Assert.Null(await repository.FindByNameAsync(ProfileId, "salary_expectation", Now));
    }

    [Fact]
    public async Task Another_candidates_answers_are_invisible_rather_than_forbidden()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(OtherProfileId, Answer("Yes"), Now);

        Assert.Null(await repository.FindAsync(ProfileId, Question, Now));
        Assert.Empty(await repository.ListAsync(ProfileId, Now, includeSuperseded: true));

        // And the same question is answerable by both, independently - one live answer per
        // candidate, not per question.
        await repository.RecordAsync(ProfileId, Answer("No"), Now);
        Assert.Equal("No", (await repository.FindAsync(ProfileId, Question, Now))?.Answer.Value);
        Assert.Equal("Yes", (await repository.FindAsync(OtherProfileId, Question, Now))?.Answer.Value);
    }

    // -----------------------------------------------------------------------
    // Declared, never derived
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_question_the_profile_could_answer_is_unanswered_until_somebody_types_it()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        const string EmailQuestion = "What is your email address?";

        // The profile has one. This store cannot reach it, and that is the sensitive-data
        // guarantee rather than an oversight: an EEO question, a salary expectation or a date of
        // birth is not reachable from the profile at all, so a value of that kind can exist here
        // because somebody wrote it and nowhere else because there is nowhere else. A flag
        // marking catalogue fields sensitive would have converted "cannot be answered" into
        // "answered unless a boolean was right".
        Assert.Null(await repository.FindAsync(ProfileId, EmailQuestion, Now));
        Assert.Null(await repository.FindByNameAsync(ProfileId, "email", Now));

        await repository.RecordAsync(
            ProfileId,
            Answer("i-typed-this@example.invalid", name: "email", question: EmailQuestion),
            Now);

        var found = await repository.FindAsync(ProfileId, EmailQuestion, Now);

        // What comes back is what was typed, not what the profile holds.
        Assert.Equal("i-typed-this@example.invalid", found?.Answer.Value);
        Assert.NotEqual(ProfileEmail, found?.Answer.Value);
    }

    [Fact]
    public async Task A_sensitive_answer_is_stored_as_typed_and_comes_back_marked()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(
            ProfileId,
            Answer(
                "Prefer not to say",
                name: "ethnicity",
                sensitive: true,
                question: "Which of the following best describes your ethnic background?"),
            Now);

        var found = await repository.FindByNameAsync(ProfileId, "ethnicity", Now);

        // Verbatim. "Prefer not to say" is a stored value like any other, and the flag drives
        // redaction in the disclosure log rather than permission to infer something better.
        Assert.Equal("Prefer not to say", found?.Answer.Value);
        Assert.True(found?.Answer.Sensitive);
    }

    // -----------------------------------------------------------------------
    // The resolution cache
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_second_occurrence_of_a_question_resolves_without_a_model_call()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        var answer = await repository.RecordAsync(ProfileId, Answer("No"), Now);

        var modelCalls = 0;

        // Stands in for the resolver: cache first, model only on a miss. The acceptance
        // criterion is that this counter stops moving, not that the second call is quick.
        async Task<CachedResolution> ResolveAsync(string question, IReadOnlyList<string>? options)
        {
            var cached = await repository.GetResolutionAsync(ProfileId, question, options, Now);

            if (cached is not null)
            {
                return cached;
            }

            modelCalls++;

            return await repository.RecordResolutionAsync(
                ProfileId,
                new ResolutionOutcome(
                    question,
                    options,
                    0.94,
                    "The candidate has answered this question directly.",
                    AnswerId: answer.Answer.Answer.Id,
                    ResolvedName: "sponsorship",
                    Model: "gpt-4.1-mini"),
                Now);
        }

        var first = await ResolveAsync(Question, null);
        var second = await ResolveAsync(SameQuestionReworded, null);

        Assert.Equal(1, modelCalls);

        // A hit is self-sufficient: it carries the answer, so nothing downstream has to fetch
        // one by id - which is what a caller able to fetch somebody else's would look like.
        Assert.Equal(first.Answer?.Answer.Id, second.Answer?.Answer.Id);
        Assert.Equal("No", second.Answer?.Answer.Value);
        Assert.Equal("gpt-4.1-mini", second.Model);
        Assert.False(second.Abstained);
    }

    [Fact]
    public async Task An_abstention_is_cached_so_the_next_run_does_not_pay_to_rediscover_it()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordResolutionAsync(
            ProfileId,
            new ResolutionOutcome(
                Question,
                null,
                0.2,
                "Below the confidence floor; no stored answer covers this wording."),
            Now);

        var cached = await repository.GetResolutionAsync(ProfileId, Question, null, Now);

        // "We looked at this and would not answer it" is an outcome, not a gap. Without the row,
        // every run pays for the same refusal again.
        Assert.NotNull(cached);
        Assert.True(cached.Abstained);
        Assert.Equal(0.2, cached.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(cached.Rationale));
    }

    [Fact]
    public async Task A_question_asked_with_a_different_option_set_is_resolved_again()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordResolutionAsync(
            ProfileId,
            new ResolutionOutcome(Question, ["Yes", "No"], 0.9, "Mapped onto the two-option form."),
            Now);

        // The same question against a third choice can resolve differently and honestly, so one
        // row for both would serve the first form's answer to the second.
        Assert.Null(await repository.GetResolutionAsync(
            ProfileId, Question, ["Yes", "No", "Prefer not to say"], Now));

        // Order is the form's, not the question's: a re-rendered dropdown still hits.
        var reshuffled = await repository.GetResolutionAsync(ProfileId, Question, ["No", "Yes"], Now);
        Assert.Equal("Mapped onto the two-option form.", reshuffled?.Rationale);

        // And a free-text box is a different question again from a select.
        Assert.Null(await repository.GetResolutionAsync(ProfileId, Question, null, Now));
    }

    [Fact]
    public async Task Resolving_a_question_again_replaces_the_cached_outcome_rather_than_adding_one()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordResolutionAsync(
            ProfileId,
            new ResolutionOutcome(Question, null, 0.9, "First pass.", Confirmed: true),
            Now.AddDays(-1));

        await repository.RecordResolutionAsync(
            ProfileId, new ResolutionOutcome(Question, null, 0.4, "Second pass."), Now);

        Assert.Equal(1, await db.FormAnswerResolutions.CountAsync(r => r.ProfileId == ProfileId));

        var cached = await repository.GetResolutionAsync(ProfileId, Question, null, Now);

        Assert.Equal("Second pass.", cached?.Rationale);
        Assert.Equal(Now, cached?.ResolvedAtUtc);

        // A person agreed with the answer that was there, not with whatever replaced it.
        // Inheriting the flag would let a later model call arrive pre-approved.
        Assert.False(cached?.Confirmed);
    }

    [Fact]
    public async Task A_resolution_naming_another_candidates_answer_is_refused()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        var theirs = await repository.RecordAsync(OtherProfileId, Answer("Yes"), Now);

        // The only place an answer id comes from is a read on this class, and every read is
        // profile-scoped - so an id belonging to somebody else means the caller did not look.
        // The foreign key cannot catch this: it points at FormAnswers and knows nothing about
        // whose answer it is.
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.RecordResolutionAsync(
                ProfileId,
                new ResolutionOutcome(Question, null, 0.9, "Borrowed.", AnswerId: theirs.Answer.Answer.Id),
                Now));

        Assert.Empty(await db.FormAnswerResolutions.Where(r => r.ProfileId == ProfileId).ToListAsync());
    }

    [Fact]
    public async Task Another_candidates_cache_is_not_read()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordResolutionAsync(
            ProfileId, new ResolutionOutcome(Question, null, 0.9, "Mine."), Now);

        Assert.Null(await repository.GetResolutionAsync(OtherProfileId, Question, null, Now));
    }

    [Fact]
    public async Task A_confidence_outside_the_range_is_clamped_rather_than_stored()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        var recorded = await repository.RecordResolutionAsync(
            ProfileId, new ResolutionOutcome(Question, null, 4.2, "Arithmetic went wrong."), Now);

        // Stored raw, one row would outrank every honest resolution in any comparison written
        // later - the same reason the match path clamps polarity rather than storing it.
        Assert.Equal(1, recorded.Confidence);
    }

    // -----------------------------------------------------------------------
    // Staleness, which is reported and never acted on here
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_stale_answer_is_reported_and_not_deleted()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(
            ProfileId,
            Answer("£70,000", name: "salary_expectation", answeredAtUtc: Now.AddDays(-200)),
            Now);

        var stale = await repository.ListStaleAsync(ProfileId, TimeSpan.FromDays(180), Now);

        Assert.Equal("salary_expectation", Assert.Single(stale).Answer.Name);
        Assert.Equal(TimeSpan.FromDays(200), stale[0].Age);

        // Reported twice, because reporting changes nothing. Deleting on a timer would throw
        // away the history this table exists to keep, and which answers go off is a judgement
        // about the question - a salary expectation ages, an email address does not - which a
        // table of hashes is in no position to make.
        Assert.Single(await repository.ListStaleAsync(ProfileId, TimeSpan.FromDays(180), Now));
        Assert.Single(await repository.ListAsync(ProfileId, Now));
    }

    [Fact]
    public async Task A_recent_answer_and_a_replaced_one_are_both_left_alone()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(ProfileId, Answer("No", answeredAtUtc: Now.AddDays(-400)), Now);
        await repository.RecordAsync(ProfileId, Answer("Yes", answeredAtUtc: Now.AddDays(-2)), Now);

        // The old row is 400 days old and needs nothing: it has already been replaced, which is
        // what re-confirming an answer is for.
        Assert.Empty(await repository.ListStaleAsync(ProfileId, TimeSpan.FromDays(180), Now));
    }

    [Fact]
    public async Task The_age_of_an_answer_is_measured_against_the_clock_the_caller_passed()
    {
        await using var db = CreateContext();
        var repository = new FormAnswerRepository(db);

        await repository.RecordAsync(ProfileId, Answer("No", answeredAtUtc: Now.AddDays(-10)), Now);

        var read = await repository.FindAsync(ProfileId, Question, Now.AddDays(5));

        // Not DateTimeOffset.UtcNow anywhere in the repository: a clock reached for inside a
        // read is one no test can move, and every other read in this codebase takes it as an
        // argument for the same reason.
        Assert.Equal(TimeSpan.FromDays(15), read?.Age);
    }
}
