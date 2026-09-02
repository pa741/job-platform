using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The declared-answer domain: what the candidate typed, and how a form's phrasing finds it.
/// </summary>
/// <remarks>
/// Two halves, and they fail in opposite directions. <see cref="QuestionKey"/> folding too
/// little costs one interruption - the candidate is asked something they have already answered.
/// Folding too much costs a false statement on somebody's application, because one question's
/// answer gets typed into another's form. So the normalisation tests come in pairs: what must
/// collide, and what must not.
///
/// <see cref="AnswerPrecedence"/> is tested against the version of the rule that is wrong -
/// specificity read before liveness, applicability left to the caller - because both of those
/// are what the obvious implementation does.
/// </remarks>
public sealed class FormAnswerTests
{
    private const string Question = "Do you require sponsorship to work in the UK?";

    // Written as code points rather than as literals so that what is being tested survives a
    // copy, an editor's autocorrect, and a diff viewer that renders all three the same.
    private const char CurlyApostrophe = (char)0x2019;
    private const char NonBreakingSpace = (char)0x00a0;
    private const char CombiningAcute = (char)0x0301;
    private const char PrecomposedEAcute = (char)0x00e9;

    private static DateTimeOffset At(int day) => new(2026, 9, day, 9, 0, 0, TimeSpan.Zero);

    private static FormAnswer Answer(
        AnswerScope scope,
        string value,
        int day = 1,
        int? companyId = null,
        long? postingId = null,
        DateTimeOffset? supersededAtUtc = null,
        long id = 0)
    {
        var answer = FormAnswer.Create(
            Question,
            value,
            scope,
            FormAnswerSource.Candidate,
            At(day),
            companyId: companyId,
            postingId: postingId);

        return answer with { Id = id, SupersededAtUtc = supersededAtUtc };
    }

    [Fact]
    public void The_two_sponsorship_phrasings_are_one_question()
    {
        // The pair the design names. A form writes the question with its capitals and its
        // question mark; a person types it back in lower case with neither, and the answer they
        // already gave has to be found.
        Assert.Equal(
            QuestionKey.Hash("Do you require sponsorship to work in the UK?"),
            QuestionKey.Hash("do you require sponsorship to work in the uk"));

        // Pinned as text as well as as a hash: the normalised form is stored beside the hash so
        // a miss can be read rather than guessed at, and a preimage nobody has ever looked at is
        // not one that can be relied on.
        Assert.Equal(
            "do you require sponsorship to work in the uk",
            QuestionKey.Normalise("  Do you require sponsorship to work in the UK?  "));
    }

    [Fact]
    public void Casing_and_spacing_are_typography_and_do_not_make_a_second_question()
    {
        var expected = QuestionKey.Hash("What is your notice period?");

        Assert.Equal(expected, QuestionKey.Hash("WHAT IS YOUR NOTICE PERIOD"));
        Assert.Equal(expected, QuestionKey.Hash("what   is your\tnotice\nperiod ?"));
        Assert.Equal(expected, QuestionKey.Hash("\r\n  What is your notice period?  \r\n"));
    }

    [Fact]
    public void A_trailing_question_mark_is_dropped()
    {
        // It falls out of punctuation folding rather than being a step of its own, which is
        // precisely why it is pinned: "already handled" is not something the next reader can see,
        // and a rule with no test is the one that gets optimised away.
        Assert.Equal(
            QuestionKey.Hash("Are you eligible to work in the UK"),
            QuestionKey.Hash("Are you eligible to work in the UK?"));
        Assert.Equal("are you eligible to work in the uk", QuestionKey.Normalise("Are you eligible to work in the UK???"));
    }

    [Fact]
    public void Punctuation_between_words_becomes_a_space_rather_than_vanishing()
    {
        // The case that decides the rule. Deleting the hyphen would give "fulltime" against
        // "full time" - two hashes for one question, which is the failure the fold exists to
        // prevent, arrived at by folding.
        Assert.Equal(
            QuestionKey.Hash("Are you looking for full-time work?"),
            QuestionKey.Hash("Are you looking for full time work?"));

        Assert.Equal(
            QuestionKey.Hash("Notice period (in weeks)"),
            QuestionKey.Hash("Notice period in weeks"));
    }

