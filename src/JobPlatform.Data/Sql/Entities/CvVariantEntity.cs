using System.Linq.Expressions;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One CV the candidate wrote, kept as markdown and rendered to the two formats an ATS asks for.
/// </summary>
/// <remarks>
/// <b>The row exists because a model wrote a sentence no candidate would.</b> Asked what else an
/// employer should know, the writer gave the candidate's citizenship - correctly, out of their own
/// summary - and added "I am an AI and they should have seen this", which was stored, served
/// through the pack, and would have been typed into a real employer's form under a person's name.
/// A guard now drops that class of sentence and a guard is a net under a trapeze. This table is the
/// other half: the prose in <see cref="Markdown"/> is the candidate's, and <b>nothing in this
/// system writes to that column on their behalf</b>.
///
/// <b>Archived, never deleted, and the schema is what makes that stick.</b>
/// <see cref="IsArchived"/> removes a variant from selection and from nothing else - the paths, the
/// hash and the markdown stay exactly where they were, because <c>Submissions.CvVariantId</c> names
/// this row by id and an application made last year has to stay explicable. That is also why the
/// submission side of that foreign key is <c>Restrict</c>: a delete here would make a sent file
/// unexplainable, so the database refuses it rather than trusting every future caller to.
///
/// <b>The concepts extracted from this document live in <see cref="CvVariantConceptEntity"/> and
/// feed selection only.</b> They must never reach <c>ProfileConcepts</c>, never move a match score
/// and never widen what the candidate is judged to have: a CV is written <i>from</i> the profile,
/// and letting it back in would let a document inflate the record it came from, after which the
/// loop applies to jobs on the strength of its own prose. There is no navigation from here to
/// anything on the profile side, so the mistake has no path through this file.
///
/// <b>Rendered once and hashed, which is the opposite of what <c>ApplicationDocuments</c> does.</b>
/// A per-posting document is re-rendered per request so a template fix reaches documents already
/// generated; a variant's bytes were uploaded into somebody else's system, so "what exactly did we
/// send them" is a question about a file that has to still exist and still be the same file.
/// </remarks>
public sealed class CvVariantEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Whose library this is. Resolved by the caller before the repository is reached.
    /// </summary>
    /// <remarks>
    /// The column exists and <c>CvVariant</c> has no field for it, deliberately: every variant is
    /// materialised by a read already scoped to a candidate - the rule
    /// <c>CandidateProfileRepository</c> states as a type by taking a subject id and never a
    /// profile id - and a second copy of that fact on the record invites a caller to read the owner
    /// off the row it is deciding whether to disclose.
    /// </remarks>
    public long ProfileId { get; set; }

    /// <summary>What the person calls this CV - "Backend .NET". Their spelling, kept verbatim.</summary>
    /// <remarks>
    /// Stored as typed because it is what the pack quotes when it says which CV it chose and why,
    /// and an explanation that has re-cased somebody's own label reads as being about a different
    /// document. The comparison form is <see cref="LabelKey"/>; this one is never matched on.
    /// </remarks>
    public required string Label { get; set; }

    /// <summary>
    /// The folded form of <see cref="Label"/>, so the uniqueness rule can be an index.
    /// </summary>
    /// <remarks>
    /// <b>Persisted although <c>CvVariant.LabelKey</c> derives it, because a computed property has
    /// no SQL and an index needs a column.</b> Uniqueness has to be enforced where it cannot be
    /// argued with: a repository that reads the library first and then inserts is two statements
    /// with a gap in the middle, and two tabs saving "Backend .NET" a second apart both find it
    /// free.
    ///
    /// <b><c>CvVariantLibrary.FoldLabel</c> is the only thing that may write it</b>, which is the
    /// single-writer rule <c>JobFingerprint.CrossBoardKeyHash</c> already lives under: a second
    /// spelling of the fold - another normaliser, another notion of whitespace - splits one label
    /// into two with nothing failing and no count to compare against. The fold is also what makes
    /// the constraint mean the same thing on both engines this schema runs on: it lower-cases in
    /// Core, so the index never has to rely on a collation, and SQLite's <c>BINARY</c> and Azure
    /// SQL's <c>CI_AS</c> reach the same answer about "Backend" and "backend ".
    /// </remarks>
    public required string LabelKey { get; set; }

    /// <summary>The candidate's own words. The source both rendered formats are made from.</summary>
    public required string Markdown { get; set; }

    /// <summary>
    /// When <see cref="Markdown"/> last became what it is now.
    /// </summary>
    /// <remarks>
    /// It moves on an edit to the words and on nothing else - a rename or an archive must leave it
    /// alone, or a document that has not changed looks freshly written. Two things read it: the
    /// staleness nudge, which compares it against <c>CandidateProfiles.UpdatedUtc</c>, and the
    /// sendability predicate below, which compares it against <see cref="RenderedAtUtc"/>.
    /// </remarks>
    public DateTimeOffset AuthoredAtUtc { get; set; }

    /// <summary>When the stored files were produced, or null while the variant is still only text.</summary>
    public DateTimeOffset? RenderedAtUtc { get; set; }

    /// <summary>The PDF, as a path inside its container. Null until it is rendered.</summary>
    /// <remarks>
    /// A path and never a signed URL, for the reason <c>SubmissionEvents.ScreenshotRef</c> gives: a
    /// user-delegation SAS expires, and an expired URL stored beside a document is a dead pointer
    /// that still looks live. <c>ApplicationPackFile.VariantBlobPath</c> is what writes it, and it
    /// has nowhere to put a variant's label - so two applications made with two different CVs
    /// upload files whose names are identical.
    /// </remarks>
    public string? PdfBlobPath { get; set; }

    /// <summary>The DOCX, which several large ATS vendors parse more reliably than a PDF.</summary>
    public string? DocxBlobPath { get; set; }

    /// <summary>Over the rendered bytes, so "what did we send them" is answerable exactly.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Retired from selection, and from nothing else.</summary>
    /// <remarks>
    /// Two indexes read it and they read it for different reasons: the filtered unique index frees
    /// an archived variant's label for the CV that replaces it, and the selection index skips
    /// archived rows without touching them. Neither is a permission check - archiving must never
    /// make a file that was actually sent unfetchable.
    /// </remarks>
    public bool IsArchived { get; set; }

    public CandidateProfileEntity? Profile { get; set; }

    /// <summary>What this document says, for selection and for nothing else.</summary>
    public ICollection<CvVariantConceptEntity> Concepts { get; } = [];

    /// <summary>
    /// The variants a selection pass may choose between, as a predicate the database runs.
    /// </summary>
    /// <remarks>
    /// <b>An expression rather than a property, because a computed property has no SQL.</b>
    /// <c>CvVariant.IsSendable</c> is the rule in Core and it cannot be translated: EF would refuse
    /// the query, or - worse, where a caller has materialised first - the filter would run in
    /// memory over every variant the candidate has ever written. So the rule is spelled once here,
    /// over columns, and every reader composes this rather than writing its own <c>where</c>. That
    /// is the arrangement <c>ParkReasonPolicy.Permanent</c> exists for on the other side of the
    /// same queue: one definition, several readers, nothing to drift. The shortlist's channel
    /// filter is what it looks like when there is no such escape - two spellings held together by a
    /// test that has already caught them diverging once.
    ///
    /// <b>Exactly the two exclusions Core makes, and no third.</b> Archived, because archiving is
    /// what removes a variant from selection; not currently rendered, because a variant with no
    /// current PDF has no URL for the pack to hand over and choosing it produces a pack whose file
    /// the browser loop discovers is missing at the upload box - after the tab is open, which is
    /// the late discovery <c>SubmissionQuota</c> exists to prevent on the other side of the same
    /// loop. <b>Staleness is deliberately not here</b>: somebody who adds a job to their profile at
    /// lunchtime would otherwise find every CV excluded and every posting parked, and be told they
    /// have no CVs when they have six good ones and a line to add to them.
    ///
    /// <b>The render is current by arithmetic rather than by a flag</b>, and an edit invalidates it
    /// by moving <see cref="AuthoredAtUtc"/> past <see cref="RenderedAtUtc"/>. A boolean would have
    /// to be cleared by every writer that touches the markdown, and the one that forgets is the one
    /// that uploads last week's paragraph. A file stamped <i>before</i> the text it came from reads
    /// as not current, which is the same reading Core takes and for the same reason: treating it as
    /// current sends an employer a PDF of a paragraph the candidate may have deleted, and treating
    /// it as stale costs one deterministic re-render.
    ///
    /// <b>An empty path is compared rather than <c>string.IsNullOrWhiteSpace</c>, which Core
    /// uses.</b> The two disagree on exactly one input - a path made only of whitespace - which
    /// <c>ApplicationPackFile.VariantBlobPath</c> cannot produce, and the comparison is what both
    /// providers translate. Worth knowing that even here the engines differ: SQL Server pads on
    /// comparison, so a whitespace path would be read as empty there and as present on SQLite. The
    /// case is unreachable, and where it is not the divergence lands on the safe side - SQL Server
    /// agrees with Core.
    /// </remarks>
    public static Expression<Func<CvVariantEntity, bool>> Sendable { get; } =
        variant => !variant.IsArchived
            && variant.RenderedAtUtc != null
            && variant.PdfBlobPath != null
            && variant.PdfBlobPath != ""
            && variant.RenderedAtUtc >= variant.AuthoredAtUtc;
}

