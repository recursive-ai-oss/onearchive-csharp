namespace RecursiveAI.Fuzz;

/// <summary>
/// One fuzz target: feeds arbitrary bytes to a decoder or parser. A target returns normally
/// or throws one of the library's typed exceptions; anything else is a finding.
/// </summary>
internal sealed class FuzzTarget(string name, Action<ReadOnlySpan<byte>> run, Func<Exception, bool> isExpected)
{
    public string Name { get; } = name;

    public Action<ReadOnlySpan<byte>> Run { get; } = run;

    /// <summary>Returns true for exceptions that are the documented response to malformed input.</summary>
    public Func<Exception, bool> IsExpected { get; } = isExpected;
}

/// <summary>The list of targets. Each decoder or parser adds its own entry here.</summary>
internal static class FuzzTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } =
    [
        // Exercises the driver itself; never throws.
        new FuzzTarget("noop", static data => _ = data.Length, static _ => false),
        ZstdTargets.Plain,
        ZstdTargets.WithDictionary,
        Lz4Targets.Frame,
        Lz4Targets.FrameWithDictionary,
        Lz4Targets.Block,
        ZstdEncoderTargets.RoundTrip,
        ZstdLongTargets.RoundTrip,
        ZstdTrainingTargets.RoundTrip,
        Lz4EncoderTargets.Frame,
        Lz4EncoderTargets.Block,
        ZstdSeekableTargets.Read,
        ZstdSeekableTargets.RoundTrip,
        DeflateTargets.Raw,
        DeflateTargets.ZLib,
        DeflateTargets.GZip,
        BZip2Targets.Decode,
        LzmaTargets.Lzma,
        LzmaTargets.Xz,
        LzmaTargets.Lzip,
        DeflateEncoderTargets.RoundTrip,
        LzmaEncoderTargets.LzmaRoundTrip,
        LzmaEncoderTargets.XzRoundTrip,
        ArchiveTargets.TarRead,
        ArchiveTargets.Open,
        ArchiveTargets.TarRoundTrip,
        CpioArTargets.CpioRead,
        CpioArTargets.ArRead,
        CpioArTargets.CpioRoundTrip,
        CpioArTargets.ArRoundTrip,
        ZipTargets.Read,
        ZipTargets.RoundTrip,
        ZipTargets.Single,
        SevenZipTargets.Read,
    ];
}