    [Fact]
    public void An_apostrophe_folds_whatever_shape_it_arrived_in()
    {
        // A question pasted out of a document and the same question typed into a browser differ
        // by one code point that renders identically. It is inside a word rather than between
        // two, so it is dropped and not spaced - otherwise "candidate s notice period".
        var typed = "What is the candidate's notice period?";
        var pasted = $"What is the candidate{CurlyApostrophe}s notice period?";

        Assert.Equal(QuestionKey.Hash(typed), QuestionKey.Hash(pasted));
        Assert.Equal("what is the candidates notice period", QuestionKey.Normalise(pasted));
    }

    [Fact]
    public void Unicode_that_renders_the_same_hashes_the_same()
    {
        // Composition first, and the reason is worse than an ordinary miss: a combining acute is
        // not a letter, so the decomposed spelling would fold to "caf e" - a space driven into
        // the middle of a word by a character nobody can see.
        var precomposed = $"Have you worked in a caf{PrecomposedEAcute}";
        var decomposed = $"Have you worked in a cafe{CombiningAcute}";

        Assert.Equal(QuestionKey.Hash(precomposed), QuestionKey.Hash(decomposed));
        Assert.DoesNotContain("caf e", QuestionKey.Normalise(decomposed));

        // A non-breaking space arrives from every form rendered as HTML and is not the space a
        // person typed.
        Assert.Equal(
            QuestionKey.Hash("Do you require sponsorship"),
            QuestionKey.Hash($"Do you require{NonBreakingSpace}sponsorship"));
    }

    [Fact]
    public void A_leading_article_is_dropped_and_the_interior_ones_are_kept()
    {
        Assert.Equal(
            QuestionKey.Hash("Notice period you must give"),
            QuestionKey.Hash("The notice period you must give"));

        Assert.Equal("visa type you hold", QuestionKey.Normalise("A visa type you hold"));
        Assert.Equal("apprenticeship you completed", QuestionKey.Normalise("An apprenticeship you completed"));

        // Deliberately not folded, and the asymmetry decides it: an interior article might be
        // ornamental and might be load-bearing, and this function cannot tell. A missed merge
        // asks the candidate one extra question; a wrong merge answers a question they were not
        // asked.
        Assert.NotEqual(QuestionKey.Hash("Do you need a visa"), QuestionKey.Hash("Do you need visa"));
    }

    [Fact]
    public void Genuinely_different_questions_do_not_collide()
    {
        var sponsorship = QuestionKey.Hash(Question);

        // One country apart, one negation apart, one qualifier apart. Each of these is a
        // question a form asks alongside the others, and each answer is wrong on the other.
        Assert.NotEqual(sponsorship, QuestionKey.Hash("Do you require sponsorship to work in the US?"));
        Assert.NotEqual(sponsorship, QuestionKey.Hash("Do you not require sponsorship to work in the UK?"));
        Assert.NotEqual(sponsorship, QuestionKey.Hash("Will you require sponsorship in future to work in the UK?"));

        Assert.NotEqual(
            QuestionKey.Hash("Do you hold a driving licence?"),
            QuestionKey.Hash("Do you hold a HGV driving licence?"));
    }

    [Fact]
    public void A_hash_is_sixty_four_lowercase_hex_characters()
    {
        var hash = QuestionKey.Hash(Question);

        // The width is the column, so it is asserted against the constant the column is declared
        // from rather than against the number 64 written twice.
        Assert.Equal(FormAnswerLimits.QuestionHashLength, hash.Length);
        Assert.All(hash, ch => Assert.Contains(ch, "0123456789abcdef"));
    }

    [Fact]
    public void An_empty_question_normalises_to_nothing_rather_than_throwing()
    {
        // The guard that matters is in Create, which refuses to store one. A normaliser that
        // threw would put a null check in front of every call site to prevent something no call
        // site can act on.
        Assert.Equal(string.Empty, QuestionKey.Normalise(null));
        Assert.Equal(string.Empty, QuestionKey.Normalise("   "));
        Assert.Equal(string.Empty, QuestionKey.Normalise("?!  -"));
        Assert.Equal(FormAnswerLimits.QuestionHashLength, QuestionKey.Hash(null).Length);
    }

