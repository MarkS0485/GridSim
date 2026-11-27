using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.DistributionOptimisation;

/// <summary>A candidate bus for a shunt capacitor bank and the bank sizes it may take (Mvar, ascending, the first 0).</summary>
public sealed record CapacitorCandidate(int BusId, IReadOnlyList<double> StepsMvar);

/// <summary>The settings of <see cref="CapacitorPlacement.Run"/>.</summary>
public sealed record CapPlaceOptions
{
    /// <summary>The candidate buses with their bank sizes, in search order; null for every PQ bus (model order) with
    /// <see cref="DefaultStepsMvar"/>.</summary>
    public IReadOnlyList<CapacitorCandidate>? Candidates { get; init; }

    /// <summary>The bank sizes (Mvar, ascending, the first 0) of every PQ bus when <see cref="Candidates"/> is null.</summary>
    public IReadOnlyList<double>? DefaultStepsMvar { get; init; }

    /// <summary>The fixed cost of each installed bank (a bank above 0 Mvar), in the money unit of the energy value.</summary>
    public double FixedCostPerBank { get; init; }

    /// <summary>The cost per installed Mvar.</summary>
    public double CostPerMvar { get; init; }

    /// <summary>The value of lost energy, money per MWh (default 1).</summary>
    public double EnergyCostPerMwh { get; init; } = 1.0;

    /// <summary>The hours per year the snapshot losses last (default 8760).</summary>
    public double HoursPerYear { get; init; } = 8760.0;

    /// <summary>The voltage band, power flow and confirmation settings.</summary>
    public DistOptCommon Common { get; init; } = new();
}

/// <summary>One greedy step: in round <paramref name="Round"/> the bank at bus <paramref name="BusId"/> went to
/// <paramref name="SizeMvar"/>, giving the objective and losses shown.</summary>
public sealed record CapPlaceStep(int Round, int BusId, double SizeMvar, double Objective, double LossMw);

/// <summary>A candidate's chosen bank: its step index and size (0 Mvar = no bank).</summary>
public sealed record CapPlaceBank(int BusId, int Step, double SizeMvar);

/// <summary>The result of <see cref="CapacitorPlacement.Run"/>.</summary>
/// <param name="Banks">Every candidate's chosen bank, in candidate order.</param>
/// <param name="Steps">The greedy steps in order.</param>
/// <param name="ConfirmationSolves">Power flows made by the neighbour confirmations.</param>
/// <param name="ConfirmationImprovements">Confirmations that found a better combination than the greedy search.</param>
/// <param name="BaseLossMw">Losses with no bank added.</param>
/// <param name="BaseObjective">The objective with no bank added.</param>
/// <param name="LossMw">Losses at the result.</param>
/// <param name="LossCostPerYear">Losses x energy value x hours per year.</param>
/// <param name="CapitalCost">Fixed cost per bank plus cost per Mvar, over the installed banks.</param>
/// <param name="Objective">The annual loss cost plus the capital cost.</param>
/// <param name="VminPu">The lowest bus voltage at the result.</param>
/// <param name="VmaxPu">The highest bus voltage at the result.</param>
/// <param name="ViolationPu">The total voltage violation at the result (0 when every bus is in its band).</param>
/// <param name="Voltages">The bus voltages at the result, in model order.</param>
/// <param name="PowerFlows">Every power flow made, the start included.</param>
/// <param name="Rounds">Greedy-plus-confirmation rounds made.</param>
/// <param name="Status">How the search ended.</param>
/// <param name="Message">A plain sentence saying how it ended.</param>
/// <param name="UnconfirmedBanks">The buses holding a bank that the last neighbour confirmation did not cover, in candidate
/// order: empty unless more than <see cref="DistOpt.MaxConfirmControls"/> buses hold a bank (the confirmation then covers the
/// <see cref="DistOpt.MaxConfirmControls"/> banks the greedy search changed last).</param>
public sealed record CapPlaceResult(
    IReadOnlyList<CapPlaceBank> Banks, IReadOnlyList<CapPlaceStep> Steps, int ConfirmationSolves, int ConfirmationImprovements,
    double BaseLossMw, double BaseObjective, double LossMw, double LossCostPerYear, double CapitalCost, double Objective,
    double VminPu, double VmaxPu, double ViolationPu, IReadOnlyList<DistOptBusVoltage> Voltages, int PowerFlows, int Rounds,
    DistOptStatus Status, string Message, IReadOnlyList<int> UnconfirmedBanks)
{
    /// <summary>True when every bus is within its voltage band at the result.</summary>
    public bool Feasible => ViolationPu <= DistOpt.ViolationTiePu;
}

