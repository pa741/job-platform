namespace JobPlatform.Ai;

/// <summary>
/// Settings for the Semantic Kernel-backed ranker.
/// </summary>
/// <remarks>
/// Provider-shaped rather than Anthropic-shaped, so pointing the Kernel at a different chat
/// service later is a configuration change. Only <see cref="ApiKey"/> is provider-specific,
/// and only because a hosted model needs a credential of some kind.
/// </remarks>
public sealed class SemanticKernelOptions
{
    public const string SectionName = "Matching:Anthropic";

    /// <summary>
    /// The Anthropic API key.
    /// </summary>
    /// <remarks>
    /// The only secret in this system, and it is treated as one. In Azure it arrives as a
    /// Container Apps secret backed by Key Vault and resolved with the shared managed
    /// identity, surfaced here as the environment variable
    /// <c>Matching__Anthropic__ApiKey</c>. It is never a Bicep parameter, never a template
    /// output, and never committed. Locally, set it as a user secret:
    ///   dotnet user-secrets set "Matching:Anthropic:ApiKey" "&lt;key&gt;" --project src/JobPlatform.Api
    /// If it is absent the matching feature falls back to the keyword ranker rather than
    /// failing to start.
    /// </remarks>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5";

    public int MaxTokens { get; set; } = 8000;

    /// <summary>Wall-clock ceiling for one ranking call, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Reported to callers in the match response, so they know what ranked their CV.</summary>
    public string ProviderName { get; set; } = "semantic-kernel/anthropic";
}
