using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public partial class Lf2ContainedTests
{
    // =================================================================================================================
    // N232: an outage that leaves a part with no reference bus and a real-power balance it can never meet is reported
    // at once (the verdict the solve would have ended in), instead of running the BiCGSTAB fallback to its cap.
    // =================================================================================================================

    /// <summary>A pass-through solver the screens do not recognise as a built-in Newton engine, so they always call it:
    /// the pre-N232 path, used here as the oracle. It solves with exactly the engine it wraps.</summary>
    private sealed class N232Opaque(IPowerFlowSolver inner) : IPowerFlowSolver
    {
        public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null) => inner.Solve(model, options);
    }

    private static int N232Branch(GridModel m, int from, int to)
        => Enumerable.Range(0, m.Branches.Count).Single(j => m.Branches[j].From == from && m.Branches[j].To == to);

    private static GridModel N232Without(GridModel m, int from, int to)
    {
        int k = N232Branch(m, from, to);
        return m.CopyWith(branches: m.Branches.Where((_, j) => j != k).ToList());
    }

    /// <summary>Exact, field by field: every double compared by its bits, every list element by element.</summary>
    private static void N232AssertIdentical(ContingencyResult expected, ContingencyResult actual)
    {
        string who = expected.Outage;
        Assert.Equal(expected.Outage, actual.Outage);
        Assert.True(expected.Converged == actual.Converged, $"{who}: Converged {expected.Converged} -> {actual.Converged}");
        Assert.True(BitConverter.DoubleToInt64Bits(expected.MaxBranchLoadingPercent) == BitConverter.DoubleToInt64Bits(actual.MaxBranchLoadingPercent), $"{who}: MaxBranchLoadingPercent");
        Assert.True(BitConverter.DoubleToInt64Bits(expected.WorstBoundaryLoadingPercent) == BitConverter.DoubleToInt64Bits(actual.WorstBoundaryLoadingPercent), $"{who}: WorstBoundaryLoadingPercent");
        Assert.True(BitConverter.DoubleToInt64Bits(expected.MinVm) == BitConverter.DoubleToInt64Bits(actual.MinVm), $"{who}: MinVm");
        Assert.True(BitConverter.DoubleToInt64Bits(expected.MaxVm) == BitConverter.DoubleToInt64Bits(actual.MaxVm), $"{who}: MaxVm");
        Assert.Equal(expected.Violations, actual.Violations);
        Assert.Equal(expected.Details.Count, actual.Details.Count);
        for (int i = 0; i < expected.Details.Count; i++)
        {
            ContingencyViolation e = expected.Details[i], a = actual.Details[i];
            Assert.Equal((e.Kind, e.Element, e.Name), (a.Kind, a.Element, a.Name));
            Assert.True(BitConverter.DoubleToInt64Bits(e.Value) == BitConverter.DoubleToInt64Bits(a.Value), $"{who}: detail {i} value");
        }
        Assert.Equal(expected.NettedAgainstBase, actual.NettedAgainstBase);
        Assert.Equal(expected.PreExistingViolations, actual.PreExistingViolations);
        Assert.True(expected.Islanded == actual.Islanded, $"{who}: Islanded {expected.Islanded} -> {actual.Islanded}");
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.True(BitConverter.DoubleToInt64Bits(expected.LostLoadMw) == BitConverter.DoubleToInt64Bits(actual.LostLoadMw), $"{who}: LostLoadMw");
        Assert.True(BitConverter.DoubleToInt64Bits(expected.LostGenerationMw) == BitConverter.DoubleToInt64Bits(actual.LostGenerationMw), $"{who}: LostGenerationMw");
        Assert.Equal(expected.Note, actual.Note);
    }

    public static TheoryData<string> N232Cases => new() { "case9", "case14", "case30", "case57", "case118" };

    private static GridModel N232Case(string name) => name switch
    {
        "case9" => IeeeCases.Case9(),
        "case14" => IeeeCases.Case14(),
        "case30" => IeeeCases.Case30(),
        "case57" => IeeeCases.Case57(),
        "case118" => IeeeCases.Case118(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not an N232 test case"),
    };

    [Theory]
    [MemberData(nameof(N232Cases))]
    public void N232_every_branch_generator_and_bus_outage_is_bit_identical_to_the_full_solve(string name)
    {
        // The proof, run: every single outage of the case (branches, units, buses), screened by the study solver (which
        // now skips the provably unsolvable states) and by the same engine behind an opaque wrapper (which the screen
        // must always call: the pre-N232 path). Every field of every result is identical, bit for bit. The islanded
        // outages among them (case9 1-4, 3-6, 8-2; the radial spurs of the larger cases) prove the shortcut returns the
        // very verdict the full solve ends in; all the others prove nothing else moved.
        GridModel m = N232Case(name);
        IReadOnlyList<ElementOutage> outages = ContingencyAnalysis.SingleOutages(m, true, true, true);
        IReadOnlyList<ContingencyResult> fast = ContingencyAnalysis.ScreenElementOutages(
            m, outages, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        IReadOnlyList<ContingencyResult> full = ContingencyAnalysis.ScreenElementOutages(
            m, outages, new N232Opaque(ContingencyAnalysis.CreateStudySolver(m)), ContingencyAnalysis.StudyOptions);
        Assert.Equal(full.Count, fast.Count);
        for (int i = 0; i < full.Count; i++) N232AssertIdentical(full[i], fast[i]);

        // The base case too.
        N232AssertIdentical(
            ContingencyAnalysis.ScreenBaseCase(m, new N232Opaque(ContingencyAnalysis.CreateStudySolver(m)), ContingencyAnalysis.StudyOptions),
            ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions));
    }

    [Fact]
    public void N232_case9_n_minus_2_is_bit_identical_to_the_full_solve()
    {
        // Every pair of case9 branches (36 pairs, many islanding), exhaustive, through the parallel N-k screen.
        GridModel m = IeeeCases.Case9();
        IReadOnlyList<ContingencyResult> fast = ContingencyAnalysis.ScreenNMinusK(m, 2,
            ContingencyAnalysis.StudySolverFactory(m), ContingencyAnalysis.StudyOptions, exhaustive: true);
        IReadOnlyList<ContingencyResult> full = ContingencyAnalysis.ScreenNMinusK(m, 2,
            () => new N232Opaque(ContingencyAnalysis.CreateStudySolver(m)), ContingencyAnalysis.StudyOptions, exhaustive: true);
        Assert.Equal(full.Count, fast.Count);
        for (int i = 0; i < full.Count; i++) N232AssertIdentical(full[i], fast[i]);
    }

    [Fact]
    public void N232_the_documented_case9_islanded_outages_take_the_shortcut_and_keep_their_verdict()
    {
        // The three ISLANDED rows of the nminus1-case9 fixture: each leaves a part the proof covers.
        GridModel m = IeeeCases.Case9();
        double tol = ContingencyAnalysis.StudyOptions.Tolerance;

        // 1-4: buses 2-9 lose the reference (bus 1): 248 MW of units against 315 MW of demand.
        ContingencyAnalysis.UnsolvablePart? load = ContingencyAnalysis.ProvablyUnsolvablePart(N232Without(m, 1, 4), tol);
        Assert.NotNull(load);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8, 9 }, load!.BusIds);
        Assert.True(load.NetInjectionPu < 0.0);
        Assert.Contains("demand exceeds its generation", load.Reason, StringComparison.Ordinal);

        // 3-6 and 8-2: a generator bus alone, with no branch and no shunt: lossless, 85 MW and 163 MW unbalanced.
        foreach ((int from, int to, int bus) in new[] { (3, 6, 3), (8, 2, 2) })
        {
            ContingencyAnalysis.UnsolvablePart? gen = ContingencyAnalysis.ProvablyUnsolvablePart(N232Without(m, from, to), tol);
            Assert.NotNull(gen);
            Assert.Equal(new[] { bus }, gen!.BusIds);
            Assert.True(gen.NetInjectionPu > 0.0);
            Assert.Contains("lossless", gen.Reason, StringComparison.Ordinal);
        }

        // And the screen's verdict for them is the fixture's: islanded, not converged.
        foreach ((int from, int to) in new[] { (1, 4), (3, 6), (8, 2) })
        {
            ContingencyResult r = ContingencyAnalysis.ScreenOutage(m, new[] { N232Branch(m, from, to) },
                ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
            Assert.False(r.Converged);
            Assert.True(r.Islanded);
            Assert.Equal(new[] { "islanded" }, r.Violations);
        }
    }

    [Fact]
    public void N232_the_case14_condenser_cut_off_by_7_8_is_still_solved()
    {
        // Bus 8 of case14 is a synchronous condenser (Pg = 0, no load, no shunt). Cut off by branch 7-8 its P equation
        // holds (0 = 0), so the proof does not apply and the screen solves the outage exactly as before: the
        // nminus1-case14 fixture's "base only" row.
        GridModel m = IeeeCases.Case14();
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232Without(m, 7, 8), ContingencyAnalysis.StudyOptions.Tolerance));
        ContingencyResult r = ContingencyAnalysis.ScreenOutage(m, new[] { N232Branch(m, 7, 8) },
            ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.True(r.Converged);
        Assert.False(r.Islanded);
    }

    [Fact]
    public void N232_a_connected_model_has_no_unsolvable_part()
    {
        foreach (string name in new[] { "case9", "case14", "case30", "case57", "case118" })
            Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232Case(name), ContingencyAnalysis.StudyOptions.Tolerance));
    }

    /// <summary>A three-bus model: bus 1 the reference with a unit, bus 2 and bus 3 joined by one branch, and bus 2 cut
    /// off from bus 1 (the 1-2 branch out of service), so {2, 3} is a part with no reference.</summary>
    private static GridModel N232ThreeBus(double pg2, double pd3, double r23, ZipLoad? zip3 = null, double gs3 = 0.0,
        BusType type2 = BusType.Pv) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack),
            new Bus(2, type2),
            new Bus(3, BusType.Pq, Pd: pd3, Gs: gs3, Zip: zip3),
        },
        Generators = new[] { new Generator(1, 50), new Generator(2, pg2) },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.1, InService: false),
            new Branch(2, 3, r23, 0.1),
        },
    };

    [Fact]
    public void N232_the_proof_covers_only_what_it_proves()
    {
        double tol = new PowerFlowOptions().Tolerance;

        // Load part (demand > units, R >= 0): covered.
        Assert.NotNull(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 10, pd3: 30, r23: 0.01), tol));
        // Lossless part, unbalanced either way: covered.
        Assert.NotNull(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 30, pd3: 10, r23: 0.0), tol));
        Assert.NotNull(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 10, pd3: 30, r23: 0.0), tol));

        // Lossy part with a surplus: its losses might absorb it, so it is left to the solve.
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 30, pd3: 10, r23: 0.01), tol));
        // Balanced within the margin: left to the solve.
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 10, pd3: 10, r23: 0.0), tol));
        // A voltage-dependent load: its P specification moves with the voltage, so no proof.
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(
            N232ThreeBus(pg2: 10, pd3: 30, r23: 0.01, zip3: new ZipLoad(Ap: 1, Cp: 0)), tol));
        // A negative resistance or shunt conductance (an equivalent's artefact) could make losses negative: no proof.
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 10, pd3: 30, r23: -0.01), tol));
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 10, pd3: 30, r23: 0.01, gs3: -1.0), tol));
        // A part with its own reference is an ordinary second island: left to the solve.
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 10, pd3: 30, r23: 0.01, type2: BusType.Slack), tol));
        // A tolerance that is not a positive finite number proves nothing.
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 10, pd3: 30, r23: 0.01), 0.0));
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(N232ThreeBus(pg2: 10, pd3: 30, r23: 0.01), double.NaN));
    }

    [Fact]
    public void N232_a_branch_or_unit_on_a_missing_bus_is_left_to_the_engine()
    {
        // The engine refuses such a model itself and the screen records that error, as before: no shortcut.
        double tol = new PowerFlowOptions().Tolerance;
        GridModel bad = N232ThreeBus(pg2: 10, pd3: 30, r23: 0.01);
        GridModel badBranch = bad.CopyWith(branches: bad.Branches.Append(new Branch(3, 99, 0.01, 0.1)).ToList());
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(badBranch, tol));
        GridModel badUnit = bad.CopyWith(generators: bad.Generators.Append(new Generator(99, 5)).ToList());
        Assert.Null(ContingencyAnalysis.ProvablyUnsolvablePart(badUnit, tol));
    }

    [Fact]
    public void N232_only_the_built_in_newton_engines_take_the_shortcut()
    {
        GridModel m = IeeeCases.Case9();
        Assert.True(ContingencyAnalysis.IsBuiltInNewton(new NewtonRaphsonPowerFlow()));
        Assert.True(ContingencyAnalysis.IsBuiltInNewton(new SparseNewtonRaphsonPowerFlow()));
        Assert.True(ContingencyAnalysis.IsBuiltInNewton(ContingencyAnalysis.CreateStudySolver(m)));
        Assert.True(ContingencyAnalysis.IsBuiltInNewton(SolverSelector.CreateNewton(m)));
        Assert.False(ContingencyAnalysis.IsBuiltInNewton(new BackwardForwardSweep()));
        Assert.False(ContingencyAnalysis.IsBuiltInNewton(new N232Opaque(new NewtonRaphsonPowerFlow())));
        Assert.False(ContingencyAnalysis.IsBuiltInNewton(new OltcLoopPowerFlow(new N232Opaque(new NewtonRaphsonPowerFlow()))));
    }

    [Fact]
    public void N232_a_cancelled_screen_stays_cancelled_on_the_shortcut()
    {
        // The solve would have thrown at its first iteration; the shortcut throws the same way, never returns a verdict.
        GridModel m = IeeeCases.Case9();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        PowerFlowOptions cancelled = ContingencyAnalysis.StudyOptions with { Cancellation = cts.Token };
        Assert.ThrowsAny<OperationCanceledException>(() => ContingencyAnalysis.ScreenOutage(m, new[] { N232Branch(m, 1, 4) },
            ContingencyAnalysis.CreateStudySolver(m), cancelled));
    }
}
