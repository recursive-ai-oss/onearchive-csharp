using System.Diagnostics.CodeAnalysis;
namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// Content that LZ4 data was compressed against, used as the history for the first matches of a block or frame
/// (Frame format, "Dictionary ID"). Only the last 64 KiB can be referenced, so only that much is kept.
/// </summary>
/// <remarks>
/// <para>Instances are immutable and may be shared between decoders and threads.</para>
/// <para>LZ4 dictionaries are plain content. The content of a Zstandard-trained dictionary works as is (<see cref="Train"/> does exactly
/// that); its entropy tables are of no use to LZ4 and are dropped.</para>
/// </remarks>
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

    /// <summary>
    /// Trains a dictionary from sample payloads with the Zstandard trainer (COVER family), and keeps its content, which is what LZ4 uses.
    /// Samples of the kind that will be compressed, a few thousand of them, work best. The result depends only on the samples and the options.
    /// </summary>
    /// <param name="samples">The training samples; empty samples are ignored.</param>
    /// <param name="options">The training options; <see langword="null"/> trains a dictionary of <see cref="MaxLength"/> bytes with the other options at their defaults. A larger <c>DictionarySize</c> is allowed, but only the last <see cref="MaxLength"/> bytes are kept.</param>
    /// <param name="id">The dictionary ID a frame names to select this dictionary, or zero for none.</param>
    /// <param name="cancellationToken">Cancels a long search.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    /// <exception cref="CompressionDictionaryException">The samples hold fewer than 8 bytes in total.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Lz4Dictionary Train(IReadOnlyList<ReadOnlyMemory<byte>> samples, Zstd.ZstdDictionaryTrainingOptions? options = null, uint id = 0, CancellationToken cancellationToken = default)
    {
        options ??= new Zstd.ZstdDictionaryTrainingOptions { DictionarySize = MaxLength };
        Zstd.ZstdCompressionDictionary trained = Zstd.ZstdCompressionDictionary.Train(samples, options, cancellationToken);
        return new Lz4Dictionary(trained.Content.Span, id);
    }

    /// <summary>Gets the dictionary ID, or zero when it has none.</summary>
    public uint Id { get; }

    /// <summary>Gets the number of bytes kept, at most <see cref="MaxLength"/>.</summary>
    public int Length => _content.Length;

    /// <summary>Gets the kept bytes.</summary>
    public ReadOnlySpan<byte> Content => _content;
}
