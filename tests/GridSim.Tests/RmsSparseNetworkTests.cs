using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The sparse full-network RMS path (<see cref="SparseRmsNetwork"/>: machines as Norton sources behind X'',
/// factored per stage) must reproduce the dense Kron-reduced path - the network is linear, so they are the same
/// model. Pinned on WSCC-9 for a fault-and-clear run (machine-only and with a converter) and for the critical
/// clearing time.
/// </summary>
public class RmsSparseNetworkTests
{
    private static readonly Dictionary<int, double> Xdp = new() { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    private static (GridModel M, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Case(bool battery)
    {
        GridModel b = IeeeCases.Case9();
        var m = new GridModel
        {
            BaseMva = b.BaseMva, Buses = b.Buses, Branches = b.Branches,
            Generators = b.Generators.Select(g => battery && g.Bus == 3 ? g with { Type = "battery" } : g).ToList(),
        };
        var data = m.Generators.Select(g =>
        {
            double x = Xdp[g.Bus];
            return new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, 3 * x, x, 0.8 * x, 2.5 * x, 1.5 * x, 0.8 * x,
                8.0, 0.03, 0.4, 0.05);
        }).ToList();
        return (m, new NewtonRaphsonPowerFlow().Solve(m), data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sparse_network_run_matches_the_dense_kron_run(bool battery)
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case(battery);
        // N271: the dense Kron run is the test oracle (was sparseNetwork: false); the production run is sparse (was
        // sparseNetwork: true). The comparison and its tolerance are unchanged.
        MultiMachineSubtransient.Result dense;
        using (SubtransientInit.DenseKronReferenceScope())
            dense = RmsTransientStability.Study(m, pf, data, faultBusId: 7,
                onsetSeconds: 0.1, clearingSeconds: 0.08, stepSeconds: 0.002, durationSeconds: 2.0);
        MultiMachineSubtransient.Result sparse = RmsTransientStability.Study(m, pf, data, faultBusId: 7,
            onsetSeconds: 0.1, clearingSeconds: 0.08, stepSeconds: 0.002, durationSeconds: 2.0);

        Assert.Equal(dense.Machines.Count, sparse.Machines.Count);
        for (int i = 0; i < dense.Machines.Count; i++)
            for (int k = 0; k < dense.Machines[i].DeltaRad.Count; k++)
                Assert.True(Math.Abs(dense.Machines[i].DeltaRad[k] - sparse.Machines[i].DeltaRad[k]) < 1e-6,
                    $"machine {i} step {k}: dense {dense.Machines[i].DeltaRad[k]} sparse {sparse.Machines[i].DeltaRad[k]}");
        if (battery)
        {
            RmsConverterTrace cd = Assert.Single(dense.Converters!), cs = Assert.Single(sparse.Converters!);
            for (int k = 0; k < cd.TerminalVoltagePu.Count; k++)
                Assert.True(Math.Abs(cd.TerminalVoltagePu[k] - cs.TerminalVoltagePu[k]) < 1e-6, $"step {k}");
        }
    }

    [Fact]
    public void Sparse_critical_clearing_time_matches_dense()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case(battery: true);
        double dense;   // N271: dense Kron test oracle (was sparseNetwork: false); production is sparse
        using (SubtransientInit.DenseKronReferenceScope()) dense = RmsTransientStability.CriticalClearingTimeSeconds(m, pf, data, 7);
        double sparse = RmsTransientStability.CriticalClearingTimeSeconds(m, pf, data, 7);
        Assert.True(Math.Abs(dense - sparse) < 0.005, $"dense {dense:F4} s vs sparse {sparse:F4} s");
    }
}
