using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql;

namespace JobPlatform.Api.Features.CvVariants;

/// <summary>
/// One CV in the library, as a row on the page the candidate keeps it on.
/// </summary>
/// <remarks>
/// <b>No markdown here, and the omission follows the rule list responses already live under.</b>
/// <c>PostingSummary</c> carries no description because that column is unbounded
/// <c>nvarchar(max)</c> and only the detail route returns it; a variant's markdown is the same
/// column and the same argument, with one extra reason. The live variants are capped at
/// <see cref="CvVariantLimits.MaxPerProfile"/>, but the archived ones are kept forever - archiving
/// is how a CV is retired here, so the list grows every time somebody rewrites one - and a library
/// with thirty retired CVs would answer this route with half a megabyte of documents nobody asked
/// to read. <see cref="CvVariantDetail"/> is what the editor opens.
///
/// <b>No blob path and no URL either.</b> A stored path is a pointer inside a container this
/// tenant owns, and a link to a rendered file is a bearer credential in a query string with its own
/// lifetime rules - see <c>ApplicationPackOptions.LinkLifetime</c>. Neither belongs in a list
/// response that a dashboard will hold on screen for as long as somebody leaves the tab open.
/// <see cref="Sha256"/> is here because it is short and it is the answer to "what exactly did we
/// send them", which is a question about a file this row is the record of.
///
/// <b>Every derived flag is Core's own reading rather than arithmetic done here.</b>
/// <see cref="IsSendable"/>, <see cref="IsRenderCurrent"/> and <see cref="IsStale"/> are computed by
/// <see cref="CvVariant"/> and by <c>CvVariantLibrary</c>, so the badge beside a row and the
/// sentence at the top of the page are the same function called twice rather than two spellings of
/// one rule - a page that said "three of your CVs are out of date" beside four flagged rows would
/// be worse than one that said nothing.
/// </remarks>
public record CvVariantSummary
{
    /// <summary>The row, which is what a submission records and what a rename cannot change.</summary>
    public required long VariantId { get; init; }

    /// <summary>What the person calls it. Unique among the CVs in use, reusable once one is archived.</summary>
    public required string Label { get; init; }

    /// <summary>When the words last became what they are now. Untouched by a rename or an archive.</summary>
    public required DateTimeOffset AuthoredAtUtc { get; init; }

    /// <summary>When the stored files were produced, or null while this is still only text.</summary>
    public DateTimeOffset? RenderedAtUtc { get; init; }

    /// <summary>Over the rendered PDF's bytes. Null until there is one.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Retired from selection, and from nothing else. There is no delete.</summary>
    public required bool IsArchived { get; init; }

    /// <summary>
    /// Whether the stored files are the current words rendered.
    /// </summary>
    /// <remarks>
    /// False is the dashboard's "this needs rendering again", and it goes false by arithmetic
    /// rather than by a flag somebody has to clear: an edit moves <see cref="AuthoredAtUtc"/> past
    /// <see cref="RenderedAtUtc"/> and the variant leaves selection until a render stamps a newer
    /// time. Carried separately from <see cref="IsSendable"/> because the two are false for
    /// different reasons and only one of them is a thing to tell somebody about.
    /// </remarks>
    public required bool IsRenderCurrent { get; init; }

    /// <summary>
    /// Whether a selection pass may choose this for an application being made now.
    /// </summary>
    /// <remarks>
    /// Archived or not currently rendered, and deliberately <i>not</i> staleness: a CV written
    /// before this morning's profile edit is still a CV worth sending, and excluding it would mean
    /// somebody who adds a job to their profile at lunchtime finds every posting parked for want of
    /// a CV while holding six good ones.
    /// </remarks>
    public required bool IsSendable { get; init; }