/// <summary>
/// One concept extracted from one CV variant. The selection side of
/// <see cref="ProfileConceptEntity"/>.
/// </summary>
/// <remarks>
/// <b>Deliberately the same columns as the profile and posting sides, <see cref="Source"/>
/// included.</b> That mirroring is the whole payoff of a shape fixed before there was a profile to
/// put in it: selection is a join between tables of identical shape rather than a translation layer
/// between two vocabularies, and the coverage question a parked posting waits on -
/// <see cref="SubmissionParkGapEntity"/> - is the same join read from the other end. <b>Do not let
/// the three drift.</b> <c>The_variant_concept_table_mirrors_the_profile_one_column_for_column</c>
/// is what says so out loud, because a column added to one of them and not the others is a join
/// that silently starts answering a narrower question.
///
/// <b>These concepts feed selection only, and that is the guard this feature turns on.</b> Nothing
/// may copy a row from here into <c>ProfileConcepts</c>, and nothing may let one move a match
/// score: the profile is the only source of truth for what the candidate is judged to have, a CV is
/// written from it, and a document that could write back would inflate the record it was derived
/// from - after which the loop applies to jobs on the strength of its own prose. The tables are
/// separate rather than one table with a discriminator precisely so that "which rows are the
/// candidate's qualifications" is answered by which table you are reading, not by a filter somebody
/// has to remember.
///
/// <b><see cref="Polarity"/> is stored and the selector is not shown it.</b> The column is here
/// because the shapes must match; <c>CvVariantFacts</c> carries concept <i>keys</i> and nothing
/// else, so a document describing itself emphatically cannot outscore one that mentions the same
/// work plainly. The narrowing belongs in the projection, where it is visible, rather than in the
/// schema, where its absence would be indistinguishable from an oversight.
/// </remarks>
public sealed class CvVariantConceptEntity
{
    public long VariantId { get; set; }
    public CvVariantEntity? Variant { get; set; }

