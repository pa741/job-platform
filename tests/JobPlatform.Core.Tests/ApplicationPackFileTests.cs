using JobPlatform.Core.Applications;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// What a rendered document is called, and whether the path it is stored at can be read back.
/// </summary>
/// <remarks>
/// Two rules that look cosmetic and are not. The filename appears in a recruiter's file list, so
/// <c>cv.pdf</c> is a product defect rather than a naming preference; and the path is written into
/// a SQL column and read back by different code to mint a link, so the two directions have to be
/// inverses or a stored file becomes unreachable with nothing failing.
/// </remarks>
public sealed class ApplicationPackFileTests
{
    private const string Container = "application-packs";

    [Fact]
    public void A_document_is_named_after_the_person_it_belongs_to()
    {
        Assert.Equal(
            "Pablo_De_Groot_CV.pdf",
            ApplicationPackFile.FileName("Pablo De Groot", PackDocument.CurriculumVitae, PackFormat.Pdf));

        Assert.Equal(
            "Pablo_De_Groot_Cover_Letter.pdf",
            ApplicationPackFile.FileName("Pablo De Groot", PackDocument.CoverLetter, PackFormat.Pdf));

        Assert.Equal(
            "Pablo_De_Groot_CV.docx",
            ApplicationPackFile.FileName("Pablo De Groot", PackDocument.CurriculumVitae, PackFormat.Docx));
    }

    [Fact]
    public void A_name_written_in_another_script_survives_intact()
    {
        // The failure this pins is a sanitiser that keeps only ASCII: it would name every file
        // after nobody, on the documents by which those candidates are judged. Blob names are
        // UTF-8 and RFC 6266 carries a UTF-8 filename, so there is nothing to trade away here.
        Assert.Equal(
            "李明_CV.pdf",
            ApplicationPackFile.FileName("李明", PackDocument.CurriculumVitae, PackFormat.Pdf));

        Assert.Equal(
            "Renée_Dubois_CV.pdf",
            ApplicationPackFile.FileName("Renée Dubois", PackDocument.CurriculumVitae, PackFormat.Pdf));
    }

    [Fact]
    public void A_decomposed_accent_names_the_same_file_as_a_composed_one()
    {
        // "Renée" typed on a Mac arrives decomposed - e followed by a combining acute. The mark is
        // not a letter, so without composing first it would fold to a separator and produce
        // "Rene_e_Dubois_CV.pdf", a different file for the same person.
        Assert.Equal(
            ApplicationPackFile.FileName("Renée Dubois", PackDocument.CurriculumVitae, PackFormat.Pdf),
            ApplicationPackFile.FileName("Renée Dubois", PackDocument.CurriculumVitae, PackFormat.Pdf));
    }

    [Theory]
    [InlineData("Siobhan O'Brien")]
    [InlineData("Siobhan O’Brien")]
    public void An_apostrophe_joins_a_name_rather_than_splitting_it(string name)
    {
        // O_Brien reads as two names. The same rule, and the same character set, as the question
        // normaliser - which splits questions in two for the same reason if it gets this wrong.
        Assert.Equal(
            "Siobhan_OBrien_CV.pdf",
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
        Assert.EndsWith("_CV.pdf", name, StringComparison.Ordinal);
        Assert.Equal("etc_passwd_rm_rf_CV.pdf", name);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_with_no_name_gets_a_generic_file_and_that_is_the_honest_answer()
    {
        // Deliberately the very filename this exists to avoid. Nothing else on a profile
        // identifies the person without disclosing something, so the fix is a name on the
        // profile - and the generic name is what makes that visible.
        Assert.Equal("CV.pdf", ApplicationPackFile.FileName(null, PackDocument.CurriculumVitae, PackFormat.Pdf));
        Assert.Equal("CV.pdf", ApplicationPackFile.FileName("   ", PackDocument.CurriculumVitae, PackFormat.Pdf));
        Assert.Equal(
            "Cover_Letter.docx",
            ApplicationPackFile.FileName("!!!", PackDocument.CoverLetter, PackFormat.Docx));
    }

    [Fact]
    public void A_long_name_cannot_produce_a_path_the_document_row_would_refuse()
    {
        // RecordRenderedAsync throws on an over-long reference rather than truncating it, because
        // a truncated pointer loses the file it points at. A path built here must never be one
        // that repository would reject.
        var name = ApplicationPackFile.FileName(
            new string('A', 500), PackDocument.CurriculumVitae, PackFormat.Pdf);

        var path = ApplicationPackFile.BlobPath(Container, long.MaxValue, long.MaxValue, name);

        Assert.True(name.Length <= ApplicationPackFile.MaxNameChars + "_CV.pdf".Length);
        Assert.True(path.Length <= SubmissionLimits.MaxScreenshotRefLength);
    }

    [Fact]
    public void A_stored_path_names_the_container_the_profile_and_the_document()
    {
        // Container-qualified, so the reference means something years later to code that does not
        // know which container was configured when it was written. Profile before document, so a
        // prefix listing per candidate is possible.
        Assert.Equal(
            "application-packs/12/34/Pablo_De_Groot_CV.pdf",
            ApplicationPackFile.BlobPath(Container, 12, 34, "Pablo_De_Groot_CV.pdf"));
    }

    [Fact]
    public void A_stored_path_reads_back_as_the_blob_it_named()
    {
        var path = ApplicationPackFile.BlobPath(Container, 12, 34, "Pablo_De_Groot_CV.pdf");

        Assert.True(ApplicationPackFile.TryBlobName(Container, path, out var blobName));
        Assert.Equal("12/34/Pablo_De_Groot_CV.pdf", blobName);
    }

    [Fact]
    public void A_reference_stored_without_the_container_still_resolves()
    {
        // Both spellings name the same blob. Refusing the bare one would turn a resolvable
        // reference - an older row, another tool - into a missing file for no gain.
        Assert.True(ApplicationPackFile.TryBlobName(Container, "12/34/Pablo_De_Groot_CV.pdf", out var blobName));
        Assert.Equal("12/34/Pablo_De_Groot_CV.pdf", blobName);

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
        var header = ApplicationPackFile.ContentDisposition("Renée_Dubois_CV.pdf");

        // Both parameters, always: filename* is what every current browser reads, and the ASCII
        // filename= is for whatever does not - without it such a client falls back to the last
        // path segment of the URL, which carries a SAS query string.
        Assert.Equal(
            "attachment; filename=\"Renee_Dubois_CV.pdf\"; filename*=UTF-8''Ren%C3%A9e_Dubois_CV.pdf",
            header);
    }

    [Fact]
    public void An_ascii_fallback_is_never_empty_even_when_the_name_folds_away()
    {
        // A name in a script with no ASCII equivalent leaves the document kind and the extension,
        // which are always ASCII. An empty filename= would be a header a client cannot use.
        var header = ApplicationPackFile.ContentDisposition("李明_CV.pdf");

        Assert.Contains("filename=\"CV.pdf\"", header, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''%E6%9D%8E%E6%98%8E_CV.pdf", header, StringComparison.Ordinal);
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
}
