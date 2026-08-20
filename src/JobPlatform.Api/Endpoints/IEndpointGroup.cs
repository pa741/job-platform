namespace JobPlatform.Api.Endpoints;

/// <summary>
/// One feature's routes.
/// </summary>
/// <remarks>
/// The extension point this API is organised around. Adding a feature means adding a folder
/// with one of these in it and one line in <see cref="EndpointGroupExtensions"/> - never
/// editing a growing switch or a thousand-line Program.cs. Registration is explicit rather
/// than by assembly scanning so the route surface stays greppable and startup stays
/// debuggable by reading it.
/// </remarks>
public interface IEndpointGroup
{
    void Map(IEndpointRouteBuilder routes);
}
