using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.Protection.Coordination;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Core.Protection.Workflow;

/// <summary>The worst grading margin of one primary/backup pair (and shot) over every fault where both were evaluated.</summary>
public sealed record AuditPair(
    string PrimaryId, string BackupId, int Shot, int FaultsEvaluated, double WorstMarginS, string WorstFault,
    double PrimaryTimeS, double BackupTimeS, double RequiredS, bool Coordinated);

/// <summary>Sensitivity of one device: the smallest fault (minimum objective, c = 1.00 at HV) in its zone and how far above
/// its pickup that current is. <see cref="Operates"/> is true when the ratio exceeds 1. The instantaneous reach is the largest
/// current the device sees for a fault at the far end of its own branch divided by its instantaneous pickup: at or above 1
/// the element overreaches (operates for faults beyond the protected branch).</summary>
public sealed record AuditDevice(
    string DeviceId, string Function, ProtectionConfidence Confidence, bool Evaluated, string? Reason,
    string? MinFault, double MinFaultCurrentA, double PickupA, double SensitivityRatio, bool Operates,
    double InstantaneousReachRatio, bool InstantaneousOverreaches);

/// <summary>A breaker rating check against the maximum three-phase duty at the device's bus.</summary>
public sealed record AuditBreaker(
    string DeviceId, int Bus, bool Evaluated, string? Reason, bool Adequate,
    double MakingMarginKa, double BreakingMarginKa, double ThermalMarginKa);

/// <summary>A check that was not carried out, and why. Missing evidence is never a pass.</summary>
public sealed record NotEvaluatedCheck(string Subject, string Check, string Reason);

public enum AuditVerdict { Coordinated, NotCoordinated, Incomplete }

/// <summary>The whole audit (<c>gridsim-protection-audit/1</c> result).</summary>
public sealed record ProtectionAuditResult(
    double RequiredMarginS, AuditVerdict Verdict, IReadOnlyList<TraceResult> Faults, IReadOnlyList<AuditPair> Pairs,
    IReadOnlyList<AuditDevice> Devices, IReadOnlyList<AuditBreaker> Breakers, IReadOnlyList<NotEvaluatedCheck> NotEvaluated);

