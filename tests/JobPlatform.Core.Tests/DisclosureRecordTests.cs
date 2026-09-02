using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// What the disclosure log is required to record.
/// </summary>
/// <remarks>
/// The factory is the only constructor precisely so these bounds cannot be skipped at a call
/// site, and there will be more call sites. These assert the part of that contract a compiler
/// cannot.
/// </remarks>
public sealed class DisclosureRecordTests
{
    private const string Subject = "22222222-2222-2222-2222-222222222222";
    private const string Actor = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// Whose data left and what took it are recorded separately.
    /// </summary>
    /// <remarks>
    /// An unattended client presents its own service principal and is mapped to a candidate, so
    /// collapsing the two would record that a CV was disclosed while losing what pulled it -
    /// which is the question an audit asks when the answer to the first was expected.
    /// </remarks>
    [Fact]
    public void The_subject_and_the_actor_are_both_recorded()
    {
        var record = DisclosureRecord.Create(
            DateTimeOffset.UtcNow, Subject, Actor, "get_form_field", "email", answered: true);

        Assert.Equal(Subject, record.SubjectId);
        Assert.Equal(Actor, record.ActorId);
    }

    /// <summary>
    /// A caller that cannot say what acted is refused rather than credited to the candidate.
    /// </summary>
    /// <remarks>
    /// Defaulting the actor to the subject would put a claim in the log that nothing checked,
    /// and it would read exactly like a person having asked for their own data.
    /// </remarks>
    [Fact]
    public void An_absent_actor_is_refused_rather_than_defaulted_to_the_subject()
        => Assert.Throws<ArgumentException>(() => DisclosureRecord.Create(
            DateTimeOffset.UtcNow, Subject, "  ", "get_form_field", "email", answered: true));

    [Fact]
    public void The_detail_is_bounded_so_a_caller_cannot_write_an_essay_into_the_log()
    {
        var record = DisclosureRecord.Create(
            DateTimeOffset.UtcNow, Subject, Actor, "get_submission_pack",
            new string('x', DisclosureRecord.MaxDetailChars * 2), answered: true);

        Assert.Equal(DisclosureRecord.MaxDetailChars, record.Detail.Length);
    }

    /// <summary>
    /// Two identical disclosures are two events.
    /// </summary>
    /// <remarks>
    /// The id is random rather than derived from the fields, so asking for the same field twice
    /// records twice. A deterministic id would silently overwrite one with the other, and "how
    /// often" is part of what this log exists to answer.
    /// </remarks>
    [Fact]
    public void Two_identical_disclosures_are_recorded_separately()
    {
        var at = DateTimeOffset.UtcNow;

        var first = DisclosureRecord.Create(at, Subject, Actor, "get_form_field", "email", true);
        var second = DisclosureRecord.Create(at, Subject, Actor, "get_form_field", "email", true);

        Assert.NotEqual(first.Id, second.Id);
    }
}
