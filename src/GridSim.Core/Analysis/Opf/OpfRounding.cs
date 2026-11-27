using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Opf;

/// <summary>How <see cref="OpfRounding.Solve(GridModel, AcOpfExtendedOptions, OpfRoundingMode)"/> ended.</summary>
public enum OpfRoundingStatus
{
    /// <summary>There were no controls to round; the continuous optimum is the answer.</summary>
    NoControls,
    /// <summary>The continuous optimum did not converge; nothing was rounded.</summary>
    ContinuousNotConverged,
    /// <summary>Every control rounded to its nearest grid point, and the re-solve converged within every limit.</summary>
    Rounded,
    /// <summary>The rounded point failed, and flipping one control to its other neighbour gave a feasible re-solve.</summary>
    Flipped,
    /// <summary>Neither the rounded point nor any single flip gave a feasible re-solve; only the continuous optimum is
    /// reported.</summary>
    RoundedPointInfeasible,
    /// <summary>Neighbours mode: the best feasible combination of the floor/ceil neighbours of the continuous optimum.</summary>
    BestNeighbour,
    /// <summary>Neighbours mode: no combination of the floor/ceil neighbours gave a feasible re-solve; only the continuous
    /// optimum is reported.</summary>
    NoNeighbourFeasible,
}

/// <summary>How <see cref="OpfRounding.Solve(GridModel, AcOpfExtendedOptions, OpfRoundingMode)"/> rounds.</summary>
public enum OpfRoundingMode
{
    /// <summary>Round each control to its nearest grid point, then flip one control at a time if that fails (the
    /// default).</summary>
    Flip,
    /// <summary>Solve every floor/ceil combination of the discrete controls (at most
    /// <see cref="OpfRounding.MaxNeighbourControls"/>) and keep the best feasible one.</summary>
    Neighbours,
}

/// <summary>One control's rounding: the continuous optimum, its grid step count and value, and the other neighbour
/// (NaN when there is none inside the range).</summary>
public sealed record OpfRoundedControl(string Kind, string Element, double Continuous, int Step, double Rounded, double OtherNeighbour);

/// <summary>The result of <see cref="OpfRounding.Solve(GridModel, AcOpfExtendedOptions, OpfRoundingMode)"/>: the continuous optimum, the rounding of each control, and the
/// re-solve with the controls fixed (null unless <see cref="Status"/> is Rounded or Flipped).
/// <see cref="FlippedControl"/> is the index of the control flipped (-1 when none).</summary>
public sealed record OpfRoundingResult(
    OpfRoundingStatus Status, AcOpfExtendedResult Continuous, IReadOnlyList<OpfRoundedControl> Controls,
    IReadOnlyList<double> FinalValues, AcOpfExtendedResult? Final, int FlippedControl, string Message)
{
    /// <summary>The rounding mode that produced this result.</summary>
    public OpfRoundingMode Mode { get; init; } = OpfRoundingMode.Flip;

    /// <summary>Neighbours mode: the number of fixed-control re-solves (2^m, m the controls with two neighbours); 0
    /// otherwise.</summary>
    public int Combinations { get; init; }

    /// <summary>Neighbours mode: the index of the chosen combination (bit q set when the q-th control with two neighbours
    /// took its upper neighbour); -1 otherwise.</summary>
    public int Combination { get; init; } = -1;
}

/// <summary>
/// The discrete rounding rule of the AC-OPF controls (a stated heuristic, not a mixed-integer optimum). After the
/// continuous optimum, each control in variable order is mapped to its grid (taps tau = MinTap + n StepPu, shunts
/// B = BMin + n StepMvar) and rounded to the nearest point; a tie (|frac - 0.5| &lt;= 1e-9) goes to the point nearer
/// the case value, then to the lower one. All controls are then fixed and the same objective is solved again. If that
/// does not converge or breaks a limit (by more than 1e-6 pu), the controls are flipped one at a time, in variable order,
/// to their other neighbour (the rest stay rounded), and the first flip that converges within every limit is kept. If
/// none does, the result says "rounded point infeasible" and carries the continuous optimum only. Nothing further is
/// searched. A phase-shifter angle with a step of 0 is continuous: it is fixed at its continuous optimum, not rounded.
/// <para>Neighbours mode (<see cref="OpfRoundingMode.Neighbours"/>, opt-in): every discrete control has its floor and ceil
/// grid points around the continuous optimum (one point when the optimum is on the grid within the tie band, or at the end
/// of the range). Every combination is solved with the controls fixed, 2^m solves for the m controls with two points,
/// taken in Gray-code order (successive combinations differ in one control). Each solve starts from the interior point
/// method's own starting point, so each result is the same as a direct fixed-control solve of that combination,
/// whatever the order. The best feasible objective is kept; a tie goes to the lowest combination index (bit q set when the
/// q-th two-point control is at its ceil). More than <see cref="MaxNeighbourControls"/> discrete controls are refused.
/// This is optimal over the floor/ceil neighbours of the continuous optimum. It is not a global mixed-integer
/// optimum.</para>
/// </summary>
public static class OpfRounding
{
    /// <summary>The tie band on the fractional grid position.</summary>
    public const double TieBand = 1e-9;

