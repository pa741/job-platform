using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The rules B2 does not trust a model with: what a stored answer may become on a form, which
/// questions may only be answered by a person, and what a resolution is allowed to say.
/// </summary>
/// <remarks>
/// These fail in opposite directions and the tests come in pairs because of it. Mapping too little
/// costs one interruption - somebody picks a dropdown value they had already typed. Mapping too
/// much types a wrong notice period, a wrong salary band or a wrong right-to-work answer into a
/// real application, under somebody's name, where it reads as a statement they made. So every
/// "this must map" here is written next to the "this must not" it is one word away from.
/// </remarks>
public sealed class FormFieldResolutionTests
{
    private static readonly string[] NoticeOptions = ["Immediately", "2 weeks", "1 month", "3 months"];

    private static readonly string[] VagueNoticeOptions = ["Immediate", "Less than a month", "1-3 months"];

    private static FormAnswer Answer(string question, string value, bool sensitive = false)
        => FormAnswer.Create(
            question,
            value,
            AnswerScope.Global,
            FormAnswerSource.Candidate,
            new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
            sensitive: sensitive);

    [Fact]
    public void A_stored_answer_that_is_one_of_the_offered_options_maps_to_it()
    {
        // The design's own example, and the case that has to keep working: a notice period the
        // candidate has already typed, met by a dropdown that lists it.
        Assert.Equal("1 month", FormFieldPolicy.ForForm("1 month", NoticeOptions));
    }

    [Fact]
    public void A_stored_answer_no_option_says_is_refused_rather_than_rounded()
    {
        // The same answer against a dropdown that does not list it. "Less than a month" and
        // "1-3 months" are each a defensible guess and the difference between them is a fortnight
        // of somebody's life, so nothing here picks one.
        Assert.Null(FormFieldPolicy.ForForm("1 month", VagueNoticeOptions));
    }

    [Fact]
    public void The_option_is_returned_in_the_forms_own_spelling()
    {
        // A select takes the string it published. Handing back "yes" where the form offered "Yes"
        // is a field that silently fails to set, which afterwards reads as a wrong answer rather
        // than an absent one.
        Assert.Equal("Yes", FormFieldPolicy.ForForm("yes", ["Yes", "No"]));
    }

    [Fact]
    public void Typography_folds_when_matching_an_option_and_nothing_else_does()
    {
        // The same fold that decides two questions are one question - casing and punctuation, no
        // more - so a dropdown written "1 Month." still matches what the candidate typed.
        Assert.Equal("1 Month.", FormFieldPolicy.ForForm("1 month", ["1 Month.", "3 months"]));

        // And a word away is still a different answer.
        Assert.Null(FormFieldPolicy.ForForm("1 month", ["about 1 month", "3 months"]));
    }

    [Fact]
    public void Two_options_that_fold_together_are_an_ambiguity_rather_than_a_choice()
    {
        // A form offering both is broken; picking the first would be picking arbitrarily, and the
        // whole cost of refusing is one interruption on a form that is already wrong.
        Assert.Null(FormFieldPolicy.ForForm("1 month", ["1 month", "1 Month"]));
    }

    [Fact]
    public void A_free_text_box_takes_what_the_candidate_wrote()
    {
        Assert.Equal("Six years", FormFieldPolicy.ForForm("  Six years  ", null));
        Assert.Equal("Six years", FormFieldPolicy.ForForm("Six years", []));
    }

    [Fact]
    public void A_sensitive_answer_is_matched_verbatim_and_never_folded_onto_an_option()
    {
        // Verbatim or abstain. Case and surrounding whitespace are all that may differ, because a
        // near-miss on a right-to-work question is a false statement on an application - so the
        // punctuation fold that is safe everywhere else is withdrawn here.
        Assert.Equal("Yes", FormFieldPolicy.ForForm("yes", ["Yes", "No"], sensitive: true));
        Assert.Null(FormFieldPolicy.ForForm("Yes", ["Yes, I have the right to work", "No"], sensitive: true));

        // The same pair without the flag shows it is the flag doing the work.
        Assert.Equal("Yes.", FormFieldPolicy.ForForm("Yes", ["Yes.", "No"]));
        Assert.Null(FormFieldPolicy.ForForm("Yes", ["Yes.", "No"], sensitive: true));
    }

    [Fact]
    public void The_confidence_floor_is_cleared_by_a_person_agreeing_and_not_otherwise()
    {
        Assert.True(FormFieldPolicy.Meets(FormFieldPolicy.ConfidenceFloor));
        Assert.False(FormFieldPolicy.Meets(FormFieldPolicy.ConfidenceFloor - 0.01));

        // A person looked at the question and the answer together, which is strictly more than a
        // number the model reported about itself knows.
        Assert.True(FormFieldPolicy.Meets(0.1, confirmed: true));
    }

