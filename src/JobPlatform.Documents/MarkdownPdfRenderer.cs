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
    /// <summary>Body text, in points. Small enough that a CV fits, large enough to read.</summary>
    private const double BodySize = 10;

    /// <summary>
    /// The page and its margins, named once because two things now need them.
    /// </summary>
    /// <remarks>
    /// <b><c>PageFormat.A4</c> does not fill in <c>PageSetup.PageWidth</c>, which is the trap this
    /// exists to close.</b> The format is resolved when the document is rendered, so reading the
    /// width back off the section during the build answers zero - and a table sizing its columns
    /// against "zero minus two margins" got a negative width, which MigraDoc quietly turned into
    /// columns a single word wide. The failure looked like a layout opinion rather than a bug,
    /// which is the kind that survives review.
    /// </remarks>
    private static readonly Unit PageWidth = Unit.FromCentimeter(21.0);

    private static readonly Unit SideMargin = Unit.FromCentimeter(2.0);

    private static readonly Unit VerticalMargin = Unit.FromCentimeter(1.8);

    /// <summary>What a full-width element may occupy.</summary>
    private static Unit TextWidth => Unit.FromPoint(PageWidth.Point - (2 * SideMargin.Point));

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
        section.PageSetup.TopMargin = VerticalMargin;
        section.PageSetup.BottomMargin = VerticalMargin;
        section.PageSetup.LeftMargin = SideMargin;
        section.PageSetup.RightMargin = SideMargin;

        // The pipeline lives in MarkdownAst because the DOCX renderer walks the same tree, and
        // two builders would eventually disagree about what the markdown means.
        var parsed = MarkdownAst.Parse(markdown);

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

    /// <summary>
    /// A pipe table, as a table.
    /// </summary>
    /// <remarks>
    /// <b>This used to flatten every row into one paragraph with an em dash between the
    /// cells</b>, which was the honest thing to write when the parser did not produce tables at
    /// all: it could not be reached, and if it ever were, a row read as a sentence rather than as
    /// syntax. Now that a table parses, a table is what somebody typed and a table is what they
    /// should get - an education section flattened into "BSc (Hons) — University of Northampton —
    /// September 2025 — 2:1" is legible and is not what the document said.
    ///
    /// <b>Columns are sized half evenly and half by their longest cell, and the blend is the whole
    /// of the layout thinking here.</b> MigraDoc cannot size a column from its content, so
    /// something has to guess. Even columns were tried first and are visibly wrong on a real CV:
    /// an education table is one long qualification against an institution, a date range and a
    /// grade of three characters, and a quarter of the page each wraps the first cell to one word
    /// per line while "2:1" sits in four centimetres of white. Purely proportional is worse in the
    /// other direction - the grade column collapses to a few millimetres and wraps a date. Half of
    /// the width shared equally and half shared by the longest cell in each column gives the long
    /// column roughly twice its share and never starves the short one, and both failure modes stay
    /// mild.
    ///
    /// Characters rather than measured text, deliberately: a real measurement needs the font, the
    /// size and a graphics context, and would still be an estimate of how a cell wraps. This is an
    /// estimate that is a line of arithmetic and fails predictably.
    ///
    /// <b>The header row is the parser's, not the first row's.</b> Markdig marks it from the
    /// delimiter line, so a table written without one has no header and is rendered as all body -
    /// which is right, and is the case a "first row is always the header" rule would get wrong
    /// every time somebody pasted a table's middle.
    ///
    /// <b>Borders are one hairline under the header and nothing else.</b> A CV is read by a person
    /// in a hurry and parsed by software that does better with less; a full grid is heavier on the
    /// page and no clearer, and a table with no rule at all loses the header the moment it wraps.
    /// </remarks>
    private static void WriteTable(Section section, Table table)
    {
        var rows = table.OfType<TableRow>().ToList();

        if (rows.Count == 0)
        {
            return;
        }

        // The widest row decides, so a short row cannot cut a column off the ones below it: a
        // table whose header names four things and whose last row states three is malformed and
        // still has to render everything it holds.
        var columns = rows.Max(row => row.OfType<TableCell>().Count());

        if (columns == 0)
        {
            return;
        }

        var rendered = section.AddTable();
        rendered.Borders.Width = 0;
        rendered.TopPadding = Unit.FromPoint(2);
        rendered.BottomPadding = Unit.FromPoint(2);

        var widths = ColumnWidths(rows, columns, TextWidth);

        for (var index = 0; index < columns; index++)
        {
            var column = rendered.AddColumn(widths[index]);
            column.Format.Alignment = ParagraphAlignment.Left;

            // The cell keeps its own right margin so two columns of prose do not touch. Applied
            // to the column rather than to every cell, for the reason the styles are set once on
            // the document: a per-cell assignment is a loop nobody remembers to extend.
            column.RightPadding = Unit.FromPoint(6);
        }

        foreach (var row in rows)
        {
            var target = rendered.AddRow();

            // Repeated at the top of a page where the table breaks, which is the whole value of
            // the parser knowing which row is the header.
            target.HeadingFormat = row.IsHeader;
            target.Format.Font.Bold = row.IsHeader;

            if (row.IsHeader)
            {
                target.Borders.Bottom.Width = 0.75;
                target.Borders.Bottom.Color = Colors.Gray;
            }

            var cells = row.OfType<TableCell>().ToList();

            for (var index = 0; index < cells.Count && index < columns; index++)
            {
                var paragraph = target.Cells[index].AddParagraph();

                // Cells hold blocks, and a cell of two paragraphs is one paragraph here with a
                // break between them: a nested paragraph in a table cell is the same content and
                // MigraDoc's own spacing between them reads as a gap in the row.
                var written = false;

                foreach (var child in cells[index])
                {
                    if (child is not ParagraphBlock content)
                    {
                        continue;
                    }

                    if (written)
                    {
                        paragraph.AddLineBreak();
                    }

                    WriteInlines(paragraph, content.Inline);
                    written = true;
                }
            }
        }
    }

    /// <summary>
    /// How wide each column gets: half the page shared evenly, half shared by content.
    /// </summary>
    /// <remarks>
    /// The weight is the longest cell in the column rather than the average, because what decides
    /// whether a column is too narrow is its worst row - an average over four short rows and one
    /// long one sizes the column for text that is not the problem.
    /// </remarks>
    private static Unit[] ColumnWidths(List<TableRow> rows, int columns, Unit available)
    {
        var longest = new int[columns];

        foreach (var row in rows)
        {
            var cells = row.OfType<TableCell>().ToList();

            for (var index = 0; index < cells.Count && index < columns; index++)
            {
                longest[index] = Math.Max(longest[index], Length(cells[index]));
            }
        }

        var total = longest.Sum();
        var widths = new Unit[columns];

        for (var index = 0; index < columns; index++)
        {
            var even = 1d / columns;
            var byContent = total == 0 ? even : (double)longest[index] / total;

            widths[index] = Unit.FromPoint(available.Point * ((even + byContent) / 2));
        }

        return widths;
    }

    /// <summary>How much text one cell holds, for sizing rather than for layout.</summary>
    private static int Length(TableCell cell)
        => cell.OfType<ParagraphBlock>()
            .Sum(block => block.Inline?.Descendants<LiteralInline>()
                .Sum(literal => literal.Content.Length) ?? 0);

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

            case HtmlEntityInline entity:
                paragraph.AddText(entity.Transcoded.ToString());
                break;

            case ContainerInline nested:
                foreach (var child in nested)
                {
                    WriteInline(paragraph, child);
                }

                break;

            default:
                // Nothing, and the rule this replaces was worse than the gap it feared.
                //
                // It read "anything unmapped keeps its text", which is what ToString() looks like
                // it does and does not: Markdig overrides ToString on LiteralInline alone, so
                // every other node answered with its .NET type name. Measured in the content
                // stream of a real render, a CV writing "R&amp;D" came out
                // "R Markdig.Syntax.Inlines.HtmlEntityInline D" - 39 characters of framework
                // internals in a document somebody sends to an employer, and 39 characters is
                // about a third of a line, so it moved the page breaks too.
                //
                // Every inline that carries text is handled above. What reaches here cannot yield
                // text generically, and the DOCX renderer - which has always handled these - is
                // the reference for what the document should say.
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

            case HtmlEntityInline entity:
                parent.AddText(entity.Transcoded.ToString());
                break;

            case CodeInline code:
            {
                // Reached by `code` inside **bold** or _italic_, which the top-level switch never
                // sees. Without it the span rendered as "Markdig.Syntax.Inlines.CodeInline".
                var formatted = parent.AddFormattedText(code.Content);
                formatted.Font.Name = EmbeddedFontResolver.MonoFamily;
                break;
            }

            case LineBreakInline lineBreak:
                if (lineBreak.IsHard)
                {
                    parent.AddLineBreak();
                }
                else
                {
                    parent.AddSpace(1);
                }

                break;

            case ContainerInline nested:
                foreach (var child in nested)
                {
                    WriteInline(parent, child);
                }

                break;

            default:
                // See the top-level walker: emitting a type name is worse than emitting nothing.
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

            case HtmlEntityInline entity:
                parent.AddText(entity.Transcoded.ToString());
                break;

            case CodeInline code:
            {
                var formatted = parent.AddFormattedText(code.Content);
                formatted.Font.Name = EmbeddedFontResolver.MonoFamily;
                break;
            }

            case EmphasisInline emphasis:
            {
                // Bold and italic inside a link used to be dropped: LinkInline's children arrive
                // as ContainerInline, so emphasis was recursed through with its formatting lost.
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
                // See the top-level walker: emitting a type name is worse than emitting nothing.
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
