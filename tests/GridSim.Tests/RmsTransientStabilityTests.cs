using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the RMS transient-stability study (<see cref="RmsTransientStability"/>) on WSCC-9:
///   - REDUCTION TO THE CLASSICAL CCT: with every reactance collapsed to X'd the subtransient critical
///     clearing time matches the trusted <see cref="ClassicalInit.CriticalClearingTimeSeconds"/>;
///   - STABILITY BOUNDARY: a fault cleared well inside the CCT keeps synchronism, one cleared well beyond it
///     does not.
/// </summary>
public class RmsTransientStabilityTests
{
    private static readonly Dictionary<int, double> XdpByBus = new() { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    private static double[] Xdp(GridModel model)
    {
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = XdpByBus[model.Generators[i].Bus];
        return xdp;
    }

    // Flat subtransient data: every reactance equal to the machine's X'd (so it reduces to the classical model).
    private static IReadOnlyList<SubtransientInit.MachineData> FlatData(GridModel model)
    {
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
        {
            double x = XdpByBus[gen.Bus];
            data.Add(new SubtransientInit.MachineData(
                H: gen.H, Damping: 0.0, Xd: x, Xdp: x, Xdpp: x, Xq: x, Xqp: x, Xqpp: x,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05));
        }
        return data;
    }

    // ---- The subtransient CCT reduces to the classical CCT with flat reactances ----
    [Fact]
    public void Subtransient_critical_clearing_time_reduces_to_the_classical()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);

        double clsCct = ClassicalInit.CriticalClearingTimeSeconds(model, pf, Xdp(model), faultBusId: 7,
            f0: 50.0, stepSeconds: 0.002, maxSeconds: 1.0);
        double subCct = RmsTransientStability.CriticalClearingTimeSeconds(model, pf, FlatData(model), faultBusId: 7,
            f0: 50.0, stepSeconds: 0.002, maxSeconds: 1.0);

        Assert.True(clsCct > 0.0 && clsCct < 1.0, $"classical CCT {clsCct:F3}s should be interior");
        Assert.True(Math.Abs(clsCct - subCct) < 0.02, $"subtransient CCT {subCct:F3}s vs classical {clsCct:F3}s");
    }

    // ---- Stability boundary: short clear survives, long clear does not ----
    [Fact]
    public void A_short_fault_survives_and_a_long_fault_does_not()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        IReadOnlyList<SubtransientInit.MachineData> data = FlatData(model);

        double cct = RmsTransientStability.CriticalClearingTimeSeconds(model, pf, data, faultBusId: 7,
            f0: 50.0, stepSeconds: 0.002, maxSeconds: 1.0);

        MultiMachineSubtransient.Result stable = RmsTransientStability.Study(
            model, pf, data, faultBusId: 7, onsetSeconds: 0.1, clearingSeconds: 0.5 * cct,
            f0: 50.0, stepSeconds: 0.002, durationSeconds: 6.0);
        MultiMachineSubtransient.Result unstable = RmsTransientStability.Study(
            model, pf, data, faultBusId: 7, onsetSeconds: 0.1, clearingSeconds: Math.Min(1.0, 2.0 * cct),
            f0: 50.0, stepSeconds: 0.002, durationSeconds: 6.0);

        Assert.True(stable.StayedInSynchronism);
        Assert.False(unstable.StayedInSynchronism);
    }
}
