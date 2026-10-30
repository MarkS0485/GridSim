using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The frequency-dependent admittance Y(h): it reduces EXACTLY to the fundamental YBus at h=1 (the grounded
/// baseline), scales branch reactance and line charging with the harmonic order, and produces a complex
/// system the complex-solver seam solves consistently across backends.
/// </summary>
public class FrequencyDependentYBusTests
{
    private static Complex Cell(ComplexCsr y, int i, int j)
    {
        for (int k = y.RowPtr[i]; k < y.RowPtr[i + 1]; k++)
            if (y.ColIdx[k] == j) return new Complex(y.G[k], y.B[k]);
        return Complex.Zero;
    }

    [Fact]
    public void Y_of_order_one_reduces_to_the_fundamental_ybus()
    {
        GridModel model = IeeeCases.Case9();
        ComplexCsr yh = FrequencyDependentYBus.Build(model, 1.0);   // default options: no skin, no load shunts
        SparseYBus yb = YBus.BuildSparse(model);

        // Collect the nonzero cells of Y(1) and compare to the fundamental YBus cells (to machine precision -
        // the only gap is duplicate-summation order in the sparse builder).
        var cells = new Dictionary<(int, int), Complex>();
        for (int i = 0; i < yh.N; i++)
            for (int k = yh.RowPtr[i]; k < yh.RowPtr[i + 1]; k++)
            {
                var v = new Complex(yh.G[k], yh.B[k]);
                if (v != Complex.Zero) cells[(i, yh.ColIdx[k])] = v;
            }

        Assert.Equal(yb.Cells.Count, cells.Count);
        foreach (KeyValuePair<(int Row, int Col), Complex> kv in yb.Cells)
        {
            Assert.True(cells.TryGetValue(kv.Key, out Complex v), $"missing cell {kv.Key}");
            Assert.True((v - kv.Value).Magnitude < 1e-9, $"cell {kv.Key}: {v} vs {kv.Value}");
        }
    }

    [Fact]
    public void Series_reactance_and_charging_scale_with_the_harmonic_order()
    {
        // One branch, R + jX, with line charging B. Off-diagonal series admittance is -1/(R + j h X).
        const double r = 0.02, x = 0.10, b = 0.30;
        var model = new GridModel
        {
            BaseMva = 100,
            FrequencyHz = 50,
            Buses = new[] { new Bus(0, BusType.Slack), new Bus(1, BusType.Pq) },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(0, 1, r, x, b) },
        };

        foreach (double h in new[] { 1.0, 5.0, 11.0 })
        {
            ComplexCsr y = FrequencyDependentYBus.Build(model, h);
            Complex expectedSeries = -(Complex.One / new Complex(r, h * x));   // off-diagonal
            Assert.True((Cell(y, 0, 1) - expectedSeries).Magnitude < 1e-12, $"h={h} off-diagonal");

            // Diagonal = series + half charging (scaled with h).
            Complex expectedDiag = Complex.One / new Complex(r, h * x) + new Complex(0, (b * h) / 2.0);
            Assert.True((Cell(y, 0, 0) - expectedDiag).Magnitude < 1e-12, $"h={h} diagonal");
        }
    }

    [Fact]
    public void A_real_Yh_solves_consistently_across_the_direct_complex_solvers()
    {
        GridModel model = IeeeCases.Case9();
        ComplexCsr y = FrequencyDependentYBus.Build(model, 5.0, new HarmonicModelOptions { IncludeLoadShunts = true });

        // A fixed complex injection vector.
        var b = new Complex[y.N];
        for (int i = 0; i < y.N; i++) b[i] = new Complex(0.1 * (i + 1), -0.05 * (i + 1));

        Complex[]? dense = new DenseComplexLuSolver().Solve(y, b);
        Complex[]? real = new RealExpandedComplexSolver().Solve(y, b);
        Assert.NotNull(dense);
        Assert.NotNull(real);
        for (int i = 0; i < y.N; i++)
            Assert.True((dense![i] - real![i]).Magnitude < 1e-7, $"row {i}: {dense[i]} vs {real![i]}");
    }
}
