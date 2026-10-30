using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R071 (CF4, October 2026): the batched linear solver seam is wired into the contingency sweep
/// (<see cref="ContingencyAnalysis.ScreenBranchesBatched"/>). Each lane runs an ordinary Newton solve of its outage and
/// posts every step to a hub that solves the whole lane set's steps in one batched call. With the CPU reference backend
/// (<see cref="CpuBatchedSparseLuSolver"/>, the canonical sparse LU per system) the batched sweep must give EXACTLY the
/// bits of the one-at-a-time sweep: these tests compare every field of every outage result, doubles by their bit
/// patterns, on case9, case14 and case118.
/// </summary>
public sealed class Cf4BatchedContingencySweepTests
{
    public static TheoryData<string> Cases => new() { "case9", "case14", "case118" };

    private static GridModel Load(string name) => name switch
    {
        "case9" => IeeeCases.Case9(),
        "case14" => IeeeCases.Case14(),
        "case118" => IeeeCases.Case118(),
        _ => throw new ArgumentException(name),
    };

    private static void SameBits(double expected, double actual, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R} (bits differ)");

    /// <summary>Every field of two outage results, doubles bit for bit.</summary>
    private static void AssertSameResult(ContingencyResult e, ContingencyResult a)
    {
        string at = e.Outage;
        Assert.Equal(e.Outage, a.Outage);
        Assert.Equal(e.Converged, a.Converged);
        SameBits(e.MaxBranchLoadingPercent, a.MaxBranchLoadingPercent, at + " max loading");
        SameBits(e.WorstBoundaryLoadingPercent, a.WorstBoundaryLoadingPercent, at + " worst boundary");
        SameBits(e.MinVm, a.MinVm, at + " min Vm");
        SameBits(e.MaxVm, a.MaxVm, at + " max Vm");
        Assert.Equal(e.Violations, a.Violations);
        Assert.Equal(e.Details.Count, a.Details.Count);
        for (int i = 0; i < e.Details.Count; i++)
        {
            Assert.Equal(e.Details[i].Kind, a.Details[i].Kind);
            Assert.Equal(e.Details[i].Element, a.Details[i].Element);
            Assert.Equal(e.Details[i].Name, a.Details[i].Name);
            SameBits(e.Details[i].Value, a.Details[i].Value, at + " violation value");
        }
        Assert.Equal(e.NettedAgainstBase, a.NettedAgainstBase);
        Assert.Equal(e.PreExistingViolations, a.PreExistingViolations);
        Assert.Equal(e.Islanded, a.Islanded);
        Assert.Equal(e.Kind, a.Kind);
        SameBits(e.LostLoadMw, a.LostLoadMw, at + " lost load");
        SameBits(e.LostGenerationMw, a.LostGenerationMw, at + " lost generation");
        Assert.Equal(e.Note, a.Note);
    }