/// <summary>
/// Shunt capacitor placement and sizing on a balanced case, for loss reduction against the cost of the banks.
/// <para>Objective (after Grainger and Lee, "Optimum size and location of shunt capacitors for reduction of losses on
/// distribution feeders", IEEE Trans. PAS-100(3), 1981): annual loss cost (losses x energy value x hours per year) plus
/// capital cost (a fixed cost per installed bank plus a cost per Mvar). A bank is a constant admittance: its size is added
/// to the bus's <see cref="Bus.Bs"/> (Mvar injected at 1 pu, positive for a capacitor, the GridSim shunt convention).</para>
/// <para>Method, a heuristic (the greedy search by marginal benefit is common practice, not a global optimum):</para>
/// <list type="number">
/// <item>Greedy: each round tries every candidate (in candidate order) at its next size with a full power flow and keeps
/// the best trial (<see cref="DistOpt"/> comparison: voltage violation first, then the objective; ties to the lower
/// candidate). It stops when no trial improves.</item>
/// <item>Confirmation: every combination of one step down, no change or one step up over the buses that hold a bank (3^m
/// power flows). When more than <see cref="DistOpt.MaxConfirmControls"/> buses hold a bank, the confirmation covers the
/// <see cref="DistOpt.MaxConfirmControls"/> whose banks the greedy search changed last (a fixed, deterministic choice) and
/// the result names the others (<see cref="CapPlaceResult.UnconfirmedBanks"/>). If a combination improves, the
/// greedy search runs again from it, then another confirmation, until a confirmation finds nothing better (or the round
/// cap). Each round strictly improves the objective, so it ends.</item>
/// </list>
/// <para>Ties: the objective tie is the solver noise (<see cref="DistOpt.SolverNoiseMw"/>) times the energy value times the
/// hours per year, plus the relative part (<see cref="DistOpt.ObjectiveTie"/>); the capital cost is exact.</para>
/// <para>Snapshot only: the losses are those of the case as given, held for the hours per year. A case with an OLTC or a
/// transformer flow control is refused (<see cref="DistOpt"/>: the trial solves do not regulate them).</para>
/// </summary>
public static class CapacitorPlacement
{
    /// <summary>Run the placement on <paramref name="model"/> (which is not changed). Throws <see cref="ArgumentException"/>
    /// for a bad candidate or option or a case with an OLTC or a transformer flow control, and
    /// <see cref="InvalidDataException"/> when the starting case does not solve.</summary>
    public static CapPlaceResult Run(GridModel model, CapPlaceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        CapPlaceOptions opt = options ?? new CapPlaceOptions();
        opt.Common.Validate(nameof(options));
        foreach ((string name, double v) in new[] { ("fixed cost per bank", opt.FixedCostPerBank), ("cost per Mvar", opt.CostPerMvar), ("energy value", opt.EnergyCostPerMwh), ("hours per year", opt.HoursPerYear) })
            if (!double.IsFinite(v) || v < 0)
                throw new ArgumentException($"Capacitor placement: the {name} must be a finite number of 0 or more (got {v.ToString("R", CultureInfo.InvariantCulture)}).", nameof(options));
        DistOpt.RefuseUnregulated(model, Array.Empty<string>(), "Capacitor placement", "Remove those control records from the case (each transformer then keeps its case tap) to place capacitors with the taps fixed.", nameof(model));

        Dictionary<int, int> busIndex = DistOpt.BusIndex(model);
        IReadOnlyList<CapacitorCandidate> cands = opt.Candidates ?? DefaultCandidates(model, opt.DefaultStepsMvar);
        if (cands.Count == 0) throw new ArgumentException("Capacitor placement: there is no candidate bus.", nameof(options));
        var seen = new HashSet<int>();
        int n = cands.Count;
        var busOf = new int[n];
        var counts = new int[n];
        for (int i = 0; i < n; i++)
        {
            CapacitorCandidate c = cands[i];
            if (!busIndex.TryGetValue(c.BusId, out int k)) throw new ArgumentException($"Capacitor placement: candidate bus {c.BusId} is not a bus of the case.", nameof(options));
            if (!seen.Add(c.BusId)) throw new ArgumentException($"Capacitor placement: candidate bus {c.BusId} is listed twice.", nameof(options));
            CheckSteps(c.StepsMvar, $"candidate bus {c.BusId}");
            busOf[i] = k;
            counts[i] = c.StepsMvar.Count;
        }

        var pos = new int[n];
        var lastStep = new int[n];   // the greedy step that last changed each candidate (0: none)
        double noise = DistOpt.SolverNoiseMw(model, opt.Common) * opt.EnergyCostPerMwh * opt.HoursPerYear;
        int flows = 0;
        (DistOpt.Eval E, double Obj) Solve(int[] p)
        {
            var add = new double[model.Buses.Count];
            for (int i = 0; i < n; i++) add[busOf[i]] = cands[i].StepsMvar[p[i]];
            DistOpt.Eval e = DistOpt.Evaluate(model.CopyWith(buses: DistOpt.WithShunts(model, add)), opt.Common);
            flows++;
            return (e, e.Ok ? Objective(p, e.LossMw) : double.PositiveInfinity);
        }
        double Capital(int[] p)
        {
            double cap = 0;
            for (int i = 0; i < n; i++)
            {
                double s = cands[i].StepsMvar[p[i]];
                if (s > 0) cap += opt.FixedCostPerBank + opt.CostPerMvar * s;
            }
            return cap;
        }
        double Objective(int[] p, double lossMw) => lossMw * opt.EnergyCostPerMwh * opt.HoursPerYear + Capital(p);

        (DistOpt.Eval cur, double curObj) = Solve(pos);
        if (!cur.Ok) throw new InvalidDataException("Capacitor placement: the case does not solve with no bank added.");
        DistOpt.Eval baseEval = cur;
        double baseObj = curObj;

        var steps = new List<CapPlaceStep>();
        var unconfirmed = new List<int>();
        int confirmSolves = 0, confirmImproved = 0, rounds = 0, greedyRound = 0;
        DistOptStatus status = DistOptStatus.LocalOptimum;
        while (true)
        {
            if (rounds >= opt.Common.MaxRounds) { status = DistOptStatus.RoundCap; break; }
            rounds++;
            // Greedy by marginal benefit.
            while (true)
            {
                int bestI = -1;
                DistOpt.Eval bestE = default;
                double bestObj = double.PositiveInfinity;
                for (int i = 0; i < n; i++)
                {
                    if (pos[i] + 1 >= counts[i]) continue;
                    pos[i]++;
                    (DistOpt.Eval e, double obj) = Solve(pos);
                    pos[i]--;
                    bool beatsCurrent = DistOpt.Better(e, obj, cur, curObj, noise);
                    if (beatsCurrent && (bestI < 0 || DistOpt.Better(e, obj, bestE, bestObj, noise))) { bestI = i; bestE = e; bestObj = obj; }
                }
                if (bestI < 0) break;
                pos[bestI]++;
                cur = bestE;
                curObj = bestObj;
                steps.Add(new CapPlaceStep(++greedyRound, cands[bestI].BusId, cands[bestI].StepsMvar[pos[bestI]], curObj, cur.LossMw));
                lastStep[bestI] = greedyRound;
            }
            if (!opt.Common.Confirm) break;
            var chosen = new List<int>();
            for (int i = 0; i < n; i++) if (pos[i] > 0) chosen.Add(i);
            unconfirmed = new List<int>();
            if (chosen.Count > DistOpt.MaxConfirmControls)
            {
                // Bounded confirmation: the banks the greedy search changed last (every bank was placed by a greedy step, and
                // each step number is used once, so the choice is unique), kept in candidate order.
                var recent = new HashSet<int>(chosen.OrderByDescending(i => lastStep[i]).Take(DistOpt.MaxConfirmControls));
                var kept = new List<int>(DistOpt.MaxConfirmControls);
                foreach (int i in chosen)
                {
                    if (recent.Contains(i)) kept.Add(i);
                    else unconfirmed.Add(cands[i].BusId);
                }
                chosen = kept;
            }
            confirmSolves += DistOpt.Confirm(chosen, pos, counts, Solve, noise, ref cur, ref curObj, out bool improved);
            if (!improved) break;
            confirmImproved++;
        }

        var banks = new CapPlaceBank[n];
        for (int i = 0; i < n; i++) banks[i] = new CapPlaceBank(cands[i].BusId, pos[i], cands[i].StepsMvar[pos[i]]);
        double capital = Capital(pos);
        string msg = status == DistOptStatus.RoundCap
            ? $"stopped at the round cap ({opt.Common.MaxRounds}); a further round may still improve the objective (heuristic search)"
            : opt.Common.Confirm
                ? "no greedy step and no neighbour combination improves the objective (heuristic search: a local optimum, not a guaranteed global one)"
                : "no greedy step improves the objective (heuristic search, no neighbour confirmation)";
        if (unconfirmed.Count > 0)
            msg += $"; the neighbour confirmation covered the {DistOpt.MaxConfirmControls} banks the greedy search changed last, not the banks at bus(es) {string.Join(", ", unconfirmed.Select(b => b.ToString(CultureInfo.InvariantCulture)))}";
        return new CapPlaceResult(banks, steps, confirmSolves, confirmImproved, baseEval.LossMw, baseObj, cur.LossMw,
            cur.LossMw * opt.EnergyCostPerMwh * opt.HoursPerYear, capital, curObj, cur.VminPu, cur.VmaxPu, cur.ViolationPu,
            cur.Voltages, flows, rounds, status, msg, unconfirmed);
    }

    private static List<CapacitorCandidate> DefaultCandidates(GridModel model, IReadOnlyList<double>? steps)
    {
        if (steps is null) throw new ArgumentException("Capacitor placement: give the candidate buses, or the bank sizes every PQ bus may take.", nameof(steps));
        var list = new List<CapacitorCandidate>();
        foreach (Bus b in model.Buses)
            if (b.Type == BusType.Pq) list.Add(new CapacitorCandidate(b.Id, steps));
        return list;
    }

    private static void CheckSteps(IReadOnlyList<double>? steps, string where)
    {
        if (steps is null || steps.Count < 2) throw new ArgumentException($"Capacitor placement: {where} needs at least two bank sizes (0 and one more).", nameof(steps));
        if (steps[0] != 0.0) throw new ArgumentException($"Capacitor placement: {where}: the first bank size must be 0 Mvar (no bank).", nameof(steps));
        for (int s = 1; s < steps.Count; s++)
            if (!double.IsFinite(steps[s]) || !(steps[s] > steps[s - 1]))
                throw new ArgumentException($"Capacitor placement: {where}: the bank sizes must be finite and strictly ascending (size {s}).", nameof(steps));
    }
}
