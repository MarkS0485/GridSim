using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Core.Protection.Placed;

/// <summary>RC5 WP18: one zone of an 87B evaluation, per phase (a, b, c), in secondary amps of the reference CT: the
/// differential, bias and threshold currents, and every feeder current into the zone (the CTs in file order, then one
/// entry per zone bus that carries units, loads or shunts).</summary>
public sealed record BusZoneResult(double[] IdA, double[] IbA, double[] ThresholdA, IReadOnlyList<Complex[]> FeedersA, int DecidingPhase, bool Operates);

/// <summary>RC5 WP18: one 87B evaluation: the main zone and, when set, the check zone. A trip needs both.</summary>
public sealed record BusDifferentialResult(BusZoneResult Zone, BusZoneResult? CheckZone)
{
    public bool Operates => Zone.Operates && (CheckZone?.Operates ?? true);
}

/// <summary>
/// RC5 WP18: the low-impedance busbar differential (ANSI 87B), by Kirchhoff's current law over the zone.
/// <para>Id = |sum of the feeder currents into the zone| per phase, every current scaled to secondary amps of the reference
/// CT (the largest primary). Ib is the sum (bias "sum") or the largest (bias "max") of the feeder current magnitudes
/// (Horowitz and Phadke, Power System Relaying, 4th ed., ch. 9). Operate when Id &gt; K1 Ib + Is1, or by the dual-slope
/// 87L threshold when Is2 and K2 are given.</para>
/// <para>The feeders are the CTs on the branches that leave the zone, and the units, loads and shunts at each zone bus,
/// taken together as one feeder per bus: their net injection, read from the solved fault state by KCL at that bus (the
/// branch currents leaving it, plus the fault current when the bus is the faulted one). Zone completeness (G7): every
/// in-service branch with exactly one end in the zone needs a CT at its zone end; otherwise the element is not
/// evaluated and the missing branches are named.</para>
/// <para>Ideal CTs on fundamental phasors: no CT saturation detection. High-impedance 87B is out of scope.</para>
/// </summary>
public static class BusDifferential
{
    /// <summary>The bind-time checks of an 87B.</summary>
    internal static void Check(string at, BusDifferentialSetting s, GridModel model)
    {
        static bool Fin(double v) => double.IsFinite(v);
        var ids = new HashSet<int>(model.Buses.Select(b => b.Id));
        HashSet<int> Set(IReadOnlyList<int> list, string what)
        {
            if (list.Count == 0) throw new DeviceSetException($"{at}: the {what} lists no bus.");
            var set = new HashSet<int>();
            foreach (int b in list)
            {
                if (!ids.Contains(b)) throw new DeviceSetException($"{at}: the {what} names bus {b}, which is not in the case.");
                if (!set.Add(b)) throw new DeviceSetException($"{at}: the {what} names bus {b} twice.");
            }
            return set;
        }
        HashSet<int> zone = Set(s.Zone, "zone");
        HashSet<int>? check = s.CheckZone is { } cz ? Set(cz, "check zone") : null;
        if (check is not null && !zone.IsSubsetOf(check)) throw new DeviceSetException($"{at}: the check zone must hold every bus of the zone.");
        if (s.Cts.Count == 0) throw new DeviceSetException($"{at}: the busbar differential lists no CT.");
        var seen = new HashSet<(int, string)>();
        for (int i = 0; i < s.Cts.Count; i++)
        {
            BusCt c = s.Cts[i];
            string ca = $"{at}: CT {i + 1}";
            if (c.At.BranchIndex < 0 || c.At.BranchIndex >= model.Branches.Count)
                throw new DeviceSetException($"{ca} branch {c.At.BranchIndex} is outside the case (0..{model.Branches.Count - 1}).");
            Branch br = model.Branches[c.At.BranchIndex];
            if (br.From != c.At.From || br.To != c.At.To)
                throw new DeviceSetException($"{ca} branch {c.At.BranchIndex} joins {br.From}-{br.To} in the case, not {c.At.From}-{c.At.To}.");
            if (!(c.PrimaryA > 0.0) || !(c.SecondaryA > 0.0) || !Fin(c.PrimaryA) || !Fin(c.SecondaryA))
                throw new DeviceSetException($"{ca}: the CT primary and secondary amps must be finite and > 0.");
            if (!seen.Add((c.At.BranchIndex, c.At.End))) throw new DeviceSetException($"{ca} repeats a CT on branch {c.At.BranchIndex} at its {c.At.End} end.");
            int other = c.At.AtFromEnd ? br.To : br.From;
            bool onZone = zone.Contains(c.At.Bus) && !zone.Contains(other);
            bool onCheck = check is not null && check.Contains(c.At.Bus) && !check.Contains(other);
            if (!onZone && !onCheck)
                throw new DeviceSetException($"{ca} on branch {c.At.BranchIndex} must sit at the zone end of a branch that leaves the zone (or the check zone).");
        }
        if (!(s.Is1A > 0.0) || !Fin(s.Is1A)) throw new DeviceSetException($"{at}: is1A must be finite and > 0 A.");
        if (!(s.K1 >= 0.0) || !Fin(s.K1)) throw new DeviceSetException($"{at}: k1 must be finite and >= 0.");
        if (s.Is2A is double is2 && (!(is2 > 0.0) || !Fin(is2))) throw new DeviceSetException($"{at}: is2A must be finite and > 0 A.");
        if (s.K2 is double k2 && (!Fin(k2) || k2 < s.K1)) throw new DeviceSetException($"{at}: the second slope k2 must be finite and not below k1.");
    }

