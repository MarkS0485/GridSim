using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.LongTerm;

/// <summary>WP21: one sample of a long-term run (the equilibrium of one QSS step).</summary>
/// <param name="TimeSeconds">The step time (s).</param>
/// <param name="FrequencyDeviationHz">The system frequency deviation (Hz).</param>
/// <param name="MinVoltagePu">The lowest bus voltage (pu).</param>
/// <param name="MinVoltageBus">The bus with the lowest voltage (the first in model order on a tie).</param>
/// <param name="AgcUnitMw">Each AGC unit's AGC set point change (MW), in AGC unit order.</param>
/// <param name="Taps">Each timed tap changer's tap ratio, in timer order.</param>
/// <param name="LoadPMw">Each recovering load's active power drawn (MW), in load order.</param>
/// <param name="LoadQMvar">Each recovering load's reactive power drawn (Mvar), in load order.</param>
/// <param name="EquilibriumPasses">Power-flow solves the step's equilibrium took (0 for the starting solve).</param>
/// <param name="ResidualMw">The reference bus's generation less its allocation at the accepted equilibrium (MW).</param>
/// <param name="Events">The disturbances applied at this step, "; "-separated (empty when none).</param>
public sealed record LongTermSample(double TimeSeconds, double FrequencyDeviationHz, double MinVoltagePu, int MinVoltageBus,
    IReadOnlyList<double> AgcUnitMw, IReadOnlyList<double> Taps, IReadOnlyList<double> LoadPMw, IReadOnlyList<double> LoadQMvar,
    int EquilibriumPasses, double ResidualMw, string Events);

/// <summary>WP21: a disturbance as applied.</summary>
public sealed record LongTermAppliedEvent(int InputIndex, string Description, double RequestedSeconds, double AppliedSeconds, int AppliedStep);

/// <summary>WP21: one tap move.</summary>
public sealed record LongTermTapMove(string Transformer, double AtSeconds, double FromTap, double ToTap);

/// <summary>WP21: a long-term run.</summary>
/// <param name="StepSeconds">The QSS step (s).</param>
/// <param name="BetaMwPerHz">The natural frequency response beta = D + sum 1/R (MW/Hz).</param>
/// <param name="AgcGenerators">The AGC units, by generator index.</param>
/// <param name="OltcTransformers">The timed tap changers, by transformer name.</param>
/// <param name="LoadBuses">The recovering loads, by bus.</param>
/// <param name="Samples">One sample per step reached, from t = 0.</param>
/// <param name="Events">The disturbances, in input order, with the step they were applied at.</param>
/// <param name="TapMoves">Every tap move in time order.</param>
/// <param name="Completed">False when the run stopped early (see <paramref name="StopReason"/>).</param>
/// <param name="StopReason">Why the run stopped early, or null.</param>
public sealed record LongTermResult(double StepSeconds, double BetaMwPerHz, IReadOnlyList<int> AgcGenerators, IReadOnlyList<string> OltcTransformers,
    IReadOnlyList<int> LoadBuses, IReadOnlyList<LongTermSample> Samples, IReadOnlyList<LongTermAppliedEvent> Events,
    IReadOnlyList<LongTermTapMove> TapMoves, bool Completed, string? StopReason)
{
    /// <summary>The run as <c>long-term.csv</c>: one row per sample, invariant culture, round-trip numbers.</summary>
    public string ToCsv()
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("t_s,df_hz,min_v_pu,min_v_bus");
        foreach (int g in AgcGenerators) sb.Append(",p_agc_gen").Append(g.ToString(CultureInfo.InvariantCulture)).Append("_mw");
        foreach (string t in OltcTransformers) sb.Append(",tap_").Append(t.Replace(',', '_').Replace('"', '_'));
        foreach (int b in LoadBuses) sb.Append(",pd_bus").Append(b.ToString(CultureInfo.InvariantCulture)).Append("_mw");
        foreach (int b in LoadBuses) sb.Append(",qd_bus").Append(b.ToString(CultureInfo.InvariantCulture)).Append("_mvar");
        sb.Append(",passes,residual_mw,events\n");
        foreach (LongTermSample s in Samples)
        {
            sb.Append(N(s.TimeSeconds)).Append(',').Append(N(s.FrequencyDeviationHz)).Append(',').Append(N(s.MinVoltagePu)).Append(',')
                .Append(s.MinVoltageBus.ToString(CultureInfo.InvariantCulture));
            foreach (double v in s.AgcUnitMw) sb.Append(',').Append(N(v));
            foreach (double v in s.Taps) sb.Append(',').Append(N(v));
            foreach (double v in s.LoadPMw) sb.Append(',').Append(N(v));
            foreach (double v in s.LoadQMvar) sb.Append(',').Append(N(v));
            sb.Append(',').Append(s.EquilibriumPasses.ToString(CultureInfo.InvariantCulture)).Append(',').Append(N(s.ResidualMw)).Append(',')
                .Append('"').Append(GridSim.Core.IO.CsvFormulaGuard.Neutralise(s.Events).Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"\n");
        }
        return sb.ToString();
    }
}

