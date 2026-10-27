namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Options for compressing to Zstandard. The same options always give the same bytes, on every platform and thread count.
/// </summary>
public sealed class ZstdCompressionOptions
{
    /// <summary>Gets the shared instance with every option at its default.</summary>
    public static ZstdCompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the compression level, numbered as the <c>zstd</c> command line tool numbers them: 1 to 15 are implemented here (levels 13 to 15 of small sources use the strategy of level 12 until the optimal parsers exist), 0 means
    /// the default (3), and negative levels (-1 and below) trade ratio for speed. The default is 3. Other levels throw
    /// <see cref="ArgumentOutOfRangeException"/> when compression starts.
    /// </summary>
    public int Level { get; init; } = 3;

    /// <summary>
    /// Gets a value indicating whether the frame ends with a 32-bit content checksum (XXH64, low half) that decoders verify.
    /// The default is false, as in the C library's one-shot call; the checksum costs 4 bytes per frame.
    /// </summary>
    public bool Checksum { get; init; }

    /// <summary>
    /// Gets a value indicating whether the frame header states the content size when it is known (one-shot calls, or a size
    /// given in <see cref="ContentSize"/>). The default is true. With the size stated, small frames also drop the window
    /// descriptor.
    /// </summary>
    public bool WriteContentSize { get; init; } = true;

    /// <summary>
    /// Gets the total number of bytes that a streaming encoder will receive for a frame, when the caller knows it. It sizes the
    /// window and tables as a one-shot call would, and is written into the header. The encoder throws
    /// <see cref="InvalidOperationException"/> if the frame ends with another count. The default is null (unknown).
    /// </summary>
    public long? ContentSize { get; init; }

    /// <summary>Gets the dictionary to compress with, or <see langword="null"/> for none. The decoder must be given the same dictionary.</summary>
    public ZstdCompressionDictionary? Dictionary { get; init; }

    /// <summary>
    /// Gets a value indicating whether the dictionary's ID is written into the frame header (when the dictionary has one).
    /// The default is true; with false the decoder must be told which dictionary to use.
    /// </summary>
    public bool WriteDictionaryId { get; init; } = true;

    /// <summary>
    /// Gets the window log to use, from 10 to 31, or 0 (the default) to take it from the level and the content size (27 when
    /// <see cref="LongDistanceMatching"/> is on). A larger window lets matches reach further back and makes decoders hold more
    /// memory: the decoder's default limit is 8 MiB (<see cref="ZstdDecompressionOptions.MaxWindowSize"/>), and <c>zstd -d</c> takes
    /// up to 2^27 without being told (<c>--long=31</c> or <c>--memory</c> raise it). The header states the log asked for, but
    /// matches never reach further than 2^30 bytes.
    /// </summary>
    public int WindowLog { get; init; }

    /// <summary>
    /// Gets a value indicating whether long-distance matching is on (<c>zstd --long</c>): a second matcher finds long repeats anywhere in
    /// the window and the level's own match finder parses the rest. It pays off on data with far repeats (archives of similar files,
    /// snapshots) and costs a little time elsewhere. The default is false. With it on and <see cref="WindowLog"/> 0 the window is 2^27
    /// (smaller when the content size is known to be smaller), so decoders must allow a window that large. The dictionary's content
    /// is not searched by the long-distance matcher.
    /// </summary>
    public bool LongDistanceMatching { get; init; }

    /// <summary>Gets the log of the long-distance matcher's table size (6 to 26 entries of 8 bytes), or 0 (the default) for the window log minus 7, at most 24.</summary>
    public int LongDistanceHashLog { get; init; }

    /// <summary>Gets the shortest long match to report, 16 to 4096, or 0 (the default) for 64.</summary>
    public int LongDistanceMinMatch { get; init; }

    /// <summary>Gets the log of the number of remembered positions per hash bucket, 1 to 4, or 0 (the default) for 2.</summary>
    public int LongDistanceBucketSizeLog { get; init; }

    /// <summary>Gets the log of the sampling rate of the long-distance matcher, 1 to 24 (about one position in 2^n is a checkpoint), or 0 (the default) for the window log minus the table log.</summary>
    public int LongDistanceHashRateLog { get; init; }

    /// <summary>The value of <see cref="JobSize"/> that asks for the default job size.</summary>
    public const int AutoJobSize = -1;

    /// <summary>
    /// Gets the size in bytes of the jobs the input is cut into for multithreaded compression, or 0 (the default) for no cutting: the
    /// input is one job and <see cref="Workers"/> has no effect. <see cref="AutoJobSize"/> picks <c>max(1 MiB, 4 * min(window size, 4 MiB))</c>.
    /// A size is rounded up to a multiple of 128 KiB (the block size), at most 1 GiB. The jobs, and so the compressed bytes, depend on
    /// this and on <see cref="OverlapSize"/> and never on <see cref="Workers"/>. Jobs are compressed independently and written one
    /// after another into one frame, so each costs a little ratio (it cannot repeat the previous job's entropy tables or repeat
    /// offsets) in exchange for parallelism.
    /// </summary>
    public int JobSize { get; init; }

    /// <summary>
    /// Gets the number of threads that compress jobs at once, 1 by default; 0 means one per processor. It changes speed and memory,
    /// never the output. It has an effect only when <see cref="JobSize"/> is not 0, and there is nothing to run in parallel for an
    /// input of one job. In a browser, jobs run one after another.
    /// </summary>
    public int Workers { get; init; } = 1;

    /// <summary>
    /// Gets how many bytes before a job are given to the match finder as history it can match into, or -1 (the default) for the
    /// window size divided by 8 up to 1 MiB; 0 for none. Larger values recover ratio at some cost in time. Capped at the window size.
    /// With <see cref="LongDistanceMatching"/> the long matcher always sees the whole window before a job.
    /// </summary>
    public int OverlapSize { get; init; } = -1;
}
