using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Profiles;
using JobPlatform.Core.Settings;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The nine levers over HTTP: what a candidate who has configured nothing runs on, what a save
/// stores, what a refusal says, and who may read whose.
/// </summary>
/// <remarks>
/// <b>Three kinds of claim are made here and they fail in three different ways.</b>
///
/// The first is the property the whole feature is judged on: <b>a deployment that configures
/// nothing must not be able to tell the feature arrived.</b> That is asserted against
/// <see cref="PipelineSettings.Default"/> rather than against nine literals, because the defaults
/// are a transcription of the constants the shipped code already ran and re-typing them here would
/// be a second copy with nothing that fails when the two diverge. What this suite adds over
/// <c>PipelineSettings</c>'s own tests is that the values survive the round trip - the record, the
/// mapping to the response, System.Text.Json, the row, and back - which is a chain of four
/// hand-written transcriptions that no compiler checks. <c>PipelineMapping</c> and
/// <c>PipelineSettingsRepository.ToDomain</c> both say in their own remarks that a forgotten line
/// is a test obligation rather than a compiler one;
/// <see cref="A_save_stores_all_nine_levers_and_answers_what_it_stored"/> is that obligation, and it
/// uses nine pairwise-distinct values none of which is its own default so that a transposed or
/// dropped field cannot read as a success.
///
/// The second is the refusal contract. A bound rendered in a form is a hint and this is the
/// enforcement, so it is exercised through the wire rather than against the validator - a client
/// may be a browser, an old build of one, or <c>curl</c>. <b>Every problem at once, not the
/// first</b>: a form with four bad fields should say so once rather than over four saves, and a
/// handler taking <c>problems[0]</c> compiles perfectly.
///
/// The third is the authorisation boundary, and it is asserted <b>as endpoint metadata rather than
/// through a status code</b>. That is not belt-and-braces: <c>CLAUDE.md</c> records that the
/// behavioural version of exactly this test passed for the wrong reason once, because every handler
/// also calls <c>CallerIdentity.TryGetSubjectId</c>, which answers 401 for a token with no
/// <c>oid</c> - so an anonymous request answers 401 whichever policy is on the group, and swapping
/// <c>AuthenticatedPolicy</c> for <c>PublicReadPolicy</c> left the read cases green. Defence in
/// depth working, and a test measuring the second layer while describing the first. Both layers are
/// pinned here, separately and by name, so the next person cannot relax the group invisibly.
/// </remarks>
public sealed class PipelineSettingsEndpointTests
{
    private const string Route = "/api/v1/pipeline-settings";

    /// <summary>Exactly what the wire carries: the nine levers, plus whether anybody chose them.</summary>
    /// <remarks>
    /// <b>An equality over the property names, which is the contract the client was written
    /// against character for character.</b> Nothing else in the build fails when a name changes -
    /// the response is a record with no schema behind it and the dashboard reads it dynamically -
    /// so a renamed field is a page that silently shows zero where somebody stored twenty-five.
    /// <c>updatedUtc</c> is in the set rather than optional: a client has to be able to tell "these
    /// are the shipped defaults" from "somebody chose these", and that distinction is this field
    /// and nothing else.
    /// </remarks>
    private static readonly string[] SettingsFields =
    [
        "assessmentThreshold", "assessmentsPerNight", "chaseAfterDays", "dailySendCap",
        "draftMinAssessmentScore", "draftPostedWithinDays", "draftsPerNight",
        "recentSharePercent", "recentWindowDays", "updatedUtc",
    ];

    /// <summary>The two routes this feature is allowed to have, by name.</summary>
    /// <remarks>
    /// <b>An equality and not a superset, and the property being defended is an absence.</b> There
    /// is no route that names whose settings these are: the subject id comes from the token, and
    /// <c>PipelineSettingsRepository</c>'s person-facing methods take a subject id with no overload
    /// a route parameter could be handed to. A third route here - a "reset", an "apply to all", a
    /// <c>/pipeline-settings/{profileId}</c> written by somebody wiring an admin page - is the
    /// change this equality makes into a diff somebody signs off.
    /// </remarks>
    private static readonly string[] RouteNames = ["GetPipelineSettings", "SavePipelineSettings"];

    /// <summary>
    /// A complete configuration whose nine values are pairwise distinct and none of them a default.
    /// </summary>
    /// <remarks>
    /// <b>Distinctness is the whole point and it is doing real work.</b> Eight <c>int</c> and one
    /// <c>int?</c> in overlapping small ranges cross four hand-written transcriptions between the
    /// form and the row - the record, <c>PipelineMapping.ToResponse</c>,
    /// <c>PipelineSettingsRepository.Apply</c> and its <c>ToDomain</c> - and a transposed pair
    /// compiles in every one of them. Nine values that are all different means a swap shows up as a
    /// failed assertion rather than as two equal numbers agreeing with each other.
    ///
    /// <b>And none of them is its own default</b>, because a field dropped from a mapping reads
    /// back as the default and would be indistinguishable from a candidate who asked for it.
    ///
    /// Built with <c>PipelineSettings.Default with { ... }</c> rather than by writing nine values
    /// into a constructor - the spelling the record exists to make possible - even though this one
    /// happens to override all nine.
    /// </remarks>
    private static readonly PipelineSettings Chosen = PipelineSettings.Default with
    {
        AssessmentsPerNight = 137,
        AssessmentThreshold = 52,
        RecentSharePercent = 41,
        RecentWindowDays = 9,
        DraftsPerNight = 17,
        DraftMinAssessmentScore = 73,
        DraftPostedWithinDays = 21,
        DailySendCap = 88,
        ChaseAfterDays = 6,
    };