/// <summary>
/// WP21 (RC5): long-term (quasi-steady-state, QSS) simulation, after Van Cutsem and Vournas, <i>Voltage Stability of Electric
/// Power Systems</i> (Kluwer, 1998), chapter 6, and Van Cutsem's QSS lecture notes (University of Liege). The fast dynamics
/// (rotor swings, exciters, governors' own lags) are replaced by their equilibrium, a power flow; the slow elements are
/// stepped with a fixed QSS step h.
///
/// <para><b>Each step n (time n h):</b></para>
/// <list type="number">
/// <item>Disturbances whose time has come (the first step with <c>n h + h 1e-6 &gt;= time</c>, the RMS kernel's stage rule)
/// are applied, in time order and then input order: branch trips, generator trips, constant-power load steps, set-point
/// changes.</item>
/// <item>The equilibrium: a Newton power flow with frequency as one system value. Each governed unit produces
/// <c>P_sched + P_agc,i - (MBase_i / (R_i f0)) df</c>, held within [Pmin, Pmax]; the loads give up <c>D df</c> in proportion
/// to their starting active power. Starting from the last step's df, each pass solves the power flow and reads the
/// reference bus's generation less its allocation, e; the frequency is corrected by <c>df -= e / (D + sum of 1/R of the
/// units not at a limit)</c> until |e| is within <see cref="LongTermCase.EquilibriumToleranceMw"/>. On a lossless network
/// one correction settles it: then <c>df = -dP / beta</c>, <c>beta = D + sum 1/R</c>.</item>
/// <item>The sample is recorded.</item>
/// <item>The slow states advance over h, in this order, each from the step's equilibrium: AGC
/// (<see cref="AgcModel"/>), the tap changer timers (<see cref="OltcTimer"/>), the recovering loads
/// (<see cref="LoadRecoveryQss"/>).</item>
/// </list>
/// <para>At t = 0 the sample is the starting power flow itself (df = 0); the reference bus's units are scheduled at their
/// solved output there, so the undisturbed system is at equilibrium. A disturbance at t = 0 is refused (the start must be
/// the undisturbed state the recovering loads and timers start from).</para>
/// <para><b>Fail loud.</b> When an equilibrium power flow does not converge, or every governed unit is at a limit with an
/// imbalance left, the run stops with "loss of short-term equilibrium" and the time; it does not carry on. When the
/// frequency loop runs out of passes (<see cref="LongTermCase.MaxEquilibriumPasses"/>), the run stops with "numerical
/// failure" and the time instead: that is GridSim's loop failing, not the system losing its equilibrium.</para>
/// <para><b>Scope.</b> Single area (AGC tie-line deviation 0). Frequency is one value for the system. No fast dynamics: no
/// electromechanical oscillation and no instability of the fast subsystem. No over-excitation limiter and no timed switched
/// shunts. Reactive limits as the power flow applies them. A tap changer of the model that has no timer holds its tap.</para>
/// </summary>
public static class QssEngine
{
    /// <summary>Run <paramref name="c"/> on <paramref name="model"/>. Bad input throws <see cref="ArgumentException"/>;
    /// a starting power flow that does not converge throws <see cref="InvalidOperationException"/>; a loss of equilibrium
    /// during the run ends it early (<see cref="LongTermResult.Completed"/> false, with the reason).</summary>
    public static LongTermResult Run(GridModel model, LongTermCase c)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(c);
        double h = c.StepSeconds;
        if (!(h > 0.0) || !double.IsFinite(h)) throw new ArgumentOutOfRangeException(nameof(c), h, "long-term: the step must be a finite number of seconds above 0.");
        if (!(c.DurationSeconds > 0.0) || !double.IsFinite(c.DurationSeconds))
            throw new ArgumentOutOfRangeException(nameof(c), c.DurationSeconds, "long-term: the duration must be a finite number of seconds above 0.");
        double ratio = c.DurationSeconds / h;
        int steps = (int)Math.Round(ratio);
        if (steps < 1 || Math.Abs(ratio - steps) > 1e-9 * Math.Max(1.0, steps))
            throw new ArgumentException("long-term: the duration must be a whole number of steps.", nameof(c));
        if (!(c.EquilibriumToleranceMw > 0.0) || !(c.PowerFlowTolerancePu > 0.0) || c.MaxEquilibriumPasses < 1)
            throw new ArgumentException("long-term: the equilibrium tolerance and the power-flow tolerance must be above 0, and at least one pass allowed.", nameof(c));
        if (!(c.LoadDampingMwPerHz >= 0.0) || !double.IsFinite(c.LoadDampingMwPerHz))
            throw new ArgumentOutOfRangeException(nameof(c), c.LoadDampingMwPerHz, "long-term: the load damping must be a finite number of MW/Hz, 0 or more.");
        double f0 = model.FrequencyHz;
        if (!(f0 > 0.0) || !double.IsFinite(f0)) throw new ArgumentException("long-term: the model's nominal frequency must be above 0.", nameof(model));

