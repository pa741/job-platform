using JobPlatform.Ai.Applications;
using JobPlatform.Core.Submissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobPlatform.Ai;

/// <summary>
/// Registers the form-field resolver, which is the one service in this layer that is registered
/// whether or not there is a provider.
/// </summary>
/// <remarks>
/// <b>Deliberately not inside <see cref="AiRegistration.AddAiProvider"/>, and that is the whole
/// reason it is a separate call.</b> <c>IDocumentExtractor</c>, <c>ICandidacyAssessor</c>,
/// <c>IApplicationWriter</c> and <c>ITextEmbedder</c> all live inside that method's provider
/// check and are resolved as nullable, because a deployment with no Azure OpenAI endpoint has
/// nothing for them to do. That is right for them and wrong here: three of the resolver's four
/// stages are lookups over what the candidate has already typed, and registering it conditionally
/// would take those down with the model - a candidate's own stored answers would stop being found
/// because an environment variable was absent. The precedent is <c>MatchSweepFunction</c>, which
/// is registered unconditionally so that scoring still happens where assessment cannot.
///
/// <b>The Kernel is resolved as an optional constructor parameter rather than looked up.</b> When
/// <c>AddAiProvider</c> registered one it is injected and stage four works; when it did not, the
/// default null is used and stage four abstains with a sentence saying so. Registration order
/// between the two calls does not matter - the container resolves constructor arguments when the
/// service is first built, not when it is registered.
///
/// <b><see cref="OptionsServiceCollectionExtensions.AddOptions(IServiceCollection)"/> is called
/// here so this method stands alone.</b> The resolver reads deployment names and the call timeout
/// from <c>AzureOpenAiOptions</c>; without a provider nothing has bound that section, and an
/// <c>IOptions&lt;T&gt;</c> that cannot be resolved would turn "no AI configured" into a startup
/// failure - which is the exact outcome <c>AddAiProvider</c>'s remarks say must not happen. With
/// it, the options carry their defaults, which is all stages one to three need of them: nothing.
/// </remarks>
public static class FormFieldResolverRegistration
{
    /// <summary>Adds <see cref="IFormFieldResolver"/>. Safe to call with or without a provider.</summary>
    /// <remarks>
    /// <c>TryAdd</c> rather than <c>Add</c>, so a host that has already substituted its own
    /// resolver - a test, or a later deployment with a different last resort - keeps it. A second
    /// registration would win silently otherwise, and the thing it would win over is the class that
    /// decides what gets typed into somebody's application.
    /// </remarks>
    public static IServiceCollection AddFormFieldResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        services.TryAddSingleton<IFormFieldResolver, FormFieldResolver>();

        return services;
    }
}
