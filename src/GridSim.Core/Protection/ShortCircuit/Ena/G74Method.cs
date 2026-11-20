using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;

namespace GridSim.Core.Protection.ShortCircuit.Ena;

/// <summary>Options of a G74-style study. Times are the break times (s after fault inception) at which the AC envelope is
/// evaluated; <see cref="Decay"/> false switches machine decay off (every contribution stays at its initial value).</summary>
public sealed record G74Options(
    IReadOnlyList<double> Times, double BreakTimeSeconds = 0.10, double FaultDurationSeconds = 1.0,
    ShortCircuitObjective Objective = ShortCircuitObjective.Maximum, bool Decay = true,
    PeakFactorMethod PeakMethod = PeakFactorMethod.MethodB, Iec60909.BreakerRating? Breaker = null);

/// <summary>One source's contribution to the fault: its initial current, its AC current at each requested time, its
/// current at the breaker time and its steady current (kA).</summary>
public sealed record G74Contribution(string Kind, string Name, int Bus, double IkppKa, IReadOnlyList<double> AtTimesKa, double AtBreakTimeKa, double SteadyKa);

public enum G74Status { Ok, Incomplete, Failed }

/// <summary>The G74-style duty at one bus. When <see cref="Status"/> is not Ok the numbers are NaN and
/// <see cref="Reasons"/> says what is missing.</summary>
public sealed record G74BusResult(
    int Bus, G74Status Status, IReadOnlyList<string> Reasons,
    double IkppKa, double PeakIpKa, double Kappa, IReadOnlyList<double> Times, IReadOnlyList<double> BreakKa, double SteadyKa,
    double BreakAtTbKa, IReadOnlyList<G74Contribution> Contributions, double IecIbKa, Iec60909.Duty? IecDuty,
    Iec60909.BreakerAdequacy? Breaker, string ConverterModel, IReadOnlyList<string> Awaiting)
{
    public string Verdict => Status switch { G74Status.Ok => "OK", G74Status.Incomplete => "INCOMPLETE", _ => "FAILED" };
}

/// <summary>
/// A G74-style short-circuit study built over the existing IEC 60909 machinery, read-only (the incumbent
/// <see cref="Iec60909"/> is called, never changed). Order of operations: validate, prefault/island and sequence network
/// (inside <see cref="Iec60909"/>), per-source contribution (<see cref="Iec60909.PartialThreePhase"/> on the case with its
/// induction motors added as equivalent sources), decay at the requested times, converter contribution (constant), sum in
/// the fixed order machines, motors, converters, breaker check (signed margins, as <c>iec60909 --breaker</c>), report.
/// <para>
/// WHAT THIS IS NOT. ENA ER G74 Issue 2:2021 could not be read (see the R4-06 evidence report). Its coefficients,
/// clauses, motor rules, converter rules and worked examples are not in this build and are listed in
/// <see cref="Awaiting"/> on every result. The decay used is the classical two-exponential envelope per machine with its
/// own reactances (unit transformer added) and the supplied short-circuit time constants; the sum of contribution
/// magnitudes is exact for in-phase partial currents and conservative otherwise. The peak current is the incumbent IEC 60909
/// peak, not a G74 peak.
/// </para>
/// </summary>
public static class G74Method
{
    public static readonly IReadOnlyList<string> Awaiting = new[]
    {
        "AWAITING STANDARD TEXT: ENA ER G74 Issue 2:2021 clause numbers and edition-specific formulae (text not obtained)",
        "AWAITING STANDARD TEXT: G74 synchronous machine decay treatment (AVR, external impedance, time dependence)",
        "AWAITING STANDARD TEXT: G74 induction motor contribution rules and decay",
        "AWAITING STANDARD TEXT: G74 converter negative-sequence behaviour and voltage dependence",
        "AWAITING STANDARD TEXT: G74 peak and DC component treatment (IEC 60909 kappa is used)",
        "AWAITING STANDARD TEXT: G74 worked examples (no example reproduced)",
    };

    private const string ConverterText = G74ConverterContribution.PositiveSequenceModel + "; " + G74ConverterContribution.NegativeSequenceModel;

    public static string MotorGeneratorName(G74MotorData m) => $"motor:{m.Name}@bus {m.Bus}";