        int g = model.Generators.Count;
        int[] slackBuses = model.Buses.Where(b => b.Type == BusType.Slack).Select(b => b.Id).Distinct().ToArray();
        if (slackBuses.Length != 1)
            throw new ArgumentException($"long-term: the case has {slackBuses.Length} reference buses; one is needed (one system frequency, one area).", nameof(model));
        int slackId = slackBuses[0];
        if (!model.Generators.Any(x => x.Bus == slackId))
            throw new ArgumentException($"long-term: the reference bus {slackId} has no in-service generator.", nameof(model));

        // Governed units: the response 1/R in MW/Hz.
        var invR = new double[g];
        var governed = new bool[g];
        foreach (LongTermUnit u in c.Units)
        {
            if (u is null) throw new ArgumentException("long-term: a unit entry is null.", nameof(c));
            if (u.Generator < 0 || u.Generator >= g) throw new ArgumentOutOfRangeException(nameof(c), u.Generator, $"long-term: unit generator {u.Generator} is not in the case (0 to {g - 1}).");
            if (governed[u.Generator]) throw new ArgumentException($"long-term: generator {u.Generator} is listed as a unit twice.", nameof(c));
            if (!(u.DroopPu > 0.0) || !double.IsFinite(u.DroopPu)) throw new ArgumentOutOfRangeException(nameof(c), u.DroopPu, $"long-term: unit {u.Generator}: the droop must be above 0.");
            double mBase = u.MBaseMva ?? MachineBase.EffectiveMva(model.Generators[u.Generator], model.BaseMva);
            if (!(mBase > 0.0) || !double.IsFinite(mBase)) throw new ArgumentOutOfRangeException(nameof(c), mBase, $"long-term: unit {u.Generator}: the base must be above 0.");
            governed[u.Generator] = true;
            invR[u.Generator] = mBase / (u.DroopPu * f0);
        }
        double sumInvR = 0.0;
        for (int i = 0; i < g; i++) sumInvR += invR[i];
        double beta = c.LoadDampingMwPerHz + sumInvR;
        if (!(beta > 0.0)) throw new ArgumentException("long-term: no frequency response (no governed unit and no load damping), so the frequency is not defined.", nameof(c));

