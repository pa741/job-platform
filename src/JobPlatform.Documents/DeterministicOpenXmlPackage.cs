using System.IO.Compression;

namespace JobPlatform.Documents;

/// <summary>
/// Rewrites a freshly written OOXML package so that identical input produces identical bytes.
/// </summary>
/// <remarks>
/// <b>A .docx is a ZIP, and a ZIP records when it was made.</b> Every entry carries a modified
/// timestamp, and <see cref="ZipArchive"/> fills that in from the clock, so two renders of the
/// same CV a second apart differ in bytes while being the same document. That is not a cosmetic
/// problem here: the generated pack is hashed and the hash is stored beside it, so "did the
/// document change" has to mean "did the words change" and not "was it rendered twice". Without
/// this pass every regeneration looks like an edit.
///
/// So the package is unzipped in memory and written again with one fixed timestamp on every
/// entry. The date is the earliest a ZIP can express - the format stores DOS date-time, whose
/// epoch is 1 January 1980, and anything earlier is rejected by the writer. Its offset is
/// explicitly UTC so the stamp does not move with the machine's time zone; a build in London and
/// a build in a container set to UTC have to agree.
///
/// <b>Entry order is preserved, with one exception.</b> The Open Packaging Conventions require
/// the content-type stream to be the first item in the archive, and permissive readers have made
/// that easy to forget; it is put first explicitly rather than trusted to come out of the SDK
/// that way. Everything else keeps the order the SDK wrote it in, which is itself deterministic:
/// parts are written in creation order, and the renderer creates them in a fixed sequence.
///
/// What this pass deliberately does <i>not</i> do is touch part content. Timestamps inside the
/// XML - the created and modified dates in the core properties - are not stripped here, they are
/// never written in the first place; the renderer writes that part by hand for exactly that
/// reason. A canonicaliser that edited XML would be a second, weaker place where the document's
/// meaning could change.
/// </remarks>
internal static class DeterministicOpenXmlPackage
{
    /// <summary>The OPC content-type stream, which has to be the archive's first entry.</summary>
    private const string ContentTypes = "[Content_Types].xml";

    /// <summary>
    /// The ZIP format's zero. Every entry gets this, so the bytes carry no wall clock at all.
    /// </summary>
    private static readonly DateTimeOffset Epoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Canonicalise(byte[] package)
    {
        var entries = new List<(string Name, byte[] Content)>();

        using (var source = new MemoryStream(package, writable: false))
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                using var content = entry.Open();
                using var buffer = new MemoryStream();

                content.CopyTo(buffer);
                entries.Add((entry.FullName, buffer.ToArray()));
            }
        }

        // A stable sort on one key: the content types stream leads, everything else holds its
        // position. List.Sort is unstable, hence the index rather than a comparison on names -
        // sorting the parts alphabetically would also be deterministic, but it would reorder a
        // package for no reason and make a diff against a Word-written file harder to read.
        var ordered = entries
            .Select((entry, index) => (entry, index))
            .OrderBy(pair => pair.entry.Name == ContentTypes ? 0 : 1)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.entry);

        using var output = new MemoryStream();

        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in ordered)
            {
                var written = target.CreateEntry(name, CompressionLevel.Optimal);
                written.LastWriteTime = Epoch;

                using var stream = written.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return output.ToArray();
    }
}
