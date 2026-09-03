using System.Globalization;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace JobPlatform.Documents;

/// <summary>
/// Turns generated markdown into a Word document.
/// </summary>
/// <remarks>
/// <b>The same document as the PDF, for a reader that is a program.</b> Workday, iCIMS, Taleo and
/// most of the rest run a parser over an uploaded file and pre-fill their form from it; they do
/// that far more reliably from DOCX than from PDF, because a PDF is a description of where ink
/// goes and a DOCX still says what a heading is. So both are emitted, from one parse, and a
/// recruiter may open either - which is why this file is a second backend behind
/// <see cref="MarkdownAst"/> rather than a second template, and why every typographic decision
/// below is a deliberate copy of <see cref="MarkdownPdfRenderer"/>'s.
///
/// <b>The property that made the PDF renderer safe is the one that matters most here.</b>
/// Markdown is parsed into a syntax tree and each node type is mapped onto an OOXML element from
/// a fixed set. There is no HTML step, no string template, no path by which model output is
/// interpreted as markup: a run of text is a <c>w:t</c> whose content the XML writer escapes,
/// so <c>&lt;script&gt;</c> in a generated CV arrives in Word as those eleven characters and
/// nothing else. The worst a bad generation can do is read badly.
///
/// <b>Structure is the deliverable, not decoration.</b> Headings are real heading styles carrying
/// <c>w:outlineLvl</c>, and lists are real numbering definitions with <c>w:numPr</c> - not
/// paragraphs with a bullet character typed in front. An ATS that sees a literal bullet at the
/// start of a body paragraph files the line as prose; the same line as a list item becomes a
/// skill. This is the whole reason the format is in the pack, so it is the one thing not to
/// compromise for the sake of looking identical.
///
/// <b>Where it cannot match the PDF, and why.</b>
/// <list type="bullet">
/// <item><description><i>Fonts are named, not embedded.</i> The PDF carries Roboto inside it and
/// renders identically on any machine. Word's font embedding is an obfuscated copy of the face
/// that only Word honours - LibreOffice, Google Docs and every ATS ignore it - and it would
/// roughly double a file whose entire purpose is being parsed. So the DOCX asks for Roboto and
/// accepts the reader's substitute, which is also what a recruiter's own template would do.
/// </description></item>
/// <item><description><i>Pagination is the reader's.</i> Word reflows with whatever font it
/// substituted, so the PDF's "a real CV fits on one page" cannot be asserted here. Margins, type
/// sizes and spacing are the same, so it normally still does. Headings carry <c>keepNext</c>,
/// which the fixed-layout PDF has no need of, so a reflow cannot strand one at the foot of a
/// page.</description></item>
/// <item><description><i>The rule under each section heading is 5/8 pt, not 0.6 pt.</i> Word
/// quantises borders to eighths of a point; 0.625 is the nearest it can express.</description>
/// </item>
/// <item><description><i>A list marker is a numbering definition, not text.</i> The PDF writes
/// the bullet and two spaces into the paragraph and hangs the first line; here the marker comes
/// from <c>numbering.xml</c>, an ordered list restarts because each one gets its own instance,
/// and a wrapped item's continuation paragraph aligns with the text instead of inheriting the
/// hanging indent. Same shape on the page, different mechanism, and only the second one parses.
/// </description></item>
/// <item><description><i>Link targets are checked.</i> The PDF hands any string to MigraDoc; a
/// hyperlink in a DOCX is something Word will pass to the shell when clicked, so only
/// <c>http</c>, <c>https</c> and <c>mailto</c> become links here and anything else degrades to
/// its text. The model is not the threat; a job advert quoted into a prompt is.</description>
/// </item>
/// </list>
///
/// <b>DocumentFormat.OpenXml rather than the alternatives.</b> MigraDoc cannot write DOCX, so the
/// second backend needs a second library, and this is the only one that clears the same bar
/// PDFsharp did: MIT, first-party, no native dependency and no Office install to shell out to -
/// the API runs in a Linux container. Its object model is a generated mirror of the OOXML schema,
/// so the node map below is checked by the compiler rather than by eye, which is precisely what a
/// hand-rolled ZIP-and-XML writer would give up at the point where the ATS-facing structure
/// lives. NPOI's Word support is a thinner port and brings the whole POI surface for one document
/// type; the commercial wrappers are out on the repository being public.
///
/// <b>Output is byte-identical for identical input.</b> That does not come free: a package is a
/// ZIP, and both the ZIP and Word's own metadata are full of clocks and generated identifiers.
/// The countermeasures are all here - relationship ids are assigned rather than generated, no
/// <c>w:nsid</c> or <c>w:rsid</c> is written, the core properties are written by hand so that no
/// created or modified date exists, and <see cref="DeterministicOpenXmlPackage"/> restamps the
/// archive. A stored hash of the document has to mean "the words changed", not "it was rendered
/// again".
/// </remarks>
public static class MarkdownDocxRenderer
{
    /// <summary>Body text, in points. The same figure the PDF uses; keep them together.</summary>
    private const double BodySize = 10;