    /// <summary>
    /// Whether this row is one the staleness sentence is counting. The nudge, per row.
    /// </summary>
    /// <remarks>
    /// <b>This is the loop body of <c>CvVariantLibrary.Staleness</c> and not merely the same idea
    /// spelled again</b>, which is what makes the badges and the sentence add up: that count skips
    /// archived variants and then asks <c>CvVariant.PredatesProfileUpdate</c>, so a field here that
    /// asked only the second half would flag retired CVs the sentence had not counted - a page
    /// saying "one of your CVs predates your last profile change" over two flagged rows, which is
    /// worse than a page that says nothing. An archived CV falling behind the profile is not a thing
    /// to nudge about: it is not going to be sent, and rewriting it is not work worth asking for.
    ///
    /// <b>It reports and nothing acts on it.</b> There is no route in this feature that responds to
    /// a true here by rewriting the document, and there must not be one - regenerating the
    /// candidate's CV with a model is precisely what this design removes, and doing it on a timer
    /// would put the model back into the one document it was taken out of, unattended.
    /// </remarks>
    public required bool IsStale { get; init; }
}

/// <summary>
/// One CV with the candidate's own words, which is the only thing this system will not rewrite.
/// </summary>
/// <remarks>
/// <b>The markdown is returned as stored rather than as submitted</b>, so what the editor shows
/// after a save is what a render would lay out - <c>CvVariant.Create</c> trims the ends, and a
/// client redisplaying its own request body would show a document that differs from the one on
/// disk by whitespace nobody can see.
///
/// Extends the summary rather than repeating it, so a field can never be present on the list and
/// missing from the detail - the shape of contract drift that costs a dashboard a badge with
/// nothing failing.
/// </remarks>
public sealed record CvVariantDetail : CvVariantSummary
{
    /// <summary>The candidate's own words. The source both rendered formats are made from.</summary>
    public required string Markdown { get; init; }
}

/// <summary>
/// How much of the library has fallen behind the profile it was written from.
/// </summary>
/// <remarks>
/// <b>Counts and no verb.</b> The sentence - "three of your CVs predate your last profile change" -
/// belongs to whatever renders it, and there is deliberately no <c>suggestedAction</c> here: a
/// summary carrying an instruction is how "regenerate them for me" arrives six months later as a
/// helpful automation.
///
/// <see cref="ProfileUpdatedUtc"/> is carried so that "nothing is out of date" can be told apart
/// from "the profile has never been updated". Both are zero, for opposite reasons, and a page that
/// cannot separate them says nothing on the day the feature ships and says nothing on the day it
/// matters.
/// </remarks>
public sealed record CvLibraryStaleness
{
    /// <summary>How many were counted. Archived variants never reach it.</summary>
    public required int Considered { get; init; }

    /// <summary>How many predate the profile's last change.</summary>
    public required int Stale { get; init; }

    /// <summary>How many are still level with it.</summary>
    public required int Current { get; init; }

    /// <summary>Whether there is anything to say at all.</summary>
    public required bool AnyStale { get; init; }

    /// <summary>What they were compared against, or null where the profile has never recorded one.</summary>
    public DateTimeOffset? ProfileUpdatedUtc { get; init; }
}

/// <summary>
/// How much room is left in the library, so a refusal is not the first time anybody hears the cap.
/// </summary>
/// <remarks>
/// <b>The numbers, not a permission.</b> The repository enforces the cap and is the only thing
/// that does - <c>CvVariantRepository.CreateAsync</c> refuses, this route reports - so a client
/// reading <see cref="HasRoomForAnother"/> as authority is reading a value that was true when the
/// page loaded. What it is for is a "new CV" button that explains itself before it is pressed.
///
/// <see cref="InUse"/> counts what is in use and never what is archived, because the cap does not
/// count archived rows either: they are kept forever so that past applications stay explicable, and
/// a cap counting them would make the seventh rewrite of a CV impossible until somebody erased a
/// file an employer was actually sent.
/// </remarks>
public sealed record CvLibraryCapacity
{
    /// <summary>How many variants are in use.</summary>
    public required int InUse { get; init; }

    /// <summary>The cap, from <see cref="CvVariantLimits.MaxPerProfile"/>.</summary>
    public required int Cap { get; init; }

