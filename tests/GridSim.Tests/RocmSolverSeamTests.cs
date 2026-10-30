using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The native AMD ROCm/HIP solvers sit behind the same ILinearSolver/ISparseLinearSolver seams
/// as the CPU and ILGPU implementations and MUST agree with them. Like <see cref="GpuSolverSeamTests"/>
/// these are hardware-free: on a box with no ROCm (the CI/dev box) each solver's availability probe
/// returns false and it transparently defers to the CPU reference, so the tests pin the fallback
/// path; on an AMD Instinct box the same tests pin the hipSOLVER/rocSPARSE/rocBLAS kernels against
/// the CPU reference. The singular-matrix case uses an EXACTLY singular matrix (a repeated row) -
/// hipSOLVER reports that via devInfo, matching the CPU floor on both paths.
/// </summary>
public class RocmSolverSeamTests
{
    private static (double[,] a, double[] b) WellConditioned()
    {
        double[,] a =
        {
            { 10,  1,  0,  2 },
            {  1, 12,  3,  0 },
            {  0,  3,  9,  1 },
            {  2,  0,  1, 11 },
        };
        double[] b = { 13, 16, 13, 14 };
        return (a, b);
    }

    [Fact]
    public void Rocm_dense_lu_matches_cpu_dense_lu()
    {
        (double[,] a1, double[] b1) = WellConditioned();
        (double[,] a2, double[] b2) = WellConditioned();

        new DenseLuSolver().SolveInPlace(a1, b1);
        new RocmDenseLinearSolver().SolveInPlace(a2, b2);

        for (int i = 0; i < b1.Length; i++)
            Assert.Equal(b1[i], b2[i], 10);
    }

    [Fact]
    public void Rocm_dense_lu_throws_on_an_exactly_singular_matrix()
    {
        // A repeated row is exactly rank-deficient: an exact-zero pivot appears in LU. Both the CPU
        // floor and hipSOLVER's devInfo>0 singular flag reject it, so the throw holds on either path.
        double[,] a =
        {
            { 1.0, 2.0, 3.0 },
            { 1.0, 2.0, 3.0 },
            { 4.0, 5.0, 6.0 },
        };
        double[] b = { 1, 1, 1 };
        Assert.ThrowsAny<InvalidOperationException>(() => new RocmDenseLinearSolver().SolveInPlace(a, b));
    }

    [Fact]
    public void Rocm_dense_lu_rejects_mismatched_dimensions()
    {
        var a = new double[3, 3];
        var b = new double[2];
        Assert.Throws<ArgumentException>(() => new RocmDenseLinearSolver().SolveInPlace(a, b));
    }

    [Fact]
    public void Rocm_sparse_solver_matches_cpu_sparse_lu()
    {
        var builder = new CsrMatrix.Builder(4);
        builder.Add(0, 0, 10); builder.Add(0, 1, 1); builder.Add(0, 3, 2);
        builder.Add(1, 0, 1); builder.Add(1, 1, 12); builder.Add(1, 2, 3);
        builder.Add(2, 1, 3); builder.Add(2, 2, 9); builder.Add(2, 3, 1);
        builder.Add(3, 0, 2); builder.Add(3, 2, 1); builder.Add(3, 3, 11);
        CsrMatrix a = builder.Build();
        double[] b = { 13, 16, 13, 14 };

        double[]? cpu = new CpuSparseLuSolver().Solve(a, (double[])b.Clone());
        double[]? rocm = new RocmSparseLinearSolver().Solve(a, (double[])b.Clone());

        Assert.NotNull(cpu);
        Assert.NotNull(rocm);
        for (int i = 0; i < 4; i++)
            Assert.Equal(cpu![i], rocm![i], 8);
    }

    [Fact]
    public void Rocm_sparse_solver_rejects_mismatched_rhs_length()
    {
        var builder = new CsrMatrix.Builder(2);
        builder.Add(0, 0, 1); builder.Add(1, 1, 1);
        CsrMatrix a = builder.Build();
        Assert.Throws<ArgumentException>(() => new RocmSparseLinearSolver().Solve(a, new double[3]));
    }
}
