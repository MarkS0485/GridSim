using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Protection.Placed;

/// <summary>An unreadable swing trace (an input error at the CLI).</summary>
public sealed class SwingTraceException(string message) : StudyCaseException(message);

/// <summary>
/// RC5 WP17: a sampled positive-sequence bus-voltage trace for power-swing blocking. The text is CSV: lines that are blank
/// or start with '#' are skipped; the first other line is the header, <c>t_s</c> followed by pairs <c>vm_&lt;bus&gt;</c>
/// (pu) and <c>va_&lt;bus&gt;</c> (degrees) in any order; every later line is one sample, numbers in invariant culture.
/// The times must increase.
/// <para>The text may instead be a <c>dynamics-study</c> result file (JSON, recognised by its first character '{'). Its
/// one simulated case's <c>run.timeSeconds</c> are the times, and each pair of <c>busVoltage N</c> channels (unit
/// <c>pu</c> for the magnitude, <c>deg</c> for the angle) gives bus N, exactly as the CSV columns <c>vm_N</c> and
/// <c>va_N</c> would. A result with no simulated case, or with more than one, is refused: keep one fixed-time case in the
/// study.</para>
/// </summary>
public sealed class SwingTrace
{
    private readonly Dictionary<int, Complex[]> _v;

    private SwingTrace(double[] t, Dictionary<int, Complex[]> v) { TimeS = t; _v = v; }

    public IReadOnlyList<double> TimeS { get; }

    /// <summary>The bus IDs the trace carries, ascending.</summary>
    public IReadOnlyList<int> Buses
    {
        get
        {
            var ids = new List<int>(_v.Keys);
            ids.Sort();
            return ids;
        }
    }

    public bool TryVoltage(int bus, out IReadOnlyList<Complex> v)
    {
        bool ok = _v.TryGetValue(bus, out Complex[]? a);
        v = a ?? Array.Empty<Complex>();
        return ok;
    }