    /// <summary>
    /// <see cref="Chosen"/> as a client actually sends it: nine camelCase names, spelled out.
    /// </summary>
    /// <remarks>
    /// <b>Literal JSON rather than an anonymous object, because the names are the contract.</b> An
    /// anonymous object serialised by the client's own camelCase policy would agree with the server
    /// by construction whatever either of them was called, which is precisely the agreement worth
    /// testing: <c>web/src/api/types.ts</c> was written against these names, and nothing in this
    /// solution compiles against that file.
    /// </remarks>
    private const string ChosenJson = """
        {
          "assessmentsPerNight":     137,
          "assessmentThreshold":     52,
          "recentSharePercent":      41,
          "recentWindowDays":        9,
          "draftsPerNight":          17,
          "draftMinAssessmentScore": 73,
          "draftPostedWithinDays":   21,
          "dailySendCap":            88,
          "chaseAfterDays":          6
        }
        """;

    /// <summary>
    /// A candidate who has configured nothing is answered the constants the shipped code ran.
    /// </summary>
    /// <remarks>
    /// <b>The property the feature is judged on, at the surface a person actually reads.</b> A
    /// deployment that configures nothing must not be able to tell the feature arrived, so the read
    /// for an absent row is a <i>complete</i> configuration equal to
    /// <see cref="PipelineSettings.Default"/> - never null, never a half-filled record, never a
    /// 404. The asymmetry with <c>PUT</c>, which can answer 404, is the design rather than an
    /// inconsistency: nobody is missing an answer, they are running on the shipped one.
    ///
    /// <b>Asserted against <see cref="PipelineSettings.Default"/> and not against nine literals.</b>
    /// The defaults are a transcription of <c>MatchSweepFunction</c>'s constants,
    /// <c>ApplicationGenerationOptions</c>' and <c>SubmissionLimits</c>' - forty, forty-five, the
    /// two-thirds reservation, three days, ten, eighty, no age bound, twenty-five and a fortnight -
    /// and re-typing them here would be a further copy that goes stale silently.
    /// <c>PipelineSettings</c>'s own tests are where those nine numbers are held to the constants
    /// they replace.
    ///
    /// <b><c>updatedUtc</c> is null and that is a complete answer.</b> It is the only thing
    /// separating "I never chose" from "I chose the shipped behaviour", which is why there is no
    /// delete on this resource: resetting is a save of the defaults, and the two states differ by
    /// this timestamp and by nothing the pipeline can observe.
    /// </remarks>
    [Fact]
    public async Task An_unconfigured_candidate_is_answered_the_shipped_defaults()
    {
        using var harness = await Harness.CreateAsync();

        var response = await harness.As(Harness.Ada).GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(SettingsFields, Names(body));
        AssertLevers(PipelineSettings.Default, body);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("updatedUtc").ValueKind);
    }

    /// <summary>
    /// A save stores all nine, answers what it stored, and the next read agrees with it.
    /// </summary>
    /// <remarks>
    /// <b>This is the test <c>PipelineMapping</c> and <c>PipelineSettingsRepository.ToDomain</c>
    /// both ask for by name.</b> The nine values are written out by hand in four places and
    /// "nothing in the type system catches a forgotten line" is said in the remarks of two of them.
    /// A field missed in a mapping reads back as <c>0</c> - or <c>null</c> for the age bound -
    /// where the candidate stored something else, and the failure is quiet in the worst way: the
    /// save succeeded, the pipeline runs on the stored number, and the page shows a different one.
    /// Nobody sees an error; they see a settings page that disagrees with their bill.
    ///
    /// <b>Both the save's own answer and a fresh read are asserted, because they come from
    /// different places.</b> The endpoint deliberately reads back through the same pair of calls
    /// the <c>GET</c> uses rather than echoing the request, so asserting only the save's response
    /// would still pass against a repository that stored nothing at all - which is exactly what a
    /// missing <c>AsTracking()</c> produces, silently, on a host whose global default was once
    /// <c>NoTracking</c>. A second request through a second scope is what proves a row was written.
    ///
    /// <b><c>updatedUtc</c> becomes the instant of the save</b>, from the host's
    /// <see cref="TimeProvider"/>, which the harness fixes so the timestamp is a constant a test
    /// can name rather than a window it has to tolerate.
    /// </remarks>
    [Fact]
    public async Task A_save_stores_all_nine_levers_and_answers_what_it_stored()
    {
        using var harness = await Harness.CreateAsync();
        var client = harness.As(Harness.Ada);

        var saved = await PutAsync(client, ChosenJson);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var written = await saved.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(SettingsFields, Names(written));
        AssertLevers(Chosen, written);
        Assert.Equal(Harness.Saved, written.GetProperty("updatedUtc").GetDateTimeOffset());

        // A second request, through a second scope, against the row rather than against whatever
        // the write path was still holding.
        var read = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var reread = await read.Content.ReadFromJsonAsync<JsonElement>();

        AssertLevers(Chosen, reread);
        Assert.Equal(Harness.Saved, reread.GetProperty("updatedUtc").GetDateTimeOffset());
    }

    /// <summary>
    /// A field the client omitted keeps the constant the shipped code already ran.
    /// </summary>
    /// <remarks>
    /// <b>A partial document is a partial override by construction, and that is load-bearing rather
    /// than convenient.</b> The body deserialises into <see cref="PipelineSettings"/> itself - there
    /// is no request DTO, deliberately - so an absent field keeps its property initialiser. It is
    /// resolved once, by the record, and never again by the endpoint or the repository: two places
    /// a partial document is resolved would be two answers to "what does an omitted field mean" and
    /// one of them would be wrong.
    ///
    /// <b>It is also the argument for the record being nine named <c>init</c> properties rather
    /// than a positional one.</b> A positional record's defaults depend on the serialiser honouring
    /// C# optional parameter values; property initialisers survive every serialiser. This asserts
    /// the behaviour through the serialiser the host actually runs, which is the only place that
    /// claim can be checked.
    ///
    /// The one lever sent is <c>draftsPerNight</c>, and it is sent below the default send cap on
    /// purpose - a partial document still has to satisfy the cross-field rules, and it satisfies
    /// them against the defaults it inherited rather than against anything the client said.
    /// </remarks>
    [Fact]
    public async Task An_omitted_field_keeps_the_default_rather_than_arriving_as_zero()
    {
        using var harness = await Harness.CreateAsync();

        var saved = await PutAsync(harness.As(Harness.Ada), """{ "draftsPerNight": 4 }""");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();

        AssertLevers(PipelineSettings.Default with { DraftsPerNight = 4 }, body);

        // Said again at the wire level, because "kept the default" and "arrived as zero" are the
        // two readings this test exists to separate and eight of the nine defaults are non-zero.
        Assert.Equal(40, body.GetProperty("assessmentsPerNight").GetInt32());
        Assert.Equal(25, body.GetProperty("dailySendCap").GetInt32());
    }

    /// <summary>
    /// The age bound clears to null, and zero is refused rather than accepted as a bound.
    /// </summary>
    /// <remarks>
    /// <b>The only nullable lever, and null is not zero.</b> Null is "no age bound at all" and is
    /// the shipped behaviour; zero through <c>PostingAge.Cutoff</c> means "posted since this
    /// instant" and would select almost nothing - a drafting pass that quietly stops writing. Two
    /// states a form renders as the same empty box have to be different bytes on the wire, which is
    /// the rule the scraper configuration already runs under from the other direction.
    ///
    /// <b>Clearing is asserted after setting</b>, because that is the one field where a merge and a
    /// replace differ visibly: <c>PipelineSettingsRepository.Apply</c> assigns the age bound rather
    /// than conditionally assigning it, so an emptied box writes NULL instead of leaving
    /// yesterday's number standing. A conditional assignment there would pass every other test in
    /// this file.
    /// </remarks>
    [Fact]
    public async Task The_age_bound_clears_to_null_and_zero_is_refused()
    {
        using var harness = await Harness.CreateAsync();
        var client = harness.As(Harness.Ada);

        var bounded = await PutAsync(client, ChosenJson);

        Assert.Equal(
            21,
            (await bounded.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("draftPostedWithinDays").GetInt32());

        var cleared = await PutAsync(client, AgeBound("null"));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            (await cleared.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("draftPostedWithinDays").ValueKind);

        // And the row really lost it, rather than the response having lost it.
        Assert.Equal(
            JsonValueKind.Null,
            (await (await client.GetAsync(Route)).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("draftPostedWithinDays").ValueKind);

        var zero = await PutAsync(client, AgeBound("0"));

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains("every age", await DetailOf(zero), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refused save names every problem at once, and stores none of them.
    /// </summary>
    /// <remarks>
    /// <b>Every problem, not the first, and a handler taking <c>problems[0]</c> compiles
    /// perfectly.</b> A form with four bad fields should say so once rather than over four saves -
    /// the contract <c>ScraperSearchValidation</c> already keeps, and the reason
    /// <c>PipelineSettingsValidation.Validate</c> returns a list rather than throwing on the first
    /// failure. The four chosen here are deliberately one of each shape: a ceiling, a floor, a
    /// second ceiling and a second floor, so a validator that checked only maxima or only the first
    /// section would still be caught.
    ///
    /// <b>The expected messages come from the validator rather than being transcribed.</b> Copying
    /// the sentences into this file would make a wording change a failing test for no reason, and
    /// the claim being made is not what the sentences say - it is that <i>all four of them
    /// arrive</i>. The count is asserted against the pure validator first, so this cannot pass
    /// vacuously against a record that turned out to have one problem; then each message is
    /// required to be in the <c>detail</c>, and the <c>detail</c> is required not to be just the
    /// first of them.
    ///
    /// <b>Refusals rather than clamps, and nothing is written.</b> A setting that is silently
    /// corrected is a setting that lies to the person who typed it: they ask for four hundred
    /// sends, the page saves, and the cap stays where it was with nothing anywhere saying why. So
    /// the read afterwards has to be the untouched defaults - not the clamped values, and not a
    /// half-applied row.
    /// </remarks>
    [Fact]
    public async Task A_refused_save_names_every_problem_and_stores_nothing()
    {
        using var harness = await Harness.CreateAsync();
        var client = harness.As(Harness.Ada);

        var impossible = PipelineSettings.Default with
        {
            AssessmentsPerNight = 5_000,   // above MaxAssessmentsPerNight
            RecentWindowDays = 0,          // below MinRecentWindowDays
            DailySendCap = 400,            // above MaxDailySendCap
            ChaseAfterDays = 4_000,        // above MaxChaseAfterDays
        };

        var problems = PipelineSettingsValidation.Validate(impossible);

        // Four bad fields, so four problems, so this fixture can actually distinguish "every
        // problem" from "the first one".
        Assert.Equal(4, problems.Count);

        var refused = await PutAsync(client, """
            {
              "assessmentsPerNight":     5000,
              "assessmentThreshold":     45,
              "recentSharePercent":      67,
              "recentWindowDays":        0,
              "draftsPerNight":          10,
              "draftMinAssessmentScore": 80,
              "draftPostedWithinDays":   null,
              "dailySendCap":            400,
              "chaseAfterDays":          4000
            }
            """);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var detail = await DetailOf(refused);

        foreach (var problem in problems)
        {
            Assert.Contains(problem, detail, StringComparison.Ordinal);
        }

        // The refusal names the numbers the person actually typed, which is what makes it
        // actionable in one pass rather than in four.
        Assert.Contains("5000", detail, StringComparison.Ordinal);
        Assert.Contains("400", detail, StringComparison.Ordinal);
        Assert.Contains("4000", detail, StringComparison.Ordinal);
        Assert.NotEqual(problems[0], detail);

        // Nothing was clamped and nothing was half-written: the candidate is still unconfigured.
        var read = await (await client.GetAsync(Route)).Content.ReadFromJsonAsync<JsonElement>();

        AssertLevers(PipelineSettings.Default, read);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("updatedUtc").ValueKind);
    }

    /// <summary>
    /// A drafting floor below the assessment threshold is refused, with all nine in range.
    /// </summary>
    /// <remarks>
    /// <b>The cross-field rule a client-side check is most likely to miss and a person most needs
    /// the sentence for.</b> Every one of the nine numbers here is individually legal - 55 and 60
    /// are both inside 0..100 - so nothing about the form's own bounds refuses this, and it is the
    /// only kind of test in this file where the 400 cannot have come from a range check.
    ///
    /// <b>The two numbers are read against different columns, which is why the rule is stated
    /// rather than obvious.</b> The threshold reads the deterministic match score and decides
    /// whether a judgement is bought at all; the drafting floor reads
    /// <c>JobMatches.AssessmentScore</c>, which only exists for pairs that cleared the threshold.
    /// So a floor set below the threshold reads as widening the drafting band and widens nothing -
    /// the rows it means to admit are the rows nothing has judged, and they carry no assessment
    /// score for the floor to read. Refused rather than tolerated, because a setting that does
    /// nothing is worse than one that is rejected: the candidate believes they asked for more
    /// drafts and gets the same ones.
    ///
    /// <b>The message names both values</b>, because a refusal naming one of a pair sends somebody
    /// to change the wrong box.
    /// </remarks>
    [Fact]
    public async Task A_drafting_floor_below_the_assessment_threshold_is_refused()
    {
        using var harness = await Harness.CreateAsync();

        var contradiction = PipelineSettings.Default with
        {
            AssessmentThreshold = 60,
            DraftMinAssessmentScore = 55,
        };

        // Individually in range, so the only thing that can refuse this is the cross-field rule.
        Assert.Single(PipelineSettingsValidation.Validate(contradiction));

        var refused = await PutAsync(harness.As(Harness.Ada), """
            {
              "assessmentsPerNight":     40,
              "assessmentThreshold":     60,
              "recentSharePercent":      67,
              "recentWindowDays":        3,
              "draftsPerNight":          10,
              "draftMinAssessmentScore": 55,
              "draftPostedWithinDays":   null,
              "dailySendCap":            25,
              "chaseAfterDays":          14
            }
            """);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var detail = await DetailOf(refused);

        Assert.Contains("55", detail, StringComparison.Ordinal);
        Assert.Contains("60", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A night may not write more letters than a day may send, and that is refused too.
    /// </summary>
    /// <remarks>
    /// <b>The second cross-field rule, and the one that used to be a silent
    /// <c>Math.Clamp</c>.</b> <c>GenerateApplicationsFunction</c> clamped the configured figure to
    /// the day's send cap, which is the shape of correction this whole feature refuses: the
    /// candidate asks for twenty drafts against a cap of five, the page saves, and the pass writes
    /// five every night with nothing anywhere saying why.
    ///
    /// <b>Twenty and five are each individually legal</b> - twenty is inside
    /// <see cref="PipelineSettingsValidation.MaxDraftsPerNight"/> and five inside
    /// <see cref="PipelineSettingsValidation.MaxDailySendCap"/> - so the effective ceiling on
    /// drafts really is the lower of the constant and the candidate's own cap, and it is enforced
    /// here rather than assumed. The corpus is re-scraped nightly, so the surplus is not merely
    /// early: it is prose tailored to adverts that will have gone before the queue could reach them.
    /// </remarks>
    [Fact]
    public async Task A_night_that_drafts_more_than_a_day_can_send_is_refused()
    {
        using var harness = await Harness.CreateAsync();

        var contradiction = PipelineSettings.Default with { DraftsPerNight = 20, DailySendCap = 5 };

        Assert.Single(PipelineSettingsValidation.Validate(contradiction));

        var refused = await PutAsync(harness.As(Harness.Ada), """
            {
              "assessmentsPerNight":     40,
              "assessmentThreshold":     45,
              "recentSharePercent":      67,
              "recentWindowDays":        3,
              "draftsPerNight":          20,
              "draftMinAssessmentScore": 80,
              "draftPostedWithinDays":   null,
              "dailySendCap":            5,
              "chaseAfterDays":          14
            }
            """);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var detail = await DetailOf(refused);

        Assert.Contains("(20)", detail, StringComparison.Ordinal);
        Assert.Contains("(5)", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// One candidate's levers are invisible to another, on both verbs.
    /// </summary>
    /// <remarks>
    /// <b>There is no id in either URL, so this is not a test that a route parameter is checked -
    /// it is a test that the scoping key is the token.</b> The repository's person-facing methods
    /// take a subject id and have no overload a request could hand a profile id to, which is the
    /// authorisation boundary expressed as a type; what remains assertable over HTTP is that the
    /// subject actually used is the caller's own <c>oid</c> and not, say, the first row in the
    /// table. A join written against <c>CandidateProfiles</c> without the subject clause would
    /// serve Grace's nightly budget to Ada and pass every other test in this file.
    ///
    /// <b>Ada reads the defaults rather than a 404 or an error</b>, which is the same answer she
    /// would get on a deployment where Grace does not exist. Another person's configuration is not
    /// merely forbidden here, it is unobservable - there is nothing in Ada's response that differs
    /// according to whether anybody else has ever saved.
    ///
    /// <b>And a write is scoped the same way</b>, which is the half that would be worse: the
    /// response body of a <c>GET</c> is handed straight back to a <c>PUT</c> by the settings page,
    /// so a read that crossed principals would not merely show one person another's cap - it would
    /// let a stranger save it back onto them without ever seeing anything wrong.
    /// </remarks>
    [Fact]
    public async Task One_candidate_cannot_read_or_overwrite_another_candidates_levers()
    {
        using var harness = await Harness.CreateAsync();

        var hers = await PutAsync(harness.As(Harness.Grace), ChosenJson);

        Assert.Equal(HttpStatusCode.OK, hers.StatusCode);

        // Ada has a profile and has saved nothing, so she is unconfigured - Grace's row is not
        // merely forbidden to her, it is invisible.
        var ada = await (await harness.As(Harness.Ada).GetAsync(Route))
            .Content.ReadFromJsonAsync<JsonElement>();

        AssertLevers(PipelineSettings.Default, ada);
        Assert.Equal(JsonValueKind.Null, ada.GetProperty("updatedUtc").ValueKind);

        // And Ada's own save writes her row rather than the one she could see.
        var mine = await PutAsync(harness.As(Harness.Ada), """{ "chaseAfterDays": 30 }""");

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(
            30,
            (await mine.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("chaseAfterDays").GetInt32());

        var grace = await (await harness.As(Harness.Grace).GetAsync(Route))
            .Content.ReadFromJsonAsync<JsonElement>();

        AssertLevers(Chosen, grace);
    }

    /// <summary>
    /// A principal with no profile reads the defaults and cannot save, and that asymmetry is
    /// deliberate.
    /// </summary>
    /// <remarks>
    /// <b><c>GET</c> never answers 404 and <c>PUT</c> can.</b> Somebody with no profile has no
    /// pipeline running at all, so the settings their pipeline would run under are the ones nobody
    /// configured - a complete answer rather than a missing one, and a client forced to tell "no
    /// settings" from "a failure" by reading a status code is a client that eventually shows an
    /// error to somebody whose pipeline is working perfectly. A save is different because the row
    /// needs a key: it is keyed to the profile, and there is nothing to key it on yet.
    ///
    /// <b>404 rather than creating the profile.</b> A profile invented as a side effect of visiting
    /// a settings page is an empty row the extraction sweep would then pick up. The refusal carries
    /// a detail because the 404 is otherwise surprising - nothing in the URL names a resource that
    /// could be missing - so the body has to be the thing that explains it.
    ///
    /// <b>Not a 401 and not a 403.</b> The principal is perfectly valid; there is simply no
    /// pipeline yet, which is a statement about the account rather than about permission.
    /// </remarks>
    [Fact]
    public async Task A_principal_with_no_profile_reads_the_defaults_and_cannot_save()
    {
        using var harness = await Harness.CreateAsync();
        var client = harness.As(Harness.Stranger);

        var read = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var body = await read.Content.ReadFromJsonAsync<JsonElement>();

        AssertLevers(PipelineSettings.Default, body);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("updatedUtc").ValueKind);

        var refused = await PutAsync(client, ChosenJson);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Contains("profile", await DetailOf(refused), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Neither route opens when the anonymous-reads switch is on.
    /// </summary>
    /// <remarks>
    /// <c>Api:AllowAnonymousReads</c> exists to open the posting corpus, which is public text. These
    /// two routes decide how many calls a scheduled pass makes to a language model on somebody's
    /// behalf and how many applications may be recorded as sent in their name; the harness turns the
    /// switch on precisely so this can be asserted with it on.
    ///
    /// <b>This is the weaker half of the pair on purpose, and it is kept because it proves the
    /// routes answer at all.</b> On its own it cannot tell which layer produced the 401 - see
    /// <see cref="Every_pipeline_settings_route_requires_the_authenticated_policy"/> - but a 404
    /// here would mean the group was never registered in <c>EndpointGroupExtensions</c>, which is
    /// the other thing a feature that is a folder plus one line can get wrong, and which no
    /// metadata assertion would report as anything but an empty set.
    /// </remarks>
    [Fact]
    public async Task Both_routes_stay_closed_even_when_anonymous_reads_are_allowed()
    {
        using var harness = await Harness.CreateAsync();
        var anonymous = harness.As(subject: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(anonymous, ChosenJson)).StatusCode);
    }

    /// <summary>
    /// The policy on the group, asserted as metadata rather than through a response.
    /// </summary>
    /// <remarks>
    /// <b>The behavioural test above cannot pin this on its own, and <c>CLAUDE.md</c> records that
    /// exact failure happening.</b> Both handlers call <c>CallerIdentity.TryGetSubjectId</c>, which
    /// answers 401 for a principal carrying no <c>oid</c>, so an anonymous request answers 401
    /// whichever policy is on the group - defence in depth working, and a test measuring the second
    /// layer while describing the first. Swapping <see cref="AuthSetup.AuthenticatedPolicy"/> for
    /// <see cref="AuthSetup.PublicReadPolicy"/> here would leave every status-code assertion in this
    /// file green while making a candidate's nightly budget readable by anyone the moment a
    /// deployment sets <c>Api:AllowAnonymousReads</c> - which is the flag the frontend is developed
    /// against.
    ///
    /// Reading the endpoint metadata cannot be fooled that way. The count is asserted first so the
    /// test cannot pass vacuously against a prefix that has moved and now matches nothing.
    /// </remarks>
    [Fact]
    public void Every_pipeline_settings_route_requires_the_authenticated_policy()
    {
        var routes = PipelineRoutes();

        Assert.Equal(RouteNames.Length, routes.Count);

        foreach (var route in routes)
        {
            var policies = route.Metadata
                .OfType<IAuthorizeData>()
                .Select(data => data.Policy)
                .ToList();

            Assert.Contains(AuthSetup.AuthenticatedPolicy, policies);
            Assert.DoesNotContain(AuthSetup.PublicReadPolicy, policies);
        }
    }

    /// <summary>
    /// Neither route is output cached, asserted as metadata and against a control.
    /// </summary>
    /// <remarks>
    /// <b>A safety property rather than a tuning choice, and this is the strongest form the
    /// assertion has.</b> The output cache is keyed on the URL and this URL contains no user, so a
    /// cached <c>GET /pipeline-settings</c> is one person's nightly budget served to the next
    /// caller. It is worse here than on the profile or the searches: the settings page hands the
    /// response body straight back to a <c>PUT</c>, so the value served would not merely be shown -
    /// it would be typed over and saved back, writing one person's send cap onto another person's
    /// row by somebody who never saw a thing wrong.
    ///
    /// <b>Metadata rather than behaviour, because behaviour cannot see this here.</b> The suite
    /// runs with <c>Cache:Enabled</c> off - <see cref="ApiFactory"/> turns it off so a test's second
    /// request cannot be served the first one's body - so a <c>.CacheOutput(...)</c> added to this
    /// group would change no status code and no body anywhere in this project, and would ship. The
    /// metadata is attached where the route is mapped, whatever the configuration says, so it is
    /// visible from here and a regression is a red build rather than an incident.
    ///
    /// <b>The control is not decoration, and it earned its place the first time it ran.</b>
    /// <c>/api/v1/postings</c> is cached deliberately - public text, no principal in the answer -
    /// so it is asserted to trip the same detection this test uses. The obvious predicate,
    /// <c>GetType().Name.Contains("OutputCache")</c>, does not: <c>.CacheOutput("postings")</c>
    /// adds <c>Microsoft.AspNetCore.OutputCaching.NamedPolicy</c>, whose <i>name</i> contains
    /// neither word, so that predicate answers "not cached" for all four cached posting routes.
    /// Written without the control, this test would have shipped green and would have stayed green
    /// through somebody adding a cache to these two routes - a passing assertion that can no longer
    /// see anything is indistinguishable from a passing assertion that has nothing to see. See
    /// <see cref="IsOutputCache"/> for what is matched instead.
    /// </remarks>
    [Fact]
    public void Neither_pipeline_settings_route_is_output_cached()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };

        // Forces the host to build; the endpoint data source is not populated before it does.
        using var client = factory.CreateClient();

        var settings = RoutesUnder(factory, Route);

        Assert.Equal(RouteNames.Length, settings.Count);
        Assert.All(settings, route => Assert.DoesNotContain(route.Metadata, IsOutputCache));

        // The control: a route that is cached on purpose still trips this detection, so a green
        // result above means "no cache" rather than "nothing was looked at".
        Assert.Contains(
            RoutesUnder(factory, "/api/v1/postings"),
            route => route.Metadata.Any(IsOutputCache));
    }

    /// <summary>
    /// The route surface is exactly a read and a replace, and neither URL names a candidate.
    /// </summary>
    /// <remarks>
    /// <b>An equality rather than a superset, and what is defended is an absence.</b> There is no
    /// route parameter naming whose settings these are and there must never be one: the subject id
    /// comes from the token through <c>CallerIdentity.TryGetSubjectId</c>, and the repository's
    /// person-facing methods have no overload a route parameter could be handed to. Its two
    /// profile-id methods exist for the unattended nightly passes; a
    /// <c>/pipeline-settings/{profileId}</c> added by somebody wiring an admin page is exactly how
    /// they would become reachable from a request, and a superset assertion would accept it in
    /// silence.
    ///
    /// <b>And no destructive verb.</b> Resetting to the shipped behaviour is a <c>PUT</c> of the
    /// defaults - a row of default values and no row at all read back identically apart from the
    /// timestamp - so a <c>DELETE</c> here would be a second spelling of one operation rather than
    /// a capability. The names are asserted alongside the count so that a route added without
    /// <c>WithName</c> cannot slip past a set comparison.
    /// </remarks>
    [Fact]
    public void The_route_surface_is_exactly_a_read_and_a_replace_and_neither_names_a_candidate()
    {
        var routes = PipelineRoutes();

        Assert.Equal(RouteNames.Length, routes.Count);

        Assert.Equal(
            RouteNames,
            routes
                .Select(route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
                .OfType<string>()
                .Order(StringComparer.Ordinal));

        Assert.Equal(
            ["GET", "PUT"],
            routes
                .SelectMany(route => route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        // Nothing in either pattern a caller could substitute an identity into.
        Assert.All(routes, route => Assert.DoesNotContain('{', route.RoutePattern.RawText ?? string.Empty));
    }

    /// <summary>Every lever, by name, so a mapping that dropped one cannot read as a success.</summary>
    /// <remarks>
    /// Written out rather than compared as whole records, because the response is a different type
    /// from <see cref="PipelineSettings"/> and the assertion has to cross the wire. Naming all nine
    /// is the point: a helper that compared "the ones this test cares about" would be the same
    /// omission the mapping itself is at risk of.
    /// </remarks>
    private static void AssertLevers(PipelineSettings expected, JsonElement body)
    {
        ArgumentNullException.ThrowIfNull(expected);

        Assert.Equal(expected.AssessmentsPerNight, body.GetProperty("assessmentsPerNight").GetInt32());
        Assert.Equal(expected.AssessmentThreshold, body.GetProperty("assessmentThreshold").GetInt32());
        Assert.Equal(expected.RecentSharePercent, body.GetProperty("recentSharePercent").GetInt32());
        Assert.Equal(expected.RecentWindowDays, body.GetProperty("recentWindowDays").GetInt32());
        Assert.Equal(expected.DraftsPerNight, body.GetProperty("draftsPerNight").GetInt32());
        Assert.Equal(expected.DraftMinAssessmentScore, body.GetProperty("draftMinAssessmentScore").GetInt32());
        Assert.Equal(expected.DailySendCap, body.GetProperty("dailySendCap").GetInt32());
        Assert.Equal(expected.ChaseAfterDays, body.GetProperty("chaseAfterDays").GetInt32());

        var age = body.GetProperty("draftPostedWithinDays");

        if (expected.DraftPostedWithinDays is { } within)
        {
            Assert.Equal(within, age.GetInt32());
        }
        else
        {
            // Null and not 0: the distinction the whole field exists to carry.
            Assert.Equal(JsonValueKind.Null, age.ValueKind);
        }
    }

    /// <summary>
    /// <see cref="ChosenJson"/> with a different age bound, and nothing else moved.
    /// </summary>
    /// <remarks>
    /// One field is varied against a body that is otherwise byte-for-byte the one already known to
    /// save, so a refusal or a cleared value can only be about the age bound. Written as a
    /// replacement of the whole line rather than of the number, because <c>21</c> is a substring a
    /// looser edit could find elsewhere the moment somebody changes one of the other eight.
    /// </remarks>
    private static string AgeBound(string value)
        => ChosenJson.Replace(
            "\"draftPostedWithinDays\":   21",
            $"\"draftPostedWithinDays\":   {value}",
            StringComparison.Ordinal);

    /// <summary>Sends a body exactly as written, rather than as an object graph re-serialised.</summary>
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string json)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.PutAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static async Task<string> DetailOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString() ?? string.Empty;

    /// <summary>
    /// Whether one piece of endpoint metadata is an output-caching policy.
    /// </summary>
    /// <remarks>
    /// <b>By interface, and never by type name, because the type name does not contain the
    /// words.</b> <c>.CacheOutput("postings")</c> - the spelling every cached route in this API
    /// uses - adds <c>Microsoft.AspNetCore.OutputCaching.NamedPolicy</c>, so a predicate reading
    /// <c>GetType().Name.Contains("OutputCache")</c> matches nothing at all and answers "not
    /// cached" for a route that plainly is. That is the failure mode this whole test exists to
    /// prevent, arriving through the test rather than through the endpoint, and it is why the
    /// control assertion in <see cref="Neither_pipeline_settings_route_is_output_cached"/> is not
    /// optional.
    ///
    /// <see cref="IOutputCachePolicy"/> is what all four spellings have in common - the named
    /// policy, the default <c>.CacheOutput()</c>, an inline builder, and the
    /// <c>[OutputCache]</c> attribute - because it is the interface the middleware itself reads.
    /// A fifth spelling that this missed would also be a policy the middleware could not run.
    /// </remarks>
    private static bool IsOutputCache(object metadata)
        => metadata is IOutputCachePolicy;

    private static List<RouteEndpoint> PipelineRoutes()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };

        // Forces the host to build; the endpoint data source is not populated before it does.
        using var client = factory.CreateClient();

        return RoutesUnder(factory, Route);
    }

    private static List<RouteEndpoint> RoutesUnder(ApiFactory factory, string prefix)
        => [.. factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?
                .StartsWith(prefix, StringComparison.Ordinal) == true)];

    /// <summary>The property names of one object, sorted, so a contract can be asserted as a set.</summary>
    private static IReadOnlyList<string> Names(JsonElement element)
        => [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Two candidates with profiles, a fixed clock, and nothing configured.
    /// </summary>
    /// <remarks>
    /// <b>Over HTTP rather than against the handlers</b>, for the reason <c>QuestionQueueHarness</c>
    /// and <c>CvLibraryHarness</c> both give: a test that invokes a handler directly passes against
    /// a route nothing mapped, and "the group is registered in <c>EndpointGroupExtensions</c>" is
    /// one of the two things a feature that is a folder plus one line can get wrong. Three
    /// dashboards in this codebase have already been built against a route nobody mapped. So
    /// requests go through routing, the authorization policy, model binding and the serialiser, and
    /// the assertions are on the JSON a browser receives.
    ///
    /// <b>A test authentication scheme, because a test host cannot mint an Entra token.</b> It
    /// issues <c>oid</c> and nothing else - exactly what <c>CallerIdentity</c> reads and
    /// deliberately never falls back from. <see cref="ApiFactory"/> is sealed, so it is composed
    /// through <c>WithWebHostBuilder</c> rather than derived from, which keeps the SQLite database,
    /// the stubbed Cosmos readers and the container's own scope validation.
    ///
    /// <b>A request with no header authenticates as nobody</b> rather than as a default candidate.
    /// The two facts worth pinning about this group are that it is per-principal and that
    /// <c>Api:AllowAnonymousReads</c> does not open it, and neither is assertable against a harness
    /// that signs everybody in - so <see cref="AuthenticateResult.NoResult"/> is what an
    /// unauthenticated request gets, which is the difference between a 401 from the policy and a
    /// 401 from the handler.
    ///
    /// <b>Nothing is seeded into the settings table, deliberately.</b> The state this feature is
    /// judged on is the one every deployment starts in: a candidate with a profile, no stored row,
    /// and a pipeline running on the constants the shipped code already ran. Every configured state
    /// in this suite is reached by saving through the route, so nothing is asserted about a row
    /// shape only a test knows how to write.
    ///
    /// <b>Both candidates have profiles</b>, because a save needs somewhere to put a row - the
    /// table is keyed to the profile, and a subject without one gets a 404 rather than an invented
    /// profile the extraction sweep would later pick up.
    /// </remarks>
    private sealed class Harness : IDisposable
    {
        /// <summary>The candidate whose settings these are. Their token carries this as <c>oid</c>.</summary>
        public const string Ada = "11111111-1111-1111-1111-111111111111";

        /// <summary>Another candidate, whose levers must be invisible rather than forbidden.</summary>
        public const string Grace = "22222222-2222-2222-2222-222222222222";

        /// <summary>A principal with no profile at all, which is the ordinary first visit.</summary>
        public const string Stranger = "99999999-9999-9999-9999-999999999999";

        /// <summary>
        /// The instant every save in this suite is stamped with.
        /// </summary>
        /// <remarks>
        /// A fixed <see cref="TimeProvider"/> on the host, so <c>updatedUtc</c> is a constant a test
        /// can name rather than a window it has to tolerate. The distinction that field carries -
        /// "nobody has ever chosen" against "somebody chose" - is then asserted as null against an
        /// exact value, where an assertion that merely checked for non-null would pass against a
        /// mapping that fell back to "now", which the mapping's own remarks say it must never learn
        /// to do.
        /// </remarks>
        public static readonly DateTimeOffset Saved = new(2026, 9, 10, 11, 25, 0, TimeSpan.Zero);

        private readonly ApiFactory _factory = new() { AllowAnonymousReads = true };
        private readonly List<HttpClient> _clients = [];
        private WebApplicationFactory<Program> _host = null!;

        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();

            harness._host = harness._factory.WithWebHostBuilder(builder => builder.ConfigureServices(
                services =>
                {
                    services
                        .AddAuthentication(HeaderPrincipalHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, HeaderPrincipalHandler>(
                            HeaderPrincipalHandler.SchemeName, _ => { });

                    // Last registration wins, so this replaces Program.cs's TimeProvider.System
                    // for the one thing in this feature that reads a clock: the stamp on a save.
                    services.AddSingleton<TimeProvider>(new FixedTime(Saved));
                }));

            await harness.SeedAsync();

            return harness;
        }

        /// <summary>A client whose every request carries that subject, or nobody's.</summary>
        public HttpClient As(string? subject)
        {
            var client = _host.CreateClient();

            if (subject is not null)
            {
                client.DefaultRequestHeaders.Add(HeaderPrincipalHandler.SubjectHeader, subject);
            }

            _clients.Add(client);

            return client;
        }

        public void Dispose()
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _host?.Dispose();
            _factory.Dispose();
        }

        /// <summary>
        /// Two profiles and no settings rows at all.
        /// </summary>
        /// <remarks>
        /// Through <c>CandidateProfileRepository</c> rather than by inserting entities, so the rows
        /// are shaped exactly as a real save writes them - and stamped through a fixed clock,
        /// because a profile's own timestamp is not the one this feature reads and a wall-clock one
        /// would make the two harder to tell apart in a failure message.
        /// </remarks>
        private async Task SeedAsync()
        {
            using var scope = _host.Services.CreateScope();
            var profiles = new CandidateProfileRepository(
                scope.ServiceProvider.GetRequiredService<JobsDbContext>());
            var clock = new FixedTime(Saved);

            await profiles.SaveAsync(
                new CandidateProfile { SubjectId = Ada, FullName = "Ada Lovelace" }, clock);

            await profiles.SaveAsync(
                new CandidateProfile { SubjectId = Grace, FullName = "Grace Hopper" }, clock);
        }

        /// <summary>
        /// The principal a test acts as, taken from a header.
        /// </summary>
        /// <remarks>
        /// It issues <c>oid</c> and nothing else, which is exactly what <c>CallerIdentity</c> reads
        /// and deliberately never falls back from. A handler that also issued
        /// <c>ClaimTypes.NameIdentifier</c> would let a route resolving the caller the wrong way
        /// pass here and fail against a real token - and settings stored under <c>sub</c>, which is
        /// pairwise per application, would be invisible to the same person arriving through a
        /// second app registration, with a symptom that looks like data loss rather than like a
        /// claim mix-up.
        ///
        /// <see cref="AuthenticateResult.NoResult"/> for a request with no header, so an anonymous
        /// request is anonymous rather than a caller with an empty subject: the difference is a 401
        /// from the policy against a 401 from the handler, and only one of those pins the policy.
        /// </remarks>
        private sealed class HeaderPrincipalHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            public const string SchemeName = "TestPrincipal";

            public const string SubjectHeader = "X-Test-Subject";

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var subject = Request.Headers[SubjectHeader].ToString();

                if (string.IsNullOrWhiteSpace(subject))
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }

                var identity = new ClaimsIdentity([new Claim("oid", subject)], SchemeName);

                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        /// <summary>A clock that does not move, so a stored timestamp is a constant a test can name.</summary>
        private sealed class FixedTime(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
