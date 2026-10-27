using System.Runtime.InteropServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A view of bytes that stay pinned and unchanged while worker threads read them. A span cannot cross threads, so the code that
/// owns the data pins it (<c>fixed</c>) for the whole parallel region and hands out this view; the length is the bound.
/// </summary>
internal readonly unsafe struct SharedSource
{
    private readonly byte* _pointer;
    private readonly int _length;

    public SharedSource(byte* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public int Length => _length;

    // The pointer is valid for _length bytes while the owner's fixed block lasts, which encloses every use.
    public ReadOnlySpan<byte> Span => new(_pointer, _length);
}

/// <summary>
/// Compresses one job of a multithreaded frame: a stretch of the input cut at a fixed size, parsed and coded into blocks on its
/// own so that jobs can run side by side and be concatenated in order. A job starts with no entropy tables to repeat and no
/// repeat offsets (the decoder's come from the previous job), and sees the bytes before it as history.
/// </summary>
internal sealed class ZstdJobCompressor
{
    private const int BlockSize = ZstdFormat.BlockSizeMax;

    private readonly ZstdMatchFinder?[] _finders = new ZstdMatchFinder?[10];
    private readonly ZstdBlockWriter _writer = new();
    private readonly ZstdSequenceStore _store = new();
    private readonly List<ZstdLongMatch> _long = [];
    private readonly ZstdLongCheckpoints _checkpoints = new();
    private byte[] _output = [];
    private int _outputLength;

    /// <summary>Takes the compressed blocks of the last job, and with them the buffer: the next job starts with a new or lent one.</summary>
    public byte[] DetachOutput(out int length)
    {
        byte[] output = _output;
        length = _outputLength;
        _output = [];
        _outputLength = 0;
        return output;
    }

    /// <summary>
    /// Finds the long matches of the job's blocks, in the blocks' order, from the checkpoints <see cref="ScanLongMatches"/> found. The
    /// matcher is shared by all jobs and must see the data in order, so the caller serializes these calls by job.
    /// </summary>
    public void FindLongMatches(ZstdLongDistanceMatcher matcher, ReadOnlySpan<byte> data, int jobStart, int jobEnd)
    {
        _long.Clear();
        int next = 0;
        for (int blockStart = jobStart; blockStart < jobEnd; blockStart += BlockSize)
        {
            matcher.FindMatches(data, blockStart, Math.Min(blockStart + BlockSize, jobEnd), _checkpoints, ref next, _long);
        }
    }

    /// <summary>Finds the checkpoints of the job's bytes. Any number of jobs may do this at once; <see cref="FindLongMatches"/> follows, in job order.</summary>
    public void ScanLongMatches(ZstdLongDistanceMatcher matcher, ReadOnlySpan<byte> data, int jobStart, int jobEnd) =>
        matcher.Scan(data, jobStart, jobEnd, _checkpoints);

    /// <summary>
    /// Compresses <c>data[jobStart..jobEnd]</c> into blocks, which <see cref="DetachOutput"/> then hands over.
    /// </summary>
    /// <param name="data">The input; <c>data[historyStart..jobStart]</c> is history the job may match into.</param>
    /// <param name="jobStart">Index of the job's first byte.</param>
    /// <param name="jobEnd">Index just past the job's last byte.</param>
    /// <param name="historyStart">Index of the first byte of history the job may reach.</param>
    /// <param name="primeStart">Index of the first history byte given to the finder's tables (at least <paramref name="historyStart"/>).</param>
    /// <param name="parameters">The match-finding parameters of the frame.</param>
    /// <param name="dictionary">The frame's dictionary, used only by a job that starts the frame.</param>
    /// <param name="startsFrame">Whether the job starts the frame (it then uses the dictionary and the initial repeat offsets).</param>
    /// <param name="endsFrame">Whether the job's last block ends the frame.</param>
    /// <param name="useLongMatches">Whether <see cref="FindLongMatches"/> ran for this job.</param>
    /// <param name="lent">A buffer to write the blocks to, if it is large enough; otherwise one is made.</param>
    public void Compress(
        ReadOnlySpan<byte> data,
        int jobStart,
        int jobEnd,
        int historyStart,
        int primeStart,
        in ZstdCompressionParameters parameters,
        ZstdCompressionDictionary? dictionary,
        bool startsFrame,
        bool endsFrame,
        bool useLongMatches,
        byte[]? lent = null)
    {
        ReadOnlySpan<byte> window = data[historyStart..jobEnd];
        int start = jobStart - historyStart;
        int length = jobEnd - jobStart;
        int blocks = (length + BlockSize - 1) / BlockSize;
        int capacity = length + (3 * blocks) + ZstdBlockWriter.DestinationSlack + 64;
        if (lent is not null && lent.Length >= capacity)
        {
            _output = lent;
        }
        else if (_output.Length < capacity)
        {
            _output = new byte[capacity];
        }

        ZstdMatchFinder finder = _finders[(int)parameters.Strategy] ??= ZstdMatchFinder.Create(parameters.Strategy);
        ZstdCompressionDictionary? used = startsFrame ? dictionary : null;
        finder.BeginFrame(parameters, used);
        _writer.BeginFrame(used, parameters.CompressLiterals);
        if (!startsFrame)
        {
            _writer.ForgetRepeatOffsets();
            finder.Prime(window, primeStart - historyStart, start);
        }

        // The long matches were found in the data's own indexes; the window starts at historyStart.
        Span<ZstdLongMatch> matches = default;
        if (useLongMatches)
        {
            matches = CollectionsMarshal.AsSpan(_long);
            for (int i = 0; i < matches.Length; i++)
            {
                matches[i] = new ZstdLongMatch(matches[i].Start - historyStart, matches[i].Length, matches[i].Offset);
            }
        }

        long windowStartPosition = startsFrame ? 0 : historyStart;
        int next = 0;
        int p = 0;
        for (int blockStart = start; blockStart < start + length; blockStart += BlockSize)
        {
            int blockEnd = Math.Min(blockStart + BlockSize, start + length);
            bool last = endsFrame && blockEnd == window.Length;
            ReadOnlySpan<byte> block = window[blockStart..blockEnd];
            Span<byte> target = _output.AsSpan(p);
            int first = next;
            while (next < matches.Length && matches[next].Start < blockEnd)
            {
                next++;
            }

            int size;
            if (blockEnd - blockStart > 1 && block.IndexOfAnyExcept(block[0]) < 0)
            {
                size = ZstdBlockWriter.WriteRleBlock(block[0], block.Length, last, target);
            }
            else
            {
                _store.Clear();
                ZstdLongSequencer.Fill(finder, window, blockStart, blockEnd, windowStartPosition, _writer.RepeatOffsets, matches[first..next], _store);
                size = _writer.WriteBlock(block, _store, last, target);
            }

            p += size;
        }

        _outputLength = p;
    }
}
