using GridSim.Core.Estimation;
using GridSim.Core.Model;

namespace GridSim.Web.Realtime;

/// <summary>
/// A <see cref="IRealtimeWindowFeed"/> that streams measurement windows from a directory of
/// <c>gridsim-measurements/1</c> JSON files (sorted by name) - the reference feed for driving the resident
/// estimator over replayed history. Each file is loaded against the real horizon (D-DOC-018): a fresh or undated
/// window is skipped and listed in <see cref="SkippedFiles"/>, and the streaming engine checks every window again.
/// </summary>
public sealed class ReplayDirectoryWindowFeed : IRealtimeWindowFeed
{
    private readonly string _directory;

    public ReplayDirectoryWindowFeed(GridModel model, string directory)
    {
        Model = model;
        _directory = directory;
    }

    /// <summary>The network the windows are estimated on.</summary>
    public GridModel Model { get; }

    private readonly List<string> _skipped = new();
    private readonly Lock _skippedGate = new();

    /// <summary>Files skipped because they were malformed or not a gridsim-measurements/1 document. A SNAPSHOT, taken
    /// under the list's lock: the estimator thread appends while GET /api/realtime serialises it, and enumerating the
    /// live list during an append threw (or read a torn list).</summary>
    public IReadOnlyList<string> SkippedFiles
    {
        get { lock (_skippedGate) return _skipped.ToArray(); }
    }

    // Bug #175: every window file handed to the estimator, by name and the SHA-256 of the bytes it was loaded from, in
    // the order consumed. The audit entry used to carry a hash of the whole folder taken once at start-up, over every
    // file at any depth, while this feed reads only the top-level *.json files, lazily: a window added or changed after
    // start-up was estimated but not covered, and a nested or non-JSON file changed the hash without being read.
    private readonly List<(string Name, string Sha256)> _consumed = new();

    /// <summary>Bug #175: the windows consumed so far, by file name and content hash (a snapshot, under the lock).</summary>
    public IReadOnlyList<(string Name, string Sha256)> Consumed
    {
        get { lock (_skippedGate) return _consumed.ToArray(); }
    }

    /// <summary>Bug #175: one audit input, <c>windows-consumed</c>, hashing the consumed list (one line per window:
    /// file name, a tab, its SHA-256, a line feed). Null before any window was consumed.</summary>
    public GridSim.Core.Diagnostics.AuditInput? ConsumedInput
    {
        get
        {
            (string Name, string Sha256)[] list = Consumed.ToArray();
            if (list.Length == 0) return null;
            var sb = new System.Text.StringBuilder();
            foreach ((string name, string sha) in list) sb.Append(name).Append('\t').Append(sha).Append('\n');
            return GridSim.Core.Diagnostics.AuditInput.OfText("windows-consumed", sb.ToString());
        }
    }

    // SHA-256 of a file's bytes as 64 upper-case hex digits (the audit log's form).
    private static string FileSha256(string path)
    {
        using FileStream s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
    }

    /// <summary>Lazily yield each measurement set in the directory, in filename order.</summary>
    public IEnumerable<MeasurementSet> Windows()
    {
        if (!Directory.Exists(_directory)) yield break;
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            MeasurementSet? set;
            // A single malformed or foreign JSON file must not crash the whole hosted estimator (the throw
            // would surface out of the engine's enumeration and fault the BackgroundService -> stop the web
            // host). Skip it, record it, and carry on with the rest of the directory.
            //
            // D-DOC-018 (HORIZON, same seam as `serve`): each file is loaded against the REAL clock, so
            // MeasurementSet.Load's guard seam is on. It used to load with DateTime.MaxValue and leave the check to the
            // engine alone. A window newer than the horizon, or an undated one (D-DOC-061), is now refused at load: it
            // is skipped and listed with the reason (surfaced by GET /api/realtime), never estimated. Before, an undated
            // window reached the engine, which threw and faulted the hosted service.
            // Bug #175: the file is hashed just before and just after it is loaded; a file that changed while it was
            // read is skipped (named, like a malformed one), so the recorded hash is the content that was estimated.
            string before, after;
            try
            {
                before = FileSha256(file);
                set = MeasurementSet.Load(file, utcNow: null);
                after = FileSha256(file);
            }
            catch (Exception ex)
            {
                lock (_skippedGate) _skipped.Add($"{Path.GetFileName(file)}: {ex.Message}");
                continue;
            }
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                lock (_skippedGate) _skipped.Add($"{Path.GetFileName(file)}: the file changed while it was being read; skipped");
                continue;
            }
            lock (_skippedGate) _consumed.Add((Path.GetFileName(file), after));
            yield return set;
        }
    }
}
