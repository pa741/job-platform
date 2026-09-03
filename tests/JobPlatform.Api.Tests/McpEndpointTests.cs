using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPlatform.Api.Features.Mcp;
using JobPlatform.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The agent surface: what it exposes, and what it refuses to expose.
/// </summary>
/// <remarks>
/// The tool <i>behaviour</i> is not tested here - it needs a principal the transport has no way to
/// mint from a test host, so it is exercised against the tool class directly in
/// <c>McpToolPayloadTests</c>, <c>McpToolRefusalTests</c> and <c>McpAnswerSourceTests</c>. What is
/// tested here is the part that is specific to this surface and would be catastrophic to get
/// wrong: the route is closed, the surface is exactly the fourteen tools somebody reviewed, and no
/// tool's <i>signature</i> offers a caller a way to name whose data it is asking about.
///
/// <b>Names and shapes only.</b> This file reads the registered tools' protocol descriptions - the
/// list, the descriptions and the input schemas - and asserts nothing about what any of them
/// returns. That division is deliberate and it is also where a gap was found: a name list cannot
/// notice a whole-profile object bolted onto a tool that already exists, so what may leave the
/// system is pinned in <c>McpToolPayloadTests</c> and not here.
/// </remarks>
public sealed class McpEndpointTests
{
    /// <summary>The whole surface, by name. Adding to this list is a deliberate act.</summary>
    private static readonly string[] Expected =
    [
        "list_applyable",
        "get_submission_pack",
        "get_form_field",
        "get_form_fields",
        "resolve_form_field",
        "list_submissions",
        "list_open_questions",
        "record_form_answer",
        "create_submission",
        "record_event",
        "park_application",
        "start_run",
        "finish_run",
        "match_email_to_submission",
    ];

    /// <summary>
    /// Names a caller must never be able to send, whatever the tool.
    /// </summary>
    /// <remarks>
    /// Spellings rather than one name, because the rule is about the <i>capability</i> and a model
    /// asked to write a client would reach for any of these. Each is folded to letters before
    /// comparison, so <c>profileId</c>, <c>profile_id</c> and <c>ProfileID</c> are one entry here.
    ///
    /// <b>A bare <c>subject</c> is deliberately not on the list</b>, though <c>subjectId</c> is:
    /// <c>match_email_to_submission</c> takes the subject <i>line</i> of a recruiter's message,
    /// which is a fact about an email and not a name for a person. A list that caught it would be
    /// relaxed by whoever hit it next, and a rule people switch off protects nothing.
    /// </remarks>
    private static readonly string[] ForbiddenIdentityArguments =
    [
        "profileid",
        "profile",
        "candidateid",
        "candidate",
        "subjectid",
        "userid",
        "oid",
        "objectid",
        "principalid",
        "actorid",
        "tenantid",
        "onbehalfof",
    ];

    /// <summary>
    /// The tools registered are exactly the ones intended.
    /// </summary>
    /// <remarks>
    /// <b>An equality, not a superset.</b> The point is the tools that are <i>absent</i>: there is
    /// no <c>submit_application</c>, because applying is irreversible and outward-facing and
    /// nothing in this repository may reach an employer; there is no <c>get_profile</c>, because
    /// a tool result is transcript content wherever the client runs. A superset assertion would
    /// pass while either was quietly added, and both are the kind of thing that gets added to be
    /// helpful.
    ///
    /// <b>The number is not the property; the equality is.</b> This list went from six to fourteen
    /// with the apply loop, and it will move again - what must not move is that moving it is a
    /// diff somebody signs off. A test asserting "at least the six" would have accepted the eight
    /// new tools silently, and would accept a fifteenth just as silently.
    ///
    /// It also pins the registration style. <c>WithTools&lt;T&gt;</c> is explicit; had it been
    /// <c>WithToolsFromAssembly</c>, a class gaining an attribute would become a public tool with
    /// nothing failing, and this test is what turns that into a red build.
    /// </remarks>
    [Fact]
    public void The_tool_surface_is_exactly_the_fourteen_intended_tools()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var tools = factory.Services
            .GetRequiredService<IOptions<McpServerOptions>>()
            .Value
            .ToolCollection;

        Assert.NotNull(tools);

