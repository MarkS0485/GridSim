using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>One converged point of a PV curve: the load multiplier and every bus voltage magnitude (pu) there, aligned
/// with <see cref="PvCurve.BusIds"/>.</summary>
public sealed record PvPoint(double Lambda, double[] Vm);

/// <summary>The PV curves of a continuation (F18): the largest converged multiplier and every converged point.</summary>
/// <param name="MaxLambda">Exactly <see cref="ContinuationPowerFlow.MaxLoadability"/> for the same arguments (0 when even
/// the base case did not converge).</param>
/// <param name="BusIds">Bus ids, in the order of each point's <see cref="PvPoint.Vm"/>.</param>
/// <param name="Points">The converged points, in the order solved (strictly rising multipliers).</param>
public sealed record PvCurve(double MaxLambda, IReadOnlyList<int> BusIds, IReadOnlyList<PvPoint> Points)
{
    /// <summary>D-DOC-129 (October 2026): how the walk behind <see cref="MaxLambda"/> ended (bracketed to the minimum
    /// step, or stopped by the solve cap). Set by the study overload of TracePvCurve; null from the others.</summary>
    public LoadabilityWalkEnd? WalkEnd { get; init; }
}

/// <summary>
/// D-DOC-129 (October 2026): how the bisecting loadability walk ended. The walk steps the load multiplier up while the
/// power flow converges and, on a failure, backs off and halves the step. It ends either because the step fell below the
/// minimum step (the nose is then BRACKETED: it lies above the largest converged multiplier and below
/// <see cref="LowestFailedLambda"/>, which is within twice the final step of it) or because the cap on solves ran out
/// first (NOT bracketed: the largest converged multiplier is only a lower bound, and when no solve failed at all the nose
/// was never approached). The cpf verb printed "nose bracketed" in both cases.
/// </summary>
/// <param name="Bracketed">True when the walk ended because the step fell below the minimum step.</param>
/// <param name="Solves">Power-flow solves the walk made.</param>
/// <param name="FinalStep">The step the walk would have taken next.</param>
/// <param name="LowestFailedLambda">The smallest multiplier that did not converge (NaN when every solve converged).</param>
public sealed record LoadabilityWalkEnd(bool Bracketed, int Solves, double FinalStep, double LowestFailedLambda);

/// <summary>The result of <see cref="ContinuationPowerFlow.ScanGrid"/> (R072, October 2026): max loadability on a fixed
/// grid of load factors, by the shared loadability walk.</summary>
/// <param name="LastConvergedIndex">Index of the highest factor converged before the first non-convergence; -1 when the
/// first factor did not converge (or the grid is empty). Every factor at or below this index converged.</param>
/// <param name="MaxFactor">That factor (NaN when <paramref name="LastConvergedIndex"/> is -1).</param>
/// <param name="Results">Every factor's solve, in grid order (null where the point could not be solved at all),
/// including points beyond the first failure, which play no part in the answer.</param>
public sealed record LoadabilityGridScan(int LastConvergedIndex, double MaxFactor, IReadOnlyList<PowerFlowResult?> Results)
{
    /// <summary>True when every factor of a non-empty grid converged (the case did not collapse within the range).</summary>
    public bool ConvergedThroughout => Results.Count > 0 && LastConvergedIndex == Results.Count - 1;
}

/// <summary>
/// Maximum loadability by load-multiplier stepping and bisection. D-DOC-189 (October 2026): despite the class name
/// (kept, as the API is public), this is NOT a predictor-corrector continuation power flow: there is no tangent
/// predictor, no parameterised corrector and no tracing round the nose onto the lower branch. It scales the system load
/// along a direction until the ordinary power flow can no longer solve - the voltage-collapse "nose" point - and reports
/// the loadability margin. It steps the load multiplier lambda up while the solve converges and bisects the step as it
/// crosses the nose, so the returned margin brackets the collapse point from below (the last multiplier at which a
/// Newton power flow still converges; near the nose the Jacobian becomes singular and Newton
/// fails slightly before the true nose, so the bracket is conservative). The margin is the headline voltage-stability number a low-inertia,
/// high-renewable grid cares about (how much more load before collapse).
/// </summary>
public static class ContinuationPowerFlow
{
    /// <summary>The maximum load multiplier lambda for which the power flow still converges (the slack absorbs
    /// the extra generation). lambda = 1 is the base case; the return is &gt;= the largest converged multiplier.</summary>
    public static double MaxLoadability(GridModel model, double step = 0.25, double minStep = 1e-3, int maxSteps = 200)
        => Run(model, step, minStep, maxSteps, onConverged: null);