    /// <summary>Whether another may be written, as the library stood when this was read.</summary>
    public required bool HasRoomForAnother { get; init; }
}

/// <summary>The whole library page in one answer: the rows, the nudge and the room.</summary>
/// <remarks>
/// <b>One response rather than three routes, because they are one read.</b>
/// <c>CvVariantRepository.ListAsync</c> answers all of it from a single materialisation plus one
/// column of the profile, and splitting it would put the dashboard in the position of asking a
/// database that pauses when idle three times to draw one page. It would also let the three answers
/// come from three moments, which is exactly how a summary sentence ends up disagreeing with the
/// rows underneath it.
/// </remarks>
public sealed record CvLibraryResponse
{
    /// <summary>Live variants first, then archived, each in authoring order. Never a ranking.</summary>
    public required IReadOnlyList<CvVariantSummary> Items { get; init; }

    /// <summary>How many have fallen behind the profile.</summary>
    public required CvLibraryStaleness Staleness { get; init; }

    /// <summary>How much room is left.</summary>
    public required CvLibraryCapacity Capacity { get; init; }
}

/// <summary>
/// A new CV, in the candidate's own words.
/// </summary>
/// <remarks>
/// <b>No length attributes, deliberately, and this is the one place that decision is visible.</b>
/// The bounds live on <c>CvVariantLimits</c> and are enforced by <c>CvVariant.Create</c>, which is
/// the only constructor that validates; a <c>[MaxLength]</c> here would be a second copy of a
/// number that has already drifted from a column width once in this codebase's history, and the
/// failure mode of the drift is a 500 on somebody's save with their typing lost. The route maps
/// what <c>Create</c> throws onto a 400 instead, so there is exactly one statement of what a CV may
/// be.
///
/// <b>There is no source field and no "generate" flag.</b> Every route in this feature writes
/// markdown that arrived in a request body and from nowhere else. A parameter naming where the
/// words came from would be the first half of a path that lets a model fill this box in, which is
/// the failure the whole change exists to remove.
/// </remarks>
/// <param name="Label">What the person calls it. Unique among the CVs they are using.</param>
/// <param name="Markdown">Their CV. Refused blank, refused past the bound, never truncated.</param>
public sealed record CreateCvVariantRequest(string Label, string Markdown);

/// <summary>
/// A new name for a CV, and nothing else about it.
/// </summary>
/// <remarks>
/// Separate from <see cref="ReauthorCvVariantRequest"/> because the two writes must not share a
/// path: a rename has to leave <c>CvVariant.AuthoredAtUtc</c> alone, or a document nobody has
/// edited looks freshly written and a stale PDF is quietly marked current. One request carrying
/// both an optional label and an optional markdown is how that pairing gets broken by a client
/// sending one field.
/// </remarks>
/// <param name="Label">The new label.</param>
public sealed record RenameCvVariantRequest(string Label);

/// <summary>
/// Replacement words for a CV, dated as of now.
/// </summary>
/// <remarks>
/// <b>Saving without changing a word is still an edit</b>, and that is the point rather than an
/// accident: somebody who reads a stale CV, decides it is still accurate and presses save has
/// answered the nudge, and a route that converged on "no change" would leave the notice standing
/// after the person had dealt with it.
/// </remarks>
/// <param name="Markdown">The CV as it now reads.</param>
public sealed record ReauthorCvVariantRequest(string Markdown);

/// <summary>
/// Whether a CV is retired from selection.
/// </summary>
/// <remarks>
/// <b>One request with a direction rather than two verbs, and no <c>DELETE</c> anywhere on this
/// resource.</b> Archiving is an update: the markdown, the paths and the hash stay exactly where
/// they were, because <c>Submissions.CvVariantId</c> names this row and an application made last
/// year has to stay explicable - a system that answers "what did you send them" with "that CV was
/// deleted" has lost the record it exists to keep. The reverse direction is the same edit, and
/// putting both on one route is what makes that legible: a client that can archive can always
/// unarchive, and neither is a destruction.
///
/// It follows <c>MapPut("/{postingId:long}/dismissed")</c> on the matches group, which is the same
/// shape for the same reason - a reversible flag on a row the caller already owns.
/// </remarks>
/// <param name="Archived">True to retire it from selection, false to put it back into use.</param>
public sealed record ArchiveCvVariantRequest(bool Archived);

