namespace JobPlatform.Core.Realtime;

/// <summary>What a browser needs to open a realtime connection, and nothing more.</summary>
/// <param name="Url">The hub endpoint. Public, and useless without the token.</param>
/// <param name="AccessToken">
/// Short-lived and minted per client. <b>Not a service key</b> - the service's own keys are
/// disabled, and this is issued by the managed identity for one client for a bounded window.
/// </param>
public sealed record RealtimeAccess(string Url, string AccessToken);

/// <summary>
/// The push half of the dashboard, which has been a poll until now.
/// </summary>
/// <remarks>
/// <b>The one piece of <c>model.md</c> never built</b> - "Realtime: reacts to metric writes,
/// pushes to clients" - and it is built now for a reason rather than for completeness. Every AI
/// path in this system degrades silently by design, the ledger made those failures readable
/// after the fact, and this makes them visible as they happen. A feed nobody asked for is
/// scaffolding; a feed that tells somebody their nightly sweep is losing batches is a feature.
///
/// <b>Resolved as nullable, like every other optional service here.</b> No endpoint configured
/// means no feed: the ledger still records, the dashboard still polls, and nothing throws. That
/// is the same contract <c>ICandidacyAssessor</c> and <c>ITextEmbedder</c> run under, and it
/// exists so a fresh clone deploys and works without this resource at all.
///
/// <b>Implementations must not throw.</b> A failed push is a missed notification; a failed push
/// that propagates would take down the change-feed trigger that observed it, and with it every
/// later notification too.
/// </remarks>
public interface IRealtimeFeed
{
    /// <summary>
    /// Mints a client's own access to the hub, or null where the feed is unavailable.
    /// </summary>
    /// <param name="subjectId">
    /// Who is connecting, so the service can address them individually later. Passed through
    /// rather than used today - the current feed broadcasts, because every message on it is
    /// about the system rather than about a person.
    /// </param>
    Task<RealtimeAccess?> NegotiateAsync(string subjectId, CancellationToken ct = default);

    /// <summary>Sends one message to every connected client. Never throws.</summary>
    Task PublishAsync(string target, object payload, CancellationToken ct = default);
}

/// <summary>
/// The names both ends agree on. Changing one is a change to a wire contract.
/// </summary>
/// <remarks>
/// Constants rather than literals at the call sites, because the sender is C# in a Function and
/// the receiver is TypeScript in a browser: nothing in the compiler connects them, so the only
/// protection against a rename is that there is exactly one place to rename.
/// </remarks>
public static class RealtimeChannels
{
    /// <summary>The hub. One is enough while every message is about the system.</summary>
    public const string Hub = "dashboard";

    /// <summary>A model call that lost something. The first and only consumer.</summary>
    public const string AiFailure = "aiFailure";
}

/// <summary>
/// A model call that lost something, as a client sees it.
/// </summary>
/// <remarks>
/// Deliberately not the whole <c>AiCallRecord</c>. That type carries an optional prompt, which
/// holds the candidate's employment history and salary expectations, and the three guards that
/// keep it out of the list endpoint would have to be reproduced here to keep it off a websocket
/// - so the safer shape is a projection that has no field for it at all. A client wanting more
/// asks the ledger, which is authenticated.
/// </remarks>
public sealed record AiFailureNotice(
    string Operation,
    string? Deployment,
    string Outcome,
    int Requested,
    int Returned,
    string? Reason,
    DateTimeOffset OccurredAtUtc)
{
    public int Discarded => Math.Max(0, Requested - Returned);
}