    /// <summary>A limit broken by more than this (pu) makes a re-solve infeasible.</summary>
    public const double LimitTolerancePu = 1e-6;

    /// <summary>The most discrete controls neighbours mode takes (2^12 = 4096 re-solves).</summary>
    public const int MaxNeighbourControls = 12;

    /// <summary>The honest label of neighbours mode.</summary>
    public const string NeighboursLabel = "optimal over the floor/ceil neighbours of the continuous optimum. This is not a global mixed-integer optimum.";

    // The grid of control j in variable order: (min, max, step); a phase shifter's in degrees (step 0: continuous).
    private static (double Min, double Max, double Step) Grid(OpfControlSet set, int j)
    {
        if (j < set.Taps.Count) return (set.Taps[j].MinTap, set.Taps[j].MaxTap, set.Taps[j].StepPu);
        int s = j - set.Taps.Count;
        if (s < set.Shunts.Count) return (set.Shunts[s].BMinMvar, set.Shunts[s].BMaxMvar, set.Shunts[s].StepMvar);
        OpfPhaseShiftControl p = set.PhaseShifts[s - set.Shunts.Count];
        return (p.MinDeg, p.MaxDeg, p.StepDeg);
    }

    /// <summary>The number of discrete controls (every tap and shunt, and every phase shifter with a step above 0).</summary>
    public static int DiscreteCount(OpfControlSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        int n = set.Taps.Count + set.Shunts.Count;
        foreach (OpfPhaseShiftControl p in set.PhaseShifts) if (p.StepDeg > 0) n++;
        return n;
    }

    /// <summary>Solve the continuous AC-OPF, then round its controls in <paramref name="mode"/>. Flip is
    /// <see cref="Solve(GridModel, AcOpfExtendedOptions)"/> exactly.</summary>
    public static OpfRoundingResult Solve(GridModel model, AcOpfExtendedOptions options, OpfRoundingMode mode)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (mode == OpfRoundingMode.Flip) return Solve(model, options);
        if (mode != OpfRoundingMode.Neighbours) throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown rounding mode.");
        OpfControlSet set = options.Controls ?? OpfControlSet.None;
        int discrete = DiscreteCount(set);
        if (discrete > MaxNeighbourControls)
            throw new InvalidDataException(
                $"OPF rounding: neighbours mode solves every floor/ceil combination, 2^n re-solves for n discrete controls; {discrete} controls would need {1L << Math.Min(discrete, 62)}, above the limit of {MaxNeighbourControls} controls ({1 << MaxNeighbourControls} re-solves). Use the flip rounding, or fewer controls.");

        AcOpfExtendedResult cont = AcOpfExtended.Solve(model, options with { FixedControlValues = null });
        if (set.Count == 0)
            return new OpfRoundingResult(OpfRoundingStatus.NoControls, cont, Array.Empty<OpfRoundedControl>(), Array.Empty<double>(), cont, -1, "no controls to round")
            { Mode = mode };
        if (!cont.Converged)
            return new OpfRoundingResult(OpfRoundingStatus.ContinuousNotConverged, cont, Array.Empty<OpfRoundedControl>(), Array.Empty<double>(), null, -1,
                "the continuous optimum did not converge, so nothing was rounded") { Mode = mode };

