using System.Text.Json;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// What the reads may hand over, asserted as a shape rather than as a name.
/// </summary>
/// <remarks>
/// <b>This file exists because the surface test could not have caught B4 going wrong.</b>
/// <c>McpEndpointTests</c> asserts the tool list is exactly fourteen names, which is the right
/// assertion and is blind to the failure that actually threatens the no-whole-profile rule: nobody
/// adds a <c>get_profile</c> tool, they add a <c>profile</c> object to
/// <c>get_submission_pack</c> - a tool that already exists, already discloses, and already has a
/// green test. The name list stays fourteen and an employment history starts leaving the system
/// with nothing red.
///
/// So the payloads are pinned by key set, and pinned as an <b>equality</b> for the same reason the
/// tool list is. A superset assertion would let a field be added silently, which is precisely the
/// diff this is here to force somebody to write on purpose.
///
/// <b>Every profile value is checked against <c>FormFieldCatalog</c> by name.</b> That is the
/// stronger half: the pack could keep its key set exactly and still widen what
/// <c>profileFields</c> carries, because the catalogue is what bounds that list and a change there
/// is a change here. Repeated groups are expanded into individually named entries -
/// <c>work_history[0].employer</c> - which is what B4 ships as instead of a structure, so a
/// structure appearing in its place fails on the name and not on a guess about the shape.
///
/// <b>And the disclosures are read back.</b> A read of somebody's own data that is not recorded is
/// the audit failing quietly, and a record that carries the value has moved the problem rather than
/// solved it. Both are asserted here because the production log is Cosmos-backed and the API test
/// host removes it, so nothing else in this repository can see one.
/// </remarks>
public sealed class McpToolPayloadTests
{
    /// <summary>
    /// The pack's top-level shape, reviewed once and pinned here.
    /// </summary>
    /// <remarks>
    /// Sorted, so a reordering of the projection is not a failure and an addition is.
    /// </remarks>
    private static readonly string[] PackKeys =
    [
        "advertText",
        "applyUrl",
        "applyUrlSource",
        "atsVendor",
        "channel",
        "company",
        "coverLetterMarkdown",
        "curriculumVitaeMarkdown",
        "documentUrls",
        "draftedAnswers",
        "note",
        "postingId",
        "profileFields",
        "revision",
        "title",
    ];

