using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Gda;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Storage carried through the live loop (<see cref="LiveSim"/>) and the historical replay
/// (<see cref="GdaReplayLoop"/>) via the shared <see cref="StorageFleet"/>: state persists tick to tick, the
/// frames report it, the network sees it, and a replay with hypothetical storage is tagged Counterfactual.
/// </summary>
public class StorageLoopTests
{
    private static StorageFleet Fleet(GridModel m, IStorageDispatchPolicy policy, double soc = 0.5) => new(new[]
    {
        new StorageSite("BESS", 59, 100, PackModel.Sized(BatteryChemistryLibrary.Lfp, 200e6, 1000, soc, ratedPowerW: 100e6), policy),
    });

    [Fact]
    public void Live_loop_carries_storage_state_across_a_day()
    {
        GridModel m = IeeeCases.Case118();
        double peak = m.Buses.Sum(b => b.Pd);
        var sim = new LiveSim(m, storage: Fleet(m, new PeakShavePolicy(0.9 * peak, 0.7 * peak)));
        var frames = new List<SimFrame>();
        for (int t = 0; t < 72; t++) frames.Add(sim.Step(t, 20));            // 24 h of 20-minute ticks

        Assert.All(frames, f => Assert.True(f.Result.Converged));
        Assert.All(frames, f => Assert.Single(f.Storage!));
        double[] soc = frames.Select(f => f.Storage![0].Soc).ToArray();
        Assert.True(soc.Max() - soc.Min() > 0.2, "the battery should cycle over the day");
        // State persists: each tick starts where the last ended (SoC moves by at most one tick's worth).
        for (int i = 1; i < soc.Length; i++) Assert.True(Math.Abs(soc[i] - soc[i - 1]) < 100.0 / 200.0 / 3.0 + 0.01);
        // The network carries it: the solved model includes the storage generator at its delivered output.
        SimFrame busy = frames.OrderByDescending(f => Math.Abs(f.Storage![0].DeliveredMw)).First();
        Assert.Contains(busy.Model.Generators, g => g.Type == "storage" && Math.Abs(g.Pg - busy.Storage![0].DeliveredMw) < 1e-9);
    }

    [Fact]
    public void Live_loop_without_storage_is_unchanged()
    {
        GridModel m = IeeeCases.Case118();
        var plain = new LiveSim(m);
        var idle = new LiveSim(m, storage: null);
        for (int t = 0; t < 5; t++)
        {
            SimFrame a = plain.Step(t, 20), b = idle.Step(t, 20);
            Assert.Null(a.Storage);
            Assert.Equal(a.Result.Vm, b.Result.Vm);
        }
    }

    [Fact]
    public void Replay_with_hypothetical_storage_is_counterfactual()
    {
        GridModel m = IeeeCases.Case118();
        DateTime t0 = new(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var series = new GdaTimeSeries("day", Enumerable.Range(0, 48)
            .Select(i => new GdaTimeStep(t0.AddMinutes(30 * i), LoadProfile.DailyFactor(i * 0.5))).ToList());
        DateTime now = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double peak = m.Buses.Sum(b => b.Pd);

        GdaReplayResult plain = GdaReplayLoop.Replay(m, series, utcNow: now);
        GdaReplayResult withBess = GdaReplayLoop.Replay(m, series, utcNow: now,
            storage: Fleet(m, new PeakShavePolicy(0.9 * peak, 0.7 * peak)));

        Assert.Equal(ProvenanceTag.DerivedFromRecorded, plain.Provenance);
        Assert.Equal(ProvenanceTag.Counterfactual, withBess.Provenance);
        Assert.Equal(48, withBess.StorageSteps!.Count);
        Assert.True(withBess.StorageSummaries![0].EnergyOutMwh > 10);
        Assert.Equal(0, withBess.NonConverged);
    }
}
