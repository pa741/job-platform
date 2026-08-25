using Microsoft.SemanticKernel.Connectors.AzureOpenAI;

namespace JobPlatform.Ai;

/// <summary>
/// The execution settings every prompt in this layer is invoked with.
/// </summary>
/// <remarks>
/// Centralised because two of these properties are load-bearing in ways that fail obscurely
/// when they are wrong, and neither is something a caller should be expected to remember.
/// </remarks>
internal static class AiPrompt
{
    /// <summary>
    /// Settings for the high-volume deployment: extraction and candidacy assessment.
    /// </summary>
    /// <param name="options">Deployment names and ceilings.</param>
    /// <param name="reasoningEffort">
    /// How much thinking to pay for. <c>low</c> for reading structure out of text that already
    /// contains it; the assessment pass raises it, because judging a candidate against a role
    /// is the one bulk job that is actually a judgement.
    /// </param>
    public static AzureOpenAIPromptExecutionSettings Bulk(
        AzureOpenAiOptions options, string reasoningEffort = "low")
        => Json(AzureOpenAiOptions.BulkServiceId, options.BulkMaxTokens, reasoningEffort);

    /// <summary>Settings for the writing deployment: tailored CV and cover letter.</summary>
    public static AzureOpenAIPromptExecutionSettings Writing(
        AzureOpenAiOptions options, string reasoningEffort = "medium")
        => Json(AzureOpenAiOptions.WritingServiceId, options.WritingMaxTokens, reasoningEffort);

    private static AzureOpenAIPromptExecutionSettings Json(
        string serviceId, int maxTokens, string reasoningEffort)
        => new()
        {
            // Which of the Kernel's two chat services answers. Both are registered on one
            // Kernel, so this is the only thing that decides whether a call costs Luna money
            // or Sol money.
            ServiceId = serviceId,

            // JSON mode. The Azure OpenAI connector can express this where the provider-neutral
            // settings could not, which is the concrete thing the move off the hand-composed
            // Anthropic transport bought: the response is guaranteed to parse.
            //
            // It guarantees *valid JSON*, not the right shape - the schema is still carried by
            // the prompt - so AiJson and the defensive parsing downstream both stay.
            ResponseFormat = "json_object",

            MaxTokens = maxTokens,

            // Without this, Semantic Kernel serialises MaxTokens as `max_tokens`, which every
            // GPT-5 series model rejects outright: "Unsupported parameter: 'max_tokens' is not
            // supported with this model. Use 'max_completion_tokens' instead." The failure is a
            // 400 on the first real call and nothing catches it earlier, so it is set here once
            // rather than per prompt.
            //
            // Marked experimental by Semantic Kernel (SKEXP0010), and suppressed at exactly
            // this line rather than through a project-wide NoWarn: the suppression is only
            // correct for this one property, and a blanket one would silently accept the next
            // experimental API somebody reaches for.
#pragma warning disable SKEXP0010
            SetNewMaxCompletionTokensEnabled = true,
#pragma warning restore SKEXP0010

            // Reasoning models bill thinking tokens, and on a corpus-wide pass that is most of
            // the bill. Deliberately not `none`: at none the model stops reasoning about
            // whether a phrase means "essential" or "desirable", which is the one thing the
            // deterministic pass cannot do and therefore the entire reason for calling it.
            ReasoningEffort = reasoningEffort,

            // Temperature is deliberately left unset. Reasoning models accept only the default
            // and answer 400 for anything else, and an unset property is not serialised.
        };
}