/// <summary>
/// The stored shapes as the wire shapes, in one place so the two readings cannot diverge.
/// </summary>
/// <remarks>
/// <b>Every derived value is delegated rather than computed here.</b> The staleness count is
/// <c>CvVariantLibrary.Staleness</c>, the per-row badge is
/// <c>CvVariant.PredatesProfileUpdate</c> - which is the same call the count is made of - the
/// capacity is <c>CvVariantLibrary.ActiveCount</c> and <c>HasRoomForAnother</c>, and the sendable
/// flag is <c>CvVariant.IsSendable</c>. A mapping that counted its own stale rows would be a
/// second implementation of a rule whose failure nobody notices, because a badge missing from a row
/// looks exactly like a row that is fine.
/// </remarks>
internal static class CvVariantMapping
{
    /// <summary>The whole page, from the one read that answers all of it.</summary>
    public static CvLibraryResponse ToResponse(this CvLibraryView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new CvLibraryResponse
        {
            Items = [.. view.Variants.Select(variant => variant.ToSummary(view.ProfileUpdatedUtc))],
            Staleness = view.Staleness.ToResponse(),
            Capacity = new CvLibraryCapacity
            {
                InUse = CvVariantLibrary.ActiveCount(view.Variants),
                Cap = CvVariantLimits.MaxPerProfile,
                HasRoomForAnother = view.HasRoomForAnother,
            },
        };
    }

    /// <summary>What a library nobody has started looks like, which is not an error.</summary>
    /// <remarks>
    /// <c>CvVariantStaleness.None</c> rather than zeroes written out here: an empty library is not
    /// a thing the staleness notice reports on, and saying "none of your zero CVs are out of date"
    /// on the page of somebody who has not started is noise. The room is real - nobody has spent
    /// any of the cap - so the "write your first CV" affordance is enabled from the same field it
    /// is enabled from afterwards.
    /// </remarks>
    public static CvLibraryResponse EmptyLibrary()
        => new()
        {
            Items = [],
            Staleness = CvVariantStaleness.None.ToResponse(),
            Capacity = new CvLibraryCapacity
            {
                InUse = 0,
                Cap = CvVariantLimits.MaxPerProfile,
                HasRoomForAnother = CvVariantLibrary.HasRoomForAnother([]),
            },
        };

    public static CvLibraryStaleness ToResponse(this CvVariantStaleness staleness)
    {
        ArgumentNullException.ThrowIfNull(staleness);

        return new CvLibraryStaleness
        {
            Considered = staleness.Considered,
            Stale = staleness.Stale,
            Current = staleness.Current,
            AnyStale = staleness.AnyStale,
            ProfileUpdatedUtc = staleness.ProfileUpdatedUtc,
        };
    }

    /// <summary>
    /// Whether this row is one <c>CvVariantLibrary.Staleness</c> would have counted.
    /// </summary>
    /// <remarks>
    /// <b>The archived exclusion belongs here because it belongs to the count.</b> That function
    /// skips an archived variant before it asks anything else, so a badge asking only
    /// <c>CvVariant.PredatesProfileUpdate</c> would mark rows the sentence above them had not
    /// counted, and a reader would be left deciding which of the two to believe. The pair adds up
    /// exactly: the number of rows flagged here is <c>staleness.stale</c>, always, and that is an
    /// invariant a test can assert rather than a claim a comment makes.
    /// </remarks>
    private static bool IsStale(CvVariant variant, DateTimeOffset? profileUpdatedUtc)
        => !variant.IsArchived && variant.PredatesProfileUpdate(profileUpdatedUtc);

