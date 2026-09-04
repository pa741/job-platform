using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Applications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// What a variant's render hands the repository, and what it refuses to hand it.
/// </summary>
/// <remarks>
/// <b>Written against a stub store rather than a storage account, which is the only way these
/// assertions can exist at all.</b> No suite here reaches Azure - deliberately, so a fresh clone
/// runs green with no credential - and the facts worth pinning are on the far side of an upload:
/// that the digest recorded is the digest of the <i>PDF</i> and not of the DOCX, that both files
/// land under the variant's own id, and that a filename carries no trace of which CV this is.
/// <c>ICvVariantFileStore</c> exists partly for this.
///
/// <b>The failure cases are the point of the class, so they are the bulk of the file.</b> Rendering
/// must not fail a save: the markdown is the record, a variant whose render failed is recoverable
/// by rendering it again, and <c>CvVariant.IsSendable</c> already keeps an unrendered variant out
/// of selection - so the correct behaviour is a null and a log, and the thing to guard against is
/// somebody later making it throw, or making it record half a render.
///
/// The renderers themselves are asserted in <see cref="MarkdownPdfRendererTests"/> and
/// <see cref="MarkdownDocxRendererTests"/>. What is asserted here is what happens <i>around</i>
/// them.
/// </remarks>
public sealed class CvVariantRendererTests
{
    private const string Markdown = """
        # Pablo De Groot

        Platform engineer. Kubernetes, Terraform, .NET.

        ## Experience

        - Built a job-market pipeline on Azure.
        - Ran the thing that ran the thing.
        """;

    [Fact]
    public async Task A_render_uploads_both_formats_under_the_variants_own_id()
    {
        var store = new RecordingStore();

        var rendered = await Renderer(store).RenderAsync(12, Variant(34), "Pablo De Groot");

        Assert.NotNull(rendered);

        // The variant id is a directory and never part of the filename, which is what lets two
        // applications made with two different CVs upload files whose names are identical.
        Assert.Equal("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", rendered.PdfBlobPath);
        Assert.Equal("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.docx", rendered.DocxBlobPath);

        Assert.Equal(new[] { PackFormat.Pdf, PackFormat.Docx }, store.Requests.Select(r => r.Format));
        Assert.All(store.Requests, request => Assert.Equal(34, request.VariantId));
        Assert.All(store.Requests, request => Assert.Equal(12, request.ProfileId));
    }

