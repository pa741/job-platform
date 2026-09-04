using JobPlatform.Core.Applications;
using JobPlatform.Data.Sql;
using JobPlatform.Documents;
using Microsoft.Extensions.Logging;

namespace JobPlatform.Data.Applications;

/// <summary>
/// Turns one CV variant's markdown into the two formats an upload box asks for, and puts both
/// where a browser can fetch them.
/// </summary>
/// <remarks>
/// <b>This class is what is left of a job a model used to do.</b> The CV was written per posting
/// by the application writer, and on the first real run that writer answered a question about the
/// candidate with their citizenship and the sentence "I am an AI and they should have seen this" -
/// stored, served through the pack, and one form submission away from a real employer. The prose
/// in a variant is the candidate's, so what remains to automate is the mechanical half: parse the
/// markdown they wrote, lay it out twice, upload it. <b>Nothing in this file reads a posting, calls
/// a model, or changes a word.</b>
///
/// <b>It renders and the repository records, and that split is not tidiness.</b>
/// <see cref="RenderAsync"/> returns exactly the record <c>CvVariantRepository.RecordRenderAsync</c>
/// takes, so both paths and the digest reach SQL in one update <i>after</i> the uploads have
/// already succeeded. Anything in between - a caller assembling its own shape, a write per file -
/// opens a window in which the row claims to be rendered while pointing at a blob that is not there
/// yet: <c>CvVariant.IsRenderCurrent</c> goes true, selection picks the variant up, and the browser
/// loop finds the file missing at the upload box with the tab already open, which is the late
/// discovery <c>SubmissionQuota</c> exists to prevent on the other side of the same loop.
///
/// <b>A failed render must not fail the save.</b> The one thing here that can throw is a renderer
/// and it is wrapped; the store answers null for every storage failure by contract. So this answers
/// null: the markdown is the record, the files are a copy of it, and a variant whose words are
/// stored and whose render failed is recoverable by rendering it again - where typing lost to a
/// MigraDoc exception is not. It is also visible where it matters rather than silent, which is the
/// half that makes swallowing defensible: <c>CvVariant.IsSendable</c> already excludes a variant
/// with no current PDF, so the cost is a line on the dashboard saying a CV needs rendering, not an
/// application made against a document that does not exist. That is the contract
/// <c>ScraperConfigPublisher</c> runs under, and the same trade.
///
/// <b>Neither format's title carries the variant's label, and that is the filename's rule enforced
/// at the second door.</b> A title is not decoration: MigraDoc writes it into the PDF's Info
/// dictionary and <c>MarkdownDocxRenderer</c> writes it into <c>docProps/core.xml</c>, both of
/// which travel with the file and are shown by the readers an employer opens it in. So "AI &amp;
/// data platforms" in a title is the same disclosure <c>ApplicationPackFile</c> refuses to put in a
/// filename - the fact that a different CV is kept for other roles, handed over before anybody
/// reads a word the candidate wrote. That class has nowhere to pass a label; <see cref="Title"/>
/// takes a person's name and nothing else, for the same reason.
/// </remarks>
public sealed class CvVariantRenderer(
    ICvVariantFileStore files,
    TimeProvider time,
    ILogger<CvVariantRenderer> logger)
{
    /// <summary>What every variant's document is called inside itself, whichever one it is.</summary>
    /// <remarks>
    /// The written-out form rather than "CV", matching the filename's stem and for the argument
    /// made there: two letters read as the start of a name, and a name is where a role ends up.
    /// </remarks>
    private const string DocumentTitle = "Curriculum Vitae";

    /// <summary>
    /// Renders a variant to PDF and DOCX and uploads both. Null where there is nothing to record.
    /// </summary>
    /// <remarks>
    /// <b>The timestamp is taken before the render rather than after it, and the direction is the
    /// whole point.</b> <c>CvVariant.IsRenderCurrent</c> compares it against
    /// <see cref="CvVariant.AuthoredAtUtc"/>, and what is being laid out is a snapshot of the
    /// markdown taken before the first byte of it is read. A save landing while the renderer works
    /// therefore carries an authoring time <i>after</i> this stamp, the render reads as stale, and
    /// the next one picks up the newer words - one wasted render, deterministic and free. Stamped
    /// at the end instead, that same interleaving produces a row claiming a file describes text it
    /// has never seen, which is a PDF of a paragraph the candidate deleted, uploaded under their
    /// name and not recoverable once a form is submitted.
    ///
    /// <b>The PDF is required and the DOCX is not.</b> A variant with only a PDF can be uploaded to
    /// most forms and one with no PDF can be uploaded nowhere, which is the rule
    /// <c>CvVariant.IsRendered</c> already states from the other side - so a failed PDF returns null
    /// and records nothing at all, while a failed DOCX returns a render carrying a null path.
    /// <c>RecordRenderAsync</c> stores that null rather than leaving the previous DOCX standing,
    /// which is right: the three values describe one pass of the renderer over one version of the
    /// text, and a path kept from an earlier pass would point at a file the new digest does not
    /// describe.
    ///
    /// <b>The digest is the PDF's.</b> There is one <c>Sha256</c> column and two files, so it has
    /// to name one of them, and it names the file that must exist for the variant to be sendable
    /// and the one an upload box asks for by default. A digest meaning the PDF on most rows and the
    /// DOCX on some would answer "what exactly did we send them" <i>wrongly</i> rather than not at
    /// all, which is the worse of the two failures by a distance.
    ///
    /// <b>An unwritten variant is refused rather than rendered.</b> Identity is the database's to
    /// assign, so <see cref="CvVariant.Id"/> is zero until the row exists - and
    /// <c>ApplicationPackFile.VariantBlobPath</c> would put every such variant in the same
    /// <c>{profile}/0/</c> directory, where each overwrites the last under a filename that no
    /// longer distinguishes them, while the repository refuses to record any of them because no row
    /// has that id. One directory per variant is what makes a stored digest describe the bytes at
    /// that path, and refusing here is what keeps it true.
    ///
    /// <b>It renders whatever it is handed, including an archived variant.</b> Archiving governs
    /// selection and nothing else, and a template fix that reaches every stored document should not
    /// stop at the retired ones - they are what a past application is explained by. The caller
    /// decides what is worth rendering; this decides only whether it can be.
    /// </remarks>
    /// <param name="profileId">Whose library this is. The caller's own, already resolved from a token.</param>
    /// <param name="variant">The variant as stored. Its markdown is what is laid out, unaltered.</param>
    /// <param name="candidateName">The candidate's own name, for the filename. Blank is allowed.</param>
    /// <param name="ct">Cancelled work throws, unlike a failure, because it is not one.</param>
    public async Task<RenderedVariant?> RenderAsync(
        long profileId,
        CvVariant variant,
        string? candidateName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variant);

        if (profileId <= 0 || variant.Id <= 0)
        {
            logger.LogWarning(
                "Refusing to render variant {VariantId} for profile {ProfileId}: a variant is "
                + "rendered into a directory of its own, and an unassigned id has no directory - "
                + "every unsaved variant would share one and overwrite the last. Save the variant "
                + "first, then render the row that comes back.",
                variant.Id,
                profileId);

            return null;
        }

        if (string.IsNullOrWhiteSpace(variant.Markdown))
        {
            // Unreachable through CvVariant.Create, which refuses a blank CV outright, and checked
            // because of what it would produce if it ever were reachable: a blank PDF that uploads
            // to an employer as cleanly as a real one and is discovered by a person reading it.
            logger.LogWarning(
                "Refusing to render variant {VariantId}: it has no markdown, and a blank CV "
                + "renders to a document that can be sent.",
                variant.Id);

            return null;
        }

        var renderedAtUtc = time.GetUtcNow();
        var title = Title(candidateName);

        var pdf = await StoreAsync(profileId, variant, candidateName, title, PackFormat.Pdf, ct);

        if (pdf is null)
        {
            // Nothing is recorded, so the row keeps whatever it had: the previous render's files
            // stay in place and stay explainable, and the variant is simply not current. Recording
            // a DOCX-only render instead would leave a row that reads as rendered and has no file
            // most forms would take.
            return null;
        }

        var docx = await StoreAsync(profileId, variant, candidateName, title, PackFormat.Docx, ct);

        return new RenderedVariant
        {
            PdfBlobPath = pdf.BlobPath,
            DocxBlobPath = docx?.BlobPath,
            Sha256 = pdf.Sha256,
            RenderedAtUtc = renderedAtUtc,
        };
    }

    /// <summary>
    /// Lays one format out and uploads it. Null where either half did not happen.
    /// </summary>
    /// <remarks>
    /// <b>Only the render is wrapped, because only the render can throw.</b>
    /// <see cref="ICvVariantFileStore"/> answers null for every storage failure by contract, which
    /// is the half of this that was already safe. What a renderer can fail on is the markdown -
    /// a construct the AST maps onto nothing, a font resolver that did not install, an OOXML part
    /// the SDK refused - and this markdown is a person's, typed rather than generated, so it will
    /// eventually contain something nobody anticipated.
    ///
    /// <b>The log names the variant and the format and never the markdown.</b> That text is
    /// somebody's CV: it carries their employment history and their contact details, this line is
    /// read by whoever is deciding whether a renderer has a bug rather than by anybody recovering
    /// data, and a log is the wrong place for either. The same rule the application endpoints
    /// already keep about a tailored CV, and a stronger one here because nothing regenerates this
    /// document.
    /// </remarks>
    private async Task<StoredPackFile?> StoreAsync(
        long profileId,
        CvVariant variant,
        string? candidateName,
        string title,
        PackFormat format,
        CancellationToken ct)
    {
        byte[] content;

        try
        {
            content = format == PackFormat.Docx
                ? MarkdownDocxRenderer.Render(variant.Markdown, title)
                : MarkdownPdfRenderer.Render(variant.Markdown, title);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not render variant {VariantId} as {Format}. The markdown is saved and is "
                + "the record; the variant stays out of selection until a render succeeds, which "
                + "is what CvVariant.IsSendable says on the dashboard.",
                variant.Id,
                format);

            return null;
        }

        return await files.StoreVariantAsync(
            new VariantFileRequest
            {
                ProfileId = profileId,
                VariantId = variant.Id,
                Format = format,
                Content = content,
                CandidateName = candidateName,
            },
            ct);
    }

    /// <summary>
    /// What the document calls itself: <c>Pablo De Groot - Curriculum Vitae</c>.
    /// </summary>
    /// <remarks>
    /// <b>A person and a document kind, and there is nowhere here to put anything else.</b> This
    /// string is written into the PDF's Info dictionary and the DOCX's core properties, so it
    /// leaves the tenant inside the file and is shown by the reader an employer opens it in - the
    /// same exposure the filename has, one layer further in, and therefore the same rule: no label,
    /// no posting, no employer, nothing about which of the candidate's CVs this is.
    ///
    /// <b>The blank case is the generic title rather than a cleverer default.</b> A profile with no
    /// name gets a document called <c>Curriculum Vitae</c>, exactly as it gets a file called
    /// <c>Curriculum_Vitae.pdf</c>: there is nothing else on a profile that identifies a person
    /// without disclosing something they did not choose to disclose, so the fix is a name on the
    /// profile rather than a substitute invented here.
    ///
    /// A hyphen rather than an em dash, because this reaches <c>docProps/core.xml</c> through an
    /// <c>XmlWriter</c> and a PDF string, and the plain form survives every encoding either of them
    /// might be read back through.
    /// </remarks>
    private static string Title(string? candidateName)
        => string.IsNullOrWhiteSpace(candidateName)
            ? DocumentTitle
            : $"{candidateName.Trim()} - {DocumentTitle}";
}
