using System.Globalization;
using System.Text;
using JobPlatform.Core.Submissions;

namespace JobPlatform.Core.Applications;

/// <summary>Which of the two documents a rendered file is.</summary>
/// <remarks>
/// Named rather than a boolean, for the reason <c>RenderedDocuments</c> has named members: a
/// <c>bool cv</c> threaded through a render, an upload and a filename is one negation away from
/// putting a covering letter in front of an employer under the name of a CV, and nothing
/// downstream would notice.
/// </remarks>
public enum PackDocument
{
    /// <summary>The tailored CV. The file an upload box asks for by that name.</summary>
    CurriculumVitae = 1,

    /// <summary>The covering letter.</summary>
    CoverLetter = 2,
}

/// <summary>What a document was rendered as.</summary>
/// <remarks>
/// <b>Two formats of one document, never two documents.</b> They are rendered from the same
/// markdown and are interchangeable at the upload box; the choice belongs to whoever is filling
/// the form in, because <c>MarkdownPdfRenderer</c>'s output is what a person would send and a
/// DOCX is what several large ATS vendors parse more reliably. A form that accepts only one of
/// the two is common enough that offering a single format means some applications cannot be
/// completed at all.
/// </remarks>
public enum PackFormat
{
    /// <summary>PDF, from the MigraDoc template. The default, and what a person would send.</summary>
    Pdf = 1,

    /// <summary>DOCX, for the ATS vendors that parse it better than they parse a PDF.</summary>
    Docx = 2,
}

/// <summary>
/// What a rendered document is called, and where it is kept.
/// </summary>
/// <remarks>
/// <b>The filename is a product surface, not an implementation detail.</b> It ends up in a
/// recruiter's file list beside forty others called <c>cv.pdf</c>, in the candidate's downloads
/// folder, and in whatever the ATS renames it to. <c>Pablo_De_Groot_CV.pdf</c> costs nothing to
/// produce and is the difference between a document that can be found again and one that cannot,
/// so the name is derived from the person rather than from the row's identifiers.
///
/// <b>Pure, and in Core, because the two directions have to agree.</b>
/// <see cref="BlobPath"/> writes a reference into <c>ApplicationDocuments</c> and
/// <see cref="TryBlobName"/> reads it back to mint a link; they are inverses, and two copies of
/// one convention in two projects is how a stored path stops resolving without anything failing
/// loudly. Nothing here knows a storage account exists - it is string arithmetic over a
/// container name that the caller supplies.
///
/// <b>Non-ASCII names are kept, not folded away.</b> Blob names are UTF-8 and
/// <c>Content-Disposition</c> has carried a UTF-8 filename since RFC 6266, so there is no
/// technical reason to turn Renée into Renee - and a system that quietly anglicises somebody's
/// name on the document they are judged by has made a choice it should not be making. The ASCII
/// fold survives only as the legacy <c>filename=</c> parameter, for clients that ignore
/// <c>filename*</c>. See <see cref="ContentDisposition"/>.
/// </remarks>
public static class ApplicationPackFile
{
    /// <summary>
    /// How much of a candidate's name reaches the filename.
    /// </summary>
    /// <remarks>
    /// A bound rather than a limit anybody has hit. It keeps the whole stored path structurally
    /// inside <see cref="SubmissionLimits.MaxScreenshotRefLength"/>, which
    /// <c>ApplicationDocumentRepository.RecordRenderedAsync</c> <i>throws</i> on rather than
    /// truncating - the one place in this system where an over-long value is refused instead of
    /// trimmed, because a truncated pointer loses the file it points at. A path this class builds
    /// must never be one that repository would reject.
    /// </remarks>
    public const int MaxNameChars = 60;

    /// <summary>Apostrophes, in every spelling a name arrives in. Dropped rather than separated.</summary>
    /// <remarks>
    /// <c>O'Brien</c> is one word, and folding the apostrophe to a separator would make it
    /// <c>O_Brien</c> - which reads as two names. The same set and the same argument as
    /// <c>FormAnswerText.Normalise</c>, where splitting on an apostrophe would have made one
    /// question into two.
    ///
    /// Spelled numerically rather than pasted, exactly as <c>FormAnswerText</c> spells the same
    /// set: the three are indistinguishable on screen, and a literal an editor had re-encoded
    /// would silently stop matching.
    /// </remarks>
    private static readonly char[] Apostrophes = [(char)0x0027, (char)0x2019, (char)0x02bc];