    /// <summary>Run the study at <paramref name="busId"/>.</summary>
    public static G74BusResult Compute(GridModel model, int busId, G74Data data, G74Options options)
    {
        var reasons = new List<string>();
        double[] times = options.Times.ToArray();
        if (times.Length == 0) reasons.Add("no break times requested");
        foreach (double t in times) if (!(t >= 0.0) || !double.IsFinite(t)) reasons.Add($"break time {t} is not a finite number of seconds >= 0");
        if (!(options.BreakTimeSeconds >= 0.0) || !double.IsFinite(options.BreakTimeSeconds)) reasons.Add("breaker time must be a finite number of seconds >= 0");
        if (!model.Buses.Any(b => b.Id == busId)) return Fail(busId, G74Status.Failed, $"bus {busId} is not in the case", times);
        if (reasons.Count > 0) return Fail(busId, G74Status.Failed, reasons, times);

        // Motors: each becomes an equivalent X''d source on its own base, appended to the fleet in file order.
        GridModel work = model;
        var motorByName = new Dictionary<string, G74MotorData>(StringComparer.Ordinal);
        if (data.Motors.Count > 0)
        {
            var fleet = new List<Generator>(model.AllGenerators);
            foreach (G74MotorData m in data.Motors)
            {
                if (!model.Buses.Any(b => b.Id == m.Bus)) { reasons.Add($"motor '{m.Name}': bus {m.Bus} is not in the case"); continue; }
                if (!(m.RatedMva > 0.0) || !(m.LockedRotorCurrentRatio > 0.0)) { reasons.Add($"motor '{m.Name}': rating or locked-rotor ratio missing"); continue; }
                if (m.Decay == MotorDecayModel.Iec60909Q && !(m.MwPerPolePair > 0.0)) { reasons.Add($"motor '{m.Name}': iec60909-q decay needs ratedMw and polePairs"); continue; }
                if (m.Decay == MotorDecayModel.Exponential && !(m.TimeConstantS > 0.0)) { reasons.Add($"motor '{m.Name}': exponential decay needs a time constant"); continue; }
                string gname = MotorGeneratorName(m);
                if (!motorByName.TryAdd(gname, m)) { reasons.Add($"motor name '{m.Name}' is duplicated at bus {m.Bus}"); continue; }
                fleet.Add(new Generator(m.Bus, Pg: 0.0, MBase: m.RatedMva, Name: gname, Type: "induction-motor",
                    SubtransientXdPu: G74InductionMotor.EquivalentImpedancePu(m.LockedRotorCurrentRatio), ConverterInterfaced: false));
            }
            if (reasons.Count > 0) return Fail(busId, G74Status.Incomplete, reasons, times);
            work = model.CopyWith(generators: fleet);
        }

        Iec60909.FaultContribution parts;
        Iec60909.Duty duty;
        try
        {
            parts = Iec60909.PartialThreePhase(work, busId, options.Objective, minTimeDelaySeconds: options.BreakTimeSeconds);
            duty = Iec60909.ThreePhase(work, busId, options.Objective, f0Hz: work.FrequencyHz, breakTimeSeconds: options.BreakTimeSeconds,
                faultDurationSeconds: options.FaultDurationSeconds, peakFactorMethod: options.PeakMethod);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Fail(busId, G74Status.Failed, ex.Message, times);
        }

        // Evaluation grid: the requested times, then the breaker time.
        double[] grid = new double[times.Length + 1];
        Array.Copy(times, grid, times.Length);
        grid[times.Length] = options.BreakTimeSeconds;

        var contribs = new List<G74Contribution>(parts.Machines.Count);
        foreach (Iec60909.MachinePartialCurrent mp in parts.Machines)
        {
            double[] v;
            string kind;
            string name = mp.Name;
            double steady;
            if (mp.IsConverter)
            {
                kind = "converter"; v = Constant(mp.IkppKa, grid.Length); steady = mp.IkppKa;
            }
            else if (motorByName.TryGetValue(mp.Name, out G74MotorData? mot))
            {
                kind = "induction-motor"; name = mot.Name; steady = options.Decay ? 0.0 : mp.IkppKa;
                v = options.Decay
                    ? grid.Select(t => mp.IkppKa * G74InductionMotor.Ratio(mot.Decay, mot.TimeConstantS, mot.MwPerPolePair, t)).ToArray()
                    : Constant(mp.IkppKa, grid.Length);
            }
            else if (!options.Decay)
            {
                kind = "synchronous"; v = Constant(mp.IkppKa, grid.Length); steady = mp.IkppKa;
            }
            else
            {
                kind = "synchronous";
                Generator? g = work.Generators.FirstOrDefault(x => x.Bus == mp.Bus && (x.Name ?? $"gen@bus {x.Bus}") == mp.Name);
                int atBus = data.Generators.Count(y => y.Bus == mp.Bus);
                G74GeneratorData? gd = data.Generators.FirstOrDefault(x => x.Bus == mp.Bus && (x.Name is null ? atBus == 1 : x.Name == mp.Name));
                if (g is null) { reasons.Add($"generator '{mp.Name}': cannot be matched in the case"); continue; }
                if (gd is null) { reasons.Add($"generator '{mp.Name}' at bus {mp.Bus}: no decay data (X'd, Xd, T''d, T'd) in the g74 data; none is defaulted"); continue; }
                if (!(g.SubtransientXdPu > 0.0)) { reasons.Add($"generator '{mp.Name}': the case carries no X''d (the synthesised default would be used); decay needs a real value"); continue; }
                if (!(g.SubtransientXdPu < gd.XdTransPu)) { reasons.Add($"generator '{mp.Name}': case X''d {g.SubtransientXdPu} is not below X'd {gd.XdTransPu}"); continue; }
                double xt = Math.Max(0.0, g.UnitTransformerXPu);
                double x2 = g.SubtransientXdPu + xt, x1 = gd.XdTransPu + xt, x0 = gd.XdSyncPu + xt;
                v = grid.Select(t => mp.IkppKa * G74MachineDecay.Ratio(x2, x1, x0, gd.TdSubS, gd.TdTransS, t)).ToArray();
                steady = mp.IkppKa * G74MachineDecay.SteadyRatio(x2, x0);
            }
            contribs.Add(new G74Contribution(kind, name, mp.Bus, mp.IkppKa, v.Take(times.Length).ToArray(), v[times.Length], steady));
        }
        if (reasons.Count > 0) return Fail(busId, G74Status.Incomplete, reasons, times);

        // Sum in the fixed element order (the order PartialThreePhase lists them: machines and motors in network order,
        // converters last).
        double[] breakKa = new double[times.Length];
        for (int i = 0; i < times.Length; i++)
        {
            double s = 0.0;
            foreach (G74Contribution c in contribs) s += c.AtTimesKa[i];
            breakKa[i] = s;
        }
        double steadySum = 0.0, atTb = 0.0;
        foreach (G74Contribution c in contribs) { steadySum += c.SteadyKa; atTb += c.AtBreakTimeKa; }

        Iec60909.BreakerAdequacy? adequacy = null;
        if (options.Breaker is not null)
        {
            double kv = model.Buses.First(b => b.Id == busId).BaseKv;
            adequacy = Iec60909.CheckBreaker(duty with { BreakingIbKa = atTb, SteadyIkKa = steadySum }, options.Breaker, kv);
        }
        return new G74BusResult(busId, G74Status.Ok, Array.Empty<string>(), duty.IkppKa, duty.PeakIpKa, duty.Kappa, times, breakKa, steadySum,
            atTb, contribs, duty.BreakingIbKa, duty, adequacy, ConverterText, Awaiting);
    }

    private static double[] Constant(double x, int n)
    {
        var a = new double[n];
        Array.Fill(a, x);
        return a;
    }

    private static G74BusResult Fail(int bus, G74Status st, string reason, double[] times) => Fail(bus, st, new[] { reason }, times);

    private static G74BusResult Fail(int bus, G74Status st, IReadOnlyList<string> reasons, double[] times) =>
        new(bus, st, reasons, double.NaN, double.NaN, double.NaN, times, times.Select(_ => double.NaN).ToArray(), double.NaN, double.NaN,
            Array.Empty<G74Contribution>(), double.NaN, null, null, ConverterText, Awaiting);
}