    private static void AssertSameSweep(IReadOnlyList<ContingencyResult> expected, IReadOnlyList<ContingencyResult> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int k = 0; k < expected.Count; k++) AssertSameResult(expected[k], actual[k]);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void R071_batched_study_sweep_is_bit_identical_to_the_sequential_sweep(string name)
    {
        GridModel m = Load(name);
        IReadOnlyList<ContingencyResult> sequential =
            ContingencyAnalysis.ScreenBranches(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        IReadOnlyList<ContingencyResult> batched =
            ContingencyAnalysis.ScreenBranchesBatched(m, options: ContingencyAnalysis.StudyOptions);
        Assert.Equal(m.Branches.Count, batched.Count);
        AssertSameSweep(sequential, batched);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void R071_batched_study_sweep_is_bit_identical_to_the_parallel_sweep(string name)
    {
        GridModel m = Load(name);
        IReadOnlyList<ContingencyResult> parallel = ContingencyAnalysis.ScreenBranchesParallel(
            m, ContingencyAnalysis.StudySolverFactory(m), ContingencyAnalysis.StudyOptions);
        IReadOnlyList<ContingencyResult> batched = ContingencyAnalysis.ScreenBranchesBatched(
            m, new CpuBatchedSparseLuSolver(), ContingencyAnalysis.StudyOptions, lanes: 3);
        AssertSameSweep(parallel, batched);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void R071_batched_bare_engine_sweep_is_bit_identical_to_the_default_screen(string name)
    {
        GridModel m = Load(name);
        IReadOnlyList<ContingencyResult> sequential = ContingencyAnalysis.ScreenBranches(m);
        IReadOnlyList<ContingencyResult> batched = ContingencyAnalysis.ScreenBranchesBatched(m, oltcLoop: false);
        AssertSameSweep(sequential, batched);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(64)]
    public void R071_the_lane_count_changes_no_bit(int lanes)
    {
        // Which outages share a batch depends on the lane count; the systems never interact, so no number may move.
        GridModel m = IeeeCases.Case14();
        IReadOnlyList<ContingencyResult> reference =
            ContingencyAnalysis.ScreenBranches(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        AssertSameSweep(reference,
            ContingencyAnalysis.ScreenBranchesBatched(m, options: ContingencyAnalysis.StudyOptions, lanes: lanes));
    }

    [Fact]
    public void R071_a_branch_subset_matches_the_parallel_subset_bit_for_bit()
    {
        GridModel m = IeeeCases.Case118();
        int[] subset = { 0, 7, 8, 50, 100, m.Branches.Count - 1 };
        AssertSameSweep(
            ContingencyAnalysis.ScreenBranchesParallel(m, ContingencyAnalysis.StudySolverFactory(m),
                ContingencyAnalysis.StudyOptions, branchIndices: subset),
            ContingencyAnalysis.ScreenBranchesBatched(m, options: ContingencyAnalysis.StudyOptions, branchIndices: subset));
    }

    /// <summary>Records every batch the sweep hands the backend, and delegates to the CPU reference.</summary>
    private sealed class CountingBackend : IBatchedSparseLinearSolver
    {
        private readonly CpuBatchedSparseLuSolver _inner = new();
        public List<int> BatchSizes { get; } = new();
        public string Name => "counting";

        public double[]?[] SolveBatch(IReadOnlyList<CsrMatrix> matrices, IReadOnlyList<double[]> rhs, Exception?[] errors)
        {
            lock (BatchSizes) BatchSizes.Add(matrices.Count);
            return _inner.SolveBatch(matrices, rhs, errors);
        }
    }

    [Fact]
    public void R071_the_steps_really_go_through_the_batched_backend_in_batches()
    {
        // Four lanes on case14 (20 branches): the first phase cannot complete until all four lanes have posted their
        // first step, so the widest batch is exactly four systems; and every step went through the backend.
        GridModel m = IeeeCases.Case14();
        var backend = new CountingBackend();
        IReadOnlyList<ContingencyResult> r =
            ContingencyAnalysis.ScreenBranchesBatched(m, backend, ContingencyAnalysis.StudyOptions, lanes: 4);
        Assert.Equal(m.Branches.Count, r.Count);
        Assert.NotEmpty(backend.BatchSizes);
        Assert.Equal(4, backend.BatchSizes.Max());
        Assert.True(backend.BatchSizes.Sum() >= m.Branches.Count, "at least one Newton step per outage");
    }

    private sealed class ThrowingBackend : IBatchedSparseLinearSolver
    {
        public string Name => "broken-backend";
        public double[]?[] SolveBatch(IReadOnlyList<CsrMatrix> matrices, IReadOnlyList<double[]> rhs, Exception?[] errors)
            => throw new NotSupportedException("device lost");
    }

    private sealed class WrongCountBackend : IBatchedSparseLinearSolver
    {
        public string Name => "wrong-count-backend";
        public double[]?[] SolveBatch(IReadOnlyList<CsrMatrix> matrices, IReadOnlyList<double[]> rhs, Exception?[] errors)
            => new double[]?[matrices.Count + 1];
    }

    private sealed class WrongLengthBackend : IBatchedSparseLinearSolver
    {
        public string Name => "wrong-length-backend";
        public double[]?[] SolveBatch(IReadOnlyList<CsrMatrix> matrices, IReadOnlyList<double[]> rhs, Exception?[] errors)
            => matrices.Select(a => (double[]?)new double[a.N + 1]).ToArray();
    }

    [Fact]
    public void R071_a_backend_that_throws_stops_the_sweep_loud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ContingencyAnalysis.ScreenBranchesBatched(IeeeCases.Case9(), new ThrowingBackend(), lanes: 2));
        Assert.Contains("broken-backend", ex.Message);
        Assert.Contains("device lost", ex.Message);
    }

    [Fact]
    public void R071_a_backend_that_answers_the_wrong_count_stops_the_sweep_loud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ContingencyAnalysis.ScreenBranchesBatched(IeeeCases.Case9(), new WrongCountBackend(), lanes: 2));
        Assert.Contains("wrong-count-backend", ex.Message);
    }

