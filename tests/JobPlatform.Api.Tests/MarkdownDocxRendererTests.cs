using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using JobPlatform.Documents;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The markdown-to-DOCX renderer.
/// </summary>
/// <remarks>
/// These assert something the PDF suite cannot: a Word document says what its parts <i>are</i>,
/// so "the heading is a heading and the list is a list" is checkable rather than a claim about
/// pixels. That is also the only reason the format is in the pack - an applicant tracking system
/// parses the file on upload, and a bullet typed into a paragraph is prose to it - so the
/// structural assertions here are the ones not to relax.
///
/// The rest mirrors <see cref="MarkdownPdfRendererTests"/> deliberately, case for case. The
/// markdown comes from a language model, which means it eventually contains everything the prompt
/// asked it not to, and both renderers have to degrade rather than take a download with them.
/// Two hostile cases are this renderer's own, because an XML writer is stricter than MigraDoc
/// about what a character is.
/// </remarks>
public sealed class MarkdownDocxRendererTests
{
    private const string Cv =
        """
        # Ada Lovelace
        ada@example.com · +44 7700 900000 · London · [GitHub](https://github.com/example)

        ## Summary
        Backend engineer with **eight years** building distributed systems in *C#* and Go.

        ## Skills
        - Kubernetes, Docker, Terraform
        - PostgreSQL and `EF Core`

        ## Experience

        ### Senior Engineer, Contoso
        *Mar 2021 - Present*

        - Rebuilt the ingestion pipeline, cutting run time from 40 minutes to 6.
        - Led the migration of 30 services to Kubernetes.
          - Wrote the Helm charts.

        ## Education
        ### BSc Computer Science, University of Somewhere
        *2014 - 2017* - First class
        """;

    private static WordprocessingDocument Read(byte[] bytes)
        => WordprocessingDocument.Open(new MemoryStream(bytes), isEditable: false);

    private static Body BodyOf(WordprocessingDocument document)
        => document.MainDocumentPart!.Document!.Body!;

    /// <summary>Every word a reader would see, which is also every word a parser reads.</summary>
    private static string TextOf(byte[] bytes)
    {
        using var document = Read(bytes);

        return string.Concat(BodyOf(document).Descendants<Text>().Select(text => text.Text));
    }

