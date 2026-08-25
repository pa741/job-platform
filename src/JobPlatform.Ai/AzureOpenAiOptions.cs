namespace JobPlatform.Ai;

/// <summary>
/// Settings for the Semantic Kernel-backed chat provider.
/// </summary>
/// <remarks>
/// <b>There is no key here, and there is not meant to be.</b> Azure OpenAI authenticates with
/// Microsoft Entra, so the provider is reached with the same user-assigned managed identity
/// that already reaches SQL, Cosmos and Storage. That is what removed the single exception
/// this architecture used to carry: the Anthropic API key, its Key Vault, its Container Apps
/// secret reference and the out-of-band <c>az keyvault secret set</c> that put a value in it
/// have all gone, and a fresh clone now deploys with genuinely nothing to leak.
///
/// Two deployments rather than one, because the two jobs have opposite shapes. Extraction and
/// assessment are high-volume, structured and cheap-per-item, so they run on the smallest
/// model that can do the job. Writing a CV happens once, for one person, and is the thing they
/// will actually be judged on, so it runs on the best model available. Both are deployment
/// names rather than model ids: an Azure OpenAI deployment is a name the subscription chose,
/// and pointing one at a newer model is a Bicep change with no code in it.
/// </remarks>
public sealed class AzureOpenAiOptions
{
    public const string SectionName = "Ai:AzureOpenAi";

    /// <summary>Which provider <c>AddAiProvider</c> resolves: <c>none</c> or <c>azureopenai</c>.</summary>
    public const string ProviderKey = "Ai:Provider";

    /// <summary>Service id the bulk deployment is registered under inside the Kernel.</summary>
    public const string BulkServiceId = "bulk";

    /// <summary>Service id the writing deployment is registered under inside the Kernel.</summary>
    public const string WritingServiceId = "writing";

    /// <summary>
    /// The resource endpoint, e.g. <c>https://jobplatform-ai.openai.azure.com/</c>.
    /// </summary>
    /// <remarks>
    /// Not a secret, and treated as configuration rather than as one: it comes from the
    /// Bicep output through a plain environment variable, the same way the Cosmos endpoint
    /// does. Absent it, no Kernel is registered and everything AI-shaped stays inert.
    /// </remarks>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Deployment name for the high-volume pass: extraction and candidacy assessment.
    /// </summary>
    /// <remarks>
    /// Named for the job rather than the model, matching what Bicep creates. Which model sits
    /// behind it is a deployment decision: <c>gpt-5.6-luna</c> where the subscription has quota
    /// for it - cheapest of the 5.6 family, 1.05M context, which is what makes packing many
    /// postings into one call worthwhile - and whatever else has capacity where it does not.
    /// Either way this name does not move, so changing model is not a configuration change.
    /// </remarks>
    public string BulkDeployment { get; set; } = "bulk";

    /// <summary>
    /// Deployment name for the writing pass: tailored CV and cover letter.
    /// </summary>
    /// <remarks>
    /// Named for the job rather than the model, as above. <c>gpt-5.6-sol</c> where quota
    /// allows - roughly twenty-five times the price of Luna per token, affordable precisely
    /// because this path runs once per application rather than once per posting.
    /// </remarks>
    public string WritingDeployment { get; set; } = "writing";

    /// <summary>
    /// How many documents travel in one bulk call.
    /// </summary>
    /// <remarks>
    /// The whole reason batching pays. The concept vocabulary is several thousand tokens and
    /// has to precede every extraction; sending it once per posting means paying for it once
    /// per posting. Ten documents to a call amortises it tenfold, and Luna's context window is
    /// nowhere near the constraint - the output token budget is, which is why this is ten and
    /// not a hundred.
    /// </remarks>
    public int BatchSize { get; set; } = 10;

    /// <summary>Output ceiling for one bulk call. Scales with <see cref="BatchSize"/>.</summary>
    public int BulkMaxTokens { get; set; } = 16_000;

    /// <summary>Output ceiling for one writing call: a CV and a cover letter together.</summary>
    public int WritingMaxTokens { get; set; } = 8_000;

    /// <summary>Wall-clock ceiling for one bulk call, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Wall-clock ceiling for one writing call, in seconds.</summary>
    public int WritingTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Client id of the user-assigned managed identity to authenticate as.
    /// </summary>
    /// <remarks>
    /// Required in Azure and empty locally. A host with several identities cannot be asked to
    /// guess which one to present, which is the same reason the SQL connection string carries
    /// <c>User Id=&lt;clientId&gt;</c>. Left empty, <c>DefaultAzureCredential</c> falls back to
    /// the signed-in developer.
    /// </remarks>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Names the provider in anything a caller sees, so they know what answered.</summary>
    public string ProviderName { get; set; } = "semantic-kernel/azure-openai";
}
