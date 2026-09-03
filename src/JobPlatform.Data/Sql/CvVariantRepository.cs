using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// One candidate's whole CV library, with the one fact from outside it that staleness needs.
/// </summary>
/// <remarks>
/// <b>The profile's timestamp travels with the variants because staleness is arithmetic over the
/// two, and it is never stored.</b> A column saying "this CV is out of date" would need a timer to
/// write it, and the row would be wrong between the timer and the edit - the argument
/// <c>SubmissionState</c> already makes about <c>Stale</c>, and a stronger one here because the
/// thing going out of date is a document somebody may be about to send. So it is computed on read,
/// and this record is what makes computing it a property access rather than a second query.
///
/// <b>One read answers every question the page asks.</b> The variants list, how many predate the
/// last profile change, which of them a selection pass may choose, and whether there is room for
/// another are four questions with one answer set, and each is delegated to
/// <see cref="CvVariantLibrary"/> rather than restated here. That is the arrangement
/// <c>ParkReasonPolicy</c> exists for on the other side of the same feature: one definition,
/// several readers, nothing to drift - and a rule spelled a second time in this project would
/// drift silently, because a variant missing from a list is not something anybody notices.
///
/// <b><see cref="ProfileUpdatedUtc"/> is nullable although the column is not.</b> Null here means
/// no profile row answered - which <see cref="CvVariant.PredatesProfileUpdate"/> reads as "nothing
/// is stale", the correct reading: a profile nothing has written is not a profile changed at the
/// beginning of time, and the opposite reading would open the dashboard with every CV flagged.
/// </remarks>
/// <param name="Variants">
/// Everything the candidate holds, archived rows included, live ones first and then in authoring
/// order. Archived variants are here deliberately - <see cref="CvVariantLibrary.IsLabelAvailable"/>
/// and the staleness count both need to see the whole library to answer about part of it.
/// </param>
/// <param name="ProfileUpdatedUtc">When the profile last moved, or null where no profile answered.</param>
public sealed record CvLibraryView(IReadOnlyList<CvVariant> Variants, DateTimeOffset? ProfileUpdatedUtc)
{
    /// <summary>The variants a selection pass may choose between, as Core defines that.</summary>
    /// <remarks>
    /// <c>CvVariant.IsSendable</c> is a computed property and has no SQL, so this is a filter over
    /// what was materialised rather than a <c>where</c> clause - and it is Core's filter rather
    /// than a second spelling of it. Both exclusions are Core's too: archived, and not currently
    /// rendered. Staleness is not one of them, which is the whole difference between a nudge and a
    /// lockout.
    /// </remarks>
    public IReadOnlyList<CvVariant> Selectable => CvVariantLibrary.Selectable(Variants);

    /// <summary>How many of these were written before the profile last changed.</summary>
    public CvVariantStaleness Staleness => CvVariantLibrary.Staleness(Variants, ProfileUpdatedUtc);

    /// <summary>Whether another variant may be written, counting only the ones in use.</summary>
    public bool HasRoomForAnother => CvVariantLibrary.HasRoomForAnother(Variants);
}

/// <summary>What happened to a write against the CV library.</summary>
/// <remarks>
/// <b>Four ordinary outcomes rather than one success and three exceptions</b>, the shape
/// <c>SubmissionEventResult</c> already takes on the other side of the apply loop. Every one of
/// these is a thing a person did rather than a fault: they have six CVs already, they picked a name
/// another CV is using, or they are looking at a variant somebody else owns. A caller acts
/// differently on each and none of them is worth a stack trace.
///
/// <b>Numbered from one</b>, like every other classification here: a zero member acquires
/// <c>default</c>, and a defaulted result would read as a write that succeeded.
/// </remarks>
public enum CvVariantWriteOutcome
{
    /// <summary>The row is as the caller asked for it. Also the answer to a repeat of a write that already stands.</summary>
    Written = 1,

    /// <summary>No such variant for this candidate - which a caller cannot tell from "not yours".</summary>
    NotFound = 2,

    /// <summary>
    /// The library is at <see cref="CvVariantLimits.MaxPerProfile"/> and nothing was written.
    /// </summary>
    /// <remarks>
    /// The answer to a library <i>over</i> the cap as well as one exactly at it. That state is
    /// reachable without anybody doing anything wrong - lowering the constant leaves every library
    /// above it above it - and the response is "no room" rather than an exception on a page
    /// somebody is only trying to save on.
    /// </remarks>
    LibraryFull = 3,

    /// <summary>Another variant in use already answers to that name, and nothing was written.</summary>
    LabelTaken = 4,
}

/// <summary>
/// What a write did, and the variant as it now stands.
/// </summary>
/// <remarks>
/// <b><see cref="Variant"/> is null on every outcome but <see cref="CvVariantWriteOutcome.Written"/>,
/// and null means nothing changed rather than "there is something you may not see."</b> The same
/// care <c>SubmissionWriteResult.Row</c> takes: a refusal leaves the row exactly as the caller last
/// read it, so there is nothing to hand back that the caller does not already have, and returning a
/// half-applied copy would invite somebody to render the page from it.
/// </remarks>
public sealed record CvVariantWriteResult(CvVariantWriteOutcome Outcome, CvVariant? Variant)
{
    /// <summary>Whether the row now says what the caller asked for.</summary>
    public bool Written => Outcome is CvVariantWriteOutcome.Written;

    /// <summary>No such variant for this candidate.</summary>
    public static CvVariantWriteResult NotFound { get; } = new(CvVariantWriteOutcome.NotFound, null);

    /// <summary>The cap is spent. Archiving one is how room is made; deleting is not available.</summary>
    public static CvVariantWriteResult LibraryFull { get; } = new(CvVariantWriteOutcome.LibraryFull, null);

    /// <summary>A live variant already answers to that name.</summary>
    public static CvVariantWriteResult LabelTaken { get; } = new(CvVariantWriteOutcome.LabelTaken, null);

