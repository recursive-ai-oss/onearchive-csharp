using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Zstd;

// Long-distance matching and multithreaded compression for the encoder. See docs/zstd-encoder.md, "Long-distance matching" and
// "Jobs and threads", for the rules; this file is their implementation.
public sealed partial class ZstdEncoder
{
    // Matches never reach back further than this, so a window buffer fits an array whatever window log the header states.
    private const int MaxMatchWindowLog = 30;

    private ZstdLongDistanceMatcher? _ldm;
    private readonly List<ZstdLongMatch> _ldmMatches = [];
    private int _headerWindowLog;
    private readonly List<ZstdJobCompressor> _jobWorkers = [];

    // Streaming with jobs: the buffer holds the history jobs may reach, then input that has not been compressed yet.
    private bool _mtActive;
    private byte[] _mtBuffer = [];
    private int _mtFill;
    private int _mtDone;
    private long _mtDonePosition;
    private int _mtBatch;
    private int _mtHistory;
    private int _mtJobSize;
    private ZstdCompressionParameters _mtParameters;

    private void ValidateParallelOptions()
    {
        ZstdCompressionOptions o = _options;
        ArgumentOutOfRangeException.ThrowIfNegative(o.Workers, "Workers");
        ArgumentOutOfRangeException.ThrowIfLessThan(o.JobSize, ZstdCompressionOptions.AutoJobSize, "JobSize");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(o.JobSize, 1 << 30, "JobSize");
        ArgumentOutOfRangeException.ThrowIfLessThan(o.OverlapSize, -1, "OverlapSize");
        if (o.JobSize > 0 && o.JobSize < ZstdFormat.BlockSizeMax)
        {
            // Rounded up to a block, as documented; nothing to reject.
        }

        // Rejects the long-distance values that do not exist, now rather than when the first frame starts.
        _ = ZstdLongDistanceParameters.Resolve(ZstdLongDistanceParameters.DefaultWindowLog, ZstdLongDistanceParameters.DefaultWindowLog, o.LongDistanceHashLog, o.LongDistanceMinMatch, o.LongDistanceBucketSizeLog, o.LongDistanceHashRateLog);
    }

    private void ResetParallel()
    {
        _mtActive = false;
        _mtFill = 0;
        _mtDone = 0;
        _mtDonePosition = 0;
    }

    private static int CeilLog2(long value) => value <= 1 ? 0 : BitOperations.Log2((ulong)(value - 1)) + 1;

    private static int DefaultLongWindowLog(long contentSize, int dictionarySize)
    {
        int log = ZstdLongDistanceParameters.DefaultWindowLog;
        if (contentSize >= 0)
        {
            log = Math.Min(log, Math.Max(CeilLog2(contentSize + dictionarySize), ZstdCompressionParameters.MinWindowLog));
        }

        return log;
    }

    // Starts the long-distance matcher for a frame, or drops it when the option is off. Needs _parameters from ResolveParameters.
    private void BeginLongDistance(long contentSize)
    {
        if (!_options.LongDistanceMatching)
        {
            _ldm = null;
            return;
        }

        int windowLog = _parameters.WindowLog;
        int sizeLog = contentSize >= 0 ? Math.Min(windowLog, Math.Max(CeilLog2(contentSize), ZstdCompressionParameters.MinWindowLog)) : windowLog;
        ZstdLongDistanceParameters parameters = ZstdLongDistanceParameters.Resolve(
            windowLog,
            sizeLog,
            _options.LongDistanceHashLog,
            _options.LongDistanceMinMatch,
            _options.LongDistanceBucketSizeLog,
            _options.LongDistanceHashRateLog);
        _ldm ??= new ZstdLongDistanceMatcher();
        _ldm.BeginFrame(parameters);
        _ldmMatches.Clear();
    }

    // The job size in bytes for these parameters: a multiple of the block size, so that jobs add no blocks of their own and
    // GetMaxCompressedLength still bounds the frame. 0 when jobs are off.
    private int ResolveJobSize(in ZstdCompressionParameters parameters)
    {
        int requested = _options.JobSize;
        if (requested == 0)
        {
            return 0;
        }

        long size = requested == ZstdCompressionOptions.AutoJobSize ? Math.Max(1L << 20, 4 * Math.Min(parameters.WindowSize, 1L << 22)) : requested;
        size = (size + ZstdFormat.BlockSizeMax - 1) / ZstdFormat.BlockSizeMax * ZstdFormat.BlockSizeMax;
        return (int)Math.Min(size, 1L << 30);
    }

