using System.ComponentModel;
using System.Reflection;
using JobPlatform.Api.Features.Mcp;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using ModelContextProtocol.Server;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Everything written through this surface is a client's assertion, and nothing can make it the
/// candidate's own.
/// </summary>
/// <remarks>
/// <b>What a person asserted and what an agent inferred are different kinds of claim, and a log
/// that cannot tell them apart cannot be audited after one of them turns out to be wrong.</b> That
/// is the whole argument for <c>FormAnswerSource</c> and <c>SubmissionEventSource</c> existing at
/// all, and it survives exactly as long as no caller can choose which one is written.
///
/// <b>Three claims, and all three are needed.</b> The value written is <c>Client</c>; it is
/// <c>Client</c> for a delegated token as well as an app-only one, because a person starting a
/// client is not a person typing a sentence; and there is no argument anywhere on the surface
/// through which a caller could ask for the other value. The first two are behaviour and the third
/// is a signature - drop the third and a <c>source</c> parameter could be added tomorrow with
/// every behavioural test still green, because a test that never passes one would never see it.
///
/// <c>McpEndpointTests</c> asserts the same absence from the published JSON schema.
/// This asserts it from the method signatures, which is the half that catches a parameter renamed
/// by an attribute rather than removed.
/// </remarks>
public sealed class McpAnswerSourceTests
{
    private const string Question = "What is your notice period?";

