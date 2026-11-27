using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>One staged dip of <c>frt-long</c>: the target voltage and duration, the fault reactance of each stage (from its
/// start time), how many passes the staging took, the largest PCC deviation from the target during the dip (signed, with its
/// time from inception) and the final observed run.</summary>
public sealed record LongFaultRun(double DipSeconds, double TargetPu, int FaultBus, double AtSeconds, IReadOnlyList<(double FromSeconds, double ReactancePu)> Stages,
    IReadOnlyList<RmsEvent> Events, int Passes, double WorstDeviationPu, double WorstAtSeconds, string? StagingProblem,
    RmsTransientStability.ObservedStudy Observed, double StepSeconds, double DurationSeconds)
{
    /// <summary>2.1.126: the lowest PCC deviation from the target over the dip samples (signed; negative = below the target).
    /// The verdict reads it, as <c>frt-envelope</c> reads the lowest fault-period voltage.</summary>
    public double MinDeviationPu { get; init; } = double.NaN;
}

/// <summary>
/// <c>frt-long</c>: GB faults over 140 ms (ECC.6.3.15.9). Figures ECC.6.3.15.9(a) and (b) are voltage-duration profiles:
/// each point is a dip level and how long the unit must ride it through, not a voltage-against-time trace (ECC.A.4A.3). A
/// point is tested as a balanced dip at the PCC held at the profile's voltage for the point's duration, then cleared.
/// <para>The dip is held by a sequence of three-phase fault stages through a reactance. Every stage starts with the
/// retained-voltage reactance X = U Xth / (1 - U) (<see cref="GridCodeChecks.RetainedVoltageReactance"/>). After each pass
/// over the whole run, each stage's reactance is corrected from its mean PCC voltage Vm: the source behind Xth that gives Vm
/// is E = Vm (X + Xth) / X, and the reactance that gives U from E is U Xth / (E - U). At a fixed point Vm = U exactly,
/// whatever error Xth carries. The passes stop when every dip sample lies within 1e-3 pu of U (the depth rule of
/// <c>frt-envelope</c>), or after the pass limit.</para>
/// Verdict for a point (2.1.126, the tolerances of <c>frt-envelope</c>): NOT DEMONSTRATED when the ECC.6.3.15.8 pre-fault
/// conditions are not met; NOT EVALUATED when staying connected cannot be judged. When the unit lost synchronism or a unit
/// relay tripped it: NOT DEMONSTRATED if the PCC went below the target by more than 1e-9 pu
/// (<see cref="GridCodeChecks.FrtTolerancePu"/>: the test was more severe, so a trip is allowed) or the dip ran longer than
/// asked, else FAIL (also when the whole dip stayed above the target: a trip in a milder test is a failure). When it stayed
/// connected and stable: NOT DEMONSTRATED if the lowest PCC voltage of the dip lies above the target by more than 1e-3 pu
/// (the test was milder), else PASS (ECC.6.3.15.9.2.1(a)(i) / (b)(i); a ride-through of a dip that went below the target
/// is a harsher test passed, and says so). The active-power rules (ii) and (iii) and the 30-minute rule (iv) are not
/// checked.
/// </summary>
public static class LongFaultCheck
{
    public const string Id = "frt-long";

    /// <summary>How far (pu) the PCC voltage may lie from the profile's voltage during the dip: the <c>frt-envelope</c>
    /// depth rule (<see cref="GridCodeChecks.FrtDepthTolerancePu"/>).</summary>
    public const double TolerancePu = GridCodeChecks.FrtDepthTolerancePu;

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The whole number of steps in <paramref name="seconds"/>; refused unless it is one to 1e-9 relative.</summary>
    internal static int WholeSteps(double seconds, double stepSeconds, string what)
    {
        double n = Math.Round(seconds / stepSeconds);
        if (!(n >= 1.0) || Math.Abs(n * stepSeconds - seconds) > 1e-9 * Math.Max(1.0, seconds) || n > int.MaxValue)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{what} ({R(seconds)} s) must be a whole number of steps of {R(stepSeconds)} s, at least one."));
        return (int)n;
    }

    /// <summary>
    /// Stage a dip at <paramref name="faultBus"/> holding the PCC at <paramref name="targetPu"/> for <paramref name="dipSeconds"/>
    /// from <paramref name="atSeconds"/>, then clear it and run <paramref name="afterSeconds"/> more. Stages last
    /// <paramref name="stageSeconds"/> (the last one is cut at the end of the dip). Times are whole steps.
    /// </summary>
    public static LongFaultRun RunStaged(GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data, int pccBus, int faultBus,
        double atSeconds, double dipSeconds, double targetPu, double stageSeconds, double stepSeconds, double afterSeconds, int maxPasses)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(pf);
        ArgumentNullException.ThrowIfNull(data);
        if (!(targetPu > 0.0 && targetPu < 1.0)) throw new ArgumentOutOfRangeException(nameof(targetPu), targetPu, "the dip voltage must lie strictly between 0 and 1 pu.");
        if (maxPasses < 1) throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "at least one pass.");
        int atSteps = WholeSteps(atSeconds, stepSeconds, "the dip start");
        int dipSteps = WholeSteps(dipSeconds, stepSeconds, "the dip duration");
        int stageSteps = WholeSteps(stageSeconds, stepSeconds, "the stage length");
        int afterSteps = WholeSteps(afterSeconds, stepSeconds, "the run after clearing");
        int nStages = (dipSteps + stageSteps - 1) / stageSteps;
        var startStep = new int[nStages + 1];
        for (int j = 0; j < nStages; j++) startStep[j] = atSteps + j * stageSteps;
        startStep[nStages] = atSteps + dipSteps;
        double T(int steps) => steps * stepSeconds;
        double duration = T(atSteps + dipSteps + afterSteps);
        double xth = GridCodeChecks.TheveninPu(model, pf, faultBus);
        var x = new double[nStages];
        Array.Fill(x, GridCodeChecks.RetainedVoltageReactance(targetPu, xth));
        double half = 0.5 * stepSeconds;

        RmsTransientStability.ObservedStudy? obs = null;
        List<RmsEvent> events = new();
        double worst = double.NaN, worstAt = double.NaN, lowest = double.NaN;
        string? problem = null;
        int pass = 0;
        while (true)
        {
            pass++;
            events = Events(faultBus, x, startStep, T);
            obs = RmsTransientStability.StudyEventsObserved(model, pf, data, events, new[] { pccBus }, stepSeconds: stepSeconds, durationSeconds: duration);
            IReadOnlyList<double> times = obs.Buses.TimeSeconds;
            double[] v = obs.Buses.MagnitudePu[0];
            var sum = new double[nStages];
            var count = new int[nStages];
            worst = 0.0; worstAt = double.NaN; lowest = double.PositiveInfinity;
            int j = 0;
            for (int k = 0; k < times.Count; k++)
            {
                double t = times[k];
                if (t < T(startStep[0]) - half) continue;
                if (t >= T(startStep[nStages]) - half) break;
                while (j + 1 < nStages && t >= T(startStep[j + 1]) - half) j++;
                sum[j] += v[k]; count[j]++;
                double d = v[k] - targetPu;
                if (Math.Abs(d) > Math.Abs(worst) || double.IsNaN(worstAt)) { worst = d; worstAt = t - T(atSteps); }
                if (d < lowest) lowest = d;
            }
            if (!(Math.Abs(worst) > TolerancePu) || pass >= maxPasses || !obs.Result.StayedInSynchronism) break;
            var next = new double[nStages];
            for (int s = 0; s < nStages; s++)
            {
                if (count[s] == 0) { next[s] = x[s]; continue; }
                double vm = sum[s] / count[s];
                double e = x[s] > 0.0 ? vm * (x[s] + xth) / x[s] : double.NaN;
                if (!(e > targetPu) || !double.IsFinite(e))
                {
                    problem = string.Create(CultureInfo.InvariantCulture, $"the PCC voltage cannot be held at {R(targetPu)} pu by a fault reactance in stage {s + 1} (mean {R(vm)} pu through {R(x[s])} pu)");
                    break;
                }
                next[s] = targetPu * xth / (e - targetPu);
            }
            if (problem is not null) break;
            x = next;
        }
        var stages = new List<(double, double)>(nStages);
        for (int s = 0; s < nStages; s++) stages.Add((T(startStep[s]), x[s]));
        return new LongFaultRun(dipSeconds, targetPu, faultBus, T(atSteps), stages, events, pass, worst, worstAt, problem, obs!, stepSeconds, duration)
        { MinDeviationPu = lowest };
    }

    /// <summary>The events of a staged dip: the fault at the first stage, then at each later stage whose reactance differs a
    /// clearance and a new fault at the same instant (the RMS events at one time apply in list order), and the clearance at
    /// the end of the dip.</summary>
    private static List<RmsEvent> Events(int bus, double[] x, int[] startStep, Func<int, double> time)
    {
        var list = new List<RmsEvent> { new RmsSequenceFault(time(startStep[0]), bus, FaultType.ThreePhase, 0.0, x[0]) };
        for (int j = 1; j < x.Length; j++)
        {
            if (x[j] == x[j - 1]) continue;
            list.Add(new RmsFaultClear(time(startStep[j]), bus));
            list.Add(new RmsSequenceFault(time(startStep[j]), bus, FaultType.ThreePhase, 0.0, x[j]));
        }
        list.Add(new RmsFaultClear(time(startStep[^1]), bus));
        return list;
    }

    /// <summary>The profile of a module (ECC.6.3.15.9.2.1(a) or (b)); null when none applies.</summary>
    public static LongFaultProfile? ProfileFor(GridCodeExtRequirementSet ext, ModuleType moduleType, string technology)
    {
        ArgumentNullException.ThrowIfNull(ext);
        return ext.LongFaultProfiles.FirstOrDefault(p => p.ModuleTypes.Contains(moduleType.ToString(), StringComparer.Ordinal)
            && p.Technologies.Contains(technology, StringComparer.Ordinal));
    }

    /// <summary>The dip length in whole steps for a profile duration: the duration itself when it is a whole number of steps
    /// (to 1e-9 relative), else rounded up to the next whole step, so the dip is never shorter than the profile asks.</summary>
    public static double WholeStepDip(double durationSeconds, double stepSeconds)
    {
        double n = Math.Round(durationSeconds / stepSeconds);
        if (n >= 1.0 && Math.Abs(n * stepSeconds - durationSeconds) <= 1e-9 * Math.Max(1.0, durationSeconds)) return durationSeconds;
        return Math.Ceiling(durationSeconds / stepSeconds) * stepSeconds;
    }

    /// <summary>The verdict for one staged dip (see the type summary). When <paramref name="requestedSeconds"/> is shorter
    /// than the dip run (a profile duration that is not a whole number of steps, rounded up), the test is longer than the
    /// profile asks: staying connected still shows ride-through, but a trip is allowed, so it is NOT DEMONSTRATED, not FAIL.</summary>
    public static GridCodeCheckResult Judge(LongFaultRun run, LongFaultProfile profile, GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data,
        int unitIndex, FrtPreFault? preFault, double? requestedSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(profile);
        string clause = profile.Clause + "(i)";
        string label = string.Create(CultureInfo.InvariantCulture, $"dip to {R(run.TargetPu)} pu for {R(run.DipSeconds)} s");
        var notes = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"balanced dip at bus {run.FaultBus} from {R(run.AtSeconds)} s, held by {run.Stages.Count} fault stage(s) after {run.Passes} pass(es); {profile.Figure} is a voltage-duration profile (ECC.A.4A.3)"),
            $"{profile.Clause}(ii) to (iv) (active power during and after the dip, the 30-minute rule) are not checked",
        };
        if (preFault is not null) notes.AddRange(preFault.Notes);
        if (!run.Observed.Result.StayedInSynchronism)
            notes.Add("synchronism was lost in the run; the staging stops correcting the stages once it is");
        bool longer = requestedSeconds is double rq && run.DipSeconds > rq;
        if (longer)
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"the profile's {R(requestedSeconds!.Value)} s is not a whole number of {R(run.StepSeconds)} s steps; the dip ran {R(run.DipSeconds)} s, longer than asked"));
        if (run.StagingProblem is string sp)
            return new GridCodeCheckResult(Id, clause, RuleOutcome.NotEvaluated, run.TargetPu + run.WorstDeviationPu, run.TargetPu, null, "pu", sp + ", so the dip is not demonstrated")
            { Status = "NOT DEMONSTRATED", Case = label, AtSeconds = run.WorstAtSeconds, Notes = notes };
        if (preFault?.Problem is string problem)
            return new GridCodeCheckResult(Id, clause, RuleOutcome.NotEvaluated, null, null, null, "pu", $"the pre-fault conditions of {preFault.Clause} are not met: {problem}")
            { Status = "NOT DEMONSTRATED", Case = label, Notes = notes };
        double lo = run.MinDeviationPu;
        if (double.IsNaN(lo)) throw new ArgumentException("frt-long: the run carries no dip samples (MinDeviationPu is not set).", nameof(run));
        bool below = lo < -GridCodeChecks.FrtTolerancePu;
        bool milder = lo > TolerancePu;
        double? v = run.TargetPu + run.WorstDeviationPu, u = run.TargetPu, margin = run.WorstDeviationPu;
        notes.Add(string.Create(CultureInfo.InvariantCulture, $"the PCC voltage during the dip lies from {R(run.TargetPu + lo)} pu (lowest) to within {R(Math.Abs(run.WorstDeviationPu))} pu of {R(run.TargetPu)} pu (largest deviation)"));
        (bool? connected, string why) = GridCodeChecks.StayedConnected(run.Events, run.Observed, run.StepSeconds, run.DurationSeconds, model, pf, data, unitIndex);
        if (connected is null)
            return new GridCodeCheckResult(Id, clause, RuleOutcome.NotEvaluated, v, u, margin, "pu", why) { Case = label, AtSeconds = run.WorstAtSeconds, Notes = notes };
        if (connected == false)
        {
            if (below)
                return new GridCodeCheckResult(Id, clause, RuleOutcome.NotEvaluated, v, u, margin, "pu",
                    string.Create(CultureInfo.InvariantCulture, $"{why}; the PCC voltage went below {R(run.TargetPu)} pu by {R(-lo)} pu (more than {R(GridCodeChecks.FrtTolerancePu)} pu), so the test is more severe than the profile, a trip is allowed and compliance is not shown"))
                { Status = "NOT DEMONSTRATED", Case = label, AtSeconds = run.WorstAtSeconds, Notes = notes };
            if (longer)
                return new GridCodeCheckResult(Id, clause, RuleOutcome.NotEvaluated, v, u, margin, "pu",
                    why + "; the dip ran longer than the profile asks, so a trip is allowed and compliance is not shown")
                { Status = "NOT DEMONSTRATED", Case = label, AtSeconds = run.WorstAtSeconds, Notes = notes };
            return new GridCodeCheckResult(Id, clause, RuleOutcome.Fail, v, u, margin, "pu",
                milder ? why + "; the dip stayed above the profile (a milder test), so the trip is a failure" : why)
            { Case = label, AtSeconds = run.WorstAtSeconds, Notes = notes };
        }
        if (milder)
            return new GridCodeCheckResult(Id, clause, RuleOutcome.NotEvaluated, v, u, margin, "pu",
                string.Create(CultureInfo.InvariantCulture, $"the lowest PCC voltage during the dip is {R(run.TargetPu + lo)} pu, above {R(run.TargetPu)} pu by more than {R(TolerancePu)} pu: the test is milder than the profile, so it does not show ride-through on it"))
            { Status = "NOT DEMONSTRATED", Case = label, AtSeconds = run.WorstAtSeconds, Notes = notes };
        if (below) notes.Add(string.Create(CultureInfo.InvariantCulture, $"the PCC voltage went below {R(run.TargetPu)} pu by up to {R(-lo)} pu, a harsher test than the profile, and the unit rode it through"));
        return new GridCodeCheckResult(Id, clause, RuleOutcome.Pass, v, u, margin, "pu", why)
        { Case = label, AtSeconds = run.WorstAtSeconds, Notes = notes };
    }
}