    internal static CvVariantWriteResult Of(CvVariant variant) => new(CvVariantWriteOutcome.Written, variant);
}

/// <summary>
/// Where a variant's rendered files ended up, and what they hash to.
/// </summary>
/// <remarks>
/// <b>Required init properties rather than positional parameters, because two of the four are
/// strings that must not be swapped.</b> A path and a digest transpose without a compiler error,
/// and the result is a row that looks rendered, hands the pack a URL that resolves to nothing, and
/// carries a hash describing a file at a path nobody stored - the shape of bug
/// <c>ResolutionOutcome</c> is a record to avoid, with more at stake here because the artefact is
/// what an employer is sent.
///
/// <b>The PDF is required and the DOCX is not.</b> <see cref="CvVariant.IsRendered"/> reads the PDF
/// alone: a variant with only a PDF can still be uploaded to most forms, and one with no PDF cannot
/// be uploaded anywhere. Recording a render that produced no PDF would leave a row claiming to be
/// rendered with nothing to send.
/// </remarks>
public sealed record RenderedVariant
{
    /// <summary>Where the PDF was uploaded, as a path inside its container.</summary>
    /// <remarks>
    /// A path and never a signed URL: a user-delegation SAS expires, and an expired URL stored
    /// beside a document is a dead pointer that still looks live. <c>ApplicationPackFile.VariantBlobPath</c>
    /// is what builds it, and it has nowhere to put a variant's label - so two applications made
    /// with two different CVs upload files whose names are identical.
    /// </remarks>
    public required string PdfBlobPath { get; init; }

    /// <summary>The DOCX, which several large ATS vendors parse more reliably than a PDF.</summary>
    public string? DocxBlobPath { get; init; }

    /// <summary>Over the rendered bytes, so "what did we send them" is answerable exactly.</summary>
    public required string Sha256 { get; init; }

    /// <summary>When the files were produced.</summary>
    /// <remarks>
    /// Stamped by whoever rendered rather than read from a clock in here, because it is compared
    /// against <see cref="CvVariant.AuthoredAtUtc"/> and the comparison is what decides whether the
    /// stored file still describes the current text.
    /// </remarks>
    public required DateTimeOffset RenderedAtUtc { get; init; }
}

/// <summary>
/// The CV library: the variants a candidate wrote, and what each of them says.
/// </summary>
/// <remarks>
/// <b>This class exists because a model wrote a sentence no candidate would.</b> Asked what else an
/// employer should know, the writer gave the candidate's citizenship - correctly, out of their own
/// summary - and added "I am an AI and they should have seen this", which was stored, served
/// through the pack, and would have been typed into a real employer's form under a person's name.
/// A guard now drops that class of sentence and a guard is a net under a trapeze. This is the store
/// behind the other half: the prose in <c>CvVariants.Markdown</c> is the candidate's, and
/// <b>nothing in this file writes to that column except from words a person handed it</b>.
///
/// <b>Takes a profile id the caller has already resolved</b>, which is the authorisation boundary
/// expressed as a type - the rule <c>CandidateProfileRepository</c> sets by taking a subject id and
/// never a profile id, and <c>SubmissionRepository</c> and <c>FormAnswerRepository</c> follow.
/// There is no method here an endpoint could hand a route parameter to and none an MCP tool could
/// hand an argument named by a model, which matters more here than on a route: a tool signature is
/// exactly where a model would helpfully fill in a <c>profileId</c> it invented. <b>Every read is
/// scoped to one candidate and there is no method that returns variants for more than one.</b>
///
/// <b>A variant's concepts feed selection only, and this file is where that guard has to hold.</b>
/// <see cref="ReplaceConceptsAsync"/> writes <c>CvVariantConcepts</c> and nothing else; no method
/// here reads or writes <c>ProfileConcepts</c>, and none returns anything a profile writer would
/// accept. A CV is written <i>from</i> the profile, so letting its concepts back in would let a
/// document inflate the record it was derived from, after which the loop applies to jobs on the
/// strength of its own prose. The tables are separate precisely so that "which rows are the
/// candidate's qualifications" is answered by which table you are reading rather than by a filter
/// somebody has to remember - and the same is true of which repository you are calling.
///
/// <b>Nothing here deletes a variant.</b> Archiving is an update: it removes a CV from selection
/// and from nothing else, because <c>Submissions.CvVariantId</c> names the row and an application
/// made last year has to stay explicable. The database refuses a delete under a submission anyway;
/// this class simply never asks. The one thing it does remove is a variant's own extracted concept
/// rows, which are derived, re-derivable from markdown that has not changed, and not a record of
/// anything anybody said or sent - see <see cref="ReplaceConceptsAsync"/>.
///
/// <b>Staleness is never a column.</b> It is computed on read from the profile's <c>UpdatedUtc</c>,
/// the way submission staleness is derived from an event log, and for the same reason: a stored
/// flag needs a timer to write it and is wrong between the timer and the edit. See
/// <see cref="CvLibraryView"/>.
///
/// <b>This reads and writes Azure SQL</b>, which the architecture otherwise reserves for posting
/// browse, search and detail. Bounded the way the profile's reads are: when a page opens, when a
/// person saves, when a pack is built. It must never join a client's bootstrap sequence and must
/// never become a polling path.
/// </remarks>
public sealed class CvVariantRepository(JobsDbContext db)
{
    /// <summary>
    /// The candidate's whole library, with what staleness is measured against.
    /// </summary>
    /// <remarks>
    /// <b>Archived variants are included and the caller narrows.</b> They are what makes a past
    /// application explicable, they are what frees a label for the CV that replaces one, and
    /// leaving them out would make <see cref="CvVariantLibrary.IsLabelAvailable"/> answer a
    /// different question from the one the filtered unique index enforces. The narrowing that
    /// matters - which variants may actually be sent - is <see cref="CvLibraryView.Selectable"/>,
    /// Core's own rule over what came back.
    ///
    /// <b>The markdown comes with them, and that is affordable here and nowhere near the selection
    /// path.</b> This is a page a person opened: the live rows are bounded by
    /// <see cref="CvVariantLimits.MaxPerProfile"/> and the archived ones accumulate at the rate
    /// somebody rewrites a CV. The per-posting read is <see cref="ListSelectableFactsAsync"/>,
    /// which carries concept keys and never a document, because that one runs once per posting in
    /// an unattended pass against a database billed by the second.
    ///
    /// <b>One column of the profile is read and never the row.</b> This file has no business
    /// holding somebody's employment history, and the only thing staleness needs is when the
    /// profile last moved.
    ///
    /// Live variants first and then in authoring order, which is a presentation the page can rely
    /// on and never a ranking - ranking is the selector's job. Deterministic beyond that, because a
    /// list that shuffles between identical requests is a bug nobody can reproduce.
    /// </remarks>
    /// <param name="profileId">The candidate, resolved by the caller.</param>
    public async Task<CvLibraryView> ListAsync(long profileId, CancellationToken ct = default)
    {
        var rows = await db.CvVariants
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .OrderBy(v => v.IsArchived)
            .ThenBy(v => v.Id)
            .ToListAsync(ct);

        var profileUpdatedUtc = await db.CandidateProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => (DateTimeOffset?)p.UpdatedUtc)
            .FirstOrDefaultAsync(ct);

