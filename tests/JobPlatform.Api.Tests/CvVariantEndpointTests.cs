using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using JobPlatform.Core.Applications;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The candidate's CV library over HTTP: what they wrote, and what this system will not write.
/// </summary>
/// <remarks>
/// <b>Two kinds of claim are made here and they are worth separating.</b>
///
/// The first is ordinary: the library lists, a CV saves, a rename does not disturb a date, a full
/// library refuses with the numbers. Each of those fails as a 404 when the group is not registered,
/// which is the property that makes an over-HTTP suite worth the cost - a feature in this API is a
/// folder plus one line in <c>EndpointGroupExtensions</c>, and the line is exactly what a unit test
/// against a handler would not notice was missing.
///
/// The second is the reason the feature exists, and it is asserted structurally rather than
/// behaviourally. <b>Nothing in this system may write a variant's markdown from a model.</b> A
/// behavioural test proves what today's routes happen to do; what has to hold is that a
/// "regenerate", an "improve with AI" or a "rewrite for this posting" cannot be added without a
/// visible diff. So the mapped route set is asserted as an equality - the idiom
/// <c>McpEndpointTests</c> already uses to keep <c>submit_application</c> from ever existing - and
/// the concepts a model does read are asserted to land in <c>CvVariantConcepts</c> with
/// <c>ProfileConcepts</c> untouched, because a CV widening the record it was written from is the
/// one failure in this design that nothing downstream could notice.
/// </remarks>
public sealed class CvVariantEndpointTests
{
    /// <summary>Exactly what a row in the library carries. No markdown, no blob path.</summary>
    private static readonly string[] SummaryFields =
    [
        "authoredAtUtc", "isArchived", "isRenderCurrent", "isSendable", "isStale", "label",
        "renderedAtUtc", "sha256", "variantId",
    ];

    /// <summary>The summary plus the candidate's own words.</summary>
    private static readonly string[] DetailFields = [.. SummaryFields.Append("markdown").Order(StringComparer.Ordinal)];

    private static readonly string[] LibraryFields = ["capacity", "items", "staleness"];

    private static readonly string[] StalenessFields =
    [
        "anyStale", "considered", "current", "profileUpdatedUtc", "stale",
    ];

    private static readonly string[] CapacityFields = ["cap", "hasRoomForAnother", "inUse"];

    /// <summary>Every route this feature is allowed to have, by name.</summary>
    /// <remarks>
    /// <b>An equality and not a superset, which is the whole point.</b> The property being defended
    /// is an absence: there is no route that writes a CV from a model, and there is no route that
    /// deletes one. A superset assertion would accept a seventh route added by somebody being
    /// helpful about a staleness notice, which is precisely how "rewrite these for me" arrives.
    /// The count is asserted alongside the names so that a route added without <c>WithName</c>
    /// cannot slip past a set comparison.
    /// </remarks>
    private static readonly string[] RouteNames =
    [
        "CreateCvVariant", "DownloadCvVariant", "GetCvGapBrief", "GetCvVariant", "ListCvVariants",
        "ReauthorCvVariant", "RenameCvVariant", "SetCvVariantArchived",
    ];

    /// <summary>
    /// The library lists everything, and the sentence at the top agrees with the badges under it.
    /// </summary>
    /// <remarks>
    /// <b>The adding-up is the assertion.</b> A page that says "one of your CVs predates your last
    /// profile change" over two flagged rows is worse than a page that says nothing, and the two
    /// numbers come from different places - the count from <c>CvVariantLibrary.Staleness</c>, the
    /// badge from a per-row read - so the invariant that they are the same predicate is asserted
    /// rather than assumed. The archived CV is the case that separates them: it is listed, it was
    /// written before the profile moved, and it is neither counted nor flagged, because a retired
    /// CV falling behind is not a thing to nudge anybody about.
    ///
    /// <b>The rows carry no markdown</b>, following the rule <c>PostingSummary</c> already lives
    /// under. Archived variants accumulate for as long as somebody keeps rewriting CVs, so a list
    /// that carried the documents would grow without bound on the one route a dashboard opens
    /// first.
    /// </remarks>
    [Fact]
    public async Task The_library_lists_every_CV_and_its_staleness_adds_up()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var body = await LibraryAsync(harness, CvLibraryHarness.Ada);

        Assert.Equal(LibraryFields, Names(body));

        var items = body.GetProperty("items").EnumerateArray().ToList();