/// <summary>
/// Protection audit (report row A13): for each fault (bus list in model order, then any along-line faults in request order)
/// the maximum-fault trace gives the margin of every device pair along the supply path against the coordination time
/// interval; the minimum-fault trace gives each device's sensitivity; the instantaneous reach and the breaker rating margin are
/// through-fault and duty checks. Every check that could not be made is listed with a reason.
/// </summary>
public static class ProtectionAudit
{
    public static ProtectionAuditResult Run(BoundDeviceSet bound, IReadOnlyList<FaultSpec>? faults = null,
        double requiredMarginSeconds = OvercurrentCoordination.DefaultCoordinationTimeIntervalS)
    {
        ArgumentNullException.ThrowIfNull(bound);
        if (!(requiredMarginSeconds >= 0.0) || !double.IsFinite(requiredMarginSeconds))
            throw new ArgumentOutOfRangeException(nameof(requiredMarginSeconds), requiredMarginSeconds, "The coordination time interval must be finite and >= 0 s.");
        GridModel model = bound.Model;
        var faultList = (faults ?? model.Buses.Select(b => FaultSpec.AtBus(b.Id)).ToList()).ToList();
        var notEval = new List<NotEvaluatedCheck>();

        var maxTraces = new List<TraceResult>();
        var minTraces = new List<TraceResult>();
        foreach (FaultSpec f in faultList)
        {
            TraceResult mx = ShortCircuitTrace.Run(bound, f, ShortCircuitObjective.Maximum, requiredMarginSeconds);
            maxTraces.Add(mx);
            if (mx.NotEvaluated is not null) { notEval.Add(new NotEvaluatedCheck(f.Label, "fault", mx.NotEvaluated)); minTraces.Add(mx); continue; }
            minTraces.Add(ShortCircuitTrace.Run(bound, f, ShortCircuitObjective.Minimum, requiredMarginSeconds));
        }

        // Pairs, aggregated in first-seen order (device order within a fault, faults in order).
        var keys = new List<(string P, string B, int S)>();
        var agg = new Dictionary<(string, string, int), List<(string Fault, PairRow Row)>>();
        foreach (TraceResult t in maxTraces)
            foreach (PairRow p in t.Pairs)
            {
                var key = (p.PrimaryId, p.BackupId, p.Shot);
                if (!agg.TryGetValue(key, out var list)) { agg[key] = list = new(); keys.Add(key); }
                list.Add((t.Label, p));
            }
        var pairs = new List<AuditPair>();
        foreach (var key in keys)
        {
            var evald = agg[key].Where(x => x.Row.Evaluated).ToList();
            if (evald.Count == 0)
            {
                notEval.Add(new NotEvaluatedCheck($"{key.P} -> {key.B} shot {key.S}", "grading margin", "the primary did not operate at any fault where the pair is on the supply path"));
                continue;
            }
            var worst = evald[0];
            foreach (var x in evald)
            {
                double m = double.IsInfinity(x.Row.BackupTimeS) ? double.NegativeInfinity : x.Row.MarginS;
                double wm = double.IsInfinity(worst.Row.BackupTimeS) ? double.NegativeInfinity : worst.Row.MarginS;
                if (m < wm) worst = x;
            }
            pairs.Add(new AuditPair(key.P, key.B, key.S, evald.Count, worst.Row.MarginS, worst.Fault, worst.Row.PrimaryTimeS, worst.Row.BackupTimeS,
                requiredMarginSeconds, evald.All(x => x.Row.Coordinated)));
        }

        // Devices: sensitivity and instantaneous reach.
        var devices = new List<AuditDevice>();
        IReadOnlyList<PlacedDevice> devs = bound.Devices;
        for (int i = 0; i < devs.Count; i++)
        {
            PlacedDevice d = devs[i];
            ProtectionConfidence conf = maxTraces.SelectMany(t => t.Rows.Skip(i).Take(1)).Select(r => r.Confidence).FirstOrDefault(ProtectionConfidence.Generic);
            if (!d.IsEvaluatedFunction && d.Type is DeviceType.Relay or DeviceType.Recloser)
            {
                string why = $"function {d.Function} is not evaluated by the positive-sequence three-phase study";
                notEval.Add(new NotEvaluatedCheck(d.Id, "sensitivity and coordination", why));
                devices.Add(new AuditDevice(d.Id, d.Function, ProtectionConfidence.NotCheckable, false, why, null, double.NaN, double.NaN, double.NaN, false, double.NaN, false));
                continue;
            }
            if (d.Type == DeviceType.Breaker) continue;   // reported under breakers
            if (bound.OutOfService(d))
            {
                const string why = "the device's branch is out of service";
                notEval.Add(new NotEvaluatedCheck(d.Id, "sensitivity and coordination", why));
                devices.Add(new AuditDevice(d.Id, d.Function, conf, false, why, null, double.NaN, double.NaN, double.NaN, false, double.NaN, false));
                continue;
            }
            double pickup = PickupPrimary(d, bound.Library);
            double best = double.PositiveInfinity; string? bestFault = null;
            for (int f = 0; f < minTraces.Count; f++)
            {
                TraceResult t = minTraces[f];
                if (t.Rows.Count <= i) continue;
                DeviceTraceRow r = t.Rows[i];
                if (!r.Evaluated || r.Direction != true) continue;
                if (r.PrimaryCurrentA < best) { best = r.PrimaryCurrentA; bestFault = t.Label; }
            }
            double reach = double.NaN; bool over = false;
            if (d.Instantaneous is { } inst)
            {
                TraceResult far = ShortCircuitTrace.Run(bound, FaultSpec.AtBus(d.Location.FarBus), ShortCircuitObjective.Maximum, requiredMarginSeconds);
                if (far.NotEvaluated is null && far.Rows.Count > i && far.Rows[i].Evaluated)
                {
                    reach = far.Rows[i].PrimaryCurrentA / (inst.PickupA * d.CtRatio);
                    over = reach >= 1.0;
                }
                else notEval.Add(new NotEvaluatedCheck(d.Id, "instantaneous reach", far.NotEvaluated ?? "no current at the far-end fault"));
            }
            if (bestFault is null)
            {
                const string why = "no fault in the list lies on the far side of this device";
                notEval.Add(new NotEvaluatedCheck(d.Id, "sensitivity at minimum fault", why));
                devices.Add(new AuditDevice(d.Id, d.Function, conf, false, why, null, double.NaN, pickup, double.NaN, false, reach, over));
                continue;
            }
            double ratio = best / pickup;
            bool operates = ratio > 1.0 && !double.IsInfinity(DeviceTiming.Seconds(d, 0, TimingRole.Clearing, best, bound.Library, out _));
            devices.Add(new AuditDevice(d.Id, d.Function, conf, true, null, bestFault, best, pickup, ratio, operates, reach, over));
        }

        // Breakers: the incumbent CheckBreaker against the maximum duty at the device's bus.
        var breakers = new List<AuditBreaker>();
        foreach (PlacedDevice d in devs.Where(x => x.Rating is not null))
        {
            int bus = d.Location.NearBus;
            try
            {
                Iec60909.Duty duty = Iec60909.ThreePhase(model, bus, ShortCircuitObjective.Maximum);
                double kv = model.Buses.First(b => b.Id == bus).BaseKv;
                Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(duty, d.Rating!, kv);
                breakers.Add(new AuditBreaker(d.Id, bus, true, null, a.Adequate, a.MakingMarginKa, a.BreakingMarginKa, a.ThermalMarginKa));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                notEval.Add(new NotEvaluatedCheck(d.Id, "breaker rating", ex.Message));
                breakers.Add(new AuditBreaker(d.Id, bus, false, ex.Message, false, double.NaN, double.NaN, double.NaN));
            }
        }

        AuditVerdict verdict = pairs.Any(p => !p.Coordinated) ? AuditVerdict.NotCoordinated
            : pairs.Count > 0 ? AuditVerdict.Coordinated : AuditVerdict.Incomplete;
        return new ProtectionAuditResult(requiredMarginSeconds, verdict, maxTraces, pairs, devices, breakers, notEval);
    }

    private static double PickupPrimary(PlacedDevice d, CurveLibrary lib)
    {
        if (d.Type == DeviceType.Fuse) return lib.TryGetTabulated(d.Fuse!.MeltCurveId, out TabulatedCurve t) ? t.Points[0].CurrentA : double.NaN;
        if (d.Shots.Count > 0)
        {
            ShotSetting s = d.Shots[0];
            if (CurveLibrary.Standard(s.CurveId) is not null || s.PickupA > 0.0) return s.PickupA * d.CtRatio;
            return lib.TryGetTabulated(s.CurveId, out TabulatedCurve t2) ? t2.Points[0].CurrentA : double.NaN;
        }
        return d.Instantaneous is { } i ? i.PickupA * d.CtRatio : double.NaN;
    }
}
