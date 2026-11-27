using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Core.Protection.Workflow;

/// <summary>Where a fault is applied: at a bus, or a fraction along a branch.</summary>
public sealed record FaultSpec(int? Bus, int? Branch, double Fraction)
{
    public static FaultSpec AtBus(int bus) => new(bus, null, 0.0);
    public static FaultSpec AlongBranch(int branch, double fraction) => new(null, branch, fraction);
    public string Label => Bus is int b ? $"bus {b}" : $"branch {Branch} at {Fraction.ToString("R", CultureInfo.InvariantCulture)}";
    /// <summary>RC5 WP17: true when the faulted line has just been closed onto the fault (read only by a 21's
    /// switch-onto-fault setting in the placed-element evaluation). False by default.</summary>
    public bool Energisation { get; init; }
}

/// <summary>One device at one fault. <see cref="Direction"/> is true when the fault is on the far side of the device's
/// branch (decided by the bridge rule on the fault network), false when it is behind the device, null when the device's
/// branch is in a mesh and direction is not resolved. The device is non-directional (ANSI 50/51), so it operates on the
/// current it measures whatever the direction; direction is used to order devices along a supply path.</summary>
public sealed record DeviceTraceRow(
    string DeviceId, string Function, bool Evaluated, string? Reason, ProtectionConfidence Confidence,
    double PrimaryCurrentA, double SecondaryCurrentA, bool? Direction,
    double ClearingTimeS, double EarliestTimeS, IReadOnlyList<double> ShotClearingTimesS, bool AboveCurveRange);

/// <summary>A primary/backup pair at one fault and one shot of the primary: the grading margin against the required
/// coordination time interval. A pair whose primary does not operate is reported as such, not graded.</summary>
public sealed record PairRow(
    string PrimaryId, string BackupId, int Shot, double PrimaryCurrentA, double BackupCurrentA,
    double PrimaryTimeS, double BackupTimeS, double MarginS, double RequiredS, bool Evaluated, bool Coordinated, string? Reason);

/// <summary>The trace of one fault: every device's current and time (device order), the sequence of operation (earliest
/// first, ties by device order), primary and backup, and the graded pairs. <see cref="NotEvaluated"/> is set when the fault
/// itself could not be applied (no source, or a fault along a transformer); then there are no rows.</summary>
public sealed record TraceResult(
    FaultSpec Fault, string Label, int FaultBusId, ShortCircuitObjective Objective, string? NotEvaluated,
    IReadOnlyList<DeviceTraceRow> Rows, IReadOnlyList<string> Sequence, string? PrimaryId, string? BackupId, IReadOnlyList<PairRow> Pairs);

