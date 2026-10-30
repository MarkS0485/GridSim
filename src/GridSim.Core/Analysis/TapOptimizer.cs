using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>A recommended tap position for one transformer.</summary>
public sealed record TapSetting(string Transformer, double CurrentTap, double RecommendedTap);

/// <summary>A coordinated tap schedule across the network's OLTC transformers, with the voltage-flatness
/// objective before and after.</summary>
public sealed record TapSchedule(
    IReadOnlyList<TapSetting> Taps, double ObjectiveBefore, double ObjectiveAfter, bool Solved)
{
    /// <summary>True when the recommended schedule flattens the profile better than the current taps.</summary>
    public bool Improved => Solved && ObjectiveAfter < ObjectiveBefore - TapOptimizer.ImprovementEpsilon;

    /// <summary>D-DOC-046 (October 2026): whether the downhill-simplex search met its convergence test (simplex and
    /// objective spread both below their tolerances) rather than stopping at its iteration cap. It used to be ignored, so
    /// a search cut off at the cap was reported exactly like a converged one. True when no search ran (no OLTC, or the
    /// base case did not solve).</summary>
    public bool OptimizerConverged { get; init; } = true;

    /// <summary>D-DOC-046: iterations the search made (0 when none ran).</summary>
    public int OptimizerIterations { get; init; }

    /// <summary>D-DOC-046: each OLTC's own regulation check at the RECOMMENDED taps (the current taps when they are kept):
    /// the voltage of the bus it controls against its own target and deadband. The coordinated schedule minimises the
    /// deviation of every bus from one common target, so it can leave an OLTC's controlled bus outside that OLTC's own
    /// deadband, where its local regulator would move the tap away from the schedule. Empty when no schedule solved.</summary>
    public IReadOnlyList<OltcRegulationCheck> Regulation { get; init; } = Array.Empty<OltcRegulationCheck>();
}

/// <summary>D-DOC-046: one OLTC's own regulation at a tap schedule: the voltage of the bus it controls, against the target
/// and deadband its record gives it.</summary>
public sealed record OltcRegulationCheck(string Transformer, int ControlsBus, double TargetVpu, double DeadbandPu, double VmPu)
{
    /// <summary>True when the controlled bus is inside the OLTC's own band, |Vm - target| &lt;= deadband: its local
    /// regulator would leave the tap where the schedule put it.</summary>
    public bool WithinDeadband => Math.Abs(VmPu - TargetVpu) <= DeadbandPu;
}

/// <summary>
/// Finds a coordinated tap schedule across ALL of a network's OLTC transformers at once, to flatten the
/// voltage profile against a solved state - the multi-transformer optimisation the per-transformer deadband
/// regulator (<see cref="SteadyStateSolver"/>) cannot do, since each of its OLTCs chases only its own local
/// target with no coordination. The objective is the sum of squared voltage deviations from
/// <c>targetVpu</c>; it is minimised over the tap ratios directly by re-solving the real (nonlinear) power
/// flow for each candidate (a derivative-free downhill-simplex search), so there is no linearisation error,
/// then each tap is snapped to its discrete step grid and the schedule re-evaluated. For the handful of GSP
/// transformers a control-room operator coordinates, this is fast and exact; it reuses the power flow, the
/// <see cref="Oltc"/> bounds, and <see cref="NelderMead"/>.
/// </summary>
public static class TapOptimizer
{
    // ---- Named constants (October 2026 review, WS13 R156). Values unchanged.

    /// <summary>A schedule counts as an improvement only when it lowers the objective (sum of squared pu
    /// deviations) by more than this.</summary>
    public const double ImprovementEpsilon = 1e-9;

    /// <summary>Initial Nelder-Mead simplex spread (pu tap ratio), about two 0.0125 pu tap steps.</summary>
    public const double InitialTapStepPu = 0.02;

    /// <summary>Objective returned for a tap vector whose power flow does not converge: far above any real sum
    /// of squared deviations, so the simplex moves away from it.</summary>
    public const double NonConvergedPenalty = 1e9;

    /// <summary>Fixed penalty added when a tap leaves its [MinTap, MaxTap] range, plus
    /// <see cref="TapBoundPenaltyPerPu"/> per pu outside, so the simplex is pushed back inside.</summary>
    public const double TapBoundPenalty = 1e6;