    /// <summary>
    /// What one rendered document is called: <c>Firstname_Lastname_CV.pdf</c>.
    /// </summary>
    /// <remarks>
    /// <b>Underscores rather than spaces, and one separator rather than several.</b> The name
    /// travels through a URL, a shell somewhere, an email attachment and an ATS's own file
    /// handling; a space survives all of them in principle and is mangled by at least one of them
    /// in practice. Hyphens fold to underscores too - <c>Anne-Marie</c> becomes
    /// <c>Anne_Marie</c> - because one separator character means the result is readable back into
    /// the name that produced it, and two means guessing which was which.
    ///
    /// <b>The fallback is genuinely generic, and that is the honest answer.</b> A profile with no
    /// name gives <c>CV.pdf</c>, the very filename this exists to avoid. There is nothing else on
    /// the profile that identifies the person without disclosing something - an email address in a
    /// filename is worse, not better - so the fix is a name on the profile rather than a cleverer
    /// default here, and the generic name is what says so.
    /// </remarks>
    /// <param name="candidateName">The candidate's own name, as they wrote it. Blank is allowed.</param>
    /// <param name="document">Which document this is.</param>
    /// <param name="format">What it was rendered as.</param>
    public static string FileName(string? candidateName, PackDocument document, PackFormat format)
    {
        var stem = Sanitise(candidateName);
        var kind = document == PackDocument.CoverLetter ? "Cover_Letter" : "CV";
        var extension = Extension(format);

        return stem.Length == 0
            ? $"{kind}.{extension}"
            : $"{stem}_{kind}.{extension}";
    }

    /// <summary>The file extension for a format, without the dot.</summary>
    public static string Extension(PackFormat format)
        => format == PackFormat.Docx ? "docx" : "pdf";

    /// <summary>
    /// What a browser should be told the bytes are.
    /// </summary>
    /// <remarks>
    /// Set on the blob rather than on a response, because nothing in this system serves these
    /// bytes: the browser fetches them from storage with a signed URL, so storage is the only
    /// thing in a position to describe them. A blob with no content type is served as
    /// <c>application/octet-stream</c>, which makes a browser download a CV it could have
    /// displayed - and makes an ATS's upload widget reject it for having the wrong type.
    /// </remarks>
    public static string ContentType(PackFormat format)
        => format == PackFormat.Docx
            ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            : "application/pdf";

