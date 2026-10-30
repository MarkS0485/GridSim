using System;
using System.Linq;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Coordinated multi-transformer tap optimisation (<see cref="TapOptimizer"/>): two GSP transformers feed
/// load buses that sag under a flat 1.0 tap; optimising both taps together flattens the profile toward 1.0
/// pu - something the per-transformer deadband regulator does not do globally.
/// </summary>
public class TapOptimizerTests
{
    // 1 = 400 kV slack grid. T1 -> bus 2, T2 -> bus 3, both 132 kV load buses that sag through the
    // transformer reactance at a flat tap.
    private static GridModel Model() => new()
    {
        BaseMva = 100,
        Name = "tap-opt",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 400),
            new Bus(2, BusType.Pq, Pd: 85, Qd: 28, BaseKv: 132),
            new Bus(3, BusType.Pq, Pd: 60, Qd: 22, BaseKv: 132),
        },
        Generators = new[] { new Generator(1, 0, Vg: 1.0, Qmin: -500, Qmax: 500) },
        Branches = new[]
        {
            new Branch(1, 2, 0.004, 0.060, 0.0, TapRatio: 1.0, Name: "T1", IsTransformer: true),
            new Branch(1, 3, 0.004, 0.085, 0.0, TapRatio: 1.0, Name: "T2", IsTransformer: true),
        },
        Oltcs = new[]
        {
            new Oltc("T1", ControlsBus: 2, TargetVpu: 1.0, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125),
            new Oltc("T2", ControlsBus: 3, TargetVpu: 1.0, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125),
        },
    };

    private static double MaxDeviation(GridModel m)
    {
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        return r.Vm.Select(v => Math.Abs(v - 1.0)).Max();
    }

    [Fact]
    public void Flattens_the_profile_across_both_transformers()
    {
        GridModel model = Model();

        // Baseline: the load buses sag below 1.0 at a flat tap.
        double baseDev = MaxDeviation(model);
        Assert.True(baseDev > 0.02, $"the base profile should be off-target, got max dev {baseDev:F3}");

        TapSchedule schedule = TapOptimizer.Flatten(model);
        Assert.True(schedule.Solved);
        Assert.True(schedule.Improved, $"objective {schedule.ObjectiveBefore:F4} -> {schedule.ObjectiveAfter:F4} did not improve");
        Assert.Equal(2, schedule.Taps.Count);

        // Every recommendation is within the OLTC band and lands on the step grid.
        foreach (TapSetting t in schedule.Taps)
        {
            Oltc o = model.Oltcs.First(x => x.Transformer == t.Transformer);
            Assert.InRange(t.RecommendedTap, o.MinTap, o.MaxTap);
            double steps = (t.RecommendedTap - o.MinTap) / o.StepPu;
            Assert.True(Math.Abs(steps - Math.Round(steps)) < 1e-6, $"{t.Transformer} tap {t.RecommendedTap} is off the step grid");
        }

        // Applying the schedule genuinely flattens the profile.
        var branches = model.Branches.ToArray();
        for (int i = 0; i < branches.Length; i++)
        {
            TapSetting? rec = schedule.Taps.FirstOrDefault(t => t.Transformer == branches[i].Name);
            if (rec is not null) branches[i] = branches[i] with { TapRatio = rec.RecommendedTap };
        }
        GridModel tuned = Clone(model, branches, model.Oltcs);
        Assert.True(MaxDeviation(tuned) < baseDev, "the recommended taps should reduce the worst voltage deviation");
    }

    [Fact]
    public void No_oltcs_returns_the_unchanged_state()
    {
        GridModel model = Clone(Model(), Model().Branches, Array.Empty<Oltc>());
        TapSchedule schedule = TapOptimizer.Flatten(model);
        Assert.Empty(schedule.Taps);
        Assert.False(schedule.Improved);
        Assert.Equal(schedule.ObjectiveBefore, schedule.ObjectiveAfter);
    }

    private static GridModel Clone(GridModel m, System.Collections.Generic.IReadOnlyList<Branch> branches, System.Collections.Generic.IReadOnlyList<Oltc> oltcs) => new()
    {
        BaseMva = m.BaseMva, Name = m.Name, Buses = m.Buses, Generators = m.Generators,
        Branches = branches, Oltcs = oltcs, FrequencyHz = m.FrequencyHz,
    };
}