    /// <summary>See <see cref="TapBoundPenalty"/>: the slope of the out-of-range penalty (per pu of tap ratio).</summary>
    public const double TapBoundPenaltyPerPu = 1e6;

    /// <summary>Recommend tap ratios for every OLTC transformer that minimise the bus-voltage deviation from
    /// <paramref name="targetVpu"/>. Taps are solved WITHOUT the OLTC outer loop (they are the variables),
    /// then rounded to each OLTC's step grid.</summary>
    public static TapSchedule Flatten(
        GridModel model,
        double targetVpu = 1.0,
        PowerFlowOptions? options = null,
        NelderMead.Options? optimizer = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        PowerFlowOptions opt = options ?? new PowerFlowOptions();

        // D-DOC-046 (October 2026): every OLTC must name exactly one branch of the model and control a bus the model has.
        // An OLTC whose transformer matched no branch used to be dropped without a word (the schedule then silently left
        // it out), and a name several branches share took the first of them. Refused, naming every problem.
        IReadOnlyList<string> problems = OltcProblems(model);
        if (problems.Count > 0)
            throw new ArgumentException("tap schedule: the case's on-load tap changers do not all resolve: " + string.Join("; ", problems) + ".", nameof(model));

        // Map each OLTC to its branch index + bounds (every name resolves to exactly one branch: checked above).
        var idxByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < model.Branches.Count; i++)
            if (model.Branches[i].Name is { } nm) idxByName.TryAdd(nm, i);

        var controls = new List<(int Branch, Oltc O)>();
        foreach (Oltc o in model.Oltcs)
            if (idxByName.TryGetValue(o.Transformer, out int bi)) controls.Add((bi, o));

        double before = Objective(model, opt, targetVpu, out bool baseSolved, out PowerFlowResult baseResult);
        if (controls.Count == 0 || !baseSolved)
            return new TapSchedule(Array.Empty<TapSetting>(), before, before, baseSolved);

        double[] seed = controls.Select(c => model.Branches[c.Branch].TapRatio <= 0 ? 1.0 : model.Branches[c.Branch].TapRatio).ToArray();

        double Eval(double[] taps)
        {
            GridModel m = WithTaps(model, controls, taps, out double penalty);
            if (penalty > 0) return penalty; // outside [MinTap, MaxTap] - large finite penalty, no solve
            return Objective(m, opt, targetVpu, out _);
        }

        NelderMead.Result r = NelderMead.Minimize(Eval, seed,
            optimizer ?? new NelderMead.Options { InitialStep = InitialTapStepPu }); // ~2 tap steps of initial spread

        // Snap each continuous tap to its OLTC step grid, then re-evaluate the schedule as it would be applied.
        var snapped = new double[controls.Count];
        for (int i = 0; i < controls.Count; i++) snapped[i] = SnapToGrid(r.X[i], controls[i].O);
        GridModel finalModel = WithTaps(model, controls, snapped, out _);
        double after = Objective(finalModel, opt, targetVpu, out bool finalSolved, out PowerFlowResult finalResult);

        // If snapping/optimisation didn't actually help (or didn't solve), recommend the current taps.
        if (!finalSolved || after >= before)
        {
            var keep = controls.Select(c => new TapSetting(c.O.Transformer, model.Branches[c.Branch].TapRatio, model.Branches[c.Branch].TapRatio)).ToList();
            return new TapSchedule(keep, before, before, baseSolved)
            {
                OptimizerConverged = r.Converged, OptimizerIterations = r.Iterations, Regulation = RegulationAt(controls, baseResult),
            };
        }

