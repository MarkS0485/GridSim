using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GridSim.Core.Journal;

/// <summary>One line read from a journal: an entry, or the reason it could not be read.</summary>
/// <param name="File">The journal file (active file or segment) the line is in.</param>
/// <param name="LineNumber">The line's number in that file, from 1.</param>
/// <param name="Entry">The entry, or null when the line is damaged.</param>
/// <param name="Error">Null for a readable entry; "damaged entry" or "entry schema unknown" otherwise.</param>
public sealed record JournalLine(string File, int LineNumber, JournalEntry? Entry, string? Error);

/// <summary>
/// Appending to and reading a journal. Many processes may append at once: each append takes the lock file
/// <c>&lt;journal&gt;.lock</c> (opened with no sharing, retried with a bounded back-off), reads the last sequence number,
/// appends one line ending in a line feed, flushes it to disk and lets the lock go. The lock file holds no data.
/// Readers never take the lock: they read whole lines only, so a line still being written is not read.
/// </summary>
public static class JournalFile
{
    /// <summary>The default size at which the active file is rotated into a segment (64 MiB).</summary>
    public const long DefaultMaxSegmentBytes = 64L * 1024 * 1024;

    /// <summary>The longest an append waits for the lock before it gives up (10 s).</summary>
    public static readonly TimeSpan LockBudget = TimeSpan.FromSeconds(10);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The lock file of a journal.</summary>
    public static string LockPath(string journalPath) => journalPath + ".lock";

    /// <summary>
    /// Append one entry. <paramref name="lineForSeq"/> is given the entry's sequence number (the last one in the
    /// journal plus one) and returns the line to write (without its line feed). Returns the sequence number written.
    /// Throws <see cref="TimeoutException"/> when the lock is not had within <paramref name="budget"/>, and
    /// <see cref="IOException"/> when the file cannot be written.
    /// </summary>
    public static long Append(string journalPath, Func<long, string> lineForSeq, long maxSegmentBytes = DefaultMaxSegmentBytes, TimeSpan? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(lineForSeq);
        string full = Path.GetFullPath(journalPath);
        string dir = Path.GetDirectoryName(full)!;
        CreateUserOnlyDirectory(dir);
        using FileStream lockStream = TakeLock(LockPath(full), budget ?? LockBudget);

        long last = LastSeq(full);
        // SEC-05: the sequence cannot wrap (a line carrying long.MaxValue made every later entry negative and unreadable).
        if (last == long.MaxValue)
            throw new InvalidDataException($"journal '{Path.GetFileName(full)}': its last sequence number is {long.MaxValue}, so no entry can follow it; start a new journal.");
        long seq = last + 1;
        byte[] line = Utf8.GetBytes(lineForSeq(seq) + "\n");

        if (File.Exists(full))
        {
            long length = new FileInfo(full).Length;
            if (length > 0 && length + line.Length > maxSegmentBytes) Rotate(full, last);
        }

        using var fs = new FileStream(full, UserOnly(FileMode.Append, FileAccess.Write));
        // A torn last line (a writer that died mid-line) is closed with its own line feed first, so the new entry
        // starts on a line of its own; the torn line stays a damaged line that readers report.
        if (fs.Length > 0 && LastByte(full, fs.Length) != (byte)'\n') fs.WriteByte((byte)'\n');
        fs.Write(line, 0, line.Length);
        fs.Flush(flushToDisk: true);
        return seq;
    }

    private static FileStreamOptions UserOnly(FileMode mode, FileAccess access)
    {
        var o = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return o;
    }

    /// <summary>Create a folder readable by its owner only on Linux and macOS (0700); on Windows the per-user ACL of the
    /// parent folder is inherited.</summary>
    public static void CreateUserOnlyDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static byte LastByte(string path, long length)
    {
        using var r = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        r.Seek(length - 1, SeekOrigin.Begin);
        return (byte)r.ReadByte();
    }