        // AGC.
        AgcModel? agc = null;
        int[] agcGen = Array.Empty<int>();
        double[] agcPart = Array.Empty<double>();
        if (c.Agc is { } a)
        {
            agcGen = new int[a.Units.Count];
            agcPart = new double[a.Units.Count];
            for (int k = 0; k < a.Units.Count; k++)
            {
                AgcUnit au = a.Units[k] ?? throw new ArgumentException("long-term: an AGC unit entry is null.", nameof(c));
                if (au.Generator < 0 || au.Generator >= g) throw new ArgumentOutOfRangeException(nameof(c), au.Generator, $"long-term: AGC unit generator {au.Generator} is not in the case.");
                if (Array.IndexOf(agcGen, au.Generator, 0, k) >= 0) throw new ArgumentException($"long-term: generator {au.Generator} is an AGC unit twice.", nameof(c));
                agcGen[k] = au.Generator;
                agcPart[k] = au.Participation;
            }
            agc = new AgcModel(a.IntegralGainPerSecond, a.BiasMwPerHz ?? beta, a.RateLimitMwPerSecond, agcPart);
        }

        // Recovering loads (checked before the solve; started from its voltages).
        var busById = new Dictionary<int, Bus>();
        foreach (Bus b in model.Buses) busById[b.Id] = b;
        var loadBuses = new List<int>();
        foreach (RecoveringLoadSpec l in c.Loads)
        {
            if (l is null) throw new ArgumentException("long-term: a load entry is null.", nameof(c));
            if (!busById.TryGetValue(l.BusId, out Bus? lb)) throw new ArgumentException($"long-term: recovering load bus {l.BusId} is not in the case.", nameof(c));
            if (loadBuses.Contains(l.BusId)) throw new ArgumentException($"long-term: bus {l.BusId} has two recovering loads.", nameof(c));
            if (lb.Zip is { VariesWithVoltage: true }) throw new ArgumentException($"long-term: bus {l.BusId} already has a voltage-dependent load; a recovering load replaces a constant-power one.", nameof(c));
            if (!(h < 2.0 * l.TpSeconds) || !(h < 2.0 * l.TqSeconds))
                throw new ArgumentException($"long-term: recovering load at bus {l.BusId}: the step must be below 2 Tp and 2 Tq (forward Euler is unstable beyond).", nameof(c));
            loadBuses.Add(l.BusId);
        }

        // Timed tap changers.
        int[] branchOfRecord = SteadyStateSolver.MapOltcRecordsToBranches(model);
        var timers = new List<OltcTimer>();
        foreach (OltcTimerSpec t in c.OltcTimers)
        {
            if (t is null) throw new ArgumentException("long-term: a tap changer timer is null.", nameof(c));
            if (!(t.FirstDelaySeconds >= 0.0) || !double.IsFinite(t.FirstDelaySeconds) || !(t.NextDelaySeconds >= 0.0) || !double.IsFinite(t.NextDelaySeconds))
                throw new ArgumentOutOfRangeException(nameof(c), $"long-term: tap changer '{t.Transformer}': the delays must be finite, 0 or more.");
            int rec = -1;
            for (int k = 0; k < model.Oltcs.Count; k++)
                if (string.Equals(model.Oltcs[k].Transformer, t.Transformer, StringComparison.Ordinal))
                {
                    if (rec >= 0) throw new ArgumentException($"long-term: transformer '{t.Transformer}' has more than one tap changer record.", nameof(c));
                    rec = k;
                }
            if (rec < 0 || branchOfRecord[rec] < 0) throw new ArgumentException($"long-term: no tap changer of the case drives transformer '{t.Transformer}'.", nameof(c));
            if (timers.Any(x => x.BranchIndex == branchOfRecord[rec])) throw new ArgumentException($"long-term: transformer '{t.Transformer}' has two timers.", nameof(c));
            if (!busById.ContainsKey(model.Oltcs[rec].ControlsBus)) throw new ArgumentException($"long-term: tap changer '{t.Transformer}' controls bus {model.Oltcs[rec].ControlsBus}, which is not in the case.", nameof(c));
            timers.Add(new OltcTimer(model.Oltcs[rec], t, model.Branches[branchOfRecord[rec]], branchOfRecord[rec]));
        }