        return new CvLibraryView([.. rows.Select(Hydrate)], profileUpdatedUtc);
    }

    /// <summary>
    /// The variants a selection pass may choose between.
    /// </summary>
    /// <remarks>
    /// <b>Materialised and then filtered by <see cref="CvVariantLibrary.Selectable"/>, rather than
    /// filtered in SQL by a predicate written out a second time.</b> <c>CvVariant.IsSendable</c> is
    /// a computed property over three columns and EF cannot translate it: a <c>where</c> clause
    /// spelling the same rule in a language the database speaks would be a second definition of
    /// what may be sent to an employer, and the two would drift the first time one of them learned
    /// about a fourth column. The set is one candidate's CVs, so materialising it costs a page of
    /// rows and the arithmetic is Core's.
    ///
    /// <b>Where the cost of the markdown does matter, the query is a different one.</b>
    /// <see cref="ListSelectableFactsAsync"/> is the per-posting read and it uses
    /// <see cref="CvVariantEntity.Sendable"/> - the entity's single spelling of the same rule, over
    /// columns - because a projection has no <see cref="CvVariant"/> to hand Core. The two are held
    /// together by a test that runs every stored variant past both, which is how this codebase
    /// holds the shortlist's channel filter to its projection.
    /// </remarks>
    public async Task<IReadOnlyList<CvVariant>> ListSelectableAsync(
        long profileId, CancellationToken ct = default)
        => (await ListAsync(profileId, ct)).Selectable;

    /// <summary>
    /// What the selector is allowed to know about each sendable variant: an id, a label, and keys.
    /// </summary>
    /// <remarks>
    /// <b>The narrowing is the guarantee rather than an optimisation.</b> <see cref="CvVariantFacts"/>
    /// carries no markdown, no blob path and no hash, so a selector that cannot reach the document
    /// cannot be tempted to read it - and it carries concept <i>keys</i> without their polarity, so
    /// a document that describes itself emphatically cannot outscore one that mentions the same
    /// work plainly. Both of those are the spec's selection-only guard read from the query side:
    /// presence or absence is all a selection is entitled to.
    ///
    /// <b>Keys are deduplicated here because <c>Source</c> is part of the key of the row.</b> A
    /// concept the candidate named in a heading and again in a bullet is two rows, deliberately -
    /// they are not equally good evidence and a collapse cannot be undone - and a selector counting
    /// presence must not see one concept twice.
    ///
    /// <b>Filtered in SQL, because this is the read that runs once per posting.</b> It is the one
    /// place in this file that uses <see cref="CvVariantEntity.Sendable"/> instead of Core's
    /// <see cref="CvVariantLibrary.Selectable"/>, and the reason is mechanical: there is no
    /// <see cref="CvVariant"/> in this projection to hand Core, and building one would mean
    /// dragging every candidate's documents across for a pass that never opens them. The two rules
    /// differ on exactly one input - a blob path made only of whitespace, which
    /// <c>ApplicationPackFile.VariantBlobPath</c> cannot produce - and a test pins that they agree
    /// on everything else.
    /// </remarks>
    public async Task<IReadOnlyList<CvVariantFacts>> ListSelectableFactsAsync(
        long profileId, CancellationToken ct = default)
    {
        var rows = await db.CvVariants
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .Where(CvVariantEntity.Sendable)
            .OrderBy(v => v.Id)
            .Select(v => new
            {
                v.Id,
                v.Label,
                Keys = v.Concepts.Select(c => c.Concept!.ConceptKey).ToList(),
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(row => new CvVariantFacts
            {
                VariantId = row.Id,
                Label = row.Label,
                ConceptKeys = [.. row.Keys.Distinct(StringComparer.Ordinal)],
            }),
        ];
    }

    /// <summary>
    /// One variant of this candidate's, archived or not.
    /// </summary>
    /// <remarks>
    /// <b>This read deliberately ignores <c>IsArchived</c>, and it is separate from the selection
    /// reads for exactly that reason.</b> A submission records <c>CvVariantId</c>, and "what did we
    /// send them" is a question about a document that was actually uploaded into somebody else's
    /// system - so it has to answer after the variant has been retired, which is the ordinary end
    /// of a CV's life. A system that answered "that CV is archived" would have lost the record it
    /// exists to keep. Archiving governs selection and nothing else, and the way to keep that true
    /// is to have one read that cannot be given a flag to filter on: <see cref="ListSelectableAsync"/>
    /// is where archiving is applied, here it never is.
    ///
    /// Null means no such variant for this candidate, which a caller cannot tell from "not yours" -
    /// the rule every other read in this codebase follows. The profile id is in the predicate rather
    /// than checked afterwards, so a stranger's CV is never materialised at all.
    /// </remarks>
    public async Task<CvVariant?> GetAsync(
        long profileId, long variantId, CancellationToken ct = default)
    {
        var entity = await db.CvVariants
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProfileId == profileId, ct);

        return entity is null ? null : Hydrate(entity);
    }

    /// <summary>
    /// Whether a label may be used, given what this candidate already keeps.
    /// </summary>
    /// <remarks>
    /// <b>Advisory, and the filtered unique index is the enforcement.</b> This is what the
    /// dashboard asks while somebody is typing a name; it reads the library and then answers, which
    /// is two statements with a gap in the middle, and two tabs saving "Backend .NET" a second apart
    /// both find it free. <c>IX_CvVariants_LiveLabel</c> is what refuses the second one, and the
    /// write paths here check first only so that the ordinary case is an answer rather than an
    /// exception.
    ///
    /// <b>It answers false for a name nobody could pick a CV by as well as for one in use</b>,
    /// because <see cref="CvVariantLibrary.IsLabelAvailable"/> does - the two are told apart by
    /// <see cref="CvVariantLibrary.IsUsableLabel"/>, which needs no query at all.
    ///
    /// <paramref name="excludingId"/> is what lets a variant keep its own name through a rename.
    /// Without it, saving "Backend .NET" over "Backend .NET" collides with itself, which reads to
    /// the person as the system refusing a change they did not make.
    /// </remarks>
    public async Task<bool> IsLabelAvailableAsync(
        long profileId, string? label, long? excludingId = null, CancellationToken ct = default)
        => CvVariantLibrary.IsLabelAvailable(await LiveAsync(profileId, ct), label, excludingId);

    /// <summary>
    /// Writes a new variant, refusing it where the library is full or the name is in use.
    /// </summary>
    /// <remarks>
    /// <b>The cap is enforced here and nowhere else</b>, which is the rule
    /// <c>SubmissionRepository</c> follows for the daily cap on <c>Submitted</c> events and for the
    /// same reason: one call site reaches this today and a second will - the dashboard, and
    /// whatever eventually imports a library - and a guard written at the call sites survives
    /// exactly until the second one is written. The counting is
    /// <see cref="CvVariantLibrary.HasRoomForAnother"/>, so what "full" means is stated once: it
    /// counts variants in use and never archived ones, because those are kept forever to explain
    /// past applications and a cap counting them would make the seventh rewrite of a CV impossible
    /// until somebody erased a file an employer was actually sent.
    ///
    /// <b>A library already over the cap answers "no room" rather than throwing.</b> Lowering the
    /// constant puts every library above it above it without anybody doing anything wrong, and an
    /// exception on that path is a page that will not draw.
    ///
    /// <b>The record is built through <see cref="CvVariant.Create"/> rather than accepted from the
    /// caller</b>, so a blank CV and a twenty-thousand-character paste are refused by the one
    /// constructor that validates instead of by whichever call site remembered. It throws for
    /// those, where a full library and a taken label are answers: the difference is between a
    /// caller that skipped a step and a person who has to be told something they can act on.
    ///
    /// <b>One read serves both rules.</b> The live variants are what the label rule needs and their
    /// count is what the cap needs, and a second query for the count is a number free to disagree
    /// with the list it was taken from.
    /// </remarks>
    /// <param name="profileId">The candidate, resolved by the caller.</param>
    /// <param name="label">What the person calls it. Refused if it is not something a CV could be picked by.</param>
    /// <param name="markdown">Their CV. Refused blank, refused past the bound, never truncated.</param>
    /// <param name="authoredAtUtc">When they wrote it. What staleness and the render comparison read.</param>
    public async Task<CvVariantWriteResult> CreateAsync(
        long profileId,
        string label,
        string markdown,
        DateTimeOffset authoredAtUtc,
        CancellationToken ct = default)
    {
        var variant = CvVariant.Create(label, markdown, authoredAtUtc);

        var live = await LiveAsync(profileId, ct);

        if (!CvVariantLibrary.HasRoomForAnother(live))
        {
            return CvVariantWriteResult.LibraryFull;
        }

        if (!CvVariantLibrary.IsLabelAvailable(live, variant.Label))
        {
            return CvVariantWriteResult.LabelTaken;
        }

        var entity = new CvVariantEntity
        {
            ProfileId = profileId,
            Label = variant.Label,

            // CvVariantLibrary.FoldLabel and nothing else, here and in RenameAsync, which is the
            // single-writer rule JobFingerprint.CrossBoardKeyHash already lives under: a second
            // spelling of the fold splits one label into two with nothing failing and no count to
            // compare against. CvVariant.LabelKey derives it from the same function.
            LabelKey = variant.LabelKey,
            Markdown = variant.Markdown,
            AuthoredAtUtc = variant.AuthoredAtUtc,
        };

        db.CvVariants.Add(entity);
        await db.SaveChangesAsync(ct);

        return CvVariantWriteResult.Of(Hydrate(entity));
    }

    /// <summary>
    /// Changes what a variant is called, and nothing else about it.
    /// </summary>
    /// <remarks>
    /// <b>It must not disturb <see cref="CvVariant.AuthoredAtUtc"/>.</b> That timestamp is the whole
    /// of the staleness signal and half of the render comparison, so a rename that moved it would
    /// make a document nobody has edited look freshly written - and would quietly mark a stale PDF
    /// current. Renaming and re-authoring are separate methods for that reason rather than for
    /// tidiness.
    ///
    /// <b>The label and its folded key move together</b>, because the key is what the unique index
    /// is built on: a rename that wrote one and not the other would leave a variant findable under
    /// a name it no longer displays, and free a name it is in fact still using.
    /// </remarks>
    public async Task<CvVariantWriteResult> RenameAsync(
        long profileId, long variantId, string label, CancellationToken ct = default)
    {
        // Core's rule rather than a copy of it: blank, over-long, and a label with no letter or
        // digit anywhere in it - "-" or "..." - are all names nobody could pick a CV by, which is
        // the one thing a label has to be able to do. It throws where CreateAsync throws, so the
        // two paths refuse the same input the same way.
        if (!CvVariantLibrary.IsUsableLabel(label))
        {
            throw new ArgumentException(
                "A label has to be something a person can pick a CV by, and at most "
                + $"{CvVariantLimits.MaxLabelLength} characters.",
                nameof(label));
        }

        // AsTracking, explicitly, because this row is about to be mutated. It reads as redundant
        // against EF's default and is not: the API host once set NoTracking globally on the
        // argument that it never wrote to SQL, and under that a read-then-mutate saves nothing and
        // throws nothing. Stating it here means this write does not depend on a line in a
        // composition root a long way from it.
        var entity = await db.CvVariants
            .AsTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProfileId == profileId, ct);

        if (entity is null)
        {
            return CvVariantWriteResult.NotFound;
        }

        if (!CvVariantLibrary.IsLabelAvailable(await LiveAsync(profileId, ct), label, variantId))
        {
            return CvVariantWriteResult.LabelTaken;
        }

        entity.Label = label.Trim();
        entity.LabelKey = CvVariantLibrary.FoldLabel(label);

        await db.SaveChangesAsync(ct);

        return CvVariantWriteResult.Of(Hydrate(entity));
    }

    /// <summary>
    /// Replaces the words of a variant, and dates them.
    /// </summary>
    /// <remarks>
    /// <b>Through <see cref="CvVariant.WithMarkdown"/>, which moves the text and its date
    /// together.</b> Setting the two columns separately is the mistake that method exists to make
    /// impossible: the pairing is what makes <see cref="CvVariant.IsRenderCurrent"/> answer
    /// honestly, and a writer free to move one without the other is a writer that can leave last
    /// week's PDF looking current in front of an employer. It also validates, so the bound on a CV
    /// is the same one <see cref="CreateAsync"/> enforces.
    ///
    /// <b>The blob paths and the hash survive the edit rather than being cleared.</b> The stored
    /// file is still the file a previous application uploaded, and blanking the pointer would make
    /// that application unexplainable in order to keep a row tidy. The variant leaves selection
    /// through the timestamps instead - <c>AuthoredAtUtc</c> moves past <c>RenderedAtUtc</c>,
    /// <see cref="CvVariant.IsRenderCurrent"/> goes false, and it comes back the moment the
    /// renderer stamps a new time. Nothing had to remember to clear a flag.
    ///
    /// <b>An unchanged document is still an edit.</b> Somebody who saves without changing a word
    /// has asserted that the CV is current as of now, which is exactly what the staleness nudge
    /// asked them to do; converging on "no change" would leave the nudge standing after the person
    /// answered it.
    /// </remarks>
    public async Task<CvVariantWriteResult> ReauthorAsync(
        long profileId,
        long variantId,
        string markdown,
        DateTimeOffset authoredAtUtc,
        CancellationToken ct = default)
    {
        // AsTracking, explicitly, because this row is about to be mutated - see RenameAsync.
        var entity = await db.CvVariants
            .AsTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProfileId == profileId, ct);

        if (entity is null)
        {
            return CvVariantWriteResult.NotFound;
        }

        var edited = Hydrate(entity).WithMarkdown(markdown, authoredAtUtc);

        entity.Markdown = edited.Markdown;
        entity.AuthoredAtUtc = edited.AuthoredAtUtc;

        await db.SaveChangesAsync(ct);

        return CvVariantWriteResult.Of(edited);
    }

    /// <summary>
    /// Retires a variant from selection, and from nothing else.
    /// </summary>
    /// <remarks>
    /// <b>An update and never a delete.</b> The markdown, the paths and the hash stay exactly where
    /// they were, because a submission names this row by id and an application made last year has
    /// to stay explicable - the database refuses the delete as well, and this class simply never
    /// asks for one. What archiving does is take the variant out of
    /// <see cref="ListSelectableAsync"/>, out of the cap's count, and out of the filtered unique
    /// index, so the CV that replaces it may take its name.
    ///
    /// <b>Idempotent, because the answer is the state and not the transition.</b> Archiving an
    /// archived variant writes nothing and answers with the same row: there is no "already
    /// archived" refusal, because there is nothing a caller would do differently on hearing it.
    /// </remarks>
    public Task<CvVariantWriteResult> ArchiveAsync(
        long profileId, long variantId, CancellationToken ct = default)
        => SetArchivedAsync(profileId, variantId, archived: true, ct);

    /// <summary>
    /// Puts a retired variant back into use, if there is room for it and its name is still free.
    /// </summary>
    /// <remarks>
    /// <b>Both rules apply again, and forgetting either is how this becomes an exception instead of
    /// an answer.</b> An unarchived variant occupies the cap, so a library already at six has no
    /// room for a seventh however it arrives; and the label it was archived under is very often the
    /// name of the CV that replaced it, in which case the filtered unique index refuses the write.
    /// Checking here turns both into outcomes a person can act on - archive one, or rename this
    /// one - rather than a <c>DbUpdateException</c> on a page.
    ///
    /// <b>Its dates are untouched.</b> A CV brought back is as old as it was: it is unarchived, not
    /// re-authored, and the staleness nudge should say so on the same afternoon.
    /// </remarks>
    public async Task<CvVariantWriteResult> UnarchiveAsync(
        long profileId, long variantId, CancellationToken ct = default)
    {
        // AsTracking, explicitly, because this row is about to be mutated - see RenameAsync.
        var entity = await db.CvVariants
            .AsTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProfileId == profileId, ct);

        if (entity is null)
        {
            return CvVariantWriteResult.NotFound;
        }

        if (!entity.IsArchived)
        {
            return CvVariantWriteResult.Of(Hydrate(entity));
        }

        // The row being restored is archived, so it is not in this list and needs no exclusion:
        // the questions are whether the live library has room for one more and whether anything
        // live has taken its name in the meantime.
        var live = await LiveAsync(profileId, ct);

        if (!CvVariantLibrary.HasRoomForAnother(live))
        {
            return CvVariantWriteResult.LibraryFull;
        }

        if (!CvVariantLibrary.IsLabelAvailable(live, entity.Label))
        {
            return CvVariantWriteResult.LabelTaken;
        }

        entity.IsArchived = false;

        await db.SaveChangesAsync(ct);

        return CvVariantWriteResult.Of(Hydrate(entity));
    }

    /// <summary>
    /// Records that a variant was rendered: the time, both paths and the hash, in one update.
    /// </summary>
    /// <remarks>
    /// <b>One write, after the upload has already succeeded.</b> The repository holds no bytes and
    /// never talks to Azure - it is handed paths to files that exist. Split into two saves, or
    /// called before the upload, the row spends a window claiming to be rendered while pointing at
    /// nothing: <see cref="CvVariant.IsRenderCurrent"/> goes true on the timestamp, selection picks
    /// the variant up, and the pack hands the browser loop a URL whose file the loop discovers is
    /// missing at the upload box - after the tab is open, which is the late discovery
    /// <c>SubmissionQuota</c> exists to prevent on the other side of the same loop. Everything that
    /// makes a variant sendable therefore becomes true in the same <c>SaveChanges</c>.
    ///
    /// <b>The hash and the paths are refused rather than trimmed.</b> That inverts what
    /// <c>SubmissionRepository.Bound</c> does to free text, and the inversion is the point: a
    /// shortened sentence is a worse audit line, where a shortened pointer costs the thing pointed
    /// at - a rendered document that exists, was uploaded to an employer, and can never be found
    /// again, with nothing in the row admitting it. <see cref="CvVariantLimits.MaxBlobPathLength"/>
    /// is the storage platform's own ceiling, so a longer path names no file that exists. The hash
    /// is checked for shape because the column is <c>nchar(64)</c> and pads: a digest of the wrong
    /// length would be stored looking like a digest and would never match the file it claims to
    /// describe.
    ///
    /// <b>A render is one set of files, so a missing DOCX clears the column rather than leaving the
    /// previous one standing.</b> The three values describe one pass of the renderer over one
    /// version of the markdown: a path kept from an earlier render would point at a file the stored
    /// hash no longer describes, which is the one question - "what exactly did we send them" -
    /// these columns exist to answer.
    ///
    /// <b>A render stamped before the text it came from is stored as given.</b> Nothing here
    /// second-guesses the clocks; Core reads such a row as not current, which costs one
    /// deterministic re-render, where the other reading sends an employer a PDF of a paragraph the
    /// candidate may have deleted.
    ///
    /// <b>Archived variants may be re-rendered.</b> Archiving governs selection and nothing else,
    /// and a template fix that reaches every stored document should not stop at the retired ones -
    /// they are what a past application is explained by.
    /// </remarks>
    public async Task<CvVariantWriteResult> RecordRenderAsync(
        long profileId, long variantId, RenderedVariant rendered, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendered);

        // Blank is refused rather than stored, because the row would then say it was rendered
        // while pointing at nothing - the one state this method exists to make unreachable.
        ArgumentException.ThrowIfNullOrWhiteSpace(rendered.PdfBlobPath);

        var pdf = BlobPath(rendered.PdfBlobPath, nameof(RenderedVariant.PdfBlobPath));
        var docx = BlobPath(rendered.DocxBlobPath, nameof(RenderedVariant.DocxBlobPath));
        var sha256 = Hash(rendered.Sha256);

        // AsTracking, explicitly, because this row is about to be mutated - see RenameAsync. This
        // is the write that failed silently in the class of bug that rule was written after: a
        // render recorded against nothing leaves a variant permanently unsendable and a container
        // full of files nothing points at.
        var entity = await db.CvVariants
            .AsTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProfileId == profileId, ct);

        if (entity is null)
        {
            return CvVariantWriteResult.NotFound;
        }

        entity.RenderedAtUtc = rendered.RenderedAtUtc;
        entity.PdfBlobPath = pdf;
        entity.DocxBlobPath = docx;
        entity.Sha256 = sha256;

        await db.SaveChangesAsync(ct);

        return CvVariantWriteResult.Of(Hydrate(entity));
    }

    /// <summary>
    /// What was extracted from one variant's markdown, as assertions.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately the same <see cref="ConceptAssertion"/> the profile and posting sides
    /// produce</b>, because the three tables mirror each other column for column and selection is a
    /// join between shapes rather than a translation layer. What the type does <i>not</i> imply is
    /// that these rows may travel: they describe a document written from the profile, and copying
    /// one into <c>ProfileConcepts</c> would let a CV inflate the record it was derived from. This
    /// read exists for the dashboard - "what does this CV say about you" - and for re-derivation.
    /// The selector is handed <see cref="ListSelectableFactsAsync"/> instead, which carries keys
    /// and no polarity at all.
    ///
    /// Scoped through the variant's owner rather than by variant id alone, so an id from a route
    /// cannot read a stranger's CV.
    /// </remarks>
    public async Task<IReadOnlyList<ConceptAssertion>> GetConceptsAsync(
        long profileId, long variantId, CancellationToken ct = default)
        => await db.CvVariantConcepts
            .AsNoTracking()
            .Where(c => c.VariantId == variantId && c.Variant!.ProfileId == profileId)
            .OrderBy(c => c.ConceptId)
            .ThenBy(c => c.Source)
            .Select(c => new ConceptAssertion(
                c.Concept!.ConceptKey,
                c.Source,
                c.Polarity,
                c.YearsMin,
                c.YearsMax,
                c.EvidenceText,
                c.Confidence))
            .ToListAsync(ct);

    /// <summary>
    /// Replaces what one variant is recorded as saying, and touches no other variant's rows.
    /// </summary>
    /// <remarks>
    /// <b>This is the only write in this file that removes anything, and the thing it removes is
    /// derived.</b> Nothing a person wrote and nothing an employer was sent is ever deleted here;
    /// these rows are an extractor's reading of markdown that is still on the row beside them, so
    /// re-deriving them costs a pass over a document that has not changed. That is the bargain
    /// <c>PostingExtractions.PayloadJson</c> strikes on the posting side.
    ///
    /// <b>Every row of this variant is replaced, not only the model's.</b> The profile side narrows
    /// its delete to <see cref="AssertionSource.Model"/> so that skills the candidate declared on a
    /// form survive a re-extraction; a variant has no form to declare on and no board to tag it, so
    /// there is no second evidence source to protect. If one ever arrives, this delete has to be
    /// narrowed in the same change - a replace that quietly erased a declared row would be
    /// invisible, because a concept missing from a set is not something anybody notices.
    ///
    /// <b>The delete and the insert are one <c>SaveChanges</c>, which is one transaction.</b>
    /// <c>ExecuteDeleteAsync</c> would be the shorter spelling and it runs outside the save, so a
    /// failure between the two leaves a variant with no concepts - a document that still looks
    /// sendable, scores nothing against every posting, and is silently never chosen again. The
    /// ordinary repair for that, a hand-written transaction, is not available either: this context
    /// is configured with <c>EnableRetryOnFailure</c>, whose execution strategy refuses
    /// user-initiated transactions. One <c>SaveChanges</c> needs neither.
    ///
    /// <b>A key the vocabulary does not know is dropped rather than invented.</b> The same rule
    /// <c>KernelDocumentExtractor</c> applies before a key is ever stored: a hallucinated concept
    /// key is indistinguishable from a real one in SQL and would quietly split a concept in two.
    /// The count returned is what was written, so a caller comparing it against what it handed in
    /// can see that the vocabulary missed something.
    ///
    /// <b>Duplicates are folded on the way in, because <c>Source</c> is part of the primary
    /// key.</b> Two assertions of one concept from one source are one row - an extractor emitting
    /// the same key twice would otherwise fail the whole write on a constraint rather than record
    /// what it read.
    /// </remarks>
    /// <param name="profileId">The candidate, resolved by the caller.</param>
    /// <param name="variantId">Whose concepts these are.</param>
    /// <param name="concepts">What was read out of the markdown. An empty list clears the variant's rows.</param>
    /// <param name="resolverVersion">
    /// Which resolver produced them, so a vocabulary improvement can be re-applied to rows below it.
    /// </param>
    /// <returns>How many rows were written, which is at most one per concept and source.</returns>
    public async Task<int> ReplaceConceptsAsync(
        long profileId,
        long variantId,
        IReadOnlyList<ConceptAssertion> concepts,
        int resolverVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(concepts);

        // The owner is established before anything is removed. Without this, an id from a route
        // would delete a stranger's extraction and write nothing in its place - the one shape of
        // mistake this file could make that loses data.
        var owned = await db.CvVariants
            .AsNoTracking()
            .AnyAsync(v => v.Id == variantId && v.ProfileId == profileId, ct);

        if (!owned)
        {
            return 0;
        }

        // AsTracking, explicitly, because these rows are about to be removed in the same save as
        // the ones replacing them - see RenameAsync for what a silent NoTracking would do to it.
        var existing = await db.CvVariantConcepts
            .AsTracking()
            .Where(c => c.VariantId == variantId)
            .ToListAsync(ct);

        db.CvVariantConcepts.RemoveRange(existing);

        var keys = concepts
            .Where(assertion => assertion is not null)
            .Select(assertion => assertion.ConceptKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var conceptIds = await db.Concepts
            .AsNoTracking()
            .Where(c => keys.Contains(c.ConceptKey))
            .Select(c => new { c.ConceptKey, c.Id })
            .ToDictionaryAsync(c => c.ConceptKey, c => c.Id, StringComparer.Ordinal, ct);

        var written = new HashSet<(int ConceptId, AssertionSource Source)>();

        foreach (var assertion in concepts)
        {
            if (assertion is null || !conceptIds.TryGetValue(assertion.ConceptKey, out var conceptId))
            {
                continue;
            }

            if (!written.Add((conceptId, assertion.Source)))
            {
                continue;
            }

            db.CvVariantConcepts.Add(new CvVariantConceptEntity
            {
                VariantId = variantId,
                ConceptId = conceptId,
                Source = assertion.Source,

                // A CV is a supply document, as a profile is, and the extractor speaks the demand
                // half because it is the same prompt that reads adverts. The mapping is
                // CandidateProfileRepository's, spelled a second time because it is private there -
                // worth noticing rather than hiding: a third writer of a supply polarity is the
                // point at which it belongs in Core. Nothing selects on this column, so a drift
                // would read oddly in the data rather than move a decision.
                Polarity = ToSupply(assertion.Polarity),
                YearsMin = assertion.YearsMin,
                YearsMax = assertion.YearsMax,

                // Trimmed to the column, unlike the paths above. This one only explains a
                // selection - "your CV says k8s, the advert says Kubernetes" - so a shortened
                // phrase is a worse explanation where a shortened path is a lost file.
                EvidenceText = Bound(assertion.EvidenceText, EvidenceTextLength),
                Confidence = assertion.Confidence,
                ResolverVersion = resolverVersion,
            });
        }

        await db.SaveChangesAsync(ct);

        return written.Count;
    }

    /// <summary>
    /// The width <c>CvVariantConcepts.EvidenceText</c> is configured at, and the two other sides with it.
    /// </summary>
    /// <remarks>
    /// Not promoted to <see cref="CvVariantLimits"/>, because the rule is the mirror rather than a
    /// bound somebody chose for CVs: <c>ProfileConcepts</c> and <c>PostingConcepts</c> carry the
    /// same 120, and a constant here would invite one of the three to move on its own.
    /// </remarks>
    private const int EvidenceTextLength = 120;

    /// <summary>
    /// This candidate's variants that are in use, which is the set both library rules read.
    /// </summary>
    /// <remarks>
    /// Bounded by the cap it is used to enforce, so the markdown that comes with these rows is a
    /// handful of documents rather than a history. Archived rows are excluded because neither rule
    /// counts them - <see cref="CvVariantLibrary.ActiveCount"/> and
    /// <see cref="CvVariantLibrary.IsLabelAvailable"/> both skip them - and because they are the
    /// part of a library that grows without limit.
    /// </remarks>
    private async Task<IReadOnlyList<CvVariant>> LiveAsync(long profileId, CancellationToken ct)
    {
        var rows = await db.CvVariants
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId && !v.IsArchived)
            .OrderBy(v => v.Id)
            .ToListAsync(ct);

        return [.. rows.Select(Hydrate)];
    }

    /// <summary>
    /// Sets or clears the archived flag, writing nothing where the row already says so.
    /// </summary>
    /// <remarks>
    /// Only the archiving direction goes through here. Unarchiving has two rules to apply first and
    /// is its own method rather than a boolean parameter, because a caller passing <c>false</c>
    /// would otherwise skip both of them.
    /// </remarks>
    private async Task<CvVariantWriteResult> SetArchivedAsync(
        long profileId, long variantId, bool archived, CancellationToken ct)
    {
        // AsTracking, explicitly, because this row is about to be mutated - see RenameAsync.
        var entity = await db.CvVariants
            .AsTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProfileId == profileId, ct);

        if (entity is null)
        {
            return CvVariantWriteResult.NotFound;
        }

        if (entity.IsArchived != archived)
        {
            entity.IsArchived = archived;

            await db.SaveChangesAsync(ct);
        }

        return CvVariantWriteResult.Of(Hydrate(entity));
    }

    /// <summary>
    /// A stored row as the record Core reasons about.
    /// </summary>
    /// <remarks>
    /// The object initialiser rather than <see cref="CvVariant.Create"/>, which is the split
    /// <c>FormAnswer</c> already draws: history that no longer satisfies a bound is still history,
    /// and a row written by an older build must not become unreadable because a number in
    /// <see cref="CvVariantLimits"/> moved. Validation belongs on the way in, and it is there.
    ///
    /// No profile id on the record, though the table carries one. It would be a second copy of a
    /// fact the query has already established, and the failure it invites is a caller reading the
    /// owner off the row it is deciding whether to disclose.
    /// </remarks>
    private static CvVariant Hydrate(CvVariantEntity entity)
        => new()
        {
            Id = entity.Id,
            Label = entity.Label,
            Markdown = entity.Markdown,
            AuthoredAtUtc = entity.AuthoredAtUtc,
            RenderedAtUtc = entity.RenderedAtUtc,
            PdfBlobPath = entity.PdfBlobPath,
            DocxBlobPath = entity.DocxBlobPath,
            Sha256 = entity.Sha256,
            IsArchived = entity.IsArchived,
        };

    /// <summary>
    /// One stored blob path, trimmed, or null where there is nothing to record.
    /// </summary>
    /// <remarks>
    /// Refused rather than shortened, for the reason
    /// <c>ApplicationDocumentRepository.RecordRenderedAsync</c> refuses one: a truncated pointer
    /// costs the file it points at, while the row goes on carrying something that still looks like a
    /// reference. The bound is <see cref="CvVariantLimits.MaxBlobPathLength"/>, which is the storage
    /// platform's own ceiling - so a longer path is one the store would have refused anyway, and can
    /// only have come from a bug here.
    /// </remarks>
    private static string? BlobPath(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= CvVariantLimits.MaxBlobPathLength
            ? trimmed
            : throw new ArgumentException(
                $"A blob path is at most {CvVariantLimits.MaxBlobPathLength} characters - the "
                + "storage account's own ceiling - so this one names no file that exists.",
                name);
    }

    /// <summary>
    /// One SHA-256, lower-cased, checked for shape because the column is fixed-length.
    /// </summary>
    /// <remarks>
    /// <c>nchar(64)</c> pads a short value with spaces, so a digest of any other length would be
    /// stored looking like a digest and would never match the file it claims to describe - the
    /// failure the column exists to catch, arriving through the column itself. Lower-cased on the
    /// way in because every hash this system writes comes from <c>Convert.ToHexStringLower</c>, and
    /// two spellings of one value compare unequal.
    /// </remarks>
    private static string Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim().ToLowerInvariant();

        return trimmed.Length == CvVariantLimits.Sha256Length && trimmed.All(char.IsAsciiHexDigit)
            ? trimmed
            : throw new ArgumentException(
                $"A variant hash is {CvVariantLimits.Sha256Length} hex characters of SHA-256. A "
                + "value of any other shape is padded into the column and never matches the file "
                + "it claims to describe.",
                nameof(value));
    }

    /// <summary>
    /// The supply polarity for what an extractor read as a demand polarity.
    /// </summary>
    /// <remarks>
    /// The two halves of <see cref="AssertionPolarity"/> exist so this conversion has to be written
    /// down rather than happening by accident. "Required" in a document somebody wrote about
    /// themselves means the skill is central to their work, which is Expert; "mentioned" is a
    /// passing reference, which is Familiar. A value already on the supply half passes through
    /// rather than being defaulted, so anything that skipped this mapping stays visible in the data.
    /// </remarks>
    private static AssertionPolarity ToSupply(AssertionPolarity demand) => demand switch
    {
        AssertionPolarity.Required => AssertionPolarity.Expert,
        AssertionPolarity.Preferred => AssertionPolarity.Proficient,
        AssertionPolarity.Mentioned => AssertionPolarity.Familiar,
        _ => demand,
    };

    /// <summary>Trims explanatory text to its column, the way <c>SubmissionRepository</c> does.</summary>
    private static string? Bound(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
