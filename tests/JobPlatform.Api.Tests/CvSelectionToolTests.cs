using System.Text.Json;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The half of the pack that chooses a CV, and the half that says what to write when none fits.
/// </summary>
/// <remarks>
/// <b>These are the assertions that make the feature real rather than present.</b> Core pins the
/// arithmetic exactly and Data pins the queue clause; what is untested until here is the join
/// between them - that the pack runs a selection at all, that it hands over the winner rather than
/// a draft's old CV, that abstaining actually parks the posting with the concepts that would
/// release it, and that a run can read back what the candidate has not written.
///
/// <b>The one this file exists for is the abstention.</b> Sending the nearest CV to a job it does
/// not fit is the failure a curated library replaces, and it is invisible: the application simply
/// never comes back and nothing in the system ever learns why. So "no CV was offered" is asserted
/// as an equality against null rather than as an absence of links, and the park that carries the
/// brief is read back out of the database rather than off the tool's own account of itself.
/// </remarks>
public sealed class CvSelectionToolTests
{
    /// <summary>
    /// Nothing fits, so nothing is sent and the posting is parked with what it wanted.
    /// </summary>
    /// <remarks>
    /// <b>Three separate claims, and dropping any one of them rebuilds a different failure.</b> No
    /// CV, or the loop sends a document aimed at the wrong role. Parked, or the next run meets the
    /// same advert, computes the same gap and declines again for ever. <i>With the concepts</i>, or
    /// the park is held for ever instead - the queue's release clause reads an empty standing set
    /// as "nothing has been covered yet", which is why <c>park_application</c> refuses this reason
    /// outright and only the pack may establish it.
    /// </remarks>
    [Fact]
    public async Task A_posting_no_variant_fits_gets_no_cv_and_is_parked_with_its_gaps()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.NoCvFit));

        var selection = pack.GetProperty("cvSelection");

        Assert.Equal("NoFit", selection.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, selection.GetProperty("cvVariantId").ValueKind);
        Assert.Equal(JsonValueKind.Null, selection.GetProperty("decidedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, pack.GetProperty("curriculumVitaeMarkdown").ValueKind);
        Assert.Equal(
            JsonValueKind.Null, pack.GetProperty("documentUrls").GetProperty("cvSha256").ValueKind);

        Assert.True(selection.GetProperty("parked").GetBoolean());

        // The note has to say the abstention produced something, or an unattended run reads "no
        // CV" as a failure and either retries or attaches whatever it has.
        Assert.Contains("told what to write", pack.GetProperty("note").GetString());

        var missing = selection.GetProperty("missingConcepts").EnumerateArray()
            .Select(gap => gap.GetProperty("key").GetString())
            .ToList();

        Assert.Contains("skill.terraform", missing);
        Assert.Contains("skill.react", missing);

        // Read out of the database rather than off the answer: the release clause joins to these
        // rows, and a park whose gaps were never written is a posting held for ever with nothing
        // saying what would let it out.
        await using var db = harness.Database();

        var parked = await db.Submissions
            .AsNoTracking()
            .Include(submission => submission.ParkGaps)
            .ThenInclude(gap => gap.Concept)
            .SingleAsync(submission => submission.PostingId == McpToolHarness.NoCvFit);

        Assert.Equal(ParkReason.NoCvVariant, parked.ParkedReason);
        Assert.Null(parked.UnparkedAtUtc);

        Assert.Equal(
            ["skill.react", "skill.terraform"],
            parked.ParkGaps.Select(gap => gap.Concept!.ConceptKey).OrderBy(key => key, StringComparer.Ordinal));
    }

    /// <summary>
    /// A parked posting leaves the queue, and stays out until a covering CV exists.
    /// </summary>
    /// <remarks>
    /// The end-to-end version of the queue's fifth clause. It is asserted here rather than only in
    /// Data because the two halves were written by different files and the park is what joins
    /// them: a pack that recorded the reason without the gaps, or the gaps against the wrong
    /// submission, would leave this posting in the queue and produce the loop the reason exists to
    /// end - the same advert, every run, for ever.
    /// </remarks>
    [Fact]
    public async Task A_posting_parked_for_want_of_a_cv_leaves_the_applyable_queue()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.NoCvFit);

        var queue = McpToolHarness.Read(
            await harness.Tools().ListApplyableAsync(McpToolHarness.AsCandidate()));

        var offered = queue.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("postingId").GetInt64())
            .ToList();

        Assert.DoesNotContain(McpToolHarness.NoCvFit, offered);
        Assert.Contains(McpToolHarness.WithDocuments, offered);
    }

    /// <summary>
    /// Two CVs that fit equally are not separated, and nothing is sent.
    /// </summary>
    /// <remarks>
    /// <b>And it is not parked either, which is the part most likely to be "fixed".</b> A
    /// <c>NoCvVariant</c> park is released when a variant covers what the park recorded as
    /// missing, and a tie has recorded nothing missing - two CVs fit. The queue holds a park with
    /// no standing gaps deliberately, so parking here would turn "we could not choose between two
    /// good CVs" into "this posting is gone", silently and for ever. Left alone it comes back next
    /// run, where a person or a changed library settles it.
    /// </remarks>
    [Fact]
    public async Task A_tie_with_no_provider_configured_sends_nothing_and_parks_nothing()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.TiedCv));

        var selection = pack.GetProperty("cvSelection");

        Assert.Equal("Ambiguous", selection.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, selection.GetProperty("cvVariantId").ValueKind);
        Assert.False(selection.GetProperty("parked").GetBoolean());

        // The contenders are reported, because a person settling this needs to know which two.
        Assert.Equal(2, selection.GetProperty("tied").GetArrayLength());

        var queue = McpToolHarness.Read(
            await harness.Tools().ListApplyableAsync(McpToolHarness.AsCandidate()));

        Assert.Contains(
            McpToolHarness.TiedCv,
            queue.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("postingId").GetInt64()));
    }

    /// <summary>
    /// The model settles a tie, over a bounded ballot, and its answer is one of a fixed set.
    /// </summary>
    /// <remarks>
    /// <b>The ballot is asserted as well as the answer.</b> A tie can be the whole library - an
    /// advert stating nothing that discriminates ties everything - so an unbounded ballot turns a
    /// tie-break into a preference over a list of names. And <c>decidedBy</c> is asserted because
    /// it is the difference between a choice reproducible from stored rows and a judgement that
    /// need not repeat, which is what C4 has to be able to tell apart afterwards.
    /// </remarks>
    [Fact]
    public async Task A_tie_is_settled_by_the_model_from_the_ballot_it_was_offered()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var chooser = new StubCvChooser(harness.DataVariant);

        var pack = McpToolHarness.Read(await harness.Tools(chooser).GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.TiedCv));

        var selection = pack.GetProperty("cvSelection");

        Assert.Equal("Ambiguous", selection.GetProperty("outcome").GetString());
        Assert.Equal(harness.DataVariant, selection.GetProperty("cvVariantId").GetInt64());
        Assert.Equal("model", selection.GetProperty("decidedBy").GetString());
        Assert.Contains("Data platforms", pack.GetProperty("curriculumVitaeMarkdown").GetString()!);

        var ballot = Assert.Single(chooser.Ballots);

        Assert.Equal(2, ballot.Count);
        Assert.All(ballot, entry => Assert.True(entry.VariantId > 0));
    }

    /// <summary>
    /// An id the ballot did not offer is refused, not repaired.
    /// </summary>
    /// <remarks>
    /// <b>The rule <c>KernelDocumentExtractor</c> follows for concept keys, applied to a document
    /// id.</b> A hallucinated variant id is indistinguishable from a real one the moment it is
    /// written onto a submission, and it would say a CV went to an employer that never did - in
    /// the one column outcome feedback correlates replies against. There is no nearest variant to
    /// fall back to, so the answer is no CV.
    /// </remarks>
    [Fact]
    public async Task A_tie_break_answering_off_the_ballot_sends_nothing()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools(new StubCvChooser(9_999)).GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.TiedCv));

        var selection = pack.GetProperty("cvSelection");

        Assert.Equal(JsonValueKind.Null, selection.GetProperty("cvVariantId").ValueKind);
        Assert.Equal(JsonValueKind.Null, pack.GetProperty("curriculumVitaeMarkdown").ValueKind);
    }

    /// <summary>
    /// A posting whose application already carries events is not parked by a pack read.
    /// </summary>
    /// <remarks>
    /// The same refusal <c>park_application</c> makes, and for the same reason: parking sets
    /// columns on whatever submission exists for the pair, so a park landing on a sent application
    /// would make it read as a posting nobody attempted and would hand the vacancy back to the
    /// queue for a second application to the same job. Applying twice is worse than not applying,
    /// and the recruiter sees both. It matters more here, because this park happens on a read that
    /// a client may repeat at any time.
    /// </remarks>
    [Fact]
    public async Task A_pack_read_does_not_park_a_posting_that_has_already_been_applied_to()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().CreateSubmissionAsync(
            McpToolHarness.AsCandidate(),
            McpToolHarness.NoCvFit,
            sent: true,
            idempotencyKey: "run-1:12:Submitted");

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.NoCvFit));

        Assert.False(pack.GetProperty("cvSelection").GetProperty("parked").GetBoolean());
        Assert.Contains("already carries events", pack.GetProperty("note").GetString());

        await using var db = harness.Database();

        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(row => row.PostingId == McpToolHarness.NoCvFit);

        Assert.Null(submission.ParkedReason);
    }

    /// <summary>
    /// The chosen variant is recorded on the submission, which is what C4 correlates against.
    /// </summary>
    /// <remarks>
    /// <b>The id and the rule version together.</b> The id says which document went; the version
    /// says which floor and margin chose it, so a constant moved halfway through a window does not
    /// silently average two experiments into one. Neither is readable off any other column: until
    /// this existed, every application was made with the same document written twice.
    /// </remarks>
    [Fact]
    public async Task The_variant_that_was_sent_is_recorded_on_the_submission()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments));

        var chosen = pack.GetProperty("cvSelection").GetProperty("cvVariantId").GetInt64();

        await harness.Tools().CreateSubmissionAsync(
            McpToolHarness.AsCandidate(),
            McpToolHarness.WithDocuments,
            sent: true,
            idempotencyKey: "run-1:10:Submitted",
            cvVariantId: chosen);

        await using var db = harness.Database();

        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(row => row.PostingId == McpToolHarness.WithDocuments);

        Assert.Equal(chosen, submission.CvVariantId);
        Assert.Equal(Core.Applications.CvSelection.CurrentVersion, submission.CvSelectionVersion);
    }

    /// <summary>
    /// A variant id that is not this candidate's is refused before anything is written.
    /// </summary>
    /// <remarks>
    /// These ids are named by a model reading a pack, so the two things that go wrong are a
    /// transposition and an invention - and both would put a row on the table claiming a document
    /// went to an employer that never did. A refusal that leaves no submission behind is the right
    /// answer: a wrong entry in the only column that says which CV earns replies is worse than no
    /// entry, because nothing downstream can tell it is wrong.
    /// </remarks>
    [Fact]
    public async Task A_variant_belonging_to_nobody_is_refused_and_records_no_submission()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var result = await harness.Tools().CreateSubmissionAsync(
            McpToolHarness.AsCandidate(),
            McpToolHarness.WithDocuments,
            cvVariantId: 4_242);

        var (refused, reason) = McpToolHarness.Refusal(result);

        Assert.True(refused);
        Assert.Contains("not one of this candidate's CVs", reason);

        await using var db = harness.Database();

        Assert.False(await db.Submissions.AnyAsync());
    }

    /// <summary>
    /// The gap brief ranks what to write next, over the postings actually blocked.
    /// </summary>
    /// <remarks>
    /// <b>The abstention's other half, and the reason abstaining beats a near miss.</b> Two
    /// postings are parked for want of a CV and they share Terraform; the brief names one gap
    /// blocking both rather than two gaps of one, because a candidate reading two rows would write
    /// two documents where one would do.
    ///
    /// <b>AWS is deliberately not in the cluster.</b> It is missing from one of the two postings
    /// the seed was built from, which is not a majority - so a CV written without it still unblocks
    /// most of what it was written for, which is exactly the test the cluster rule makes.
    /// </remarks>
    [Fact]
    public async Task The_gap_brief_ranks_one_cv_over_the_postings_that_share_its_gap()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.NoCvFit);

        await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.NoCvFitEither);

        var brief = McpToolHarness.Read(
            await harness.Tools().ListCvGapsAsync(McpToolHarness.AsCandidate()));

        Assert.Equal(
            ["blockedPostings", "gaps", "maxGaps", "minimumPostingsPerGap", "nameablePostings", "note"],
            McpToolHarness.Keys(brief));

        Assert.Equal(2, brief.GetProperty("blockedPostings").GetInt32());

        var gap = Assert.Single(brief.GetProperty("gaps").EnumerateArray().ToList());

        Assert.Equal(2, gap.GetProperty("postings").GetInt32());

        var concepts = gap.GetProperty("concepts").EnumerateArray()
            .Select(concept => concept.GetProperty("key").GetString())
            .ToList();

        Assert.Equal(["skill.terraform"], concepts);

        // The label, not the key, because it is what goes in the sentence somebody reads.
        Assert.Equal(
            "Terraform",
            gap.GetProperty("concepts")[0].GetProperty("label").GetString());

        Assert.Contains("waiting on a CV", brief.GetProperty("note").GetString());
    }

    /// <summary>
    /// With nothing blocked, the brief says so rather than inventing a work item.
    /// </summary>
    /// <remarks>
    /// The state a healthy library is in, and worth pinning: a report that always names something
    /// to write is one people stop reading, and the floor of two postings per gap is what keeps a
    /// single recruiter's vocabulary out of somebody's Saturday.
    /// </remarks>
    [Fact]
    public async Task An_unblocked_queue_produces_a_brief_with_nothing_in_it()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var brief = McpToolHarness.Read(
            await harness.Tools().ListCvGapsAsync(McpToolHarness.AsCandidate()));

        Assert.Equal(0, brief.GetProperty("blockedPostings").GetInt32());
        Assert.Empty(brief.GetProperty("gaps").EnumerateArray());
        Assert.Contains("No applyable posting is waiting", brief.GetProperty("note").GetString());
    }

    /// <summary>
    /// Reading the pack records the variant that left, and never the document.
    /// </summary>
    /// <remarks>
    /// The pack now hands over a CV in the candidate's own words rather than a model's, which does
    /// not make it less of a disclosure - so it is logged on the same terms as before, and the
    /// record names the identity of what left rather than any of it.
    /// </remarks>
    [Fact]
    public async Task The_pack_records_the_variant_it_disclosed_and_none_of_its_words()
    {
        using var harness = await McpToolHarness.CreateAsync();

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.WithDocuments));

        var record = Assert.Single(harness.Disclosures.Records);

        Assert.Contains($"cv variant {harness.BackendVariant}", record.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(
            pack.GetProperty("curriculumVitaeMarkdown").GetString()!,
            record.Detail,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The gap brief is a read and changes nothing, whatever it is asked twice.
    /// </summary>
    /// <remarks>
    /// Written because the tool sits beside one that parks: it would be an easy and quiet mistake
    /// for a report about parked postings to start unparking, re-parking or re-timestamping them,
    /// and <c>ParkedAtUtc</c> walking forward on every read is how "blocked since Tuesday" becomes
    /// "blocked a minute ago" - the fact somebody reading the queue is actually after.
    /// </remarks>
    [Fact]
    public async Task Reading_the_gap_brief_twice_changes_nothing_about_the_parks()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.NoCvFit);

        await harness.Tools().ListCvGapsAsync(McpToolHarness.AsCandidate());
        await harness.Tools().ListCvGapsAsync(McpToolHarness.AsCandidate());

        await using var db = harness.Database();

        var parked = await db.Submissions
            .AsNoTracking()
            .SingleAsync(row => row.PostingId == McpToolHarness.NoCvFit);

        Assert.Equal(McpToolHarness.Now, parked.ParkedAtUtc);

        // And it discloses nothing. The pack above logged, because it hands over a CV and the
        // allowlist entries; the brief carries a count of public postings and a list of concepts
        // this candidate's CVs do not mention, which is not something a form is filled in with.
        Assert.DoesNotContain(harness.Disclosures.Records, record => record.Tool == "list_cv_gaps");
    }

    /// <summary>
    /// A posting the queue would never offer is not parked, so the brief cannot go blind to one.
    /// </summary>
    /// <remarks>
    /// <b>The contradiction this closes was found by a model driving the surface.</b> The pack
    /// assembles for any matched posting - a person opening one for a posting the nightly
    /// assessment has not reached is ordinary - and it used to park whatever it found no CV for.
    /// The queue and the gap brief both count only what was judged at least <c>Possible</c> and
    /// not dismissed, so such a park stood in <c>list_submissions</c> as a blocked posting while
    /// <c>list_cv_gaps</c> answered that nothing was blocked at all: two reports of the same fact
    /// disagreeing, in the pair a run is meant to summarise itself from.
    ///
    /// <b>Both halves are asserted, because either alone would pass on the broken build.</b> That
    /// nothing was parked is the fix; that the brief still reads zero is what says the two now
    /// agree rather than that the brief was taught to see a park nothing else counts.
    /// </remarks>
    [Fact]
    public async Task An_unjudged_posting_is_not_parked_for_want_of_a_cv()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await using (var db = harness.Database())
        {
            var match = await db.JobMatches.SingleAsync(row => row.PostingId == McpToolHarness.NoCvFit);

            match.Verdict = null;
            match.AssessmentScore = null;
            match.AssessedAtUtc = null;

            await db.SaveChangesAsync();
        }

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.NoCvFit));

        var selection = pack.GetProperty("cvSelection");

        // The selection is unchanged - the arithmetic does not know what the assessment thinks -
        // and only the write is refused.
        Assert.Equal("NoFit", selection.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, selection.GetProperty("cvVariantId").ValueKind);
        Assert.False(selection.GetProperty("parked").GetBoolean());

        Assert.Contains(
            "not one the queue offers",
            pack.GetProperty("note").GetString(),
            StringComparison.Ordinal);

        var brief = McpToolHarness.Read(
            await harness.Tools().ListCvGapsAsync(McpToolHarness.AsCandidate()));

        Assert.Equal(0, brief.GetProperty("blockedPostings").GetInt32());

        await using var after = harness.Database();

        Assert.False(await after.Submissions.AnyAsync());
    }

    /// <summary>
    /// And a posting the candidate has dismissed is not parked either.
    /// </summary>
    /// <remarks>
    /// The same disagreement from the other side, and the more expensive one to leave: a dismissal
    /// is the candidate saying they will not apply, so a park on it is a block against an
    /// application that was never going to be made - and the brief excludes it deliberately, for
    /// the reason <c>ListCvBlockedPostingsAsync</c> gives about arguing a Saturday's work from
    /// vacancies somebody has already refused.
    /// </remarks>
    [Fact]
    public async Task A_dismissed_posting_is_not_parked_for_want_of_a_cv()
    {
        using var harness = await McpToolHarness.CreateAsync();

        await using (var db = harness.Database())
        {
            var match = await db.JobMatches.SingleAsync(row => row.PostingId == McpToolHarness.NoCvFit);

            match.DismissedAtUtc = McpToolHarness.Now;

            await db.SaveChangesAsync();
        }

        var pack = McpToolHarness.Read(await harness.Tools().GetSubmissionPackAsync(
            McpToolHarness.AsCandidate(), McpToolHarness.NoCvFit));

        Assert.False(pack.GetProperty("cvSelection").GetProperty("parked").GetBoolean());

        await using var after = harness.Database();

        Assert.False(await after.Submissions.AnyAsync());
    }
}
