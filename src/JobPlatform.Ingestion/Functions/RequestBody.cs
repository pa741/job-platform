using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace JobPlatform.Ingestion.Functions;

/// <summary>
/// Reads a JSON request body directly, rather than through <c>[FromBody]</c>.
/// </summary>
/// <remarks>
/// The Functions worker's model binder hands these endpoints a <c>null</c> for a well-formed
/// body, and does it silently. That is worse than an error on an admin route: a call asking
/// to reprocess one blob quietly reprocessed the whole container and returned 200, and the
/// only visible symptom was that it took longer than it should have. Every parameter on these
/// endpoints bounds how much work is done, so one being ignored is not a cosmetic problem.
///
/// A malformed body returns null and the caller falls back to its defaults, which matches
/// what the endpoints did before and keeps a bad request from being an exception.
/// </remarks>
internal static class RequestBody
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<T?> ReadAsync<T>(HttpRequest request, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength is null or 0)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, Options, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
