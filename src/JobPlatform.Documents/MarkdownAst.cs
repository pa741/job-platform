using Markdig;
using Markdig.Syntax;

namespace JobPlatform.Documents;

/// <summary>
/// The one parse of markdown that every renderer in this assembly walks.
/// </summary>
/// <remarks>
/// <b>Two backends, one tree.</b> A CV and a cover letter are emitted twice - once as PDF for a
/// human to look at and once as DOCX for an applicant tracking system to parse - and a recruiter
/// may open either. If the two renderers each built their own pipeline they would eventually
/// disagree about what the markdown means, and the difference would surface as a paragraph that
/// exists in one file and not the other. There is one pipeline here and both parse through it, so
/// "the same document in two formats" is enforced by construction rather than by a comment asking
/// somebody to keep two builders in step.
///
/// Deliberately close to plain CommonMark. Every extension enabled is a construct both renderers
/// then have to handle, and a CV needs headings, emphasis, lists, links and a table - not
/// footnotes, task lists or custom containers. The prompt asks for the same subset, so this is the
/// second half of one agreement rather than an independent guess.
///
/// <b>Pipe tables are on, and they were off for one release too long.</b> The argument for leaving
/// them off was that a CV needs no tables; candidates write them anyway, because an education
/// section is a table - qualification, institution, dates, grade - and so is a skills matrix. What
/// an unparsed table renders as is not a plain paragraph but the pipes themselves, run together
/// into prose: <c>| BSc (Hons) | University of Northampton | 2025 | 2:1 |</c> arriving at an
/// employer as a sentence. A construct people will type has to be handled or refused, and the one
/// thing it must not do is reach a PDF as its own syntax.
///
/// <b>A single newline is a line break here, which is a deliberate departure from CommonMark.</b>
/// The specification folds one into a space, so a skills list typed as one line per group
/// - <c>**Languages**: C#, TypeScript</c> and the next on the line below - renders as one long
/// paragraph with the bold runs buried in it. That is correct CommonMark and wrong for this
/// product: these documents are typed into a plain textarea by a person who expects the lines they
/// typed, which is the same bargain every comment box on the internet strikes. The cost is bounded
/// because the other author here is a model told to write prose in paragraphs separated by blank
/// lines: a wrapped line would break early rather than lose text, and the markdown remains the
/// record either way.
///
/// <b>There is no HTML step anywhere downstream of this.</b> The tree is walked into document
/// primitives directly; nothing that comes back from a language model is ever handed to a markup
/// interpreter. That is what makes generating a document from a model safe to hand to a
/// candidate: the worst a bad response can do is read badly.
/// </remarks>
internal static class MarkdownAst
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UsePipeTables()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>Parses model output into the syntax tree the renderers map.</summary>
    /// <remarks>
    /// Null and empty are documents with no blocks rather than an error. A generation that came
    /// back empty is a problem for the caller that decides whether to store it, not for the
    /// renderer, which still has to produce a file rather than throw on a download.
    /// </remarks>
    public static MarkdownDocument Parse(string? markdown)
        => Markdown.Parse(markdown ?? string.Empty, Pipeline);
}
