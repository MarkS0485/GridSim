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
            try { set = MeasurementSet.Load(file, utcNow: null); }
            catch (Exception ex)
            {
                lock (_skippedGate) _skipped.Add($"{Path.GetFileName(file)}: {ex.Message}");
                continue;
            }
            yield return set;
        }
    }
}