        // Disturbances: checked, and the step each lands on.
        double eps = h * MultiMachineSubtransient.StageTimeToleranceSteps;
        var evStep = new int[c.Events.Count];
        var tripCheck = new bool[g];
        var branchCheck = new bool[model.Branches.Count];
        var order = c.Events.Select((e, i) => (e, i)).OrderBy(x => x.e?.AtSeconds ?? double.NaN).ThenBy(x => x.i).ToList();
        foreach ((LongTermEvent? ev, int i) in order)
        {
            if (ev is null) throw new ArgumentException($"long-term: events[{i}] is null.", nameof(c));
            string what = $"long-term: events[{i}] ({ev.Describe()} at {ev.AtSeconds.ToString("R", CultureInfo.InvariantCulture)} s)";
            if (!double.IsFinite(ev.AtSeconds) || !(ev.AtSeconds > eps))
                throw new ArgumentOutOfRangeException(nameof(c), ev.AtSeconds, $"{what}: the time must be after t = 0 (the start is the undisturbed state the loads and timers start from).");
            if (ev.AtSeconds > c.DurationSeconds + eps) throw new ArgumentOutOfRangeException(nameof(c), ev.AtSeconds, $"{what}: it is after the end of the run.");
            evStep[i] = AppliedStep(ev.AtSeconds, h);
            switch (ev)
            {
                case LongTermBranchTrip bt:
                    if (bt.Branch < 0 || bt.Branch >= model.Branches.Count) throw new ArgumentOutOfRangeException(nameof(c), bt.Branch, $"{what}: no such branch.");
                    if (!model.Branches[bt.Branch].InService || branchCheck[bt.Branch]) throw new ArgumentException($"{what}: the branch is already out.", nameof(c));
                    branchCheck[bt.Branch] = true;
                    break;
                case LongTermGeneratorTrip gt:
                    if (gt.Generator < 0 || gt.Generator >= g) throw new ArgumentOutOfRangeException(nameof(c), gt.Generator, $"{what}: no such generator.");
                    if (tripCheck[gt.Generator]) throw new ArgumentException($"{what}: the generator is already tripped.", nameof(c));
                    tripCheck[gt.Generator] = true;
                    bool slackLeft = false;
                    for (int k = 0; k < g; k++) if (model.Generators[k].Bus == slackId && !tripCheck[k]) slackLeft = true;
                    if (!slackLeft) throw new ArgumentException($"{what}: it would leave the reference bus {slackId} with no unit.", nameof(c));
                    break;
                case LongTermLoadStep ls:
                    if (!busById.ContainsKey(ls.BusId)) throw new ArgumentException($"{what}: bus {ls.BusId} is not in the case.", nameof(c));
                    if (loadBuses.Contains(ls.BusId)) throw new ArgumentException($"{what}: bus {ls.BusId} has a recovering load; step another bus.", nameof(c));
                    if (!double.IsFinite(ls.PMw) || !double.IsFinite(ls.QMvar) || (ls.PMw == 0.0 && ls.QMvar == 0.0))
                        throw new ArgumentException($"{what}: the step must be finite and not zero.", nameof(c));
                    break;
                case LongTermSetPoint sp:
                    if (sp.Generator < 0 || sp.Generator >= g) throw new ArgumentOutOfRangeException(nameof(c), sp.Generator, $"{what}: no such generator.");
                    if (!double.IsFinite(sp.PMw)) throw new ArgumentException($"{what}: the set point must be finite.", nameof(c));
                    break;
                default:
                    throw new ArgumentException($"{what}: unknown event kind.", nameof(c));
            }
        }