/// <summary>
/// Short-circuit trace (report row A14): for a fault, the current each placed device measures (through its CT), its
/// operating time, the sequence of operation, the primary and backup device, and the grading margin of each downstream device
/// against the nearest upstream one. Order of operations: build the fault network; per-branch currents; devices in
/// device-number order; sequence earliest first (ties by device order); pairs in device order, shots ascending.
/// </summary>
public static class ShortCircuitTrace
{
    public static TraceResult Run(BoundDeviceSet bound, FaultSpec fault, ShortCircuitObjective objective, double requiredMarginSeconds)
    {
        ArgumentNullException.ThrowIfNull(bound); ArgumentNullException.ThrowIfNull(fault);
        GridModel model = bound.Model;
        FaultNetwork net;
        try { net = fault.Bus is int b ? new FaultNetwork(model, CheckBus(model, b), false, -1, -1) : FaultAlongLine.Build(model, fault.Branch!.Value, fault.Fraction); }
        catch (InvalidOperationException ex) { return Empty(fault, objective, ex.Message); }
        BranchCurrentTable table;
        try { table = FaultAlongLine.Currents(net.Model, net.FaultBusId, objective); }
        catch (InvalidOperationException ex) { return Empty(fault, objective, ex.Message); }

        IReadOnlyList<PlacedDevice> devs = bound.Devices;
        var rows = new List<DeviceTraceRow>(devs.Count);
        var faultedBranch = new int[devs.Count];
        for (int i = 0; i < devs.Count; i++)
        {
            PlacedDevice d = devs[i];
            faultedBranch[i] = d.Location.AtFromEnd ? FaultNetwork.FromSegment(d.Location.BranchIndex) : net.ToSegment(d.Location.BranchIndex);
        }
        var dir = new bool?[devs.Count];
        for (int i = 0; i < devs.Count; i++)
        {
            PlacedDevice d = devs[i];
            if (bound.OutOfService(d)) continue;
            dir[i] = Direction(net, d, faultedBranch[i]);
        }

        for (int i = 0; i < devs.Count; i++)
        {
            PlacedDevice d = devs[i];
            ProtectionConfidence conf = Confidence(d, bound.Library);
            if (!d.IsEvaluatedFunction && d.Type != DeviceType.Fuse && d.Type != DeviceType.Breaker)
            {
                rows.Add(NotRow(d, $"function {d.Function} needs data the positive-sequence three-phase study does not carry (earth-fault, directional and distance elements are not evaluated here)", ProtectionConfidence.NotCheckable));
                continue;
            }
            if (bound.OutOfService(d)) { rows.Add(NotRow(d, "the device's branch is out of service", conf)); continue; }
            double kA = table.At(faultedBranch[i], d.Location.AtFromEnd);
            if (double.IsNaN(kA)) { rows.Add(NotRow(d, "the device's branch carries no fault current in this fault's island", conf)); continue; }
            double amps = kA * 1000.0;
            if (d.Type == DeviceType.Breaker)
            {
                rows.Add(new DeviceTraceRow(d.Id, d.Function, true, "breaker: no time-current characteristic (rating checked by the audit)", conf, amps, amps / d.CtRatio, dir[i],
                    double.PositiveInfinity, double.PositiveInfinity, Array.Empty<double>(), false));
                continue;
            }
            int shots = Math.Max(1, d.Shots.Count);
            var times = new double[shots];
            bool above = false;
            for (int s = 0; s < shots; s++) { times[s] = DeviceTiming.Seconds(d, s, TimingRole.Clearing, amps, bound.Library, out bool a); above |= a; }
            double earliest = DeviceTiming.Seconds(d, 0, TimingRole.Earliest, amps, bound.Library, out bool a2);
            above |= a2;
            rows.Add(new DeviceTraceRow(d.Id, d.Function, true, null, conf, amps, amps / d.CtRatio, dir[i], times[0], earliest, times, above));
        }

        // Sequence of operation: devices that operate, earliest first, ties by device order (OrderBy is stable).
        var order = Enumerable.Range(0, rows.Count).Where(i => rows[i].Evaluated && !double.IsInfinity(rows[i].ClearingTimeS))
            .OrderBy(i => rows[i].ClearingTimeS).ToList();
        string? primary = order.Count > 0 ? rows[order[0]].DeviceId : null;
        string? backup = order.Count > 1 ? rows[order[1]].DeviceId : null;

        // Pairs along the supply path.
        var pairs = new List<PairRow>();
        var forward = Enumerable.Range(0, devs.Count).Where(i => dir[i] == true && rows[i].Evaluated && devs[i].Type != DeviceType.Breaker).ToList();
        var depth = new Dictionary<int, int>();
        foreach (int a in forward) depth[a] = forward.Count(b => b != a && IsDownstream(net, devs, faultedBranch, a, b));
        foreach (int a in forward)
        {
            int parent = -1;
            foreach (int b in forward)
                if (b != a && IsDownstream(net, devs, faultedBranch, a, b) && (parent < 0 || depth[b] > depth[parent])) parent = b;
            if (parent < 0) continue;
            DeviceTraceRow ra = rows[a], rb = rows[parent];
            int shots = ra.ShotClearingTimesS.Count;
            for (int s = 0; s < shots; s++)
            {
                double tp = ra.ShotClearingTimesS[s];
                double tb = DeviceTiming.Seconds(devs[parent], s, TimingRole.Earliest, rb.PrimaryCurrentA, bound.Library, out _);
                if (double.IsInfinity(tp))
                {
                    pairs.Add(new PairRow(ra.DeviceId, rb.DeviceId, s, ra.PrimaryCurrentA, rb.PrimaryCurrentA, tp, tb, double.NaN, requiredMarginSeconds, false, false,
                        "the primary does not operate at this fault current (see its sensitivity check)"));
                    continue;
                }
                double margin = tb - tp;
                bool ok = !double.IsInfinity(tb) && margin >= requiredMarginSeconds;
                pairs.Add(new PairRow(ra.DeviceId, rb.DeviceId, s, ra.PrimaryCurrentA, rb.PrimaryCurrentA, tp, tb, margin, requiredMarginSeconds, true, ok,
                    double.IsInfinity(tb) ? "the backup does not operate at this fault current" : null));
            }
        }
        return new TraceResult(fault, fault.Label, net.FaultBusId, objective, null, rows, order.Select(i => rows[i].DeviceId).ToList(), primary, backup, pairs);
    }