    [Fact]
    public void The_questions_only_a_person_may_answer_are_recognised_by_their_words()
    {
        Assert.True(SensitiveQuestions.Looks("Will you now or in the future require visa sponsorship?"));
        Assert.True(SensitiveQuestions.Looks("Do you have the right to work in the United Kingdom?"));
        Assert.True(SensitiveQuestions.Looks("What are your salary expectations?"));
        Assert.True(SensitiveQuestions.Looks("Please state your date of birth"));
        Assert.True(SensitiveQuestions.Looks("Do you consider yourself to have a disability?"));
        Assert.True(SensitiveQuestions.Looks("Have you ever been convicted of a criminal offence?"));
    }

    [Fact]
    public void A_word_that_merely_contains_a_listed_one_is_not_a_sensitive_question()
    {
        // The reason the match is over whole words. "Sussex" contains "sex", "manage" contains
        // "age" and "embrace" contains "race", and all three are ordinary things for a form to
        // say - a substring test would refuse to answer where somebody lives.
        Assert.False(SensitiveQuestions.Looks("Are you based in Sussex?"));
        Assert.False(SensitiveQuestions.Looks("How many people did you manage?"));
        Assert.False(SensitiveQuestions.Looks("Which technologies did you embrace first?"));
    }

    [Fact]
    public void A_notice_period_is_deliberately_not_sensitive()
    {
        // It is the design's own example of an answer that must map onto an option set, so listing
        // it would make that case unreachable. The line is drawn at what nobody may guess at:
        // immigration status, money, health, identity and record.
        Assert.False(SensitiveQuestions.Looks("What is your notice period?"));
        Assert.False(SensitiveQuestions.Looks("How did you hear about us?"));
        Assert.False(SensitiveQuestions.Looks("Do you hold a full UK driving licence?"));
    }

    [Fact]
    public void An_answer_guards_on_its_own_question_as_well_as_on_its_flag()
    {
        // The half that does not depend on a boolean being right. An answer to a right-to-work
        // question is never offered to a model whether or not anybody ticked the box - which is
        // what makes the driving-licence match unreachable rather than merely discouraged.
        Assert.True(SensitiveQuestions.Guards(Answer("Do you have the right to work in the UK?", "Yes")));
        Assert.True(SensitiveQuestions.Guards(Answer("Which office would you prefer?", "London", sensitive: true)));
        Assert.False(SensitiveQuestions.Guards(Answer("What is your notice period?", "1 month")));
    }

    [Fact]
    public void A_refusal_carries_no_value_and_names_no_field()
    {
        var refusal = FormFieldResolution.Ask(FormFieldStage.Model, "Ask the candidate.", 0.4);

        Assert.True(refusal.NeedsUser);
        Assert.Null(refusal.Value);
        Assert.Null(refusal.Field);
        Assert.Null(refusal.AnswerId);
        Assert.False(refusal.Sensitive);

        // The confidence survives rather than being zeroed: a model that reported 0.4 and was
        // refused by the floor is a different row from one that reported nothing at all, and the
        // difference is what says whether the floor is in the right place.
        Assert.Equal(0.4, refusal.Confidence);
    }

    [Fact]
    public void An_answer_carries_a_value_and_is_never_needsUser()
    {
        var answered = FormFieldResolution.Answered(
            FormFieldStage.DeclaredAnswer, "1 month", "Their own words.", 1, "notice_period", 7);

        Assert.False(answered.NeedsUser);
        Assert.Equal("1 month", answered.Value);
        Assert.Equal("notice_period", answered.Field);
        Assert.Equal(7, answered.AnswerId);
    }

    [Fact]
    public void Whether_a_model_was_called_is_read_off_the_stage_rather_than_stored_twice()
    {
        // The acceptance criterion is "the second occurrence resolves without a model call", and it
        // has to be assertable. A second field could disagree with the stage; a derived one cannot.
        Assert.False(FormFieldResolution.Ask(FormFieldStage.Cache, "Reused.").ConsultedModel);
        Assert.False(FormFieldResolution.Ask(FormFieldStage.None, "Nothing to ask.").ConsultedModel);
        Assert.True(FormFieldResolution.Ask(FormFieldStage.Model, "It would not commit.").ConsultedModel);
    }

    [Fact]
    public void A_confidence_outside_zero_to_one_is_clamped_rather_than_stored()
    {
        Assert.Equal(1, FormFieldResolution.Answered(FormFieldStage.Model, "Yes", "Why.", 4.2).Confidence);
        Assert.Equal(0, FormFieldResolution.Ask(FormFieldStage.Model, "Why.", -3).Confidence);
    }

    [Fact]
    public void A_rationale_is_bounded_to_the_column_that_stores_it()
    {
        var resolution = FormFieldResolution.Ask(FormFieldStage.Model, new string('x', 4_000));

        Assert.Equal(SubmissionLimits.MaxNoteLength, resolution.Rationale.Length);
    }
}