        // The starting power flow.
        var options = new PowerFlowOptions { Tolerance = c.PowerFlowTolerancePu };
        PowerFlowResult r0 = SolverSelector.CreateNewton(model).Solve(model, options);
        if (!r0.Converged) throw new InvalidOperationException("long-term: the starting power flow does not converge, so there is no starting equilibrium.");
        var rowOf = new Dictionary<int, int>();
        for (int i = 0; i < r0.BusIds.Count; i++) rowOf[r0.BusIds[i]] = i;
        var sched = new double[g];
        for (int i = 0; i < g; i++)
            sched[i] = model.Generators[i].Bus == slackId ? SolvedGeneration.UnitOutput(model, r0, i).PMw : model.Generators[i].Pg;
        for (int i = 0; i < g; i++)
            if (governed[i] && (sched[i] < model.Generators[i].Pmin || sched[i] > model.Generators[i].Pmax))
                throw new ArgumentException($"long-term: governed generator {i} starts at {sched[i].ToString("R", CultureInfo.InvariantCulture)} MW, outside its limits.", nameof(c));
        var loads = new LoadRecoveryQss[loadBuses.Count];
        for (int k = 0; k < loads.Length; k++)
        {
            Bus lb = busById[loadBuses[k]];
            loads[k] = new LoadRecoveryQss(c.Loads[k], lb.Pd, lb.Qd, r0.Vm[rowOf[lb.Id]]);
        }
        // The damping relief: shared by the buses' starting active load.
        double sumP0 = 0.0;
        foreach (Bus b in model.Buses) if (b.Pd > 0.0) sumP0 += b.Pd;
        if (c.LoadDampingMwPerHz > 0.0 && !(sumP0 > 0.0)) throw new ArgumentException("long-term: load damping needs load to act on.", nameof(c));

        // Run state.
        var tripped = new bool[g];
        var branchOut = new bool[model.Branches.Count];
        var stepP = new Dictionary<int, double>();
        var stepQ = new Dictionary<int, double>();
        var applied = new LongTermAppliedEvent[c.Events.Count];
        var moves = new List<LongTermTapMove>();
        var samples = new List<LongTermSample>(steps + 1);
        double df = 0.0;
        string? stop = null;

        double AgcOf(int i)
        {
            if (agc is null) return 0.0;
            double alive = 0.0, mine = 0.0;
            for (int k = 0; k < agcGen.Length; k++)
            {
                if (tripped[agcGen[k]]) continue;
                alive += agcPart[k];
                if (agcGen[k] == i) mine += agcPart[k];
            }
            return alive > 0.0 ? mine / alive * agc.TotalMw : 0.0;
        }
        double Raw(int i, double f) => sched[i] + AgcOf(i) - invR[i] * f;
        double Alloc(int i, double f)
        {
            if (tripped[i]) return 0.0;
            double p = Raw(i, f);
            return governed[i] ? Math.Clamp(p, model.Generators[i].Pmin, model.Generators[i].Pmax) : p;
        }
        GridModel Build(double f)
        {
            var buses = new List<Bus>(model.Buses.Count);
            foreach (Bus b0 in model.Buses)
            {
                // A voltage-controlled bus whose every unit is tripped no longer holds its voltage: it becomes a load bus.
                Bus b = b0;
                if (b0.Type == BusType.Pv)
                {
                    bool live = false;
                    for (int i = 0; i < g; i++) if (model.Generators[i].Bus == b0.Id && !tripped[i]) live = true;
                    if (!live) b = b0 with { Type = BusType.Pq };
                }
                double relief = c.LoadDampingMwPerHz > 0.0 && b.Pd > 0.0 ? c.LoadDampingMwPerHz * f * (b.Pd / sumP0) : 0.0;
                int li = loadBuses.IndexOf(b.Id);
                if (li >= 0) { buses.Add(loads[li].AsBus(b, relief)); continue; }
                double dp = stepP.TryGetValue(b.Id, out double sp) ? sp : 0.0, dq = stepQ.TryGetValue(b.Id, out double sq) ? sq : 0.0;
                buses.Add(relief == 0.0 && dp == 0.0 && dq == 0.0 ? b : b with { Pd = b.Pd + dp + relief, Qd = b.Qd + dq });
            }
            var gens = new List<Generator>(g);
            for (int i = 0; i < g; i++) gens.Add(model.Generators[i] with { Pg = Alloc(i, f), InService = !tripped[i] });
            var branches = new List<Branch>(model.Branches.Count);
            for (int j = 0; j < model.Branches.Count; j++)
            {
                Branch br = model.Branches[j];
                if (branchOut[j]) br = br with { InService = false };
                branches.Add(br);
            }
            foreach (OltcTimer t in timers)
            {
                Branch br = branches[t.BranchIndex];
                if (t.Record.ImpedanceCorrection is { } ic)
                {
                    (double rc, double xc) = ic.At(t.Tap);
                    branches[t.BranchIndex] = br with { TapRatio = t.Tap, R = rc, X = xc };
                }
                else branches[t.BranchIndex] = br with { TapRatio = t.Tap };
            }
            return model.CopyWith(buses: buses, generators: gens, branches: branches);
        }
        LongTermSample Sample(double t, double fHz, PowerFlowResult r, GridModel m, int passes, double residual, string events)
        {
            double vmin = double.PositiveInfinity; int vbus = 0;
            for (int i = 0; i < r.BusIds.Count; i++) if (r.Vm[i] < vmin) { vmin = r.Vm[i]; vbus = r.BusIds[i]; }
            var agcMw = new double[agcGen.Length];
            for (int k = 0; k < agcGen.Length; k++) agcMw[k] = tripped[agcGen[k]] ? 0.0 : AgcOf(agcGen[k]);
            var taps = timers.Select(x => x.Tap).ToArray();
            var pl = new double[loads.Length];
            var ql = new double[loads.Length];
            for (int k = 0; k < loads.Length; k++)
            {
                int row = rowOf[loadBuses[k]];
                Bus mb = m.Buses.First(b => b.Id == loadBuses[k]);
                (pl[k], ql[k]) = LoadAtVoltage.Mw(mb, r.Vm[row]);
            }
            return new LongTermSample(t, fHz, vmin, vbus, agcMw, taps, pl, ql, passes, residual, events);
        }

