using System.Globalization;
using GridSim.Core.IO;
using GridSim.Core.Layout;
using GridSim.Core.Model;

namespace GridSim.Core.Simulation;

/// <summary>
/// A loaded, pre-solved run: a cursor over the grid-state snapshots the CLI exported (the <c>exports/*.json</c>
/// from a <c>transient</c> or <c>gda-replay</c> run), reconstructed into the <see cref="SimFrame"/>s the live
/// views already bind to. Per-frame scalar telemetry (frequency, RoCoF, generation, demand, clock) comes from the
/// run's sidecar CSV where present, else is computed from the model. The map topology is constant across frames,
/// so a single laid-out model (<see cref="LayoutModel"/>) drives the renderer while only the result changes.
/// Exports are typically <b>sampled</b> (<c>--export-every</c>), so this steps through snapshots, not every slice.
/// </summary>
public sealed class PresolvedReplay
{
    public string Title { get; }
    public IReadOnlyList<SimFrame> Frames { get; }
    public GridModel LayoutModel { get; }   // frame 0's model with map coordinates, for renderer.Load
    private readonly IReadOnlyList<string> _labels;

    public int Index { get; private set; }
    public int Count => Frames.Count;
    public SimFrame Current => Frames[Math.Clamp(Index, 0, Count - 1)];
    public bool AtEnd => Index >= Count - 1;
    public string CurrentLabel => Label(Index);
    public string Label(int i) => _labels[Math.Clamp(i, 0, Math.Max(0, Count - 1))];

    public void Advance(int n = 1) => Index = Math.Clamp(Index + n, 0, Math.Max(0, Count - 1));
    public void Rewind(int n = 1) => Advance(-n);
    public void Seek(int i) => Index = Math.Clamp(i, 0, Math.Max(0, Count - 1));
    public void SeekFraction(double f) => Seek((int)Math.Round(Math.Clamp(f, 0, 1) * Math.Max(0, Count - 1)));
    public void Restart() => Index = 0;

    private PresolvedReplay(string title, IReadOnlyList<SimFrame> frames, IReadOnlyList<string> labels, GridModel layout)
    {
        Title = title;
        Frames = frames;
        _labels = labels;
        LayoutModel = layout;
    }

    /// <summary>True if <paramref name="dir"/> looks like a loadable run (has exports/slice-*.json or state-*.json).</summary>
    public static bool IsRunDirectory(string dir)
    {
        string exports = Path.Combine(dir, "exports");
        return Directory.Exists(exports) &&
               Directory.EnumerateFiles(exports, "*.json").Any(f => IndexOf(f) >= 0);
    }

    /// <summary>
    /// Load a run directory. D-DOC-061 (October 2026): every dated row of the run's sidecar (the <c>time_utc</c> column
    /// of a gda-replay <c>state-estimate.csv</c>) passes the horizon guard, so a replay of states newer than the horizon
    /// is refused with <see cref="Diagnostics.ForwardInferenceException"/> instead of being shown. This loader used to
    /// call no guard at all. A transient run's <c>t_seconds</c> is model time (seconds into a simulation), not a grid
    /// instant, so it is not checked; nor are the export files' run-time metadata stamps (when the CLI ran).
    /// </summary>
    /// <param name="runDir">The run directory (holds <c>exports/</c>).</param>
    /// <param name="utcNow">The guard's 'now': null is the real clock; a test may pass an earlier instant (never a later
    /// one, see <see cref="Diagnostics.ForwardInferenceGuard.RequireNotAfterRealClock"/>).</param>
    public static PresolvedReplay Load(string runDir, DateTime? utcNow = null)
    {
        DateTime now = Diagnostics.ForwardInferenceGuard.RequireNotAfterRealClock(utcNow, "the replay loader 'now'");
        string exportsDir = Path.Combine(runDir, "exports");
        if (!Directory.Exists(exportsDir))
            throw new DirectoryNotFoundException($"No 'exports' folder under {runDir}. Not a GridSim run directory.");

        var files = Directory.GetFiles(exportsDir, "*.json")
            .Select(f => (file: f, idx: IndexOf(f)))
            .Where(x => x.idx >= 0)
            .OrderBy(x => x.idx)
            .ToList();
        if (files.Count == 0)
            throw new InvalidDataException($"No slice-*.json / state-*.json snapshots in {exportsDir}.");
        // Two snapshots with the same index (slice-1.json and state-1.json) have no defined order: their relative order
        // was the file system's enumeration order, which differs between operating systems. Refuse, naming both.
        for (int k = 1; k < files.Count; k++)
            if (files[k].idx == files[k - 1].idx)
                throw new InvalidDataException(
                    $"Two snapshots in {exportsDir} have the same index {files[k].idx} ('{Path.GetFileName(files[k - 1].file)}' and " +
                    $"'{Path.GetFileName(files[k].file)}'); the replay order would depend on the file system. Keep one.");

        Dictionary<int, Telemetry> telemetry = LoadTelemetry(runDir, now);

        var frames = new List<SimFrame>(files.Count);
        var labels = new List<string>(files.Count);
        GridModel? layout = null;

        foreach ((string file, int idx) in files)
        {
            SolveSnapshot snap = SolveImport.ReadFile(file);
            layout ??= GraphLayout.EnsureLayout(snap.Model);

            telemetry.TryGetValue(idx, out Telemetry? row);
            double demand = row?.DemandMw ?? snap.Model.Buses.Sum(b => b.Pd);
            double gen = row?.GenMw ?? snap.Model.Generators.Sum(g => g.Pg);
            // R196: a frame with no telemetry frequency shows the snapshot model's own nominal frequency, not a
            // hard-coded 50 Hz (every bundled and imported model is 50 Hz today, so no displayed number moves).
            double freq = row?.FreqHz ?? snap.Model.FrequencyHz;
            double rocof = row?.RocofHzPerS ?? 0.0;
            double hour = row?.Hour ?? 0.0;

            frames.Add(new SimFrame(snap.Model, snap.Result, hour, idx, freq, gen, demand, rocof));
            labels.Add(row?.Label ?? $"#{idx}");
        }

        string title = Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new PresolvedReplay(title, frames, labels, layout!);
    }

