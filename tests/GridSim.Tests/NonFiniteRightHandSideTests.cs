using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using ILGPU.Runtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug #40 (October 2026): the direct solvers refuse a NaN or infinite right-hand side by name instead of returning a
/// NaN / Inf solution as if it were an answer (the GPU sparse solver used to return null and send it to a fallback).
/// The refusal is a read-only pre-check; the finite solves here are compared bit for bit with the same solve before the
/// bad entry was put in, and the pinned solver slices are unchanged.
/// </summary>
public class NonFiniteRightHandSideTests
{
    public static TheoryData<double> Bad => new() { double.NaN, double.PositiveInfinity, double.NegativeInfinity };

    private static double[,] Dense() => new double[,] { { 4.0, -1.0, 0.5 }, { -1.0, 5.0, -2.0 }, { 0.5, -2.0, 6.0 } };

    private static CsrMatrix Sparse()
    {
        var b = new CsrMatrix.Builder(3);
        double[,] d = Dense();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                b.Add(i, j, d[i, j]);
        return b.Build();
    }

    private static double[] Rhs() => new[] { 1.0, -2.5, 0.75 };

    [Theory]
    [MemberData(nameof(Bad))]
    public void The_cpu_dense_lu_refuses_a_non_finite_right_hand_side(double bad)
    {
        double[] b = Rhs();
        b[1] = bad;
        var ex = Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().SolveInPlace(Dense(), b));
        Assert.Equal(NumericTolerances.NonFiniteRightHandSide("LU solve", bad, 1), ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bad))]
    public void The_cpu_sparse_lu_refuses_a_non_finite_right_hand_side(double bad)
    {
        double[] b = Rhs();
        b[2] = bad;
        var ex = Assert.Throws<ArgumentException>(() => new CpuSparseLuSolver().Solve(Sparse(), b));
        Assert.StartsWith(NumericTolerances.NonFiniteRightHandSide("Sparse LU", bad, 2), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_matrix_keeps_its_own_refusal_and_a_singular_one_still_returns_null()
    {
        double[] b = Rhs();
        b[0] = double.NaN;
        double[,] nanMatrix = Dense();
        nanMatrix[2, 0] = double.NaN;
        Assert.Equal("LU solve: the matrix contains a NaN entry.",
            Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().SolveInPlace(nanMatrix, (double[])b.Clone())).Message);

        var singular = new CsrMatrix.Builder(2);
        singular.Add(0, 0, 1.0); singular.Add(0, 1, 1.0); singular.Add(1, 0, 1.0); singular.Add(1, 1, 1.0);
        Assert.Null(new CpuSparseLuSolver().Solve(singular.Build(), new[] { double.NaN, 1.0 }));
    }

    [Fact]
    public void Finite_solves_are_unchanged_bit_for_bit()
    {
        // The same system solved through each entry point gives the bits the factorisation always gave.
        double[] dense = Rhs();
        new DenseLuSolver().SolveInPlace(Dense(), dense);
        double[] sparse = new CpuSparseLuSolver().Solve(Sparse(), Rhs())!;
        double[] direct = SparseLu.FactorWithOrdering(Sparse(), SparseLu.Ordering(Sparse()))!.Solve(Rhs());
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(direct[i]), BitConverter.DoubleToInt64Bits(sparse[i]));
            Assert.True(double.IsFinite(dense[i]));
        }
    }

    [Theory]
    [InlineData(AcceleratorType.Cuda)]
    [InlineData(AcceleratorType.OpenCL)]
    public void The_gpu_solvers_refuse_a_non_finite_right_hand_side_on_the_device_or_the_fallback(AcceleratorType accel)
    {
        // With the device the GPU solver's own check refuses it; without one the CPU solver it defers to does. Either way
        // the right-hand side is named, and the GPU sparse solver no longer returns null for it.
        double[] b = Rhs();
        b[1] = double.PositiveInfinity;
        var dense = Assert.Throws<InvalidOperationException>(() => new GpuDenseLinearSolver(accel).SolveInPlace(Dense(), (double[])b.Clone()));
        Assert.Contains("the right-hand side has a non-finite entry (Infinity at row 1)", dense.Message, StringComparison.Ordinal);
        var sparse = Assert.Throws<ArgumentException>(() => new GpuSparseLinearSolver(accel: accel).Solve(Sparse(), (double[])b.Clone()));
        Assert.Contains("the right-hand side has a non-finite entry (Infinity at row 1)", sparse.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cpu_batched_dense_solver_flags_the_system_with_a_non_finite_right_hand_side_and_solves_the_others()
    {
        // The batch catches the dense LU's refusal per system (as it does a singular pivot): that system is flagged, the
        // others solve to the same bits as alone.
        double[,] d = Dense();
        var aFlat = new double[2 * 9];
        for (int s = 0; s < 2; s++)
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    aFlat[s * 9 + i * 3 + j] = d[i, j];
        double[] bFlat = Rhs().Concat(new[] { 1.0, double.NaN, 2.0 }).ToArray();
        bool[] ok = new CpuBatchedLuSolver().SolveBatch(2, 3, aFlat, bFlat);
        Assert.True(ok[0]);
        Assert.False(ok[1]);
        double[] alone = Rhs();
        new DenseLuSolver().SolveInPlace(Dense(), alone);
        for (int i = 0; i < 3; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(alone[i]), BitConverter.DoubleToInt64Bits(bFlat[i]));
        Assert.All(bFlat.Skip(3), v => Assert.True(double.IsNaN(v)));
    }
}