        samples.Add(Sample(0.0, 0.0, r0, model, 0, 0.0, ""));
        PowerFlowResult last = r0;
        for (int n = 0; ; n++)
        {
            if (n > 0)
            {
                // 1. Disturbances due at this step.
                var names = new List<string>();
                foreach ((LongTermEvent? ev, int i) in order)
                {
                    if (evStep[i] != n) continue;
                    switch (ev)
                    {
                        case LongTermBranchTrip bt: branchOut[bt.Branch] = true; break;
                        case LongTermGeneratorTrip gt: tripped[gt.Generator] = true; break;
                        case LongTermLoadStep ls:
                            stepP[ls.BusId] = (stepP.TryGetValue(ls.BusId, out double p0) ? p0 : 0.0) + ls.PMw;
                            stepQ[ls.BusId] = (stepQ.TryGetValue(ls.BusId, out double q0) ? q0 : 0.0) + ls.QMvar;
                            break;
                        case LongTermSetPoint sp: sched[sp.Generator] = sp.PMw; break;
                    }
                    applied[i] = new LongTermAppliedEvent(i, ev!.Describe(), ev.AtSeconds, n * h, n);
                    names.Add(ev.Describe());
                }

                // 2. The equilibrium.
                double t = n * h;
                (PowerFlowResult? r, GridModel? m, double fNew, int passes, double residual, string? why, bool numerical) = Equilibrium(df);
                if (r is null)
                {
                    // A frequency loop that runs out of passes is a numerical failure of GridSim's own loop, not a sign that
                    // the system has no equilibrium, so it has its own stop reason (2.1.126).
                    stop = numerical
                        ? $"numerical failure at t = {t.ToString("R", CultureInfo.InvariantCulture)} s: {why}"
                        : $"loss of short-term equilibrium at t = {t.ToString("R", CultureInfo.InvariantCulture)} s: {why}";
                    break;
                }
                df = fNew;
                last = r;
                // 3. The sample.
                samples.Add(Sample(t, df, r, m!, passes, residual, string.Join("; ", names)));
            }
            if (n == steps) break;

            // 4. The slow states, from this step's equilibrium.
            if (agc is not null)
            {
                // Anti-windup: the AGC units still in service with a share, each held at a limit at this equilibrium.
                bool any = false, allUp = true, allDown = true;
                for (int k = 0; k < agcGen.Length; k++)
                {
                    int i = agcGen[k];
                    if (tripped[i] || !(agcPart[k] > 0.0)) continue;
                    any = true;
                    double raw = Raw(i, df);
                    // Only a governed unit is held within [Pmin, Pmax] (Alloc); an ungoverned one follows its set point.
                    if (!(governed[i] && raw >= model.Generators[i].Pmax)) allUp = false;
                    if (!(governed[i] && raw <= model.Generators[i].Pmin)) allDown = false;
                }
                agc.Advance(df, h, any && allUp, any && allDown);
            }
            foreach (OltcTimer tm in timers)
            {
                double from = tm.Tap;
                if (tm.Advance(last.Vm[rowOf[tm.ControlsBus]], h)) moves.Add(new LongTermTapMove(tm.Transformer, (n + 1) * h, from, tm.Tap));
            }
            for (int k = 0; k < loads.Length; k++) loads[k].Advance(last.Vm[rowOf[loadBuses[k]]], h);
        }

