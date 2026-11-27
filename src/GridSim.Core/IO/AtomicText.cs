using System.Text;

namespace GridSim.Core.IO;

/// <summary>
/// IO-22: write a whole text file so that a reader never sees it half written. The text goes to a temporary sibling
/// (a random name, created new, so a planted file or link at a fixed name is never followed), is flushed to disk and is
/// then moved over the target, which an existing output is replaced by, as before. A failure part way leaves the target
/// as it was and removes the temporary file. The bytes are those of <see cref="File.WriteAllText(string, string)"/>
/// (UTF-8, no byte-order mark).
/// </summary>
internal static class AtomicText
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Write <paramref name="text"/> to <paramref name="path"/> through a temporary sibling.</summary>
    public static void Write(string path, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);
        string full = Path.GetFullPath(path);
        string temp = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Utf8NoBom.GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* best effort: the temporary file is the only thing left behind */ }
            catch (UnauthorizedAccessException) { /* as above */ }
        }
    }
}
