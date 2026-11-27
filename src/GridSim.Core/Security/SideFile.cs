using System.Text;

namespace GridSim.Core.Security;

/// <summary>
/// The rule for a side file: a file that an input file names inside itself (a QSTS profile's <c>csv</c>, an event
/// catalogue's <c>nesoTraceCsvPath</c>, a model assignment's <c>file</c>, a protection-audit study's <c>devices</c> and
/// <c>curves</c>). The user named the input file, not the side file, so a side file is held to these rules:
/// <list type="bullet">
/// <item>a network (UNC) path, a <c>\\?\</c> or <c>\\.\</c> path, a reserved device name (<c>CON</c>, <c>NUL</c>,
/// <c>COM1</c>, ...) on Windows and a path under <c>/dev</c>, <c>/proc</c> or <c>/sys</c> elsewhere are refused before
/// anything touches the file system, so an input file never makes the program open a network connection or a device;</item>
/// <item>a relative path is resolved against the folder of the input file that names it, not the working folder; a local
/// absolute path is allowed;</item>
/// <item>the file is read through a stream that stops at a size limit for its kind, so a very large file or a device that
/// never ends is refused, not read into memory;</item>
/// <item>a refusal names the input file's member and the path as written, and never quotes the side file's content.</item>
/// <item>on Windows the resolved path must start with a drive letter (<c>C:\</c>), so every NT, device or network spelling
/// (<c>\??\</c>, <c>GLOBALROOT</c>, UNC) fails, including forms not listed here;</item>
/// <item>every symbolic link or junction on the way is read one hop at a time without being followed (at most 40 hops),
/// and a link whose target is a network or device path is refused before anything opens the path.</item>
/// </list>
/// Known residuals: a FIFO (named pipe) at a side-file path on Linux or macOS blocks the open, because .NET offers no
/// non-blocking open without native calls; a drive letter mapped to a network share counts as local (the user made that
/// connection).
/// Every refusal is an <see cref="InvalidDataException"/>, so a caller that reports bad input as an input error does so
/// here too.
/// </summary>
public static class SideFile
{
    /// <summary>The limit for a CSV profile read whole (a QSTS profile): 64 MiB, far above a year of one-minute values.</summary>
    public const long CsvMaxBytes = 64L * 1024 * 1024;

    /// <summary>The limit for a streamed trace (a NESO frequency trace): 512 MiB, several months of one-second values.</summary>
    public const long TraceMaxBytes = 512L * 1024 * 1024;

    /// <summary>The limit for a case file named by a study (a PSS/E RAW or DYR): the input-file limit, 256 MiB.</summary>
    public const long CaseMaxBytes = 256L * 1024 * 1024;

    /// <summary>The limit for a model or device document (JSON): 16 MiB.</summary>
    public const long DocumentMaxBytes = 16L * 1024 * 1024;

    private static readonly string[] WindowsDeviceNames =
    [
        "AUX", "CLOCK$", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "CON", "CONIN$", "CONOUT$",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "NUL", "PRN",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    ];

    /// <summary>
    /// Resolve the side file <paramref name="written"/> named by an input file in <paramref name="baseDirectory"/>: the full
    /// path, or a <see cref="InvalidDataException"/> when the path is empty, not a valid path, or a network or device path.
    /// Nothing on the file system is touched before the path is judged.
    /// </summary>
    /// <param name="written">The path as the input file writes it.</param>
    /// <param name="baseDirectory">The folder of the input file that names it.</param>
    /// <param name="what">The member that names it, for the message (for example <c>profiles.p.csv</c>).</param>
    public static string Resolve(string written, string baseDirectory, string what)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(what);
        if (string.IsNullOrWhiteSpace(written)) throw new InvalidDataException($"{what}: the path is empty.");
        if (IsNetworkOrDevice(written)) throw Refused(what, written);
        string full;
        try { full = Path.GetFullPath(written, Path.GetFullPath(baseDirectory.Length == 0 ? "." : baseDirectory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"{what}: '{written}' is not a valid path.");
        }
        if (IsNetworkOrDevice(full) || !IsLocalDrivePath(full)) throw Refused(what, written);
        if (LinkRefusal(full)) throw Refused(what, written);
        return full;
    }

    /// <summary>R1-01: on Windows, true only for a path that starts with a drive letter, a colon and a separator; elsewhere
    /// true for any rooted path.</summary>
    private static bool IsLocalDrivePath(string full)
        => !OperatingSystem.IsWindows()
           || (full.Length >= 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && full[2] is '\\' or '/');