    /// <summary>
    /// The <c>Content-Disposition</c> that keeps <see cref="FileName"/> on the downloaded file.
    /// </summary>
    /// <remarks>
    /// <b>Both parameters, always.</b> <c>filename*</c> is the real name, percent-encoded UTF-8
    /// per RFC 5987, and it is what every current browser uses. <c>filename=</c> is the ASCII
    /// fold, and it exists for whatever does not read the starred form - without it such a client
    /// falls back to the last path segment of the URL, which carries a SAS query string. The two
    /// disagree only for a name with characters outside ASCII, which is the case the pair exists
    /// to handle rather than a bug.
    ///
    /// <b><c>attachment</c>, not <c>inline</c>.</b> This is a file destined for an upload box; a
    /// PDF that opens in a tab instead of landing in the downloads folder is one more step for the
    /// person doing the applying, and the DOCX would download regardless - so inline would make
    /// the two formats behave differently for no gain.
    ///
    /// The header is only ever built over <see cref="FileName"/>'s output, which contains letters,
    /// digits, underscores and one dot. That is what makes the quoted form safe to build by
    /// concatenation: there is no quote or backslash in it to escape.
    /// </remarks>
    public static string ContentDisposition(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var ascii = Ascii(fileName);

        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    /// <summary>
    /// Where one rendered file is kept: <c>application-packs/{profile}/{document}/{name}</c>.
    /// </summary>
    /// <remarks>
    /// <b>Container-qualified, so a stored reference says what it points at.</b> The path is
    /// written into a SQL column and read back by whatever mints a link, possibly years later and
    /// certainly by different code; a bare blob name would be meaningless without knowing which
    /// container was configured at the time it was written. <see cref="TryBlobName"/> strips the
    /// prefix back off, and refuses nothing on account of it - see the argument there about what a
    /// foreign path can and cannot reach.
    ///
    /// <b>Keyed by profile then document, in that order.</b> A prefix listing is then per
    /// candidate, which is what a deletion on request has to enumerate; the reverse order would
    /// make "everything belonging to this person" a full scan of the container. The document id
    /// rather than the posting id, because a regeneration is a new document and its files must not
    /// overwrite the ones the candidate may already have sent.
    ///
    /// <b>The name is inside the path rather than beside it</b>, so re-rendering the same document
    /// overwrites the file it replaces. That is deliberate and is stated at
    /// <c>ApplicationDocumentRepository.RecordRenderedAsync</c>: the row's hash then describes the
    /// bytes actually at that path, which a fresh path per render would quietly stop being true.
    /// </remarks>
    public static string BlobPath(string containerName, long profileId, long documentId, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{containerName.Trim().Trim('/')}/{profileId}/{documentId}/{fileName}");
    }

    /// <summary>
    /// The blob a stored reference names, or false where it names nothing this store can serve.
    /// </summary>
    /// <remarks>
    /// <b>The inverse of <see cref="BlobPath"/>, and the reason both live here.</b> Everything
    /// resolved through this is resolved <i>inside</i> the pack container: a reference that names
    /// some other container is treated as a blob name that happens to contain a slash, so the
    /// worst it can do is produce a signed URL for a blob that does not exist. That is the
    /// property worth having - a path from the wrong column, or from a row written by a different
    /// build, can yield a dead link but never a signature over anything outside the one container
    /// whose contents are allowed to leave the tenant.
    ///
    /// <b>It accepts both spellings on purpose.</b> Anything this system writes is
    /// container-qualified; a bare <c>{profile}/{document}/{name}</c> is what an older row or
    /// another tool would hold, and both name the same blob. Refusing the bare form would turn a
    /// resolvable reference into a missing file for no gain.
    ///
    /// <b>What it refuses is what cannot be a blob at all</b>: a blank, something with a scheme in
    /// it - a stored URL is the mistake this codebase has written down three times not to make -
    /// an empty or relative segment, and anything longer than the column it came out of could
    /// hold. Each of those is a bug in the caller rather than a missing file, but the answer is
    /// the same false: the pack degrades to saying no file is available, which is the behaviour
    /// this surface has everywhere.
    /// </remarks>
    public static bool TryBlobName(string containerName, string? storedPath, out string blobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        blobName = string.Empty;

        if (string.IsNullOrWhiteSpace(storedPath)
            || storedPath.Length > SubmissionLimits.MaxScreenshotRefLength
            || storedPath.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = storedPath.Trim().TrimStart('/');
        var prefix = containerName.Trim().Trim('/') + "/";

        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var segment in trimmed.Split('/'))
        {
            // "." and ".." are not path traversal here - the SDK sends the name verbatim and the
            // service has no notion of a parent - but they are certain evidence that whatever
            // built this string thought it was addressing a filesystem, and a link minted from
            // that names the wrong file rather than no file.
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        blobName = trimmed;

        return true;
    }

    /// <summary>
    /// A person's name reduced to one safe token, keeping every script it was written in.
    /// </summary>
    /// <remarks>
    /// Composed to Form C first, exactly as <c>FormAnswerText.Normalise</c> does and for the same
    /// reason: a decomposed é is a letter followed by a combining mark, the mark is not a letter,
    /// and folding it as punctuation would put a separator in the middle of a word.
    ///
    /// Everything that is not a letter or a digit becomes a single underscore, so the output can
    /// contain nothing that means something to a URL, a shell or a header - which is what lets
    /// <see cref="ContentDisposition"/> build a quoted string by concatenation.
    /// </remarks>
    private static string Sanitise(string? candidateName)
    {
        if (string.IsNullOrWhiteSpace(candidateName))
        {
            return string.Empty;
        }

        var composed = candidateName.Trim().Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(Math.Min(composed.Length, MaxNameChars));

        // Starts true so a leading separator emits nothing rather than an underscore that then
        // has to be trimmed back off.
        var lastWasSeparator = true;

        foreach (var ch in composed)
        {
            if (builder.Length >= MaxNameChars)
            {
                break;
            }

            if (Array.IndexOf(Apostrophes, ch) >= 0)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('_');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    /// <summary>
    /// The ASCII fold of a filename, for the legacy <c>filename=</c> parameter only.
    /// </summary>
    /// <remarks>
    /// Decomposed to Form D so an accent separates from the letter it sits on and can be dropped
    /// on its own - <c>Renée</c> folds to <c>Renee</c> rather than losing the whole letter. A name
    /// in a script with no ASCII equivalent folds away entirely, leaving the document kind and the
    /// extension, which are always ASCII: the result is never empty, and it is never the one a
    /// client with a working <c>filename*</c> uses.
    /// </remarks>
    private static string Ascii(string fileName)
    {
        var decomposed = fileName.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSeparator = true;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark
                || !char.IsAscii(ch))
            {
                continue;
            }

            if (ch == '_')
            {
                // A run of underscores is what a folded-away name leaves behind. Collapsing them
                // keeps the fallback readable instead of "Li__Wang_CV.pdf".
                if (!lastWasSeparator)
                {
                    builder.Append('_');
                    lastWasSeparator = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSeparator = false;
        }

        return builder.ToString().Trim('_');
    }
}
