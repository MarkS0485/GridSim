using System.Collections.Concurrent;
using GridSim.Core.Simulation;
using GridSim.Web.Model;
using GridSim.Web.Projection;

namespace GridSim.Web.Api;

/// <summary>
/// D-DOC-225: the map topology of each presolved replay run, built once and reused by <c>GET /api/topology</c>.
/// <para>
/// Before, every call loaded the WHOLE run (every exported snapshot, parsed and laid out) only to read frame 0's
/// layout model and the frame count: 71 s per call on a 42-frame case118 replay, the same work again on each call.
/// The topology of a run is fixed by its files, so it is now built on the first call and kept.
/// </para>
/// <para>
/// Staying honest about the disk: each entry remembers a fingerprint of the run's <c>exports</c> folder (the snapshot
/// files' names, sizes and last-write times, plus the sidecar CSV's). A call whose fingerprint differs (a run
/// re-exported in place, a snapshot added or removed) rebuilds the entry, so the cache never serves a topology the
/// files no longer describe. Taking the fingerprint lists one folder; it does not open any snapshot.
/// </para>
/// <para>
/// The cached value is the projected <see cref="TopologyDto"/> only. Sessions (Blazor, /ws/sim) still load their own
/// <see cref="PresolvedReplay"/>, because the replay is a cursor with per-session position; sharing it would let one
/// viewer's seek move another's frame.
/// </para>
/// </summary>
public sealed class ReplayTopologyCache
{
    private sealed record Entry(string Fingerprint, TopologyDto Topology);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    // One build at a time per run: two first calls racing on the same run would otherwise both pay the full load.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private int _builds;

    /// <summary>How many times a topology has been built from the run files (a test and the measurement read it).</summary>
    public int Builds => Volatile.Read(ref _builds);

    /// <summary>
    /// The topology of the replay run <paramref name="entry"/>: from the cache when the run's files are unchanged,
    /// else built with <paramref name="load"/> (normally <see cref="CaseCatalog.LoadReplay"/>) and remembered.
    /// Exceptions from the load (a corrupt snapshot, a licence refusal) pass through and nothing is cached.
    /// </summary>
    public TopologyDto Get(CaseEntry entry, Func<string, PresolvedReplay> load)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(load);
        if (entry.Kind != CaseKind.Replay)
            throw new ArgumentException($"Case '{entry.Id}' is not a replay run.", nameof(entry));

        string fingerprint = Fingerprint(entry.Path);
        if (_entries.TryGetValue(entry.Id, out Entry? hit) && hit.Fingerprint == fingerprint) return hit.Topology;

        SemaphoreSlim gate = _gates.GetOrAdd(entry.Id, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            // Another caller may have built it while this one waited.
            if (_entries.TryGetValue(entry.Id, out hit) && hit.Fingerprint == fingerprint) return hit.Topology;

            PresolvedReplay r = load(entry.Id);
            TopologyDto topo = FrameProjector.BuildTopology(entry.Id, entry.Label, r.LayoutModel, replay: true, frameCount: r.Count);
            Interlocked.Increment(ref _builds);
            _entries[entry.Id] = new Entry(fingerprint, topo);
            return topo;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A text that changes whenever the files the replay is built from change: every <c>exports/*.json</c> and the two
    /// sidecar CSVs <see cref="PresolvedReplay"/> reads, each as name, length and last-write time (UTC ticks), in
    /// ordinal name order so the text does not depend on the order the OS lists files in.
    /// </summary>
    internal static string Fingerprint(string runDir)
    {
        var parts = new List<string>();
        string exports = Path.Combine(runDir, "exports");
        if (Directory.Exists(exports))
            foreach (string f in Directory.GetFiles(exports, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                parts.Add(Describe(f));
        foreach (string sidecar in new[] { "timeseries.csv", "state-estimate.csv" })
        {
            string f = Path.Combine(runDir, sidecar);
            if (File.Exists(f)) parts.Add(Describe(f));
        }
        return string.Join("|", parts);
    }

    private static string Describe(string file)
    {
        var fi = new FileInfo(file);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{fi.Name}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}");
    }
}
