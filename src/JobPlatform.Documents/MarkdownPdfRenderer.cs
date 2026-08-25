using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace JobPlatform.Documents;

/// <summary>
/// Turns generated markdown into a PDF.
/// </summary>
/// <remarks>
/// <b>The model writes words; this file decides how they look.</b> Markdown is parsed into an
/// abstract syntax tree and each node type is mapped onto a MigraDoc element from a fixed set -
/// there is no path by which model output is interpreted as markup, no HTML step, and no
/// styling the model can influence. That is the property that makes generating a document from
/// a language model safe to hand to a candidate: the worst a bad response can do is read badly.
///
/// A node type with no mapping is rendered as its plain text rather than dropped, so a
/// construct nobody anticipated degrades to a paragraph instead of vanishing from someone's CV.
///
/// PDFsharp rather than a browser engine: the API runs in a Linux container, and an HTML-to-PDF
/// path would mean shipping Chromium in it. This has no native dependency at all, which is also
/// why the platform-independent build is referenced rather than the GDI one - and why every
/// font it draws with has to be supplied by <see cref="EmbeddedFontResolver"/>.
/// </remarks>
public static class MarkdownPdfRenderer
{
    /// <summary>
    /// The pipeline the markdown is parsed with.
    /// </summary>
    /// <remarks>
    /// Deliberately close to plain CommonMark. Every extension enabled is a construct the
    /// renderer below then has to handle, and a CV needs headings, emphasis, lists and links -
    /// not footnotes, task lists or custom containers. The prompt asks for the same subset, so
    /// this is the second half of one agreement rather than an independent guess.
    /// </remarks>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .Build();

    /// <summary>Body text, in points. Small enough that a CV fits, large enough to read.</summary>
    private const double BodySize = 10;

