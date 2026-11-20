using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.Protection.ShortCircuit.Ena;

namespace GridSim.Core.Protection.ShortCircuit.Iec61363;

/// <summary>A breaker's ratings for the check: making capacity (kA peak) and breaking capacity (kA rms, compared with the symmetrical AC
/// component at the break time). Which quantity the standard compares with which rating is AWAITING STANDARD TEXT.</summary>
public sealed record Iec61363Breaker(double MakingPeakKa, double BreakingRmsKa);

/// <summary>Options: the times (s after inception) at which AC and DC are reported, the break time, the peak search window.</summary>
public sealed record Iec61363Options(
    IReadOnlyList<double> Times, double BreakTimeSeconds = 0.10, double PeakWindowSeconds = 0.20,
    ShortCircuitObjective Objective = ShortCircuitObjective.Maximum, Iec61363Breaker? Breaker = null);

public enum Iec61363Status { Ok, Incomplete, Failed }

/// <summary>One source's contribution: its initial rms current, its AC rms and DC (peak value) components at each time, and at the break time.</summary>
public sealed record Iec61363Contribution(
    string Kind, string Name, int Bus, double IkppKa, IReadOnlyList<double> AcKa, IReadOnlyList<double> DcKa, double AcAtBreakKa, double DcAtBreakKa);

public sealed record Iec61363BreakerCheck(bool MakingOk, bool BreakingOk, bool Adequate, double MakingMarginKa, double BreakingMarginKa);

/// <summary>The ship-system duty at one bus. NaN when not Ok.</summary>
public sealed record Iec61363BusResult(
    int Bus, Iec61363Status Status, IReadOnlyList<string> Reasons, double IkppKa, IReadOnlyList<double> Times,
    IReadOnlyList<double> AcKa, IReadOnlyList<double> DcKa, IReadOnlyList<double> AsymRmsKa, double AcAtBreakKa, double DcAtBreakKa,
    double PeakKa, double PeakTimeS, IReadOnlyList<Iec61363Contribution> Contributions, Iec61363BreakerCheck? Breaker, IReadOnlyList<string> Notes,
    IReadOnlyList<string> Awaiting)
{
    public string Verdict => Status switch { Iec61363Status.Ok => "OK", Iec61363Status.Incomplete => "INCOMPLETE", _ => "FAILED" };
}

/// <summary>
/// A ship-system short-circuit study in the manner of IEC 61363-1 (generator decay plus DC component), built beside <see cref="Iec60909"/>
/// (called read-only for the per-source initial currents, exactly as the G74 study does) and reusing the G74 decay and motor machinery.
/// Order: validate, add motors as equivalent sources, per-source initial currents (PartialThreePhase), AC envelope at each time (two-exponential
/// for machines, one exponential for motors, constant for converters), DC component of each machine and motor (sqrt(2) I''0 e^(-t/Ta)),
/// fixed-order sums, instantaneous worst-case wave and its peak, breaker check, report.
/// <para>
/// Partial currents are summed as in-phase magnitudes (exact for a lossless network, conservative otherwise). The method is valid for
/// unmeshed three-phase AC systems in the standard's scope; no check is made here that the network is radial (noted). The standard's own
/// method constants, validity period, motor aggregation rules, AVR treatment of the steady-state current, treatment of unbalanced faults and
/// worked example are AWAITING STANDARD TEXT.
/// </para>
/// </summary>
public static class Iec61363Method
{
    public static readonly IReadOnlyList<string> Awaiting = new[]
    {
        "AWAITING STANDARD TEXT: IEC 61363-1 clause numbers and edition (text is paid; not read)",
        "AWAITING STANDARD TEXT: the standard's method constants, the period of validity of its formulae and the treatment of the steady-state current with AVR/excitation ceiling",
        "AWAITING STANDARD TEXT: the standard's treatment of motor groups (which motors count, aggregation, decay and DC time constants)",
        "AWAITING STANDARD TEXT: the standard's peak (making current) construction and which current is compared with a breaker's making and breaking ratings",
        "AWAITING STANDARD TEXT: unbalanced faults and the effect of external impedance on the supplied time constants",
        "AWAITING STANDARD TEXT: the standard's worked example (none reproduced; no acceptance claimed)",
    };