    private static string PartXml(byte[] bytes, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    [Fact]
    public void A_cv_renders_to_a_valid_docx()
    {
        var bytes = MarkdownDocxRenderer.Render(Cv, "Ada Lovelace - CV");

        // A .docx is a ZIP, and every ZIP starts "PK".
        Assert.StartsWith("PK", Encoding.ASCII.GetString(bytes, 0, 2), StringComparison.Ordinal);

        using var document = Read(bytes);

        Assert.NotNull(document.MainDocumentPart!.StyleDefinitionsPart);
        Assert.NotNull(document.MainDocumentPart.NumberingDefinitionsPart);
        Assert.NotEmpty(BodyOf(document).Elements<Paragraph>());
    }

    [Theory]
    [InlineData(Cv)]
    [InlineData("> quoted\n>\n> - and a list inside")]
    [InlineData("1. one\n2. two\n\n- and a bullet\n  - nested\n\n```\ncode\n```")]
    public void The_package_validates_against_the_schema(string markdown)
    {
        // The closest thing to opening it in Word that a test can do, and worth having because
        // the schema's rules are mostly about *order* - a numbering instance before its abstract
        // definition, a border after the spacing it belongs before - which this SDK will write
        // out happily and Word will refuse to open.
        using var document = Read(MarkdownDocxRenderer.Render(markdown, "CV"));

        var errors = new OpenXmlValidator(FileFormatVersions.Office2013)
            .Validate(document)
            .Select(error => $"{error.Path?.XPath}: {error.Description}")
            .ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void The_content_types_stream_is_the_first_entry()
    {
        // The packaging conventions require it, permissive readers have made it easy to forget,
        // and the canonicalising pass reorders the archive - so this is where that would break.
        using var archive = new ZipArchive(
            new MemoryStream(MarkdownDocxRenderer.Render(Cv, "CV")), ZipArchiveMode.Read);

        Assert.Equal("[Content_Types].xml", archive.Entries[0].FullName);
    }

    [Fact]
    public void The_title_reaches_the_document_metadata()
    {
        // What a file manager shows in a preview, and what a recruiter's system shows when the
        // filename has been stripped off the upload.
        using var document = Read(MarkdownDocxRenderer.Render(Cv, "CV - Platform Engineer"));

        Assert.Equal("CV - Platform Engineer", document.PackageProperties.Title);
    }

    [Fact]
    public void The_metadata_carries_no_timestamp()
    {
        // The one place a clock could still get into the bytes after the archive is restamped.
        // Word writes created and modified dates here; this renderer writes the part by hand so
        // that it cannot.
        var core = PartXml(MarkdownDocxRenderer.Render(Cv, "CV"), "docProps/core.xml");

        Assert.DoesNotContain("created", core, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("modified", core, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Headings_are_real_headings_rather_than_bold_paragraphs()
    {
        // The assertion the whole format is here for. An ATS reads the outline; a paragraph that
        // merely looks like a heading contributes nothing to it.
        using var document = Read(MarkdownDocxRenderer.Render(Cv, "CV"));

        var applied = BodyOf(document)
            .Elements<Paragraph>()
            .Select(paragraph => paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value)
            .Where(style => style is not null)
            .ToList();

        Assert.Contains("Heading1", applied);
        Assert.Contains("Heading2", applied);
        Assert.Contains("Heading3", applied);

        var styles = document.MainDocumentPart!.StyleDefinitionsPart!.Styles!;
        var heading2 = styles.Elements<Style>().Single(style => style.StyleId?.Value == "Heading2");

        Assert.Equal("heading 2", heading2.StyleName?.Val?.Value);
        Assert.Equal(1, heading2.StyleParagraphProperties?.OutlineLevel?.Val?.Value);
    }

    [Fact]
    public void Bullets_are_a_numbering_definition_and_never_typed_into_the_text()
    {
        var bytes = MarkdownDocxRenderer.Render(Cv, "CV");

        using var document = Read(bytes);

        var numbered = BodyOf(document)
            .Elements<Paragraph>()
            .Count(paragraph => paragraph.ParagraphProperties?.NumberingProperties is not null);

        Assert.Equal(5, numbered);

        // The PDF writes the glyph into the paragraph. If it appeared here the list would render
        // identically and parse as five sentences that happen to start with a dot.
        Assert.DoesNotContain("•", TextOf(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_list_carries_its_depth()
    {
        using var document = Read(MarkdownDocxRenderer.Render(Cv, "CV"));

        var levels = BodyOf(document)
            .Elements<Paragraph>()
            .Select(paragraph =>
                paragraph.ParagraphProperties?.NumberingProperties?.NumberingLevelReference?.Val?.Value)
            .Where(level => level is not null)
            .Distinct()
            .ToList();

        Assert.Contains(0, levels);
        Assert.Contains(1, levels);
    }

    [Fact]
    public void Each_ordered_list_restarts_instead_of_continuing_the_one_before_it()
    {
        // Word numbers a list from its instance, so two lists sharing one instance would run
        // 1, 2, 3, 4 down a CV that meant 1, 2 and then 1, 2 again.
        var bytes = MarkdownDocxRenderer.Render(
            "1. first\n2. second\n\nA paragraph.\n\n1. first again\n2. second again", "Lists");

        using var document = Read(bytes);

        var instances = BodyOf(document)
            .Elements<Paragraph>()
            .Select(paragraph => paragraph.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value)
            .Where(id => id is not null)
            .Distinct()
            .ToList();

        Assert.Equal(2, instances.Count);

        // The schema wants every abstract definition ahead of every instance; a package this SDK
        // opens happily can still be refused by Word for getting that order wrong.
        var numbering = document.MainDocumentPart!.NumberingDefinitionsPart!.Numbering!;
        var elements = numbering.ChildElements.Select(element => element.LocalName).ToList();

        Assert.Equal(elements.LastIndexOf("abstractNum") + 1, elements.IndexOf("num"));
    }

    [Fact]
    public void Emphasis_becomes_run_formatting_and_composes()
    {
        using var document = Read(MarkdownDocxRenderer.Render(
            "Plain **bold *and italic* still bold** plain", "Emphasis"));

        var both = BodyOf(document)
            .Descendants<Run>()
            .Single(run => run.RunProperties?.Bold is not null && run.RunProperties?.Italic is not null);

        Assert.Equal("and italic", both.GetFirstChild<Text>()?.Text);
    }

    [Fact]
    public void A_web_link_becomes_a_real_hyperlink()
    {
        var bytes = MarkdownDocxRenderer.Render("[GitHub](https://github.com/example)", "Link");

        using var document = Read(bytes);

        var hyperlink = Assert.Single(BodyOf(document).Descendants<Hyperlink>());
        var relationship = document.MainDocumentPart!.HyperlinkRelationships
            .Single(link => link.Id == hyperlink.Id?.Value);

        Assert.Equal("https://github.com/example", relationship.Uri.ToString());
        Assert.True(relationship.IsExternal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ms-msdt:/id")]
    [InlineData("/relative/path")]
    public void A_target_the_shell_would_take_is_written_as_text_and_not_as_a_link(string url)
    {
        // Word hands a hyperlink's target to the operating system on a click, and the markdown
        // behind it was written by a model that had just read a job advert somebody else wrote.
        var bytes = MarkdownDocxRenderer.Render($"[click here]({url})", "Link");

        using var document = Read(bytes);

        Assert.Empty(BodyOf(document).Descendants<Hyperlink>());
        Assert.Empty(document.MainDocumentPart!.HyperlinkRelationships);
        Assert.Contains("click here", TextOf(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_the_model_returns_is_ever_interpreted_as_markup()
    {
        // The property the whole design rests on. There is no HTML step: a run of text is a w:t
        // whose content the XML writer escapes, so the worst a bad generation can do is read
        // badly.
        var bytes = MarkdownDocxRenderer.Render(
            "Wrote <b>bold</b> and <script>alert(1)</script> inline.", "Escaping");

        var xml = PartXml(bytes, "word/document.xml");

        Assert.DoesNotContain("<b>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", xml, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;", xml, StringComparison.Ordinal);

        // And it survives as the characters the model typed rather than being dropped, which is
        // the half of the rule that a fallback returning Markdig's type name used to break.
        Assert.Equal("Wrote <b>bold</b> and <script>alert(1)</script> inline.", TextOf(bytes));
    }

    [Fact]
    public void The_words_survive_into_the_document()
    {
        var text = TextOf(MarkdownDocxRenderer.Render(Cv, "CV"));

        Assert.Contains("Ada Lovelace", text, StringComparison.Ordinal);
        Assert.Contains("Rebuilt the ingestion pipeline", text, StringComparison.Ordinal);
        Assert.Contains("Wrote the Helm charts.", text, StringComparison.Ordinal);
        Assert.Contains("EF Core", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just a line with no structure at all")]
    [InlineData("| a | b |\n| - | - |\n| 1 | 2 |")]
    [InlineData("<script>alert(1)</script>\n\n<b>not bold</b>")]
    [InlineData("---\n\n***\n\n___")]
    [InlineData("- a\n  - b\n    - c\n      - d\n")]
    [InlineData("> quoted\n>\n> - and a list inside")]
    [InlineData("```\ncode block\n```")]
    [InlineData("Café — naïve — 日本語 — €50,000 — ★")]
    [InlineData("[a link with no target]()")]
    [InlineData("![an image](https://example.com/x.png)")]
    [InlineData("###### deeply nested heading")]
    public void No_input_makes_the_renderer_throw(string markdown)
    {
        // The PDF renderer's thirteen, case for case. Everything here is something the prompt
        // asks the model not to produce, which is exactly why each one has to survive: a prompt
        // is a request, not a guarantee, and the cost of being wrong is a candidate's download
        // failing.
        var bytes = MarkdownDocxRenderer.Render(markdown, "Edge case");

        Assert.NotEmpty(bytes);

        using var document = Read(bytes);

        Assert.NotNull(document.MainDocumentPart!.Document!.Body);
    }

    [Fact]
    public void A_character_xml_cannot_carry_does_not_make_the_renderer_throw()
    {
        // This renderer's own hostile case, and the reason it needs one the PDF does not:
        // MigraDoc draws a control character as nothing, an XmlWriter throws on it, and that
        // throw would arrive as a failed download rather than as a test. Built here rather than
        // written as a literal, because a source file carrying a NUL is a source file no tool
        // will treat as text.
        var markdown = "a control character " + (char)0 + " and a bell " + (char)7 + " in the middle";

        var text = TextOf(MarkdownDocxRenderer.Render(markdown, "Edge case"));

        // A NUL never reaches the renderer: CommonMark replaces it with U+FFFD before the tree
        // is built, which is legal XML and is kept. The bell is the one this file catches - it
        // is what an XmlWriter would have thrown on.
        Assert.Equal("a control character " + (char)0xFFFD + " and a bell  in the middle", text);
    }

    [Fact]
    public void Half_a_character_pair_does_not_make_the_renderer_throw()
    {
        // The other input an XmlWriter refuses. Scraped adverts have been through several
        // encodings by the time a model quotes one back.
        var markdown = "half a pair " + (char)0xD800 + " on its own";

        var text = TextOf(MarkdownDocxRenderer.Render(markdown, "Edge case"));

        Assert.Equal("half a pair  on its own", text);
    }

    [Fact]
    public void A_hostile_title_does_not_reach_the_metadata_as_a_broken_character()
    {
        // The title is built from an advert title, which is text an employer typed.
        var title = "CV - Engineer <&> " + (char)0xD800;

        using var document = Read(MarkdownDocxRenderer.Render("# CV", title));

        Assert.Equal("CV - Engineer <&> ", document.PackageProperties.Title);
    }

    [Fact]
    public void Rendering_is_byte_identical_for_the_same_input()
    {
        // Stronger than the PDF suite's length check, and it has to be: the generated pack is
        // hashed and the hash stored beside it, so "did the document change" must mean "did the
        // words change" rather than "was it rendered twice". A ZIP stamps every entry with the
        // clock unless something stops it.
        var first = MarkdownDocxRenderer.Render(Cv, "CV");
        var second = MarkdownDocxRenderer.Render(Cv, "CV");

        // Part by part before byte for byte, so a regression names the part that moved rather
        // than an offset into a compressed stream.
        using (var left = new ZipArchive(new MemoryStream(first), ZipArchiveMode.Read))
        using (var right = new ZipArchive(new MemoryStream(second), ZipArchiveMode.Read))
        {
            foreach (var entry in left.Entries)
            {
                Assert.Equal(PartXml(first, entry.FullName), PartXml(second, entry.FullName));
            }
        }

        Assert.Equal(first, second);
    }

    [Fact]
    public void Every_entry_is_stamped_with_the_same_fixed_date()
    {
        using var archive = new ZipArchive(
            new MemoryStream(MarkdownDocxRenderer.Render(Cv, "CV")), ZipArchiveMode.Read);

        Assert.All(
            archive.Entries,
            entry => Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), entry.LastWriteTime.DateTime));
    }

    [Fact]
    public void A_long_document_stays_one_section()
    {
        // The PDF's equivalent asserts a page count. There is none to assert here - Word
        // paginates on open, with whatever font it substituted for Roboto - so what is checked
        // instead is that a long document is one flow with one page setup rather than something
        // that grew a section break.
        var builder = new StringBuilder("# Long CV\n\n");

        for (var i = 0; i < 200; i++)
        {
            builder.Append("- A bullet point describing a piece of work, number ").Append(i).AppendLine(".");
        }

        using var document = Read(MarkdownDocxRenderer.Render(builder.ToString(), "Long"));

        Assert.Single(BodyOf(document).Elements<SectionProperties>());
        Assert.Equal(201, BodyOf(document).Elements<Paragraph>().Count());
    }

    [Fact]
    public void Both_renderers_accept_the_same_markdown()
    {
        // One parse, two backends. The point of the shared pipeline is that a construct cannot
        // exist in one document and not the other, so the cheapest guard against them drifting
        // is to run the same input through both.
        Assert.NotEmpty(MarkdownPdfRenderer.Render(Cv, "CV"));
        Assert.NotEmpty(MarkdownDocxRenderer.Render(Cv, "CV"));
    }
}
