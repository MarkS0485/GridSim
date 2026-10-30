using System;
using System.Collections.Generic;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-389: the parallel N-1 branch screen gives exactly the sequential screen's results, bit for bit, in branch order,
/// whatever the degree of parallelism (each worker has its own solver; results are written by index).
/// </summary>
public sealed class ContingencyParallelBitIdentityTests
{
    private static void AssertBitIdentical(IReadOnlyList<ContingencyResult> a, IReadOnlyList<ContingencyResult> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Outage, b[i].Outage);
            Assert.Equal(a[i].Converged, b[i].Converged);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].MaxBranchLoadingPercent), BitConverter.DoubleToInt64Bits(b[i].MaxBranchLoadingPercent));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].WorstBoundaryLoadingPercent), BitConverter.DoubleToInt64Bits(b[i].WorstBoundaryLoadingPercent));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].MinVm), BitConverter.DoubleToInt64Bits(b[i].MinVm));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].MaxVm), BitConverter.DoubleToInt64Bits(b[i].MaxVm));
            Assert.Equal(a[i].Violations, b[i].Violations);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void The_parallel_screen_equals_the_sequential_screen_bit_for_bit(int degree)
    {
        GridModel m = IeeeCases.Case30();
        IReadOnlyList<ContingencyResult> sequential = ContingencyAnalysis.ScreenBranches(m);
        IReadOnlyList<ContingencyResult> parallel = ContingencyAnalysis.ScreenBranchesParallel(m, maxDegreeOfParallelism: degree);
        AssertBitIdentical(sequential, parallel);
    }

    [Fact]
    public void Two_parallel_runs_are_bit_identical()
    {
        GridModel m = IeeeCases.Case57();
        AssertBitIdentical(ContingencyAnalysis.ScreenBranchesParallel(m), ContingencyAnalysis.ScreenBranchesParallel(m));
    }
}