    public static string MotorGeneratorName(Iec61363MotorData m) => $"motor:{m.Name}@bus {m.Bus}";

    public static Iec61363BusResult Compute(GridModel model, int busId, Iec61363Data data, Iec61363Options options)
    {
        var reasons = new List<string>();
        var notes = new List<string>();
        double[] times = options.Times.ToArray();
        if (times.Length == 0) reasons.Add("no report times requested");
        foreach (double t in times) if (!(t >= 0.0) || !double.IsFinite(t)) reasons.Add($"time {t} is not a finite number of seconds >= 0");
        if (!(options.BreakTimeSeconds >= 0.0) || !double.IsFinite(options.BreakTimeSeconds)) reasons.Add("break time must be a finite number of seconds >= 0");
        if (!(options.PeakWindowSeconds > 0.0) || !double.IsFinite(options.PeakWindowSeconds)) reasons.Add("peak window must be a finite number of seconds above 0");
        if (!model.Buses.Any(b => b.Id == busId)) return Fail(busId, Iec61363Status.Failed, $"bus {busId} is not in the case", times);
        if (reasons.Count > 0) return Fail(busId, Iec61363Status.Failed, reasons, times);

        GridModel work = model;
        var motorByName = new Dictionary<string, Iec61363MotorData>(StringComparer.Ordinal);
        if (data.Motors.Count > 0)
        {
            var fleet = new List<Generator>(model.AllGenerators);
            foreach (Iec61363MotorData m in data.Motors)
            {
                if (!model.Buses.Any(b => b.Id == m.Bus)) { reasons.Add($"motor '{m.Name}': bus {m.Bus} is not in the case"); continue; }
                string gname = MotorGeneratorName(m);
                if (!motorByName.TryAdd(gname, m)) { reasons.Add($"motor name '{m.Name}' is duplicated at bus {m.Bus}"); continue; }
                fleet.Add(new Generator(m.Bus, Pg: 0.0, MBase: m.RatedMva, Name: gname, Type: "induction-motor",
                    SubtransientXdPu: G74InductionMotor.EquivalentImpedancePu(m.LockedRotorCurrentRatio), ConverterInterfaced: false));
            }
            if (reasons.Count > 0) return Fail(busId, Iec61363Status.Incomplete, reasons, times);
            work = model.CopyWith(generators: fleet);
        }

        Iec60909.FaultContribution parts;
        try { parts = Iec60909.PartialThreePhase(work, busId, options.Objective, minTimeDelaySeconds: options.BreakTimeSeconds); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return Fail(busId, Iec61363Status.Failed, ex.Message, times); }

        double f = work.FrequencyHz > 0.0 ? work.FrequencyHz : 50.0;
        double omega = 2.0 * Math.PI * f;
        double[] grid = new double[times.Length + 1];
        Array.Copy(times, grid, times.Length);
        grid[times.Length] = options.BreakTimeSeconds;

        var paths = new List<(double Ikpp, Func<double, double> AcAt, double Ta, bool HasDc)>();
        var contribs = new List<Iec61363Contribution>();
        foreach (Iec60909.MachinePartialCurrent mp in parts.Machines)
        {
            Func<double, double> acAt; double ta = 0.0; bool dc; string kind, name = mp.Name;
            if (mp.IsConverter)
            {
                kind = "converter"; acAt = _ => mp.IkppKa; dc = false;
                notes.Add($"converter '{mp.Name}' contributes a constant current and no DC component (no converter model in this method)");
            }
            else if (motorByName.TryGetValue(mp.Name, out Iec61363MotorData? mot))
            {
                kind = "induction-motor"; name = mot.Name; dc = true; ta = mot.TaS;
                acAt = t => mp.IkppKa * DetMath.Exp(-t / mot.AcTimeConstantS);
            }
            else
            {
                kind = "synchronous"; dc = true;
                Generator? g = work.Generators.FirstOrDefault(x => x.Bus == mp.Bus && (x.Name ?? $"gen@bus {x.Bus}") == mp.Name);
                int atBus = data.Generators.Count(y => y.Bus == mp.Bus);
                Iec61363GeneratorData? gd = data.Generators.FirstOrDefault(x => x.Bus == mp.Bus && (x.Name is null ? atBus == 1 : x.Name == mp.Name));
                if (g is null) { reasons.Add($"generator '{mp.Name}': cannot be matched in the case"); continue; }
                if (gd is null) { reasons.Add($"generator '{mp.Name}' at bus {mp.Bus}: no decay data (X'd, Xd, T''d, T'd, Ta) in the iec61363 data; none is defaulted"); continue; }
                if (!(g.SubtransientXdPu > 0.0)) { reasons.Add($"generator '{mp.Name}': the case carries no X''d (the synthesised default would be used); a real value is needed"); continue; }
                if (!(g.SubtransientXdPu < gd.XdTransPu)) { reasons.Add($"generator '{mp.Name}': case X''d {g.SubtransientXdPu} is not below X'd {gd.XdTransPu}"); continue; }
                double xt = Math.Max(0.0, g.UnitTransformerXPu);
                double x2 = g.SubtransientXdPu + xt, x1 = gd.XdTransPu + xt, x0 = gd.XdSyncPu + xt;
                acAt = t => mp.IkppKa * G74MachineDecay.Ratio(x2, x1, x0, gd.TdSubS, gd.TdTransS, t);
                ta = gd.TaS;
            }
            double[] ac = grid.Select(acAt).ToArray();
            double[] dcv = dc ? grid.Select(t => Iec61363Waveform.DcComponent(mp.IkppKa, ta, t)).ToArray() : new double[grid.Length];
            paths.Add((mp.IkppKa, acAt, ta, dc));
            contribs.Add(new Iec61363Contribution(kind, name, mp.Bus, mp.IkppKa, ac.Take(times.Length).ToArray(), dcv.Take(times.Length).ToArray(),
                ac[times.Length], dcv[times.Length]));
        }
        if (reasons.Count > 0) return Fail(busId, Iec61363Status.Incomplete, reasons, times);
        notes.Add("network meshing is not checked: the standard's method is for unmeshed systems");

        int nt = times.Length;
        double[] acSum = new double[nt], dcSum = new double[nt], asym = new double[nt];
        for (int i = 0; i < nt; i++)
        {
            foreach (Iec61363Contribution c in contribs) { acSum[i] += c.AcKa[i]; dcSum[i] += c.DcKa[i]; }
            double dcRms = dcSum[i] / Math.Sqrt(2.0);
            asym[i] = Math.Sqrt(acSum[i] * acSum[i] + dcRms * dcRms);
        }
        double acB = 0.0, dcB = 0.0;
        foreach (Iec61363Contribution c in contribs) { acB += c.AcAtBreakKa; dcB += c.DcAtBreakKa; }

        (double peak, double tpk) = Iec61363Waveform.Peak(t =>
        {
            double s = 0.0;
            foreach (var p in paths)
                s += Iec61363Waveform.Instant(p.AcAt(t), p.HasDc ? Iec61363Waveform.DcComponent(p.Ikpp, p.Ta, t) : 0.0, omega, t);
            return s;
        }, f, options.PeakWindowSeconds);

        Iec61363BreakerCheck? chk = null;
        if (options.Breaker is Iec61363Breaker br)
        {
            bool mk = peak <= br.MakingPeakKa, bk = acB <= br.BreakingRmsKa;
            chk = new Iec61363BreakerCheck(mk, bk, mk && bk, br.MakingPeakKa - peak, br.BreakingRmsKa - acB);
        }
        return new Iec61363BusResult(busId, Iec61363Status.Ok, Array.Empty<string>(), parts.TotalIkppKa, times, acSum, dcSum, asym, acB, dcB,
            peak, tpk, contribs, chk, notes, Awaiting);
    }

    private static Iec61363BusResult Fail(int bus, Iec61363Status st, string reason, double[] times) => Fail(bus, st, new[] { reason }, times);

    private static Iec61363BusResult Fail(int bus, Iec61363Status st, IReadOnlyList<string> reasons, double[] times) =>
        new(bus, st, reasons, double.NaN, times, times.Select(_ => double.NaN).ToArray(), times.Select(_ => double.NaN).ToArray(),
            times.Select(_ => double.NaN).ToArray(), double.NaN, double.NaN, double.NaN, double.NaN, Array.Empty<Iec61363Contribution>(), null,
            Array.Empty<string>(), Awaiting);
}