    public static SwingTrace Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReadOnlySpan<char> lead = text.AsSpan().TrimStart();
        if (lead.Length > 0 && lead[0] == '{') return ParseDynamicsResult(text);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[]? header = null;
        int lineNo = 0;
        var rows = new List<(int Line, string[] Cells)>();
        foreach (string raw in lines)
        {
            lineNo++;
            string l = raw.Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            string[] cells = l.Split(',');
            for (int c = 0; c < cells.Length; c++) cells[c] = cells[c].Trim();
            if (header is null) header = cells;
            else rows.Add((lineNo, cells));
        }
        if (header is null) throw new SwingTraceException("the swing trace has no header line.");
        if (header[0] != "t_s") throw new SwingTraceException($"the swing trace header must start with t_s, not \"{header[0]}\".");
        var vmCol = new Dictionary<int, int>();
        var vaCol = new Dictionary<int, int>();
        for (int c = 1; c < header.Length; c++)
        {
            string h = header[c];
            bool isVm = h.StartsWith("vm_", StringComparison.Ordinal), isVa = h.StartsWith("va_", StringComparison.Ordinal);
            if (!isVm && !isVa) throw new SwingTraceException($"swing trace column \"{h}\": expected vm_<bus> or va_<bus>.");
            if (!int.TryParse(h.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bus))
                throw new SwingTraceException($"swing trace column \"{h}\": the bus ID is not a whole number.");
            if (!(isVm ? vmCol : vaCol).TryAdd(bus, c)) throw new SwingTraceException($"swing trace column \"{h}\" appears twice.");
        }
        foreach (int bus in vmCol.Keys) if (!vaCol.ContainsKey(bus)) throw new SwingTraceException($"swing trace: bus {bus} has vm_{bus} but no va_{bus}.");
        foreach (int bus in vaCol.Keys) if (!vmCol.ContainsKey(bus)) throw new SwingTraceException($"swing trace: bus {bus} has va_{bus} but no vm_{bus}.");
        if (rows.Count == 0) throw new SwingTraceException("the swing trace has no samples.");
        var t = new double[rows.Count];
        var v = new Dictionary<int, Complex[]>();
        foreach (int bus in vmCol.Keys) v[bus] = new Complex[rows.Count];
        for (int k = 0; k < rows.Count; k++)
        {
            (int line, string[] cells) = rows[k];
            if (cells.Length != header.Length) throw new SwingTraceException($"swing trace line {line}: {cells.Length} values, the header has {header.Length}.");
            t[k] = Num(cells[0], line);
            if (k > 0 && !(t[k] > t[k - 1])) throw new SwingTraceException($"swing trace line {line}: the time must increase.");
            foreach (KeyValuePair<int, int> kv in vmCol)
                v[kv.Key][k] = DetMath.FromPolar(Num(cells[kv.Value], line), Num(cells[vaCol[kv.Key]], line) * Math.PI / 180.0);
        }
        return new SwingTrace(t, v);
    }

    /// <summary>A dynamics-study result file: the one simulated case's time column and busVoltage channel pairs.</summary>
    private static SwingTrace ParseDynamicsResult(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new SwingTraceException($"the swing trace looks like JSON but cannot be read: {ex.Message}"); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cases", out JsonElement cases) || cases.ValueKind != JsonValueKind.Array)
                throw new SwingTraceException("a JSON swing trace must be a dynamics-study result file (it has no \"cases\" array).");
            var runs = new List<(string Name, JsonElement Run)>();
            foreach (JsonElement c in cases.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("run", out JsonElement r) && r.ValueKind == JsonValueKind.Object)
                    runs.Add((c.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "?", r));
            if (runs.Count == 0) throw new SwingTraceException("the dynamics-study result has no simulated case, so it has no voltage trace.");
            if (runs.Count > 1)
                throw new SwingTraceException($"the dynamics-study result has {runs.Count.ToString(CultureInfo.InvariantCulture)} simulated cases ({string.Join(", ", runs.Select(x => x.Name))}); "
                    + "the swing trace needs one: keep one fixed-time case in the study.");
            JsonElement run = runs[0].Run;
            if (!run.TryGetProperty("timeSeconds", out JsonElement times) || times.ValueKind != JsonValueKind.Array)
                throw new SwingTraceException("the dynamics-study run has no timeSeconds array.");
            double[] t = Numbers(times, "timeSeconds");
            if (t.Length == 0) throw new SwingTraceException("the swing trace has no samples.");
            for (int k = 1; k < t.Length; k++)
                if (!(t[k] > t[k - 1])) throw new SwingTraceException($"dynamics-study timeSeconds[{k.ToString(CultureInfo.InvariantCulture)}]: the time must increase.");
            var vm = new Dictionary<int, double[]>();
            var va = new Dictionary<int, double[]>();
            if (run.TryGetProperty("channels", out JsonElement channels) && channels.ValueKind == JsonValueKind.Array)
                foreach (JsonElement ch in channels.EnumerateArray())
                {
                    // ANA-09: a channel that is not an object is refused (TryGetProperty threw InvalidOperationException).
                    if (ch.ValueKind != JsonValueKind.Object) throw new SwingTraceException("dynamics-study channels: every channel must be an object.");
                    if (!(ch.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "busVoltage")) continue;
                    string label = ch.TryGetProperty("label", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "";
                    if (!label.StartsWith("busVoltage ", StringComparison.Ordinal)
                        || !int.TryParse(label.AsSpan(11), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bus))
                        throw new SwingTraceException($"dynamics-study channel \"{label}\": expected the label busVoltage <bus>.");
                    string unit = ch.TryGetProperty("unit", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
                    Dictionary<int, double[]> into = unit switch
                    {
                        "pu" => vm,
                        "deg" => va,
                        _ => throw new SwingTraceException($"dynamics-study channel \"{label}\": unit \"{unit}\", expected pu or deg."),
                    };
                    if (!ch.TryGetProperty("values", out JsonElement vals) || vals.ValueKind != JsonValueKind.Array)
                        throw new SwingTraceException($"dynamics-study channel \"{label}\" has no values array.");
                    double[] x = Numbers(vals, label);
                    if (x.Length != t.Length)
                        throw new SwingTraceException($"dynamics-study channel \"{label}\" ({unit}) has {x.Length.ToString(CultureInfo.InvariantCulture)} values for {t.Length.ToString(CultureInfo.InvariantCulture)} times.");
                    if (!into.TryAdd(bus, x)) throw new SwingTraceException($"dynamics-study channel \"{label}\" ({unit}) appears twice.");
                }
            foreach (int bus in vm.Keys) if (!va.ContainsKey(bus)) throw new SwingTraceException($"dynamics-study result: bus {bus.ToString(CultureInfo.InvariantCulture)} has a voltage magnitude but no angle.");
            foreach (int bus in va.Keys) if (!vm.ContainsKey(bus)) throw new SwingTraceException($"dynamics-study result: bus {bus.ToString(CultureInfo.InvariantCulture)} has a voltage angle but no magnitude.");
            if (vm.Count == 0) throw new SwingTraceException("the dynamics-study run has no busVoltage channel; add one for each end of the relay's branch.");
            var v = new Dictionary<int, Complex[]>();
            foreach (KeyValuePair<int, double[]> kv in vm)
            {
                double[] ang = va[kv.Key];
                var z = new Complex[t.Length];
                for (int k = 0; k < z.Length; k++) z[k] = DetMath.FromPolar(kv.Value[k], ang[k] * Math.PI / 180.0);
                v[kv.Key] = z;
            }
            return new SwingTrace(t, v);
        }
    }

    private static double[] Numbers(JsonElement array, string what)
    {
        var x = new double[array.GetArrayLength()];
        int k = 0;
        foreach (JsonElement e in array.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double d) || !double.IsFinite(d))
                throw new SwingTraceException($"dynamics-study {what}[{k.ToString(CultureInfo.InvariantCulture)}] is not a finite number.");
            x[k++] = d;
        }
        return x;
    }

    private static double Num(string s, int line) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && double.IsFinite(x) ? x
            : throw new SwingTraceException($"swing trace line {line}: \"{s}\" is not a finite number.");

    /// <summary>
    /// The positive-sequence impedance a device measures over the trace, secondary ohms: V_near / I_near, with the current
    /// into the branch at the device's end from the branch's pi stamp on the two bus voltages (exact for the algebraic
    /// network of an RMS study while the branch itself is healthy and its tap is the case's: the stamp uses the case's
    /// static tap, so a tap changer that moves during the RMS run is not followed). A sample with no current (|I| at or below
    /// <see cref="ProtectionContext.MinApparentPowerPu"/>) gives NaN, which lies inside no blinder. Null when a bus of the
    /// branch is not in the trace or the near bus has no base kV.
    /// </summary>
    public IReadOnlyList<Complex>? Impedance(GridModel model, PlacedDevice d)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(d);
        Branch br = model.Branches[d.Location.BranchIndex];
        if (!_v.TryGetValue(br.From, out Complex[]? vf) || !_v.TryGetValue(br.To, out Complex[]? vt)) return null;
        double kv = 0.0;
        foreach (Bus bus in model.Buses) if (bus.Id == d.Location.NearBus) { kv = bus.BaseKv; break; }
        if (!(kv > 0.0)) return null;
        double ohms = PlacedElementEvaluator.OhmsPerPu(model.BaseMva, kv, d);
        BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
        var z = new Complex[TimeS.Count];
        for (int k = 0; k < z.Length; k++)
        {
            Complex i = d.Location.AtFromEnd ? s.Yff * vf[k] + s.Yft * vt[k] : s.Ytf * vf[k] + s.Ytt * vt[k];
            Complex v = d.Location.AtFromEnd ? vf[k] : vt[k];
            z[k] = i.Magnitude > ProtectionContext.MinApparentPowerPu ? v / i * ohms : new Complex(double.NaN, double.NaN);
        }
        return z;
    }
}