    public int ConceptId { get; set; }
    public ConceptEntity? Concept { get; set; }

    /// <summary>
    /// How this concept was arrived at. Part of the key, exactly as on the other two sides.
    /// </summary>
    /// <remarks>
    /// A concept the candidate named in a heading and again in a bullet is one row per source, so
    /// the distinction survives: it is not equally good evidence, and a collapse cannot be undone
    /// afterwards. <c>AssertionSource.Model</c> is what an extractor read out of the markdown, which
    /// is nearly everything here - a variant has no board to tag it and no form to declare on.
    /// </remarks>
    public AssertionSource Source { get; set; }

    /// <summary>The supply half: Familiar, Proficient, Expert. Stored, never scored on.</summary>
    public AssertionPolarity Polarity { get; set; }

    public int? YearsMin { get; set; }
    public int? YearsMax { get; set; }

    /// <summary>The phrase in the CV this was read from, so a selection can be explained.</summary>
    public string? EvidenceText { get; set; }

    public double? Confidence { get; set; }

    /// <summary>
    /// Which resolver wrote this, so a vocabulary improvement can be re-applied.
    /// </summary>
    /// <remarks>
    /// Re-deriving a variant's concepts costs nothing outside this table: the markdown is stored, it
    /// is the candidate's and it does not change when the vocabulary does. That is the same bargain
    /// <c>PostingExtractions.PayloadJson</c> strikes on the posting side, and it is why rows below
    /// the current version can simply be rebuilt rather than re-asked for.
    /// </remarks>
    public int ResolverVersion { get; set; }
}