        int n = set.Count;
        var lo = new double[n];
        var hi = new double[n];
        var loStep = new int[n];
        var hiStep = new int[n];
        var two = new List<int>();          // the controls with two points, in variable order
        for (int j = 0; j < n; j++)
        {
            OpfControlValue cv = cont.Controls[j];
            (double min, double max, double step) = Grid(set, j);
            if (step == 0) { lo[j] = hi[j] = cv.Value; loStep[j] = hiStep[j] = 0; continue; }
            int last = (int)Math.Floor((max - min) / step + TieBand);
            double pos = (cv.Value - min) / step;
            double near = Math.Round(pos, MidpointRounding.ToEven);
            int a, b;
            if (Math.Abs(pos - near) <= TieBand) { a = b = (int)near; }
            else { a = (int)Math.Floor(pos); b = a + 1; }
            a = Math.Clamp(a, 0, last);
            b = Math.Clamp(b, 0, last);
            loStep[j] = a; hiStep[j] = b;
            lo[j] = min + a * step;
            hi[j] = min + b * step;
            if (a != b) two.Add(j);
        }

        int m = two.Count;
        int combos = 1 << m;
        AcOpfExtendedResult? best = null;
        double[]? bestValues = null;
        int bestCode = -1;
        for (int i = 0; i < combos; i++)
        {
            int code = i ^ (i >> 1);   // Gray code: one control changes from the previous combination
            var values = (double[])lo.Clone();
            for (int q = 0; q < m; q++)
                if (((code >> q) & 1) == 1) values[two[q]] = hi[two[q]];
            AcOpfExtendedResult r = AcOpfExtended.Solve(model, options with { FixedControlValues = values });
            if (!Feasible(model, r)) continue;
            double obj = r.Base.ObjectivePerHour;
            if (best is null || obj < best.Base.ObjectivePerHour || (obj == best.Base.ObjectivePerHour && code < bestCode))
            {
                best = r; bestValues = values; bestCode = code;
            }
        }