    [Fact]
    public void Options_key_the_same_whatever_order_they_were_listed_in()
    {
        // The same dropdown re-rendered with its choices shuffled is the same question, and a
        // cache that missed on it would buy a model call to reach the answer it already had.
        Assert.Equal(
            QuestionKey.OptionsHash(["Yes", "No", "Prefer not to say"]),
            QuestionKey.OptionsHash(["Prefer not to say", "No", "Yes"]));

        // Case and punctuation fold here for the same reason they fold in a question, and a
        // repeated choice is one choice.
        Assert.Equal(
            QuestionKey.OptionsHash(["Yes", "No"]),
            QuestionKey.OptionsHash(["yes.", "NO", "Yes"]));
    }

    [Fact]
    public void Where_the_options_split_still_matters()
    {
        // Both sort to the same three words, so joining on a space would make these one option
        // set. The separator is a character the normaliser cannot emit, which is what keeps the
        // boundaries in the hash.
        Assert.NotEqual(
            QuestionKey.OptionsHash(["b", "a c"]),
            QuestionKey.OptionsHash(["a", "c b"]));
    }

    [Fact]
    public void Nothing_worth_keying_on_is_null_rather_than_a_hash_of_emptiness()
    {
        // Null is what the cache column holds for "no options", and a free-text question wants
        // that rather than a real-looking hash every empty option set would share.
        Assert.Null(QuestionKey.OptionsHash(null));
        Assert.Null(QuestionKey.OptionsHash([]));
        Assert.Null(QuestionKey.OptionsHash(["", "   ", "?"]));
    }

    [Fact]
    public void Create_derives_the_hash_from_the_question_it_stores()
    {
        // The two columns are written together by one function so they cannot disagree. A hash
        // computed at one call site and the text stored by another is the drift this prevents.
        var answer = FormAnswer.Create(
            "  Do you require sponsorship to work in the UK?  ",
            "  No  ",
            AnswerScope.Global,
            FormAnswerSource.Client,
            At(1),
            name: "  sponsorship_required  ");

        Assert.Equal("Do you require sponsorship to work in the UK?", answer.QuestionText);
        Assert.Equal("do you require sponsorship to work in the uk", answer.NormalisedQuestion);
        Assert.Equal(QuestionKey.Hash(Question), answer.QuestionHash);
        Assert.Equal("No", answer.Value);
        Assert.Equal("sponsorship_required", answer.Name);
        Assert.Equal(FormAnswerSource.Client, answer.Source);
        Assert.True(answer.IsLive);
    }

    [Fact]
    public void A_name_nobody_gave_is_null_rather_than_an_empty_string()
    {
        // Resolution falls back to the name when the hash misses. An empty string is a key that
        // every unnamed answer would share.
        Assert.Null(FormAnswer.Create(Question, "No", AnswerScope.Global, FormAnswerSource.Candidate, At(1)).Name);
        Assert.Null(FormAnswer.Create(Question, "No", AnswerScope.Global, FormAnswerSource.Candidate, At(1), name: "   ").Name);
    }

    [Fact]
    public void A_scope_and_its_id_are_validated_together()
    {
        // A company-scoped answer with no company applies to every employer, which is the "why
        // do you want to work here" failure with the safety taken out; a global answer carrying
        // a posting id looks scoped in the database and is not.
        Assert.Throws<ArgumentException>(() =>
            FormAnswer.Create(Question, "No", AnswerScope.Company, FormAnswerSource.Candidate, At(1)));

        Assert.Throws<ArgumentException>(() =>
            FormAnswer.Create(Question, "No", AnswerScope.Posting, FormAnswerSource.Candidate, At(1)));

        Assert.Throws<ArgumentException>(() =>
            FormAnswer.Create(Question, "No", AnswerScope.Global, FormAnswerSource.Candidate, At(1), postingId: 7));

        Assert.Throws<ArgumentException>(() =>
            FormAnswer.Create(Question, "No", AnswerScope.Company, FormAnswerSource.Candidate, At(1), companyId: 3, postingId: 7));

        var company = FormAnswer.Create(Question, "No", AnswerScope.Company, FormAnswerSource.Candidate, At(1), companyId: 3);
        var posting = FormAnswer.Create(Question, "No", AnswerScope.Posting, FormAnswerSource.Candidate, At(1), postingId: 7);

        Assert.Equal(3, company.CompanyId!.Value);
        Assert.Null(company.PostingId);
        Assert.Equal(7L, posting.PostingId!.Value);
        Assert.Null(posting.CompanyId);
    }