    [Fact]
    public async Task The_submission_pack_returns_exactly_the_keys_it_was_reviewed_with()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments));

        Assert.Equal(PackKeys, McpToolHarness.Keys(pack));

        // The nested objects too, because "add a profile blob" is at least as likely to land
        // inside documentUrls or beside a drafted answer as at the top level.
        Assert.Equal(
            ["coverLetterPdf", "curriculumVitaeDocx", "curriculumVitaePdf", "cvSha256", "expiresInMinutes"],
            McpToolHarness.Keys(pack.GetProperty("documentUrls")));

        Assert.All(
            pack.GetProperty("draftedAnswers").EnumerateArray(),
            answer => Assert.Equal(["answer", "category", "questionText"], McpToolHarness.Keys(answer)));
    }

    /// <summary>
    /// The pack's profile half is named allowlist entries, and never a structure.
    /// </summary>
    /// <remarks>
    /// <b>Two assertions, and the second is the one that bites.</b> Every entry is a flat
    /// <c>{name, value}</c> pair, so a nested object cannot be smuggled in as one entry's value;
    /// and every name is one <c>FormFieldCatalog</c> publishes, so the catalogue stays the single
    /// list of what may leave. An employment history added to <c>CandidateProfile</c> and handed
    /// over as a structure fails both.
    /// </remarks>
    [Fact]
    public async Task The_submission_pack_carries_named_allowlist_entries_and_never_a_profile_object()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments));

        var fields = pack.GetProperty("profileFields").EnumerateArray().ToList();

        // Vacuous if the profile answered nothing, and the fixture is fully populated precisely
        // so this cannot pass by returning an empty list.
        Assert.NotEmpty(fields);

        Assert.All(fields, field =>
        {
            Assert.Equal(["name", "value"], McpToolHarness.Keys(field));
            Assert.Equal(JsonValueKind.String, field.GetProperty("value").ValueKind);
            Assert.Contains(field.GetProperty("name").GetString(), FormFieldCatalog.Names);
        });

        // The repeated groups arrive expanded and individually named, which is what B4 ships as
        // instead of a structure. A 'work_history' object would satisfy neither test above, and
        // this says which shape was expected rather than only that the wrong one failed.
        Assert.Contains(
            fields,
            field => field.GetProperty("name").GetString() == "work_history[0].employer");
    }

    /// <summary>
    /// A posting with nothing generated for it answers with an explanation, not an error.
    /// </summary>
    /// <remarks>
    /// The same key set as a pack that has documents, so a client parses one shape. Absence is
    /// reported in <c>note</c> and as nulls, because a missing key and a null are different things
    /// to a caller and only one of them is the truth here.
    /// </remarks>
    [Fact]
    public async Task A_pack_with_no_documents_keeps_its_shape_and_says_so_in_a_note()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.WithoutDocuments));

        Assert.Equal(PackKeys, McpToolHarness.Keys(pack));
        Assert.Equal(JsonValueKind.Null, pack.GetProperty("curriculumVitaeMarkdown").ValueKind);
        Assert.Contains("No documents have been generated", pack.GetProperty("note").GetString());
    }

    /// <summary>
    /// One field read answers with the name, the value and a note, and nothing else.
    /// </summary>
    /// <remarks>
    /// The narrowest surface in the system and the one most worth keeping narrow: it exists
    /// because there is no <c>get_profile</c>, so anything it returns beyond the field asked for
    /// is that tool being reinvented one property at a time.
    /// </remarks>
    [Fact]
    public async Task A_form_field_read_returns_the_name_the_value_and_nothing_else()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var answer = McpToolHarness.Read(
            await harness.Tools().GetFormFieldAsync(McpToolHarness.AsCandidate(), "email"));

        Assert.Equal(["name", "note", "value"], McpToolHarness.Keys(answer));
        Assert.Equal("email", answer.GetProperty("name").GetString());
        Assert.Equal("ada@example.invalid", answer.GetProperty("value").GetString());
    }

    /// <summary>
    /// Listing the allowlist carries nobody's data - it is names and descriptions.
    /// </summary>
    /// <remarks>
    /// The same fixed list for every candidate, which is why the tool does not log it. A value
    /// appearing here would be a disclosure nothing recorded.
    /// </remarks>
    [Fact]
    public async Task Listing_the_allowlist_returns_names_and_descriptions_and_no_values()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var listing = McpToolHarness.Read(
            await harness.Tools().GetFormFieldAsync(McpToolHarness.AsCandidate()));

        Assert.Equal(["fields"], McpToolHarness.Keys(listing));

        Assert.All(
            listing.GetProperty("fields").EnumerateArray(),
            field => Assert.Equal(["description", "name"], McpToolHarness.Keys(field)));

        Assert.Empty(harness.Disclosures.Records);
    }

    /// <summary>
    /// The batch read is the singular one in a list, entry for entry.
    /// </summary>
    /// <remarks>
    /// <b>A saving in round trips and never in audit.</b> If the batch could return a shape the
    /// singular tool cannot, it would be a second disclosure surface with its own rules - and the
    /// per-name refusal it carries is exactly the sort of thing that invites one.
    /// </remarks>
    [Fact]
    public async Task A_batch_form_field_read_returns_one_entry_shape_for_answers_and_refusals_alike()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var batch = McpToolHarness.Read(await harness.Tools().GetFormFieldsAsync(
            McpToolHarness.AsCandidate(), ["email", "phone", "salary_expectation"]));

        Assert.Equal(["items"], McpToolHarness.Keys(batch));

        var items = batch.GetProperty("items").EnumerateArray().ToList();

        Assert.All(items, item =>
            Assert.Equal(["name", "note", "reason", "refused", "value"], McpToolHarness.Keys(item)));

        // Not on the allowlist and never will be: the salary question is one only the candidate
        // may answer, so the batch refuses that entry and answers the other two.
        var refused = items.Single(item => item.GetProperty("refused").GetBoolean());

        Assert.Equal("salary_expectation", refused.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("value").ValueKind);
    }

    /// <summary>
    /// Every field the batch answered is recorded, one disclosure each.
    /// </summary>
    /// <remarks>
    /// One per field rather than one per call, so a review of what left this system does not have
    /// to know which shape the caller happened to use - and so the count cannot be reduced by
    /// batching, which is the optimisation somebody will eventually propose.
    /// </remarks>
    [Fact]
    public async Task A_batch_read_writes_one_disclosure_per_field_exactly_as_the_singular_tool_does()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().GetFormFieldsAsync(
            McpToolHarness.AsCandidate(), ["email", "phone", "salary_expectation"]);

        Assert.Equal(
            ["email", "phone"],
            harness.Disclosures.Records.Select(record => record.Detail).Order(StringComparer.Ordinal));

        Assert.All(harness.Disclosures.Records, record => Assert.Equal("get_form_fields", record.Tool));
    }

    /// <summary>
    /// A disclosure names what was asked for and never what came back.
    /// </summary>
    /// <remarks>
    /// <b>An audit log holding the data it audits has moved the problem rather than solved it.</b>
    /// The values searched for here are the ones the pack actually handed over on the same call,
    /// so this is not a test that a string is absent from an unrelated record - it is a test that
    /// the record and the payload were built from different halves of the same read.
    ///
    /// Both principals are asserted too. For a person's own client they are the same id and that
    /// looks like redundancy; it stops being redundant for an app-only caller, where "whose data
    /// left" and "what took it" are different answers.
    /// </remarks>
    [Fact]
    public async Task Reading_the_pack_records_a_disclosure_naming_the_request_and_never_the_answer()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments));

        var record = Assert.Single(harness.Disclosures.Records);

        Assert.Equal("get_submission_pack", record.Tool);
        Assert.Equal(McpToolHarness.Subject, record.SubjectId);
        Assert.Equal(McpToolHarness.Subject, record.ActorId);
        Assert.True(record.Answered);
        Assert.Contains("posting 10", record.Detail, StringComparison.Ordinal);

        var disclosed = Disclosed(pack).ToList();

        // A "none of these appear" assertion over an empty list passes for the wrong reason, and
        // this one would go empty the day the fixture profile stopped answering anything.
        Assert.NotEmpty(disclosed);

        foreach (var value in disclosed)
        {
            Assert.DoesNotContain(value, record.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The same rule for the narrow read: the question, never the answer.</summary>
    [Fact]
    public async Task Reading_one_field_records_the_field_name_and_never_its_value()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().GetFormFieldAsync(McpToolHarness.AsCandidate(), "phone");

        var record = Assert.Single(harness.Disclosures.Records);

        Assert.Equal("get_form_field", record.Tool);
        Assert.Equal("phone", record.Detail);
    }

    /// <summary>
    /// A read that found nothing is recorded too, and says it found nothing.
    /// </summary>
    /// <remarks>
    /// A log that only records successful reads cannot answer "what did this client ask for",
    /// which is the question an audit is actually for. The refusal is worth as much as the answer.
    /// </remarks>
    [Fact]
    public async Task A_field_the_profile_does_not_carry_is_still_recorded_as_having_been_asked_for()
    {
        using var harness = await McpToolHarness.CreateAsync();

        // On the allowlist and empty on this profile: nothing in the fixture is an apprenticeship,
        // so the second education entry has no institution to answer with.
        await harness.Tools().GetFormFieldAsync(McpToolHarness.AsCandidate(), "education[1].institution");

        var record = Assert.Single(harness.Disclosures.Records);

        Assert.Equal("education[1].institution", record.Detail);
        Assert.False(record.Answered);
    }

    /// <summary>Every profile value the pack actually handed over, so it can be looked for elsewhere.</summary>
    private static IEnumerable<string> Disclosed(JsonElement pack)
        => pack.GetProperty("profileFields")
            .EnumerateArray()
            .Select(field => field.GetProperty("value").GetString()!)
            .Where(value => value.Length >= 4);
}
