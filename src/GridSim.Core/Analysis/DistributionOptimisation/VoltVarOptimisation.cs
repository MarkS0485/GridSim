using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.DistributionOptimisation;

/// <summary>What <see cref="VoltVarOptimisation.Run"/> minimises.</summary>
public enum VvoObjective
{
    /// <summary>The network losses (MW).</summary>
    Losses,
    /// <summary>The total real power the loads draw at their solved voltages (MW): conservation voltage reduction (CVR).
    /// Only voltage-dependent (ZIP) loads respond; a constant-power load draws the same at any voltage.</summary>
    Demand,
}

/// <summary>A switched capacitor bank: <paramref name="Mvar"/> at 1 pu added to bus <paramref name="BusId"/> when on.</summary>
public sealed record VvoCapacitor(string Name, int BusId, double Mvar, bool InitiallyOn = false);

/// <summary>The settings of <see cref="VoltVarOptimisation.Run"/>.</summary>
public sealed record VvoOptions
{
    /// <summary>What is minimised (default the losses).</summary>
    public VvoObjective Objective { get; init; } = VvoObjective.Losses;

    /// <summary>The OLTCs to move, by transformer name (<see cref="Oltc.Transformer"/>), in order; null for every OLTC of the
    /// case, in its order; empty for none.</summary>
    public IReadOnlyList<string>? Taps { get; init; }

    /// <summary>The switched capacitor banks, in order (default none).</summary>
    public IReadOnlyList<VvoCapacitor> Capacitors { get; init; } = Array.Empty<VvoCapacitor>();

    /// <summary>The voltage band, power flow and confirmation settings.</summary>
    public DistOptCommon Common { get; init; } = new();
}

/// <summary>A control at the start and at the result. For a tap the value is the ratio (pu); for a capacitor the Mvar
/// added (0 when off). Positions count from 0: a tap's position k is the ratio MinTap + k x StepPu; a capacitor is 0 off,
/// 1 on.</summary>
public sealed record VvoControlState(string Kind, string Name, int StartPosition, double StartValue, int Position, double Value);

/// <summary>One accepted move of the coordinate descent: in sweep <paramref name="Sweep"/> control <paramref name="Name"/>
/// went to <paramref name="Position"/>, giving the objective and violation shown.</summary>
public sealed record VvoMove(int Sweep, string Kind, string Name, int Position, double Objective, double ViolationPu);

/// <summary>The result of <see cref="VoltVarOptimisation.Run"/>.</summary>
/// <param name="Objective">What was minimised.</param>
/// <param name="Controls">The controls in search order (taps, then capacitors).</param>
/// <param name="Moves">The accepted moves in order.</param>
/// <param name="BaseObjectiveValue">The objective at the start.</param>
/// <param name="ObjectiveValue">The objective at the result.</param>
/// <param name="BaseLossMw">Losses at the start.</param>
/// <param name="LossMw">Losses at the result.</param>
/// <param name="BaseDemandMw">Load drawn at the start.</param>
/// <param name="DemandMw">Load drawn at the result.</param>
/// <param name="BaseViolationPu">The total voltage violation at the start.</param>
/// <param name="ViolationPu">The total voltage violation at the result.</param>
/// <param name="VminPu">The lowest bus voltage at the result.</param>
/// <param name="VmaxPu">The highest bus voltage at the result.</param>
/// <param name="Voltages">The bus voltages at the result, in model order.</param>
/// <param name="Sweeps">Coordinate-descent sweeps made.</param>
/// <param name="ConfirmationSolves">Power flows made by the neighbour confirmations.</param>
/// <param name="ConfirmationImprovements">Confirmations that found a better combination.</param>
/// <param name="PowerFlows">Every power flow made, the start included.</param>
/// <param name="Status">How the search ended.</param>
/// <param name="Notes">Recorded decisions (a starting tap moved onto its step grid).</param>
/// <param name="Message">A plain sentence saying how it ended.</param>
public sealed record VvoResult(
    VvoObjective Objective, IReadOnlyList<VvoControlState> Controls, IReadOnlyList<VvoMove> Moves,
    double BaseObjectiveValue, double ObjectiveValue, double BaseLossMw, double LossMw, double BaseDemandMw, double DemandMw,
    double BaseViolationPu, double ViolationPu, double VminPu, double VmaxPu, IReadOnlyList<DistOptBusVoltage> Voltages,
    int Sweeps, int ConfirmationSolves, int ConfirmationImprovements, int PowerFlows, DistOptStatus Status,
    IReadOnlyList<string> Notes, string Message)
{
    /// <summary>True when every bus is within its voltage band at the result.</summary>
    public bool Feasible => ViolationPu <= DistOpt.ViolationTiePu;
}

