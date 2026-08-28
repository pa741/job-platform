using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPlatform.Core.Ai;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The ledger as somebody reads it, which is the half that makes it worth writing.
/// </summary>
/// <remarks>
/// A record nobody can see is a log line with extra steps. These pin the two things the endpoint
/// exists for: that failures are what it shows by default, and that a failure says what it lost.
/// </remarks>
public sealed class AiCallEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AiCallEndpointTests()
    {
        _client = _factory.CreateClient();

        _factory.AiCalls.Records.Add(Record(AiCallOutcome.Succeeded, requested: 10, returned: 10));
        _factory.AiCalls.Records.Add(Record(
            AiCallOutcome.PartiallyDiscarded,
            requested: 10,
            returned: 4,
            reason: "6 of 10 role indices unusable: String:\"0\"",
            affected: [11, 12, 13, 14, 15, 16]));
        _factory.AiCalls.Records.Add(Record(
            AiCallOutcome.Failed, requested: 10, returned: 0, reason: "timed out after 180s"));
    }

    private static AiCallRecord Record(
        AiCallOutcome outcome,
        int requested,
        int returned,
        string? reason = null,
        IReadOnlyList<long>? affected = null)
        => AiCallRecord.Create(
            new DateTimeOffset(2026, 8, 28, 3, 30, 0, TimeSpan.Zero),
            "candidacy-assessment",
            "bulk",
            outcome,
            requested,
            returned,
            durationMs: 4_200,
            reason,
            affected);

    [Fact]
    public async Task Failures_are_what_the_list_shows_by_default()
    {
        // A list of calls that worked is a list nobody reads, and the whole reason this exists is
        // that the losses were the part nobody could see.
        var response = await _client.GetAsync("/api/v1/ai-calls");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var items = body.GetProperty("items");

        Assert.Equal(2, items.GetArrayLength());
        Assert.All(
            items.EnumerateArray(),
            item => Assert.NotEqual("Succeeded", item.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task Asking_for_everything_includes_the_successes()
    {
        var response = await _client.GetAsync("/api/v1/ai-calls?failuresOnly=false");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(3, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_failure_names_what_it_lost()
    {
        // "One call failed" is not something anybody can act on. These six postings going
        // unassessed, and the reason saying which fault it was, is.
        var response = await _client.GetAsync("/api/v1/ai-calls");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        var partial = body.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("outcome").GetString() == "PartiallyDiscarded");

        Assert.Equal(10, partial.GetProperty("requested").GetInt32());
        Assert.Equal(4, partial.GetProperty("returned").GetInt32());
        Assert.Equal(6, partial.GetProperty("discarded").GetInt32());
        Assert.Equal(6, partial.GetProperty("affectedIds").GetArrayLength());
        Assert.Contains("unusable", partial.GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_puts_what_was_paid_for_beside_what_arrived()
    {
        // The pairing is the point. 14 of 30 reads as a bad night; 14 on its own reads as a
        // small one, which is exactly how a 55% loss went unnoticed.
        var response = await _client.GetAsync("/api/v1/ai-calls/summary");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        var total = body.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("candidacy-assessment", total.GetProperty("operation").GetString());
        Assert.Equal(3, total.GetProperty("calls").GetInt32());
        Assert.Equal(2, total.GetProperty("failedCalls").GetInt32());
        Assert.Equal(30, total.GetProperty("requested").GetInt32());
        Assert.Equal(14, total.GetProperty("returned").GetInt32());
        Assert.Equal(16, total.GetProperty("discarded").GetInt32());
    }

    [Fact]
    public async Task No_prompt_or_response_body_reaches_the_client()
    {
        // The prompts carry the candidate's employment history. The contract has no field for
        // them, and this asserts the shape rather than trusting that nobody adds one.
        var response = await _client.GetAsync("/api/v1/ai-calls?failuresOnly=false");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        foreach (var item in body.GetProperty("items").EnumerateArray())
        {
            Assert.False(item.TryGetProperty("prompt", out _));
            Assert.False(item.TryGetProperty("response", out _));
            Assert.False(item.TryGetProperty("profile", out _));
        }
    }

    [Fact]
    public async Task The_list_never_carries_a_prompt_even_when_one_was_kept()
    {
        // The guard that matters. Api:AllowAnonymousReads relaxes the policy this list sits
        // behind, and an assessment prompt is somebody's employment history - so one config
        // flag would be the difference between a dashboard and a published CV. The prompt is
        // absent from the list contract entirely rather than filtered, so exposing it would
        // take a deliberate edit.
        _factory.AiCalls.Records.Add(AiCallRecord.Create(
            new DateTimeOffset(2026, 8, 28, 3, 30, 0, TimeSpan.Zero),
            "candidacy-assessment",
            "bulk",
            AiCallOutcome.Failed,
            requested: 1,
            returned: 0,
            durationMs: 10,
            reason: "malformed JSON",
            affectedIds: [99],
            prompt: "CANDIDATE / somebody's employment history"));

        var response = await _client.GetAsync("/api/v1/ai-calls?failuresOnly=false");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("employment history", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"prompt\"", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replay_stays_closed_even_though_the_list_beside_it_is_open()
    {
        // The whole reason the prompt has its own route. This test host runs with anonymous
        // reads on - every assertion above is made without a token - and the replay route
        // still refuses, because it is behind AuthenticatedPolicy, which ignores that flag
        // entirely. Same reasoning as /me.
        var record = _factory.AiCalls.Records[1];

        var listed = await _client.GetAsync("/api/v1/ai-calls");
        var replay = await _client.GetAsync($"/api/v1/ai-calls/{record.Id}/replay");

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