    public static CvVariantSummary ToSummary(this CvVariant variant, DateTimeOffset? profileUpdatedUtc)
    {
        ArgumentNullException.ThrowIfNull(variant);

        return new CvVariantSummary
        {
            VariantId = variant.Id,
            Label = variant.Label,
            AuthoredAtUtc = variant.AuthoredAtUtc,
            RenderedAtUtc = variant.RenderedAtUtc,
            Sha256 = variant.Sha256,
            IsArchived = variant.IsArchived,
            IsRenderCurrent = variant.IsRenderCurrent,
            IsSendable = variant.IsSendable,
            IsStale = IsStale(variant, profileUpdatedUtc),
        };
    }

    public static CvVariantDetail ToDetail(this CvVariant variant, DateTimeOffset? profileUpdatedUtc)
    {
        ArgumentNullException.ThrowIfNull(variant);

        return new CvVariantDetail
        {
            VariantId = variant.Id,
            Label = variant.Label,
            Markdown = variant.Markdown,
            AuthoredAtUtc = variant.AuthoredAtUtc,
            RenderedAtUtc = variant.RenderedAtUtc,
            Sha256 = variant.Sha256,
            IsArchived = variant.IsArchived,
            IsRenderCurrent = variant.IsRenderCurrent,
            IsSendable = variant.IsSendable,
            IsStale = IsStale(variant, profileUpdatedUtc),
        };
    }

    /// <summary>The brief, as the dashboard reads it.</summary>
    /// <remarks>
    /// The floor travels with the answer rather than being assumed by the client: without it, a
    /// reader seeing a gap that blocks one posting missing from the list has no way to tell the
    /// floor doing its job from a bug.
    /// </remarks>
    public static CvGapBriefResponse ToResponse(this CvGapBrief brief)
        => new(
            brief.BlockedPostings,
            brief.NameablePostings,
            [.. brief.Gaps.Select(gap => new CvGapResponse(
                [.. gap.Concepts.Select(c => new CvGapConceptResponse(c.Key, c.Label, c.Postings))],
                gap.Postings))],
            CvGapBrief.MinimumPostings);

    /// <summary>What a candidate with no profile is waiting on, which is nothing.</summary>
    public static CvGapBriefResponse EmptyGapBrief() => new(0, 0, [], CvGapBrief.MinimumPostings);

}

/// <summary>One concept a missing CV would have to speak to.</summary>
/// <param name="Key">The vocabulary's own spelling. Identity, not prose.</param>
/// <param name="Label">The preferred name. What a sentence says.</param>
/// <param name="Postings">How many of the gap's own postings ask for this one.</param>
public sealed record CvGapConceptResponse(string Key, string Label, int Postings);

/// <summary>One CV worth writing, and the applications it would release.</summary>
/// <param name="Concepts">What it has to cover, heaviest first, so reading them in order names it.</param>
/// <param name="Postings">
/// Applyable postings this gap blocks, after the gaps ranked above it have taken theirs. Greedy on
/// purpose: ranked independently, one set of nine postings wanting two things would report two gaps
/// of nine and read as eighteen postings of payoff.
/// </param>
public sealed record CvGapResponse(IReadOnlyList<CvGapConceptResponse> Concepts, int Postings);

/// <summary>
/// The brief for the CVs that do not exist yet, ranked by what each would release.
/// </summary>
/// <remarks>
/// <b>The point of this shape is that it is a work item and not an error list.</b> A refusal says
/// an application could not be made; this says which document would have made it, and how many
/// others it would carry with it - which is what turns "eleven postings could not be applied to"
/// into an afternoon with an obvious payoff.
/// </remarks>
/// <param name="BlockedPostings">Applyable postings currently parked for want of a CV, counted once each.</param>
/// <param name="NameablePostings">
/// How many of those named something a CV could cover.
/// </param>
/// <param name="Gaps">The CVs to write, best first.</param>
/// <param name="MinimumPostings">How many blocked postings a gap needs before it is listed.</param>
public sealed record CvGapBriefResponse(
    int BlockedPostings,
    int NameablePostings,
    IReadOnlyList<CvGapResponse> Gaps,
    int MinimumPostings);