    [Fact]
    public void Nothing_is_not_an_answer_and_prefer_not_to_say_is_one()
    {
        // Storing whitespace would tell every later resolution that this question is settled.
        Assert.Throws<ArgumentException>(() =>
            FormAnswer.Create(Question, "   ", AnswerScope.Global, FormAnswerSource.Candidate, At(1)));

        Assert.Throws<ArgumentException>(() =>
            FormAnswer.Create("  ", "No", AnswerScope.Global, FormAnswerSource.Candidate, At(1)));

        // A refusal to answer is a value like any other, stored as typed and marked sensitive so
        // it is confirmed rather than inferred from.
        var declined = FormAnswer.Create(
            "What is your ethnic group?",
            "Prefer not to say",
            AnswerScope.Global,
            FormAnswerSource.Candidate,
            At(1),
            sensitive: true);

        Assert.Equal("Prefer not to say", declined.Value);
        Assert.True(declined.Sensitive);
    }

    [Fact]
    public void An_over_long_answer_is_refused_rather_than_truncated()
    {
        // The bound exists so the column and the validation are one decision. Truncating instead
        // would put half a sentence into somebody's application, where it reads as a statement
        // rather than as a bug - the failure SubmissionLimits was written after paying for.
        Assert.Throws<ArgumentException>(() => FormAnswer.Create(
            Question, new string('x', FormAnswerLimits.MaxValueLength + 1),
            AnswerScope.Global, FormAnswerSource.Candidate, At(1)));

        Assert.Throws<ArgumentException>(() => FormAnswer.Create(
            new string('x', FormAnswerLimits.MaxQuestionTextLength + 1), "No",
            AnswerScope.Global, FormAnswerSource.Candidate, At(1)));

        Assert.Throws<ArgumentException>(() => FormAnswer.Create(
            Question, "No", AnswerScope.Global, FormAnswerSource.Candidate, At(1),
            name: new string('x', FormAnswerLimits.MaxNameLength + 1)));

        // Exactly at the bound is a legal answer, not an off-by-one refusal.
        var atTheBound = FormAnswer.Create(
            Question, new string('x', FormAnswerLimits.MaxValueLength),
            AnswerScope.Global, FormAnswerSource.Candidate, At(1));

        Assert.Equal(FormAnswerLimits.MaxValueLength, atTheBound.Value.Length);
    }

    [Fact]
    public void An_answer_is_live_until_it_is_superseded()
    {
        var answer = Answer(AnswerScope.Global, "No");

        Assert.True(answer.IsLive);
        Assert.False((answer with { SupersededAtUtc = At(4) }).IsLive);

        // Superseding keeps the old row rather than editing it: an answer store that overwrites
        // cannot say what was submitted last year.
        Assert.Equal("No", (answer with { SupersededAtUtc = At(4) }).Value);
    }

    [Fact]
    public void The_narrowest_scope_that_applies_here_wins()
    {
        var global = Answer(AnswerScope.Global, "global");
        var company = Answer(AnswerScope.Company, "company", companyId: 3);
        var posting = Answer(AnswerScope.Posting, "posting", postingId: 7);

        Assert.Equal("posting", AnswerPrecedence.Best([global, company, posting], companyId: 3, postingId: 7)?.Value);
        Assert.Equal("company", AnswerPrecedence.Best([global, company], companyId: 3, postingId: 7)?.Value);
        Assert.Equal("global", AnswerPrecedence.Best([global], companyId: 3, postingId: 7)?.Value);
    }

    [Fact]
    public void An_answer_written_for_another_company_is_not_ranked_at_all()
    {
        // The failure this prevents is the one AnswerScope exists for, reintroduced one layer
        // up: a repository that fetched every answer with this hash holds the paragraph written
        // about a different employer, and ranking before filtering hands it over for being the
        // more specific of the two.
        var global = Answer(AnswerScope.Global, "global");
        var elsewhere = Answer(AnswerScope.Company, "written about someone else", companyId: 3);

        Assert.Equal("global", AnswerPrecedence.Best([global, elsewhere], companyId: 9)?.Value);
        Assert.Null(AnswerPrecedence.Best([elsewhere], companyId: 9));
    }

