using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the harmonic-solver reuse: reusing the cached RCM ordering across
/// same-pattern orders, and factoring once for many right-hand sides, must be BIT-FOR-BIT identical to the
/// naive path (a fresh solver + a fresh Solve per system/RHS). RCM is a pure function of the pattern and the
/// numeric factorization is deterministic, so reuse only saves work - it never changes the answer.
/// </summary>
public class ComplexSolverReusePinTests
{
    [Fact]
    public void Cached_ordering_across_same_pattern_orders_matches_a_fresh_solver_bit_for_bit()
    {
        GridModel model = IeeeCases.Case30();
        var opt = new HarmonicModelOptions { Orders = new[] { 5, 7, 11, 13, 17 } };
        var rhs = MakeRhs(model.BusCount);

        var reused = new RealExpandedComplexSolver(); // accumulates the ordering cache across orders
        foreach (int h in opt.Orders)
        {
            ComplexCsr y = FrequencyDependentYBus.Build(model, h, opt);
            Complex[]? viaReused = reused.Solve(y, rhs);
            Complex[]? viaFresh = new RealExpandedComplexSolver().Solve(y, rhs); // no cache
            AssertBitEqual(viaFresh, viaReused);
        }
    }

    [Fact]
    public void Factor_once_multi_rhs_matches_solve_per_rhs_bit_for_bit()
    {
        GridModel model = IeeeCases.Case30();
        var opt = new HarmonicModelOptions();
        ComplexCsr y = FrequencyDependentYBus.Build(model, 7, opt);
        int n = y.N;

        var solver = new RealExpandedComplexSolver();
        IComplexLinearFactorization? fac = solver.Factor(y);
        Assert.NotNull(fac);

        // Several distinct unit-vector RHS (the HSE shape): factor-once vs one-shot Solve must match.
        for (int j = 0; j < n; j += 3)
        {
            var e = new Complex[n];
            e[j] = Complex.One;
            Complex[]? once = fac!.Solve(e);
            Complex[]? shot = new RealExpandedComplexSolver().Solve(y, e);
            AssertBitEqual(shot, once);
        }
    }

    [Fact]
    public void Penetration_is_unchanged_end_to_end()
    {
        // End-to-end: the default (reusing) solver must give the same bus-voltage spectra as forcing a
        // fresh solver per order.
        GridModel model = IeeeCases.Case30();
        var sources = new[] { HarmonicSourceLibrary.Make("6-pulse", model.Buses[3].Id, 0.7) };

        HarmonicPenetrationResult reused = HarmonicPenetration.Solve(model, sources);
        HarmonicPenetrationResult fresh = HarmonicPenetration.Solve(model, sources, null, new FreshEachCall());

        for (int oi = 0; oi < reused.Orders.Count; oi++)
            for (int i = 0; i < reused.BusIds.Count; i++)
                AssertBitEqual(fresh.Voltage[oi][i], reused.Voltage[oi][i]);
    }

    [Fact]
    public void Impedance_scan_is_unchanged_end_to_end()
    {
        // The default (parallel, per-worker cached) scan must match forcing a fresh sequential solver,
        // including the resonance-peak selection and participation column.
        GridModel model = IeeeCases.Case30();
        int bus = model.Buses[10].Id;

        ImpedanceScanResult reused = HarmonicImpedanceScan.DrivingPoint(model, bus);
        ImpedanceScanResult fresh = HarmonicImpedanceScan.DrivingPoint(model, bus, null, new FreshEachCall());

        Assert.Equal(fresh.ResonanceOrder, reused.ResonanceOrder);
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(fresh.PeakMagnitude), System.BitConverter.DoubleToInt64Bits(reused.PeakMagnitude));
        for (int oi = 0; oi < reused.Orders.Count; oi++)
            AssertBitEqual(fresh.Z[oi], reused.Z[oi]);
        for (int i = 0; i < reused.BusIds.Count; i++)
            AssertBitEqual(fresh.Participation[i], reused.Participation[i]);
    }

    // A solver wrapper that delegates to a brand-new RealExpandedComplexSolver every call - no cache reuse.
    private sealed class FreshEachCall : IComplexLinearSolver
    {
        public string Name => "fresh-each-call";
        public Complex[]? Solve(ComplexCsr a, Complex[] b) => new RealExpandedComplexSolver().Solve(a, b);
    }

    private static Complex[] MakeRhs(int n)
    {
        var b = new Complex[n];
        for (int i = 0; i < n; i++) b[i] = new Complex(0.1 * ((i % 5) - 2), 0.05 * ((i % 3) - 1));
        return b;
    }

    private static void AssertBitEqual(Complex[]? expected, Complex[]? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Length, actual!.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(expected[i].Real), System.BitConverter.DoubleToInt64Bits(actual[i].Real));
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(expected[i].Imaginary), System.BitConverter.DoubleToInt64Bits(actual[i].Imaginary));
        }
    }

    private static void AssertBitEqual(Complex expected, Complex actual)
    {
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(expected.Real), System.BitConverter.DoubleToInt64Bits(actual.Real));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(expected.Imaginary), System.BitConverter.DoubleToInt64Bits(actual.Imaginary));
    }
}
