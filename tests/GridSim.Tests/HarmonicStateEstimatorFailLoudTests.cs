using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N012 (WS5 follow-up): the harmonic state estimator solved its Gram matrix with the clamping solver that never
/// returns null, so an unobservable source was silently estimated as zero, and a singular Y(h) gave an all-zero
/// impedance column. Both now fail loud; an observable estimate is unchanged (same elimination, same bits).
/// </summary>
public class HarmonicStateEstimatorFailLoudTests
{
    [Fact]
    public void Indistinguishable_candidate_sources_are_refused_not_zeroed()
    {
        // The same bus listed twice: the two H columns are identical, so the Gram matrix is singular.
        GridModel model = IeeeCases.Case9();
        var opt = new HarmonicModelOptions { Orders = new[] { 5 } };
        int b = model.Buses[4].Id;
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(model,
            new[] { HarmonicSourceLibrary.Make("6-pulse", b, fundamentalCurrentPu: 0.8) }, opt);
        var meas = model.Buses.Select((bus, i) => new HarmonicVoltageMeasurement(bus.Id, pen.Voltage[0][i], 1e-4)).ToList();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            HarmonicStateEstimator.Estimate(model, 5, meas, new[] { b, b }, opt));
        Assert.Contains("not observable", ex.Message);
    }

    [Fact]
    public void The_honest_dense_solve_matches_the_clamping_solve_on_a_non_singular_system()
    {
        var a = new Complex[,] { { new(4, 1), new(1, -1) }, { new(1, 2), new(3, 0.5) } };
        var bvec = new[] { new Complex(1, 0), new Complex(0, 2) };
        Complex[] clamped = DenseComplexLuSolver.SolveDense(a, bvec, 2);
        Complex[]? honest = DenseComplexLuSolver.SolveDenseOrNull(a, bvec, 2);
        Assert.NotNull(honest);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(clamped[i].Real), BitConverter.DoubleToInt64Bits(honest![i].Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(clamped[i].Imaginary), BitConverter.DoubleToInt64Bits(honest[i].Imaginary));
        }
    }

    [Fact]
    public void The_honest_dense_solve_returns_null_on_a_singular_system()
    {
        var a = new Complex[,] { { Complex.One, new(2, 0) }, { new(2, 0), new(4, 0) } };
        Assert.Null(DenseComplexLuSolver.SolveDenseOrNull(a, new[] { Complex.One, Complex.One }, 2));
    }

    [Fact]
    public void The_estimator_no_longer_substitutes_a_zero_impedance_column()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Harmonics", "HarmonicStateEstimator.cs"));
        Assert.DoesNotContain("?? new Complex[n]", src);
        Assert.DoesNotContain("DenseComplexLuSolver.SolveDense(gram", src);
    }
}
