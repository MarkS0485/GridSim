using System.Text;

namespace GridSim.Core.IO;

/// <summary>
/// The size a case or study input file may have before a parser reads it whole into memory. The file parsers read a
/// file completely (text or bytes) before parsing it, so an enormous file would exhaust memory before any format check
/// could refuse it. The file length is checked first and a longer file is refused with an <see cref="InputFileTooLargeException"/>
/// that names the file and the limit. The limit is far above any real case (the largest shipped case file is a few
/// tens of MB); it is a guard against a hostile or mistaken file, not a model-size limit. A file of exactly the limit
/// is read.
/// </summary>
internal static class InputFileLimits
{
    /// <summary>The default limit: 256 MiB.</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private static readonly AsyncLocal<long?> Scoped = new();

    /// <summary>The limit in force for the current call context.</summary>
    public static long MaxBytes => Scoped.Value ?? DefaultMaxBytes;

    /// <summary>Use <paramref name="maxBytes"/> as the limit in the current async context until the result is disposed (tests).</summary>
    public static IDisposable Scope(long maxBytes)
    {
        long? previous = Scoped.Value;
        Scoped.Value = maxBytes;
        return new Restore(previous);
    }

    /// <summary>Throw <see cref="InputFileTooLargeException"/> when the file is longer than the limit. A missing file is left to the reader.</summary>
    public static void Check(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxBytes)
            throw new InputFileTooLargeException(Path.GetFileName(path), info.Length, MaxBytes);
    }

    /// <summary><see cref="File.ReadAllText(string)"/> after the size check.</summary>
    public static string ReadAllText(string path)
    {
        Check(path);
        return File.ReadAllText(path);
    }

    /// <summary><see cref="File.ReadAllText(string, Encoding)"/> after the size check.</summary>
    public static string ReadAllText(string path, Encoding encoding)
    {
        Check(path);
        return File.ReadAllText(path, encoding);
    }

    /// <summary><see cref="File.ReadAllBytes(string)"/> after the size check.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        Check(path);
        return File.ReadAllBytes(path);
    }

    private sealed class Restore(long? previous) : IDisposable
    {
        public void Dispose() => Scoped.Value = previous;
    }
}

/// <summary>An input file is longer than <see cref="InputFileLimits.MaxBytes"/>. An <see cref="IOException"/>, so callers that treat unreadable input as an input error do so here too.</summary>
internal sealed class InputFileTooLargeException(string fileName, long length, long limit)
    : IOException($"{fileName}: the file is {length} bytes, over the {limit}-byte limit for an input file; it was not read")
{
    public string FileName { get; } = fileName;
    public long Length { get; } = length;
    public long Limit { get; } = limit;
}
