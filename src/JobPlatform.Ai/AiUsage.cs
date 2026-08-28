using JobPlatform.Core.Ai;
using Microsoft.SemanticKernel;
using OpenAI.Chat;

namespace JobPlatform.Ai;

/// <summary>
/// Reads what a call cost out of a Semantic Kernel result.
/// </summary>
/// <remarks>
/// One reader for every call site, for the reason <c>AiJson</c> gives: the alternative is three
/// copies that drift, and the extraction path has already been caught carrying a bug the
/// assessment path had independently.
///
/// The connector puts the provider's usage object in <c>FunctionResult.Metadata["Usage"]</c>.
/// That key is a connector detail rather than a Semantic Kernel contract, so this returns an
/// empty usage instead of throwing when the shape changes - a ledger that loses its token counts
/// is worse than one without them, but an assessment lost because the metadata moved is worse
/// still.
/// </remarks>
public static class AiUsage
{
    public static AiTokenUsage From(FunctionResult? result)
    {
        if (result?.Metadata is null
            || !result.Metadata.TryGetValue("Usage", out var value)
            || value is not ChatTokenUsage usage)
        {
            return default;
        }

        return new AiTokenUsage(
            usage.InputTokenCount,
            usage.OutputTokenCount,
            // Null on a non-reasoning deployment, which is a real case: the writing and bulk
            // deployments are configured independently and either could be swapped.
            usage.OutputTokenDetails?.ReasoningTokenCount ?? 0,
            usage.TotalTokenCount);
    }
}