    /// <summary>
    /// The same continuation as <see cref="MaxLoadability"/>, also recording every converged point: its load
    /// multiplier and every bus voltage magnitude there, in the order solved (F18, October 2026: the PV-curve export of
    /// the <c>cpf</c> verb). The multipliers rise strictly (a step that fails is backed off and halved, so the next one
    /// tried is still above the last converged one), so the points trace each bus's PV curve up to the nose.
    /// <see cref="PvCurve.MaxLambda"/> is bit-for-bit what <see cref="MaxLoadability"/> returns for the same arguments:
    /// it is the same loop, with a recorder that only copies results.
    /// </summary>
    public static PvCurve TracePvCurve(GridModel model, double step = 0.25, double minStep = 1e-3, int maxSteps = 200)
    {
        var points = new List<PvPoint>();
        IReadOnlyList<int>? busIds = null;
        double max = Run(model, step, minStep, maxSteps, (lambda, r) =>
        {
            busIds ??= r.BusIds;
            points.Add(new PvPoint(lambda, (double[])r.Vm.Clone()));
        });
        return new PvCurve(max, busIds ?? model.Buses.Select(b => b.Id).ToArray(), points);
    }

    // ---- The study variant (N161, Ruling R1, Mark delegated 2026-10-04) ---------------------------------------------
    //
    // The cpf verb used to solve each point with the engine's DEFAULT options (30 iterations, reactive limits on, 1e-8 pu,
    // no tap-changer loop), while stress, nminus1, nminus2, n-1-1, nminus1-sweep and chain all solve with the
    // contingency study option set (ContingencyAnalysis.StudyOptions: 100 iterations, reactive limits ON, 1e-8 pu)
    // through an engine wrapped in the on-load tap changer loop. So "converged" meant two different things depending on
    // the verb. The ruling: one definition of a converged point. The overloads below take the solver and the options
    // explicitly; the cpf verb passes ContingencyAnalysis.WithOltcLoop(<Newton engine>) and StudyOptions, exactly as
    // stress does. The parameterless overloads above keep their old behaviour (engine defaults, no OLTC loop) for any
    // Core caller that relies on it, bit for bit.

