using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.DistributionOptimisation;

/// <summary>
/// The settings shared by <see cref="CapacitorPlacement"/> and <see cref="VoltVarOptimisation"/>: the voltage band, the
/// power flow of every trial and the neighbour confirmation.
/// </summary>
public sealed record DistOptCommon
{
    /// <summary>The lower voltage limit (pu) of a bus with no <see cref="Bus.VminPu"/> of its own (default 0.95).</summary>
    public double VminPu { get; init; } = 0.95;

    /// <summary>The upper voltage limit (pu) of a bus with no <see cref="Bus.VmaxPu"/> of its own (default 1.05).</summary>
    public double VmaxPu { get; init; } = 1.05;

    /// <summary>The Newton tolerance on the largest power mismatch of every trial power flow (pu; default 1e-10).</summary>
    public double TolerancePu { get; init; } = 1e-10;

    /// <summary>The Newton step cap of every trial power flow (default 30).</summary>
    public int MaxIterations { get; init; } = 30;

    /// <summary>Run the neighbour confirmation after the search (default on): every combination of one step down, no
    /// change or one step up of the confirmed controls, 3^m power flows. Refused above <see cref="DistOpt.MaxConfirmControls"/>
    /// controls.</summary>
    public bool Confirm { get; init; } = true;

    /// <summary>The most search rounds (a greedy or coordinate-descent pass followed by a confirmation) before the search
    /// stops and says so (default 100).</summary>
    public int MaxRounds { get; init; } = 100;

    internal void Validate(string paramName)
    {
        if (!double.IsFinite(VminPu) || !double.IsFinite(VmaxPu) || !(VminPu > 0) || !(VmaxPu > VminPu))
            throw new ArgumentException($"Distribution optimisation: the voltage band must be finite with 0 < Vmin < Vmax (got {VminPu.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, {VmaxPu.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}).", paramName);
        if (!(TolerancePu > 0) || !(TolerancePu <= PowerFlowOptions.MaxTolerance))
            throw new ArgumentException($"Distribution optimisation: the power-flow tolerance must be above 0 and at most {PowerFlowOptions.MaxTolerance} pu.", paramName);
        if (MaxIterations < 1) throw new ArgumentException("Distribution optimisation: the power-flow step cap must be 1 or more.", paramName);
        if (MaxRounds < 1) throw new ArgumentException("Distribution optimisation: the round cap must be 1 or more.", paramName);
    }
}

/// <summary>How a capacitor placement or volt/VAr search ended.</summary>
public enum DistOptStatus
{
    /// <summary>No move of the search and no neighbour combination improves the objective: a local discrete optimum. (A
    /// capacitor placement with more than <see cref="DistOpt.MaxConfirmControls"/> banks confirms over the banks the greedy
    /// search changed last; <see cref="CapPlaceResult.UnconfirmedBanks"/> names the others.)</summary>
    LocalOptimum,
    /// <summary>The round cap was reached; a further round may still improve the objective.</summary>
    RoundCap,
}

/// <summary>A solved bus voltage (pu) in model bus order.</summary>
public readonly record struct DistOptBusVoltage(int BusId, double VmPu);

/// <summary>
/// The evaluation and comparison rules shared by the distribution optimisation searches.
/// <para>Every trial is one Newton power flow (<see cref="NewtonRaphsonPowerFlow"/>, generator reactive limits on). A trial
/// that does not converge is rejected. The plain Newton solve does not run the regulating loop of OLTCs or transformer flow
/// controls, so a case with an OLTC that is not one of the controls, or with a transformer flow control, is refused by
/// name (<see cref="RefuseUnregulated"/>) rather than solved with those devices held at their case settings. HVDC links,
/// FACTS devices, static var compensators and induction machines are solved by the Newton solve itself. Trials are compared lexicographically: first the total voltage violation (the sum
/// over buses of how far each is below its lower or above its upper limit, pu), then the objective. So an infeasible start
/// first moves towards the band, and from a feasible point an infeasible trial is never taken. A trial replaces the
/// incumbent only when its violation is lower by more than <see cref="ViolationTiePu"/>, or equal within that and its
/// objective lower by more than the objective tie (<see cref="ObjectiveTie"/>); trials are made in a fixed order, so ties go
/// to the lowest index.</para>
/// </summary>
public static class DistOpt
{
    /// <summary>The most controls the neighbour confirmation takes (3^8 = 6561 power flows).</summary>
    public const int MaxConfirmControls = 8;