        // Live first and then in authoring order, which is the repository's presentation and never
        // a ranking - ranking is the selector's job.
        Assert.Equal([harness.Backend, harness.Retired], items.Select(item => item.GetProperty("variantId").GetInt64()));

        var backend = items[0];

        Assert.Equal(SummaryFields, Names(backend));
        Assert.Equal("Backend .NET", backend.GetProperty("label").GetString());
        Assert.True(backend.GetProperty("isRenderCurrent").GetBoolean());
        Assert.True(backend.GetProperty("isSendable").GetBoolean());
        Assert.True(backend.GetProperty("isStale").GetBoolean());
        Assert.False(backend.GetProperty("isArchived").GetBoolean());

        var retired = items[1];

        Assert.True(retired.GetProperty("isArchived").GetBoolean());
        Assert.False(retired.GetProperty("isSendable").GetBoolean());
        Assert.False(retired.GetProperty("isStale").GetBoolean());

        var staleness = body.GetProperty("staleness");

        Assert.Equal(StalenessFields, Names(staleness));
        Assert.Equal(1, staleness.GetProperty("considered").GetInt32());
        Assert.Equal(1, staleness.GetProperty("stale").GetInt32());
        Assert.Equal(0, staleness.GetProperty("current").GetInt32());
        Assert.True(staleness.GetProperty("anyStale").GetBoolean());
        Assert.Equal(
            CvLibraryHarness.ProfileChanged,
            staleness.GetProperty("profileUpdatedUtc").GetDateTimeOffset());

        // The invariant: the number in the sentence is the number of flagged rows, always.
        Assert.Equal(
            staleness.GetProperty("stale").GetInt32(),
            items.Count(item => item.GetProperty("isStale").GetBoolean()));

        var capacity = body.GetProperty("capacity");

