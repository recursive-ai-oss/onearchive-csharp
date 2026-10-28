using System.Diagnostics.CodeAnalysis;
namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// Content that LZ4 data was compressed against, used as the history for the first matches of a block or frame
/// (Frame format, "Dictionary ID"). Only the last 64 KiB can be referenced, so only that much is kept.
/// </summary>
/// <remarks>Instances are immutable and may be shared between decoders and threads.</remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is the LZ4 format's own term; the type is not a collection.")]
[Experimental("ONECOMPRESS002", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Lz4Dictionary
{
    /// <summary>The most dictionary content a match can reach: 64 KiB.</summary>
    public const int MaxLength = 64 * 1024;

    private readonly byte[] _content;

    /// <summary>Creates a dictionary from raw content.</summary>
    /// <param name="content">The dictionary bytes; when longer than <see cref="MaxLength"/>, only the last <see cref="MaxLength"/> bytes are kept.</param>
    /// <param name="id">
    /// The dictionary ID a frame names to select this dictionary. Zero (the default) means the dictionary has no ID and
    /// serves every frame that does not name another dictionary.
    /// </param>
    public Lz4Dictionary(ReadOnlySpan<byte> content, uint id = 0)
    {
        _content = content.Length > MaxLength ? content[^MaxLength..].ToArray() : content.ToArray();
        Id = id;
    }

    /// <summary>Gets the dictionary ID, or zero when it has none.</summary>
    public uint Id { get; }

    /// <summary>Gets the number of bytes kept, at most <see cref="MaxLength"/>.</summary>
    public int Length => _content.Length;

    /// <summary>Gets the kept bytes.</summary>
    public ReadOnlySpan<byte> Content => _content;
}
