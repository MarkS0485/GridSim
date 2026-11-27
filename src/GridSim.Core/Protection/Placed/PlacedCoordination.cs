using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.Coordination;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Core.Protection.Placed;

/// <summary>One fault of a grading study: its position, type, the phasor study's refusal (if any) and every device's row.</summary>
public sealed record PlacedFaultResult(FaultSpec Fault, FaultType Type, string Label, string? NotEvaluated, IReadOnlyList<PlacedRow> Rows);

/// <summary>G1: a primary/backup pair on the supply path at one fault. <see cref="Evaluated"/> is false (with the reason)
/// when either device does not operate there; a pair is graded only when both do.</summary>
public sealed record PlacedPairRow(
    string Fault, string PrimaryId, string BackupId, double PrimaryTimeS, double BackupTimeS, double MarginS, double RequiredS,
    bool Evaluated, bool Coordinated, string? Reason);

/// <summary>One check of G2 to G5 for one device. <see cref="Value"/> is the check's number (the zone for G2 and G3, the
/// coverage fraction for G4, the sensitivity ratio for G5). A check that could not be made is listed in
/// <see cref="PlacedCoordinationResult.NotEvaluated"/> instead, never as a pass.</summary>
public sealed record PlacedCheck(string Check, string DeviceId, bool Passed, double Value, string Detail);

public enum PlacedVerdict { Coordinated, NotCoordinated, Incomplete }

public sealed record PlacedCoordinationResult(
    double RequiredMarginS, IReadOnlyList<PlacedFaultResult> Faults, IReadOnlyList<PlacedPairRow> Pairs, IReadOnlyList<PlacedCheck> Checks,
    IReadOnlyList<NotEvaluatedCheck> NotEvaluated, PlacedVerdict Verdict);

/// <summary>
/// Coordination checks over placed devices (RC5 WP4; NPAG ch. 9 and 11, IEEE C37.113 for distance zone practice):
/// <list type="bullet">
/// <item>G1 grading margin: at every fault of the list and every fault type, for each device with the fault beyond it, the
/// nearest upstream device on the supply path is its backup (the short-circuit trace's rule); when both operate,
/// t_backup - t_primary must be at least the coordination time interval. Times are the distance zone times, the 50/51,
/// 50N/51N and 67 curve times, a fuse's total clear as primary and minimum melt as backup, and 0 for 87L.</item>
/// <item>G2 zone-1 underreach: a bolted three-phase fault at the far bus of a 21's own branch must not be in zone 1.</item>
/// <item>G3 zone-2 cover: the same fault must operate in zone 2 or faster.</item>
/// <item>G4 zone-1 coverage: the largest swept fraction of the protected line, from the relay's end, at which zone 1
/// operates for a bolted three-phase fault (a zone 1 that covers no swept point fails).</item>
/// <item>G5 earth-fault sensitivity: for a 50N/51N, the smallest |3 I0| over the single-line-to-ground faults beyond it,
/// over its pickup (the shot's, else the instantaneous element's); it passes when the device operates there.</item>
/// <item>G6 zone reaches load (RC5 WP17), only for a 21 with a load blinder: the phase-loop impedance at the prefault load
/// point must lie in no forward zone, or else in the blinder's load region (where the blinder blocks it).</item>
/// <item>G7 zone completeness (RC5 WP18), only for an 87B: every branch that leaves the zone (and the check zone) has a CT
/// at its zone end. A missing CT fails, naming the branch, and the element is not evaluated.</item>
/// <item>G8 stability (RC5 WP18), only for an 87T or 87B: it does not operate for any external fault of the study.</item>
/// </list>
/// The verdict is NotCoordinated when any graded pair or check fails, Coordinated when none fails and at least one was
/// made, and Incomplete when nothing could be graded.
/// </summary>
public static class PlacedCoordination
{
    /// <summary>The default G4 sweep: 0.05 to 0.95 in steps of 0.05 (k / 20).</summary>
    public static IReadOnlyList<double> DefaultFractions { get; } = Enumerable.Range(1, 19).Select(k => k / 20.0).ToArray();