    private int ResolveOverlap(in ZstdCompressionParameters parameters)
    {
        int requested = _options.OverlapSize;
        return requested < 0 ? (int)Math.Min(parameters.WindowSize >> 3, 1 << 20) : (int)Math.Min(requested, parameters.WindowSize);
    }

    // How far before a job the data must stay available: the whole window for the long-distance matcher, else the overlap.
    private int ResolveHistory(in ZstdCompressionParameters parameters) =>
        _ldm is not null ? (int)Math.Min(parameters.WindowSize, 1 << 30) : ResolveOverlap(parameters);

    // Only the speed depends on this, never the bytes.
    private int ResolveWorkers() =>
        OperatingSystem.IsBrowser() ? 1 : _options.Workers == 0 ? Environment.ProcessorCount : _options.Workers;

    // ---- Jobs ----

    // One-shot: the jobs of source (more than one) are compressed and their blocks appended to destination at p.
    private unsafe bool TryCompressJobs(ReadOnlySpan<byte> source, in ZstdCompressionParameters parameters, int jobSize, Span<byte> destination, ref int p)
    {
        // The pins last until every worker is done, which RunJobs guarantees before it returns.
        fixed (byte* input = &MemoryMarshal.GetReference(source))
        fixed (byte* output = &MemoryMarshal.GetReference(destination))
        {
            var sink = new OutputSink(output + p, destination.Length - p);
            RunJobs(new SharedSource(input, source.Length), 0, source.Length, startsFrame: true, endsFrame: true, jobSize, parameters, sink);
            if (sink.Overflowed)
            {
                return false;
            }

            p += sink.Position;
            return true;
        }
    }

