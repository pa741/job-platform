using JobPlatform.Core.Applications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Reads the employers' boards one pass needs, once each, a few at a time.
/// </summary>
/// <remarks>
/// <b>Three rules about how a pass behaves towards somebody else's API live here, because none of
/// them can be enforced by a single client.</b> A client knows how to read one board; only
/// something looking at the whole pass can promise that a board is read once, that a vendor is not
/// asked fifty things at the same moment, and that a vendor nobody has written a reader for costs
/// no request at all.
///
/// <b>One fetch per board, not per posting, and the deduplication is what makes that true.</b>
/// Cloudflare's Greenhouse board answered 333 jobs in one request on 2026-09-07; a pass over the
/// 309 link-less postings would otherwise ask the same board once for each of its own postings and
/// receive the identical bytes each time. <c>AtsBoard</c> is a record with value equality over
/// vendor, token and region, so <c>Distinct</c> is the whole implementation - and its token is
/// case-sensitive on purpose, so two spellings of one employer's board cost two requests and never
/// a wrong answer, which is the trade <c>AtsBoard</c> itself argues for.
///
/// <b>The concurrency bound protects the vendor rather than us</b>, which is the same argument
/// <c>AtsBoardCandidates.MaxCandidates</c> makes for its own number. It is global rather than per
/// vendor: see <see cref="AtsBoardOptions.MaxConcurrentFetches"/>.
///
/// <b>Nothing here throws</b>, including on cancellation. A cancelled pass answers
/// <see cref="AtsBoardRead.Unavailable"/> for what it did not reach and hands back what it did,
/// because the boards already read are work that has been paid for - in requests to somebody
/// else - and throwing them away to report the cancellation the caller already knows about would
/// spend that again on the next pass.
///
/// <b>It is deliberately not a queue, a retry policy or a cache.</b> Whether an employer is worth a
/// probe is <c>AtsBoardCandidates</c>'s judgement, whether a board is worth trusting is
/// <c>AtsBoardCandidates.Confirm</c>'s, and which listing is the posting is
/// <c>AtsListingMatcher</c>'s. This decides only which requests are made and how many at once.
/// </remarks>
public sealed class AtsBoardReader
{
    private readonly Dictionary<AtsVendor, IAtsBoardClient> _clients = [];
    private readonly AtsBoardOptions _options;
    private readonly ILogger<AtsBoardReader> _logger;

    /// <summary>Indexes the registered readers by vendor.</summary>
    /// <remarks>
    /// A second client for one vendor keeps the first and warns rather than throwing. Throwing
    /// would take down a host over a duplicate registration line, and silently taking the last
    /// would be a reader replaced by whichever registration ran second - which is the failure the
    /// curated container's own registration remarks describe, where two registrations of one type
    /// resolved by order and the wrong one wrote into the landing account.
    /// </remarks>
    public AtsBoardReader(
        IEnumerable<IAtsBoardClient> clients,
        IOptions<AtsBoardOptions> options,
        ILogger<AtsBoardReader> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;

        foreach (var client in clients)
        {
            if (!_clients.TryAdd(client.Vendor, client))
            {
                _logger.LogWarning(
                    "Two board readers are registered for {Vendor}; keeping the first. One of the "
                    + "two is unreachable, which is a wiring error rather than a vendor answer.",
                    client.Vendor);
            }
        }
    }

    /// <summary>
    /// Whether any reader can answer for this vendor at all.
    /// </summary>
    /// <remarks>
    /// <b>For a probe budget to consult before it spends anything.</b> <c>AtsBoard</c> admits the
    /// five vendors that serve a public board, and a reader exists for four of them - so a caller
    /// generating candidate tokens for a Workable employer would otherwise spend
    /// <c>AtsBoardCandidates.MaxCandidates</c> round trips to be told four times that nobody is
    /// listening. Asking here costs nothing and answers the same thing.
    /// </remarks>
    public bool CanRead(AtsVendor vendor) => _clients.ContainsKey(vendor);

    /// <summary>
    /// Reads one board.
    /// </summary>
    /// <remarks>
    /// A vendor with no reader answers <see cref="AtsBoardRead.Unavailable"/> and never
    /// <see cref="AtsBoardRead.NotABoard"/>. The distinction is the one thing this method has to
    /// get right: "nobody here can read Workable" is a fact about this repository, and reporting it
    /// as "no such board" would tell a caller the employer's token was wrong - which is a
    /// conclusion it might store.
    ///
    /// <b>The catch is defence in depth rather than the net.</b> <see cref="AtsBoardClient"/>
    /// already promises never to throw, and every reader here is one; this covers a reader added
    /// later that is not, and it is loud about it - a warning naming the vendor, because an
    /// implementation ignoring its own interface is a bug rather than a vendor being down. Pinning
    /// the guarantee in two layers is the same instinct as pinning an authorization policy as
    /// endpoint metadata as well as behaviourally.
    /// </remarks>
    public async Task<AtsBoardRead> ReadAsync(
        AtsBoard board,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);

        if (!_clients.TryGetValue(board.Vendor, out var client))
        {
            _logger.LogDebug(
                "No board reader for {Vendor}, so board {Token} was not asked for. This is a gap "
                + "in this repository, not an answer about the employer.",
                board.Vendor,
                board.Token);

            return AtsBoardRead.Unavailable;
        }

        try
        {
            return await client.ReadAsync(board, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The {Vendor} board reader threw, which its interface forbids. Board {Token} is "
                + "unread; the pass continues.",
                board.Vendor,
                board.Token);

            return AtsBoardRead.Unavailable;
        }
    }

    /// <summary>
    /// Reads every distinct board once, at most
    /// <see cref="AtsBoardOptions.MaxConcurrentFetches"/> at a time.
    /// </summary>
    /// <remarks>
    /// The result is keyed by the board rather than by the token, because a token is only unique
    /// within one vendor and one region - which is the whole reason <c>AtsBoardRegion</c> exists,
    /// and a dictionary keyed on the string would silently merge a European tenant with the
    /// unrelated company holding the same slug on the ordinary host.
    /// </remarks>
    public async Task<IReadOnlyDictionary<AtsBoard, AtsBoardRead>> ReadAsync(
        IEnumerable<AtsBoard> boards,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boards);

        var distinct = boards.Distinct().ToArray();

        if (distinct.Length == 0)
        {
            return new Dictionary<AtsBoard, AtsBoardRead>();
        }

        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentFetches));

        var reads = await Task.WhenAll(
            distinct.Select(board => ReadInTurnAsync(gate, board, cancellationToken)));

        var results = new Dictionary<AtsBoard, AtsBoardRead>(distinct.Length);

        for (var index = 0; index < distinct.Length; index++)
        {
            results[distinct[index]] = reads[index];
        }

        return results;
    }

    /// <summary>One board, once a slot is free, and never as an exception.</summary>
    /// <remarks>
    /// The wait is the only place cancellation can surface, because
    /// <see cref="IAtsBoardClient.ReadAsync"/> already answers rather than throws. Catching it here
    /// is what lets <c>Task.WhenAll</c> above be safe to await without a handler: a pass stopped
    /// half way returns the boards it read and <see cref="AtsBoardRead.Unavailable"/> for the rest.
    /// </remarks>
    private async Task<AtsBoardRead> ReadInTurnAsync(
        SemaphoreSlim gate,
        AtsBoard board,
        CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return AtsBoardRead.Unavailable;
        }

        try
        {
            return await ReadAsync(board, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