    // slice-000000123.json -> 123 ; state-000003.json -> 3 ; anything else -> -1
    private static int IndexOf(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        if (!(n.StartsWith("slice-", StringComparison.OrdinalIgnoreCase) ||
              n.StartsWith("state-", StringComparison.OrdinalIgnoreCase)))
            return -1;
        int dash = n.IndexOf('-');
        // Digits only, invariant culture: the index is a file-name counter, never a culture-formatted number.
        return dash >= 0 && int.TryParse(n[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : -1;
    }

    private sealed record Telemetry(double? FreqHz, double? RocofHzPerS, double? GenMw, double? DemandMw, double? Hour, string? Label);

    // Parse the run's sidecar CSV (transient timeseries.csv or gda-replay state-estimate.csv), keyed by tick/index.
    private static Dictionary<int, Telemetry> LoadTelemetry(string runDir, DateTime utcNow)
    {
        var map = new Dictionary<int, Telemetry>();
        string? csv = new[] { "timeseries.csv", "state-estimate.csv" }
            .Select(f => Path.Combine(runDir, f)).FirstOrDefault(File.Exists);
        if (csv is null) return map;

        string[] lines = File.ReadAllLines(csv);
        int h = Array.FindIndex(lines, l => l.Length > 0 && !l.StartsWith('#'));
        if (h < 0) return map;

        string[] cols = lines[h].Split(',');
        int Col(string name) => Array.FindIndex(cols, c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        int iTick = Col("tick"), iIndex = Col("index");
        int iKey = iTick >= 0 ? iTick : iIndex;
        if (iKey < 0) return map;
        int iFreq = Col("freq_hz"), iRocof = Col("rocof_hz_s"), iGen = Col("gen_mw"), iDem = Col("demand_mw"),
            iT = Col("t_seconds"), iTime = Col("time_utc");

        for (int r = h + 1; r < lines.Length; r++)
        {
            string line = lines[r];
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] f = line.Split(',');
            // GR-872: a row whose key cell is missing or not a whole number is a broken file; it used to be skipped,
            // silently dropping that frame's telemetry.
            if (iKey >= f.Length || !int.TryParse(f[iKey].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int key))
                throw new InvalidDataException(
                    $"{Path.GetFileName(csv)} line {r + 1}: the '{cols[iKey].Trim()}' cell is missing or not a whole number.");
            // A repeated key silently overwrote the earlier row; the telemetry for a frame must be one row.
            if (map.ContainsKey(key))
                throw new InvalidDataException($"{Path.GetFileName(csv)} line {r + 1}: the key {key} appears twice; each frame has one telemetry row.");

            // A missing column or an EMPTY cell is "no value" (the frame falls back to the model); a present cell that
            // is not a number is a broken file and fails loud, naming the line and column, instead of falling back
            // silently as if it were empty.
            double? Num(int c)
            {
                if (c < 0 || c >= f.Length) return null;
                string cell = f[c].Trim();
                if (cell.Length == 0) return null;
                if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
                throw new InvalidDataException($"{Path.GetFileName(csv)} line {r + 1}: '{cols[c].Trim()}' value '{cell}' is not a number.");
            }

            double? hour = null;
            string? label = null;
            if (iT >= 0 && Num(iT) is double ts) { hour = ts / 3600.0; label = $"t={ts:F2}s"; }
            else if (iTime >= 0 && iTime < f.Length)
            {
                label = f[iTime].Trim();
                // A naive stamp (no 'Z' and no offset) is UTC by contract: the column is called time_utc. Without
                // AssumeUniversal, .NET reads a naive stamp as machine-local time and AdjustToUniversal then shifts
                // it by the local offset, so on a BST machine every frame's hour came out one hour early. With both
                // flags a naive stamp is taken as UTC, and a stamp that carries 'Z' or an offset is still converted
                // to UTC exactly as before.
                if (label.Length > 0)
                {
                    // ISO 8601 only (UtcTime.TryParseIso8601, as every other GridSim instant): the lenient parse read
                    // 01/02/2024 as 2 January. The writer (gda-replay) stamps the round-trip "o" form, which is ISO.
                    if (!Diagnostics.UtcTime.TryParseIso8601(label, out DateTime dt))
                    {
                        // Fail loud: a present but unreadable timestamp used to leave the hour silently at 0
                        // (midnight), which mislabels the replay clock rather than telling anyone the CSV is bad.
                        throw new InvalidDataException(
                            $"{Path.GetFileName(csv)} line {r + 1}: time_utc '{label}' is not a readable date/time.");
                    }
                    // D-DOC-061: a replayed state is a grid instant, so it must be older than the horizon.
                    Diagnostics.ForwardInferenceGuard.RejectFuture(dt, utcNow,
                        $"{Path.GetFileName(csv)} line {r + 1}: replayed state time_utc");
                    hour = dt.TimeOfDay.TotalHours;
                }
            }

            map[key] = new Telemetry(Num(iFreq), Num(iRocof), Num(iGen), Num(iDem), hour, label);
        }
        return map;
    }
}