    public static byte[] Render(string markdown, string title)
    {
        // Idempotent, and called here rather than left to a caller: the resolver is
        // process-wide static state in PDFsharp, and forgetting it fails at render time with
        // an exception about an internal error font that names nothing in this file.
        EmbeddedFontResolver.Install();

        var document = new Document();
        document.Info.Title = title;

        Style(document);

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.0);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2.0);

        var parsed = Markdown.Parse(markdown ?? string.Empty, Pipeline);

        foreach (var block in parsed)
        {
            WriteBlock(section, block, listDepth: 0);
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);

        return stream.ToArray();
    }

    /// <summary>
    /// The whole visual identity of a generated document, in one place.
    /// </summary>
    /// <remarks>
    /// The font names here are the ones <see cref="EmbeddedFontResolver"/> bundles. Nothing is
    /// resolved from the machine: the container has essentially no fonts installed and
    /// PDFsharp's platform-independent build would not read them if it had, so a name not in
    /// that resolver's table falls back to the sans face rather than rendering differently
    /// between a developer's laptop and production.
    /// </remarks>
    private static void Style(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.SansFamily;
        normal.Font.Size = BodySize;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.15;

        var h1 = document.Styles[StyleNames.Heading1]!;
        h1.Font.Size = 19;
        h1.Font.Bold = true;
        h1.ParagraphFormat.SpaceBefore = 0;
        h1.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var h2 = document.Styles[StyleNames.Heading2]!;
        h2.Font.Size = 12;
        h2.Font.Bold = true;
        h2.Font.Color = Colors.Black;
        h2.ParagraphFormat.SpaceBefore = Unit.FromPoint(12);
        h2.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);

        // A rule under each section heading. The one piece of decoration in the whole
        // document, and it is here because a CV with no visual separation between sections
        // reads as a wall of text at a glance - which is the only way most of them are read.
        h2.ParagraphFormat.Borders.Bottom.Width = 0.6;
        h2.ParagraphFormat.Borders.Bottom.Color = Colors.Gray;
        h2.ParagraphFormat.Borders.Distance = Unit.FromPoint(2);

        var h3 = document.Styles[StyleNames.Heading3]!;
        h3.Font.Size = BodySize + 0.5;
        h3.Font.Bold = true;
        h3.ParagraphFormat.SpaceBefore = Unit.FromPoint(8);
        h3.ParagraphFormat.SpaceAfter = 0;

        var h4 = document.Styles.AddStyle("Heading4Local", StyleNames.Heading3);
        h4.Font.Size = BodySize;

        var list = document.Styles[StyleNames.List]!;
        list.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var hyperlink = document.Styles[StyleNames.Hyperlink]!;
        hyperlink.Font.Color = Colors.Black;
        hyperlink.Font.Underline = Underline.Single;
    }

    private static void WriteBlock(Section section, Block block, int listDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                WriteHeading(section, heading);
                break;

            case ParagraphBlock paragraph:
                WriteInlines(section.AddParagraph(), paragraph.Inline);
                break;

            case ListBlock list:
                WriteList(section, list, listDepth);
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    var before = section.Elements.Count;
                    WriteBlock(section, child, listDepth);
                    Indent(section, before, Unit.FromCentimeter(0.6));
                }

                break;

            case CodeBlock code:
                WriteCode(section, code);
                break;

            case ThematicBreakBlock:
                // Asked against in the prompt, and harmless when it turns up anyway. Rendered
                // as space rather than a rule, which is what it means in a document like this.
                section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(8);
                break;

            case Table table:
                // Also asked against. Flattened row by row rather than dropped: losing a table
                // silently would remove content from someone's CV with nothing to show for it.
                WriteTable(section, table);
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    WriteBlock(section, child, listDepth);
                }

                break;
        }
    }

    private static void WriteHeading(Section section, HeadingBlock heading)
    {
        var paragraph = section.AddParagraph();

        paragraph.Style = heading.Level switch
        {
            1 => StyleNames.Heading1,
            2 => StyleNames.Heading2,
            3 => StyleNames.Heading3,
            _ => "Heading4Local",
        };

        WriteInlines(paragraph, heading.Inline);
    }

    private static void WriteList(Section section, ListBlock list, int listDepth)
    {
        var number = 1;

        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var marker = list.IsOrdered
                ? $"{number++.ToString(System.Globalization.CultureInfo.InvariantCulture)}."
                : "•";

            var first = true;

            foreach (var child in listItem)
            {
                if (child is ParagraphBlock paragraph)
                {
                    var target = section.AddParagraph();
                    target.Style = StyleNames.List;
                    target.Format.LeftIndent = Unit.FromCentimeter(0.5 + (0.5 * listDepth));
                    target.Format.FirstLineIndent = Unit.FromCentimeter(-0.35);

                    // Only the first paragraph of an item carries the bullet. A continuation
                    // paragraph indented to the same place with no marker is what a wrapped
                    // list item is supposed to look like.
                    if (first)
                    {
                        target.AddText(marker + "  ");
                    }

                    WriteInlines(target, paragraph.Inline);
                    first = false;
                }
                else
                {
                    WriteBlock(section, child, listDepth + 1);
                }
            }
        }
    }

    private static void WriteCode(Section section, CodeBlock code)
    {
        var paragraph = section.AddParagraph();
        paragraph.Format.Font.Name = EmbeddedFontResolver.MonoFamily;
        paragraph.Format.Font.Size = BodySize - 1;
        paragraph.Format.LeftIndent = Unit.FromCentimeter(0.5);

        foreach (var line in code.Lines.Lines)
        {
            if (line.Slice.Text is null)
            {
                continue;
            }

            paragraph.AddText(line.ToString());
            paragraph.AddLineBreak();
        }
    }

    private static void WriteTable(Section section, Table table)
    {
        foreach (var row in table)
        {
            if (row is not TableRow tableRow)
            {
                continue;
            }

            var paragraph = section.AddParagraph();
            var first = true;

            foreach (var cell in tableRow)
            {
                if (cell is not TableCell tableCell)
                {
                    continue;
                }

                if (!first)
                {
                    paragraph.AddText("  —  ");
                }

                foreach (var child in tableCell)
                {
                    if (child is ParagraphBlock content)
                    {
                        WriteInlines(paragraph, content.Inline);
                    }
                }

                first = false;
            }
        }
    }

    /// <summary>
    /// Walks the inline tree, carrying emphasis down as formatting rather than as markup.
    /// </summary>
    /// <remarks>
    /// A <see cref="FormattedText"/> per emphasis span rather than a mutable "current style"
    /// flag, so nesting composes: bold inside a link inside a list item is three wrappers and
    /// each one only has to know its own job.
    /// </remarks>
    private static void WriteInlines(Paragraph paragraph, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            WriteInline(paragraph, inline);
        }
    }

    private static void WriteInline(Paragraph paragraph, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                paragraph.AddText(literal.ToString());
                break;

            case EmphasisInline emphasis:
            {
                var formatted = paragraph.AddFormattedText();

                if (emphasis.DelimiterCount >= 2)
                {
                    formatted.Bold = true;
                }
                else
                {
                    formatted.Italic = true;
                }

                foreach (var child in emphasis)
                {
                    WriteInline(formatted, child);
                }

                break;
            }

            case CodeInline code:
            {
                var formatted = paragraph.AddFormattedText(code.Content);
                formatted.Font.Name = EmbeddedFontResolver.MonoFamily;
                break;
            }

            case LinkInline link:
            {
                // Images are dropped to their alt text. A generated CV has no business
                // carrying one, and a broken image box is worse than the words.
                if (link.IsImage)
                {
                    WriteInlines(paragraph, link);
                    break;
                }

                var target = link.Url;

                if (string.IsNullOrWhiteSpace(target))
                {
                    WriteInlines(paragraph, link);
                    break;
                }

                var hyperlink = paragraph.AddHyperlink(target, HyperlinkType.Web);

                foreach (var child in link)
                {
                    WriteInline(hyperlink, child);
                }

                break;
            }

            case LineBreakInline lineBreak:
                if (lineBreak.IsHard)
                {
                    paragraph.AddLineBreak();
                }
                else
                {
                    paragraph.AddSpace(1);
                }

                break;

            case ContainerInline nested:
                foreach (var child in nested)
                {
                    WriteInline(paragraph, child);
                }

                break;

            default:
                // Anything unmapped keeps its text. Silently dropping a node would take
                // content out of a document somebody is about to send to an employer.
                paragraph.AddText(inline.ToString() ?? string.Empty);
                break;
        }
    }

    private static void WriteInline(FormattedText parent, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                parent.AddText(literal.ToString());
                break;

            case EmphasisInline emphasis:
            {
                var formatted = parent.AddFormattedText();

                if (emphasis.DelimiterCount >= 2)
                {
                    formatted.Bold = true;
                }
                else
                {
                    formatted.Italic = true;
                }

                foreach (var child in emphasis)
                {
                    WriteInline(formatted, child);
                }

                break;
            }

            case ContainerInline nested:
                foreach (var child in nested)
                {
                    WriteInline(parent, child);
                }

                break;

            default:
                parent.AddText(inline.ToString() ?? string.Empty);
                break;
        }
    }

    private static void WriteInline(Hyperlink parent, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                parent.AddText(literal.ToString());
                break;

            case ContainerInline nested:
                foreach (var child in nested)
                {
                    WriteInline(parent, child);
                }

                break;

            default:
                parent.AddText(inline.ToString() ?? string.Empty);
                break;
        }
    }

    /// <summary>Indents everything added to the section since <paramref name="from"/>.</summary>
    private static void Indent(Section section, int from, Unit amount)
    {
        for (var i = from; i < section.Elements.Count; i++)
        {
            if (section.Elements[i] is Paragraph paragraph)
            {
                paragraph.Format.LeftIndent = amount;
            }
        }
    }
}