    /// <summary>
    /// An answer recorded through a person's own client is still stamped <c>Client</c>.
    /// </summary>
    /// <remarks>
    /// <b>The case that looks wrong and is not.</b> A delegated token means somebody started this
    /// client, which is not the same as somebody having typed this sentence - the agent composed
    /// it, or read it off a page, or inferred it. <c>Candidate</c> is reachable only from the
    /// dashboard, where a person typed it into a box.
    /// </remarks>
    [Fact]
    public async Task An_answer_recorded_by_a_persons_own_client_is_stored_as_a_clients_assertion()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(), Question, "One month");

        var stored = await new FormAnswerRepository(harness.Database())
            .FindAsync(harness.ProfileId, Question, McpToolHarness.Now);

        Assert.NotNull(stored);
        Assert.Equal("One month", stored.Answer.Value);
        Assert.Equal(FormAnswerSource.Client, stored.Answer.Source);
    }

    /// <summary>And so is one recorded by an unattended client acting for that person.</summary>
    /// <remarks>
    /// The mapping says whose pipeline the principal acts on. It does not, and must not, say the
    /// principal speaks in their voice.
    /// </remarks>
    [Fact]
    public async Task An_answer_recorded_by_an_unattended_client_is_stored_the_same_way()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().RecordFormAnswerAsync(
            McpToolHarness.AsMappedApplication(), Question, "Two months");

        var stored = await new FormAnswerRepository(harness.Database())
            .FindAsync(harness.ProfileId, Question, McpToolHarness.Now);

        Assert.NotNull(stored);
        Assert.Equal(FormAnswerSource.Client, stored.Answer.Source);
    }

    /// <summary>
    /// Nothing this surface stores is ever stamped <c>Candidate</c>, whatever was asked for.
    /// </summary>
    /// <remarks>
    /// The word is sent in every argument that takes free text, which is what an attacker or a
    /// confused client would actually do: there is no <c>source</c> parameter, so the only way to
    /// try is to write it into the value, the name, the scope or the question. Reading every row
    /// afterwards is what makes "none of them" assertable rather than "not the one I checked".
    /// </remarks>
    [Fact]
    public async Task No_argument_carrying_the_word_candidate_can_make_an_answer_the_candidates_own()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var tools = harness.Tools();

        await tools.RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(), "Where did you hear about us?", "Candidate");

        await tools.RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(), "Candidate source?", "A friend", name: "Candidate");

        // A scope that is not a scope. Refused, and the point is that it is refused rather than
        // read as anything at all.
        Assert.True(McpToolHarness.Refusal(await tools.RecordFormAnswerAsync(
            McpToolHarness.AsCandidate(), "Preferred start date?", "March", "Candidate")).Refused);

        var rows = await new FormAnswerRepository(harness.Database())
            .ListAsync(harness.ProfileId, McpToolHarness.Now, includeSuperseded: true);

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(FormAnswerSource.Client, row.Answer.Source));
    }

    /// <summary>
    /// The events written by the two write paths carry <c>Client</c> too.
    /// </summary>
    /// <remarks>
    /// <b>Both paths, because there are two and they build the event separately.</b>
    /// <c>create_submission</c> inlines a <c>Submitted</c> event so there is no window in which an
    /// application exists in the world and not in the log; <c>record_event</c> appends every later
    /// claim. A source set correctly in one and defaulted in the other would show up only in the
    /// rows, months later, in a log that has no eraser.
    /// </remarks>
    [Fact]
    public async Task Every_event_written_through_this_surface_is_a_clients_claim()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var created = McpToolHarness.Read(await harness.Tools().CreateSubmissionAsync(
            McpToolHarness.AsCandidate(),
            McpToolHarness.WithDocuments,
            sent: true,
            idempotencyKey: "run-1:10:Submitted"));

        var submissionId = created.GetProperty("submissionId").GetInt64();

        await harness.Tools().RecordEventAsync(
            McpToolHarness.AsCandidate(), submissionId, "Acknowledged", "run-1:10:Acknowledged");

        var events = await new SubmissionRepository(harness.Database())
            .ListEventsAsync(harness.ProfileId, submissionId);

        Assert.Equal(2, events.Count);
        Assert.All(events, entry => Assert.Equal(SubmissionEventSource.Client, entry.Source));
    }

    /// <summary>
    /// No tool takes a source, by name or by type, anywhere on the surface.
    /// </summary>
    /// <remarks>
    /// <b>By type as well as by name, because a rename is the cheap way past a name check.</b> A
    /// parameter typed <c>FormAnswerSource</c> or <c>SubmissionEventSource</c> is a source
    /// whatever it is called, and a parameter <i>called</i> source is one whatever its type - so
    /// both are excluded and the intersection of the two rules is what a caller cannot express.
    ///
    /// It walks every method carrying <c>[McpServerTool]</c> rather than the one tool this file is
    /// named after, because the rule is a property of the surface. The next write tool is where it
    /// will be broken.
    /// </remarks>
    [Fact]
    public void No_tool_takes_a_source_by_name_or_by_type()
    {
        var offenders = typeof(SubmissionTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method.GetParameters().Select(parameter => (method, parameter)))
            .Where(entry =>
                entry.parameter.ParameterType == typeof(FormAnswerSource)
                || entry.parameter.ParameterType == typeof(SubmissionEventSource)
                || entry.parameter.Name?.Contains("source", StringComparison.OrdinalIgnoreCase) == true)
            .Select(entry => $"{entry.method.Name}({entry.parameter.Name})")
            .ToList();

        // applyUrlSource is the near miss this rule has to live beside: it names where a link came
        // from, which is a fact about a job board and not a claim about who said something. It is
        // excluded by name here rather than by loosening the rule, so the next parameter ending in
        // "Source" has to be argued for in a diff.
        Assert.Equal(["ListApplyableAsync(applyUrlSource)"], offenders);
    }

    /// <summary>
    /// And the tool says so in the description a model actually reads.
    /// </summary>
    /// <remarks>
    /// The absence of a parameter is invisible to a model looking for one. A client told to record
    /// "what the candidate said" will otherwise reach for the nearest field it can find - the name,
    /// or the answer text - so the description states the rule rather than leaving it to be
    /// inferred from a gap.
    /// </remarks>
    [Fact]
    public void Record_form_answer_says_in_its_description_that_it_cannot_speak_for_the_candidate()
    {
        var description = typeof(SubmissionTools)
            .GetMethod(nameof(SubmissionTools.RecordFormAnswerAsync))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

        Assert.Contains("no 'source' parameter", description, StringComparison.Ordinal);
        Assert.Contains("never as the candidate's own", description, StringComparison.Ordinal);
    }
}