    private static int CheckBus(GridModel m, int bus)
    {
        if (!m.Buses.Any(b => b.Id == bus)) throw new ArgumentException($"fault bus {bus} is not in the case.", nameof(bus));
        return bus;
    }

    private static TraceResult Empty(FaultSpec f, ShortCircuitObjective o, string reason) =>
        new(f, f.Label, f.Bus ?? -1, o, reason, Array.Empty<DeviceTraceRow>(), Array.Empty<string>(), null, null, Array.Empty<PairRow>());

    private static DeviceTraceRow NotRow(PlacedDevice d, string reason, ProtectionConfidence c) =>
        new(d.Id, d.Function, false, reason, c, double.NaN, double.NaN, null, double.PositiveInfinity, double.PositiveInfinity, Array.Empty<double>(), false);

    /// <summary>Equation curves on a user setting are Generic; a tabulated curve from a manufacturer source is Grounded (a
    /// published value for that device); a synthetic fixture is Generic.</summary>
    private static ProtectionConfidence Confidence(PlacedDevice d, CurveLibrary lib)
    {
        IEnumerable<string> ids = d.Type == DeviceType.Fuse ? new[] { d.Fuse!.MeltCurveId, d.Fuse.ClearCurveId } : d.Shots.Select(s => s.CurveId);
        bool anyTab = false, allManufacturer = true;
        foreach (string id in ids)
            if (lib.TryGetTabulated(id, out TabulatedCurve t)) { anyTab = true; allManufacturer &= t.Source.Kind == "manufacturer"; }
        return anyTab && allManufacturer ? ProtectionConfidence.Grounded : ProtectionConfidence.Generic;
    }

    // ---- topology ----------------------------------------------------------------------------------------------------

    private static HashSet<int> Reach(GridModel m, int excludedBranch, int start)
    {
        var adj = new Dictionary<int, List<int>>();
        for (int k = 0; k < m.Branches.Count; k++)
        {
            Branch br = m.Branches[k];
            if (k == excludedBranch || !br.InService) continue;
            if (!adj.TryGetValue(br.From, out var a)) adj[br.From] = a = new List<int>();
            if (!adj.TryGetValue(br.To, out var c)) adj[br.To] = c = new List<int>();
            a.Add(br.To); c.Add(br.From);
        }
        var seen = new HashSet<int> { start };
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            if (!adj.TryGetValue(u, out var nbrs)) continue;
            foreach (int v in nbrs) if (seen.Add(v)) stack.Push(v);
        }
        return seen;
    }

    private static (int Near, int Far) Ends(FaultNetwork net, PlacedDevice d, int faultedBranch)
    {
        Branch fb = net.Model.Branches[faultedBranch];
        return d.Location.AtFromEnd ? (fb.From, fb.To) : (fb.To, fb.From);
    }

    private static bool? Direction(FaultNetwork net, PlacedDevice d, int faultedBranch)
    {
        (int near, int far) = Ends(net, d, faultedBranch);
        HashSet<int> beyond = Reach(net.Model, faultedBranch, far);
        if (beyond.Contains(near))
            return net.Inserted && d.Location.BranchIndex == net.SplitBranch ? true : null;   // meshed: not resolved
        return beyond.Contains(net.FaultBusId);
    }

    private static bool IsDownstream(FaultNetwork net, IReadOnlyList<PlacedDevice> devs, int[] fb, int a, int b)
    {
        (int nearA, _) = Ends(net, devs[a], fb[a]);
        (_, int farB) = Ends(net, devs[b], fb[b]);
        return Reach(net.Model, fb[b], farB).Contains(nearA);
    }
}