        var names = tools.Select(tool => tool.ProtocolTool.Name).OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(Expected.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    /// <summary>
    /// Every tool describes itself, because the description is the interface a model reads.
    /// </summary>
    /// <remarks>
    /// A tool with no description is one a model has to guess the purpose of from its name, and
    /// guessing wrong here means calling <c>get_form_field</c> when it meant
    /// <c>get_submission_pack</c> - which is a disclosure, not a mistake to shrug at.
    /// </remarks>
    [Fact]
    public void Every_tool_carries_a_description()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var tools = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;

        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description)));
    }

    /// <summary>
    /// The container can actually build the class those fourteen tools live on.
    /// </summary>
    /// <remarks>
    /// <b>Registering a tool and being able to invoke it are two facts, and only the first is
    /// checked by everything above.</b> <c>WithTools&lt;T&gt;</c> reads the attributes off the type
    /// at startup and builds the instance from the request's services at call time, so a
    /// repository nobody registered in <c>Program.cs</c> costs nothing until a client calls a tool
    /// - and then costs every tool at once, on a surface whose other tests are all green. Eight
    /// services go into that constructor and one of them, <c>IFormFieldResolver</c>, is registered
    /// outside <c>AddAiProvider</c>'s provider check on purpose, which is exactly the sort of call
    /// that gets folded back inside it by somebody tidying up.
    ///
    /// <c>ActivatorUtilities</c> rather than <c>GetRequiredService</c>, because that is what the
    /// SDK does: the type is not a registered service, it is constructed per call from whatever
    /// the container can supply, with the two optional parameters left null where it cannot.
    /// </remarks>
    [Fact]
    public void The_class_the_tools_live_on_can_be_built_from_the_containers_own_registrations()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        var tools = ActivatorUtilities.CreateInstance<SubmissionTools>(scope.ServiceProvider);

        Assert.NotNull(tools);
    }

    /// <summary>
    /// No tool takes a profile id, or anything else by which a caller could name a person.
    /// </summary>
    /// <remarks>
    /// <b>The rule is stated in prose everywhere and was pinned nowhere.</b> <c>SubmissionTools</c>
    /// argues it at length, <c>McpOptions.AppPrincipals</c> argues it again from the configuration
    /// side, and <c>CandidateProfileRepository</c> expresses it as a type - and none of that stops
    /// somebody adding an optional <c>profileId</c> to one new tool. This walks the schemas the
    /// server actually publishes, which is the only place the rule is observable from outside.
    ///
    /// <b>Schemas rather than method signatures, deliberately.</b> A reflection test over
    /// <c>SubmissionTools</c>' methods would miss a parameter renamed by an attribute, and would
    /// pass for a tool registered from somewhere else entirely. What a client can send is what the
    /// published schema says it can send, so that is what is read.
    ///
    /// It is a whole-word match against a folded name, not a substring test: <c>postingId</c> and
    /// <c>submissionId</c> are ids this surface accepts on purpose - both are checked against the
    /// caller's own matches before anything is written - and a substring rule that caught "id"
    /// would have to be relaxed until it caught nothing.
    /// </remarks>
    [Fact]
    public void No_tool_takes_a_profile_id_or_any_other_way_to_name_a_person()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var tools = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;

        var offenders = tools
            .SelectMany(tool => Arguments(tool.ProtocolTool.InputSchema)
                .Where(argument => ForbiddenIdentityArguments.Contains(Fold(argument), StringComparer.Ordinal))
                .Select(argument => $"{tool.ProtocolTool.Name}({argument})"))
            .ToList();

        // Named in the failure rather than counted. A caller-named identity reaching this surface
        // is the whole authorisation model failing at once, and the reader has to know which tool.
        Assert.Empty(offenders);
    }

    /// <summary>
    /// <c>record_form_answer</c> offers no way to say who asserted the answer.
    /// </summary>
    /// <remarks>
    /// <b>The absence of the parameter is the guarantee, so the absence is what is asserted.</b>
    /// Everything written through this surface is stamped <c>Client</c> from the token; a
    /// <c>source</c> argument would let a model stamp its own inference as the candidate's own
    /// words by filling in a field, which is the same failure the profile-id rule above exists to
    /// prevent and a worse one - an answer is text that gets typed into an employer's form and
    /// sent under somebody's name.
    ///
    /// That the value written really is <c>Client</c> is a different claim and is asserted in
    /// <c>McpAnswerSourceTests</c>. This half only says a caller cannot ask for the other one.
    /// </remarks>
    [Fact]
    public void Record_form_answer_offers_no_way_to_name_the_source_of_an_answer()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var tools = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;

        var tool = tools.Single(entry => entry.ProtocolTool.Name == "record_form_answer");

        var arguments = Arguments(tool.ProtocolTool.InputSchema).Select(Fold).ToList();

        Assert.DoesNotContain("source", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("assertedby", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("author", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("origin", arguments, StringComparer.Ordinal);
    }

    /// <summary>
    /// The route requires an authenticated principal, asserted as metadata.
    /// </summary>
    /// <remarks>
    /// As metadata rather than through a response, for the reason
    /// <c>AuthorizationTests.Every_submission_route_requires_the_authenticated_policy</c> gives:
    /// an anonymous request answers 401 for several reasons and a behavioural test alone cannot
    /// tell which one it caught. This surface reads a person's shortlist, their generated CV and
    /// their contact details, so <c>Api:AllowAnonymousReads</c> must never reach it - and that
    /// flag is set to true here precisely so this test would notice if it did.
    /// </remarks>
    [Fact]
    public void The_mcp_route_requires_the_authenticated_policy()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        var routes = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/mcp", StringComparison.Ordinal) == true)
            .ToList();

        // The transport maps more than one endpoint under the prefix. Asserting it found some
        // stops this passing vacuously if the path ever moves.
        Assert.NotEmpty(routes);

        Assert.All(routes, route =>
        {
            var policies = route.Metadata.OfType<IAuthorizeData>().Select(data => data.Policy).ToList();

            Assert.Contains(AuthSetup.AuthenticatedPolicy, policies);
            Assert.DoesNotContain(AuthSetup.PublicReadPolicy, policies);
        });
    }

    /// <summary>And it answers 401 end to end, with the anonymous-reads switch on.</summary>
    [Fact]
    public async Task An_unauthenticated_client_cannot_list_the_tools()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The argument names one tool's published schema allows.</summary>
    /// <remarks>
    /// A schema with no <c>properties</c> is a tool that takes nothing, which is a real case -
    /// <c>start_run</c> - and answers an empty list rather than throwing.
    /// </remarks>
    private static IEnumerable<string> Arguments(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
                ? properties.EnumerateObject().Select(property => property.Name)
                : [];

    /// <summary>
    /// A name reduced to its letters, lower-cased.
    /// </summary>
    /// <remarks>
    /// So that one entry in the forbidden list covers <c>profileId</c>, <c>profile_id</c>,
    /// <c>Profile-Id</c> and <c>PROFILEID</c>. Casing and separators are a spelling choice; what
    /// the argument means is not.
    /// </remarks>
    private static string Fold(string name)
        => new([.. name.Where(char.IsLetter).Select(char.ToLowerInvariant)]);
}