    // Compresses data[start..end] as jobs of jobSize (the last may be shorter) and returns each job's blocks in order. data[0..start]
    // is history. The long-distance matcher, when on, must be positioned at start. The jobs' blocks go to the sink in job order, as
    // soon as each job and all before it are done.
    private void RunJobs(SharedSource data, int start, int end, bool startsFrame, bool endsFrame, int jobSize, in ZstdCompressionParameters parameters, OutputSink sink)
    {
        int count = (int)(((long)(end - start) + jobSize - 1) / jobSize);
        var run = new JobRun(data, start, end, jobSize, count, startsFrame, endsFrame, ResolveOverlap(parameters), ResolveHistory(parameters), parameters, _options.Dictionary, _ldm, sink);
        int threads = Math.Min(ResolveWorkers(), count);
        while (_jobWorkers.Count < threads)
        {
            _jobWorkers.Add(new ZstdJobCompressor());
        }

        if (threads <= 1)
        {
            run.Work(_jobWorkers[0]);
        }
        else
        {
            var tasks = new Task[threads - 1];
            for (int t = 1; t < threads; t++)
            {
                ZstdJobCompressor worker = _jobWorkers[t];
                tasks[t - 1] = Task.Factory.StartNew(() => run.Work(worker), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            run.Work(_jobWorkers[0]);
            Task.WaitAll(tasks);
        }

        if (run.Error is { } error)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    // Receives the compressed jobs, in order, into memory the caller has pinned.
    private sealed unsafe class OutputSink
    {
        private readonly byte* _pointer;
        private readonly int _capacity;

        public OutputSink(byte* pointer, int capacity)
        {
            _pointer = pointer;
            _capacity = capacity;
        }

        public int Position { get; private set; }

        public bool Overflowed { get; private set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            // The destination holds _capacity bytes while the owner's fixed block lasts.
            if (Overflowed || data.Length > _capacity - Position)
            {
                Overflowed = true;
                return;
            }

            data.CopyTo(new Span<byte>(_pointer + Position, data.Length));
            Position += data.Length;
        }
    }

    private sealed class JobRun
    {
        private readonly SharedSource _data;
        private readonly int _start;
        private readonly int _end;
        private readonly int _jobSize;
        private readonly int _count;
        private readonly bool _startsFrame;
        private readonly bool _endsFrame;
        private readonly int _overlap;
        private readonly int _history;
        private readonly ZstdCompressionParameters _parameters;
        private readonly ZstdCompressionDictionary? _dictionary;
        private readonly ZstdLongDistanceMatcher? _ldm;
        private readonly OutputSink _sink;
        private readonly object _gate = new();
        private readonly Stack<byte[]> _pool = new();
        private readonly byte[]?[] _done;
        private readonly int[] _doneLength;
        private int _emit;
        private int _next;
        private int _turn;
        private bool _failed;

        public JobRun(
            SharedSource data,
            int start,
            int end,
            int jobSize,
            int count,
            bool startsFrame,
            bool endsFrame,
            int overlap,
            int history,
            ZstdCompressionParameters parameters,
            ZstdCompressionDictionary? dictionary,
            ZstdLongDistanceMatcher? ldm,
            OutputSink sink)
        {
            _data = data;
            _start = start;
            _end = end;
            _jobSize = jobSize;
            _count = count;
            _startsFrame = startsFrame;
            _endsFrame = endsFrame;
            _overlap = overlap;
            _history = history;
            _parameters = parameters;
            _dictionary = dictionary;
            _ldm = ldm;
            _sink = sink;
            _done = new byte[]?[count];
            _doneLength = new int[count];
        }

        public Exception? Error { get; private set; }

        // Jobs are taken in order, so the job before k has always been taken by a running thread when k waits for its turn at the
        // long-distance matcher, which sees the data in order whichever thread compresses it.
        public void Work(ZstdJobCompressor worker)
        {
            while (true)
            {
                int k = Interlocked.Increment(ref _next) - 1;
                if (k >= _count)
                {
                    return;
                }

                try
                {
                    int jobStart = _start + (k * _jobSize);
                    int jobEnd = Math.Min(jobStart + _jobSize, _end);
                    int historyStart = jobStart - Math.Min(jobStart, _history);
                    int primeStart = jobStart - Math.Min(jobStart, _overlap);
                    ReadOnlySpan<byte> data = _data.Span;
                    if (_ldm is not null)
                    {
                        worker.ScanLongMatches(_ldm, data, jobStart, jobEnd);
                        lock (_gate)
                        {
                            while (_turn != k && !_failed)
                            {
                                Monitor.Wait(_gate);
                            }

                            if (_failed)
                            {
                                return;
                            }
                        }

                        worker.FindLongMatches(_ldm, data, jobStart, jobEnd);
                        lock (_gate)
                        {
                            _turn = k + 1;
                            Monitor.PulseAll(_gate);
                        }
                    }

                    byte[]? lent = null;
                    lock (_gate)
                    {
                        if (_pool.Count > 0)
                        {
                            lent = _pool.Pop();
                        }
                    }

                    worker.Compress(data, jobStart, jobEnd, historyStart, primeStart, _parameters, _dictionary, _startsFrame && k == 0, _endsFrame && k == _count - 1, _ldm is not null, lent);
                    byte[] blocks = worker.DetachOutput(out int length);
                    lock (_gate)
                    {
                        _done[k] = blocks;
                        _doneLength[k] = length;
                        while (_emit < _count && _done[_emit] is { } ready)
                        {
                            _sink.Write(ready.AsSpan(0, _doneLength[_emit]));
                            _pool.Push(ready);
                            _done[_emit] = null;
                            _emit++;
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (_gate)
                    {
                        Error ??= e;
                        _failed = true;
                        Monitor.PulseAll(_gate);
                    }

                    return;
                }
            }
        }
    }

    // ---- Streaming with jobs ----

    private OperationStatus CompressJobs(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock)
    {
        consumed = 0;
        written = 0;
        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (_finished)
            {
                return OperationStatus.Done;
            }

            if (!_started)
            {
                StartFrameJobs();
                continue;
            }

            // A batch runs only when input remains after it, so that the frame's last block is always written by the final call
            // and the bytes do not depend on how the input was split across calls.
            int pending = _mtFill - _mtDone;
            bool inputLeft = consumed < source.Length;
            if (pending > _mtBatch || (isFinalBlock && !inputLeft && pending > 0))
            {
                bool last = isFinalBlock && !inputLeft && pending <= _mtBatch;
                RunBatch(Math.Min(pending, _mtBatch), last);
                continue;
            }

            if (inputLeft)
            {
                int take = Math.Min(_mtBuffer.Length - _mtFill, source.Length - consumed);
                source.Slice(consumed, take).CopyTo(_mtBuffer.AsSpan(_mtFill));
                if (_options.Checksum)
                {
                    _hash.Append(source.Slice(consumed, take));
                }

                _mtFill += take;
                _totalIn += take;
                consumed += take;
                continue;
            }

            if (isFinalBlock && !_lastBlockWritten)
            {
                WriteEmptyLastBlock();
                continue;
            }

            return OperationStatus.Done;
        }
    }

    private OperationStatus FlushJobs(Span<byte> destination, out int written)
    {
        written = 0;
        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            int pending = _mtFill - _mtDone;
            if (_finished || pending == 0)
            {
                return OperationStatus.Done;
            }

            RunBatch(Math.Min(pending, _mtBatch), false);
        }
    }

    private void StartFrameJobs()
    {
        long pledged = _options.ContentSize ?? -1;
        ZstdCompressionParameters parameters = ResolveParameters(pledged);
        BeginLongDistance(pledged);
        int jobSize = ResolveJobSize(parameters);
        int history = ResolveHistory(parameters);
        long limit = Array.MaxLength - 64L;
        if (history + (long)jobSize + 1 > limit)
        {
            throw new InvalidOperationException("The window and the job size together need a buffer larger than an array can be; use a smaller window or job size.");
        }

        // The batch is as many jobs as there are threads, as far as the buffer can hold them; this changes memory, not bytes.
        long jobs = Math.Max(1, Math.Min(ResolveWorkers(), (limit - history - 1) / jobSize));
        _mtJobSize = jobSize;
        _mtHistory = history;
        _mtBatch = (int)(jobs * jobSize);
        _mtParameters = parameters;
        int capacity = _mtHistory + _mtBatch + 1;
        if (_mtBuffer.Length < capacity)
        {
            _mtBuffer = new byte[capacity];
        }

        _mtFill = 0;
        _mtDone = 0;
        _mtDonePosition = 0;
        _hash = new XxHash64(0);
        _totalIn = 0;
        if (_pending.Length < ZstdFrameWriter.MaxHeaderSize)
        {
            _pending = new byte[ZstdFrameWriter.MaxHeaderSize + 64];
        }

        _pendingStart = 0;
        _pendingEnd = ZstdFrameWriter.WriteHeader(_pending, _headerWindowLog, _options.WriteContentSize ? pledged : -1, DictionaryIdToWrite(), _options.Checksum);
        _mtActive = true;
        _started = true;
    }

    // Compresses the next length bytes of the buffer as jobs and queues their blocks for output.
    private unsafe void RunBatch(int length, bool last)
    {
        // The queue was drained before the batch ran, so it is empty; it must hold every block of the batch (at most 3 bytes of block
        // header each, and a job's tail may add a block).
        long bound = length + (3L * (((length + ZstdFormat.BlockSizeMax - 1) / ZstdFormat.BlockSizeMax) + (length / _mtJobSize) + 2)) + 256;
        if (_pending.Length < bound)
        {
            _pending = new byte[bound];
        }

        _pendingStart = 0;
        fixed (byte* input = _mtBuffer)
        fixed (byte* output = _pending)
        {
            var sink = new OutputSink(output, _pending.Length);
            RunJobs(new SharedSource(input, _mtFill), _mtDone, _mtDone + length, _mtDonePosition == 0, last, _mtJobSize, _mtParameters, sink);
            if (sink.Overflowed)
            {
                throw new InvalidOperationException("The compressed jobs exceeded their bound.");
            }

            _pendingEnd = sink.Position;
        }

        _mtDone += length;
        _mtDonePosition += length;

        // Keep the history the next jobs may reach; the rest moves out.
        int keep = Math.Min(_mtHistory, _mtDone);
        int delta = _mtDone - keep;
        if (delta > 0)
        {
            Buffer.BlockCopy(_mtBuffer, delta, _mtBuffer, 0, _mtFill - delta);
            _mtFill -= delta;
            _mtDone -= delta;
            _ldm?.Slide(delta);
        }

        if (last)
        {
            _lastBlockWritten = true;
            CompleteFrame();
        }
    }
}
