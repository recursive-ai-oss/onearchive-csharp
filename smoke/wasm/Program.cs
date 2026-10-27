using RecursiveAI.OneArchive;
using RecursiveAI.OneCompress;

// Runs under Node. The hash-matrix runner will grow out of this host.
string compress = OneCompressInfo.Name;
string archive = OneArchiveInfo.Name;
if (compress != "OneCompress" || !archive.Contains(compress, StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke failed");
    return 1;
}

Console.WriteLine("ok: " + archive);
return 0;
