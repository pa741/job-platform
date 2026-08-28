using JobPlatform.Core.Matching;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The vector arithmetic and the storage format.
/// </summary>
/// <remarks>
/// The round trip is the one worth having. This blob is written by the Functions host and read
/// by the sweep, and a packing bug does not throw - it produces a plausible cosine against every
/// posting, which is a working-looking ranking built on noise.
/// </remarks>
public sealed class EmbeddingVectorTests
{
    private static float[] Full(Func<int, float> value)
        => [.. Enumerable.Range(0, EmbeddingVector.Dimensions).Select(value)];

    [Fact]
    public void Packing_round_trips_exactly()
    {
        // Exactly, not approximately: pack and unpack are both IEEE-754 single, so anything less
        // than bit equality is a bug in the byte order rather than a rounding artefact.
        var vector = Full(i => (float)Math.Sin(i * 0.017));

        var restored = EmbeddingVector.Unpack(EmbeddingVector.Pack(vector));

        Assert.Equal(vector, restored);
    }

    [Fact]
    public void Packing_is_four_bytes_per_dimension()
        => Assert.Equal(
            EmbeddingVector.ByteLength,
            EmbeddingVector.Pack(Full(_ => 1f)).Length);

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    // Not a whole number of floats. A truncated blob has to be recognisable, because the
    // alternative is a shorter vector that silently compares against nothing.
    [InlineData(new byte[] { 1, 2, 3 })]
    public void A_malformed_blob_unpacks_to_null(byte[]? bytes)
        => Assert.Null(EmbeddingVector.Unpack(bytes));

    [Fact]
    public void Normalising_produces_a_unit_vector()
    {
        var unit = EmbeddingVector.Normalise(Full(i => i % 7));

        Assert.NotNull(unit);
        Assert.Equal(1.0, Math.Sqrt(unit!.Sum(v => (double)v * v)), precision: 5);
    }

    [Fact]
    public void Normalising_preserves_direction()
    {
        // The whole point: a vector and the same vector scaled must be the same direction, so
        // cosine between them is one. This is what makes the truncated embedding safe to store.
        var a = EmbeddingVector.Normalise(Full(i => i % 5))!;
        var b = EmbeddingVector.Normalise(Full(i => (i % 5) * 3f))!;

        Assert.Equal(1.0, EmbeddingVector.Similarity(a, b)!.Value, precision: 5);
    }

    [Fact]
    public void A_zero_vector_normalises_to_null_rather_than_to_itself()
    {
        // A zero vector has a cosine of zero against everything, which reads as "unrelated to
        // every posting in the corpus" - a confident claim made from no evidence at all. Absent
        // is the honest answer, and the ranker drops the axis for it.
        Assert.Null(EmbeddingVector.Normalise(Full(_ => 0f)));
    }

    [Fact]
    public void A_vector_of_the_wrong_width_normalises_to_null()
        => Assert.Null(EmbeddingVector.Normalise([1f, 2f, 3f]));

    [Fact]
    public void Similarity_is_null_where_either_side_is_missing()
    {
        var vector = EmbeddingVector.Normalise(Full(i => i % 3));

        Assert.Null(EmbeddingVector.Similarity(vector, null));
        Assert.Null(EmbeddingVector.Similarity(null, vector));
        Assert.Null(EmbeddingVector.Similarity(null, null));
    }

    [Fact]
    public void Similarity_is_null_where_the_widths_disagree()
    {
        // Two vectors from different models, or one truncated in storage. Comparing them by
        // their shared prefix would produce a number on an incomparable scale that looks exactly
        // like a working one.
        Assert.Null(EmbeddingVector.Similarity(Full(_ => 1f), [1f, 1f]));
    }

    [Fact]
    public void Opposite_directions_come_back_as_minus_one()
    {
        var a = EmbeddingVector.Normalise(Full(i => i + 1f))!;
        var b = EmbeddingVector.Normalise(Full(i => -(i + 1f)))!;

        Assert.Equal(-1.0, EmbeddingVector.Similarity(a, b)!.Value, precision: 5);
    }

    [Fact]
    public void Similarity_never_escapes_its_range()
    {
        // Floating-point error can carry a dot product a hair past one, and the ranker min-maxes
        // over these values assuming they are bounded.
        var vector = EmbeddingVector.Normalise(Full(i => (float)Math.Cos(i)))!;

        var self = EmbeddingVector.Similarity(vector, vector)!.Value;

        Assert.InRange(self, -1.0, 1.0);
        Assert.Equal(1.0, self, precision: 5);
    }
}

/// <summary>
/// What actually gets embedded, on both sides.
/// </summary>
/// <remarks>
/// Worth its own tests because a cosine between two documents means nothing unless both were
/// built the same way, and this is the only file that decides how.
/// </remarks>
public sealed class EmbeddingTextTests
{
    [Fact]
    public void An_advert_leads_with_its_title()
    {
        var text = EmbeddingText.ForAdvert("Senior .NET Developer", "We are looking for...");

        Assert.StartsWith("Senior .NET Developer\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_survives_truncation()
    {
        // The densest sentence in the advert, so it leads and is never the part that gets cut.
        var text = EmbeddingText.ForAdvert("Platform Engineer", new string('x', 50_000));

        Assert.StartsWith("Platform Engineer", text, StringComparison.Ordinal);
        Assert.Equal("Platform Engineer".Length + 1 + EmbeddingText.MaxAdvertChars, text.Length);
    }

    [Fact]
    public void An_advert_with_no_body_is_still_its_title()
        => Assert.Equal("Data Engineer", EmbeddingText.ForAdvert("Data Engineer", null));

    [Fact]
    public void An_advert_with_no_title_is_still_its_body()
        => Assert.Equal("A role.", EmbeddingText.ForAdvert(null, "A role."));

    [Fact]
    public void A_profile_gets_twice_an_advert_length()
    {
        // Not the same limit, deliberately: an advert describes one role and a profile describes
        // a career, so one advert's worth would cut a candidate off in their second job.
        Assert.Equal(2 * EmbeddingText.MaxAdvertChars, EmbeddingText.MaxProfileChars);
        Assert.Equal(
            EmbeddingText.MaxProfileChars,
            EmbeddingText.ForProfile(new string('y', 50_000)).Length);
    }

    [Fact]
    public void An_empty_document_embeds_to_nothing_rather_than_to_whitespace()
    {
        Assert.Equal(string.Empty, EmbeddingText.ForProfile("   "));
        Assert.Equal(string.Empty, EmbeddingText.ForProfile(null));
        Assert.Equal(string.Empty, EmbeddingText.ForAdvert(null, null));
    }
}