    /// <summary>
    /// <see cref="MaxLoadability(GridModel, double, double, int)"/> with the solver and the options given explicitly: every
    /// load point is solved by <paramref name="solver"/> with <paramref name="options"/>. The CLI <c>cpf</c> verb passes
    /// the contingency study set (<see cref="ContingencyAnalysis.StudyOptions"/> through
    /// <see cref="ContingencyAnalysis.WithOltcLoop"/>), as <c>stress</c> does (N161).
    /// </summary>
    public static double MaxLoadability(GridModel model, IPowerFlowSolver solver, PowerFlowOptions options,
        double step = 0.25, double minStep = 1e-3, int maxSteps = 200)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(options);
        return Run(model, step, minStep, maxSteps, onConverged: null, out _, solver, options);
    }

    /// <summary><see cref="TracePvCurve(GridModel, double, double, int)"/> with the solver and the options given
    /// explicitly (N161); its <see cref="PvCurve.MaxLambda"/> is bit for bit
    /// <see cref="MaxLoadability(GridModel, IPowerFlowSolver, PowerFlowOptions, double, double, int)"/> for the same
    /// arguments (the same loop with a copy-only recorder).</summary>
    public static PvCurve TracePvCurve(GridModel model, IPowerFlowSolver solver, PowerFlowOptions options,
        double step = 0.25, double minStep = 1e-3, int maxSteps = 200)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(options);
        var points = new List<PvPoint>();
        IReadOnlyList<int>? busIds = null;
        double max = Run(model, step, minStep, maxSteps, (lambda, r) =>
        {
            busIds ??= r.BusIds;
            points.Add(new PvPoint(lambda, (double[])r.Vm.Clone()));
        }, out LoadabilityWalkEnd end, solver, options);
        return new PvCurve(max, busIds ?? model.Buses.Select(b => b.Id).ToArray(), points) { WalkEnd = end };
    }

    /// <summary>
    /// D-DOC-129 (October 2026): <see cref="MaxLoadability(GridModel, IPowerFlowSolver, PowerFlowOptions, double, double, int)"/>
    /// with how its walk ended (bracketed to <paramref name="minStep"/>, or stopped by <paramref name="maxSteps"/>). The
    /// multiplier is the same bits: it is the same loop, which only also reports its final state.
    /// </summary>
    public static (double MaxLambda, LoadabilityWalkEnd End) MaxLoadabilityWithEnd(GridModel model, IPowerFlowSolver solver, PowerFlowOptions options,
        double step = 0.25, double minStep = 1e-3, int maxSteps = 200)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(options);
        double max = Run(model, step, minStep, maxSteps, onConverged: null, out LoadabilityWalkEnd end, solver, options);
        return (max, end);
    }

    // The continuation run behind MaxLoadability and TracePvCurve (so the two cannot differ): the bisecting walk, through
    // the shared loadability loop below. The recorder (null for MaxLoadability) is called for each converged point
    // before the step on, and changes nothing it is given. With no solver given (the parameterless overloads) it is the
    // shared Newton engine with the engine's default options, exactly as before N161: the call is the one-argument
    // Solve(model), statement for statement, so those overloads' bits cannot move.
    private static double Run(GridModel model, double step, double minStep, int maxSteps, Action<double, PowerFlowResult>? onConverged,
        IPowerFlowSolver? solver = null, PowerFlowOptions? options = null)
        => Run(model, step, minStep, maxSteps, onConverged, out _, solver, options);

    // The same, also reporting how the walk ended (D-DOC-129). The walk object only records its state; the multipliers it
    // proposes, and so every solve and the answer, are exactly those of the run above.
    private static double Run(GridModel model, double step, double minStep, int maxSteps, Action<double, PowerFlowResult>? onConverged,
        out LoadabilityWalkEnd end, IPowerFlowSolver? solver = null, PowerFlowOptions? options = null)
    {
        var walk = new BisectingWalk(step, minStep, maxSteps);
        double max;
        if (solver is null)
        {
            IPowerFlowSolver engine = SolverSelector.CreateNewton(model);   // shared dense/sparse rule (was always dense; R004)
            max = Walk(walk, lambda => engine.Solve(ScaleLoads(model, lambda)), onConverged, out _);
        }
        else max = Walk(walk, lambda => solver.Solve(ScaleLoads(model, lambda), options!), onConverged, out _);
        end = new LoadabilityWalkEnd(walk.StepBelowMinimum, walk.Solves, walk.Step, walk.LowestFailed);
        return max;
    }

    // ---- The shared loadability loop (R072, October 2026) ------------------------------------------------------------
    //
    // "Max loadability" is defined ONCE, here: walk the load multiplier upward, solving at each multiplier the walk
    // proposes; the answer is the last multiplier that converged. Two walks use it:
    //   * BisectingWalk (the cpf verb, MaxLoadability, TracePvCurve): step up while the solve converges; on a failure back
    //     off, halve the step and try again, until the step is below minStep or maxSteps solves have been made;
    //   * GridWalk (the CLI stress verb, through ScanGrid): the given factors in order, stopping at the first failure or
    //     the end of the grid, so the answer is the highest factor converged BEFORE the first non-convergence.
    // What the two verbs still do differently is deliberate and labelled in each verb's help: HOW the load is scaled at a
    // multiplier (cpf: every bus's P and Q times lambda with the slack absorbing the balance; stress: GridScenario.AtLoad,
    // which re-dispatches the generators), the solver options, and the walk (bisection to a bracket versus a fixed grid).
    // Before R072 the stress verb kept its own copy of the "highest converged factor before the first failure" rule; it
    // now runs through Walk, with the same multipliers, the same independent solves and the same rule, so its numbers
    // are bit-for-bit unchanged.

    /// <summary>A walk of the load multiplier: where to start, and where to go next given whether the last solve
    /// converged. Returning false ends the walk.</summary>
    private interface IMultiplierWalk
    {
        /// <summary>The multiplier to solve next (valid after <see cref="Start"/> or <see cref="Advance"/> returned true).</summary>
        double Current { get; }

        /// <summary>Begin the walk: true when there is a first multiplier to solve.</summary>
        bool Start();

        /// <summary>Move on after solving <see cref="Current"/>: true when there is another multiplier to solve.</summary>
        bool Advance(bool converged);
    }

    // The continuation stepping exactly as it was written before R072, statement for statement, so MaxLoadability's
    // bits cannot move:
    //   lambda = 1; while (step >= minStep && steps++ < maxSteps) { solve(lambda); if converged { lambda += step }
    //   else { lambda -= step; step *= 0.5; lambda += step } }
    private sealed class BisectingWalk(double step, double minStep, int maxSteps) : IMultiplierWalk
    {
        private double _step = step;
        private int _steps;

        public double Current { get; private set; } = 1.0;   // lambda = 1 is the base case

        // D-DOC-129: read-only views of the walk's state, for the caller to say how it ended. Nothing in the walk reads them.
        /// <summary>The step the walk would take next.</summary>
        public double Step => _step;
        /// <summary>True when the step has fallen below the minimum step (the nose is bracketed).</summary>
        public bool StepBelowMinimum => !(_step >= minStep);
        /// <summary>Solves made so far (one per multiplier proposed and solved).</summary>
        public int Solves { get; private set; }
        /// <summary>The smallest multiplier that did not converge (NaN until one fails).</summary>
        public double LowestFailed { get; private set; } = double.NaN;

        public bool Start() => _step >= minStep && _steps++ < maxSteps;

        public bool Advance(bool converged)
        {
            Solves++;
            if (converged)
            {
                Current += _step;
            }
            else
            {
                if (!(LowestFailed <= Current)) LowestFailed = Current;   // D-DOC-129: record only (NaN compares false)
                Current -= _step;   // back off
                _step *= 0.5;       // refine
                Current += _step;
            }
            return _step >= minStep && _steps++ < maxSteps;
        }
    }

    // A fixed grid of multipliers, in the given order, ending at the first failure or at the end of the grid.
    private sealed class GridWalk(IReadOnlyList<double> factors) : IMultiplierWalk
    {
        private int _i;

        public double Current => factors[_i];

        public bool Start() => factors.Count > 0;

        public bool Advance(bool converged) => converged && ++_i < factors.Count;
    }

    // The loop itself. solveAt may return null, meaning "this point could not be solved at all" (counted as not
    // converged). Returns the last converged multiplier (0 when none converged) and, through convergedCount, how many
    // points converged.
    private static double Walk(IMultiplierWalk walk, Func<double, PowerFlowResult?> solveAt,
        Action<double, PowerFlowResult>? onConverged, out int convergedCount)
    {
        double lastGood = 0.0;
        convergedCount = 0;
        bool more = walk.Start();
        while (more)
        {
            double lambda = walk.Current;
            PowerFlowResult? r = solveAt(lambda);
            bool converged = r is { Converged: true };
            if (converged)
            {
                onConverged?.Invoke(lambda, r!);
                lastGood = lambda;
                convergedCount++;
            }
            more = walk.Advance(converged);
        }
        return lastGood;
    }

    /// <summary>
    /// Max loadability on a fixed grid of load factors (R072, October 2026; the CLI <c>stress</c> verb). Every factor is
    /// solved by <paramref name="solveAt"/> (in parallel, up to <paramref name="maxDegreeOfParallelism"/> at once, so
    /// each solve must be independent of the others), then the shared loadability walk runs over the factors in order
    /// and stops at the first one that did not converge. <paramref name="solveAt"/> returns null for a point that could
    /// not be solved at all (an exception the caller chose to absorb); that counts as not converged. Points beyond the
    /// first failure are solved too (all points are solved up front, in parallel, as the verb always did) but play no
    /// part in the answer.
    /// </summary>
    /// <param name="factors">The load factors, in walk order (the verb's ascending grid).</param>
    /// <param name="solveAt">Solves the case at one factor (the caller's scaling, solver and options).</param>
    /// <param name="maxDegreeOfParallelism">Points solved at once; at least 1.</param>
    public static LoadabilityGridScan ScanGrid(IReadOnlyList<double> factors, Func<double, PowerFlowResult?> solveAt, int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(factors);
        ArgumentNullException.ThrowIfNull(solveAt);
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), maxDegreeOfParallelism,
                "The number of load points solved at once must be at least 1.");

        // Solve every point first. Each solve is independent, so the order they run in cannot change any number.
        var results = new PowerFlowResult?[factors.Count];
        Parallel.For(0, factors.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
            i => results[i] = solveAt(factors[i]));

        // Then the shared walk over the grid, reading the stored results in walk order (GridWalk visits 0, 1, 2, ...).
        int next = 0;
        double max = Walk(new GridWalk(factors), _ => results[next++], onConverged: null, out int convergedCount);
        return new LoadabilityGridScan(convergedCount - 1, convergedCount > 0 ? max : double.NaN, results);
    }

    /// <summary>A copy of the model with every bus's real and reactive demand scaled by <paramref name="lambda"/>.</summary>
    public static GridModel ScaleLoads(GridModel model, double lambda)
    {
        var buses = new List<Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses) buses.Add(b with { Pd = b.Pd * lambda, Qd = b.Qd * lambda });
        // N094 (October 2026): through GridModel.CopyWith, which carries the out-of-service generators (F16) and every
        // other model property. The hand-built copy kept only the in-service list, so a writer given this model lost
        // the out-of-service units. Engines read Generators, which is the same list as before, so no number changes.
        return model.CopyWith(buses: buses);
    }
}