    [Fact]
    public async Task The_recorded_digest_is_the_pdfs_and_not_the_docxs()
    {
        // One Sha256 column and two files, so it has to name one of them - and it names the file
        // that must exist for the variant to be sendable. A digest meaning the PDF on most rows
        // and the DOCX on some would answer "what exactly did we send them" wrongly rather than
        // not at all, which is the worse failure of the two.
        var store = new RecordingStore();

        var rendered = await Renderer(store).RenderAsync(12, Variant(34), "Pablo De Groot");

        Assert.NotNull(rendered);

        var pdf = store.Requests.Single(r => r.Format == PackFormat.Pdf).Content;
        var docx = store.Requests.Single(r => r.Format == PackFormat.Docx).Content;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pdf)), rendered.Sha256);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(docx)), rendered.Sha256);
    }

    [Fact]
    public async Task The_render_is_stamped_before_the_words_could_change_under_it()
    {
        // IsRenderCurrent compares this against AuthoredAtUtc, and what is being laid out is a
        // snapshot taken before the first byte of markdown is read. Stamped at the end, a save
        // landing mid-render would produce a row claiming a file describes text it never saw -
        // which is a PDF of a paragraph the candidate deleted, under their name. Stamped at the
        // start, that same interleaving costs one deterministic re-render.
        var time = new FakeTime(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore();

        var rendered = await new CvVariantRenderer(store, time, NullLogger<CvVariantRenderer>.Instance)
            .RenderAsync(12, Variant(34), "Pablo De Groot");

        Assert.NotNull(rendered);
        Assert.Equal(time.First, rendered.RenderedAtUtc);
    }

    [Fact]
    public async Task Neither_format_carries_the_variants_label_in_its_metadata()
    {
        // The same disclosure the filename refuses, one layer in. A title is written into the
        // PDF's Info dictionary and into docProps/core.xml, so it travels with the file and is
        // shown by whatever an employer opens it in - and "AI & data platforms" there tells them
        // a different CV is kept for other roles, before anybody reads a word.
        var store = new RecordingStore();

        var rendered = await Renderer(store).RenderAsync(
            12, Variant(34, label: "AI & data platforms"), "Pablo De Groot");

        Assert.NotNull(rendered);
        Assert.DoesNotContain("data platforms", rendered.PdfBlobPath, StringComparison.OrdinalIgnoreCase);

        var pdf = Encoding.Latin1.GetString(store.Requests.Single(r => r.Format == PackFormat.Pdf).Content);
        var docx = CoreProperties(store.Requests.Single(r => r.Format == PackFormat.Docx).Content);

        Assert.DoesNotContain("data platforms", pdf, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data platforms", docx, StringComparison.OrdinalIgnoreCase);

        // And the title that is there is the person and the document kind, which is what a reader
        // shows in its window title.
        Assert.Contains("Pablo De Groot - Curriculum Vitae", docx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_variant_with_no_name_on_the_profile_still_renders_under_the_generic_title()
    {
        // The fallback is deliberately unhelpful rather than clever: there is nothing else on a
        // profile that identifies somebody without disclosing something, and an email address in
        // a filename is worse rather than better. The fix is a name on the profile.
        var store = new RecordingStore();

        var rendered = await Renderer(store).RenderAsync(12, Variant(34), candidateName: null);

        Assert.NotNull(rendered);
        Assert.Equal("profile-cvs/12/34/Curriculum_Vitae.pdf", rendered.PdfBlobPath);
        Assert.Contains(
            "Curriculum Vitae",
            CoreProperties(store.Requests.Single(r => r.Format == PackFormat.Docx).Content),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_pdf_upload_records_nothing_at_all()
    {
        // Half a render is worse than none. A row carrying a DOCX path and a rendered timestamp
        // reads as sendable, and most forms want the PDF - so the pack would hand the browser loop
        // a variant whose file it discovers is missing at the upload box.
        var store = new RecordingStore { Refuse = PackFormat.Pdf };

        Assert.Null(await Renderer(store).RenderAsync(12, Variant(34), "Pablo De Groot"));
    }

    [Fact]
    public async Task A_failed_docx_upload_still_records_the_pdf_with_no_docx_path()
    {
        // A variant with only a PDF can be uploaded to most forms; one with no PDF can be uploaded
        // nowhere. The null path is the honest answer and RecordRenderAsync stores it as one,
        // clearing any DOCX from an earlier pass - the three values describe one render, and a
        // path kept from the last one would point at a file the new digest does not describe.
        var store = new RecordingStore { Refuse = PackFormat.Docx };

        var rendered = await Renderer(store).RenderAsync(12, Variant(34), "Pablo De Groot");

        Assert.NotNull(rendered);
        Assert.Equal("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", rendered.PdfBlobPath);
        Assert.Null(rendered.DocxBlobPath);
    }

    [Theory]
    [InlineData(0, 34)]
    [InlineData(12, 0)]
    [InlineData(-1, 34)]
    public async Task An_unwritten_variant_is_refused_rather_than_rendered(long profileId, long variantId)
    {
        // Identity is the database's to assign, so an id of zero is a row that does not exist yet.
        // Rendering one would put every unsaved variant in the same {profile}/0/ directory, each
        // overwriting the last under a filename that no longer distinguishes them - and the
        // repository would refuse to record any of them, leaving orphaned blobs behind.
        var store = new RecordingStore();

        Assert.Null(await Renderer(store).RenderAsync(profileId, Variant(variantId), "Pablo De Groot"));
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task A_variant_with_no_markdown_is_refused_rather_than_rendered()
    {
        // Unreachable through CvVariant.Create, which refuses a blank CV outright, and refused
        // here because of what it would produce if it ever were reachable: a blank PDF that
        // uploads to an employer as cleanly as a real one.
        var store = new RecordingStore();

        var variant = new CvVariant
        {
            Id = 34,
            Label = "Backend .NET",
            Markdown = "   ",
            AuthoredAtUtc = DateTimeOffset.UnixEpoch,
        };

        Assert.Null(await Renderer(store).RenderAsync(12, variant, "Pablo De Groot"));
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task An_archived_variant_still_renders()
    {
        // Archiving governs selection and nothing else. A template fix that reaches every stored
        // document must not stop at the retired ones - they are what a past application is
        // explained by, and Submissions.CvVariantId names them by id.
        var rendered = await Renderer(new RecordingStore())
            .RenderAsync(12, Variant(34) with { IsArchived = true }, "Pablo De Groot");

        Assert.NotNull(rendered);
    }

    [Fact]
    public async Task Re_rendering_writes_the_same_path_so_the_stored_hash_describes_it()
    {
        // The path is derived from the variant rather than from the moment, so a re-render
        // replaces the file it replaces in SQL. A fresh path per render would leave the previous
        // file behind with a row that no longer describes it, and "what exactly did we send them"
        // is a question about a file that has to still exist and still be the same file.
        var store = new RecordingStore();
        var renderer = Renderer(store);

        var first = await renderer.RenderAsync(12, Variant(34), "Pablo De Groot");
        var second = await renderer.RenderAsync(12, Variant(34), "Pablo De Groot");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.PdfBlobPath, second.PdfBlobPath);
        Assert.Equal(first.DocxBlobPath, second.DocxBlobPath);
    }

    private static CvVariantRenderer Renderer(ICvVariantFileStore store)
        => new(store, TimeProvider.System, NullLogger<CvVariantRenderer>.Instance);

    private static CvVariant Variant(long id, string label = "Backend .NET")
        => new()
        {
            Id = id,
            Label = label,
            Markdown = Markdown,
            AuthoredAtUtc = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
        };

    /// <summary>The DOCX's core properties, which is where its title travels.</summary>
    private static string CoreProperties(byte[] docx)
    {
        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("docProps/core.xml")!.Open());

        return reader.ReadToEnd();
    }

    /// <summary>
    /// A store that keeps what it was handed and builds the path the real one would.
    /// </summary>
    /// <remarks>
    /// The path comes from <c>ApplicationPackFile.VariantBlobPath</c> rather than from a literal,
    /// because the assertion worth making is that the renderer passed the right profile, variant,
    /// name and format - not that this test can spell a path. The container name is the default
    /// one, which is what the deployment sets.
    /// </remarks>
    private sealed class RecordingStore : ICvVariantFileStore
    {
        public List<VariantFileRequest> Requests { get; } = [];

        /// <summary>The format this store answers null for, as a storage failure would.</summary>
        public PackFormat? Refuse { get; init; }

        public Task<StoredPackFile?> StoreVariantAsync(
            VariantFileRequest file, CancellationToken ct = default)
        {
            Requests.Add(file);

            if (Refuse == file.Format)
            {
                return Task.FromResult<StoredPackFile?>(null);
            }

            var path = ApplicationPackFile.VariantBlobPath(
                "profile-cvs", file.ProfileId, file.VariantId, file.CandidateName, file.Format);

            return Task.FromResult<StoredPackFile?>(new StoredPackFile
            {
                BlobPath = path,
                FileName = path[(path.LastIndexOf('/') + 1)..],
                ContentType = ApplicationPackFile.ContentType(file.Format),
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Content)),
                Length = file.Content.Length,
            });
        }
    }

    /// <summary>A clock that moves on every read, so "which reading was recorded" is answerable.</summary>
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private int _reads;

        public DateTimeOffset First => start;

        public override DateTimeOffset GetUtcNow() => start.AddMinutes(_reads++);
    }
}
