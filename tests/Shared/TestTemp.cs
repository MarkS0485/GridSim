global using GridSim.TestSupport;

using System.Diagnostics;

namespace GridSim.TestSupport;

/// <summary>
/// The one way a test makes a temporary folder or file. Every path handed out is recorded, and every recorded path that
/// still exists is deleted when the test process ends, whether or not the test cleaned up after itself. The record is
/// also written to a small manifest beside the folders, so a run that was killed before it could clean up is swept by
/// the next run (only the paths a dead run recorded, never anything else in the temporary folder).
/// <para>Nothing here changes a path: <see cref="Under"/> returns exactly what <c>Path.Combine(Path.GetTempPath(), ...)</c>
/// returned before, so a test sees the same names it always did.</para>
/// <para>Linked into every test project (tests/Shared); <c>TempHygieneGuardTests</c> refuses a test file that makes a
/// temporary path any other way.</para>
/// </summary>
internal static class TestTemp
{
    /// <summary>The manifest prefix. A manifest is named prefix + process id + "-" + a fresh id + ".paths".</summary>
    internal const string ManifestPrefix = "gridsim-testrun-";

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    private static readonly object Gate = new();
    private static readonly List<string> Recorded = [];
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal);
    private static readonly string ManifestPath = Start();

    /// <summary>The paths this process has recorded so far (full paths, first-recorded first).</summary>
    internal static IReadOnlyList<string> RecordedPaths
    {
        get { lock (Gate) return Recorded.ToArray(); }
    }

    /// <summary>
    /// <c>Path.Combine(Path.GetTempPath(), first, rest...)</c>, with the top-level entry (the temporary folder's child
    /// named by <paramref name="first"/>) recorded for deletion at the end of the run. Nothing is created.
    /// </summary>
    public static string Under(string first, params string[] rest)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(rest);
        string root = Path.GetTempPath();
        Record(TopLevel(root, first));
        if (rest.Length == 0) return Path.Combine(root, first);
        var parts = new string[rest.Length + 2];
        parts[0] = root;
        parts[1] = first;
        Array.Copy(rest, 0, parts, 2, rest.Length);
        return Path.Combine(parts);
    }

    /// <summary><see cref="Directory.CreateTempSubdirectory(string?)"/>, recorded for deletion at the end of the run.</summary>
    public static DirectoryInfo CreateSubdirectory(string prefix)
    {
        DirectoryInfo d = Directory.CreateTempSubdirectory(prefix);
        Record(d.FullName);
        return d;
    }

    /// <summary><see cref="Path.GetTempFileName"/> (an empty file is created), recorded for deletion at the end of the run.</summary>
    public static string NewFile()
    {
        string path = Path.GetTempFileName();
        Record(path);
        return path;
    }

    /// <summary>A new, empty, recorded folder <c>%TEMP%/prefix + id</c>, deleted when the returned object is disposed.</summary>
    public static TrackedTempDir NewDir(string prefix) => new(prefix);

    /// <summary>Delete one path (a file or a folder with everything in it), clearing read-only flags when a plain delete
    /// is refused. True when the path is gone afterwards.</summary>
    internal static bool TryDelete(string path)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0) ClearReadOnly(path);
            }
        }
        return !Directory.Exists(path) && !File.Exists(path);
    }

    /// <summary>Delete every path in <paramref name="paths"/> that still exists; returns the ones that could not be deleted.</summary>
    internal static IReadOnlyList<string> Sweep(IEnumerable<string> paths)
    {
        var left = new List<string>();
        foreach (string p in paths)
            if (IsSafeChild(p) && !TryDelete(p)) left.Add(p);
        return left;
    }

    /// <summary>True only for a path that is a direct child of the temporary folder whose name survives normalisation
    /// unchanged: never the temporary folder itself, never anything outside it. Windows trims trailing spaces and dots
    /// (" ", "...", ". " all resolve to the folder itself), so the name must not end in either and must hold a letter or
    /// digit. Every record, sweep and delete goes through this check.</summary>
    internal static bool IsSafeChild(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (!string.Equals(full, path, StringComparison.Ordinal)) return false;
        string? parent = Path.GetDirectoryName(full);
        if (parent is null || !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) return false;
        string name = Path.GetFileName(full);
        return name.Length > 0 && name != "." && name != ".." && !name.EndsWith(' ') && !name.EndsWith('.')
            && name.Any(char.IsLetterOrDigit);
    }

    private static void Record(string fullPath)
    {
        if (!IsSafeChild(fullPath))
            throw new ArgumentException($"A temporary path must be a direct child of the temporary folder with a plain name; got '{fullPath}'.", nameof(fullPath));
        lock (Gate)
        {
            if (!Known.Add(fullPath)) return;
            Recorded.Add(fullPath);
            try { File.AppendAllText(ManifestPath, fullPath + "\n"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the in-process record still holds it */ }
        }
    }

    /// <summary>The temporary folder's child that <paramref name="first"/> names. Refuses a name that is empty, rooted or
    /// climbs out, so the temporary folder itself (or anything outside it) is never recorded.</summary>
    private static string TopLevel(string root, string first)
    {
        string head = first.Split(Separators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (head.Length == 0 || head == "." || head == ".." || Path.IsPathRooted(first) || head.EndsWith(' ') || head.EndsWith('.')
            || !head.Any(char.IsLetterOrDigit))
            throw new ArgumentException($"A temporary path must name a child of the temporary folder; got '{first}'.", nameof(first));
        return Path.GetFullPath(Path.Combine(root, head));
    }

    private static string Start()
    {
        SweepAbandonedRuns();
        string manifest = Path.Combine(Path.GetTempPath(), ManifestPrefix + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".paths");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AtExit(manifest);
        return manifest;
    }

    private static void AtExit(string manifest)
    {
        IReadOnlyList<string> left = Sweep(RecordedPaths);
        if (left.Count == 0)
        {
            TryDelete(manifest);
            return;
        }
        // The manifest stays, so the next run sweeps what this one could not (a file still held open, for example).
        Console.Error.WriteLine($"TestTemp: {left.Count} temporary path(s) could not be deleted at the end of the run and are left for the next run to sweep: " + string.Join(", ", left.Take(10)));
    }

    /// <summary>Sweep the paths recorded by earlier test processes that are no longer running (their manifests stay only
    /// when they were killed or could not finish their own sweep). A manifest of a live process is left alone.</summary>
    private static void SweepAbandonedRuns()
    {
        string[] manifests;
        try { manifests = Directory.GetFiles(Path.GetTempPath(), ManifestPrefix + "*.paths"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (string m in manifests)
        {
            string name = Path.GetFileNameWithoutExtension(m);
            int dash = name.IndexOf('-', ManifestPrefix.Length);
            if (dash < 0 || !int.TryParse(name.AsSpan(ManifestPrefix.Length, dash - ManifestPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid)) continue;
            if (IsRunning(pid)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(m); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            string root = Path.GetFullPath(Path.GetTempPath());
            // Only children of the temporary folder are ever recorded; anything else in a manifest is ignored.
            IEnumerable<string> mine = lines.Where(l => l.Length > root.Length && l.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(l.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { } parent
                && string.Equals(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
            if (Sweep(mine).Count == 0) TryDelete(m);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }   // cannot tell: leave it alone
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
            }
            else if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the second delete reports what is left */ }
    }
}

/// <summary>A recorded temporary folder, created on construction and deleted on <see cref="Dispose"/> (and, failing that,
/// at the end of the run by <see cref="TestTemp"/>).</summary>
internal sealed class TrackedTempDir : IDisposable
{
    public TrackedTempDir(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        Path = TestTemp.Under(prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The folder's full path.</summary>
    public string Path { get; }

    /// <summary>A path inside the folder.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() { if (TestTemp.IsSafeChild(Path)) TestTemp.TryDelete(Path); }
}
