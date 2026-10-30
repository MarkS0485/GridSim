using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public partial class PPerformanceTests
{
    // =================================================================================================================
    // R007: ClassicalInit on the sparse cells, and the FaultScreen, against the verbatim 1.95.225 FromSolvedCase.
    // =================================================================================================================

    private static void AssertSamePrepared(ClassicalInit.Prepared e, ClassicalInit.Prepared a, string what)
    {
        Assert.Equal(e.H.Length, a.H.Length);
        for (int i = 0; i < e.H.Length; i++)
        {
            Assert.True(SameBits(e.H[i], a.H[i]), $"{what}: H[{i}]");
            Assert.True(SameBits(e.Pm[i], a.Pm[i]), $"{what}: Pm[{i}] {e.Pm[i]:R} vs {a.Pm[i]:R}");
            Assert.True(SameBits(e.Eq[i], a.Eq[i]), $"{what}: Eq[{i}]");
            Assert.True(SameBits(e.Delta0[i], a.Delta0[i]), $"{what}: Delta0[{i}]");
        }
        AssertSameMatrix(e.YReduced, a.YReduced, what + " YReduced");
        Assert.Equal(e.YReducedFaulted is null, a.YReducedFaulted is null);
        if (e.YReducedFaulted is not null) AssertSameMatrix(e.YReducedFaulted, a.YReducedFaulted!, what + " YReducedFaulted");
        Assert.Equal(e.GeneratorBuses, a.GeneratorBuses);
        Assert.Equal(e.MachineIndices is null, a.MachineIndices is null);
        Assert.Equal(e.MachineIndex, a.MachineIndex);
    }

    /// <summary>A case with inertia on every unit (MATPOWER cases carry none), a few units made converters so holding
    /// has something to hold.</summary>
    private static GridModel WithInertia(GridModel m, bool someConverters)
        => m.CopyWith(generators: m.Generators.Select((g, k) => g with
        {
            H = 2.0 + (k % 5),
            Type = someConverters && k % 4 == 3 ? "wind" : g.Type,
        }).ToList());

    private static IEnumerable<(string Name, GridModel Model, double[]? Xdp)> ClassicalModels()
    {
        GridModel c9 = IeeeCases.Case9();
        double[] xdp9 = c9.Generators.Select(g => IeeeCases.Wscc9TransientReactancePu.TryGetValue(g.Bus, out double v) ? v : 0.3).ToArray();
        yield return ("case9 Anderson-Fouad", c9, xdp9);
        yield return ("case14", WithInertia(IeeeCases.Case14(), false), null);
        yield return ("case30 with converters", WithInertia(IeeeCases.Case30(), true), null);
        yield return ("case57", WithInertia(IeeeCases.Case57(), false), null);
        yield return ("case118 with converters", WithInertia(IeeeCases.Case118(), true), null);
    }

    [Fact]
    public void R007_from_solved_case_is_bit_identical_to_the_dense_code_unfaulted_and_at_every_bus()
    {
        foreach ((string name, GridModel m, double[]? xdp) in ClassicalModels())
        {
            PowerFlowResult pf = SolverSelector.CreateNewton(m).Solve(m);
            Assert.True(pf.Converged, name);
            foreach (bool hold in new[] { false, true })
            {
                // Unfaulted.
                R007Compare(() => PPerformanceOracles.OldFromSolvedCase(m, pf, xdp, null, hold),
                    () => ClassicalInit.FromSolvedCase(m, pf, xdp, null, hold), $"{name} hold={hold} unfaulted");
                // Every bus as the fault bus (case118: every fourth, to keep the dense oracle's time down), the screen too.
                ClassicalInit.FaultScreen screen = ClassicalInit.PrepareFaultScreen(m, pf, xdp, hold);
                for (int b = 0; b < m.Buses.Count; b += m.Buses.Count > 60 ? 4 : 1)
                {
                    int bus = m.Buses[b].Id;
                    R007Compare(() => PPerformanceOracles.OldFromSolvedCase(m, pf, xdp, bus, hold),
                        () => ClassicalInit.FromSolvedCase(m, pf, xdp, bus, hold), $"{name} hold={hold} fault {bus}");
                    R007Compare(() => PPerformanceOracles.OldFromSolvedCase(m, pf, xdp, bus, hold),
                        () => screen.ForFault(bus), $"{name} hold={hold} screen fault {bus}");
                }
                // An unknown fault bus fails the same way on both.
                R007Compare(() => PPerformanceOracles.OldFromSolvedCase(m, pf, xdp, -12345, hold),
                    () => screen.ForFault(-12345), $"{name} hold={hold} unknown bus");
            }
        }
    }

    /// <summary>Both preparations: the same record field for field, or the same exception type and message.</summary>
    private static void R007Compare(Func<ClassicalInit.Prepared> old, Func<ClassicalInit.Prepared> now, string what)
    {
        ClassicalInit.Prepared? e = null, a = null;
        Exception? ee = null, ae = null;
        try { e = old(); } catch (Exception ex) { ee = ex; }
        try { a = now(); } catch (Exception ex) { ae = ex; }
        Assert.True((ee is null) == (ae is null), $"{what}: old {(ee is null ? "succeeded" : "threw " + ee.Message)}, new {(ae is null ? "succeeded" : "threw " + ae.Message)}");
        if (ee is not null)
        {
            Assert.Equal(ee.GetType(), ae!.GetType());
            Assert.Equal(ee.Message, ae.Message);
            return;
        }
        AssertSamePrepared(e!, a!, what);
    }

    [Fact]
    public void R007_the_failures_before_and_inside_the_reduction_are_the_dense_code_ones()
    {
        // No inertia anywhere (a MATPOWER case as loaded) and a non-converged result: the same exceptions, same text.
        GridModel c14 = IeeeCases.Case14();
        PowerFlowResult pf = SolverSelector.CreateNewton(c14).Solve(c14);
        foreach (bool hold in new[] { false, true })
            R007Compare(() => PPerformanceOracles.OldFromSolvedCase(c14, pf, null, 4, hold),
                () => ClassicalInit.FromSolvedCase(c14, pf, null, 4, hold), $"case14 no inertia hold={hold}");

        // An isolated bus in the network: the reduction meets its zero pivot, with the same message.
        GridModel withIsolated = WithInertia(c14, false).CopyWith(buses: c14.Buses.Concat(new[] { new Bus(999, BusType.Pq) }).ToList());
        var pfIso = new PowerFlowResult
        {
            Converged = pf.Converged,
            Iterations = pf.Iterations,
            MaxMismatch = pf.MaxMismatch,
            BusIds = pf.BusIds.Concat(new[] { 999 }).ToArray(),
            Vm = pf.Vm.Concat(new[] { 1.0 }).ToArray(),
            VaDeg = pf.VaDeg.Concat(new[] { 0.0 }).ToArray(),
            PInjMw = pf.PInjMw.Concat(new[] { 0.0 }).ToArray(),
            QInjMvar = pf.QInjMvar.Concat(new[] { 0.0 }).ToArray(),
            TotalLossMw = pf.TotalLossMw,
            TotalLossMvar = pf.TotalLossMvar,
            LineFlows = pf.LineFlows,
        };
        R007Compare(() => PPerformanceOracles.OldFromSolvedCase(withIsolated, pfIso, null, 4, true),
            () => ClassicalInit.FromSolvedCase(withIsolated, pfIso, null, 4, true), "case14 + isolated bus");
    }

    [Fact]
    public void R007_the_cct_bracket_on_a_screen_is_the_per_bus_bracket()
    {
        // The CLI screen's loop (every generator bus) on case9 with the Anderson-Fouad reactances, through the screen and
        // through the per-bus overload: the same bracket, field for field.
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = SolverSelector.CreateNewton(m).Solve(m);
        double[] xdp = m.Generators.Select(g => IeeeCases.Wscc9TransientReactancePu.TryGetValue(g.Bus, out double v) ? v : 0.3).ToArray();
        ClassicalInit.FaultScreen screen = ClassicalInit.PrepareFaultScreen(m, pf, xdp, holdNonMachines: true);
        foreach (int bus in m.Generators.Select(g => g.Bus).Distinct().Concat(new[] { 7 }))
        {
            CctBracket perBus = ClassicalInit.CriticalClearingBracket(m, pf, xdp, bus, holdNonMachines: true);
            CctBracket viaScreen = ClassicalInit.CriticalClearingBracket(screen, bus);
            Assert.Equal(perBus, viaScreen);   // a record of ints, bools and doubles (each double compared below by bits)
            Assert.True(SameBits(perBus.StableSeconds, viaScreen.StableSeconds), $"bus {bus} StableSeconds");
        }
    }

    [Fact]
    public void R007_a_screen_computes_nothing_until_asked_and_each_prepared_owns_its_arrays()
    {
        // Making a screen on a model with no generator cannot fail (the CLI makes it before a loop that may not run).
        GridModel none = IeeeCases.Case14().CopyWith(generators: Array.Empty<Generator>());
        PowerFlowResult pf = SolverSelector.CreateNewton(IeeeCases.Case14()).Solve(IeeeCases.Case14());
        ClassicalInit.FaultScreen empty = ClassicalInit.PrepareFaultScreen(none, pf);
        var ex = Assert.Throws<InvalidOperationException>(() => empty.ForFault(4));
        Assert.Equal("No generators to initialise.", ex.Message);

        // Two Prepared records from one screen never share an array (writing one does not touch the other).
        GridModel m = WithInertia(IeeeCases.Case14(), false);
        PowerFlowResult pfm = SolverSelector.CreateNewton(m).Solve(m);
        ClassicalInit.FaultScreen screen = ClassicalInit.PrepareFaultScreen(m, pfm);
        ClassicalInit.Prepared a = screen.ForFault(4), b = screen.ForFault(4);
        Assert.NotSame(a.H, b.H);
        Assert.NotSame(a.Pm, b.Pm);
        Assert.NotSame(a.YReduced, b.YReduced);
        Assert.NotSame(a.YReducedFaulted, b.YReducedFaulted);
        double before = b.Pm[0];
        a.Pm[0] = double.NaN;
        a.YReduced[0, 0] = Complex.NaN;
        Assert.True(SameBits(before, b.Pm[0]));
        Assert.False(double.IsNaN(screen.ForFault(5).YReduced[0, 0].Real));
    }
}