    /// <summary>The families named in the styles. Roboto to match the PDF, if the reader has it.</summary>
    private const string SansFamily = "Roboto";
    private const string MonoFamily = "Roboto Mono";

    private const string NormalStyle = "Normal";
    private const string Heading1Style = "Heading1";
    private const string Heading2Style = "Heading2";
    private const string Heading3Style = "Heading3";
    private const string Heading4Style = "Heading4";
    private const string ListStyle = "ListParagraph";
    private const string CodeStyle = "CodeBlock";
    private const string HyperlinkStyle = "Hyperlink";

    /// <summary>Word defines nine list levels; deeper nesting is clamped onto the last.</summary>
    private const int MaxListLevel = 8;

    private const string CorePropertiesNamespace =
        "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";

    private const string DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";

    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    public static byte[] Render(string markdown, string title)
    {
        using var buffer = new MemoryStream();

        using (var package = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            // Every relationship id is pinned immediately after the part is created, and that is
            // not tidiness. The SDK mints ids like "R82299e4565ad4bfa" from a random source, so
            // two renders of one CV differ in .rels before a single word has been written -
            // which is the first thing the determinism test catches when this is removed.
            var main = package.AddMainDocumentPart();
            package.ChangeIdOfPart(main, "rId1");

            var styles = main.AddNewPart<StyleDefinitionsPart>();
            main.ChangeIdOfPart(styles, "rId1");

            var numbering = main.AddNewPart<NumberingDefinitionsPart>();
            main.ChangeIdOfPart(numbering, "rId2");

            var body = new Body();
            var writer = new Writer(main);

            foreach (var block in MarkdownAst.Parse(markdown))
            {
                writer.WriteBlock(body, block, listDepth: 0);
            }

            body.AppendChild(PageSetup());

            main.Document = new Document(body);
            styles.Styles = BuildStyles();

            // After the walk, not before: every ordered list in the document contributes a
            // numbering instance of its own, which is what makes the second list start at 1
            // again instead of continuing the first.
            numbering.Numbering = writer.BuildNumbering();

            WriteCoreProperties(package, title);
        }

        return DeterministicOpenXmlPackage.Canonicalise(buffer.ToArray());
    }

