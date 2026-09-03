using JobPlatform.Core.Submissions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The container is built the way a container should be built, and every lifetime survives it.
/// </summary>
/// <remarks>
/// <b>Written because the suite could not see the fault it is about.</b> The form-field resolver
/// consults <c>IAiCallLog</c> so that a resolution reaching the model leaves a record like every
/// other call site, and this host registers that log <i>scoped</i>. Registered as a singleton the
/// resolver would capture one request's log and hold it for the life of the process - every later
/// resolution writing its audit trail through a disposed context belonging to a request that ended
/// hours ago.
///
/// <b>The ordinary defences both miss it.</b> Production does not validate scopes, so nothing
/// fails at startup - it degrades quietly, in the audit log specifically, which is the one place
/// a fault is least likely to be noticed and most likely to matter. And
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/> does not turn the
/// validation on either: reverting the registration to a singleton leaves all 313 of the other
/// tests in this project green, which is how this gap was found rather than reasoned about.
///
/// So this asks the question directly. <c>ValidateScopes</c> makes resolving a scoped service from
/// the root provider an error, and <c>ValidateOnBuild</c> makes it an error at build time for
/// every registration rather than only for the ones a test happens to exercise - which is the
/// property worth having, because the next captured dependency will be in a service no test
/// resolves.
/// </remarks>
public sealed class ServiceLifetimeTests
{
    [Fact]
    public void Every_registration_survives_a_container_that_checks_its_lifetimes()
    {
        using var factory = new ApiFactory();

        // Building the host is the assertion: ApiFactory turns ValidateOnBuild on, which walks
        // every registration the container can construct and throws an AggregateException naming
        // each captured dependency.
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider);
    }

    /// <summary>The resolver is scoped, and asking the root container for it is how we know.</summary>
    /// <remarks>
    /// <b>ValidateOnBuild does not cover this one, which is why it is asserted separately.</b> The
    /// resolver takes an optional <c>Kernel</c>, and no AI provider is configured in these tests,
    /// so the container cannot construct it and the build-time walk skips it entirely. The check
    /// that does bite is at resolution: with <c>ValidateScopes</c> on, asking the ROOT provider for
    /// a scoped service throws, and a singleton answers. So the throw is the evidence.
    ///
    /// What it protects is not startup - production disables both checks and would start fine - but
    /// the audit trail. A singleton resolver captures the first request's <c>IAiCallLog</c> and
    /// writes every later resolution's record through a context belonging to a request that ended
    /// long ago.
    /// </remarks>
    [Fact]
    public void The_form_field_resolver_is_scoped_because_the_call_log_it_writes_to_is()
    {
        using var factory = new ApiFactory();

        var fromRoot = Record.Exception(() => factory.Services.GetRequiredService<IFormFieldResolver>());

        Assert.IsType<InvalidOperationException>(fromRoot);

        // And it does resolve where it is meant to, so the assertion above is about the lifetime
        // rather than about the registration having gone missing altogether.
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IFormFieldResolver>());
    }
}
