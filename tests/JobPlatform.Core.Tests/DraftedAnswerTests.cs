using JobPlatform.Core.Applications;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The free text drafted per posting, and the column it survives in.
/// </summary>
/// <remarks>
/// Two kinds of test, because the type does two things. The catalogue tests are about the
/// boundary - what is in the list, what may never be, and that a canned answer is never an
/// assertion nothing checked - which is the same kind of test <c>FormFieldCatalogTests</c> is.
/// The serialisation tests are about a stored column outliving the build that wrote it: every
/// one of them is a shape somebody's row could already be in.
/// </remarks>
public sealed class DraftedAnswerTests
{
    /// <summary>
    /// The whole per-posting list, pinned.
    /// </summary>
    /// <remarks>
    /// Each entry costs output tokens on every generation and is prose a candidate may end up
    /// sending, so growing the list should be a red build and then a deliberate edit rather than
    /// a quiet widening - the rule the field allowlist runs under.
    /// </remarks>
    [Fact]
    public void The_drafted_questions_are_exactly_these()
    {
        Assert.Equal(
            [
                "Why do you want to work at this company?",
                "Why are you interested in this role?",
                "Which of our products or areas of work interests you most, and why?",
                "What makes you a good fit for this role?",
                "Is there anything else you would like us to know?",
            ],
            DraftedAnswerCatalog.PerPosting.Select(prompt => prompt.QuestionText));
    }

    [Fact]
    public void Every_drafted_question_tells_the_writer_what_to_ground_it_in()
    {
        // The guidance is the half that prevents the generic paragraph. A prompt carrying only a
        // question is an invitation to write the template this feature exists to avoid, and an
        // unbounded one is an invitation to overflow a form box.
        Assert.All(DraftedAnswerCatalog.PerPosting, prompt =>
        {
            Assert.False(string.IsNullOrWhiteSpace(prompt.Guidance));
            Assert.InRange(prompt.MaxWords, 50, 400);
        });
    }

