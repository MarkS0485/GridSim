namespace GridSim.Core.IO;

/// <summary>
/// Creates an output file that is guaranteed NOT to replace an existing one. Used by the interposer's window sinks:
/// two windows can carry the same instant and sequence number (a replayed stream, a restarted listener writing into
/// the same folder, a device clock that stands still), and before October 2026 the second silently overwrote the
/// first, losing evidence. Here the requested name is tried first; if it is taken, "-dup1", "-dup2", ... is inserted
/// before the extension until a free name is found. The file is opened with <see cref="FileMode.CreateNew"/>, so the
/// check and the creation are one atomic step: a file appearing between them cannot be overwritten either.
/// </summary>
public static class NeverOverwrite
{
    /// <summary>How many "-dupN" names are tried before giving up. Reaching it means something is badly wrong (the
    /// same window written a thousand times), so it fails loud rather than looping for ever.</summary>
    public const int MaxDuplicates = 1000;

    /// <summary>
    /// Open a new, empty file at <paramref name="path"/>, or at the first free "-dupN" variant of it. Returns the
    /// open stream and the path actually used (compare with <paramref name="path"/> to tell the user a duplicate was
    /// kept under another name). Creates the directory if needed. Throws <see cref="IOException"/> when no free name
    /// is found within <see cref="MaxDuplicates"/>.
    /// </summary>
    public static FileStream CreateNew(string path, out string actualPath)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string stem = Path.GetFileNameWithoutExtension(full);
        string ext = Path.GetExtension(full);

        for (int k = 0; k <= MaxDuplicates; k++)
        {
            string candidate = k == 0 ? full : Path.Combine(dir, $"{stem}-dup{k}{ext}");
            try
            {
                FileStream fs = new(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                actualPath = candidate;
                return fs;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken: try the next suffix. (Any other IOException - a full disk, no permission - is re-thrown.)
            }
        }
        throw new IOException(
            $"could not find a free file name for '{full}' after {MaxDuplicates} duplicates; refusing to overwrite.");
    }

    /// <summary>Write <paramref name="text"/> to a new file (see <see cref="CreateNew"/>) and return the path used.</summary>
    public static string WriteAllText(string path, string text)
    {
        using FileStream fs = CreateNew(path, out string actual);
        using var w = new StreamWriter(fs);
        w.Write(text);
        return actual;
    }
}
