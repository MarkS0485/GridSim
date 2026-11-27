using System.Security.Cryptography;
using GridSim.Core.Security;

namespace GridSim.Core.Journal;

/// <summary>
/// The journal's optional snapshot store: input files kept by content, <c>&lt;journal folder&gt;/objects/ab/cdef...</c>
/// (the SHA-256 of the bytes, split after two characters). An object is written once (create-new), then made read-only;
/// an object already present is not written again. Files over <see cref="MaxBytes"/> are not kept. Outputs are never
/// kept. Nothing is ever deleted automatically.
/// </summary>
public static class JournalSnapshot
{
    /// <summary>The largest file the store keeps (256 MiB).</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>The store folder of a journal.</summary>
    public static string ObjectsDir(string journalPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, "objects");

    /// <summary>The object path for a SHA-256 (lower-case hex).</summary>
    public static string ObjectPath(string objectsDir, string sha256) => Path.Combine(objectsDir, sha256[..2], sha256[2..]);

    /// <summary>
    /// Keep <paramref name="bytes"/> (whose SHA-256 is <paramref name="sha256"/>) in the store. Returns null when it is
    /// kept (or already was), else why not (recorded as the entry's <c>snapshotSkipped</c>): "size" when it is over the
    /// limit; "damaged" when a damaged object of that hash is in the store and cannot be removed; "busy" when another
    /// program held the file (a sharing or lock violation) through every retry of the move; "write-failed" when the move
    /// failed for any other reason (a full disk, a read-only volume, a path that is too long).
    /// </summary>
    public static string? Put(string objectsDir, string sha256, byte[] bytes)
    {
        if (bytes.LongLength > MaxBytes) return "size";
        string path = ObjectPath(objectsDir, sha256);
        // SEC-06: an object that is there is kept only when it still has its own hash; one cut short by a crash or a full
        // disk (it used to be skipped for ever, and every Get then reported it damaged) is replaced.
        if (File.Exists(path))
        {
            if (Intact(path, sha256, bytes.LongLength)) return null;
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
            // R3-05: a damaged object that cannot be removed is reported, not claimed as kept.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "damaged"; }
        }
        JournalFile.CreateUserOnlyDirectory(objectsDir);
        JournalFile.CreateUserOnlyDirectory(Path.GetDirectoryName(path)!);
        // SEC-06: written to a temporary sibling, flushed to disk, then moved into place, so the final name only ever holds
        // a whole object.
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(temp, o))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.ReadOnly);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead);
            // R3-05: on Windows a scanner or indexer often opens a file just closed; the move is retried briefly, and a move
            // that still cannot be made is reported ("busy") instead of escaping. R4-05: only a sharing or lock violation
            // is retried; any other failure is reported at once as "write-failed" (it was retried and called "busy").
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: false); break; }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && !File.Exists(path))
                {
                    if (!IsSharingOrLock(ex)) return "write-failed";
                    if (attempt >= 4) return "busy";
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(path))
        {
            // Another writer kept the same bytes first: the object is content-addressed, so it is the same object.
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.SetAttributes(temp, FileAttributes.Normal); File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    /// <summary>R4-05: a sharing violation (32) or a lock violation (33) in the low word of the HResult, or on Windows an
    /// access refusal (which a scanner holding the file also gives); the failures a short retry can outlast.</summary>
    internal static bool IsSharingOrLock(Exception ex) => ex switch
    {
        UnauthorizedAccessException => OperatingSystem.IsWindows(),
        IOException io => (io.HResult & 0xFFFF) is 32 or 33,
        _ => false,
    };

    /// <summary>SEC-06: true when the object at <paramref name="path"/> has the expected length and its own hash. A Put of an
    /// object that is there re-hashes it (time in its size, where it used to be a file-exists check).</summary>
    private static bool Intact(string path, string sha256, long length)
    {
        try
        {
            if (new FileInfo(path).Length != length) return false;
            using FileStream fs = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(fs)) == sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }   // unreadable now: left as it is
    }

    /// <summary>
    /// The bytes of the object <paramref name="sha256"/>, or null with <paramref name="why"/> when it is missing, lies
    /// outside the store (a link or junction on the way) or no longer has its own hash.
    /// </summary>
    public static byte[]? Get(string objectsDir, string sha256, out string? why)
    {
        why = null;
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) { why = "not a SHA-256"; return null; }
        string path = ObjectPath(objectsDir, sha256);
        string full = Path.GetFullPath(path);
        string? refusal = PathConfinement.Refusal(path, full, objectsDir);
        if (refusal is not null) { why = "snapshot object refused: " + refusal; return null; }
        if (!File.Exists(full)) { why = "no snapshot object"; return null; }
        byte[] bytes = File.ReadAllBytes(full);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256) { why = "snapshot object damaged (its hash differs)"; return null; }
        return bytes;
    }
}
