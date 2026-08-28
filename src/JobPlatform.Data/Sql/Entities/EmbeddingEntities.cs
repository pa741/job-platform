using JobPlatform.Core.Matching;

namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One advert as a vector, and enough about its input to know when the vector is stale.
/// </summary>
/// <remarks>
/// <b>A side table rather than a column on <see cref="JobPostingEntity"/>, and a deliberate
/// one.</b> The posting row is read by browse, search and detail, and a two-kilobyte blob on it
/// would be carried by every one of those queries that materialises the entity. Here, it is read
/// by exactly two things: the pass that writes it and the sweep that ranks with it.
///
/// <b>Staleness is decided without reading a description.</b> The obvious marker would be a hash
/// of the embedded text, and computing it would mean pulling every advert's unbounded
/// description across on every pass to discover that almost none of them changed. So the row
/// copies the two columns <c>JobPostingRepository.HasMaterialChange</c> already uses to decide
/// the posting's text moved - <see cref="ContentHash"/> and <see cref="DescriptionLength"/> -
/// and the "what needs embedding" query is a join against them. Cheap, set-based, and derived
/// from the same judgement rather than a second one free to disagree.
///
/// <see cref="EmbeddingVersion"/> is the third marker and the one a developer moves: it is
/// <see cref="EmbeddingVector.EmbeddingVersion"/>, so changing model, dimension or what text is
/// fed in marks every row stale without deleting anything - the same mechanism
/// <c>EnrichedPosting.CurrentVersion</c> and <c>DocumentExtraction.CurrentVersion</c> carry.
/// </remarks>
public sealed class PostingEmbeddingEntity
{
    /// <summary>The key. One vector per posting - a second would only be a stale first.</summary>
    public long PostingId { get; set; }

    public JobPostingEntity? Posting { get; set; }

    /// <summary>
    /// The unit-normalised vector, little-endian IEEE-754.
    /// </summary>
    /// <remarks>
    /// Packed by <see cref="EmbeddingVector.Pack"/> rather than by a block copy, because this is
    /// written by one process and read by another and a format that works only while both share
    /// an architecture is a format that corrupts silently the day one of them does not.
    /// </remarks>
    public byte[] Vector { get; set; } = [];

    /// <summary>Carried so a truncated or half-written blob is recognisable rather than merely wrong.</summary>
    public int Dimensions { get; set; }

    /// <summary>The deployment that produced it. Provenance, for when a number is disputed.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The posting's hash when this was taken. Moves, and the vector is stale.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// The description's length when this was taken.
    /// </summary>
    /// <remarks>
    /// <see cref="ContentHash"/> is over title, company and location only, so on its own it
    /// cannot see an employer editing the body of the advert. This is the same second signal
    /// <c>HasMaterialChange</c> pairs it with, and for the same reason.
    /// </remarks>
    public int DescriptionLength { get; set; }

    public int EmbeddingVersion { get; set; }

    public DateTimeOffset EmbeddedAtUtc { get; set; }
}

/// <summary>
/// One candidate profile as a vector, against which every advert is measured.
/// </summary>
/// <remarks>
/// <b>Its staleness marker is the profile's own extraction hash, reused rather than
/// recomputed.</b> <c>CandidateProfileEntity.ExtractionInputHash</c> is already a hash of
/// <c>CandidateProfile.ToDocument()</c>, and that document is exactly the text embedded here -
/// so the question "has the profile changed since this vector" is already answered by a column
/// that exists. It also means a save that only edits a phone number costs no embedding call,
/// which is the same property that stops it costing an extraction call.
///
/// Not mirrored column-for-column against <see cref="PostingEmbeddingEntity"/>, unlike
/// <c>ProfileConcepts</c> against <c>PostingConcepts</c>. That mirroring exists because matching
/// is a join between those two tables; nothing ever joins these two, so copying a posting's
/// staleness markers here would be shape for its own sake.
/// </remarks>
public sealed class ProfileEmbeddingEntity
{
    public long ProfileId { get; set; }

    public CandidateProfileEntity? Profile { get; set; }

    public byte[] Vector { get; set; } = [];

    public int Dimensions { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>Equal to the profile's <c>ExtractionInputHash</c> when this was taken.</summary>
    public string InputHash { get; set; } = string.Empty;

    public int EmbeddingVersion { get; set; }

    public DateTimeOffset EmbeddedAtUtc { get; set; }
}
