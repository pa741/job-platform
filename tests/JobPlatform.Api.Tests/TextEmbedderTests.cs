using System.ClientModel;
using System.ClientModel.Primitives;
using JobPlatform.Ai;
using JobPlatform.Ai.Matching;
using JobPlatform.Core.Ai;
using JobPlatform.Core.Matching;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// What the embedder does when the provider misbehaves, which on this resource it does.
/// </summary>
/// <remarks>
/// <b>Every case here was observed rather than imagined.</b> A freshly provisioned deployment on
/// the Foundry account answers <c>404 DeploymentNotFound</c> from some data-plane backends and
/// 200 from others - measured at roughly one call in three failing, randomly, for the same URL
/// and api-version. The embedding pass stops on its first fully-failed batch by design, so
/// without a retry a one-in-three failure rate is a near-certainty of stopping within a few
/// batches and never embedding the corpus at all.
///
/// The other half is the contract the caller depends on: this never throws for a provider
/// failure, it returns nulls, and it never aligns a short response by position.
/// </remarks>
public sealed class TextEmbedderTests
{
    private static KernelTextEmbedder Embedder(IEmbeddingGenerator<string, Embedding<float>> generator)
        => new(
            generator,
            Options.Create(new AzureOpenAiOptions { EmbeddingDeployment = "embeddings" }),
            logger: null,
            callLog: null,
            // Fake, so the backoff does not make a unit test take seven seconds.
            time: new InstantTime());

    private static float[] Vector(float seed)
        => [.. Enumerable.Repeat(seed, EmbeddingVector.Dimensions)];

    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_transient_404_is_retried_rather_than_surfaced()
    {
        // The measured failure. The deployment exists - the control plane says Succeeded - and
        // some backends have not caught up. Retrying is what turns that into a delay instead of
        // an unembedded corpus.
        var generator = new FlakyGenerator(failures: 2, status: 404);

        var result = await Embedder(generator).EmbedAsync(["one advert"]);

        Assert.Equal(3, generator.Calls);
        Assert.NotNull(result[0]);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task The_other_transient_statuses_are_retried_too(int status)
    {
        var generator = new FlakyGenerator(failures: 1, status);

        var result = await Embedder(generator).EmbedAsync(["one advert"]);

        Assert.Equal(2, generator.Calls);
        Assert.NotNull(result[0]);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_request_that_is_simply_wrong_is_not_retried(int status)
    {
        // Retrying a malformed request or a missing role assignment only makes it wrong four
        // times. It still must not throw - the caller writes nothing and moves on.
        var generator = new FlakyGenerator(failures: 99, status);

        var result = await Embedder(generator).EmbedAsync(["one advert"]);

        Assert.Equal(1, generator.Calls);
        Assert.Null(result[0]);
    }

    [Fact]
    public async Task A_permanently_failing_batch_gives_up_and_returns_nulls()
    {
        var generator = new FlakyGenerator(failures: 99, status: 404);

        var result = await Embedder(generator).EmbedAsync(["a", "b", "c"]);

        Assert.Equal(4, generator.Calls);
        Assert.Equal(3, result.Count);
        Assert.All(result, Assert.Null);
    }

    [Fact]
    public async Task A_response_of_the_wrong_length_is_refused_rather_than_aligned()
    {
        // The embeddings endpoint answers in request order and offers no per-item id, so a short
        // response cannot be correlated at all. Taking the first n would file one advert's vector
        // against another - wrong, self-consistent, and undetectable afterwards.
        var generator = new ShortGenerator(returns: 2);

        var result = await Embedder(generator).EmbedAsync(["a", "b", "c"]);

        Assert.Equal(3, result.Count);
        Assert.All(result, Assert.Null);
    }

    [Fact]
    public async Task Vectors_come_back_unit_normalised()
    {
        // Truncating to 512 dimensions breaks the provider's normalisation, and similarity
        // downstream is a plain dot product that assumes it holds.
        var result = await Embedder(new FlakyGenerator(failures: 0, status: 0)).EmbedAsync(["a"]);

        var vector = Assert.IsType<float[]>(result[0]);

        Assert.Equal(1.0, Math.Sqrt(vector.Sum(v => (double)v * v)), precision: 5);
    }

    [Fact]
    public async Task An_empty_request_makes_no_call_at_all()
    {
        var generator = new FlakyGenerator(failures: 0, status: 0);

        Assert.Empty(await Embedder(generator).EmbedAsync([]));
        Assert.Equal(0, generator.Calls);
    }

    // -----------------------------------------------------------------------

    /// <summary>Fails the first <c>failures</c> calls with <c>status</c>, then answers.</summary>
    private sealed class FlakyGenerator(int failures, int status)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            if (Calls <= failures)
            {
                throw new ClientResultException($"HTTP {status}", new FakeResponse(status));
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select((_, i) => new Embedding<float>(Vector(i + 1))).ToList()));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Answers with fewer embeddings than were asked for.</summary>
    private sealed class ShortGenerator(int returns) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                Enumerable.Range(0, returns).Select(i => new Embedding<float>(Vector(i + 1))).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A clock whose delays return immediately, so the backoff costs no wall time.</summary>
    private sealed class InstantTime : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => base.CreateTimer(callback, state, TimeSpan.Zero, period);
    }

    /// <summary>
    /// The least response that can carry a status code.
    /// </summary>
    /// <remarks>
    /// Only <c>Status</c> is read - by <c>IsTransient</c> - and <c>Headers</c>, by the
    /// retry-after reader. Everything else on the abstraction exists for a real transport and is
    /// left to throw, so a test that starts depending on it fails loudly rather than quietly
    /// asserting against an invented body.
    /// </remarks>
    private sealed class FakeResponse(int status) : PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => string.Empty;

        public override BinaryData Content => BinaryData.FromString(string.Empty);

        public override Stream? ContentStream
        {
            get => null;
            set => throw new NotSupportedException();
        }

        protected override PipelineResponseHeaders HeadersCore { get; } = new NoHeaders();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default)
            => Content;

        public override ValueTask<BinaryData> BufferContentAsync(
            CancellationToken cancellationToken = default) => new(Content);

        public override void Dispose()
        {
        }

        private sealed class NoHeaders : PipelineResponseHeaders
        {
            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
                => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

            public override bool TryGetValue(string name, out string? value)
            {
                value = null;
                return false;
            }

            public override bool TryGetValues(string name, out IEnumerable<string>? values)
            {
                values = null;
                return false;
            }
        }
    }
}