        var taps = new List<TapSetting>(controls.Count);
        for (int i = 0; i < controls.Count; i++)
            taps.Add(new TapSetting(controls[i].O.Transformer, model.Branches[controls[i].Branch].TapRatio, snapped[i]));
        return new TapSchedule(taps, before, after, true)
        {
            OptimizerConverged = r.Converged, OptimizerIterations = r.Iterations, Regulation = RegulationAt(controls, finalResult),
        };
    }

    /// <summary>
    /// D-DOC-046 (October 2026): what is wrong with the model's OLTC records, one plain-English line each, in record
    /// order; empty when every record resolves. An OLTC must name exactly one branch (ordinal comparison, as the schedule
    /// looks names up) and control a bus the model has.
    /// </summary>
    public static IReadOnlyList<string> OltcProblems(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Branch b in model.Branches)
            if (b.Name is { } nm) count[nm] = count.TryGetValue(nm, out int c) ? c + 1 : 1;
        var buses = new HashSet<int>(model.Buses.Select(b => b.Id));
        var problems = new List<string>();
        foreach (Oltc o in model.Oltcs)
        {
            int n = o.Transformer is null ? 0 : count.GetValueOrDefault(o.Transformer);
            if (n == 0) problems.Add($"OLTC '{o.Transformer}' names no branch of the case");
            else if (n > 1) problems.Add($"OLTC '{o.Transformer}' names {n} branches (a tap changer must name exactly one)");
            if (!buses.Contains(o.ControlsBus)) problems.Add($"OLTC '{o.Transformer}' controls bus {o.ControlsBus}, which the case does not have");
        }
        return problems;
    }

    // D-DOC-046: each OLTC's controlled-bus voltage in a solved state, against its own target and deadband.
    private static IReadOnlyList<OltcRegulationCheck> RegulationAt(List<(int Branch, Oltc O)> controls, PowerFlowResult solved)
    {
        var at = new Dictionary<int, int>();
        for (int i = 0; i < solved.BusIds.Count; i++) at.TryAdd(solved.BusIds[i], i);
        var list = new List<OltcRegulationCheck>(controls.Count);
        foreach ((_, Oltc o) in controls)
            list.Add(new OltcRegulationCheck(o.Transformer, o.ControlsBus, o.TargetVpu, o.DeadbandPu,
                at.TryGetValue(o.ControlsBus, out int k) ? solved.Vm[k] : double.NaN));
        return list;
    }

    // Sum of squared bus-voltage deviations from the target over a solved model (large if it fails to solve).
    private static double Objective(GridModel model, PowerFlowOptions opt, double target, out bool solved)
        => Objective(model, opt, target, out solved, out _);

    // The same, also handing back the solved state (D-DOC-046: the regulation check reads its voltages). The objective
    // is computed by exactly the same statements, so its value is the same bits.
    private static double Objective(GridModel model, PowerFlowOptions opt, double target, out bool solved, out PowerFlowResult r)
    {
        // Fixed taps: no OLTC loop. Shared dense/sparse rule (was always dense, once per Nelder-Mead evaluation; R004).
        r = SolverSelector.CreateNewton(model).Solve(model, opt);
        solved = r.Converged;
        if (!solved) return NonConvergedPenalty;
        double s = 0;
        for (int i = 0; i < r.Vm.Length; i++) { double d = r.Vm[i] - target; s += d * d; }
        return s;
    }

    // A copy of the model with the control branches' TapRatio set; penalty > 0 if any tap is out of bounds.
    private static GridModel WithTaps(GridModel model, List<(int Branch, Oltc O)> controls, double[] taps, out double penalty)
    {
        penalty = 0;
        var branches = model.Branches.ToArray();
        for (int i = 0; i < controls.Count; i++)
        {
            Oltc o = controls[i].O;
            double t = taps[i];
            if (t < o.MinTap) penalty += TapBoundPenalty + TapBoundPenaltyPerPu * (o.MinTap - t);
            else if (t > o.MaxTap) penalty += TapBoundPenalty + TapBoundPenaltyPerPu * (t - o.MaxTap);
            branches[controls[i].Branch] = branches[controls[i].Branch] with { TapRatio = t };
        }
        // N094 (October 2026): through GridModel.CopyWith, which carries the out-of-service generators (F16) and every
        // other model property. The hand-built copy kept only the in-service list, so a writer given this model lost
        // the out-of-service units. Engines read Generators, which is the same list as before, so no number changes.
        return model.CopyWith(branches: branches);
    }

    private static double SnapToGrid(double tap, Oltc o)
    {
        double step = o.StepPu > 0 ? o.StepPu : Oltc.DefaultStepPu;
        double snapped = o.MinTap + Math.Round((tap - o.MinTap) / step) * step;
        return Math.Clamp(snapped, o.MinTap, o.MaxTap);
    }
}
