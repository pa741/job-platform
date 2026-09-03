using JobPlatform.Core.Applications;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// What a rendered document is called, and whether the path it is stored at can be read back.
/// </summary>
/// <remarks>
/// Three rules that look cosmetic and are not. The filename appears in a recruiter's file list, so
/// <c>cv.pdf</c> is a product defect rather than a naming preference; the same filename has to
/// come out of every CV variant, or the file list tells an employer that the candidate keeps other
/// CVs for other roles; and the path is written into a SQL column and read back by different code
/// to mint a link, so the two directions have to be inverses or a stored file becomes unreachable
/// with nothing failing.
/// </remarks>
public sealed class ApplicationPackFileTests
{
    private const string Container = "application-packs";

    /// <summary>
    /// Where the library lives, and why it is not <see cref="Container"/>.
    /// </summary>
    /// <remarks>
    /// A document id and a variant id are independent identity spaces that both start at one, so
    /// sharing a container would let document 34 and variant 34 of one profile address the same
    /// directory - and, now that both spell the CV the same way, the same file.
    /// </remarks>
    private const string VariantContainer = "profile-cvs";

    [Fact]
    public void A_document_is_named_after_the_person_it_belongs_to()
    {
        Assert.Equal(
            "Pablo_De_Groot_Curriculum_Vitae.pdf",
            ApplicationPackFile.FileName("Pablo De Groot", PackDocument.CurriculumVitae, PackFormat.Pdf));

        Assert.Equal(
            "Pablo_De_Groot_Cover_Letter.pdf",
            ApplicationPackFile.FileName("Pablo De Groot", PackDocument.CoverLetter, PackFormat.Pdf));

        Assert.Equal(
            "Pablo_De_Groot_Curriculum_Vitae.docx",
            ApplicationPackFile.FileName("Pablo De Groot", PackDocument.CurriculumVitae, PackFormat.Docx));
    }