    /// <summary>The in-service branches with exactly one end in <paramref name="zone"/> that carry no CT at their zone end
    /// (zone completeness, G7), in branch order.</summary>
    public static IReadOnlyList<int> MissingCts(GridModel model, BusDifferentialSetting s, IReadOnlyList<int> zone)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(s); ArgumentNullException.ThrowIfNull(zone);
        var set = new HashSet<int>(zone);
        var missing = new List<int>();
        for (int l = 0; l < model.Branches.Count; l++)
        {
            Branch br = model.Branches[l];
            if (!br.InService) continue;
            bool f = set.Contains(br.From), t = set.Contains(br.To);
            if (f == t) continue;
            string end = f ? "from" : "to";
            if (!s.Cts.Any(c => c.At.BranchIndex == l && c.At.End == end)) missing.Add(l);
        }
        return missing;
    }

    /// <summary>The G7 refusal text for missing CTs (null when the zone is complete).</summary>
    public static string? Incomplete(GridModel model, BusDifferentialSetting s)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(s);
        string? Text(IReadOnlyList<int> zone, string what)
        {
            IReadOnlyList<int> m = MissingCts(model, s, zone);
            if (m.Count == 0) return null;
            return $"the {what} is incomplete: " + string.Join(", ", m.Select(l => $"branch {l} ({model.Branches[l].From}-{model.Branches[l].To})")) + " leave(s) it without a CT";
        }
        string? a = Text(s.Zone, "zone");
        string? b = s.CheckZone is { } cz ? Text(cz, "check zone") : null;
        return a is null ? b : b is null ? a : a + "; " + b;
    }

    /// <summary>Whether a fault lies inside the zone: at a zone bus, or along a branch with both ends in the zone.</summary>
    public static bool IsInternal(GridModel model, BusDifferentialSetting s, FaultSpec fault)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(s); ArgumentNullException.ThrowIfNull(fault);
        if (FaultBus(model, fault) is int b) return s.Zone.Contains(b);
        Branch br = model.Branches[fault.Branch!.Value];
        return s.Zone.Contains(br.From) && s.Zone.Contains(br.To);
    }

    /// <summary>The bus a fault sits at: its bus, or the end bus of a branch fault at fraction 0 (From) or 1 (To), which the
    /// line split also applies as a fault at that bus; null for a fault inside a branch.</summary>
    internal static int? FaultBus(GridModel model, FaultSpec fault)
    {
        if (fault.Bus is int b) return b;
        Branch br = model.Branches[fault.Branch!.Value];
        return fault.Fraction == 0.0 ? br.From : fault.Fraction == 1.0 ? br.To : null;
    }

    /// <summary>Whether a bus carries a load, a shunt, a unit or an induction machine (current no branch CT measures).</summary>
    internal static bool HasLocalElements(GridModel m, Bus b) =>
        b.Pd != 0.0 || b.Qd != 0.0 || b.Gs != 0.0 || b.Bs != 0.0 || m.Generators.Any(g => g.Bus == b.Id) || m.InductionMachines.Any(x => x.Bus == b.Id);

    /// <summary>Evaluate one 87B at one fault. Null (with the reason) when it cannot be evaluated.</summary>
    public static BusDifferentialResult? Evaluate(BoundDeviceSet bound, FaultPhasors fault, PlacedDevice d, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(bound); ArgumentNullException.ThrowIfNull(fault); ArgumentNullException.ThrowIfNull(d);
        BusDifferentialSetting s = d.BusDifferential ?? throw new ArgumentException("the device has no busDifferential", nameof(d));
        reason = fault.NotEvaluated;
        if (reason is not null) return null;
        reason = Incomplete(bound.Model, s);
        if (reason is not null) return null;
        double refRatio = s.Cts.OrderByDescending(c => c.PrimaryA).First().Ratio;
        BusZoneResult? main = Zone(fault, s, s.Zone, refRatio, out reason);
        if (main is null) return null;
        BusZoneResult? check = null;
        if (s.CheckZone is { } cz)
        {
            check = Zone(fault, s, cz, refRatio, out reason);
            if (check is null) return null;
        }
        return new BusDifferentialResult(main, check);
    }

    private static BusZoneResult? Zone(FaultPhasors f, BusDifferentialSetting s, IReadOnlyList<int> zone, double refRatio, out string? reason)
    {
        reason = null;
        FaultNetwork net = f.Network!;
        GridModel m = net.Model;
        var set = new HashSet<int>(zone);
        var feeders = new List<Complex[]>();
        var sum = new Complex[3];
        bool TryAmps(int bus, out double perPu, out string? why)
        {
            perPu = 0.0; why = null;
            if (!f.TryBus(bus, out int bi)) { why = $"bus {bus} is not in the fault study"; return false; }
            double kv = m.Buses[bi].BaseKv;
            if (!(kv > 0.0)) { why = $"bus {bus} has no base kV, so amps are not defined"; return false; }
            perPu = PlacedElementEvaluator.IBaseA(m.BaseMva, kv) / refRatio;
            return true;
        }
        // The CTs on this zone's boundary: the current into the zone is minus the current into the branch at the CT.
        foreach (BusCt c in s.Cts)
        {
            Branch ob = m.Branches[c.At.BranchIndex];
            int other = c.At.AtFromEnd ? ob.To : ob.From;
            if (!set.Contains(c.At.Bus) || set.Contains(other)) continue;
            if (!TryAmps(c.At.Bus, out double k, out reason)) return null;
            int seg = c.At.AtFromEnd ? FaultNetwork.FromSegment(c.At.BranchIndex) : net.ToSegment(c.At.BranchIndex);
            (Complex a, Complex b, Complex cc) = f.PhaseCurrents(seg, c.At.AtFromEnd);
            Complex[] into = [-a * k, -b * k, -cc * k];
            feeders.Add(into);
            for (int p = 0; p < 3; p++) sum[p] += into[p];
        }
        // Each zone bus's units, loads and shunts: their net injection by KCL (branch currents leaving the bus, plus the fault
        // current at the faulted bus). It always enters Id; it is a bias feeder only at a bus that carries such elements.
        (Complex If0, Complex If1, Complex If2) = f.FaultSequenceCurrents;
        foreach (int busId in zone)
        {
            if (!TryAmps(busId, out double k, out reason)) return null;
            Complex s0 = Complex.Zero, s1 = Complex.Zero, s2 = Complex.Zero;
            for (int l = 0; l < m.Branches.Count; l++)
            {
                Branch br = m.Branches[l];
                if (br.From == busId)
                {
                    (Complex i0, Complex i1, Complex i2) = f.Sequence(l, true);
                    s0 += i0; s1 += i1; s2 += i2;
                }
                if (br.To == busId)
                {
                    (Complex i0, Complex i1, Complex i2) = f.Sequence(l, false);
                    s0 += i0; s1 += i1; s2 += i2;
                }
            }
            if (net.FaultBusId == busId) { s0 += If0; s1 += If1; s2 += If2; }
            (Complex a, Complex b, Complex c) = FaultAnalysis.Phases(s0, s1, s2);
            Complex[] inj = [a * k, b * k, c * k];
            for (int p = 0; p < 3; p++) sum[p] += inj[p];
            f.TryBus(busId, out int bi);
            if (HasLocalElements(m, m.Buses[bi])) feeders.Add(inj);
        }
        var id = new double[3]; var ib = new double[3]; var thr = new double[3];
        int best = 0; double bestExcess = double.NegativeInfinity;
        DifferentialSetting? dual = s.Is2A is double is2 && s.K2 is double k2 ? new DifferentialSetting(s.Is1A, s.K1, is2, k2, 1.0, 1.0) : null;
        for (int p = 0; p < 3; p++)
        {
            id[p] = sum[p].Magnitude;
            double bias = 0.0;
            foreach (Complex[] fd in feeders)
            {
                double mag = fd[p].Magnitude;
                bias = s.Bias == BusBias.Sum ? bias + mag : Math.Max(bias, mag);
            }
            ib[p] = bias;
            thr[p] = dual is not null ? PlacedElementEvaluator.DifferentialThreshold(bias, dual) : s.K1 * bias + s.Is1A;
            double excess = id[p] - thr[p];
            if (excess > bestExcess) { bestExcess = excess; best = p; }
        }
        return new BusZoneResult(id, ib, thr, feeders, best, id[best] > thr[best]);
    }
}
