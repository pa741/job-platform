using System.Buffers.Binary;

namespace JobPlatform.Core.Matching;

/// <summary>
/// A dense text embedding: the arithmetic over it, and how it is stored.
/// </summary>
/// <remarks>
/// Pure and Azure-free, like <see cref="MatchScorer"/> and for the same reason - it is the part
/// worth pinning exactly, and it is pinnable exactly only while running it needs nothing but an
/// array.
///
/// <b>Vectors are unit-normalised on the way in, so similarity is a dot product.</b> The
/// provider returns normalised vectors for a full-width embedding, but this system asks for a
/// truncated one - Matryoshka representation learning means the first 512 of 1,536 dimensions
/// are still a usable embedding, at a third of the storage - and a truncated vector is not a
/// unit vector. Normalising here rather than trusting the provider makes that difference
/// impossible to get wrong, and costs one pass over 512 floats per posting per lifetime.
///
/// <b>The storage format is little-endian IEEE-754, written explicitly.</b> Not
/// <c>Buffer.BlockCopy</c>: this blob is read back by a different process from the one that
/// wrote it, and a format that happens to work because both ends share an architecture is a
/// format that silently corrupts the day one of them does not.
/// </remarks>
public static class EmbeddingVector
{
    /// <summary>
    /// How many dimensions of the provider's embedding are kept.
    /// </summary>
    /// <remarks>
    /// 512 of the model's 1,536. Measured rather than assumed: this is the width the profile
    /// against corpus experiment in HANDOFF 1.6 ran at, so it is the width the +0.521 describes.
    /// Changing it invalidates every stored vector, which is what
    /// <see cref="EmbeddingVersion"/> exists to say.
    /// </remarks>
    public const int Dimensions = 512;

    /// <summary>
    /// Bumped whenever a stored vector would come out different for the same text.
    /// </summary>
    /// <remarks>
    /// A change of model, of dimension, or of what text is fed in. The same staleness marker
    /// <c>EnrichedPosting.CurrentVersion</c> and <c>DocumentExtraction.CurrentVersion</c> carry:
    /// rows below it are stale, nothing has to be deleted, and the next pass rebuilds them.
    /// </remarks>
    public const int EmbeddingVersion = 1;

    public static int ByteLength => Dimensions * sizeof(float);

    /// <summary>
    /// Unit-normalises a copy of <paramref name="values"/>, or null where it cannot be one.
    /// </summary>
    /// <remarks>
    /// Null rather than a zero vector for a degenerate input. A zero vector has a cosine of zero
    /// against everything, which reads as "unrelated to every posting" - a confident claim made
    /// from no evidence. Absent is the honest answer, and the ranker drops the axis for it.
    /// </remarks>
    public static float[]? Normalise(IReadOnlyList<float>? values)
    {
        if (values is null || values.Count != Dimensions)
        {
            return null;
        }

        var norm = 0.0;

        for (var i = 0; i < values.Count; i++)
        {
            norm += (double)values[i] * values[i];
        }

        norm = Math.Sqrt(norm);

        if (norm <= double.Epsilon || double.IsNaN(norm) || double.IsInfinity(norm))
        {
            return null;
        }

        var unit = new float[Dimensions];

        for (var i = 0; i < unit.Length; i++)
        {
            unit[i] = (float)(values[i] / norm);
        }

        return unit;
    }

    /// <summary>
    /// Cosine similarity of two stored vectors, or null where either is missing or malformed.
    /// </summary>
    /// <remarks>
    /// A plain dot product, because both sides were normalised by <see cref="Normalise"/> before
    /// storage. Null rather than zero for a missing side, for the reason above: the ranker has
    /// to be able to tell "no evidence" from "no similarity", and a magic number cannot.
    /// </remarks>
    public static double? Similarity(float[]? left, float[]? right)
    {
        if (left is null || right is null || left.Length != right.Length || left.Length == 0)
        {
            return null;
        }

        var dot = 0.0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
        }

        // Floating-point error can carry a dot product a hair past one. Clamped so a caller
        // normalising over the pool cannot be handed a value outside the range it assumes.
        return double.IsNaN(dot) ? null : Math.Clamp(dot, -1.0, 1.0);
    }

    public static byte[] Pack(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var bytes = new byte[vector.Length * sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <summary>Reads a stored blob back, or null where it is not a whole vector.</summary>
    /// <remarks>
    /// Null rather than a throw. A malformed blob is a bug, but it is one whose blast radius
    /// should be a match that ranks without its embedding axis rather than a nightly sweep that
    /// dies part way through scoring every profile.
    /// </remarks>
    public static float[]? Unpack(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
        {
            return null;
        }

        var vector = new float[bytes.Length / sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }
}