    /// <summary>Violations closer than this (pu, summed over buses) count as equal.</summary>
    public const double ViolationTiePu = 1e-12;

    /// <summary>The relative part of the objective tie: 8 units in the last place of the larger objective (8 x 2^-52), what
    /// the sums that form an objective can round by.</summary>
    public const double ObjectiveRelativeTie = 8 * 2.220446049250313e-16;

    /// <summary>
    /// The solver noise on a trial's losses or demand (MW): 2 x buses x tolerance x base MVA. The Newton solve stops when
    /// every bus mismatch is within the tolerance (pu), so each bus's injection is off by at most tolerance x base MVA, and
    /// the losses (or the demand) of the solved state by at most the sum over the buses; the factor 2 covers the slack and
    /// the incremental losses that carry the error to it. Two trials closer than this cannot be told apart by the solve.
    /// </summary>
    public static double SolverNoiseMw(GridModel model, DistOptCommon common)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(common);
        return 2.0 * model.Buses.Count * common.TolerancePu * model.BaseMva;
    }

    /// <summary>
    /// The objective tie: objectives closer than this count as equal. It is the solver noise in the objective's own unit
    /// (<paramref name="noise"/>: <see cref="SolverNoiseMw"/> times the objective's value per MW, so x energy value x hours
    /// per year for capacitor placement, x 1 for volt/VAr optimisation) plus <see cref="ObjectiveRelativeTie"/> of the larger
    /// objective's magnitude. Capital costs are added exactly, so they carry no solver noise.
    /// </summary>
    public static double ObjectiveTie(double noise, double objA, double objB) =>
        noise + ObjectiveRelativeTie * Math.Max(Math.Abs(objA), Math.Abs(objB));

    /// <summary>One solved trial.</summary>
    internal readonly record struct Eval(bool Ok, double LossMw, double DemandMw, double ViolationPu, double VminPu, double VmaxPu,
        DistOptBusVoltage[] Voltages);

    internal static readonly Eval Failed = new(false, double.NaN, double.NaN, double.PositiveInfinity, double.NaN, double.NaN, Array.Empty<DistOptBusVoltage>());

    /// <summary>Solve <paramref name="m"/> and measure it. Throws <see cref="InvalidDataException"/> when a bus is left out of
    /// the solution (it is in no island with a source).</summary>
    internal static Eval Evaluate(GridModel m, DistOptCommon c)
    {
        var pf = new PowerFlowOptions { Tolerance = c.TolerancePu, MaxIterations = c.MaxIterations };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, pf);
        if (!r.Converged) return Failed;
        var index = new Dictionary<int, int>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) index[r.BusIds[i]] = i;
        double viol = 0, demand = 0, vmin = double.PositiveInfinity, vmax = double.NegativeInfinity;
        var volts = new DistOptBusVoltage[m.Buses.Count];
        for (int k = 0; k < m.Buses.Count; k++)
        {
            Bus b = m.Buses[k];
            if (!index.TryGetValue(b.Id, out int i))
                throw new InvalidDataException($"Distribution optimisation: bus {b.Id} is not in the solved network (no source reaches it).");
            double v = r.Vm[i];
            double lo = b.VminPu ?? c.VminPu, hi = b.VmaxPu ?? c.VmaxPu;
            if (v < lo) viol += lo - v;
            if (v > hi) viol += v - hi;
            vmin = Math.Min(vmin, v);
            vmax = Math.Max(vmax, v);
            demand += LoadAtVoltage.Mw(b, v).PMw;
            volts[k] = new DistOptBusVoltage(b.Id, v);
        }
        return new Eval(true, r.TotalLossMw, demand, viol, vmin, vmax, volts);
    }

    /// <summary>True when trial <paramref name="a"/> (objective <paramref name="objA"/>) beats the incumbent
    /// <paramref name="inc"/> (objective <paramref name="objInc"/>) under the lexicographic rule, with the solver noise
    /// <paramref name="noise"/> in the objective's unit (<see cref="ObjectiveTie"/>).</summary>
    internal static bool Better(in Eval a, double objA, in Eval inc, double objInc, double noise)
    {
        if (!a.Ok) return false;
        if (!inc.Ok) return true;
        if (a.ViolationPu < inc.ViolationPu - ViolationTiePu) return true;
        if (a.ViolationPu > inc.ViolationPu + ViolationTiePu) return false;
        return objA < objInc - ObjectiveTie(noise, objA, objInc);
    }

    /// <summary>
    /// Refuse, by name, the devices whose regulation the plain Newton trial solve does not run: every OLTC not in
    /// <paramref name="controlledTaps"/> (by transformer name) and every transformer flow control. Solving with them held at
    /// their case settings would give losses and voltages that differ from <c>pf</c> without saying so.
    /// </summary>
    internal static void RefuseUnregulated(GridModel model, IReadOnlyList<string> controlledTaps, string who, string hint, string paramName)
    {
        var held = new List<string>();
        foreach (Oltc o in model.Oltcs)
            if (!controlledTaps.Contains(o.Transformer, StringComparer.Ordinal)) held.Add($"OLTC '{o.Transformer}'");
        foreach (TransformerFlowControl f in model.TransformerFlowControls) held.Add($"transformer flow control '{f.Transformer}'");
        if (held.Count > 0)
            throw new ArgumentException($"{who}: every trial is a plain Newton power flow, which does not regulate {string.Join(", ", held)}; the case is refused rather than solved with them held at their case settings. {hint}", paramName);
    }

    /// <summary>
    /// The neighbour confirmation: every combination of offsets -1, 0, +1 on the controls <paramref name="controls"/>
    /// (positions <paramref name="pos"/>, counts <paramref name="counts"/>) other than all zeros, in lexicographic order of
    /// the offsets (the first control most significant, -1 before 0 before +1), skipping positions out of range. The best
    /// combination that beats the incumbent replaces it. Returns the number of power flows made.
    /// </summary>
    internal static int Confirm(IReadOnlyList<int> controls, int[] pos, int[] counts, Func<int[], (Eval E, double Obj)> evaluate,
        double noise, ref Eval incumbent, ref double incumbentObj, out bool improved)
    {
        int m = controls.Count;
        improved = false;
        if (m == 0) return 0;
        if (m > MaxConfirmControls)
            throw new InvalidOperationException($"Distribution optimisation: the neighbour confirmation over {m} controls is above the cap of {MaxConfirmControls} (3^m power flows). Turn the confirmation off or give fewer controls.");
        int total = 1;
        for (int i = 0; i < m; i++) total *= 3;
        int[] best = (int[])pos.Clone();
        int solves = 0;
        var trial = new int[pos.Length];
        for (int code = 0; code < total; code++)
        {
            Array.Copy(pos, trial, pos.Length);
            int rest = code;
            bool allZero = true, inRange = true;
            for (int i = m - 1; i >= 0; i--)
            {
                int off = rest % 3 - 1;
                rest /= 3;
                if (off != 0) allZero = false;
                int k = controls[i];
                int p = pos[k] + off;
                if (p < 0 || p >= counts[k]) { inRange = false; break; }
                trial[k] = p;
            }
            if (allZero || !inRange) continue;
            (Eval e, double obj) = evaluate(trial);
            solves++;
            if (Better(e, obj, incumbent, incumbentObj, noise))
            {
                incumbent = e;
                incumbentObj = obj;
                Array.Copy(trial, best, pos.Length);
                improved = true;
            }
        }
        Array.Copy(best, pos, pos.Length);
        return solves;
    }

    /// <summary>The bus list with <paramref name="addMvar"/> added to each bus's <see cref="Bus.Bs"/> (a zero addition keeps
    /// the bus record as it is).</summary>
    internal static Bus[] WithShunts(GridModel model, double[] addMvar)
    {
        var buses = new Bus[model.Buses.Count];
        for (int k = 0; k < buses.Length; k++)
        {
            Bus b = model.Buses[k];
            buses[k] = addMvar[k] != 0.0 ? b with { Bs = b.Bs + addMvar[k] } : b;
        }
        return buses;
    }

    /// <summary>The model-order position of every bus id.</summary>
    internal static Dictionary<int, int> BusIndex(GridModel model)
    {
        var index = new Dictionary<int, int>(model.Buses.Count);
        for (int k = 0; k < model.Buses.Count; k++)
            if (!index.TryAdd(model.Buses[k].Id, k))
                throw new InvalidDataException($"Distribution optimisation: bus {model.Buses[k].Id} appears twice in the case.");
        return index;
    }
}
