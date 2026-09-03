namespace JobPlatform.Core.Applications;

/// <summary>
/// One rendered file, on its way to storage.
/// </summary>
/// <remarks>
/// <b>Named members rather than positional</b>, for the reason <c>RenderedDocuments</c> has them:
/// two <c>long</c> identifiers side by side are a transposition the compiler cannot see, and a
/// profile id in the document position writes the file to a path that belongs to nobody - it
/// uploads cleanly, records cleanly, and is simply never found again.
///
/// <b>The bytes are passed, not a stream.</b> The renderer already has them in memory and the
/// store has to hash them as well as upload them; a stream would be read twice or copied to be
/// read twice. These are a few hundred kilobytes and they are already the smaller half of what
/// the generation call cost.
/// </remarks>
public sealed record PackFileRequest
{
    /// <summary>Whose document this is. From the caller's resolved profile, never an argument.</summary>
    public required long ProfileId { get; init; }

    /// <summary>The <c>ApplicationDocuments</c> row this was rendered from.</summary>
    /// <remarks>
    /// The document rather than the posting, so a regeneration writes beside what it replaces
    /// instead of over it. A candidate may already have sent revision one.
    /// </remarks>
    public required long DocumentId { get; init; }

    /// <summary>Which document, which decides what the file is called.</summary>
    public required PackDocument Document { get; init; }

    /// <summary>What it was rendered as.</summary>
    public required PackFormat Format { get; init; }

    /// <summary>The rendered bytes.</summary>
    public required byte[] Content { get; init; }

    /// <summary>
    /// The candidate's own name, for the filename.
    /// </summary>
    /// <remarks>
    /// Read from the profile by the caller rather than by the store, which has no repository and
    /// should not grow one: the caller has already resolved the profile to get here, and a store
    /// that looked names up would be a second place where a document id could be turned into
    /// somebody's identity.
    ///
    /// Blank is allowed and produces a generic filename - see <see cref="ApplicationPackFile"/> on
    /// why the fallback is deliberately unhelpful.
    /// </remarks>
    public string? CandidateName { get; init; }
}

/// <summary>
/// Where a rendered file ended up, and what it is.
/// </summary>
/// <remarks>
/// <b>A path and a hash, which is exactly what <c>RenderedDocuments</c> stores.</b> The caller
/// writes both onto the document row in one call; nothing here writes to SQL, and nothing in SQL
/// talks to storage, which is the split <c>ApplicationDocumentRepository</c> spells out.
///
/// <b>No URL.</b> A link is minted per request and expires; one returned here would be recorded
/// beside the path and would go on looking live long after it stopped being.
/// </remarks>
public sealed record StoredPackFile
{
    /// <summary>The stored reference, container-qualified. Goes straight into the document row.</summary>
    public required string BlobPath { get; init; }

    /// <summary>What a download will be called.</summary>
    /// <remarks>
    /// Returned so the pack can name the file it is offering without recomputing the rule. A
    /// model filling in a form is told what to expect in the downloads folder, which is the
    /// difference between attaching the right file and attaching the newest one.
    /// </remarks>
    public required string FileName { get; init; }

    /// <summary>The media type the blob was stored with.</summary>
    public required string ContentType { get; init; }

    /// <summary>SHA-256 of the bytes as stored, lower-case hex.</summary>
    /// <remarks>
    /// Computed here because this is the only place that holds the exact bytes that were written.
    /// It is what <c>RenderedDocuments.CvSha256</c> wants, and it is what makes the blob checkable
    /// against the row later: a path alone cannot say whether the file at the end of it is still
    /// the one that was sent.
    /// </remarks>
    public required string Sha256 { get; init; }

    /// <summary>How many bytes were stored.</summary>
    public required long Length { get; init; }
}

/// <summary>
/// Where rendered application documents are kept, and how a browser is let at one.
/// </summary>
/// <remarks>
/// <b>An interface in Core with its implementation in the API, because every consumer resolves it
/// as nullable.</b> That is the shape <c>IRealtimeFeed</c>, <c>IAiCallLog</c>,
/// <c>IDisclosureLog</c> and <c>IApplicationWriter</c> all have, and it is what makes a
/// deployment with no storage configured degraded rather than broken: nothing is registered, the
/// pack resolves null, and it says in its <c>note</c> that no file is available - the same answer
/// it already gives for a posting whose documents were never generated. A concrete class here
/// would make "no storage" a missing dependency at startup instead of an absent capability at
/// runtime, and would put a <c>BlobServiceClient</c> in the constructor of everything that only
/// wanted to offer a link.
///
/// <b>Neither method throws.</b> A storage failure must not fail the generation that produced the
/// document or the pack that was only trying to attach a link to it - the contract
/// <c>ScraperConfigPublisher</c> runs under, for the same reason: the markdown is the record, the
/// rendered file is a convenience, and losing the convenience must not lose the record. Both
/// return null and log, and null is a state the caller has to have handled anyway.
///
/// <b>Nothing here takes an identity.</b> The profile id on a request is the caller's own,
/// already resolved from a token; this interface cannot be asked "give me the link for
/// <i>that</i> person's CV" because a path is the only thing it accepts, and paths are read out of
/// rows that were themselves scoped to the caller.
/// </remarks>
public interface IApplicationPackStore
{
    /// <summary>
    /// How long a minted link lives.
    /// </summary>
    /// <remarks>
    /// Exposed so a pack can say so rather than hand a model a URL with no stated shelf life. A
    /// client that does not know a link expires will store it, retry with it an hour later, and
    /// report the failure as a missing document.
    /// </remarks>
    TimeSpan LinkLifetime { get; }

    /// <summary>Uploads one rendered file. Null where it could not be stored.</summary>
    Task<StoredPackFile?> StoreAsync(PackFileRequest file, CancellationToken ct = default);

    /// <summary>
    /// A short-lived read-only URL for a stored path. Null where there is nothing to link to.
    /// </summary>
    /// <remarks>
    /// Minted per request and never cached, because the URL <i>is</i> the authority: anyone
    /// holding the string can read the document until it expires. Storing one would extend that
    /// authority to whoever reads the store.
    /// </remarks>
    Task<Uri?> LinkAsync(string? blobPath, CancellationToken ct = default);
}