        Assert.Equal(CapacityFields, Names(capacity));
        Assert.Equal(1, capacity.GetProperty("inUse").GetInt32());
        Assert.Equal(CvVariantLimits.MaxPerProfile, capacity.GetProperty("cap").GetInt32());
        Assert.True(capacity.GetProperty("hasRoomForAnother").GetBoolean());
    }

    /// <summary>
    /// A saved CV is laid out, uploaded and read for concepts inside the request that saved it.
    /// </summary>
    /// <remarks>
    /// <b>Sendable in the same response, or the person has no way to know when it will be.</b>
    /// There is no queue and no scheduled pass behind this: rendering is deterministic, needs no
    /// model, and takes a moment - so a variant that saves and only becomes selectable later would
    /// be a state nothing in this system reports on.
    ///
    /// <b>The digest is the PDF's, checked against the bytes the store was actually handed.</b>
    /// "What exactly did we send them" is a question about a file in somebody else's system, and a
    /// hash that described the DOCX on some rows would answer it wrongly rather than not at all.
    /// </remarks>
    [Fact]
    public async Task A_new_CV_is_stored_rendered_and_read_in_the_request_that_saved_it()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var response = await harness.As(CvLibraryHarness.Ada)
            .PostAsJsonAsync("/api/v1/cv-variants", new { label = "AI and data platforms", markdown = $"\n{CvLibraryHarness.Cv}\n" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(DetailFields, Names(created));
        Assert.Equal("AI and data platforms", created.GetProperty("label").GetString());

        // Stored rather than echoed: Create trims the ends, and a client redisplaying its own
        // request body would show a document that differs from the one on disk.
        Assert.Equal(CvLibraryHarness.Cv, created.GetProperty("markdown").GetString());
        Assert.True(created.GetProperty("isRenderCurrent").GetBoolean());
        Assert.True(created.GetProperty("isSendable").GetBoolean());
        Assert.False(created.GetProperty("isStale").GetBoolean());

        var id = created.GetProperty("variantId").GetInt64();

        Assert.Equal(
            [PackFormat.Pdf, PackFormat.Docx],
            harness.Files.Requests.Select(request => request.Format));
        Assert.All(harness.Files.Requests, request => Assert.Equal(id, request.VariantId));
        Assert.All(harness.Files.Requests, request => Assert.Equal(harness.AdaProfile, request.ProfileId));

        // The name on the file is the person's, never the CV's. Two applications made with two
        // different variants upload files whose names are identical, which is the whole reason the
        // label has nowhere to appear.
        Assert.All(harness.Files.Requests, request => Assert.Equal("Ada Lovelace", request.CandidateName));

        var pdf = harness.Files.Requests.Single(request => request.Format == PackFormat.Pdf).Content;

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(pdf)),
            created.GetProperty("sha256").GetString());

        var read = Assert.Single(harness.Extractor.Requests);

        Assert.Equal(id, read.VariantId);
        Assert.Equal(CvLibraryHarness.Cv, read.Markdown);
        Assert.Equal("AI and data platforms", read.Label);
    }

    /// <summary>
    /// A variant's concepts reach its own table and never the profile's.
    /// </summary>
    /// <remarks>
    /// <b>The one guard this change adds, and the only one whose failure nothing downstream could
    /// notice.</b> A CV is written <i>from</i> the profile, so letting its reading back in would let
    /// a document inflate the record it came from - after which the apply loop applies to jobs on
    /// the strength of its own prose, and the extra concepts look exactly like ones the candidate
    /// declared. It is asserted against the rows rather than against a route, because no route
    /// projects either table and the failure would be a write nobody sees.
    /// </remarks>
    [Fact]
    public async Task A_variants_concepts_are_stored_against_the_variant_and_not_the_profile()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var created = await CreateAsync(harness, "Platform", CvLibraryHarness.Cv);
        var id = created.GetProperty("variantId").GetInt64();

        using var db = harness.Database();

        var stored = await db.CvVariantConcepts.AsNoTracking()
            .Where(concept => concept.VariantId == id)
            .Include(concept => concept.Concept)
            .ToListAsync();

        Assert.Equal(
            ["skill.csharp", "skill.kubernetes"],
            stored.Select(concept => concept.Concept!.ConceptKey).Order(StringComparer.Ordinal));

        Assert.Empty(await db.ProfileConcepts.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Re-authoring moves the words and their date together, and renders again.
    /// </summary>
    /// <remarks>
    /// <b>The pairing is what makes the render comparison answer honestly.</b> A writer free to
    /// move the markdown without the date can leave last week's PDF looking current, which is a
    /// paragraph the candidate deleted going to an employer under their name. Asserted through the
    /// response rather than the row, because that pair is what a client decides from.
    ///
    /// <b>Saving an unchanged document is still an edit</b>, which is why the second half asserts
    /// the date moves for a save that changes nothing: somebody who reads a flagged CV, decides it
    /// is still accurate and presses save has answered the nudge.
    /// </remarks>
    [Fact]
    public async Task Re_authoring_moves_the_words_and_their_date_and_renders_again()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var edited = await PutAsync(
            harness, CvLibraryHarness.Ada, $"{harness.Backend}/markdown",
            new { markdown = $"{CvLibraryHarness.Cv}\n\n- And ran it again." });

        Assert.Equal(DetailFields, Names(edited));
        Assert.Contains("ran it again", edited.GetProperty("markdown").GetString());

        // The label is untouched by a write about the words.
        Assert.Equal("Backend .NET", edited.GetProperty("label").GetString());

        var authored = edited.GetProperty("authoredAtUtc").GetDateTimeOffset();

        Assert.True(authored > CvLibraryHarness.Written);
        Assert.True(edited.GetProperty("renderedAtUtc").GetDateTimeOffset() >= authored);
        Assert.True(edited.GetProperty("isRenderCurrent").GetBoolean());
        Assert.True(edited.GetProperty("isSendable").GetBoolean());

        Assert.Equal(2, harness.Files.Requests.Count);
        Assert.Single(harness.Extractor.Requests);

        // A save that changes nothing is a person asserting the CV is current as of now. Converging
        // on "no change" would leave the staleness notice standing after they had answered it.
        var again = await PutAsync(
            harness, CvLibraryHarness.Ada, $"{harness.Backend}/markdown",
            new { markdown = edited.GetProperty("markdown").GetString() });

        Assert.True(again.GetProperty("authoredAtUtc").GetDateTimeOffset() >= authored);
        Assert.Equal(4, harness.Files.Requests.Count);
    }

    /// <summary>
    /// A rename changes the name and touches nothing else at all.
    /// </summary>
    /// <remarks>
    /// <b>Not even a re-render, and that is the cheap half of the same argument.</b> The words have
    /// not changed, so the stored files still describe them and the stored concepts are still what
    /// the document says - and neither carries the label, deliberately, since a title travels inside
    /// a PDF and tells an employer that a different CV is kept for other roles. Re-rendering here
    /// would spend two uploads to produce byte-identical files; re-extracting would spend a model
    /// call to store the same rows.
    /// </remarks>
    [Fact]
    public async Task A_rename_leaves_the_date_the_render_and_the_concepts_alone()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var renamed = await PutAsync(
            harness, CvLibraryHarness.Ada, $"{harness.Backend}/label", new { label = "Platform engineering" });

        Assert.Equal("Platform engineering", renamed.GetProperty("label").GetString());
        Assert.Equal(CvLibraryHarness.Written, renamed.GetProperty("authoredAtUtc").GetDateTimeOffset());
        Assert.Equal(CvLibraryHarness.Rendered, renamed.GetProperty("renderedAtUtc").GetDateTimeOffset());
        Assert.True(renamed.GetProperty("isRenderCurrent").GetBoolean());
        Assert.True(renamed.GetProperty("isStale").GetBoolean());

        Assert.Empty(harness.Files.Requests);
        Assert.Empty(harness.Extractor.Requests);
    }

    /// <summary>
    /// Archiving is an update, unarchiving undoes it, and there is no delete on this resource.
    /// </summary>
    /// <remarks>
    /// <b>The 405 is the assertion worth reading.</b> A submission records the variant it sent, so
    /// an application made last year is explained by a document that has to still exist, byte for
    /// byte, under the hash stored beside it - and a system that answered "what did you send them"
    /// with "that CV was deleted" would have lost the record it exists to keep. The route pattern
    /// exists and the verb does not, which is a stronger statement than a 404.
    ///
    /// <b>The words, the paths and the digest survive.</b> Archiving takes a variant out of
    /// selection, out of the cap's count and out of the unique index over live labels, and changes
    /// nothing else about the row.
    /// </remarks>
    [Fact]
    public async Task Archiving_is_an_update_and_nothing_here_deletes_a_CV()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var archived = await PutAsync(
            harness, CvLibraryHarness.Ada, $"{harness.Backend}/archived", new { archived = true });

        Assert.True(archived.GetProperty("isArchived").GetBoolean());
        Assert.False(archived.GetProperty("isSendable").GetBoolean());
        Assert.False(archived.GetProperty("isStale").GetBoolean());
        Assert.Equal(CvLibraryHarness.Written, archived.GetProperty("authoredAtUtc").GetDateTimeOffset());
        Assert.NotNull(archived.GetProperty("sha256").GetString());
        Assert.Equal(CvLibraryHarness.Cv, archived.GetProperty("markdown").GetString());

        using (var db = harness.Database())
        {
            var row = await db.CvVariants.AsNoTracking().SingleAsync(v => v.Id == harness.Backend);

            Assert.True(row.IsArchived);
            Assert.NotNull(row.PdfBlobPath);
            Assert.NotNull(row.DocxBlobPath);
            Assert.NotNull(row.Sha256);
        }

        var deleted = await harness.As(CvLibraryHarness.Ada)
            .DeleteAsync($"/api/v1/cv-variants/{harness.Backend}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, deleted.StatusCode);

        // And back, with its dates untouched: a CV brought back is as old as it was.
        var restored = await PutAsync(
            harness, CvLibraryHarness.Ada, $"{harness.Backend}/archived", new { archived = false });

        Assert.False(restored.GetProperty("isArchived").GetBoolean());
        Assert.True(restored.GetProperty("isSendable").GetBoolean());
        Assert.True(restored.GetProperty("isStale").GetBoolean());
        Assert.Equal(CvLibraryHarness.Written, restored.GetProperty("authoredAtUtc").GetDateTimeOffset());
    }

    /// <summary>
    /// A full library refuses with the count and the cap, and archived CVs do not hold a place.
    /// </summary>
    /// <remarks>
    /// <b>"No" on its own leaves a person guessing.</b> Somebody holding six CVs who is told only
    /// that they cannot write a seventh has to work out both what the limit is and that archiving -
    /// rather than deleting, which is unavailable - is how room is made. The refusal carries both
    /// numbers, and the read that produces them happens only on this path.
    ///
    /// <b>409 rather than 400</b>, because nothing is wrong with the request: the cap was not spent
    /// last week, and a caller told "bad request" would go looking at what it sent.
    /// </remarks>
    [Fact]
    public async Task A_full_library_is_refused_with_the_count_and_the_cap()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        // The archived one first, because a library at the cap refuses every write including the
        // seed - and it must not occupy a place once it is retired. Then Backend and these fill it.
        await harness.SeedVariantAsync(harness.AdaProfile, "Long retired", archived: true);

        for (var extra = 1; extra < CvVariantLimits.MaxPerProfile; extra++)
        {
            await harness.SeedVariantAsync(harness.AdaProfile, $"Spare {extra}");
        }

        var refused = await harness.As(CvLibraryHarness.Ada)
            .PostAsJsonAsync("/api/v1/cv-variants", new { label = "One too many", markdown = CvLibraryHarness.Cv });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var detail = await DetailOf(refused);

        Assert.Contains($"keeping {CvVariantLimits.MaxPerProfile}", detail, StringComparison.Ordinal);
        Assert.Contains($"holds {CvVariantLimits.MaxPerProfile}", detail, StringComparison.Ordinal);
        Assert.Contains("Archive one", detail, StringComparison.Ordinal);

        using (var db = harness.Database())
        {
            Assert.DoesNotContain(
                await db.CvVariants.AsNoTracking().Select(v => v.Label).ToListAsync(), label => label == "One too many");
        }

        // Archiving one makes room, which is the sentence the refusal just told them to act on.
        await PutAsync(harness, CvLibraryHarness.Ada, $"{harness.Backend}/archived", new { archived = true });

        var accepted = await harness.As(CvLibraryHarness.Ada)
            .PostAsJsonAsync("/api/v1/cv-variants", new { label = "One too many", markdown = CvLibraryHarness.Cv });

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    /// <summary>
    /// A name another live CV answers to is refused; an archived one does not hold its name.
    /// </summary>
    /// <remarks>
    /// Rewriting a CV and giving the new one the old one's name is the ordinary case - "Backend
    /// .NET" superseded by a better "Backend .NET" - and a rule reserving a label forever would push
    /// people into calling their CVs "Backend .NET v3" to get around a constraint meant to help
    /// them. History does not suffer for it: a submission records the variant's id, so the label is
    /// the live handle and the id is the record.
    /// </remarks>
    [Fact]
    public async Task A_label_in_use_is_refused_and_an_archived_one_gives_its_name_back()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        // Folded on case and spacing, because two CVs a reader cannot tell apart are two CVs
        // nobody can choose between.
        var refused = await harness.As(CvLibraryHarness.Ada)
            .PostAsJsonAsync("/api/v1/cv-variants", new { label = "backend  .net", markdown = CvLibraryHarness.Cv });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("archived", await DetailOf(refused), StringComparison.OrdinalIgnoreCase);

        await PutAsync(harness, CvLibraryHarness.Ada, $"{harness.Backend}/archived", new { archived = true });

        var accepted = await harness.As(CvLibraryHarness.Ada)
            .PostAsJsonAsync("/api/v1/cv-variants", new { label = "Backend .NET", markdown = CvLibraryHarness.Cv });

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        // And the archived one cannot come back while its name is taken - an outcome the person can
        // act on rather than a DbUpdateException on a page.
        var collision = await harness.As(CvLibraryHarness.Ada)
            .PutAsJsonAsync($"/api/v1/cv-variants/{harness.Backend}/archived", new { archived = false });

        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
    }

    /// <summary>
    /// A blank CV, an over-long one and a label nobody could pick a CV by are refused, not fixed up.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than trimmed or truncated, and the reason differs at each end.</b> A blank
    /// variant renders to a blank PDF, which uploads to an employer as cleanly as a real one and is
    /// discovered by a person reading it. An over-long one is somebody having pasted the wrong thing
    /// entirely - a portfolio site, the text dump of a PDF - and a CV cut at twenty thousand
    /// characters ends mid-sentence in front of a recruiter. A label of punctuation is
    /// indistinguishable from its neighbours in a picker and unquotable in the pack's account of
    /// which CV it chose.
    ///
    /// <b>The bound is not restated by the route.</b> These are <c>CvVariant.Create</c>'s refusals
    /// mapped onto a 400: a validator here would be a second copy of a number that has already
    /// drifted from a column width once in this codebase, and the failure after a drift is a 500 on
    /// somebody's save with their document lost.
    /// </remarks>
    [Fact]
    public async Task A_document_or_a_label_that_could_never_be_stored_is_refused()
    {
        using var harness = await CvLibraryHarness.CreateAsync();
        var client = harness.As(CvLibraryHarness.Ada);

        var blank = await client.PostAsJsonAsync(
            "/api/v1/cv-variants", new { label = "Blank", markdown = "   " });

        var overLong = await client.PostAsJsonAsync(
            "/api/v1/cv-variants",
            new { label = "Pasted", markdown = new string('x', CvVariantLimits.MaxMarkdownLength + 1) });

        var unusable = await client.PostAsJsonAsync(
            "/api/v1/cv-variants", new { label = "...", markdown = CvLibraryHarness.Cv });

        var overLongLabel = await client.PutAsJsonAsync(
            $"/api/v1/cv-variants/{harness.Backend}/label",
            new { label = new string('x', CvVariantLimits.MaxLabelLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unusable.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overLongLabel.StatusCode);

        // Nothing was stored and nothing was rendered - a refused write must not leave a file in a
        // container with no row pointing at it.
        using var db = harness.Database();

        Assert.Equal(3, await db.CvVariants.AsNoTracking().CountAsync());
        Assert.Empty(harness.Files.Requests);
        Assert.Equal("Backend .NET", (await db.CvVariants.AsNoTracking().SingleAsync(v => v.Id == harness.Backend)).Label);
    }

    /// <summary>
    /// A failed render does not fail the save, and the answer says the CV is not sendable yet.
    /// </summary>
    /// <remarks>
    /// <b>The markdown is the record and the files are a copy of it.</b> A variant whose render
    /// failed is recoverable by saving it again; typing lost to a storage failure is not. What makes
    /// swallowing defensible is that it is silent rather than invisible: <c>isSendable</c> is false
    /// in the same response, so the page can say a CV needs rendering instead of an application
    /// being made against a document that does not exist.
    /// </remarks>
    [Fact]
    public async Task A_failed_render_does_not_cost_the_candidate_their_document()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        harness.Files.Refuse = PackFormat.Pdf;

        var created = await CreateAsync(harness, "Data platforms", CvLibraryHarness.Cv);

        Assert.Equal(CvLibraryHarness.Cv, created.GetProperty("markdown").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("renderedAtUtc").ValueKind);
        Assert.False(created.GetProperty("isRenderCurrent").GetBoolean());
        Assert.False(created.GetProperty("isSendable").GetBoolean());

        using var db = harness.Database();

        Assert.NotNull(await db.CvVariants.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == created.GetProperty("variantId").GetInt64()));
    }

    /// <summary>
    /// With no storage and no provider the library is text, which is a supported deployment.
    /// </summary>
    /// <remarks>
    /// This is the shape the system ships in: no <c>ApplicationPacks:serviceUri</c> registers no
    /// renderer and no AI provider registers no extractor. A CV saved on such a host is kept and
    /// editable and simply never selectable - which <c>CvVariant.IsSendable</c> already says out
    /// loud. A 503 would be the wrong answer, unlike on document generation, where a missing writer
    /// means there is nothing to produce at all.
    /// </remarks>
    [Fact]
    public async Task A_host_with_no_storage_and_no_provider_still_keeps_the_words()
    {
        using var harness = await CvLibraryHarness.CreateAsync(rendering: false);

        var created = await CreateAsync(harness, "Data platforms", CvLibraryHarness.Cv);

        Assert.Equal(CvLibraryHarness.Cv, created.GetProperty("markdown").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("renderedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("sha256").ValueKind);
        Assert.False(created.GetProperty("isSendable").GetBoolean());

        using var db = harness.Database();

        Assert.Empty(await db.CvVariantConcepts.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Somebody else's CV does not exist as far as this caller is concerned.
    /// </summary>
    /// <remarks>
    /// 404 rather than 403, because a 403 confirms that a CV with that id exists and belongs to
    /// somebody - a fact about another person's job search. The repository takes the profile id in
    /// the predicate, so "not yours" and "no such CV" are one answer by construction, and a
    /// stranger's document is never materialised at all.
    /// </remarks>
    [Fact]
    public async Task Another_candidates_CV_is_invisible_rather_than_forbidden()
    {
        using var harness = await CvLibraryHarness.CreateAsync();
        var ada = harness.As(CvLibraryHarness.Ada);

        var read = await ada.GetAsync($"/api/v1/cv-variants/{harness.Hers}");
        var renamed = await ada.PutAsJsonAsync(
            $"/api/v1/cv-variants/{harness.Hers}/label", new { label = "Mine now" });
        var reauthored = await ada.PutAsJsonAsync(
            $"/api/v1/cv-variants/{harness.Hers}/markdown", new { markdown = CvLibraryHarness.Cv });
        var archived = await ada.PutAsJsonAsync(
            $"/api/v1/cv-variants/{harness.Hers}/archived", new { archived = true });

        foreach (var response in new[] { read, renamed, reauthored, archived })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var hers = await LibraryAsync(harness, CvLibraryHarness.Grace);

        Assert.Equal(
            [harness.Hers],
            hers.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("variantId").GetInt64()));

        using var db = harness.Database();
        var untouched = await db.CvVariants.AsNoTracking().SingleAsync(v => v.Id == harness.Hers);

        Assert.Equal("Compilers", untouched.Label);
        Assert.False(untouched.IsArchived);
    }

    /// <summary>
    /// The detail route carries the words the list does not, and archiving does not hide them.
    /// </summary>
    /// <remarks>
    /// Reading a variant deliberately ignores <c>IsArchived</c>: a submission records the variant it
    /// sent, and "what did we send them" has to answer after the CV has been retired, which is the
    /// ordinary end of a CV's life.
    /// </remarks>
    [Fact]
    public async Task An_archived_CV_is_still_readable_with_the_words_that_were_sent()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var response = await harness.As(CvLibraryHarness.Ada)
            .GetAsync($"/api/v1/cv-variants/{harness.Retired}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(DetailFields, Names(detail));
        Assert.True(detail.GetProperty("isArchived").GetBoolean());
        Assert.Equal(CvLibraryHarness.Cv, detail.GetProperty("markdown").GetString());
    }

    /// <summary>
    /// A principal with no profile has an empty library, and a CV needs somewhere to belong.
    /// </summary>
    /// <remarks>
    /// An empty list rather than a 404 on the read, following the submission and question queues:
    /// somebody who has not filled the form in is not an error, and a 404 would send the dashboard
    /// down its "something is wrong" path on the ordinary first visit. The write is refused instead
    /// of creating an implicit profile, because a CV is stored against one, rendered under it and
    /// compared against its last change - and inventing one would store an employment history the
    /// person has not entered.
    /// </remarks>
    [Fact]
    public async Task A_principal_with_no_profile_has_an_empty_library_and_cannot_write_a_CV()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var body = await LibraryAsync(harness, CvLibraryHarness.Stranger);

        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("staleness").GetProperty("considered").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("staleness").GetProperty("profileUpdatedUtc").ValueKind);
        Assert.True(body.GetProperty("capacity").GetProperty("hasRoomForAnother").GetBoolean());

        var refused = await harness.As(CvLibraryHarness.Stranger)
            .PostAsJsonAsync("/api/v1/cv-variants", new { label = "First", markdown = CvLibraryHarness.Cv });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("profile", await DetailOf(refused), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The anonymous-reads switch must not open this group.
    /// </summary>
    /// <remarks>
    /// <c>Api:AllowAnonymousReads</c> exists to open the posting corpus, which is public text. A CV
    /// is the opposite - somebody's employment history in their own words, and the single most
    /// personal document this system holds. The harness turns the switch on precisely so this can be
    /// asserted.
    /// </remarks>
    [Fact]
    public async Task The_library_stays_closed_even_when_anonymous_reads_are_allowed()
    {
        using var harness = await CvLibraryHarness.CreateAsync();
        var anonymous = harness.As(subject: null);

        Assert.Equal(
            HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/cv-variants")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/v1/cv-variants/{harness.Backend}")).StatusCode);

        var write = await anonymous.PostAsJsonAsync(
            "/api/v1/cv-variants", new { label = "Theirs", markdown = CvLibraryHarness.Cv });

        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    /// <summary>
    /// The route surface is exactly these eight, which is how the absent ninth stays absent.
    /// </summary>
    /// <remarks>
    /// <b>An equality rather than a superset, and the property is what is missing.</b> There is no
    /// route that writes a variant's markdown from a model - no "regenerate", no "improve with AI",
    /// no "rewrite for this posting" - because rewriting the candidate's document is exactly what
    /// this change removes, and a staleness notice is the most natural place in the product for
    /// somebody to hang one. A superset assertion would accept it silently; this makes adding a
    /// route a diff somebody signs off.
    ///
    /// <b>And no verb that destroys.</b> Archiving is an update, and a <c>DELETE</c> anywhere on
    /// this resource would make an application made last year unexplainable in order to tidy a row.
    /// </remarks>
    [Fact]
    public void The_route_surface_is_exactly_these_eight_and_none_of_them_writes_a_CV()
    {
        var routes = VariantRoutes();

        Assert.Equal(RouteNames.Length, routes.Count);

        Assert.Equal(
            RouteNames,
            routes
                .Select(route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
                .OfType<string>()
                .Order(StringComparer.Ordinal));

        var methods = routes
            .SelectMany(route => route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["GET", "POST", "PUT"], methods);
    }

    /// <summary>
    /// The policy on the group, asserted as metadata rather than through a response.
    /// </summary>
    /// <remarks>
    /// The behavioural version above cannot pin it on its own: every handler also calls
    /// <c>CallerIdentity.TryGetSubjectId</c>, which answers 401 for a token with no <c>oid</c>, so
    /// an anonymous request answers 401 whichever policy is on the group. That is defence in depth
    /// working and a test measuring the second layer while describing the first.
    /// </remarks>
    [Fact]
    public void Every_cv_variant_route_requires_the_authenticated_policy()
    {
        foreach (var route in VariantRoutes())
        {
            var policies = route.Metadata
                .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Select(data => data.Policy)
                .ToList();

            Assert.Contains(JobPlatform.Api.Infrastructure.AuthSetup.AuthenticatedPolicy, policies);
        }
    }

    /// <summary>
    /// Nothing here is output cached, which is a safety property rather than a tuning choice.
    /// </summary>
    /// <remarks>
    /// The library is per-principal and mutable, and the cache is keyed on a URL with no user in it
    /// - so caching <c>/api/v1/cv-variants</c> is how one person is served another's CVs, and how an
    /// archived CV keeps appearing as sendable to the person who archived it.
    /// </remarks>
    [Fact]
    public void No_cv_variant_route_is_output_cached()
    {
        foreach (var route in VariantRoutes())
        {
            Assert.DoesNotContain(
                route.Metadata,
                item => item.GetType().Name.Contains("OutputCache", StringComparison.Ordinal));
        }
    }

    private static List<RouteEndpoint> VariantRoutes()
    {
        using var factory = new ApiFactory { AllowAnonymousReads = true };

        // Forces the host to build; the endpoint data source is not populated before it does.
        using var client = factory.CreateClient();

        return [.. factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?
                .StartsWith("/api/v1/cv-variants", StringComparison.Ordinal) == true)];
    }

    private static async Task<JsonElement> LibraryAsync(CvLibraryHarness harness, string subject)
    {
        var response = await harness.As(subject).GetAsync("/api/v1/cv-variants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> CreateAsync(
        CvLibraryHarness harness, string label, string markdown)
    {
        var response = await harness.As(CvLibraryHarness.Ada)
            .PostAsJsonAsync("/api/v1/cv-variants", new { label, markdown });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PutAsync(
        CvLibraryHarness harness, string subject, string path, object body)
    {
        var response = await harness.As(subject).PutAsJsonAsync($"/api/v1/cv-variants/{path}", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// The download serves a real PDF, under the one filename every application uses.
    /// </summary>
    /// <remarks>
    /// <b>The route existed nowhere until a review looked for it, and the page had been calling it
    /// all along.</b> That is the third time in this codebase a dashboard has been built against a
    /// route nobody mapped - the question queue, the gap brief, and this - so the name equality
    /// above is not enough on its own: it proves something is mapped, not that it answers. This
    /// asks for the bytes.
    ///
    /// <b>And it asserts the filename, which is the rule the whole of section six exists for.</b>
    /// Somebody checking their own CV should see exactly what the employer sees, named the way the
    /// employer sees it - never after the variant, which would say that a different CV is kept for
    /// other roles.
    /// </remarks>
    [Fact]
    public async Task The_download_serves_the_rendered_cv_under_the_stable_filename()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var created = await CreateAsync(
            harness, "Platform engineering", "# Ada Lovelace\n\n## Summary\n\nEngineer.");

        var id = created.GetProperty("variantId").GetInt64();

        var response = await harness.As(CvLibraryHarness.Ada).GetAsync($"/api/v1/cv-variants/{id}/cv.pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);

        var name = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        Assert.EndsWith("Curriculum_Vitae.pdf", name, StringComparison.Ordinal);
        Assert.DoesNotContain("Platform", name, StringComparison.OrdinalIgnoreCase);

        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal("%PDF"u8.ToArray(), bytes.Take(4).ToArray());
    }

    /// <summary>An extension this system does not render is a route that does not exist.</summary>
    [Fact]
    public async Task A_format_nobody_renders_is_not_found()
    {
        using var harness = await CvLibraryHarness.CreateAsync();

        var created = await CreateAsync(harness, "Platform", "# Ada\n\nEngineer.");

        var response = await harness.As(CvLibraryHarness.Ada)
            .GetAsync($"/api/v1/cv-variants/{created.GetProperty("variantId").GetInt64()}/cv.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }


    private static async Task<string?> DetailOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();

    /// <summary>The property names of one object, sorted, so a contract can be asserted as a set.</summary>
    private static IReadOnlyList<string> Names(JsonElement element)
        => [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];
}
