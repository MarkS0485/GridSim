using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.Gic;

/// <summary>
/// RC5 WP28: geomagnetically induced currents for a uniform geoelectric field, by nodal analysis of the DC network
/// (NERC application guide, December 2013, Appendix II). Each line's source V = E_N L_N + E_E L_E becomes a Norton pair
/// (g V out of its from node, into its to node); Y V = J is solved by the sparse LU in the network's fixed node order.
/// Before the solve, any node left with one element is removed with that element, which then carries exactly 0 A (a
/// blocked neutral, a stub line), repeatedly; a part of the network with no path to earth has its first node held at 0 V
/// (its potential is undefined, its currents are not). Deterministic: no unordered iteration, cosines by
/// <see cref="DetMath"/>.
/// </summary>
public static class GicSolver
{
    private const double Rad = Math.PI / 180.0;

    /// <summary>The effective GIC per phase, |I_H + I_X / a| (NERC Transformer Thermal Impact Assessment white paper,
    /// eq. 1: I_dc,eq = I_H + (I_N/3 - I_H) V_X / V_H, where I_N/3 - I_H is the X-terminal current per phase).</summary>
    public static double EffectiveCurrent(double ihA, double ixA, double ratio)
    {
        if (!(ratio > 0) || !double.IsFinite(ratio)) throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "The turns ratio V_H / V_X must be a finite number above 0.");
        return Math.Abs(ihA + ixA / ratio);
    }

    /// <summary>The reactive loss Q = k V I_eff (Mvar), k in Mvar per ampere at 1 pu.</summary>
    public static double ReactiveLossMvar(double kMvarPerA, double voltagePu, double effectiveA) => kMvarPerA * voltagePu * effectiveA;

    /// <summary>Solve for the uniform field (<paramref name="northVPerKm"/>, <paramref name="eastVPerKm"/>), V/km.</summary>
    public static GicResult Solve(GicNetwork net, double northVPerKm, double eastVPerKm)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (!double.IsFinite(northVPerKm)) throw new ArgumentOutOfRangeException(nameof(northVPerKm), northVPerKm, "The field must be finite.");
        if (!double.IsFinite(eastVPerKm)) throw new ArgumentOutOfRangeException(nameof(eastVPerKm), eastVPerKm, "The field must be finite.");
        IReadOnlyList<GicElement> el = net.Elements;
        int n = net.NodeCount;

        // 1. Remove one-element nodes (their element carries no current), until none is left.
        bool[] active = new bool[el.Count];
        int[] degree = new int[n];
        for (int k = 0; k < el.Count; k++)
        {
            active[k] = true;
            degree[el[k].A]++;
            if (el[k].B >= 0) degree[el[k].B]++;
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int k = 0; k < el.Count; k++)
            {
                if (!active[k]) continue;
                GicElement e = el[k];
                if (degree[e.A] == 1 || (e.B >= 0 && degree[e.B] == 1))
                {
                    active[k] = false;
                    degree[e.A]--;
                    if (e.B >= 0) degree[e.B]--;
                    changed = true;
                }
            }
        }

        // 2. Components; a component with no ground element has its first node held at 0 V.
        int[] comp = new int[n];
        Array.Fill(comp, -1);
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        for (int k = 0; k < el.Count; k++)
        {
            if (!active[k] || el[k].B < 0) continue;
            adj[el[k].A].Add(el[k].B);
            adj[el[k].B].Add(el[k].A);
        }
        int nComp = 0;
        var compFirst = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (comp[i] >= 0 || degree[i] == 0) continue;
            var stack = new Stack<int>();
            stack.Push(i);
            comp[i] = nComp;
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                foreach (int y in adj[x])
                    if (comp[y] < 0) { comp[y] = nComp; stack.Push(y); }
            }
            compFirst.Add(i);
            nComp++;
        }
        bool[] grounded = new bool[nComp];
        for (int k = 0; k < el.Count; k++)
            if (active[k] && el[k].B < 0) grounded[comp[el[k].A]] = true;
        int[] unknown = new int[n];
        int m = 0;
        for (int i = 0; i < n; i++)
            unknown[i] = degree[i] == 0 || (!grounded[comp[i]] && compFirst[comp[i]] == i) ? -1 : m++;

        // 3. Y V = J.
        double[] v = new double[n];
        if (m > 0)
        {
            var yb = new CsrMatrix.Builder(m);
            double[] j = new double[m];
            for (int k = 0; k < el.Count; k++)
            {
                if (!active[k]) continue;
                GicElement e = el[k];
                int ia = unknown[e.A], ib = e.B >= 0 ? unknown[e.B] : -1;
                double g = e.Siemens;
                if (ia >= 0) yb.Add(ia, ia, g);
                if (ib >= 0) yb.Add(ib, ib, g);
                if (ia >= 0 && ib >= 0) { yb.Add(ia, ib, -g); yb.Add(ib, ia, -g); }
                if (e.Kind == GicElementKind.Line)
                {
                    double gv = g * Source(e, northVPerKm, eastVPerKm);
                    if (ia >= 0) j[ia] -= gv;
                    if (ib >= 0) j[ib] += gv;
                }
            }
            double[] x = new CpuSparseLuSolver().Solve(yb.Build(), j)
                ?? throw new InvalidOperationException("The GIC network matrix is singular.");
            for (int i = 0; i < n; i++) if (unknown[i] >= 0) v[i] = x[unknown[i]];
        }

        // 4. Element currents, A to B (all three phases).
        double[] cur = new double[el.Count];
        for (int k = 0; k < el.Count; k++)
        {
            if (!active[k]) continue;
            GicElement e = el[k];
            double vb = e.B >= 0 ? v[e.B] : 0.0;
            double src = e.Kind == GicElementKind.Line ? Source(e, northVPerKm, eastVPerKm) : 0.0;
            cur[k] = e.Siemens * (v[e.A] - vb + src);
        }

        var subs = new List<GicSubstationResult>(net.Substations.Count);
        var lines = new List<GicLineResult>(net.Lines.Count);
        double[] series = new double[net.Units.Count], common = new double[net.Units.Count], hvw = new double[net.Units.Count], lvw = new double[net.Units.Count];
        for (int k = 0; k < el.Count; k++)
        {
            GicElement e = el[k];
            switch (e.Kind)
            {
                case GicElementKind.Ground:
                    subs.Add(new GicSubstationResult(net.Substations[e.Owner].Name, cur[k], v[e.A]));
                    break;
                case GicElementKind.Line:
                    GicLine l = net.Lines[e.Owner];
                    lines.Add(new GicLineResult(l.Branch, l.FromBus, l.ToBus, l.NorthKm, l.EastKm, Source(e, northVPerKm, eastVPerKm), l.OhmsPerPhase, cur[k]));
                    break;
                case GicElementKind.HvWinding: hvw[e.Owner] = cur[k]; break;
                case GicElementKind.LvWinding: lvw[e.Owner] = cur[k]; break;
                case GicElementKind.Series: series[e.Owner] = cur[k]; break;
                case GicElementKind.Common: common[e.Owner] = cur[k]; break;
            }
        }
        var units = new List<GicTransformerResult>(net.Units.Count);
        for (int u = 0; u < net.Units.Count; u++)
        {
            GicUnit un = net.Units[u];
            (double ih, double ix) = un.Data.Type switch
            {
                GicTransformerType.Gsu => (hvw[u] / 3.0, 0.0),
                GicTransformerType.GyGy => (hvw[u] / 3.0, lvw[u] / 3.0),
                GicTransformerType.Auto => (series[u] / 3.0, common[u] / 3.0 - series[u] / 3.0),
                GicTransformerType.DGy => (0.0, lvw[u] / 3.0),
                _ => (0.0, 0.0),
            };
            double neutral = un.Data.Type switch
            {
                GicTransformerType.Gsu => hvw[u],
                GicTransformerType.GyGy => hvw[u] + lvw[u],
                GicTransformerType.Auto => common[u],
                GicTransformerType.DGy => lvw[u],
                _ => 0.0,
            };
            double ieff = EffectiveCurrent(ih, ix, un.Ratio);
            double? q = un.Data.KMvarPerA is double kq ? ReactiveLossMvar(kq, un.VoltagePu, ieff) : null;
            units.Add(new GicTransformerResult(un.Data.Branch, un.Data.Type, un.HvBus, un.LvBus, un.Ratio, ih, ix, neutral, ieff, un.VoltagePu, q));
        }
        return new GicResult(northVPerKm, eastVPerKm, subs, units, lines);
    }

    /// <summary>The largest effective GIC of each transformer over field directions 0, step, 2 step, ... below 360 degrees
    /// (from north towards east) at 1 V/km: E_N = cos theta, E_E = sin theta.</summary>
    public static IReadOnlyList<GicSweepEntry> Sweep(GicNetwork net, double stepDeg)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (!(stepDeg > 0) || stepDeg > 180 || !double.IsFinite(stepDeg))
            throw new ArgumentOutOfRangeException(nameof(stepDeg), stepDeg, "The sweep step must be above 0 and at most 180 degrees.");
        double[] best = new double[net.Units.Count];
        double[] at = new double[net.Units.Count];
        Array.Fill(best, -1.0);
        for (int k = 0; k * stepDeg < 360.0; k++)
        {
            double th = k * stepDeg;
            GicResult r = Solve(net, DetMath.Cos(th * Rad), DetMath.Sin(th * Rad));
            for (int u = 0; u < r.Transformers.Count; u++)
                if (r.Transformers[u].EffectiveA > best[u]) { best[u] = r.Transformers[u].EffectiveA; at[u] = th; }
        }
        var list = new List<GicSweepEntry>(net.Units.Count);
        for (int u = 0; u < net.Units.Count; u++) list.Add(new GicSweepEntry(net.Units[u].Data.Branch, best[u], at[u]));
        return list;
    }

    private static double Source(GicElement e, double en, double ee) => en * e.NorthKm + ee * e.EastKm;
}
