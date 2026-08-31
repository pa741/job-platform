using System.Text.Json;
using JobPlatform.Core.Enrichment;

namespace JobPlatform.Ai.Extraction;

/// <summary>
/// Reads a model answer that was stored earlier, so a parser change costs nothing to apply.
/// </summary>
/// <remarks>
/// <b>The narrow public door onto an otherwise internal parser.</b> <c>ExtractionPrompt</c> stays
/// internal: it is the wire format of a prompt, and nothing outside this layer should be able to
/// depend on its shape. But <c>PostingExtractions.PayloadJson</c> keeps every answer the model has
/// ever given, and that store is worth something only if something can re-read it - so this is the
/// one operation exposed, and it is deliberately the smallest one that works.
///
/// <b>Why it matters.</b> The expensive half of extraction is asking; parsing is a string and some
/// dictionary lookups. Measured on 2026-08-31, re-extracting the 5,822 postings already read would
/// be roughly ten million tokens at the observed 1,700 per document. Re-parsing them is a query.
/// So a change to how an answer is read - a new concept in the vocabulary, a fix to how the
/// model's unknown list is handled - reaches the whole corpus for free, and only a change to what
/// the model is *asked* needs paying for again.
/// </remarks>
public static class StoredExtraction
{
    /// <summary>
    /// Re-reads a stored payload, or null where it is not usable JSON.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw, because the caller is a corpus pass: one malformed row out of
    /// several thousand must leave the posting with the assertions it already had and let the
    /// pass continue, not stop the run. A null is countable and a throw is not.
    /// </remarks>
    public static DocumentExtraction? Reparse(string? payloadJson, string? model)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);

            return ExtractionPrompt.Parse(document.RootElement, model);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
