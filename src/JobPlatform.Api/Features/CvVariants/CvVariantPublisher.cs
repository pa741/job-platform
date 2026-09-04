using JobPlatform.Ai.Extraction;
using JobPlatform.Core.Applications;
using JobPlatform.Data.Applications;
using JobPlatform.Data.Sql;

namespace JobPlatform.Api.Features.CvVariants;

/// <summary>
/// What happens to a variant's words after they are stored: they are laid out, and they are read.
/// </summary>
/// <remarks>
/// <b>Neither half may fail the save, and that is the whole shape of this class.</b> The markdown
/// is the record - it is the candidate's own document, typed into a box - and everything here is
/// derived from it: a PDF and a DOCX that can be produced again from text that has not changed, and
/// a set of concepts that can be re-read from the same text for the price of one model call. Losing
/// any of that costs a re-run; losing somebody's typing to a MigraDoc exception or a provider
/// timeout costs an afternoon they will not spend twice. That is the contract
/// <c>ScraperConfigPublisher</c> already runs under and the trade <c>CvVariantRenderer</c> makes on
/// its own side, and this is the layer that has to hold to it as well.
///
/// <b>Degrading silently is not the same as degrading invisibly, which is why the failure has a
/// place to show.</b> <c>CvVariant.IsSendable</c> is false for a variant with no current PDF, so a
/// failed render is a line on the page saying a CV needs rendering rather than an application made
/// against a document that does not exist - and the response this returns carries that flag,
/// unaltered, in the same request the person saved in.
///
/// <b>The extraction goes to <c>CvVariantConcepts</c> and to nothing else, and this method's
/// return type is where that is enforced rather than remembered.</b> A variant's concepts feed
/// selection only: they must never reach <c>ProfileConcepts</c>, never move a match score, and
/// never widen what the candidate is judged to have, because a CV is written <i>from</i> the
/// profile and letting its reading back in would let a document inflate the record it came from -
/// after which the apply loop applies to jobs on the strength of its own prose. So
/// <see cref="ExtractAsync"/> answers a <c>bool</c> and <see cref="PublishAsync"/> answers a
/// <see cref="CvVariant"/>: the <c>CvVariantExtraction</c> is never in scope for a caller to hand
/// anywhere, which is the same construction <c>ProfileEndpoints.ExtractAsync</c> uses one layer up
/// and the same one the type itself already makes at the Ai boundary.
///
/// <b>Nothing here writes a word of the document.</b> The extractor reads markdown and returns
/// concept keys; the renderer parses markdown and lays it out. Neither has a path back to
/// <c>CvVariants.Markdown</c>, and no route in this feature gives them one. That absence is the
/// feature.
/// </remarks>
internal static class CvVariantPublisher
{
    /// <summary>
    /// Renders a saved variant and reads it for concepts. Answers the variant as it now stands.
    /// </summary>
    /// <remarks>
    /// <b>Render first, then extract, and the order is not arbitrary.</b> The render is what the
    /// caller's response has to reflect - it is the difference between a row that says
    /// <c>isSendable</c> and one that says a CV still needs laying out - so recording it before the
    /// model call means a provider that hangs until the request's own cancellation costs the
    /// concepts and not the rendered file. The reverse order would leave the commoner failure
    /// taking the rarer one down with it.
    ///
    /// <b>Both services are optional and their absence is a capability this deployment does not
    /// have rather than a fault.</b> A host with no storage configured registers no renderer and a
    /// host with no AI provider registers no extractor - the shape this system ships in - and a
    /// variant saved on either is text that is kept, is editable, and simply never becomes
    /// selectable. That is a different answer from <c>POST /applications/{postingId}</c>, which
    /// refuses with a 503 when there is no writer: generating a document with no writer produces
    /// nothing at all, where saving a CV with no renderer produces the thing that actually matters.
    /// </remarks>
    /// <param name="variants">The repository, already scoped to the caller by the profile id below.</param>
    /// <param name="profileId">The candidate, resolved from the token by the caller.</param>
    /// <param name="variant">The variant as stored, with an id the database has assigned.</param>
    /// <param name="candidateName">The candidate's own name, for the filename. Blank is allowed.</param>
    /// <param name="renderer">Null where no storage is configured.</param>
    /// <param name="extractor">Null where no AI provider is configured.</param>
    /// <param name="logger">Where a swallowed failure goes, so it is silent and not invisible.</param>
    /// <param name="ct">Cancellation. A cancelled request is not a failure and is not swallowed.</param>
    public static async Task<CvVariant> PublishAsync(
        CvVariantRepository variants,
        long profileId,
        CvVariant variant,
        string? candidateName,
        CvVariantRenderer? renderer,
        ICvVariantExtractor? extractor,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(logger);

        var published = await RenderAsync(variants, profileId, variant, candidateName, renderer, logger, ct)
            ?? variant;

        await ExtractAsync(variants, profileId, variant, extractor, logger, ct);

        return published;
    }