    /// <summary>
    /// The whole visual identity of a generated document, in one place.
    /// </summary>
    /// <remarks>
    /// Every number here is the same number as in <c>MarkdownPdfRenderer.Style</c>, converted to
    /// the units Word measures in: twips for lengths, half-points for type sizes, 240ths for line
    /// spacing. They are written as conversions from the original points and centimetres rather
    /// than as the converted constants, so the two files can be read side by side and a change to
    /// one is visibly a change to the other.
    ///
    /// The style ids are Word's own - "Heading1" carrying the name "heading 1" - because that
    /// pairing is what makes a paragraph a built-in heading to Word, to Google Docs, and to the
    /// parsers that matter. A privately named style that merely looked like a heading would
    /// render the same and parse as nothing.
    /// </remarks>
    private static Styles BuildStyles()
    {
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    Fonts(SansFamily),
                    new FontSize { Val = Half(BodySize) },
                    new FontSizeComplexScript { Val = Half(BodySize) })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines
                    {
                        After = Pt(5),
                        Line = "276",
                        LineRule = LineSpacingRuleValues.Auto,
                    }))));

        styles.Append(new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = NormalStyle,
            Default = true,
            StyleName = new StyleName { Val = "Normal" },
            PrimaryStyle = new PrimaryStyle(),
        });

        styles.Append(new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = Heading1Style,
            StyleName = new StyleName { Val = "heading 1" },
            BasedOn = new BasedOn { Val = NormalStyle },
            NextParagraphStyle = new NextParagraphStyle { Val = NormalStyle },
            PrimaryStyle = new PrimaryStyle(),
            StyleParagraphProperties = new StyleParagraphProperties
            {
                KeepNext = new KeepNext(),
                KeepLines = new KeepLines(),
                SpacingBetweenLines = new SpacingBetweenLines { Before = "0", After = Pt(2) },
                OutlineLevel = new OutlineLevel { Val = 0 },
            },
            StyleRunProperties = new StyleRunProperties
            {
                RunFonts = Fonts(SansFamily),
                Bold = new Bold(),
                FontSize = new FontSize { Val = Half(19) },
            },
        });

        styles.Append(new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = Heading2Style,
            StyleName = new StyleName { Val = "heading 2" },
            BasedOn = new BasedOn { Val = NormalStyle },
            NextParagraphStyle = new NextParagraphStyle { Val = NormalStyle },
            PrimaryStyle = new PrimaryStyle(),
            StyleParagraphProperties = new StyleParagraphProperties
            {
                KeepNext = new KeepNext(),
                KeepLines = new KeepLines(),

                // A rule under each section heading. The one piece of decoration in the whole
                // document, and it is here because a CV with no visual separation between
                // sections reads as a wall of text at a glance - which is the only way most of
                // them are read. Word's border width is in eighths of a point, so the PDF's
                // 0.6 pt becomes 5/8; nobody will see the fortieth of a point.
                ParagraphBorders = new ParagraphBorders(new BottomBorder
                {
                    Val = BorderValues.Single,
                    Color = "808080",
                    Size = 5U,
                    Space = 2U,
                }),
                SpacingBetweenLines = new SpacingBetweenLines { Before = Pt(12), After = Pt(3) },
                OutlineLevel = new OutlineLevel { Val = 1 },
            },
            StyleRunProperties = new StyleRunProperties
            {
                RunFonts = Fonts(SansFamily),
                Bold = new Bold(),
                Color = new Color { Val = "000000" },
                FontSize = new FontSize { Val = Half(12) },
            },
        });

        styles.Append(new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = Heading3Style,
            StyleName = new StyleName { Val = "heading 3" },
            BasedOn = new BasedOn { Val = NormalStyle },
            NextParagraphStyle = new NextParagraphStyle { Val = NormalStyle },
            PrimaryStyle = new PrimaryStyle(),
            StyleParagraphProperties = new StyleParagraphProperties
            {
                KeepNext = new KeepNext(),
                KeepLines = new KeepLines(),
                SpacingBetweenLines = new SpacingBetweenLines { Before = Pt(8), After = "0" },
                OutlineLevel = new OutlineLevel { Val = 2 },
            },
            StyleRunProperties = new StyleRunProperties
            {
                RunFonts = Fonts(SansFamily),
                Bold = new Bold(),
                FontSize = new FontSize { Val = Half(BodySize + 0.5) },
            },
        });

        // The PDF's "Heading4Local": heading three at body size. It is a real heading style here
        // rather than a local one, because outline level four is the difference between a parser
        // seeing a fourth-level section and seeing a bold paragraph.
        styles.Append(new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = Heading4Style,
            StyleName = new StyleName { Val = "heading 4" },
            BasedOn = new BasedOn { Val = Heading3Style },
            NextParagraphStyle = new NextParagraphStyle { Val = NormalStyle },
            PrimaryStyle = new PrimaryStyle(),
            StyleParagraphProperties = new StyleParagraphProperties
            {
                OutlineLevel = new OutlineLevel { Val = 3 },
            },
            StyleRunProperties = new StyleRunProperties
            {
                FontSize = new FontSize { Val = Half(BodySize) },
            },
        });

        // Word's own name for the style applied to list items, and no contextual spacing: the
        // PDF puts 2 pt after every bullet, and Word's built-in ListParagraph would suppress it
        // between consecutive items and change the density of the one page that matters.
        styles.Append(new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = ListStyle,
            StyleName = new StyleName { Val = "List Paragraph" },
            BasedOn = new BasedOn { Val = NormalStyle },
            StyleParagraphProperties = new StyleParagraphProperties
            {
                SpacingBetweenLines = new SpacingBetweenLines { After = Pt(2) },
            },
        });

        styles.Append(new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = CodeStyle,
            StyleName = new StyleName { Val = "Code Block" },
            BasedOn = new BasedOn { Val = NormalStyle },
            StyleParagraphProperties = new StyleParagraphProperties
            {
                Indentation = new Indentation { Left = Cm(0.5) },
            },
            StyleRunProperties = new StyleRunProperties
            {
                RunFonts = Fonts(MonoFamily),
                FontSize = new FontSize { Val = Half(BodySize - 1) },
            },
        });

        // Black and underlined, like the PDF. Blue links are for screens; this document is
        // printed as often as it is clicked, and a blue word in a CV reads as a mistake.
        styles.Append(new Style
        {
            Type = StyleValues.Character,
            StyleId = HyperlinkStyle,
            StyleName = new StyleName { Val = "Hyperlink" },
            StyleRunProperties = new StyleRunProperties
            {
                Color = new Color { Val = "000000" },
                Underline = new Underline { Val = UnderlineValues.Single },
            },
        });

        return styles;
    }

    /// <summary>A4 with the PDF's margins, in twips.</summary>
    private static SectionProperties PageSetup() => new(
        new PageSize { Width = (uint)Twips(21.0), Height = (uint)Twips(29.7) },
        new PageMargin
        {
            Top = Twips(1.8),
            Bottom = Twips(1.8),
            Left = (uint)Twips(2.0),
            Right = (uint)Twips(2.0),
            Header = 0U,
            Footer = 0U,
            Gutter = 0U,
        });

    /// <summary>
    /// Writes the document title, and nothing else, into the core properties.
    /// </summary>
    /// <remarks>
    /// By hand rather than through the SDK's package properties, for one reason: that path is the
    /// natural place for a created and a modified date to appear, and a timestamp inside the XML
    /// is a timestamp no amount of restamping the ZIP will remove. What is not written cannot
    /// drift. It is also why there is no author here - the candidate's name is in the document;
    /// putting it in the metadata as well would say a person authored a file a model drafted.
    /// </remarks>
    private static void WriteCoreProperties(WordprocessingDocument package, string title)
    {
        var part = package.AddCoreFilePropertiesPart();
        package.ChangeIdOfPart(part, "rId2");

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false,
        };

        using var stream = part.GetStream(FileMode.Create);
        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartDocument(standalone: true);
        writer.WriteStartElement("cp", "coreProperties", CorePropertiesNamespace);
        writer.WriteAttributeString("xmlns", "dc", XmlnsNamespace, DublinCoreNamespace);

        // Sanitised for the same reason every other string is: the title is built from an advert
        // title, which is text an employer typed, and an XmlWriter throws on a character XML
        // cannot carry. A download must not fail over a stray byte in a job title.
        writer.WriteElementString("title", DublinCoreNamespace, Sanitise(title));

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>
    /// One render's worth of state: the part relationships and numbering it accumulates.
    /// </summary>
    /// <remarks>
    /// The PDF renderer is entirely static because MigraDoc lets a paragraph carry its own
    /// hyperlink and its own bullet. OOXML does not: a link is a relationship on the part and a
    /// bullet is an instance in <c>numbering.xml</c>, so both have to be allocated centrally
    /// while the tree is walked. That is the whole reason this is an instance - it is not state
    /// about the document, it is the two indirections the format requires.
    /// </remarks>
    private sealed class Writer(MainDocumentPart part)
    {
        /// <summary>Abstract definitions: one shape for bullets, one for numbers.</summary>
        private const int BulletAbstractId = 0;
        private const int OrderedAbstractId = 1;

        /// <summary>Every bullet list shares one instance; nothing counts, so nothing restarts.</summary>
        private const int BulletNumberId = 1;

        private readonly List<int> _ordered = [];
        private int _nextNumberId = BulletNumberId + 1;
        private int _links;

        public void WriteBlock(Body body, Block block, int listDepth)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    WriteHeading(body, heading);
                    break;

                case ParagraphBlock paragraph:
                    WriteInlines(body.AppendChild(new Paragraph()), paragraph.Inline, default);
                    break;

                case ListBlock list:
                    WriteList(body, list, listDepth);
                    break;

                case QuoteBlock quote:
                    foreach (var child in quote)
                    {
                        var before = body.ChildElements.Count;
                        WriteBlock(body, child, listDepth);
                        Indent(body, before, Cm(0.6));
                    }

                    break;

                case CodeBlock code:
                    WriteCode(body, code);
                    break;

                case ThematicBreakBlock:
                    // Asked against in the prompt, and harmless when it turns up anyway.
                    // Rendered as space rather than a rule, which is what it means in a document
                    // like this.
                    body.AppendChild(new Paragraph(new ParagraphProperties
                    {
                        SpacingBetweenLines = new SpacingBetweenLines { After = Pt(8) },
                    }));

                    break;

                case MdTable table:
                    // Also asked against, and the pipeline does not even enable the extension -
                    // this is reachable only if it ever does. Flattened row by row exactly as the
                    // PDF flattens it: a real Word table would look different from the PDF and
                    // parse worse, since a table is where an ATS most often loses a column.
                    WriteTable(body, table);
                    break;

                case ContainerBlock container:
                    foreach (var child in container)
                    {
                        WriteBlock(body, child, listDepth);
                    }

                    break;
            }
        }

        /// <summary>
        /// Collects the numbering definitions the walk asked for.
        /// </summary>
        /// <remarks>
        /// The schema wants every abstract definition before every instance, which is why this is
        /// built at the end from what the walk recorded rather than appended to as it went.
        /// Neither definition carries a <c>w:nsid</c>: Word writes a random one there, and a
        /// random number in a document that has to hash the same twice is a defect.
        /// </remarks>
        public Numbering BuildNumbering()
        {
            var numbering = new Numbering(
                Definition(BulletAbstractId, ordered: false),
                Definition(OrderedAbstractId, ordered: true));

            numbering.Append(new NumberingInstance(new AbstractNumId { Val = BulletAbstractId })
            {
                NumberID = BulletNumberId,
            });

            foreach (var id in _ordered)
            {
                numbering.Append(new NumberingInstance(new AbstractNumId { Val = OrderedAbstractId })
                {
                    NumberID = id,
                });
            }

            return numbering;
        }

        private void WriteHeading(Body body, HeadingBlock heading)
        {
            var style = heading.Level switch
            {
                1 => Heading1Style,
                2 => Heading2Style,
                3 => Heading3Style,
                _ => Heading4Style,
            };

            var paragraph = body.AppendChild(new Paragraph(new ParagraphProperties
            {
                ParagraphStyleId = new ParagraphStyleId { Val = style },
            }));

            WriteInlines(paragraph, heading.Inline, default);
        }

        private void WriteList(Body body, ListBlock list, int listDepth)
        {
            // A fresh instance per ordered list is how numbering restarts. The PDF counts the
            // items itself and writes the number into the text; here Word counts, which is the
            // only version an ATS reads as an enumeration rather than as a sentence that happens
            // to begin with a digit.
            var numberId = list.IsOrdered ? Ordered() : BulletNumberId;
            var level = Math.Min(listDepth, MaxListLevel);

            foreach (var item in list)
            {
                if (item is not ListItemBlock listItem)
                {
                    continue;
                }

                var first = true;

                foreach (var child in listItem)
                {
                    if (child is ParagraphBlock paragraph)
                    {
                        var properties = new ParagraphProperties
                        {
                            ParagraphStyleId = new ParagraphStyleId { Val = ListStyle },
                        };

                        if (first)
                        {
                            properties.NumberingProperties = new NumberingProperties(
                                new NumberingLevelReference { Val = level },
                                new NumberingId { Val = numberId });
                        }
                        else
                        {
                            // Only the first paragraph of an item carries the marker. A
                            // continuation paragraph is indented to where the item's text sits
                            // and given no numbering, which is what a wrapped list item looks
                            // like - and, unlike the PDF, without the hanging indent, which
                            // would pull a line with no marker on it out into the margin.
                            properties.Indentation = new Indentation { Left = ListIndent(level) };
                        }

                        var target = body.AppendChild(new Paragraph(properties));
                        WriteInlines(target, paragraph.Inline, default);
                        first = false;
                    }
                    else
                    {
                        WriteBlock(body, child, listDepth + 1);
                    }
                }
            }
        }

        private static void WriteCode(Body body, CodeBlock code)
        {
            var paragraph = body.AppendChild(new Paragraph(new ParagraphProperties
            {
                ParagraphStyleId = new ParagraphStyleId { Val = CodeStyle },
            }));

            foreach (var line in code.Lines.Lines)
            {
                // Lines is the backing array rather than the used range, so its tail is empty
                // entries rather than nothing at all.
                if (line.Slice.Text is null)
                {
                    continue;
                }

                AddText(paragraph, line.ToString(), default);
                paragraph.AppendChild(new Run(new Break()));
            }
        }

        private void WriteTable(Body body, MdTable table)
        {
            foreach (var row in table)
            {
                if (row is not MdTableRow tableRow)
                {
                    continue;
                }

                var paragraph = body.AppendChild(new Paragraph());
                var first = true;

                foreach (var cell in tableRow)
                {
                    if (cell is not MdTableCell tableCell)
                    {
                        continue;
                    }

                    if (!first)
                    {
                        AddText(paragraph, CellSeparator, default);
                    }

                    foreach (var child in tableCell)
                    {
                        if (child is ParagraphBlock content)
                        {
                            WriteInlines(paragraph, content.Inline, default);
                        }
                    }

                    first = false;
                }
            }
        }

        /// <summary>
        /// Walks the inline tree, carrying emphasis down as run formatting rather than as markup.
        /// </summary>
        /// <remarks>
        /// The PDF nests a <c>FormattedText</c> per emphasis span. OOXML has no nesting to
        /// exploit - runs are siblings and each carries its own properties - so the format is
        /// carried down the recursion as an immutable value instead and stamped onto every run
        /// the subtree produces. It composes the same way: bold inside a link inside a list item
        /// is one flag set on the way down rather than three wrappers.
        /// </remarks>
        private void WriteInlines(OpenXmlCompositeElement target, ContainerInline? container, InlineFormat format)
        {
            if (container is null)
            {
                return;
            }

            foreach (var inline in container)
            {
                WriteInline(target, inline, format);
            }
        }

        private void WriteInline(OpenXmlCompositeElement target, Inline inline, InlineFormat format)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AddText(target, literal.ToString(), format);
                    break;

                case EmphasisInline emphasis:
                {
                    var nested = emphasis.DelimiterCount >= 2
                        ? format with { Bold = true }
                        : format with { Italic = true };

                    foreach (var child in emphasis)
                    {
                        WriteInline(target, child, nested);
                    }

                    break;
                }

                case CodeInline code:
                    AddText(target, code.Content, format with { Mono = true });
                    break;

                case LinkInline link:
                {
                    // Images are dropped to their alt text. A generated CV has no business
                    // carrying one, and a broken image box is worse than the words.
                    if (link.IsImage)
                    {
                        WriteInlines(target, link, format);
                        break;
                    }

                    if (StartLink(target, link.Url) is not { } hyperlink)
                    {
                        WriteInlines(target, link, format);
                        break;
                    }

                    foreach (var child in link)
                    {
                        WriteInline(hyperlink, child, format with { Link = true });
                    }

                    break;
                }

                case AutolinkInline autolink:
                {
                    // The bracketed form, which is plain CommonMark rather than the autolink
                    // extension - so it arrives whatever the pipeline enables, and it is one of
                    // the nodes whose ToString is its type name.
                    var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;

                    if (StartLink(target, url) is { } hyperlink)
                    {
                        AddText(hyperlink, autolink.Url, format with { Link = true });
                    }
                    else
                    {
                        AddText(target, autolink.Url, format);
                    }

                    break;
                }

                case HtmlInline html:
                    // Raw inline HTML keeps its characters and is escaped on the way out, so a
                    // tag a model emitted reads as the tag it typed. This case exists because
                    // the fallback below cannot serve it: Markdig's ToString returns the type
                    // name for this node, which would print "Markdig.Syntax.Inlines.HtmlInline"
                    // into somebody's CV.
                    AddText(target, html.Tag, format);
                    break;

                case HtmlEntityInline entity:
                    // "&amp;" and "&nbsp;" are core CommonMark, so a CV saying "R&amp;D" arrives
                    // here. The decoded character is what belongs in the document; the XML writer
                    // re-escapes whatever needs it.
                    AddText(target, entity.Transcoded.ToString(), format);
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        target.AppendChild(NewRun(format)).AppendChild(new Break());
                    }
                    else
                    {
                        AddText(target, " ", format);
                    }

                    break;

                case ContainerInline nested:
                    foreach (var child in nested)
                    {
                        WriteInline(target, child, format);
                    }

                    break;

                default:
                    // Last resort, and deliberately not load-bearing: every inline this pipeline
                    // can produce is mapped above, because Markdig's ToString is the *type name*
                    // for most node classes rather than the text - a fallback nobody had tested
                    // would put "Markdig.Syntax.Inlines.HtmlInline" in a candidate's CV. Enabling
                    // an extension means adding a case here, not trusting this line. Dropping the
                    // node instead is the worse failure: it takes content out of a document
                    // somebody is about to send to an employer, and says nothing.
                    AddText(target, inline.ToString() ?? string.Empty, format);
                    break;
            }
        }

        /// <summary>
        /// Opens a hyperlink around what follows, or answers null if it must stay plain text.
        /// </summary>
        /// <remarks>
        /// The id is assigned rather than generated. The SDK mints one from a random source, so a
        /// generated id alone would make two renders of one CV differ - and unlike the parts'
        /// relationships, this one is named from inside <c>document.xml</c>, so it has to be
        /// stable on both sides at once.
        /// </remarks>
        private Hyperlink? StartLink(OpenXmlCompositeElement target, string? url)
        {
            // A link inside a link is not expressible in OOXML, and an unusable target is not
            // worth a relationship: both cases keep their text and lose their link.
            if (target is Hyperlink || LinkTarget(url) is not { } uri)
            {
                return null;
            }

            var id = string.Create(CultureInfo.InvariantCulture, $"rIdLink{++_links}");
            part.AddHyperlinkRelationship(uri, isExternal: true, id);

            return target.AppendChild(new Hyperlink { Id = id, History = true });
        }

        /// <summary>Allocates the numbering instance for one ordered list.</summary>
        private int Ordered()
        {
            var id = _nextNumberId++;
            _ordered.Add(id);

            return id;
        }

        private static AbstractNum Definition(int id, bool ordered)
        {
            var definition = new AbstractNum
            {
                AbstractNumberId = id,
                MultiLevelType = new MultiLevelType { Val = MultiLevelValues.HybridMultilevel },
            };

            for (var level = 0; level <= MaxListLevel; level++)
            {
                definition.Append(new Level
                {
                    LevelIndex = level,
                    StartNumberingValue = new StartNumberingValue { Val = 1 },
                    NumberingFormat = new NumberingFormat
                    {
                        Val = ordered ? NumberFormatValues.Decimal : NumberFormatValues.Bullet,
                    },
                    LevelText = new LevelText
                    {
                        // "%3." at the third level shows that level's counter alone, which is
                        // what the PDF prints. The alternative, "%1.%2.%3.", is a document
                        // outline and not a list of achievements.
                        Val = ordered
                            ? string.Create(CultureInfo.InvariantCulture, $"%{level + 1}.")
                            : BulletGlyph,
                    },
                    LevelJustification = new LevelJustification { Val = LevelJustificationValues.Left },
                    PreviousParagraphProperties = new PreviousParagraphProperties(new Indentation
                    {
                        Left = ListIndent(level),
                        Hanging = Cm(0.35),
                    }),
                    NumberingSymbolRunProperties = new NumberingSymbolRunProperties(Fonts(SansFamily)),
                });
            }

            return definition;
        }
    }

    /// <summary>The run formatting an inline subtree inherits.</summary>
    /// <remarks>
    /// <c>Link</c> is carried here rather than inferred from the element being written into,
    /// because the character style has to be stamped on each run: a <c>w:hyperlink</c> element
    /// carries the relationship, not the appearance.
    /// </remarks>
    private readonly record struct InlineFormat(bool Bold, bool Italic, bool Mono, bool Link);

    /// <summary>The bullet the PDF draws, U+2022, and the em dash it separates table cells with.</summary>
    private const string BulletGlyph = "•";
    private const string CellSeparator = "  —  ";

    private static void AddText(OpenXmlCompositeElement target, string? value, InlineFormat format)
    {
        var text = Sanitise(value);

        if (text.Length == 0)
        {
            return;
        }

        var run = target.AppendChild(NewRun(format));

        // Space preservation is not optional: some of the separators this renderer writes are
        // spaces, and XML collapses leading and trailing whitespace without it.
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Run NewRun(InlineFormat format)
    {
        var run = new Run();

        if (format is { Bold: false, Italic: false, Mono: false, Link: false })
        {
            return run;
        }

        var properties = new RunProperties();

        if (format.Link)
        {
            properties.RunStyle = new RunStyle { Val = HyperlinkStyle };
        }

        if (format.Mono)
        {
            properties.RunFonts = Fonts(MonoFamily);
        }

        if (format.Bold)
        {
            properties.Bold = new Bold();
        }

        if (format.Italic)
        {
            properties.Italic = new Italic();
        }

        run.RunProperties = properties;

        return run;
    }

    /// <summary>
    /// The link targets that may become a real hyperlink.
    /// </summary>
    /// <remarks>
    /// An allowlist of three schemes, because a hyperlink in a Word document is a target Word
    /// hands to the operating system on a click, and the markdown behind it was written by a
    /// model that read a job advert somebody else wrote. Anything else - a relative path, a
    /// <c>file:</c> target, a scheme handler - keeps its text and loses its link, which costs a
    /// candidate nothing that mattered.
    /// </remarks>
    private static Uri? LinkTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var target))
        {
            return null;
        }

        return target.Scheme is "http" or "https" or "mailto" ? target : null;
    }

    /// <summary>
    /// Removes every character XML cannot carry.
    /// </summary>
    /// <remarks>
    /// <b>This is where the two backends' idea of hostile input differs.</b> MigraDoc takes a
    /// control character and draws nothing; an XML writer takes one and throws, and the throw
    /// arrives as a failed download of somebody's CV. Model output is not hand-typed text - it
    /// carries whatever was in the posting it was given, and a scraped advert is a byte sequence
    /// that has been through several encodings. So the characters XML 1.0 forbids are dropped
    /// here, and so is a surrogate without its pair, which is the other input that makes an
    /// <see cref="XmlWriter"/> throw. Nothing legible is ever removed.
    /// </remarks>
    private static string Sanitise(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];

            if (char.IsHighSurrogate(character))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(character).Append(value[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(character) || !IsLegalXml(character))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>The character range XML 1.0 permits, minus the surrogates handled by the caller.</summary>
    private static bool IsLegalXml(char character)
        => character is '\t' or '\n' or '\r'
            || (character >= 0x20 && character <= 0xD7FF)
            || (character >= 0xE000 && character <= 0xFFFD);

    /// <summary>Indents every paragraph added to the body since <paramref name="from"/>.</summary>
    private static void Indent(Body body, int from, string left)
    {
        for (var i = from; i < body.ChildElements.Count; i++)
        {
            if (body.ChildElements[i] is not Paragraph paragraph)
            {
                continue;
            }

            var properties = paragraph.ParagraphProperties ??= new ParagraphProperties();

            // The left edge alone, not the hanging indent: on a list item the hanging comes from
            // the numbering definition, and overriding only the left edge is what moves the whole
            // item across while leaving its marker where it belongs.
            (properties.Indentation ??= new Indentation()).Left = left;
        }
    }

    private static RunFonts Fonts(string family)
        => new() { Ascii = family, HighAnsi = family, ComplexScript = family };

    /// <summary>The PDF's 0.5 cm per level of nesting.</summary>
    private static string ListIndent(int level) => Cm(0.5 * (level + 1));

    private static int Twips(double centimetres)
        => (int)Math.Round(centimetres * 566.929, MidpointRounding.AwayFromZero);

    /// <summary>Centimetres as twips, the unit Word measures lengths in.</summary>
    private static string Cm(double centimetres)
        => Twips(centimetres).ToString(CultureInfo.InvariantCulture);

    /// <summary>Points as twips, for the spacing the PDF states in points.</summary>
    private static string Pt(double points)
        => ((int)Math.Round(points * 20, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Points as half-points, the unit Word states type sizes in.</summary>
    private static string Half(double points)
        => ((int)Math.Round(points * 2, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
}