        for (int i = 0; i < applied.Length; i++)
            applied[i] ??= new LongTermAppliedEvent(i, c.Events[i].Describe(), c.Events[i].AtSeconds, double.NaN, -1);
        return new LongTermResult(h, beta, agcGen, timers.Select(x => x.Transformer).ToList(), loadBuses, samples, applied, moves, stop is null, stop);

        (PowerFlowResult?, GridModel?, double, int, double, string?, bool) Equilibrium(double fStart)
        {
            double f = fStart;
            // 2.1.126: the residual e(f) rises with f and is piecewise linear, with a kink wherever a unit reaches or leaves
            // a limit. The plain step f -= e / slope (slope at the current point) can jump across a kink and cycle between
            // the two sides, so the frequencies seen on each side bound the root, and a step that leaves the bracket is
            // replaced by the bracket's midpoint. A step inside the bracket is the plain step, so a loop that never
            // brackets the root, or always steps inside it, is unchanged bit for bit.
            double fLo = double.NegativeInfinity, fHi = double.PositiveInfinity;
            for (int pass = 1; pass <= c.MaxEquilibriumPasses; pass++)
            {
                GridModel m = Build(f);
                IPowerFlowSolver solver = SolverSelector.CreateNewton(m);
                PowerFlowResult r = solver.Solve(m, options);
                if (!r.Converged) return (null, null, f, pass, double.NaN, $"the power flow does not converge ({r.Status})", false);
                int si = rowOf[slackId];
                Bus slackBus = m.Buses.First(b => b.Id == slackId);
                double slackGen = r.PInjMw[si] + LoadAtVoltage.Mw(slackBus, r.Vm[si]).PMw;
                double allocated = 0.0;
                for (int i = 0; i < g; i++) if (model.Generators[i].Bus == slackId && !tripped[i]) allocated += Alloc(i, f);
                double e = slackGen - allocated;
                if (Math.Abs(e) <= c.EquilibriumToleranceMw) return (r, m, f, pass, e, null, false);
                double slope = c.LoadDampingMwPerHz;
                for (int i = 0; i < g; i++)
                {
                    if (!governed[i] || tripped[i]) continue;
                    double raw = Raw(i, f);
                    if (raw > model.Generators[i].Pmin && raw < model.Generators[i].Pmax) slope += invR[i];
                }
                if (!(slope > 0.0)) return (null, null, f, pass, e, $"every governed unit is at a limit with {e.ToString("R", CultureInfo.InvariantCulture)} MW left unshared", false);
                if (e > 0.0) fHi = Math.Min(fHi, f); else fLo = Math.Max(fLo, f);
                double next = f - e / slope;
                f = double.IsFinite(fLo) && double.IsFinite(fHi) && !(next > fLo && next < fHi) ? 0.5 * (fLo + fHi) : next;
            }
            return (null, null, f, c.MaxEquilibriumPasses, double.NaN,
                $"the frequency loop did not settle within {c.MaxEquilibriumPasses} passes (raise maxEquilibriumPasses or the equilibrium tolerance)", true);
        }
    }

    /// <summary>The first step n with <c>n h + h 1e-6 &gt;= t</c> (the RMS kernel's stage rule).</summary>
    public static int AppliedStep(double atSeconds, double stepSeconds)
    {
        double eps = stepSeconds * MultiMachineSubtransient.StageTimeToleranceSteps;
        int k = (int)Math.Ceiling((atSeconds - eps) / stepSeconds);
        if (k < 0) k = 0;
        while (k * stepSeconds + eps < atSeconds) k++;
        while (k > 0 && (k - 1) * stepSeconds + eps >= atSeconds) k--;
        return k;
    }
}
