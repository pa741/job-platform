using System.Collections.Concurrent;

namespace JobPlatform.Ingestion.Tests.Ats;

/// <summary>
/// An <c>IHttpClientFactory</c> that answers from a recorded payload rather than from a vendor.
/// </summary>
/// <remarks>
/// <b>The factory is stubbed rather than the client</b>, because the factory is what the readers
/// actually take - matching how the production wiring works means a test exercises the same
/// construction path, and a reader that started newing up its own client would fail here rather
/// than pass while quietly leaking sockets.
///
/// The handler is shared and never disposed with the client, so a reader's <c>using var client</c>
/// is safe to run many times over one stub - which is exactly what the deduplication and
/// concurrency tests need to count.
/// </remarks>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// A handler that records what was asked for and answers what the test says.
/// </summary>
/// <remarks>
/// <b>The recorded URLs are the assertion that matters most in this suite.</b> This is the only
/// place in the system that composes a URL for somebody else's API, so "which address was called,
/// with which token, how many times" is the behaviour under test as much as the parse is.
/// </remarks>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<string, CancellationToken, Task<HttpResponseMessage>> _answer;

    public StubHandler(Func<string, HttpResponseMessage> answer)
        : this((url, _) => Task.FromResult(answer(url)))
    {
    }

    public StubHandler(Func<string, CancellationToken, Task<HttpResponseMessage>> answer)
        => _answer = answer;

    /// <summary>Every URL asked for, in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Every request's headers, so a test can assert what was <i>not</i> sent.</summary>
    public ConcurrentQueue<HttpRequestHeaders> Headers { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;

        Requests.Enqueue(url);
        Headers.Enqueue(new HttpRequestHeaders(request));

        return _answer(url, cancellationToken);
    }
}

/// <summary>What a request carried, captured before the message is disposed.</summary>
/// <remarks>
/// Copied rather than held, because <c>HttpRequestMessage</c> is disposed by the reader's
/// <c>using</c> as soon as the response is read, and a test asserting on a disposed message is a
/// test that passes for the wrong reason.
/// </remarks>
internal sealed class HttpRequestHeaders
{
    public HttpRequestHeaders(HttpRequestMessage request)
    {
        Method = request.Method.Method;
        Names = [.. request.Headers.Select(header => header.Key)];
        HasBody = request.Content is not null;
    }

    public string Method { get; }

    public IReadOnlyList<string> Names { get; }

    public bool HasBody { get; }
}