    private static FileStream TakeLock(string lockPath, TimeSpan budget)
    {
        var started = DateTime.UtcNow;
        int wait = 25;
        while (true)
        {
            try
            {
                var o = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream(lockPath, o);
            }
            catch (IOException) when (DateTime.UtcNow - started < budget)
            {
                Thread.Sleep(wait);
                wait = Math.Min(wait * 2, 400);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"the journal lock '{lockPath}' was not free within {budget.TotalSeconds:0} s", ex);
            }
        }
    }

    // The highest sequence number in the active file, else in the newest segment, else 0.
    private static long LastSeq(string full)
    {
        if (File.Exists(full))
        {
            long fromActive = LastSeqInFile(full);
            if (fromActive > 0) return fromActive;
        }
        long best = 0;
        foreach ((string _, long _, long lastSeq) in Segments(full)) best = Math.Max(best, lastSeq);
        return best;
    }

    private static long LastSeqInFile(string path)
    {
        using var r = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long length = r.Length;
        if (length == 0) return 0;
        // Read back from the end in growing windows until a whole readable line gives a sequence number.
        for (long window = 64 * 1024; ; window *= 4)
        {
            long start = Math.Max(0, length - window);
            r.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            r.ReadExactly(buffer);
            string text = Utf8.GetString(buffer);
            string[] lines = text.Split('\n');
            // The last piece is torn (or empty after a final line feed); the first may be cut by the window.
            for (int i = lines.Length - 2; i >= (start == 0 ? 0 : 1); i--)
            {
                // SEC-05: only an entry of the known schema gives the sequence number (a line of another schema used to,
                // so one crafted line could set it to anything).
                JournalEntry? e = JournalJson.Parse(lines[i], out _);
                if (e is not null && e.Seq > 0 && e.Schema == JournalEntry.SchemaTag) return e.Seq;
            }
            if (start == 0) return 0;
        }
    }

    private static readonly Regex SegmentName = new(@"^(?<stem>.+)-(?<first>\d+)-(?<last>\d+)\.jsonl$", RegexOptions.CultureInvariant);

    /// <summary>The rotated segments beside <paramref name="journalPath"/>, oldest first.</summary>
    public static IReadOnlyList<(string Path, long First, long Last)> Segments(string journalPath)
    {
        string full = Path.GetFullPath(journalPath);
        string dir = Path.GetDirectoryName(full)!;
        string stem = Path.GetFileNameWithoutExtension(full);
        var list = new List<(string, long, long)>();
        if (!Directory.Exists(dir)) return list;
        foreach (string f in Directory.GetFiles(dir, stem + "-*-*.jsonl"))
        {
            Match m = SegmentName.Match(Path.GetFileName(f));
            if (!m.Success || m.Groups["stem"].Value != stem) continue;
            if (!long.TryParse(m.Groups["first"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long a)) continue;
            if (!long.TryParse(m.Groups["last"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long b)) continue;
            list.Add((f, a, b));
        }
        list.Sort((x, y) => x.Item2 != y.Item2 ? x.Item2.CompareTo(y.Item2) : string.CompareOrdinal(x.Item1, y.Item1));
        return list;
    }

    private static void Rotate(string full, long lastSeq)
    {
        long first = 0;
        using (var r = new StreamReader(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Utf8))
        {
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                JournalEntry? e = JournalJson.Parse(line, out _);
                if (e is not null && e.Seq > 0) { first = e.Seq; break; }
            }
        }
        if (first == 0) first = lastSeq;
        string dir = Path.GetDirectoryName(full)!;
        string target = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileNameWithoutExtension(full)}-{first}-{lastSeq}.jsonl"));
        // SEC-18: a name already taken (an active file with no readable entry, rotated twice, or a leftover of that name)
        // made the move throw on every later run, so the over-size active file was never rotated and every append failed.
        // Such a file is kept under "<segment>.dupN.jsonl", which the segment reader does not take: it holds no readable
        // entry of its own (that is how the names came to clash), and nothing is overwritten.
        for (int dup = 1; File.Exists(target) && dup < 10_000; dup++)
            target = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileNameWithoutExtension(full)}-{first}-{lastSeq}.dup{dup}.jsonl"));
        File.Move(full, target, overwrite: false);
    }

    /// <summary>
    /// Read every line of a journal: its segments (oldest first), then the active file. Only lines that end in a line
    /// feed are read; a torn last line is left out. Each line that is not a readable entry is returned with its
    /// error, so the caller can report it as "journal line N unreadable".
    /// </summary>
    public static IReadOnlyList<JournalLine> Read(string journalPath)
    {
        string full = Path.GetFullPath(journalPath);
        var files = Segments(full).Select(s => s.Path).ToList();
        if (File.Exists(full)) files.Add(full);
        var lines = new List<JournalLine>();
        foreach (string f in files)
        {
            byte[] bytes;
            using (var r = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[r.Length];
                r.ReadExactly(bytes);
            }
            int end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0) continue;
            string text = Utf8.GetString(bytes, 0, end);
            string[] parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                string line = parts[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                JournalEntry? e = JournalJson.Parse(line, out string? error);
                lines.Add(new JournalLine(f, i + 1, error is null ? e : e, error));
            }
        }
        return lines;
    }
}
