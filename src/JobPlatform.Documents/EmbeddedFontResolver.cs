using System.Collections.Concurrent;
using System.Reflection;
using PdfSharp.Fonts;

namespace JobPlatform.Documents;

/// <summary>
/// Supplies every font the PDF renderer draws with, from inside the assembly.
/// </summary>
/// <remarks>
/// <b>PDFsharp's platform-independent build resolves no fonts at all.</b> Not "falls back to a
/// default" - it throws <c>InvalidOperationException</c> on the first render, including for the
/// internal error font it wants before it has drawn anything. Any font name at all, "Arial"
/// included, fails identically. So a resolver is not a refinement here; without one this
/// library does not work, and the failure arrives at request time rather than at build time.
///
/// The alternative was installing fonts in the container and reading them off disk, which was
/// rejected: it makes the API depend on a Dockerfile line that nothing tests, it renders
/// differently on a developer's Windows machine than in production, and it turns a missing
/// package into a 500 on a candidate's CV download. Embedding is 580 KB and produces a
/// byte-identical document everywhere.
///
/// Roboto, under the SIL Open Font License 1.1 (see <c>Fonts/OFL.txt</c>), which permits
/// bundling inside software without restriction. The same licence check the concept vocabulary
/// got: redistribution has to be allowed outright, because this repository is public.
///
/// <b>Unknown families resolve rather than throw.</b> MigraDoc asks for "Courier New" for its
/// own internal error font whatever the document says, and a resolver that only answered for
/// names it recognised would fail on that alone. Anything unrecognised gets the sans face, so
/// a stylesheet naming a font nobody bundled produces a readable document rather than an
/// exception.
/// </remarks>
public sealed class EmbeddedFontResolver : IFontResolver
{
    /// <summary>The family name the renderer's styles ask for.</summary>
    public const string SansFamily = "Roboto";

    /// <summary>The family used for code spans and preformatted blocks.</summary>
    public const string MonoFamily = "Roboto Mono";

    /// <summary>
    /// Installed once per process.
    /// </summary>
    /// <remarks>
    /// <c>GlobalFontSettings.FontResolver</c> is process-wide static state that PDFsharp
    /// refuses to let you set twice, so this has to be idempotent and thread-safe rather than
    /// something a caller remembers to do first. Every entry point into rendering calls it.
    /// </remarks>
    public static void Install()
    {
        if (Volatile.Read(ref _installed))
        {
            return;
        }

        lock (Gate)
        {
            if (_installed)
            {
                return;
            }

            GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
            Volatile.Write(ref _installed, true);
        }
    }

    private static readonly Lock Gate = new();
    private static bool _installed;

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>Face name to the resource file behind it.</summary>
    private static readonly Dictionary<string, string> Faces = new(StringComparer.Ordinal)
    {
        ["Roboto#Regular"] = "Roboto-Regular.ttf",
        ["Roboto#Bold"] = "Roboto-Bold.ttf",
        ["Roboto#Italic"] = "Roboto-Italic.ttf",
        ["Roboto#BoldItalic"] = "Roboto-BoldItalic.ttf",
        ["RobotoMono#Regular"] = "RobotoMono-Regular.ttf",
    };

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Monospace is asked for by several names. All of them mean the same thing here, and
        // only one weight is bundled - bold code in a CV is not worth 80 KB.
        if (IsMonospace(familyName))
        {
            return new FontResolverInfo("RobotoMono#Regular", isBold, isItalic);
        }

        var style = (isBold, isItalic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular",
        };

        return new FontResolverInfo($"Roboto#{style}");
    }

    public byte[]? GetFont(string faceName)
        => Cache.GetOrAdd(faceName, Load);

    private static bool IsMonospace(string familyName)
        => familyName.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("Consolas", StringComparison.OrdinalIgnoreCase);

    private static byte[] Load(string faceName)
    {
        if (!Faces.TryGetValue(faceName, out var fileName))
        {
            // Only reachable if ResolveTypeface returned a name not in the table above, which
            // would be a bug in this file rather than in a caller.
            throw new InvalidOperationException($"No embedded font is bundled for face '{faceName}'.");
        }

        var assembly = typeof(EmbeddedFontResolver).Assembly;
        var resource = $"{assembly.GetName().Name}.Fonts.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"The embedded font resource '{resource}' is missing. Check the EmbeddedResource "
                + "item in JobPlatform.Documents.csproj.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }
}
