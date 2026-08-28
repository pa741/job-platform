using JobPlatform.Core.Ai;
using Xunit;

namespace JobPlatform.Data.Tests;

/// <summary>
/// The three rules that hold the stored prompt down, as pure logic.
/// </summary>
/// <remarks>
/// The rules live in the sink rather than at the call sites deliberately - four passes report to
/// this ledger and a fifth will, and a rule enforced in four places survives only until somebody
/// adds the fifth. That makes them worth asserting directly, because everything upstream is now
/// free to pass a prompt it should not have.
///
/// Mirrors <c>AiCallLogRepository.Sanitise</c>. It is private and needs a Cosmos container to
/// construct around, so the decision is restated here rather than reached through one.
/// </remarks>
public sealed class AiCallLedgerSanitiseTests
{
    private static AiCallRecord Record(AiCallOutcome outcome, string? prompt)
        => AiCallRecord.Create(
            DateTimeOffset.UtcNow,
            "candidacy-assessment",
            "bulk",
            outcome,
            requested: 10,
            returned: outcome == AiCallOutcome.Succeeded ? 10 : 4,
            durationMs: 100,
            reason: null,
            affectedIds: null,
            prompt: prompt);

    private static AiCallRecord Sanitise(AiCallRecord record, bool recordPrompts)
        => record.Prompt is null || (recordPrompts && record.Outcome != AiCallOutcome.Succeeded)
            ? record
            : record with { Prompt = null };

    [Fact]
    public void A_prompt_is_dropped_when_the_deployment_did_not_ask_for_one()
    {
        var stored = Sanitise(Record(AiCallOutcome.Failed, "CANDIDATE / employment history"), false);

        Assert.Null(stored.Prompt);
    }

    [Fact]
    public void A_successful_call_keeps_no_prompt_even_when_recording_is_on()
    {
        // There is nothing to reproduce, and successes are most of the calls - so this is where
        // most of the personal data would otherwise accrue.
        var stored = Sanitise(Record(AiCallOutcome.Succeeded, "CANDIDATE / employment history"), true);

        Assert.Null(stored.Prompt);
    }

    [Theory]
    [InlineData(AiCallOutcome.Failed)]
    [InlineData(AiCallOutcome.PartiallyDiscarded)]
    public void A_call_that_lost_something_keeps_its_prompt_when_asked(AiCallOutcome outcome)
    {
        var stored = Sanitise(Record(outcome, "CANDIDATE / employment history"), true);

        Assert.Equal("CANDIDATE / employment history", stored.Prompt);
    }

    [Fact]
    public void A_prompt_is_bounded_at_construction()
    {
        // A batch prompt carries the whole vocabulary plus ten adverts and is the largest thing
        // this system produces. Bounding it at Create means no call site can put megabytes into
        // a document store for a diagnostic.
        var record = Record(AiCallOutcome.Failed, new string('x', AiCallRecord.MaxPromptChars + 5_000));

        Assert.Equal(AiCallRecord.MaxPromptChars, record.Prompt!.Length);
    }
}
