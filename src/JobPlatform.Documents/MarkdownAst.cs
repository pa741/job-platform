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
/// then have to handle, and a CV needs headings, emphasis, lists and links - not footnotes, task
/// lists or custom containers. The prompt asks for the same subset, so this is the second half of
/// one agreement rather than an independent guess. Note what follows from that: a pipe table in
/// the markdown is not parsed as a table at all, because the table extension is off; both
/// renderers still carry a mapping for one, so enabling the extension changes the output rather
/// than breaking a render.
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
