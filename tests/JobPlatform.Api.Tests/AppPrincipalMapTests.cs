using JobPlatform.Api.Features.Mcp;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Which candidate an unattended client acts for.
/// </summary>
/// <remarks>
/// Every case here fails the same way in production - an empty pipeline from a surface that
/// authenticated perfectly - so none of them is distinguishable by looking at the client. That
/// is what makes them worth pinning rather than checking by hand once.
/// </remarks>
public sealed class AppPrincipalMapTests
{
    private const string Principal = "11111111-1111-1111-1111-111111111111";
    private const string Candidate = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void A_mapped_principal_resolves_to_the_candidate_it_acts_for()
    {
        var map = new Dictionary<string, string> { [Principal] = Candidate };

        Assert.Equal(Candidate, AppPrincipalMap.Resolve(Principal, map));
    }

    /// <summary>
    /// The case the configuration binder cannot handle on its own.
    /// </summary>
    /// <remarks>
    /// Configuration binds a dictionary with an ordinal comparer, so an object id typed with a
    /// capital letter into an app setting matches nothing. Entra writes <c>oid</c> in lower case
    /// and a person copying one out of the portal may not, and the symptom - every tool reporting
    /// no profile - is identical to the setting being absent.
    /// </remarks>
    [Fact]
    public void A_mapping_written_in_a_different_case_still_resolves()
    {
        var map = new Dictionary<string, string> { [Principal.ToUpperInvariant()] = Candidate };

        Assert.Equal(Candidate, AppPrincipalMap.Resolve(Principal, map));
    }

    /// <summary>
    /// An unmapped caller is returned unchanged, never guessed at.
    /// </summary>
    /// <remarks>
    /// This is what keeps a delegated token working: a person's own <c>oid</c> is in no map and
    /// must reach the repositories as itself. It is also why a single-entry map cannot quietly
    /// become "the candidate everything resolves to".
    /// </remarks>
    [Fact]
    public void An_unmapped_caller_resolves_to_itself()
    {
        var map = new Dictionary<string, string> { [Principal] = Candidate };

        Assert.Equal("33333333-3333-3333-3333-333333333333",
            AppPrincipalMap.Resolve("33333333-3333-3333-3333-333333333333", map));
    }

    [Fact]
    public void An_empty_or_absent_map_resolves_every_caller_to_itself()
    {
        Assert.Equal(Principal, AppPrincipalMap.Resolve(Principal, new Dictionary<string, string>()));
        Assert.Equal(Principal, AppPrincipalMap.Resolve(Principal, null));
    }

    /// <summary>
    /// A setting that exists with no value is a half-finished deployment, not a mapping.
    /// </summary>
    /// <remarks>
    /// Resolving it to the empty string would hand a blank subject id to a repository, which
    /// finds no profile and reports the candidate has not filled the form in - the wrong
    /// diagnosis, and the one that sends somebody to the dashboard instead of to the setting.
    /// </remarks>
    [Fact]
    public void A_blank_mapping_is_not_a_mapping()
    {
        var map = new Dictionary<string, string> { [Principal] = "   " };

        Assert.Equal(Principal, AppPrincipalMap.Resolve(Principal, map));
    }

    [Fact]
    public void A_caller_with_no_id_is_a_programming_error_rather_than_an_unmapped_one()
        => Assert.Throws<ArgumentException>(() => AppPrincipalMap.Resolve("  ", null));
}
