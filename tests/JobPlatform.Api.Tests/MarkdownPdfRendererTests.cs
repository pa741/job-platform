using System.Text;
using JobPlatform.Documents;
using PdfSharp.Pdf.IO;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The markdown-to-PDF renderer.
/// </summary>
/// <remarks>
/// What can be asserted about a PDF without rendering it to pixels is narrow, so these tests
/// aim at the two things that actually break in production. The first is that it produces a
/// valid document at all: PDFsharp's platform-independent build resolves no fonts of its own
/// and throws on its first call without a resolver installed, and that failure would arrive as
/// a 500 on a candidate's CV download rather than at build time.
///
/// The second is that no input can make it throw. The markdown comes from a language model,
/// which means it eventually contains everything the prompt asked it not to - tables, raw HTML,
/// horizontal rules, deeply nested lists - and every one of those must degrade to something
/// readable rather than take the endpoint down.
/// </remarks>
public sealed class MarkdownPdfRendererTests
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

    private static PdfSharp.Pdf.PdfDocument Read(byte[] bytes)
        => PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);

    [Fact]
    public void A_cv_renders_to_a_valid_pdf()
    {
        var bytes = MarkdownPdfRenderer.Render(Cv, "Ada Lovelace - CV");

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 4), StringComparison.Ordinal);

        using var document = Read(bytes);
        Assert.True(document.PageCount >= 1);
    }

    [Fact]
    public void The_title_reaches_the_document_metadata()
    {
        // What a browser shows in the tab and what a file manager shows in a preview.
        using var document = Read(MarkdownPdfRenderer.Render(Cv, "CV - Platform Engineer"));

        Assert.Equal("CV - Platform Engineer", document.Info.Title);
    }

    [Fact]
    public void A_realistic_cv_fits_on_one_page()
    {
        // Not a formatting preference. A two-page CV for a single role is the clearest sign the
        // margins or the type size have drifted, and it is invisible in every other assertion
        // here.
        using var document = Read(MarkdownPdfRenderer.Render(Cv, "CV"));

        Assert.Equal(1, document.PageCount);
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
        // Everything here is something the prompt asks the model not to produce, which is
        // exactly why each one has to survive: a prompt is a request, not a guarantee, and the
        // cost of being wrong is a candidate's download failing.
        var bytes = MarkdownPdfRenderer.Render(markdown, "Edge case");

        Assert.NotEmpty(bytes);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 4), StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_is_deterministic_in_size_for_the_same_input()
    {
        // The fonts are embedded rather than resolved from the machine, so two renders of one
        // document produce the same bytes regardless of what is installed. That is the property
        // that makes a developer's laptop and the Linux container agree.
        var first = MarkdownPdfRenderer.Render(Cv, "CV");
        var second = MarkdownPdfRenderer.Render(Cv, "CV");

        Assert.Equal(first.Length, second.Length);
    }

    [Fact]
    public void A_long_document_flows_onto_further_pages()
    {
        var builder = new StringBuilder("# Long CV\n\n");

        for (var i = 0; i < 200; i++)
        {
            builder.Append("- A bullet point describing a piece of work, number ").Append(i).AppendLine(".");
        }

        using var document = Read(MarkdownPdfRenderer.Render(builder.ToString(), "Long"));

        Assert.True(document.PageCount > 1);
    }

    [Fact]
    public void The_font_resolver_can_be_installed_more_than_once()
    {
        // PDFsharp keeps the resolver in process-wide static state and refuses a second
        // assignment, so every render calls Install and it has to be a no-op after the first.
        EmbeddedFontResolver.Install();
        EmbeddedFontResolver.Install();

        Assert.NotEmpty(MarkdownPdfRenderer.Render("# Still fine", "Repeat"));
    }
}