    public static PlacedCoordinationResult Run(BoundDeviceSet bound, IReadOnlyList<FaultSpec>? faults = null, IReadOnlyList<FaultType>? types = null,
        double requiredMarginSeconds = OvercurrentCoordination.DefaultCoordinationTimeIntervalS, IReadOnlyList<double>? fractions = null,
        Func<GridModel, PowerFlowResult>? prefault = null)
    {
        ArgumentNullException.ThrowIfNull(bound);
        if (!(requiredMarginSeconds >= 0.0) || !double.IsFinite(requiredMarginSeconds))
            throw new ArgumentOutOfRangeException(nameof(requiredMarginSeconds), requiredMarginSeconds, "The coordination time interval must be finite and >= 0 s.");
        GridModel model = bound.Model;
        faults ??= model.Buses.Select(b => FaultSpec.AtBus(b.Id)).ToArray();
        types ??= new[] { FaultType.ThreePhase, FaultType.SingleLineToGround };
        fractions ??= DefaultFractions;
        foreach (double x in fractions)
            if (!(x > 0.0 && x < 1.0)) throw new ArgumentOutOfRangeException(nameof(fractions), x, "Each swept fraction must be inside (0, 1).");
        IReadOnlyList<PlacedDevice> devs = bound.Devices;
        var notEval = new List<NotEvaluatedCheck>();
        var results = new List<PlacedFaultResult>();
        var pairs = new List<PlacedPairRow>();

        // G1.
        foreach (FaultType type in types)
            foreach (FaultSpec f in faults)
            {
                FaultPhasors ph = FaultPhasors.Build(model, f, type, 0.0, prefault);
                string label = $"{f.Label} {FaultPhasors.TypeName(type)}";
                if (ph.NotEvaluated is not null)
                {
                    notEval.Add(new NotEvaluatedCheck(label, "fault", ph.NotEvaluated));
                    results.Add(new PlacedFaultResult(f, type, label, ph.NotEvaluated, Array.Empty<PlacedRow>()));
                    continue;
                }
                IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(bound, ph);
                results.Add(new PlacedFaultResult(f, type, label, null, rows));
                FaultNetwork net = ph.Network!;
                var fb = new int[devs.Count];
                for (int i = 0; i < devs.Count; i++) fb[i] = PlacedTopology.FaultedBranch(net, devs[i]);
                var forward = Enumerable.Range(0, devs.Count).Where(i => rows[i].Evaluated && rows[i].FaultDirection == true && devs[i].Type != DeviceType.Breaker).ToList();
                var depth = new Dictionary<int, int>();
                foreach (int a in forward) depth[a] = forward.Count(b => b != a && PlacedTopology.IsDownstream(net, devs, fb, a, b));
                foreach (int a in forward)
                {
                    int parent = -1;
                    foreach (int b in forward)
                        if (b != a && PlacedTopology.IsDownstream(net, devs, fb, a, b) && (parent < 0 || depth[b] > depth[parent])) parent = b;
                    if (parent < 0) continue;
                    PlacedRow p = rows[a], q = rows[parent];
                    double tp = p.TimeS, tb = q.EarliestTimeS;
                    if (double.IsInfinity(tp) || double.IsInfinity(tb))
                    {
                        pairs.Add(new PlacedPairRow(label, p.DeviceId, q.DeviceId, tp, tb, double.NaN, requiredMarginSeconds, false, false,
                            double.IsInfinity(tp) ? "the primary does not operate at this fault" : "the backup does not operate at this fault"));
                        continue;
                    }
                    double margin = tb - tp;
                    pairs.Add(new PlacedPairRow(label, p.DeviceId, q.DeviceId, tp, tb, margin, requiredMarginSeconds, true, margin >= requiredMarginSeconds, null));
                }
            }

        // G2 to G6.
        var checks = new List<PlacedCheck>();
        PowerFlowResult? loadPoint = null;
        for (int i = 0; i < devs.Count; i++)
        {
            PlacedDevice d = devs[i];
            if (d.Type is not (DeviceType.Relay or DeviceType.Recloser)) continue;
            if (d.Function == "21")
            {
                if (d.Distance is null) { notEval.Add(new NotEvaluatedCheck(d.Id, "zone reach (G2, G3, G4)", "the 21 has no distance zones")); continue; }
                if (bound.OutOfService(d)) { notEval.Add(new NotEvaluatedCheck(d.Id, "zone reach (G2, G3, G4)", "the device's branch is out of service")); continue; }
                FarBusChecks(bound, d, i, prefault, checks, notEval);
                Coverage(bound, d, i, fractions, prefault, checks, notEval);
                if (d.Distance.LoadEncroachment is not null) LoadCheck(bound, d, prefault, ref loadPoint, checks, notEval);
            }
            else if (d.Function is "50N" or "51N")
                Sensitivity(d, i, results, checks, notEval);
            else if (d.Function == "87" && (d.TransformerDifferential is not null || d.BusDifferential is not null))
                UnitChecks(bound, d, i, results, checks, notEval);
        }

        bool anyFail = pairs.Any(p => p.Evaluated && !p.Coordinated) || checks.Any(c => !c.Passed);
        bool any = pairs.Any(p => p.Evaluated) || checks.Count > 0;
        PlacedVerdict verdict = anyFail ? PlacedVerdict.NotCoordinated : any ? PlacedVerdict.Coordinated : PlacedVerdict.Incomplete;
        return new PlacedCoordinationResult(requiredMarginSeconds, results, pairs, checks, notEval, verdict);
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static void FarBusChecks(BoundDeviceSet bound, PlacedDevice d, int i, Func<GridModel, PowerFlowResult>? prefault, List<PlacedCheck> checks, List<NotEvaluatedCheck> notEval)
    {
        int far = d.Location.FarBus;
        FaultPhasors ph = FaultPhasors.Build(bound.Model, FaultSpec.AtBus(far), FaultType.ThreePhase, 0.0, prefault);
        if (ph.NotEvaluated is not null) { notEval.Add(new NotEvaluatedCheck(d.Id, "zone reach (G2, G3)", ph.NotEvaluated)); return; }
        PlacedRow r = PlacedElementEvaluator.Evaluate(bound, ph)[i];
        if (!r.Evaluated) { notEval.Add(new NotEvaluatedCheck(d.Id, "zone reach (G2, G3)", r.Reason ?? "not evaluated")); return; }
        double zone = r.Zone is int z ? z : double.NaN;
        string seen = r.Zone is int zz ? $"zone {zz}" : "no zone";
        checks.Add(new PlacedCheck("G2", d.Id, r.Zone != 1, zone, $"three-phase fault at the far bus {far}: {seen} (zone 1 must not reach it)"));
        checks.Add(new PlacedCheck("G3", d.Id, r.Zone is 1 or 2, zone, $"three-phase fault at the far bus {far}: {seen} (zone 2 or faster must cover it)"));
    }

    private static void Coverage(BoundDeviceSet bound, PlacedDevice d, int i, IReadOnlyList<double> fractions, Func<GridModel, PowerFlowResult>? prefault,
        List<PlacedCheck> checks, List<NotEvaluatedCheck> notEval)
    {
        double best = double.NaN;
        int swept = 0;
        foreach (double x in fractions)
        {
            double along = d.Location.AtFromEnd ? x : 1.0 - x;
            FaultPhasors ph = FaultPhasors.Build(bound.Model, FaultSpec.AlongBranch(d.Location.BranchIndex, along), FaultType.ThreePhase, 0.0, prefault);
            if (ph.NotEvaluated is not null) continue;
            PlacedRow r = PlacedElementEvaluator.Evaluate(bound, ph)[i];
            if (!r.Evaluated) continue;
            swept++;
            if (r.Zone == 1 && !(best >= x)) best = x;
        }
        if (swept == 0) { notEval.Add(new NotEvaluatedCheck(d.Id, "zone-1 coverage (G4)", "no swept fault along the protected line could be evaluated")); return; }
        bool ok = !double.IsNaN(best);
        checks.Add(new PlacedCheck("G4", d.Id, ok, ok ? best : 0.0,
            ok ? $"zone 1 covers up to {F(best)} of the line from the relay end ({swept} point(s) swept)" : $"zone 1 covers none of the {swept} swept point(s)"));
    }

    /// <summary>RC5 WP17, G6: the prefault phase-loop impedance against the forward zones and the load blinder.</summary>
    private static void LoadCheck(BoundDeviceSet bound, PlacedDevice d, Func<GridModel, PowerFlowResult>? prefault, ref PowerFlowResult? pf,
        List<PlacedCheck> checks, List<NotEvaluatedCheck> notEval)
    {
        DistanceSetting s = d.Distance!;
        if (pf is null)
        {
            try { pf = (prefault ?? FaultPhasors.DefaultPrefault)(bound.Model).RequireConverged("Placed protection load check"); }
            catch (InvalidOperationException ex) { notEval.Add(new NotEvaluatedCheck(d.Id, "zone reaches load (G6)", ex.Message)); return; }
        }
        if (LoadBlinder.PrefaultImpedance(bound.Model, pf, d) is not Complex z)
        {
            notEval.Add(new NotEvaluatedCheck(d.Id, "zone reaches load (G6)", "the protected branch carries no prefault load current"));
            return;
        }
        int zone = 0;
        for (int k = 0; k < s.Zones.Count; k++)
            if (!s.Zones[k].Reverse && PlacedElementEvaluator.InZoneDirected(z, s.Zones[k], s)) { zone = k + 1; break; }
        bool blocks = LoadBlinder.Blocks(z, s.LoadEncroachment!);
        string at = $"prefault load point Z = {F(z.Real)} {(z.Imaginary < 0 ? "-" : "+")} j{F(Math.Abs(z.Imaginary))} ohm sec";
        string detail = zone == 0 ? $"{at}: in no zone"
            : blocks ? $"{at}: in zone {zone}, inside the load blinder's region, so the phase loops are blocked there"
            : $"{at}: in zone {zone} and outside the load blinder's region (the zone reaches the load)";
        checks.Add(new PlacedCheck("G6", d.Id, zone == 0 || blocks, zone, detail));
    }

    /// <summary>RC5 WP18: G7 for an 87B, and G8 for an 87T or 87B over the study's external faults.</summary>
    private static void UnitChecks(BoundDeviceSet bound, PlacedDevice d, int i, List<PlacedFaultResult> results, List<PlacedCheck> checks, List<NotEvaluatedCheck> notEval)
    {
        if (d.BusDifferential is { } bd)
        {
            string? gap = BusDifferential.Incomplete(bound.Model, bd);
            checks.Add(new PlacedCheck("G7", d.Id, gap is null, gap is null ? 1.0 : 0.0, gap ?? "every branch leaving the zone has a CT at its zone end"));
            if (gap is not null) return;
        }
        int external = 0;
        string? first = null;
        foreach (PlacedFaultResult f in results)
        {
            if (f.NotEvaluated is not null) continue;
            bool inside = d.BusDifferential is { } b ? BusDifferential.IsInternal(bound.Model, b, f.Fault) : TransformerDifferential.IsInternal(bound.Model, d, f.Fault);
            if (inside || !f.Rows[i].Evaluated) continue;
            external++;
            if (f.Rows[i].Operates && first is null) first = f.Label;
        }
        if (external == 0) { notEval.Add(new NotEvaluatedCheck(d.Id, "stability (G8)", "no external fault of the study could be evaluated")); return; }
        checks.Add(new PlacedCheck("G8", d.Id, first is null, external,
            first is null ? $"does not operate for any of the {external} external fault(s)" : $"OPERATES for the external fault {first}"));
    }

    private static void Sensitivity(PlacedDevice d, int i, List<PlacedFaultResult> results, List<PlacedCheck> checks, List<NotEvaluatedCheck> notEval)
    {
        double pickup = d.Shots.Count > 0 && CurveLibrary.Standard(d.Shots[0].CurveId) is not null ? d.Shots[0].PickupA
            : d.Instantaneous is { } inst ? inst.PickupA : double.NaN;
        if (!(pickup > 0.0)) { notEval.Add(new NotEvaluatedCheck(d.Id, "earth-fault sensitivity (G5)", "the device has no equation-curve or instantaneous pickup")); return; }
        PlacedRow? worst = null; string? at = null;
        foreach (PlacedFaultResult f in results)
        {
            if (f.Type != FaultType.SingleLineToGround || f.NotEvaluated is not null) continue;
            PlacedRow r = f.Rows[i];
            if (!r.Evaluated || r.FaultDirection != true) continue;
            if (worst is null || r.MeasuredValue < worst.MeasuredValue) { worst = r; at = f.Label; }
        }
        if (worst is null) { notEval.Add(new NotEvaluatedCheck(d.Id, "earth-fault sensitivity (G5)", "no single-line-to-ground fault beyond the device in the study")); return; }
        double ratio = worst.MeasuredValue / pickup;
        checks.Add(new PlacedCheck("G5", d.Id, worst.Operates, ratio,
            $"smallest |3I0| {F(worst.MeasuredValue)} A sec at {at}, {F(ratio)} x pickup {F(pickup)} A: {(worst.Operates ? "operates" : "DOES NOT OPERATE")}"));
    }
}