    [Fact]
    public void A_scoped_answer_is_not_used_when_nothing_was_asked_about()
    {
        // With no posting in hand a posting-scoped answer is not a weaker candidate, it is not a
        // candidate. Treating an absent context as a wildcard is how the "why this role"
        // paragraph reaches the wrong role.
        var posting = Answer(AnswerScope.Posting, "posting", postingId: 7);
        var company = Answer(AnswerScope.Company, "company", companyId: 3);

        Assert.Null(AnswerPrecedence.Best([posting, company]));
        Assert.Equal("company", AnswerPrecedence.Best([posting, company], companyId: 3)?.Value);
    }

    [Fact]
    public void A_live_answer_beats_a_superseded_one_however_specific_it_was()
    {
        // The rule that stops specificity resurrecting something the candidate retracted. Read
        // in the other order - narrowest first, liveness as a tie-break - the posting answer
        // wins here, which is the obvious implementation and is wrong.
        var live = Answer(AnswerScope.Global, "current", day: 5);
        var retracted = Answer(AnswerScope.Posting, "retracted", day: 1, postingId: 7, supersededAtUtc: At(4));

        Assert.Equal("current", AnswerPrecedence.Best([retracted, live], postingId: 7)?.Value);
    }

    [Fact]
    public void A_superseded_answer_is_returned_when_it_is_all_there_is()
    {
        // It is the last thing the person actually said, which beats a blank - and IsLive is on
        // the returned row so a caller filling a form can confirm it rather than type it.
        var older = Answer(AnswerScope.Global, "older", day: 1, supersededAtUtc: At(3));
        var newer = Answer(AnswerScope.Global, "newer", day: 2, supersededAtUtc: At(4));

        var best = AnswerPrecedence.Best([older, newer]);

        Assert.Equal("newer", best?.Value);
        Assert.False(best?.IsLive);
    }

    [Fact]
    public void The_most_recent_answer_wins_within_one_scope()
    {
        var older = Answer(AnswerScope.Global, "older", day: 1, id: 1);
        var newer = Answer(AnswerScope.Global, "newer", day: 6, id: 2);

        Assert.Equal("newer", AnswerPrecedence.Best([older, newer])?.Value);

        // Two answers written in the same tick still resolve to one of them deterministically,
        // so the result cannot depend on the order the database happened to return them in.
        var sameTick = Answer(AnswerScope.Global, "second", day: 6, id: 3);

        Assert.Equal("second", AnswerPrecedence.Best([sameTick, newer])?.Value);
        Assert.Equal("second", AnswerPrecedence.Best([newer, sameTick])?.Value);
    }

    [Fact]
    public void Order_of_the_input_does_not_change_the_answer()
    {
        var global = Answer(AnswerScope.Global, "global", day: 6, id: 1);
        var company = Answer(AnswerScope.Company, "company", day: 2, companyId: 3, id: 2);
        var posting = Answer(AnswerScope.Posting, "posting", day: 1, postingId: 7, id: 3);

        Assert.Equal("posting", AnswerPrecedence.Best([global, company, posting], 3, 7)?.Value);
        Assert.Equal("posting", AnswerPrecedence.Best([posting, global, company], 3, 7)?.Value);
        Assert.Equal("posting", AnswerPrecedence.Best([company, posting, global], 3, 7)?.Value);
    }

    [Fact]
    public void Nothing_applicable_answers_null_rather_than_something_arbitrary()
    {
        Assert.Null(AnswerPrecedence.Best([]));
        Assert.Null(AnswerPrecedence.Best([Answer(AnswerScope.Posting, "posting", postingId: 7)], postingId: 8));
    }

    [Fact]
    public void An_unrecognised_scope_applies_to_nothing()
    {
        // A member added later and not taught to the precedence rules must fail closed. Failing
        // open means it applies everywhere, which for an answer store means one candidate's
        // paragraph turning up under every employer.
        var future = Answer(AnswerScope.Global, "global") with { Scope = (AnswerScope)99 };

        Assert.False(AnswerPrecedence.Applies(future, companyId: 3, postingId: 7));
        Assert.Null(AnswerPrecedence.Best([future], 3, 7));
    }
}
