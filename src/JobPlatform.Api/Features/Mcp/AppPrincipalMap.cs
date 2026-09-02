namespace JobPlatform.Api.Features.Mcp;

/// <summary>
/// Which candidate an application principal acts for.
/// </summary>
/// <remarks>
/// Pure and separated from <see cref="SubmissionTools"/> for the reason <c>MatchScorer</c> and
/// <c>SubmissionState.Fold</c> are separated from the things that call them: the interesting
/// cases here are ones a test can state exactly, and reaching them through the tools would need
/// an authenticated principal the test host cannot mint.
///
/// It decides one thing and deliberately not two. Whether a caller <i>should</i> be mapped is
/// the map's own business - an operator wrote it - so this does not inspect the token, and an
/// entry naming a person would resolve for that person too. What it must never do is invent a
/// mapping, which is why an absent or blank value resolves to the caller unchanged rather than
/// to anything else.
/// </remarks>
public static class AppPrincipalMap
{
    /// <summary>
    /// The subject <paramref name="actorId"/> acts for, or <paramref name="actorId"/> itself.
    /// </summary>
    /// <remarks>
    /// <b>Case-insensitively, and by scanning rather than by hashing.</b> Configuration binds a
    /// dictionary with an ordinal comparer, so an object id written with a capital letter in an
    /// app setting would map nothing while looking exactly right - a failure whose symptom is an
    /// empty pipeline, which is the same symptom as not having deployed the setting at all. The
    /// map holds one entry or two, so the scan costs nothing worth caching around a reloadable
    /// option.
    ///
    /// <b>A blank value is not a mapping.</b> An app setting present but empty is what a
    /// half-finished deployment looks like, and resolving it to the empty string would send a
    /// blank subject id to a repository rather than saying nothing was configured.
    /// </remarks>
    public static string Resolve(string actorId, IReadOnlyDictionary<string, string>? map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        if (map is null || map.Count == 0)
        {
            return actorId;
        }

        foreach (var pair in map)
        {
            if (string.Equals(pair.Key, actorId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value.Trim();
            }
        }

        return actorId;
    }
}
