using System.Security.Claims;

namespace JobPlatform.Api.Infrastructure;

/// <summary>
/// Resolving who is calling, once, in one place.
/// </summary>
/// <remarks>
/// Extracted because the profile, match and application endpoints all key their data on the
/// caller's directory object id, and every one of them getting this subtly wrong is the whole
/// authorisation model failing at once. There is one implementation and every feature uses it.
/// </remarks>
public static class CallerIdentity
{
    /// <summary>
    /// The caller's stable directory object id, or null where the token carries none.
    /// </summary>
    /// <remarks>
    /// <b><c>oid</c>, and deliberately never a fallback to <c>ClaimTypes.NameIdentifier</c>.</b>
    /// That resolves to <c>sub</c>, which is pairwise per application: a profile stored under it
    /// would be invisible to the same person arriving through a second app registration, and
    /// the failure would look like data loss rather than like a claim mix-up. <c>/me</c> already
    /// makes this distinction and this is the same rule, now load-bearing for data ownership
    /// rather than only for display.
    ///
    /// Both the short name and the mapped URI are read: the JWT handler rewrites several short
    /// claim names to legacy SOAP-era URIs, so <c>oid</c> is frequently absent under the name
    /// the token actually carried.
    /// </remarks>
    public static string? SubjectId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return First(user, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier");
    }

    /// <summary>
    /// The caller's subject id, or a 401 result explaining why there is none.
    /// </summary>
    /// <remarks>
    /// A token that authenticated but carries no <c>oid</c> is not a 500 and not an empty
    /// profile: it is a caller this system cannot store data for, and saying so is more useful
    /// than either alternative. In practice it means an app registration issued a token without
    /// the claim, which is a configuration problem the message should point at.
    /// </remarks>
    public static bool TryGetSubjectId(this ClaimsPrincipal user, out string subjectId, out IResult error)
    {
        var value = user.SubjectId();

        if (string.IsNullOrWhiteSpace(value))
        {
            subjectId = string.Empty;
            error = TypedResults.Problem(
                detail: "The token carries no 'oid' claim, so there is no principal to store a profile against.",
                statusCode: StatusCodes.Status401Unauthorized);

            return false;
        }

        subjectId = value;
        error = TypedResults.Empty;

        return true;
    }

    private static string? First(ClaimsPrincipal user, params string[] claimTypes)
        => claimTypes
            .Select(user.FindFirstValue)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