    [Fact]
    public void R071_a_backend_that_answers_the_wrong_length_stops_the_sweep_loud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ContingencyAnalysis.ScreenBranchesBatched(IeeeCases.Case9(), new WrongLengthBackend(), lanes: 2));
        Assert.Contains("wrong-length-backend", ex.Message);
    }

    [Fact]
    public void R071_a_cancelled_sweep_reads_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ContingencyAnalysis.ScreenBranchesBatched(IeeeCases.Case14(), lanes: 3, ct: cts.Token));
    }

    [Fact]
    public void R071_bad_arguments_are_refused_by_name()
    {
        GridModel m = IeeeCases.Case9();
        Assert.Throws<ArgumentNullException>(() => ContingencyAnalysis.ScreenBranchesBatched(null!));
        var lanes = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContingencyAnalysis.ScreenBranchesBatched(m, lanes: ContingencyAnalysis.MaxBatchedLanes + 1));
        Assert.Equal("lanes", lanes.ParamName);
        var idx = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContingencyAnalysis.ScreenBranchesBatched(m, branchIndices: new[] { m.Branches.Count }));
        Assert.Equal("branchIndices", idx.ParamName);
        Assert.Empty(ContingencyAnalysis.ScreenBranchesBatched(m, branchIndices: Array.Empty<int>()));
    }

    // ---- The backends on their own -------------------------------------------------------------------------------

    private static CsrMatrix Tridiagonal(int n, double diag, double off)
    {
        var b = new CsrMatrix.Builder(n);
        for (int i = 0; i < n; i++)
        {
            b.Add(i, i, diag + 0.125 * i);
            if (i > 0) b.Add(i, i - 1, off);
            if (i + 1 < n) b.Add(i, i + 1, off * 0.5);
        }
        return b.Build();
    }

    private static double[] Rhs(int n) => Enumerable.Range(0, n).Select(i => 1.0 + 0.25 * i).ToArray();

    [Fact]
    public void R071_cpu_batched_sparse_equals_the_canonical_step_solver_bit_for_bit()
    {
        CsrMatrix[] a = { Tridiagonal(5, 4.0, -1.0), Tridiagonal(9, 6.0, -2.0), Tridiagonal(3, 3.0, 0.5) };
        double[][] b = { Rhs(5), Rhs(9), Rhs(3) };
        var errors = new Exception?[3];
        double[]?[] x = new CpuBatchedSparseLuSolver().SolveBatch(a, b, errors);
        for (int s = 0; s < 3; s++)
        {
            Assert.Null(errors[s]);
            double[] expected = new CpuSparseLuSolver().Solve(a[s], b[s])!;
            Assert.Equal(expected.Length, x[s]!.Length);
            for (int i = 0; i < expected.Length; i++) SameBits(expected[i], x[s]![i], $"system {s} entry {i}");
        }
    }

    [Fact]
    public void R071_a_failing_system_is_handed_back_and_the_others_still_solve()
    {
        // A NaN entry makes the sparse LU throw (it refuses the matrix by name); the batch must hand that exception
        // back for that system alone, exactly as a one-at-a-time solve would have thrown it.
        var bad = new CsrMatrix.Builder(2);
        bad.Add(0, 0, double.NaN); bad.Add(1, 1, 1.0);
        CsrMatrix[] a = { Tridiagonal(4, 4.0, -1.0), bad.Build() };
        double[][] b = { Rhs(4), Rhs(2) };
        var errors = new Exception?[2];
        double[]?[] x = new CpuBatchedSparseLuSolver().SolveBatch(a, b, errors);
        Assert.Null(errors[0]);
        Assert.NotNull(x[0]);
        Assert.IsType<ArgumentException>(errors[1]);
        Assert.Null(x[1]);
        var direct = Assert.Throws<ArgumentException>(() => new CpuSparseLuSolver().Solve(a[1], b[1]));
        Assert.Equal(direct.Message, errors[1]!.Message);
    }

    [Fact]
    public void R071_malformed_batches_are_refused_by_name()
    {
        var cpu = new CpuBatchedSparseLuSolver();
        CsrMatrix[] a = { Tridiagonal(3, 3.0, 0.5) };
        Assert.Throws<ArgumentException>(() => cpu.SolveBatch(a, Array.Empty<double[]>(), new Exception?[1]));
        Assert.Throws<ArgumentException>(() => cpu.SolveBatch(a, new[] { Rhs(3) }, new Exception?[2]));
        var len = Assert.Throws<ArgumentException>(() => cpu.SolveBatch(a, new[] { Rhs(4) }, new Exception?[1]));
        Assert.Contains("right-hand side has 4 entries", len.Message);
        Assert.Empty(cpu.SolveBatch(Array.Empty<CsrMatrix>(), Array.Empty<double[]>(), Array.Empty<Exception?>()));
    }

    [Fact]
    public void R071_the_dense_adapter_groups_by_size_and_matches_the_dense_batched_lu()
    {
        // Mixed sizes in one batch: the adapter must solve each with the dense seam and return them in batch order.
        CsrMatrix[] a = { Tridiagonal(4, 4.0, -1.0), Tridiagonal(6, 5.0, -1.5), Tridiagonal(4, 7.0, 2.0) };
        double[][] b = { Rhs(4), Rhs(6), Rhs(4) };
        var errors = new Exception?[3];
        double[]?[] x = new DenseBatchSparseAdapter(new CpuBatchedLuSolver()).SolveBatch(a, b, errors);
        for (int s = 0; s < 3; s++)
        {
            Assert.Null(errors[s]);
            int n = a[s].N;
            var aFlat = new double[n * n];
            for (int i = 0; i < n; i++)
                for (int k = a[s].RowPtr[i]; k < a[s].RowPtr[i + 1]; k++) aFlat[i * n + a[s].ColIdx[k]] += a[s].Val[k];
            var bFlat = (double[])b[s].Clone();
            Assert.True(new CpuBatchedLuSolver().SolveBatch(1, n, aFlat, bFlat)[0]);
            for (int i = 0; i < n; i++) SameBits(bFlat[i], x[s]![i], $"system {s} entry {i}");
        }
        // The caller's right-hand sides are untouched.
        Assert.Equal(Rhs(4), b[0]);
    }

    [Fact]
    public void R071_the_dense_adapter_reports_a_singular_system_as_null()
    {
        var sing = new CsrMatrix.Builder(2);
        sing.Add(0, 0, 1.0); sing.Add(0, 1, 2.0); sing.Add(1, 0, 2.0); sing.Add(1, 1, 4.0);
        var errors = new Exception?[1];
        double[]?[] x = new DenseBatchSparseAdapter(new CpuBatchedLuSolver())
            .SolveBatch(new[] { sing.Build() }, new[] { new[] { 1.0, 1.0 } }, errors);
        Assert.Null(x[0]);
        Assert.Null(errors[0]);
    }
}