        var info = new List<OpfRoundedControl>(n);
        for (int j = 0; j < n; j++)
        {
            OpfControlValue cv = cont.Controls[j];
            int q = two.IndexOf(j);
            bool upper = bestCode >= 0 && q >= 0 && ((bestCode >> q) & 1) == 1;
            double chosen = upper ? hi[j] : lo[j], other = upper ? lo[j] : hi[j];
            int stepIndex = upper ? hiStep[j] : loStep[j];
            info.Add(new OpfRoundedControl(cv.Kind, cv.Element, cv.Value, stepIndex, chosen, q >= 0 ? other : double.NaN));
        }
        if (best is null)
            return new OpfRoundingResult(OpfRoundingStatus.NoNeighbourFeasible, cont, info, lo, null, -1,
                $"no combination of the floor/ceil neighbours converges within every limit ({combos} re-solve(s)); the continuous optimum is reported")
            { Mode = mode, Combinations = combos };
        return new OpfRoundingResult(OpfRoundingStatus.BestNeighbour, cont, info, bestValues!, best, -1,
            $"best of {combos} floor/ceil combination(s) (combination {bestCode}): " + NeighboursLabel)
        { Mode = mode, Combinations = combos, Combination = bestCode };
    }

    /// <summary>Solve the continuous AC-OPF of <paramref name="options"/>, then round its controls by the rule above.</summary>
    public static OpfRoundingResult Solve(GridModel model, AcOpfExtendedOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        AcOpfExtendedResult cont = AcOpfExtended.Solve(model, options with { FixedControlValues = null });
        OpfControlSet set = options.Controls ?? OpfControlSet.None;
        if (set.Count == 0)
            return new OpfRoundingResult(OpfRoundingStatus.NoControls, cont, Array.Empty<OpfRoundedControl>(), Array.Empty<double>(), cont, -1, "no controls to round");
        if (!cont.Converged)
            return new OpfRoundingResult(OpfRoundingStatus.ContinuousNotConverged, cont, Array.Empty<OpfRoundedControl>(), Array.Empty<double>(), null, -1,
                "the continuous optimum did not converge, so nothing was rounded");

        int n = set.Count;
        var rounded = new double[n];
        var other = new double[n];
        var info = new List<OpfRoundedControl>(n);
        for (int j = 0; j < n; j++)
        {
            OpfControlValue cv = cont.Controls[j];
            (double min, double max, double step) = Grid(set, j);
            if (step == 0)
            {
                // A continuous phase-shifter angle: held at its continuous optimum, never flipped.
                rounded[j] = cv.Value;
                other[j] = double.NaN;
                info.Add(new OpfRoundedControl(cv.Kind, cv.Element, cv.Value, 0, cv.Value, double.NaN));
                continue;
            }
            (int k, int alt) = Nearest(cv.Value, cv.CaseValue, min, max, step);
            rounded[j] = min + k * step;
            other[j] = alt < 0 ? double.NaN : min + alt * step;
            info.Add(new OpfRoundedControl(cv.Kind, cv.Element, cv.Value, k, rounded[j], other[j]));
        }

        AcOpfExtendedResult first = AcOpfExtended.Solve(model, options with { FixedControlValues = rounded });
        if (Feasible(model, first))
            return new OpfRoundingResult(OpfRoundingStatus.Rounded, cont, info, rounded, first, -1, "every control at its nearest grid point");
        for (int j = 0; j < n; j++)
        {
            if (double.IsNaN(other[j])) continue;
            var trial = (double[])rounded.Clone();
            trial[j] = other[j];
            AcOpfExtendedResult r = AcOpfExtended.Solve(model, options with { FixedControlValues = trial });
            if (Feasible(model, r))
                return new OpfRoundingResult(OpfRoundingStatus.Flipped, cont, info, trial, r, j,
                    $"the rounded point {(first.Converged ? "breaks a limit" : "does not converge")}; {info[j].Kind} {info[j].Element} flipped to its other neighbour");
        }
        return new OpfRoundingResult(OpfRoundingStatus.RoundedPointInfeasible, cont, info, rounded, null, -1,
            "rounded point infeasible: neither the rounded point nor any single flip converges within every limit; the continuous optimum is reported");
    }

    /// <summary>The nearest grid step to <paramref name="v"/> on min + n step (n in 0 .. the last point inside max), and
    /// the other neighbour (-1 when there is none in range). A tie goes to the point nearer <paramref name="caseValue"/>,
    /// then to the lower.</summary>
    internal static (int Nearest, int Other) Nearest(double v, double caseValue, double min, double max, double step)
    {
        int last = (int)Math.Floor((max - min) / step + TieBand);
        double pos = (v - min) / step;
        int lo = (int)Math.Floor(pos);
        double frac = pos - lo;
        int pick;
        if (Math.Abs(frac - 0.5) <= TieBand)
        {
            double dLo = Math.Abs(min + lo * step - caseValue), dHi = Math.Abs(min + (lo + 1) * step - caseValue);
            pick = dHi < dLo ? lo + 1 : lo;
        }
        else pick = frac < 0.5 ? lo : lo + 1;
        pick = Math.Clamp(pick, 0, last);
        // The other neighbour is the other end of the bracketing pair; at the end of the range it is the point inside.
        int alt = pick == lo ? lo + 1 : lo;
        if (alt > last) alt = pick - 1;
        if (alt < 0) alt = pick + 1;
        if (alt < 0 || alt > last || alt == pick) alt = -1;
        return (pick, alt);
    }

    /// <summary>True when <paramref name="r"/> converged and every voltage, unit and rated branch is within its limits
    /// to <see cref="LimitTolerancePu"/>.</summary>
    internal static bool Feasible(GridModel model, AcOpfExtendedResult r)
    {
        if (!r.Converged) return false;
        double tol = LimitTolerancePu, mva = tol * model.BaseMva;
        for (int i = 0; i < model.BusCount; i++)
        {
            Bus b = model.Buses[i];
            double lo = b.VminPu ?? VoltageLimits.OperationalLowPu, hi = b.VmaxPu ?? VoltageLimits.OperationalHighPu;
            double vm = r.Base.Buses[i].VmPu;
            if (vm < lo - tol || vm > hi + tol) return false;
        }
        for (int g = 0; g < model.Generators.Count; g++)
        {
            Generator gen = model.Generators[g];
            AcOpfGenResult res = r.Base.Generators[g];
            if (res.PgMw < gen.Pmin - mva || res.PgMw > gen.Pmax + mva || res.QgMvar < gen.Qmin - mva || res.QgMvar > gen.Qmax + mva) return false;
        }
        foreach (AcOpfBranchResult br in r.Base.Branches)
        {
            if (!(br.RateMva > 0)) continue;
            double sf = Math.Sqrt(br.PfMw * br.PfMw + br.QfMvar * br.QfMvar), st = Math.Sqrt(br.PtMw * br.PtMw + br.QtMvar * br.QtMvar);
            if (sf > br.RateMva + mva || st > br.RateMva + mva) return false;
        }
        return true;
    }
}
