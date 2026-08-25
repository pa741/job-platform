namespace JobPlatform.Ai;

/// <summary>
/// Settings for the OpenAI batch extraction path.
/// </summary>
/// <remarks>
/// <b>This is the one secret in the system, and it is worth being explicit about why it
/// exists.</b> Everything else here authenticates with Microsoft Entra and holds no credential
/// at all - Azure OpenAI included, where the resource sets <c>disableLocalAuth</c> and Azure
/// refuses to issue a usable key. Reaching OpenAI directly cannot work that way.
///
/// It buys three things Azure could not, in ascending order of importance: half price, the
/// <c>gpt-5.6</c> family that Azure's batch matrix does not yet carry, and - the one that
/// actually decided it - a rate limit pool separate from the standard deployment's, which is
/// what a corpus-wide pass keeps running into.
///
/// It is scoped as narrowly as the design allows: only job adverts go through it. A candidate
/// profile is somebody's employment history and stays on the Azure path, which means personal
/// data never leaves the tenant. That split is enforced by which function calls which
/// extractor, not by a setting.
///
/// The key lives in Key Vault, is read by the shared managed identity through a Container Apps
/// secret reference, and its value is set out of band with <c>az keyvault secret set</c> -
/// never a Bicep parameter, never a template output, never in deployment history. Absent it,
/// nothing here registers and the pipeline falls back to the Azure path.
/// </remarks>
public sealed class OpenAiBatchOptions
{
    public const string SectionName = "Ai:OpenAi";

    /// <summary>
    /// The OpenAI API key.
    /// </summary>
    /// <remarks>
    /// Locally, set it as a user secret rather than putting it in any file:
    ///   dotnet user-secrets set "Ai:OpenAi:ApiKey" "&lt;key&gt;" --project src/JobPlatform.Ingestion
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The model every request in a batch runs on.
    /// </summary>
    /// <remarks>
    /// A batch is single-model by the provider's rule, so this is not a per-request choice.
    /// Luna is the cheapest of the 5.6 family and carries a 1.05M context window; at batch rates
    /// it is $0.10 per million input tokens.
    /// </remarks>
    public string Model { get; set; } = "gpt-5.6-luna";

    /// <summary>
    /// How many documents one submitted batch may carry.
    /// </summary>
    /// <remarks>
    /// The provider's own ceiling is fifty thousand requests and two hundred megabytes, which
    /// the whole corpus fits inside several times over. This is lower on purpose: a batch is
    /// all-or-nothing to resubmit, and a smaller one that expires costs less to redo than a
    /// large one. It also keeps the input file well clear of the size limit without anyone
    /// having to measure it.
    ///
    /// A submission larger than this is trimmed rather than split - one call submits one batch,
    /// and the caller already reports a `more` flag for the remainder.
    /// </remarks>
    public int MaxBatchSize { get; set; } = 2_000;

    /// <summary>Output ceiling per request. One document's answer, not a batch of them.</summary>
    public int MaxOutputTokens { get; set; } = 4_000;

    /// <summary>
    /// How much thinking to pay for.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>none</c>: at none the model stops reasoning about whether a phrase
    /// means "essential" or "desirable", which is the one thing the deterministic pass cannot do
    /// and therefore the entire reason for calling it.
    /// </remarks>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>Names the provider in anything a caller sees, so they know what answered.</summary>
    public string ProviderName { get; set; } = "openai/batch";
}
