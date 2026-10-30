using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Physics.Experimental;

/// <summary>
/// EXPERIMENTAL: a propagating electromechanical-wave field for visualisation. A disturbance at one bus
/// does not reach the whole system at once - it spreads as an electromechanical wave at a FINITE speed
/// (the same physics <see cref="PropagationDelay"/> models and <see cref="ContinuumWaveModel"/> gives
/// the frequency of). This computes, per bus, an amplitude and a PHASE DELAY (time-of-flight from the
/// source), so an animator can draw <c>amplitude_i * cos(2*pi*f*t - phase_i)</c> and show the ripple
/// travelling outward at the wave speed - the fluid analogy made visible.
///
/// PROVENANCE: purely a visualisation aid derived from the linear wave model - the frequency is the
/// dominant inter-area mode, the delay is distance / wave speed, and the amplitude decays with distance.
/// It is not a transient simulation. Opt-in (the map's WAVE mode).
/// </summary>
public static class WaveField
{
    private const double WaveSpeedKmPerS = 1000.0; // characteristic electromechanical wave speed

    /// <summary>Wave frequency (Hz) used when the case has no inertia data to derive the dominant inter-area mode
    /// from: 0.5 Hz, the middle of the typical 0.1-1 Hz inter-area band (R187).</summary>
    public const double NominalWaveHz = 0.5;

    /// <summary>A derived dominant inter-area frequency at or below this (Hz) is not a usable mode (a rigid-body
    /// or numerically zero mode); the nominal frequency is kept instead (R187).</summary>
    public const double MinModalHz = 1e-3;
    private const double HopKm = 120.0;            // nominal spacing when a case has no map coordinates
    private const double SpanXKm = 660.0, SpanYKm = 1020.0; // GB-scale spans for 0..1 layout coords

    /// <summary>One bus's place in the wave: its ripple amplitude (0..1) and phase delay (radians).</summary>
    public sealed record NodeWave(int Bus, double Amplitude, double PhaseRad);

    /// <summary>The propagating-wave field for a solved case.</summary>
    public sealed record WaveFieldResult(double FrequencyHz, int SourceBus, double WaveSpeedKmPerS,
        IReadOnlyList<NodeWave> Nodes);

    /// <summary>Compute the wave field radiating from <paramref name="sourceBusId"/> (default: the
    /// largest generator). Frequency is the dominant inter-area mode where inertia data allows, else a
    /// nominal 0.5 Hz. Distances use the map coordinates when present, else the hop count.</summary>
    public static WaveFieldResult Compute(GridModel model, PowerFlowResult result, int? sourceBusId = null)
    {
        // R018 / N021 (October 2026): the wave model is only attempted when it can form. On a case it cannot form
        // (gb-full: 4,366 generators with H = 0) the call used to build a dense YBus and the dense (n + g)^2
        // augmented admittance, 1,456,645,640 B on gb-full, only to throw; CanForm is false exactly when that throw
        // is certain, so skipping the call gives the nominal frequency the catch gave.
        ContinuumWaveModel.WaveModes? modes = null;
        if (ContinuumWaveModel.CanForm(model, result))
        {
            try { modes = ContinuumWaveModel.FromSolvedCase(model, result); }
            catch (InvalidOperationException) { /* failed loud for a reason of its own: keep the nominal frequency */ }
        }
        return ComputeFromModes(model, result, modes, sourceBusId);
    }

    /// <summary>
    /// <see cref="Compute"/> for a caller that has already run <see cref="ContinuumWaveModel.FromSolvedCase"/> on the
    /// SAME model and result (the web EXPERIMENTAL projection), so the wave model is not built twice per frame
    /// (R018). Pass its result as <paramref name="modes"/>, or null when that call threw
    /// <see cref="InvalidOperationException"/>; the field is then bit-for-bit what <see cref="Compute"/> returns,
    /// because <see cref="ContinuumWaveModel.FromSolvedCase"/> is deterministic and this is the rest of
    /// <see cref="Compute"/> unchanged.
    /// </summary>
    public static WaveFieldResult ComputeFromModes(GridModel model, PowerFlowResult result,
        ContinuumWaveModel.WaveModes? modes, int? sourceBusId = null)
    {
        int n = model.Buses.Count;
        var idx = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idx[model.Buses[i].Id] = i;

        double freq = NominalWaveHz;
        if (modes is not null) { double f = modes.DominantInterAreaHz; if (f > MinModalHz) freq = f; }

        int source = sourceBusId ?? LargestGeneratorBus(model, result);
        if (!idx.ContainsKey(source)) source = model.Buses.Count > 0 ? model.Buses[0].Id : 0;

        double[] distKm = DistancesKm(model, idx, source);
        double maxDist = distKm.Length > 0 ? Math.Max(1.0, distKm.Max()) : 1.0;
        double lambda = maxDist / 2.0; // amplitude e-folding length
        double omega = 2.0 * Math.PI * freq;

        var nodes = new List<NodeWave>(n);
        for (int i = 0; i < n; i++)
        {
            double amp = DetMath.Exp(-distKm[i] / lambda);
            double phase = omega * (distKm[i] / WaveSpeedKmPerS); // time-of-flight phase delay
            nodes.Add(new NodeWave(model.Buses[i].Id, amp, phase));
        }
        return new WaveFieldResult(freq, source, WaveSpeedKmPerS, nodes);
    }

    private static int LargestGeneratorBus(GridModel model, PowerFlowResult result)
    {
        var busIdx = new Dictionary<int, int>();
        for (int i = 0; i < result.BusIds.Count; i++) busIdx[result.BusIds[i]] = i;
        int best = model.Buses.Count > 0 ? model.Buses[0].Id : 0;
        double bestMw = double.NegativeInfinity;
        foreach (Generator g in model.Generators)
        {
            double mw = busIdx.TryGetValue(g.Bus, out int i) ? result.PInjMw[i] : g.Pg;
            if (mw > bestMw) { bestMw = mw; best = g.Bus; }
        }
        return best;
    }

    /// <summary>Distance (km) from the source to every bus: Euclidean over the map coordinates when the
    /// case carries a layout, else a breadth-first hop count times a nominal spacing.</summary>
    private static double[] DistancesKm(GridModel model, Dictionary<int, int> idx, int source)
    {
        int n = model.Buses.Count;
        bool hasCoords = model.Buses.Any(b => b.X != 0.0 || b.Y != 0.0);
        int s = idx[source];

        if (hasCoords)
        {
            double sx = model.Buses[s].X, sy = model.Buses[s].Y;
            var d = new double[n];
            for (int i = 0; i < n; i++)
            {
                double dx = (model.Buses[i].X - sx) * SpanXKm;
                double dy = (model.Buses[i].Y - sy) * SpanYKm;
                d[i] = Math.Sqrt(dx * dx + dy * dy);
            }
            return d;
        }

        // BFS hop distance over the branch graph.
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (Branch br in model.Branches)
        {
            if (!br.InService || !idx.TryGetValue(br.From, out int f) || !idx.TryGetValue(br.To, out int t)) continue;
            adj[f].Add(t); adj[t].Add(f);
        }
        var hops = new double[n];
        Array.Fill(hops, -1.0);
        var queue = new Queue<int>();
        hops[s] = 0; queue.Enqueue(s);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int v in adj[u]) if (hops[v] < 0) { hops[v] = hops[u] + 1; queue.Enqueue(v); }
        }
        for (int i = 0; i < n; i++) hops[i] = (hops[i] < 0 ? 0 : hops[i]) * HopKm; // unreachable -> at source
        return hops;
    }
}
