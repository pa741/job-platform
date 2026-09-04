using JobPlatform.Core.Enrichment;

namespace JobPlatform.Ai.Extraction;

/// <summary>
/// One CV variant handed to the model, and nothing else about the person who wrote it.
/// </summary>
/// <remarks>
/// <b>There is no profile id here, and its absence is the same rule <c>CvVariantFacts</c> and
/// <c>CandidateProfileRepository</c> already state as types.</b> Every caller reaches this having
/// already established whose library it is reading, and the repository call that stores the answer
/// is scoped by that owner; a field here would be a second copy of a fact the caller has, free to
/// disagree with the first. The extractor has no use for it either - it reads markdown and returns
/// concepts, and knowing whose markdown it was cannot change either.
///
/// <b><see cref="VariantId"/> is carried for the ledger and read nowhere else</b>, exactly as
/// <see cref="ExtractionRequest.SourceId"/> is on the posting side. A failure that can only be
/// reported as "one CV was not read" is not something anybody can act on; one that names the
/// variant is. An id is safe to record where the document is not - the markdown is somebody's
/// employment history in their own words, the id is an integer.
/// </remarks>
/// <param name="VariantId">The row this markdown came from, so a failure can say what it lost.</param>
/// <param name="Markdown">The candidate's own words, verbatim. The only thing the model reads.</param>
/// <param name="Label">
/// What the person calls this CV - "Backend .NET". Sent because it is a heading the candidate wrote
/// over their own document and reads as one, the same way a posting's title is sent with its body.
/// </param>
public readonly record struct CvVariantExtractionRequest(
    long VariantId,
    string Markdown,
    string? Label = null);

/// <summary>
/// What a CV variant was read as saying, and deliberately nothing more.
/// </summary>
/// <remarks>
/// <b>A separate type from <see cref="DocumentExtraction"/>, and that separation is the guard
/// rather than a naming preference.</b> A variant's concepts feed selection <i>only</i>: they must
/// never reach <c>ProfileConcepts</c>, never move a match score, and never widen what the candidate
/// is judged to have. A CV is written <i>from</i> the profile, so letting its reading back in would
/// let a document inflate the record it came from, and the apply loop would then be applying to
/// jobs on the strength of its own prose.
///
/// Reusing <see cref="DocumentExtraction"/> here would have made that a rule somebody has to
/// remember. <c>CandidateProfileRepository.ApplyExtractionAsync</c> takes a
/// <see cref="DocumentExtraction"/>, so a variant pass that produced one would be a single
/// well-meaning line away from writing a CV's vocabulary into the profile - and nothing would fail,
/// because the two are the same shape by design. This type cannot be passed to it at all. That is
/// the same construction the profile endpoint already uses one layer up, where
/// <c>ProfileEndpoints.ExtractAsync</c> returns a <c>bool</c> specifically so the extraction is not
/// in scope to be handed to a caller by mistake; here the narrowing is moved into the type, so it
/// holds for every call site rather than for one.
///
/// <b>What is absent is as considered as what is present.</b> There is no seniority, no work
/// arrangement, no salary and no location, because a CV states all four and none of them may reach
/// a match: seniority read off a CV is precisely "widening what the candidate is judged to have".
/// The prompt does not ask for them and this type could not hold them if it did, which is two
/// refusals where one would have been an instruction.
///
/// <b>There is no payload either, unlike <c>PostingExtractions.PayloadJson</c>.</b> That column
/// buys a corpus-wide re-read for the price of a query and is worth several million tokens; a
/// variant is one document, its markdown is still on the row beside these rows, and re-deriving
/// costs one call over text that has not changed - the bargain
/// <c>CvVariantRepository.ReplaceConceptsAsync</c> already names. Keeping a verbatim copy of
/// somebody's CV in a diagnostics field would be paying a real disclosure risk for a saving that
/// does not exist.
/// </remarks>
public sealed record CvVariantExtraction
{
    /// <summary>
    /// Bumped when this pass would read the same markdown differently.
    /// </summary>
    /// <remarks>
    /// This is the number stored as <c>CvVariantConcepts.ResolverVersion</c>, which is what makes a
    /// vocabulary improvement re-appliable to rows below it rather than a reason to re-read every
    /// CV in the system. Separate from <see cref="DocumentExtraction.CurrentVersion"/> on purpose:
    /// the two passes ask different questions of different documents, and a posting-side prompt
    /// change has no business marking a library of six CVs stale.
    /// </remarks>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The concepts this CV was read as claiming, every one of them <c>AssertionSource.Model</c>.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately the same <see cref="ConceptAssertion"/> the profile and posting sides
    /// produce</b>, because <c>CvVariantConcepts</c> mirrors <c>ProfileConcepts</c> column for
    /// column and selection is a join between identical shapes rather than a translation layer.
    /// What the shared shape does not imply is that these rows may travel: the type they arrive in
    /// is what stops that, not the type they are made of.
    ///
    /// Every key has been re-checked against the graph before it appears here - see
    /// <see cref="KernelCvVariantExtractor"/> - so a key the vocabulary does not know is in
    /// <see cref="Mentions"/> and not in this list.
    /// </remarks>
    public IReadOnlyList<ConceptAssertion> Concepts { get; init; } = [];