/// <summary>
/// Volt/VAr optimisation (VVO) and conservation voltage reduction (CVR) on a balanced case by discrete search over OLTC
/// taps and switched capacitor banks.
/// <para>Controls: each OLTC named (its transformer branch's <see cref="Branch.TapRatio"/> on the grid
/// MinTap + k x StepPu, k = 0..floor((MaxTap - MinTap) / StepPu), the same grid the tap optimiser snaps to), then each
/// switched capacitor (off or on, its Mvar added to the bus's <see cref="Bus.Bs"/>). A starting tap off the grid is moved
/// to the nearest grid point and the move is noted. An OLTC with an impedance correction table
/// (<see cref="Oltc.ImpedanceCorrection"/>, a PSS/E TAB) sets the transformer's R and X from the table at every trial ratio
/// other than the case's own, as the steady-state tap loop does (<see cref="TapImpedanceCorrection.At"/>); at the case's
/// ratio the case's R and X are kept. Every OLTC of the case must be a control, and a transformer flow control is refused
/// (<see cref="DistOpt"/>: the trial solves do not regulate them).</para>
/// <para>Objective: <see cref="VvoObjective.Losses"/> or <see cref="VvoObjective.Demand"/>, subject to every bus voltage in
/// its band. Trials are compared by <see cref="DistOpt"/>: voltage violation first, so an infeasible start first
/// minimises the violation.</para>
/// <para>Method: discrete coordinate descent in the fixed control order (taps first, then capacitors). For each control
/// the trials one step down and one step up are solved (in that order) and the better, if it beats the incumbent, is
/// taken at once. Sweeps repeat until one moves nothing. Then the neighbour confirmation over every control (3^m power
/// flows, m at most <see cref="DistOpt.MaxConfirmControls"/>, refused above); if it improves, the descent runs again
/// from there. The result is a local discrete optimum, not a guaranteed global one. The objective tie is the solver noise
/// (<see cref="DistOpt.SolverNoiseMw"/>, MW for both objectives) plus the relative part (<see cref="DistOpt.ObjectiveTie"/>).</para>
/// </summary>
public static class VoltVarOptimisation
{
    /// <summary>Run the search on <paramref name="model"/> (which is not changed). Throws <see cref="ArgumentException"/>
    /// for a bad control or option (more than <see cref="DistOpt.MaxConfirmControls"/> controls with the confirmation on
    /// included), an OLTC of the case that is not a control or a transformer flow control, and
    /// <see cref="InvalidDataException"/> when the starting case does not solve.</summary>
    public static VvoResult Run(GridModel model, VvoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        VvoOptions opt = options ?? new VvoOptions();
        opt.Common.Validate(nameof(options));
        Dictionary<int, int> busIndex = DistOpt.BusIndex(model);
        var notes = new List<string>();

        // Taps.
        IReadOnlyList<string> tapNames = opt.Taps ?? model.Oltcs.Select(o => o.Transformer).ToArray();
        int nt = tapNames.Count, nc = opt.Capacitors.Count, m = nt + nc;
        if (m == 0) throw new ArgumentException("Volt/VAr optimisation: there is no control (no OLTC and no switched capacitor).", nameof(options));
        if (opt.Common.Confirm && m > DistOpt.MaxConfirmControls)
            throw new ArgumentException($"Volt/VAr optimisation: the neighbour confirmation over {m} controls is above the cap of {DistOpt.MaxConfirmControls} (3^m power flows). Turn the confirmation off or give fewer controls.", nameof(options));
        var tapBranch = new int[nt];
        var tapOltc = new Oltc[nt];
        var counts = new int[m];
        var pos = new int[m];
        var names = new string[m];
        var seenTap = new HashSet<string>(StringComparer.Ordinal);
        for (int t = 0; t < nt; t++)
        {
            string name = tapNames[t];
            if (!seenTap.Add(name)) throw new ArgumentException($"Volt/VAr optimisation: OLTC '{name}' is listed twice.", nameof(options));
            Oltc? o = null;
            foreach (Oltc x in model.Oltcs) if (x.Transformer == name) { o = x; break; }
            if (o is null) throw new ArgumentException($"Volt/VAr optimisation: the case has no OLTC on transformer '{name}'.", nameof(options));
            int br = -1;
            for (int k = 0; k < model.Branches.Count; k++)
            {
                if (model.Branches[k].Name != name || !model.Branches[k].InService) continue;
                if (br >= 0) throw new ArgumentException($"Volt/VAr optimisation: more than one in-service branch is named '{name}'.", nameof(options));
                br = k;
            }
            if (br < 0) throw new ArgumentException($"Volt/VAr optimisation: OLTC '{name}' names no in-service branch.", nameof(options));
            if (!double.IsFinite(o.MinTap) || !double.IsFinite(o.MaxTap) || !(o.MinTap > 0) || !(o.MaxTap > o.MinTap) || !(o.StepPu > 0) || !double.IsFinite(o.StepPu))
                throw new ArgumentException($"Volt/VAr optimisation: OLTC '{name}' needs 0 < MinTap < MaxTap and a step above 0.", nameof(options));
            int count = (int)Math.Floor((o.MaxTap - o.MinTap) / o.StepPu + 1e-9) + 1;
            double tap0 = model.Branches[br].TapRatio;
            int k0 = (int)Math.Clamp(Math.Round((tap0 - o.MinTap) / o.StepPu), 0, count - 1);
            if (TapAt(o, k0) != tap0)
                notes.Add($"OLTC '{name}': the starting ratio {tap0.ToString("R", CultureInfo.InvariantCulture)} is not on the step grid; the search starts from {TapAt(o, k0).ToString("R", CultureInfo.InvariantCulture)} (position {k0}).");
            tapBranch[t] = br;
            tapOltc[t] = o;
            counts[t] = count;
            pos[t] = k0;
            names[t] = name;
            if (o.ImpedanceCorrection is not null)
                notes.Add($"OLTC '{name}' carries an impedance correction table: each trial ratio other than the case's sets the transformer's R and X from it.");
        }
        DistOpt.RefuseUnregulated(model, tapNames, "Volt/VAr optimisation", "List every OLTC of the case under the taps (or remove its control record), and remove transformer flow controls.", nameof(model));
        // Capacitors.
        var capBus = new int[nc];
        var seenCap = new HashSet<string>(StringComparer.Ordinal);
        for (int c = 0; c < nc; c++)
        {
            VvoCapacitor cap = opt.Capacitors[c];
            if (string.IsNullOrWhiteSpace(cap.Name)) throw new ArgumentException($"Volt/VAr optimisation: capacitor {c} has no name.", nameof(options));
            if (!seenCap.Add(cap.Name)) throw new ArgumentException($"Volt/VAr optimisation: capacitor '{cap.Name}' is listed twice.", nameof(options));
            if (!busIndex.TryGetValue(cap.BusId, out int k)) throw new ArgumentException($"Volt/VAr optimisation: capacitor '{cap.Name}' is at bus {cap.BusId}, which is not a bus of the case.", nameof(options));
            if (!double.IsFinite(cap.Mvar) || !(cap.Mvar > 0)) throw new ArgumentException($"Volt/VAr optimisation: capacitor '{cap.Name}' needs a size above 0 Mvar.", nameof(options));
            capBus[c] = k;
            counts[nt + c] = 2;
            pos[nt + c] = cap.InitiallyOn ? 1 : 0;
            names[nt + c] = cap.Name;
        }
        int[] start = (int[])pos.Clone();

        double noise = DistOpt.SolverNoiseMw(model, opt.Common);
        int flows = 0;
        (DistOpt.Eval E, double Obj) Solve(int[] p)
        {
            var brs = new Branch[model.Branches.Count];
            for (int k = 0; k < brs.Length; k++) brs[k] = model.Branches[k];
            for (int t = 0; t < nt; t++)
            {
                Branch b0 = brs[tapBranch[t]];
                double tap = TapAt(tapOltc[t], p[t]);
                if (tapOltc[t].ImpedanceCorrection is { } ic && tap != b0.TapRatio)
                {
                    // As the steady-state tap loop: the table sets R and X at every ratio stepped to.
                    (double rc, double xc) = ic.At(tap);
                    brs[tapBranch[t]] = b0 with { TapRatio = tap, R = rc, X = xc };
                }
                else brs[tapBranch[t]] = b0 with { TapRatio = tap };
            }
            var add = new double[model.Buses.Count];
            for (int c = 0; c < nc; c++) if (p[nt + c] == 1) add[capBus[c]] += opt.Capacitors[c].Mvar;
            DistOpt.Eval e = DistOpt.Evaluate(model.CopyWith(buses: DistOpt.WithShunts(model, add), branches: brs), opt.Common);
            flows++;
            return (e, !e.Ok ? double.PositiveInfinity : opt.Objective == VvoObjective.Losses ? e.LossMw : e.DemandMw);
        }

        (DistOpt.Eval cur, double curObj) = Solve(pos);
        if (!cur.Ok) throw new InvalidDataException("Volt/VAr optimisation: the case does not solve at its starting controls.");
        DistOpt.Eval baseEval = cur;
        double baseObj = curObj;

        var moves = new List<VvoMove>();
        var all = Enumerable.Range(0, m).ToArray();
        int sweeps = 0, rounds = 0, confirmSolves = 0, confirmImproved = 0;
        DistOptStatus status = DistOptStatus.LocalOptimum;
        while (true)
        {
            if (rounds >= opt.Common.MaxRounds) { status = DistOptStatus.RoundCap; break; }
            rounds++;
            bool moved = true;
            while (moved)
            {
                moved = false;
                sweeps++;
                for (int i = 0; i < m; i++)
                {
                    int bestP = -1;
                    DistOpt.Eval bestE = cur;
                    double bestObj = curObj;
                    int here = pos[i];
                    for (int d = -1; d <= 1; d += 2)
                    {
                        int p = here + d;
                        if (p < 0 || p >= counts[i]) continue;
                        pos[i] = p;
                        (DistOpt.Eval e, double obj) = Solve(pos);
                        pos[i] = here;
                        if (DistOpt.Better(e, obj, bestE, bestObj, noise)) { bestP = p; bestE = e; bestObj = obj; }
                    }
                    if (bestP < 0) continue;
                    pos[i] = bestP;
                    cur = bestE;
                    curObj = bestObj;
                    moved = true;
                    moves.Add(new VvoMove(sweeps, i < nt ? "tap" : "capacitor", names[i], bestP, curObj, cur.ViolationPu));
                }
            }
            if (!opt.Common.Confirm) break;
            confirmSolves += DistOpt.Confirm(all, pos, counts, Solve, noise, ref cur, ref curObj, out bool improved);
            if (!improved) break;
            confirmImproved++;
        }

        var controls = new VvoControlState[m];
        for (int i = 0; i < m; i++)
            controls[i] = i < nt
                ? new VvoControlState("tap", names[i], start[i], TapAt(tapOltc[i], start[i]), pos[i], TapAt(tapOltc[i], pos[i]))
                : new VvoControlState("capacitor", names[i], start[i], start[i] == 1 ? opt.Capacitors[i - nt].Mvar : 0.0, pos[i], pos[i] == 1 ? opt.Capacitors[i - nt].Mvar : 0.0);
        string msg = status == DistOptStatus.RoundCap
            ? $"stopped at the round cap ({opt.Common.MaxRounds}); a further round may still improve the objective"
            : opt.Common.Confirm
                ? "local discrete optimum: no single step and no neighbour combination improves the objective"
                : "local discrete optimum of the coordinate descent: no single step improves the objective (no neighbour confirmation)";
        return new VvoResult(opt.Objective, controls, moves, baseObj, curObj, baseEval.LossMw, cur.LossMw, baseEval.DemandMw, cur.DemandMw,
            baseEval.ViolationPu, cur.ViolationPu, cur.VminPu, cur.VmaxPu, cur.Voltages, sweeps, confirmSolves, confirmImproved, flows,
            status, notes, msg);
    }

    /// <summary>The tap ratio at grid position <paramref name="k"/>: MinTap + k x StepPu.</summary>
    public static double TapAt(Oltc o, int k)
    {
        ArgumentNullException.ThrowIfNull(o);
        return o.MinTap + k * o.StepPu;
    }
}
