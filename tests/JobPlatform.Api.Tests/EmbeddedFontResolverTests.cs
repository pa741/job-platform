using System.Buffers.Binary;
using System.Text;
using JobPlatform.Documents;
using Xunit;

namespace JobPlatform.Api.Tests;

/// <summary>
/// The bytes behind each face name are the face that name promises.
/// </summary>
/// <remarks>
/// <b>Written because all four were wrong in production and nothing could see it.</b>
/// <c>Roboto-Regular.ttf</c> held Roboto <i>Bold</i>, <c>Roboto-Bold.ttf</c> held Regular, and the
/// italic pair was swapped the same way. The resolver maps a face to a <i>file name</i>
/// (<c>Faces</c>), so every CV this system has rendered set its body text in bold and its bold
/// headings in regular - and every existing test passed, because a PDF containing the wrong
/// weight is a valid PDF of the right length with the right words in it.
///
/// <b>Nothing downstream can catch this.</b> PDFsharp asks for a face and embeds whatever bytes
/// come back; MigraDoc's layout then measures the wrong metrics, so the error is self-consistent -
/// the document is correctly typeset in the wrong font. It moves line width by about 1.9%, which
/// is roughly two characters on a 105-character line, so it silently moves where a CV breaks
/// across pages. The DOCX path is unaffected, since it names a family and embeds nothing, which is
/// precisely why a comparison between the two would not have found it either.
///
/// So the assertion is made against the font's own <c>name</c> table rather than against a
/// filename, a length or a hash: the only authority on what a face is, is the face.
/// </remarks>
public class EmbeddedFontResolverTests
{
    [Theory]
    [InlineData("Roboto#Regular", "Roboto Regular")]
    [InlineData("Roboto#Bold", "Roboto Bold")]
    [InlineData("Roboto#Italic", "Roboto Italic")]
    [InlineData("Roboto#BoldItalic", "Roboto Bold Italic")]
    [InlineData("RobotoMono#Regular", "Roboto Mono Regular")]
    public void The_bytes_behind_a_face_are_that_face(string faceName, string expected)
    {
        var bytes = new EmbeddedFontResolver().GetFont(faceName);

        Assert.NotNull(bytes);
        Assert.Equal(expected, FullName(bytes));
    }

    /// <summary>Every style the renderer can ask for resolves to bytes.</summary>
    /// <remarks>
    /// The resolver answers for any family name, so a missing resource would surface as a null
    /// far from here - inside PDFsharp, on the first render of somebody's CV.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Every_style_resolves(bool bold, bool italic)
    {
        var resolver = new EmbeddedFontResolver();
        var info = resolver.ResolveTypeface(EmbeddedFontResolver.SansFamily, bold, italic);

        Assert.NotNull(info);
        Assert.NotNull(resolver.GetFont(info.FaceName));
    }

    /// <summary>Name id 4 - the full human name - out of a TrueType <c>name</c> table.</summary>
    private static string FullName(byte[] font)
    {
        var tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        var offset = -1;

        for (var i = 0; i < tables; i++)
        {
            var record = 12 + (i * 16);

            if (Encoding.ASCII.GetString(font, record, 4) == "name")
            {
                offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
                break;
            }
        }

        Assert.True(offset >= 0, "the font carries no name table");

        var count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(offset + 2));
        var strings = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(offset + 4));

        for (var i = 0; i < count; i++)
        {
            var record = offset + 6 + (i * 12);
            var platform = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record));
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 6));
            var length = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 8));
            var start = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 10));

            if (nameId != 4)
            {
                continue;
            }

            var value = font.AsSpan(offset + strings + start, length);

            return platform == 3
                ? Encoding.BigEndianUnicode.GetString(value)
                : Encoding.ASCII.GetString(value);
        }

        return string.Empty;
    }
}
