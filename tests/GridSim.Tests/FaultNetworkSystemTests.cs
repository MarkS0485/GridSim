using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The fault studies factor their admittance matrix on the sparse engine at every size (N233, October 2026; until then
/// densely up to 1,500 buses and sparse above). The dense LU survives as a test oracle
/// (<see cref="FaultNetworkSystem.DenseReferenceScope"/>), an independent algorithm: these cross-check the Zbus columns,
/// the transposed (row) solve and a triplet-assembled network, dense oracle vs the sparse engine, on IEEE cases.
/// Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the dense side used to be the default path and the sparse
/// side was forced with forceSparse: true; now the sparse side is the default and the dense side is taken under
/// DenseReferenceScope. The comparison, its tolerance (1e-9 of the column scale) and the cases are unchanged.
/// </summary>
public class FaultNetworkSystemTests
{
    private static List<(int, Complex)> MachineShunts(GridModel m)
    {
        var index = new Dictionary<int, int>();
        for (int i = 0; i < m.BusCount; i++) index[m.Buses[i].Id] = i;
        var shunts = new List<(int, Complex)>();
        foreach (Generator g in m.Generators)
            shunts.Add((index[g.Bus], Complex.One / new Complex(0, 0.2 * m.BaseMva / (g.MBase <= 0 ? m.BaseMva : g.MBase))));
        return shunts;
    }

    private static void AssertClose(Complex[] a, Complex[] b)
    {
        double scale = 0;
        foreach (Complex z in a) scale = Math.Max(scale, z.Magnitude);
        for (int i = 0; i < a.Length; i++)
            Assert.True((a[i] - b[i]).Magnitude <= 1e-9 * scale, $"entry {i}: dense {a[i]} vs sparse {b[i]}");
    }

    [Theory]
    [InlineData(118)]
    [InlineData(300)]
    public void Sparse_zbus_columns_and_rows_match_dense(int caseSize)
    {
        GridModel m = caseSize == 118 ? IeeeCases.Case118() : IeeeCases.Case300();
        List<(int, Complex)> shunts = MachineShunts(m);
        FaultNetworkSystem dense;
        using (FaultNetworkSystem.DenseReferenceScope()) dense = FaultNetworkSystem.ForModel(m, shunts);
        FaultNetworkSystem sparse = FaultNetworkSystem.ForModel(m, shunts);
        Assert.False(dense.IsSparse);
        Assert.True(sparse.IsSparse);

        FaultNetworkSystem denseT = dense.Transpose(), sparseT = sparse.Transpose();
        foreach (int k in new[] { 0, m.BusCount / 3, m.BusCount - 1 })
        {
            var e = new Complex[m.BusCount];
            e[k] = Complex.One;
            AssertClose(dense.Solve(e), sparse.Solve(e));
            AssertClose(denseT.Solve(e), sparseT.Solve(e));
        }
    }

    [Fact]
    public void Sparse_triplet_network_matches_dense()
    {
        // A grounded ladder: series admittances between neighbours plus an earth shunt at every third bus.
        const int n = 40;
        var trip = new List<(int, int, Complex)>();
        for (int i = 0; i + 1 < n; i++)
        {
            Complex y = Complex.One / new Complex(0.01 * (i % 5 + 1), 0.1 + 0.01 * i);
            trip.Add((i, i, y)); trip.Add((i + 1, i + 1, y)); trip.Add((i, i + 1, -y)); trip.Add((i + 1, i, -y));
            if (i % 3 == 0) trip.Add((i, i, Complex.One / new Complex(0, 0.5)));
        }
        var e = new Complex[n];
        e[n / 2] = Complex.One;
        FaultNetworkSystem dense;
        using (FaultNetworkSystem.DenseReferenceScope()) dense = FaultNetworkSystem.FromTriplets(n, trip);
        FaultNetworkSystem sparse = FaultNetworkSystem.FromTriplets(n, trip);
        Assert.False(dense.IsSparse);
        Assert.True(sparse.IsSparse);
        AssertClose(dense.Solve(e), sparse.Solve(e));
    }
}