    /// <summary>
    /// Technologies the CV named that the vocabulary has no concept for.
    /// </summary>
    /// <remarks>
    /// <b>Recorded rather than dropped, the rule <c>PostingMentions</c> exists to enforce</b>, and a
    /// variant is the single richest source of these in the system: it is the one document written
    /// by a person choosing their own words, where an advert is written to a template and a profile
    /// is typed into a form. Nothing persists these today - there is no <c>CvVariantMentions</c>
    /// table - so a caller that has nowhere to put them may discard them, but it must do so
    /// knowingly, which is the difference between this being returned and this being silently
    /// unavailable.
    ///
    /// It is also the honest half of a <c>NoFit</c>: a CV whose technologies the vocabulary cannot
    /// resolve scores nothing and is never chosen, and the mention list is the only thing that can
    /// say why rather than leaving the candidate to conclude their CV is bad.
    /// </remarks>
    public IReadOnlyList<UnresolvedMention> Mentions { get; init; } = [];

    /// <summary>Which reading produced this. See <see cref="CurrentVersion"/>.</summary>
    public int Version { get; init; } = CurrentVersion;
}

/// <summary>
/// Reads one CV variant for the concepts a selection pass scores it on.
/// </summary>
/// <remarks>
/// <b>Selection needs a variant to be a bag of concepts, and this is the only thing that turns
/// markdown into one.</b> <c>CvVariantSelector</c> scores <c>CvVariantFacts.ConceptKeys</c> against
/// a posting's requirements; those keys come from <c>CvVariantConcepts</c>; those rows come from
/// here. Everything downstream of this call is arithmetic over a curated graph.
///
/// <b>A separate contract from <see cref="IDocumentExtractor"/> rather than a third
/// <see cref="DocumentKind"/>.</b> Adding a <c>Cv</c> member to that enum was the smaller-looking
/// change and it does not work: the result would still be a <see cref="DocumentExtraction"/>, which
/// is the exact object <c>CandidateProfileRepository.ApplyExtractionAsync</c> accepts, so the guard
/// that a CV's vocabulary never re-enters the profile would rest on every future caller passing the
/// right one of two identical values. It would also inherit the batch surface, and see below on why
/// that is the wrong shape here. Two contracts is more code and one fewer thing to remember.
///
/// <b>There is no batch method, and its absence is measured against what batching bought
/// elsewhere.</b> <see cref="IDocumentExtractor.ExtractBatchAsync"/> exists because the concept
/// vocabulary is several thousand tokens, has to precede every extraction, and would otherwise be
/// paid for once per posting across a corpus of tens of thousands - that ratio is what makes a
/// corpus-wide pass affordable at all. A library is capped at six variants and is re-read when one
/// person saves one document, so packing would save five copies of the vocabulary, once, per
/// candidate. What it would cost is the failure mode <c>KernelDocumentExtractor.Distribute</c>
/// exists to police: an answer landing against the wrong index. On the posting side that writes one
/// advert's requirements onto another and the backfill eventually corrects it; here it writes one
/// CV's concepts onto a different CV, and the visible consequence is that the wrong document is
/// uploaded to an employer under somebody's name - wrong, self-consistent, and discovered by nobody.
/// This is the same trade the OpenAI batch path already refuses for the same reason, and the saving
/// here is far smaller.
///
/// <b>Registered only where a Kernel is</b>, exactly as the three services beside it in
/// <c>AiRegistration.AddAiProvider</c> are, so a deployment with no provider resolves this as null
/// and skips the step rather than throwing. Consumers therefore take
/// <c>ICvVariantExtractor?</c>, never <c>ICvVariantExtractor</c>.
///
/// <b>The degraded behaviour is correct rather than merely tolerable, which is unusual and worth
/// saying.</b> With no provider a variant has no concepts, and a variant with no concepts scores
/// zero against every posting, falls below <c>CvVariantSelector.SelectionFloor</c>, and cannot win a
/// selection. The pass therefore abstains and the candidate is told that no CV fits - which is the
/// answer this whole feature was built to give - rather than being sent a document nobody scored.
/// The failure that would be intolerable is the opposite one: a missing extractor that let a variant
/// through unscored.
/// </remarks>
public interface ICvVariantExtractor
{
    /// <summary>
    /// Null when the model returned nothing usable. Never throws for a bad response.
    /// </summary>
    /// <remarks>
    /// The same contract <see cref="IDocumentExtractor.ExtractAsync"/> carries, and the caller's
    /// response to null is the same: store nothing. A variant whose concepts were not read keeps
    /// whatever rows it had - which for a first save is none - and is simply not chosen until a
    /// later pass reads it. That is strictly better than storing a partial reading, because a
    /// partial reading is indistinguishable from a CV that genuinely says less.
    /// </remarks>
    Task<CvVariantExtraction?> ExtractAsync(
        CvVariantExtractionRequest request, CancellationToken ct = default);
}
