namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One concept a posting parked for want of a CV is waiting to see covered.
/// </summary>
/// <remarks>
/// <b>This table is what makes <c>ParkReason.NoCvVariant</c> a retry rather than a loop, and it is
/// the sharpest corner of the design.</b> A posting nothing fits is parked, and it must return
/// <i>when a covering variant exists</i> - not on the next run, because the gap is a pure function
/// of the posting's requirements and the library, and a run changes neither: the next pass meets
/// the same advert, scores the same variants, computes the same gap and parks it again, having
/// spent a page load to learn what the last run already knew. One missing CV would park every
/// posting that wanted it, on every run, for ever.
///
/// <b>And not on any authoring event either, which is the same loop at a longer period.</b>
/// Releasing a Kubernetes-shaped posting because a second backend CV was written hands the bill to
/// the person who has just done the work - they write a CV, the queue hands back adverts it still
/// cannot apply to, and the next park is identical. So the park records <i>which</i> concepts it
/// was missing, and the release condition is coverage of those, which is a join.
///
/// <b>Rows rather than a JSON column, and that is the entire reason this is a table.</b> Every
/// other list on this side of the schema is JSON - <c>EmphasisedJson</c>, <c>SubmittedFieldsJson</c>,
/// <c>OptionsJson</c> - because each is read back whole to be shown and never queried into. This one
/// is queried into twice: the queue joins it against <c>CvVariantConcepts</c> to decide whether a
/// posting comes back, and the gap brief groups it by concept to rank the CVs worth writing by how
/// many applyable postings each would unblock. Neither is expressible over a JSON string, and the
/// brief in particular is a group-by across candidates' whole parked backlogs.
///
/// <b>The concept id and not the key</b>, so the join is the one every other table here makes:
/// <c>skill.kubernetes</c> is the identity and the id is its projection, and a query that had to
/// resolve strings through <c>Concepts</c> first could not reach <c>ConceptClosure</c> - which is
/// what lets a variant naming a specialisation cover the demand above it, exactly as the matcher
/// already does. Every key that reaches here came off a <c>PostingConcepts</c> row, so it has an id
/// by construction.
///
/// <b>The queue's clause has three parts and getting any of them wrong reintroduces the loop.</b>
/// Read in order:
/// <list type="number">
/// <item><description><b>Only the gaps this park recorded count</b> - <see cref="RecordedAtUtc"/>
/// at or after <c>Submissions.ParkedAtUtc</c>. A submission may be parked, unparked and parked
/// again for a different reason, and rows from a superseded park fall out of the predicate by
/// arithmetic. Nothing is deleted to make that true, which is the rule this whole table lives
/// under.</description></item>
/// <item><description><b>Some variant must cover every one of them</b>, never merely "the library
/// has grown" and never "nothing is missing". The existential is over variants and the universal is
/// over gaps: written the other way round, a library covering Kubernetes in one CV and Terraform in
/// another would release a posting that needs both in one document, which is precisely the
/// application that comes back to nobody.</description></item>
/// <item><description><b>A park with no gaps recorded is held, not released.</b> A universal over
/// an empty set is vacuously true, so a clause that forgot this would release every posting whose
/// gap rows failed to be written - the loop again, arriving through a bug rather than through a
/// decision. Holding costs a delay the standing report already names and somebody can act on; the
/// two mistakes are not the same size.</description></item>
/// </list>
///
/// <b>This is also the reason parking had to be an attribute rather than an event.</b> The report -
/// which postings wait on which missing CV, and how many share a gap - is a query over the parked
/// rows, which a <c>Blocked</c> event could answer only by folding every posting's log first. And
/// the release is many-to-one: one variant lets a dozen postings back at once, which an append-only
/// log with no eraser cannot express at all.
/// </remarks>
public sealed class SubmissionParkGapEntity
{
    /// <summary>The parked submission. The gap belongs to a park, not to a posting.</summary>
    /// <remarks>
    /// Keyed on the submission rather than on <c>(ProfileId, PostingId)</c> because the park lives
    /// on the submission row and the pair is already on it - three columns saying what one says,
    /// free to disagree with it, is the arrangement <c>Submissions.DocumentRevision</c> already
    /// avoids next door.
    /// </remarks>
    public long SubmissionId { get; set; }

    public SubmissionEntity? Submission { get; set; }

    /// <summary>The concept this posting asked for that no live variant answered.</summary>
    public int ConceptId { get; set; }

    public ConceptEntity? Concept { get; set; }

    /// <summary>
    /// The park this gap was computed for, stamped with that park's own instant.
    /// </summary>
    /// <remarks>
    /// <b>Written from <c>Submissions.ParkedAtUtc</c> and never from a second clock read.</b> The
    /// two are compared, so a writer that stamped its own <c>UtcNow</c> a millisecond early would
    /// drop every gap out of the predicate - and a park whose gaps have all fallen out is the empty
    /// set the clause above must refuse to treat as coverage. One value, written twice, is the
    /// single-writer rule this codebase applies to every derived key.
    ///
    /// The comparison is "at or after" rather than equality for the same reason. Equality is
    /// exactly right and fails catastrophically when it is not; "at or after" admits a row stamped
    /// late by a disagreeing clock, which holds the posting one run longer and nothing else. It is
    /// the same choice <c>CvVariant.IsRenderCurrent</c> makes about two timestamps written by two
    /// hosts: the safe reading is the one whose mistake costs a delay rather than an application.
    /// </remarks>
    public DateTimeOffset RecordedAtUtc { get; set; }
}