    [Fact]
    public void A_cv_is_named_for_the_person_and_never_for_the_variant_it_came_from()
    {
        // The property the whole rename exists for. A CV is chosen from a library of variants -
        // "Backend .NET", "AI & data platforms" - and Pablo_De_Groot_AI_Engineer_CV.pdf would tell
        // an employer that a different CV is kept for other roles: true, none of their business,
        // and read off the file list before the document is opened. There is no parameter on
        // FileName a label could travel in, so this is a property of the signature rather than a
        // rule somebody has to remember.
        var name = ApplicationPackFile.FileName("Pablo De Groot", PackDocument.CurriculumVitae, PackFormat.Pdf);

        Assert.EndsWith("_Curriculum_Vitae.pdf", name, StringComparison.Ordinal);
        Assert.DoesNotContain("CV", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_variants_of_one_profile_upload_files_whose_names_are_identical()
    {
        // Two applications, two different CVs, one filename. The variant id separates the paths
        // and nothing separates the files, which is the only shape in which the choice stays
        // inside this tenant.
        var backend = ApplicationPackFile.VariantBlobPath(
            VariantContainer, 12, 34, "Pablo De Groot", PackFormat.Pdf);

        var platforms = ApplicationPackFile.VariantBlobPath(
            VariantContainer, 12, 77, "Pablo De Groot", PackFormat.Pdf);

        Assert.NotEqual(backend, platforms);
        Assert.Equal(LastSegment(backend), LastSegment(platforms));
        Assert.Equal("Pablo_De_Groot_Curriculum_Vitae.pdf", LastSegment(backend));
    }

    [Fact]
    public void A_variant_path_ends_in_the_stable_name_rather_than_in_the_variant_id()
    {
        // The header is a request and the path is the guarantee: the loop hands a browser's file
        // input a local path, and a client that saves a signed URL by its path and ignores
        // Content-Disposition uploads the last segment verbatim. So the id has to be a directory.
        var path = ApplicationPackFile.VariantBlobPath(
            VariantContainer, 12, 34, "Pablo De Groot", PackFormat.Pdf);

        Assert.Equal("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", path);
        Assert.DoesNotContain("34", LastSegment(path), StringComparison.Ordinal);
        Assert.True(ApplicationPackFile.IsStableCvName(path));
    }

    [Fact]
    public void Both_formats_of_one_variant_share_a_directory_and_differ_only_by_extension()
    {
        // Two formats of one document, never two documents: several large ATS parse DOCX more
        // reliably than PDF, and a form that accepts only one of the two is common enough that
        // offering a single format leaves applications that cannot be completed at all.
        var pdf = ApplicationPackFile.VariantBlobPath(
            VariantContainer, 12, 34, "Pablo De Groot", PackFormat.Pdf);

        var docx = ApplicationPackFile.VariantBlobPath(
            VariantContainer, 12, 34, "Pablo De Groot", PackFormat.Docx);

        Assert.Equal("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.docx", docx);
        Assert.Equal(Directory(pdf), Directory(docx));
        Assert.True(ApplicationPackFile.IsStableCvName(docx));
    }

    [Fact]
    public void A_variant_path_reads_back_as_the_blob_it_named()
    {
        // The same segment count as a document path, deliberately, so the one inverse serves both
        // and there is no second convention to keep in step. The container it is read against is
        // the caller's to get right - see the next test for what happens when it is not.
        var path = ApplicationPackFile.VariantBlobPath(
            VariantContainer, 12, 34, "Pablo De Groot", PackFormat.Pdf);

        Assert.True(ApplicationPackFile.TryBlobName(VariantContainer, path, out var blobName));
        Assert.Equal("12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", blobName);
    }

    [Fact]
    public void A_variant_read_against_the_pack_container_yields_a_dead_link_rather_than_a_signature()
    {
        // Pinned because it is the failure a caller wiring the library up will actually make. The
        // prefix is not stripped, so the name resolves inside application-packs and points at
        // nothing - which is the documented behaviour for any foreign path, and is exactly the
        // property worth having: a signature is never produced over a container other than the one
        // the store is bound to.
        var path = ApplicationPackFile.VariantBlobPath(
            VariantContainer, 12, 34, "Pablo De Groot", PackFormat.Pdf);

        Assert.True(ApplicationPackFile.TryBlobName(Container, path, out var blobName));
        Assert.Equal("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", blobName);
    }

    [Fact]
    public void A_nameless_profile_still_sends_one_name_across_the_whole_library()
    {
        // The generic name is the honest answer and the stable one is still stable: an anonymous
        // file is a poor CV, where a file named after a role is a disclosure. The fix is a name on
        // the profile, not a cleverer default.
        var first = ApplicationPackFile.VariantBlobPath(VariantContainer, 12, 34, null, PackFormat.Pdf);
        var second = ApplicationPackFile.VariantBlobPath(VariantContainer, 12, 77, "  ", PackFormat.Pdf);

        Assert.Equal("profile-cvs/12/34/Curriculum_Vitae.pdf", first);
        Assert.Equal(LastSegment(first), LastSegment(second));
        Assert.True(ApplicationPackFile.IsStableCvName(first));
    }

    [Theory]
    [InlineData("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", true)]
    [InlineData("profile-cvs/12/34/Pablo_De_Groot_Curriculum_Vitae.docx", true)]
    [InlineData("Curriculum_Vitae.pdf", true)]
    [InlineData("application-packs/1/2/Pablo_De_Groot_CV.pdf", false)]
    [InlineData("application-packs/1/2/Pablo_De_Groot_Cover_Letter.pdf", false)]
    [InlineData("profile-cvs/12/34/Pablo_De_Groot_AI_Engineer_CV.pdf", false)]
    [InlineData("profile-cvs/12/34/MyCurriculum_Vitae.pdf", false)]
    [InlineData("profile-cvs/12/34/curriculum_vitae.pdf", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void A_stored_reference_says_whether_the_file_it_names_leaks_anything(string? stored, bool stable)
    {
        // The read-only half of the rule, for paths this class did not build: a row written before
        // the rename carries _CV.pdf and a hand-assembled path can carry anything. Only the last
        // segment is read, because that is the only part a client that ignores Content-Disposition
        // ever saves. A covering letter answers false on purpose - the question is whether this is
        // a CV under the stable name, not whether we could have produced the string.
        Assert.Equal(stable, ApplicationPackFile.IsStableCvName(stored));
    }

    [Fact]
    public void A_name_written_in_another_script_survives_intact()
    {
        // The failure this pins is a sanitiser that keeps only ASCII: it would name every file
        // after nobody, on the documents by which those candidates are judged. Blob names are
        // UTF-8 and RFC 6266 carries a UTF-8 filename, so there is nothing to trade away here.
        Assert.Equal(
            "李明_Curriculum_Vitae.pdf",
            ApplicationPackFile.FileName("李明", PackDocument.CurriculumVitae, PackFormat.Pdf));

        Assert.Equal(
            "Renée_Dubois_Curriculum_Vitae.pdf",
            ApplicationPackFile.FileName("Renée Dubois", PackDocument.CurriculumVitae, PackFormat.Pdf));
    }

    [Fact]
    public void A_decomposed_accent_names_the_same_file_as_a_composed_one()
    {
        // "Renée" typed on a Mac arrives decomposed - e followed by a combining acute. The mark is
        // not a letter, so without composing first it would fold to a separator and produce
        // "Rene_e_Dubois_Curriculum_Vitae.pdf", a different file for the same person.
        Assert.Equal(
            ApplicationPackFile.FileName("Renée Dubois", PackDocument.CurriculumVitae, PackFormat.Pdf),
            ApplicationPackFile.FileName("Renée Dubois", PackDocument.CurriculumVitae, PackFormat.Pdf));
    }

    [Theory]
    [InlineData("Siobhan O'Brien")]
    [InlineData("Siobhan O’Brien")]
    public void An_apostrophe_joins_a_name_rather_than_splitting_it(string name)
    {
        // O_Brien reads as two names. The same rule, and the same character set, as the question
        // normaliser - which splits questions in two for the same reason if it gets this wrong.
        Assert.Equal(
            "Siobhan_OBrien_Curriculum_Vitae.pdf",
            ApplicationPackFile.FileName(name, PackDocument.CurriculumVitae, PackFormat.Pdf));
    }

    [Fact]
    public void A_hostile_name_folds_to_one_safe_token()
    {
        var name = ApplicationPackFile.FileName(
            " ../../etc/passwd\"; rm -rf /\t", PackDocument.CurriculumVitae, PackFormat.Pdf);

        // A name is text a person typed, and it becomes a filename on somebody's disk, a segment
        // of a URL and the contents of a quoted header parameter. Only letters, digits, single
        // underscores and the one extension dot may survive that.
        Assert.EndsWith("_Curriculum_Vitae.pdf", name, StringComparison.Ordinal);
        Assert.Equal("etc_passwd_rm_rf_Curriculum_Vitae.pdf", name);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_with_no_name_gets_a_generic_file_and_that_is_the_honest_answer()
    {
        // Deliberately the very filename this exists to avoid. Nothing else on a profile
        // identifies the person without disclosing something, so the fix is a name on the
        // profile - and the generic name is what makes that visible.
        Assert.Equal(
            "Curriculum_Vitae.pdf",
            ApplicationPackFile.FileName(null, PackDocument.CurriculumVitae, PackFormat.Pdf));

        Assert.Equal(
            "Curriculum_Vitae.pdf",
            ApplicationPackFile.FileName("   ", PackDocument.CurriculumVitae, PackFormat.Pdf));

        Assert.Equal(
            "Cover_Letter.docx",
            ApplicationPackFile.FileName("!!!", PackDocument.CoverLetter, PackFormat.Docx));
    }

    [Fact]
    public void A_long_name_cannot_produce_a_path_the_document_row_would_refuse()
    {
        // RecordRenderedAsync throws on an over-long reference rather than truncating it, because
        // a truncated pointer loses the file it points at. A path built here must never be one
        // that repository would reject - and the longer stem spent fourteen more characters of
        // that budget, which is the arithmetic this test does rather than assumes.
        var name = ApplicationPackFile.FileName(
            new string('A', 500), PackDocument.CurriculumVitae, PackFormat.Pdf);

        var path = ApplicationPackFile.BlobPath(Container, long.MaxValue, long.MaxValue, name);

        var variantPath = ApplicationPackFile.VariantBlobPath(
            VariantContainer, long.MaxValue, long.MaxValue, new string('A', 500), PackFormat.Docx);

        Assert.True(name.Length <= ApplicationPackFile.MaxNameChars + "_Curriculum_Vitae.pdf".Length);
        Assert.True(path.Length <= SubmissionLimits.MaxScreenshotRefLength);
        Assert.True(variantPath.Length <= SubmissionLimits.MaxScreenshotRefLength);
    }

    [Fact]
    public void A_stored_path_names_the_container_the_profile_and_the_document()
    {
        // Container-qualified, so the reference means something years later to code that does not
        // know which container was configured when it was written. Profile before document, so a
        // prefix listing per candidate is possible.
        Assert.Equal(
            "application-packs/12/34/Pablo_De_Groot_Curriculum_Vitae.pdf",
            ApplicationPackFile.BlobPath(Container, 12, 34, "Pablo_De_Groot_Curriculum_Vitae.pdf"));
    }

    [Fact]
    public void A_cover_letter_is_still_kept_per_document_because_it_is_still_written_per_posting()
    {
        // The CV moved to a library and the letter did not: it is genuinely per posting, short and
        // cheap, and the advert is in hand when it is written. So the per-document path shape has
        // to keep working beside the variant one rather than being replaced by it.
        var name = ApplicationPackFile.FileName("Pablo De Groot", PackDocument.CoverLetter, PackFormat.Pdf);

        Assert.Equal(
            "application-packs/12/34/Pablo_De_Groot_Cover_Letter.pdf",
            ApplicationPackFile.BlobPath(Container, 12, 34, name));

        Assert.True(ApplicationPackFile.TryBlobName(
            Container, ApplicationPackFile.BlobPath(Container, 12, 34, name), out var blobName));

        Assert.Equal("12/34/Pablo_De_Groot_Cover_Letter.pdf", blobName);
    }

    [Fact]
    public void A_stored_path_reads_back_as_the_blob_it_named()
    {
        var path = ApplicationPackFile.BlobPath(Container, 12, 34, "Pablo_De_Groot_Curriculum_Vitae.pdf");

        Assert.True(ApplicationPackFile.TryBlobName(Container, path, out var blobName));
        Assert.Equal("12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", blobName);
    }

    [Fact]
    public void A_reference_stored_without_the_container_still_resolves()
    {
        // Both spellings name the same blob. Refusing the bare one would turn a resolvable
        // reference - an older row, another tool - into a missing file for no gain.
        Assert.True(ApplicationPackFile.TryBlobName(
            Container, "12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", out var blobName));

        Assert.Equal("12/34/Pablo_De_Groot_Curriculum_Vitae.pdf", blobName);

        Assert.True(ApplicationPackFile.TryBlobName(Container, "/application-packs/12/34/a.pdf", out var leading));
        Assert.Equal("12/34/a.pdf", leading);
    }

    [Fact]
    public void A_reference_naming_another_container_stays_inside_this_one()
    {
        // The property worth pinning: a path from the wrong column cannot produce a signature over
        // anything outside the one container whose contents are allowed to leave the tenant. It is
        // resolved as a blob name that happens to contain a slash, so the worst it yields is a
        // link to a blob that does not exist.
        Assert.True(ApplicationPackFile.TryBlobName(Container, "jobs-landing/private.csv", out var blobName));
        Assert.Equal("jobs-landing/private.csv", blobName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("application-packs/")]
    [InlineData("https://account.blob.core.windows.net/application-packs/12/34/a.pdf")]
    [InlineData("12//34/a.pdf")]
    [InlineData("12/../34/a.pdf")]
    [InlineData("12/./a.pdf")]
    public void A_reference_that_names_no_blob_is_refused(string? stored)
    {
        // Each of these is a bug in whatever wrote the row rather than a missing file, and a
        // stored URL is the mistake this codebase has written down three times not to make. The
        // answer is the same either way: no link, and the pack says no file is available.
        Assert.False(ApplicationPackFile.TryBlobName(Container, stored, out var blobName));
        Assert.Equal(string.Empty, blobName);
    }

    [Fact]
    public void A_reference_longer_than_its_column_is_refused()
    {
        var overlong = "application-packs/1/2/" + new string('a', SubmissionLimits.MaxScreenshotRefLength);

        Assert.False(ApplicationPackFile.TryBlobName(Container, overlong, out _));
    }

    [Fact]
    public void A_download_keeps_the_real_name_and_offers_an_ascii_one()
    {
        var header = ApplicationPackFile.ContentDisposition("Renée_Dubois_Curriculum_Vitae.pdf");

        // Both parameters, always: filename* is what every current browser reads, and the ASCII
        // filename= is for whatever does not - without it such a client falls back to the last
        // path segment of the URL, which carries a SAS query string.
        Assert.Equal(
            "attachment; filename=\"Renee_Dubois_Curriculum_Vitae.pdf\"; "
            + "filename*=UTF-8''Ren%C3%A9e_Dubois_Curriculum_Vitae.pdf",
            header);
    }

    [Fact]
    public void An_ascii_fallback_is_never_empty_even_when_the_name_folds_away()
    {
        // A name in a script with no ASCII equivalent leaves the document kind and the extension,
        // which are always ASCII. An empty filename= would be a header a client cannot use - and
        // what is left is still the stable name rather than anything about the variant.
        var header = ApplicationPackFile.ContentDisposition("李明_Curriculum_Vitae.pdf");

        Assert.Contains("filename=\"Curriculum_Vitae.pdf\"", header, StringComparison.Ordinal);
        Assert.Contains(
            "filename*=UTF-8''%E6%9D%8E%E6%98%8E_Curriculum_Vitae.pdf",
            header,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_download_is_an_attachment_and_carries_no_quote_to_escape()
    {
        var header = ApplicationPackFile.ContentDisposition(
            ApplicationPackFile.FileName("Robert\"); DROP", PackDocument.CurriculumVitae, PackFormat.Pdf));

        Assert.StartsWith("attachment; ", header, StringComparison.Ordinal);

        // Exactly two quotes - the pair around the ASCII parameter. A third would mean a name had
        // closed the quoted string and started writing header syntax of its own.
        Assert.Equal(2, header.Count(c => c == '"'));
    }

    [Fact]
    public void The_two_formats_are_described_differently_to_a_browser()
    {
        // A blob with no content type is served as application/octet-stream, which makes an ATS
        // upload widget reject a perfectly good CV for having the wrong type.
        Assert.Equal("application/pdf", ApplicationPackFile.ContentType(PackFormat.Pdf));
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ApplicationPackFile.ContentType(PackFormat.Docx));
        Assert.Equal("pdf", ApplicationPackFile.Extension(PackFormat.Pdf));
        Assert.Equal("docx", ApplicationPackFile.Extension(PackFormat.Docx));
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Directory(string path) => path[..path.LastIndexOf('/')];
}