    /// <summary>R1-03: walk <paramref name="full"/> from its root; at each component that is a link, read its target (one hop,
    /// not followed: no network call is made), refuse a network or device target, and continue from the target. True when
    /// a link leads to a network or device path or the chain is longer than 40 hops.</summary>
    private static bool LinkRefusal(string full)
    {
        string current = full;
        for (int hops = 0; hops <= 40; hops++)
        {
            string root = Path.GetPathRoot(current) ?? "";
            string[] parts = current[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            string at = root;
            string? next = null;
            for (int k = 0; k < parts.Length; k++)
            {
                at = Path.Combine(at, parts[k]);
                string? target;
                try { target = new FileInfo(at).LinkTarget; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
                if (target is null) continue;
                string resolved;
                try { resolved = Path.GetFullPath(target, Path.GetDirectoryName(at) ?? root); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
                if (IsNetworkOrDevice(target) || IsNetworkOrDevice(resolved) || !IsLocalDrivePath(resolved)) return true;
                next = k + 1 < parts.Length ? Path.Combine([resolved, .. parts[(k + 1)..]]) : resolved;
                break;
            }
            if (next is null) return false;
            // R2-02: the path rebuilt after the hop is judged too (d -> / and then "d/dev/zero" reaches a device although the
            // link's own target is harmless).
            if (IsNetworkOrDevice(next) || !IsLocalDrivePath(next)) return true;
            current = next;
        }
        return true;
    }

    /// <summary>True for a network or device path: <see cref="PathConfinement.IsNetworkOrDevice"/> (<c>\\server\share</c>,
    /// <c>//server/share</c>, <c>\\?\</c>, <c>\\.\</c>), a reserved device name as the last part on Windows (with or without an
    /// extension), and a path under <c>/dev</c>, <c>/proc</c> or <c>/sys</c> on other systems.</summary>
    public static bool IsNetworkOrDevice(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (PathConfinement.IsNetworkOrDevice(path)) return true;
        if (OperatingSystem.IsWindows())
        {
            string name = Path.GetFileName(path.TrimEnd(' ', '.'));
            // R1-04: "CON:", "NUL:x" (a stream suffix) are still device names: the name ends at its first colon.
            int colon = name.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0) name = name[..colon];
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
            foreach (string d in WindowsDeviceNames)
                if (string.Equals(stem, d, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        // R1-04: compared without regard to case (the macOS root volume is case-insensitive: /DEV/tty reaches devfs).
        foreach (string root in new[] { "/dev", "/proc", "/sys" })
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Open <paramref name="full"/> (a path from <see cref="Resolve"/>) for reading through a stream that throws a
    /// <see cref="InvalidDataException"/> once more than <paramref name="maxBytes"/> bytes have been read. A file whose length
    /// is already over the limit is refused before it is opened. A missing file is a <see cref="FileNotFoundException"/>,
    /// left to the caller to word.</summary>
    public static Stream OpenRead(string full, long maxBytes, string what)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(what);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var info = new FileInfo(full);
        if (info.Exists && info.Length > maxBytes) throw TooLarge(what, maxBytes);
        var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        return new BoundedStream(fs, maxBytes, what);
    }

    /// <summary>The whole file as bytes, through <see cref="OpenRead"/>.</summary>
    public static byte[] ReadAllBytes(string full, long maxBytes, string what)
    {
        using Stream s = OpenRead(full, maxBytes, what);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The file's lines, as <see cref="File.ReadAllLines(string)"/> reads them (UTF-8, a byte order mark honoured),
    /// through <see cref="OpenRead"/>.</summary>
    public static string[] ReadAllLines(string full, long maxBytes, string what)
    {
        using var reader = new StreamReader(OpenRead(full, maxBytes, what), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        for (string? l = reader.ReadLine(); l is not null; l = reader.ReadLine()) lines.Add(l);
        return lines.ToArray();
    }

    private static InvalidDataException Refused(string what, string written)
        => new($"{what}: '{written}' is a network or device path; a file named inside an input file must be a local file, so it was not opened.");

    private static InvalidDataException TooLarge(string what, long maxBytes)
        => new(FormattableString.Invariant($"{what}: the file is over the {maxBytes}-byte limit for this kind of file; it was not read."));

    /// <summary>A read-only stream that refuses to deliver more than a set number of bytes.</summary>
    private sealed class BoundedStream(Stream inner, long maxBytes, string what) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            _read += n;
            if (_read > maxBytes) throw TooLarge(what, maxBytes);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