    /// <summary>
    /// Nothing here asks a question only a person may answer.
    /// </summary>
    /// <remarks>
    /// The catalogue holds prose about an employer. Sponsorship, salary, notice period, health
    /// and every EEO question are answered where somebody typed them or not at all, and the point
    /// of drafting is that an answer arrives without being asked for - which is exactly what must
    /// not happen to those. A word-level check is crude and it is the one that fails loudly the
    /// day somebody adds "what are your salary expectations?" for convenience.
    /// </remarks>
    [Fact]
    public void No_drafted_question_asks_something_only_the_candidate_may_assert()
    {
        string[] forbidden =
        [
            "salary", "compensation", "sponsor", "visa", "right to work", "notice period",
            "date of birth", "gender", "ethnic", "disability", "criminal",
        ];

        var everything = string.Join(
            " ",
            DraftedAnswerCatalog.PerPosting.Select(prompt => prompt.QuestionText + " " + prompt.Guidance)
                .Concat(DraftedAnswerCatalog.StableAnswers().Select(answer => answer.QuestionText + " " + answer.Answer)));

        Assert.All(forbidden, word =>
            Assert.DoesNotContain(word, everything, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nothing_in_the_catalogue_is_novel()
    {
        // Novel is what arrives at fill time from a form nobody anticipated. A curated entry is by
        // construction not that, so an answer coming back Novel is a signal - a question worth
        // adding - and it stops being one the moment the catalogue can produce one.
        Assert.All(DraftedAnswerCatalog.StableAnswers("linkedin"), answer =>
            Assert.Equal(FreeTextCategory.StableFact, answer.Category));

        Assert.DoesNotContain(
            DraftedAnswerCatalog.StableAnswers("linkedin"),
            answer => answer.Category == FreeTextCategory.Novel);
    }

    [Fact]
    public void The_canned_referral_answer_names_the_board_the_posting_came_from()
    {
        // A form answer is an assertion. "LinkedIn" on an advert found on Indeed is a small lie in
        // a document somebody signs, and it is the kind nobody notices until an employer does.
        var indeed = Assert.Single(DraftedAnswerCatalog.StableAnswers("indeed"));

        Assert.Equal("How did you hear about us?", indeed.QuestionText);
        Assert.Equal("Indeed", indeed.Answer);
        Assert.Equal(FreeTextCategory.StableFact, indeed.Category);

        Assert.Equal("LinkedIn", Assert.Single(DraftedAnswerCatalog.StableAnswers("LinkedIn ")).Answer);
    }

    [Fact]
    public void A_board_this_build_cannot_spell_gets_no_canned_answer_rather_than_a_plausible_one()
    {
        // The corpus carries boards the configured search set does not offer. Answering for one of
        // them means either naming a board the posting did not come from or writing a scraper slug
        // into a covering note; abstaining is the only honest third option.
        Assert.Empty(DraftedAnswerCatalog.StableAnswers("glassdoor"));
        Assert.Empty(DraftedAnswerCatalog.StableAnswers("zip_recruiter"));
    }

    [Fact]
    public void With_no_board_named_the_canned_answer_falls_back_to_the_one_this_pipeline_runs_on()
    {
        // Distinct from an unrecognised board: the caller has not said, rather than having said
        // something we cannot spell. LinkedIn is measured rather than assumed - all 4,470 postings
        // of one recent week came from it.
        Assert.Equal("LinkedIn", Assert.Single(DraftedAnswerCatalog.StableAnswers()).Answer);
        Assert.Equal("LinkedIn", Assert.Single(DraftedAnswerCatalog.StableAnswers("   ")).Answer);
    }

    [Fact]
    public void Answers_round_trip_through_the_column()
    {
        DraftedAnswer[] answers =
        [
            new("Why do you want to work at this company?", "Because of the thing the advert said.",
                FreeTextCategory.PostingSpecific),
            new("How did you hear about us?", "LinkedIn", FreeTextCategory.StableFact),
        ];

        Assert.Equal(answers, DraftedAnswerCatalog.Deserialise(DraftedAnswerCatalog.Serialise(answers)));
    }

    [Fact]
    public void The_category_is_stored_by_name_so_a_renumbering_cannot_reinterpret_it()
    {
        // The numbering is an implementation detail and a member inserted later would shift it.
        // Every row already written would then claim a category nobody chose for it, with nothing
        // saying so - the same class of silent reinterpretation the concept keys avoid.
        var json = DraftedAnswerCatalog.Serialise(
            [new("Why this role?", "Because.", FreeTextCategory.PostingSpecific)]);

        Assert.Contains("PostingSpecific", json, StringComparison.Ordinal);
        Assert.Contains("questionText", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_category_written_as_a_number_by_an_older_build_still_reads()
    {
        // Tolerance in one direction only: names are written, both are read. A column written
        // before the converter went on is still a candidate's drafted answers.
        var answer = Assert.Single(DraftedAnswerCatalog.Deserialise(
            """[{"questionText":"Why this role?","answer":"Because.","category":2}]"""));

        Assert.Equal(FreeTextCategory.PostingSpecific, answer.Category);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("{\"answers\":[]}")]
    [InlineData("[1,2,3]")]
    [InlineData("""[{"questionText":"Why?","answer":"Because.","category":"invented"}]""")]
    public void Malformed_stored_json_reads_as_no_answers_rather_than_throwing(string? json)
    {
        // Stored JSON is history, not input. A candidate opening their own documents must not get
        // a 500 because one column was written by a build that has since changed shape.
        Assert.Empty(DraftedAnswerCatalog.Deserialise(json));
    }

    [Fact]
    public void An_answer_with_nothing_in_it_is_dropped_and_the_ones_around_it_survive()
    {
        // A blank answer typed into a form reads to an employer as an answer. Dropping the whole
        // payload for one bad entry would lose the good ones, which is a different failure and a
        // worse one - the pack is what somebody is about to apply with.
        var answers = DraftedAnswerCatalog.Deserialise(
            """
            [{"questionText":"","answer":"Orphaned.","category":"PostingSpecific"},
             {"questionText":"Why this role?","answer":"   ","category":"PostingSpecific"},
             {"questionText":"Why this company?","answer":"Because of the advert.","category":"PostingSpecific"},
             null]
            """);

        var kept = Assert.Single(answers);

        Assert.Equal("Why this company?", kept.QuestionText);
    }

    [Fact]
    public void A_category_this_build_cannot_name_drops_the_answer_rather_than_inventing_one()
    {
        // Zero is the case that matters: the enum starts at one so a defaulted or unset column
        // reads as "nothing was said" instead of quietly claiming StableFact, which is the
        // category that says an answer may be reused on every future application.
        Assert.Empty(DraftedAnswerCatalog.Deserialise(
            """[{"questionText":"Why this role?","answer":"Because.","category":0}]"""));

        Assert.Empty(DraftedAnswerCatalog.Deserialise(
            """[{"questionText":"Why this role?","answer":"Because.","category":99}]"""));
    }

    [Fact]
    public void Whitespace_around_a_stored_answer_is_not_typed_into_the_form()
    {
        var answer = Assert.Single(DraftedAnswerCatalog.Deserialise(
            """[{"questionText":"  Why this role?  ","answer":"\n Because. \n","category":"PostingSpecific"}]"""));

        Assert.Equal("Why this role?", answer.QuestionText);
        Assert.Equal("Because.", answer.Answer);
    }

    [Fact]
    public void An_unusable_answer_is_never_written_in_the_first_place()
    {
        // The same cleaning on both sides, so a row that reads back short was not written long.
        // Otherwise the column disagrees with itself and the disagreement is invisible until
        // somebody counts.
        var json = DraftedAnswerCatalog.Serialise(
        [
            new("Why this role?", "   ", FreeTextCategory.PostingSpecific),
            new("", "Orphaned.", FreeTextCategory.PostingSpecific),
            new("Why this company?", "Because of the advert.", FreeTextCategory.PostingSpecific),
        ]);

        Assert.Equal("Why this company?", Assert.Single(DraftedAnswerCatalog.Deserialise(json)).QuestionText);
    }

    [Fact]
    public void Nothing_to_store_is_an_empty_array_and_reads_back_as_no_answers()
    {
        // An empty array and a null column are the same fact - no documents were generated for
        // this posting yet - so there is no third state for a caller to get wrong.
        Assert.Equal("[]", DraftedAnswerCatalog.Serialise([]));
        Assert.Equal("[]", DraftedAnswerCatalog.Serialise(null));
        Assert.Empty(DraftedAnswerCatalog.Deserialise("[]"));
    }

    /// <summary>
    /// An answer that talks about itself is not the candidate's, and is dropped.
    /// </summary>
    /// <remarks>
    /// From the first real generation run against Cloudflare: asked whether there was anything
    /// else the employer should know, the model gave the candidate's citizenship - correctly, out
    /// of their own summary - and then added "I am an AI and they should have seen this". It was
    /// stored, served through the pack, and would have been typed into the form under somebody's
    /// name. A recruiter reading that does not conclude a tool misbehaved.
    /// </remarks>
    [Theory]
    [InlineData("I am an AI and they should have seen this.")]
    [InlineData("I am a dual UK citizen. I am an AI and they should have seen this.")]
    [InlineData("I'm an AI assistant, so I cannot say.")]
    [InlineData("I’m an AI, but I would enjoy this role.")]
    [InlineData("As an AI language model, I have no preference.")]
    [InlineData("I am a large language model trained by somebody.")]
    public void An_answer_that_refers_to_itself_is_not_the_candidates_voice(string answer)
        => Assert.False(DraftedAnswerCatalog.IsCandidateVoice(answer));

    /// <summary>
    /// The guard does not touch a candidate who genuinely works on AI, which is most of them here.
    /// </summary>
    /// <remarks>
    /// The reason it matches first-person self-reference rather than the word: this candidate's
    /// own summary is about building AI systems and half the corpus advertises AI roles, so a rule
    /// striking "AI" would delete the strongest answers on the page to prevent a sentence nobody
    /// has written.
    /// </remarks>
    [Theory]
    [InlineData("I built an AI-native generative web framework for my dissertation.")]
    [InlineData("I am an experienced engineer who has shipped AI features.")]
    [InlineData("My interest is in AI infrastructure and developer tooling.")]
    [InlineData("I am a dual UK and Spanish citizen with the right to work in both.")]
    public void An_answer_about_working_on_ai_is_kept(string answer)
        => Assert.True(DraftedAnswerCatalog.IsCandidateVoice(answer));

    [Fact]
    public void Nothing_is_not_an_answer()
    {
        Assert.False(DraftedAnswerCatalog.IsCandidateVoice(null));
        Assert.False(DraftedAnswerCatalog.IsCandidateVoice("   "));
    }

}