    /// <summary>
    /// Lays the variant out twice, uploads both, and records the pair with the digest in one write.
    /// </summary>
    /// <remarks>
    /// <b>The renderer's own record is handed straight to the repository rather than unpacked and
    /// reassembled here.</b> <c>CvVariantRenderer.RenderAsync</c> returns exactly what
    /// <c>CvVariantRepository.RecordRenderAsync</c> takes, deliberately: both paths and the hash
    /// reach SQL in one <c>SaveChanges</c>, <i>after</i> the uploads have succeeded. A caller
    /// assembling its own shape, or writing a row per file, opens a window in which the row claims
    /// to be rendered while pointing at a blob that is not there yet - selection picks the variant
    /// up, and the browser loop finds the file missing at the upload box with the tab already open.
    ///
    /// <b>A refusal from the repository is not an exception here.</b>
    /// <c>CvVariantWriteResult.NotFound</c> means the row went away between the save and the render
    /// - a delete this system does not perform, so in practice it cannot happen - and the answer to
    /// it is the same as the answer to a failed render: the caller keeps the variant it already
    /// has, which is honest about what is stored.
    /// </remarks>
    private static async Task<CvVariant?> RenderAsync(
        CvVariantRepository variants,
        long profileId,
        CvVariant variant,
        string? candidateName,
        CvVariantRenderer? renderer,
        ILogger logger,
        CancellationToken ct)
    {
        if (renderer is null)
        {
            return null;
        }

        try
        {
            var rendered = await renderer.RenderAsync(profileId, variant, candidateName, ct);

            if (rendered is null)
            {
                // The renderer has already logged what it refused and why. Saying it twice would
                // put two lines in the log for one event and make the count of failures wrong.
                return null;
            }

            return (await variants.RecordRenderAsync(profileId, variant.Id, rendered, ct)).Variant;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The variant is already stored. Losing the render costs one line on the dashboard
            // saying this CV needs rendering, and it is recoverable by saving again; failing the
            // request would cost the document the person just wrote.
            logger.LogWarning(
                ex,
                "Storing CV variant {VariantId} succeeded but rendering it did not. The markdown is "
                + "kept and the variant stays out of selection until a render succeeds.",
                variant.Id);

            return null;
        }
    }

    /// <summary>
    /// Reads the variant's markdown for concepts and stores them against that variant.
    /// </summary>
    /// <remarks>
    /// <b>A <c>bool</c>, and not the extraction, for the reason <c>ProfileEndpoints</c> gives about
    /// its own pass and a stronger one here.</b> There, returning the extraction let a caller hand
    /// the model's demand-side polarity straight to a client. Here the risk is a table rather than a
    /// display: <c>CandidateProfileRepository.ApplyExtractionAsync</c> writes
    /// <c>ProfileConcepts</c>, and a variant's concepts reaching it would let a CV widen what the
    /// candidate is judged to have. <c>CvVariantExtraction</c> is a separate type from
    /// <c>DocumentExtraction</c> precisely so that call will not compile - and keeping the value out
    /// of scope here means nobody has to notice that it would not.
    ///
    /// <b>The version stored is the extraction's own.</b> <c>CvVariantConcepts.ResolverVersion</c>
    /// is what makes a vocabulary improvement re-appliable to the rows below it, so a constant
    /// written here instead would mark every row with a number describing a pass that did not
    /// produce it.
    ///
    /// <b>Unresolved mentions are discarded knowingly.</b> There is no <c>CvVariantMentions</c>
    /// table, and the extractor returns them rather than dropping them so that a caller with
    /// nowhere to put them has to decide. This is that decision: the honest half of a <c>NoFit</c>
    /// is worth recording and there is nothing yet to record it in, which is a gap in the schema
    /// rather than something to invent a column for from an endpoint.
    /// </remarks>
    private static async Task<bool> ExtractAsync(
        CvVariantRepository variants,
        long profileId,
        CvVariant variant,
        ICvVariantExtractor? extractor,
        ILogger logger,
        CancellationToken ct)
    {
        if (extractor is null)
        {
            return false;
        }

        try
        {
            var extraction = await extractor.ExtractAsync(
                new CvVariantExtractionRequest(variant.Id, variant.Markdown, variant.Label), ct);

            if (extraction is null)
            {
                return false;
            }

            // CvVariantConcepts and nothing else. There is no call from here into the profile's
            // concept rows, and there must never be one: a CV is written from the profile, so
            // letting its reading back in would let a document inflate the record it came from.
            await variants.ReplaceConceptsAsync(
                profileId, variant.Id, extraction.Concepts, extraction.Version, ct);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A variant with no concepts scores nothing against every posting and is therefore
            // never chosen - which is visible as an abstention and a gap brief rather than as a
            // wrong CV being sent, and is repaired by saving the variant again.
            logger.LogWarning(
                ex,
                "Storing CV variant {VariantId} succeeded but reading it for concepts did not. It "
                + "will not be selected against any posting until an extraction succeeds.",
                variant.Id);

            return false;
        }
    }
}
