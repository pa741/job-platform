namespace JobPlatform.Ai;

/// <summary>
/// Settings for the Semantic Kernel-backed chat provider.
/// </summary>
/// <remarks>
/// Provider-shaped rather than Anthropic-shaped, so pointing the Kernel at a different chat
/// service later is a configuration change. Only <see cref="ApiKey"/> is provider-specific,
/// and only because a hosted model needs a credential of some kind.
/// </remarks>
public sealed class AiProviderOptions
{
    public const string SectionName = "Ai:Anthropic";

    /// <summary>Which provider <c>AddAiProvider</c> resolves: <c>none</c> or <c>anthropic</c>.</summary>
    public const string ProviderKey = "Ai:Provider";

    /// <summary>
    /// The Anthropic API key.
    /// </summary>
    /// <remarks>
    /// The only secret in this system, and it is treated as one. In Azure it arrives as a
    /// Container Apps secret backed by Key Vault and resolved with the shared managed
    /// identity, surfaced here as the environment variable <c>Ai__Anthropic__ApiKey</c>. It is
    /// never a Bicep parameter, never a template output, and never committed. Locally, set it
    /// as a user secret:
    ///   dotnet user-secrets set "Ai:Anthropic:ApiKey" "&lt;key&gt;" --project src/JobPlatform.Api
    /// If it is absent no Kernel is registered and the application still starts.
    /// </remarks>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5";

    public int MaxTokens { get; set; } = 8000;

    /// <summary>Wall-clock ceiling for one model call, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Names the provider in anything a caller sees, so they know what answered.</summary>
    public string ProviderName { get; set; } = "semantic-kernel/anthropic";
}
