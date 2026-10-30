using GridSim.Core.Dispatch;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the OLTC outer-loop refactor: name->index map computed once, vmByBus
/// refilled in place, and the branch-list copy deferred until a tap actually moves. This is a pure
/// structural refactor - the tap arithmetic and pass sequence are unchanged - so the production solver
/// must match a faithful replica of the ORIGINAL loop BIT-FOR-BIT: same settled taps, same OltcPasses,
/// same solved voltages.
/// </summary>
public class OltcLoopEquivalencePinTests
{
    // A regulating transformer T1 from the slack into a loaded bus whose voltage starts below target, so
    // the OLTC must step its tap over several passes until the controlled bus enters the deadband.
    private static GridModel BuildOltcModel() => new()
    {
        BaseMva = 100,
        Name = "oltc-pin",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0),
            new Bus(2, BusType.Pq, Pd: 80, Qd: 30),
            new Bus(3, BusType.Pq, Pd: 40, Qd: 15),
        },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[]
        {
            new Branch(1, 2, 0.008, 0.08, Name: "T1", TapRatio: 1.0),
            new Branch(2, 3, 0.02, 0.10, Name: "L23"),
        },
        Oltcs = new[] { new Oltc("T1", ControlsBus: 2, TargetVpu: 1.02, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125, DeadbandPu: 0.01) },
    };

    [Fact]
    public void Refactored_loop_matches_the_original_bit_for_bit()
    {
        GridModel model = BuildOltcModel();
        var opt = new PowerFlowOptions { MaxIterations = 60, EnforceQLimits = false };

        // Production path.
        var solver = new SteadyStateSolver();
        PowerFlowResult prod = solver.Solve(model, opt, maxOltcPasses: 25);

        // Reference: the original loop, verbatim.
        (GridModel refModel, PowerFlowResult refResult, int refPasses) = OriginalLoop(model, opt, 25);

        Assert.Equal(refPasses, solver.OltcPasses);
        Assert.True(solver.OltcPasses > 1, "the scenario should take several OLTC passes to settle");

        // Settled taps bit-for-bit.
        for (int i = 0; i < refModel.Branches.Count; i++)
            Assert.Equal(
                System.BitConverter.DoubleToInt64Bits(refModel.Branches[i].TapRatio),
                System.BitConverter.DoubleToInt64Bits(solver.RegulatedModel.Branches[i].TapRatio));

        // Solved voltages bit-for-bit.
        for (int i = 0; i < refResult.BusIds.Count; i++)
        {
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(refResult.Vm[i]), System.BitConverter.DoubleToInt64Bits(prod.Vm[i]));
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(refResult.VaDeg[i]), System.BitConverter.DoubleToInt64Bits(prod.VaDeg[i]));
        }
    }

    // Verbatim copy of the pre-refactor SteadyStateSolver OLTC loop (per-pass ToList + FindIndex + fresh
    // vmByBus dictionary). The reference the refactor must reproduce exactly.
    private static (GridModel Model, PowerFlowResult Result, int Passes) OriginalLoop(
        GridModel model, PowerFlowOptions opt, int maxOltcPasses)
    {
        var nr = new NewtonRaphsonPowerFlow();
        GridModel current = model;
        PowerFlowResult result = nr.Solve(current, opt);
        int passes = 0;

        for (int pass = 0; pass < maxOltcPasses; pass++)
        {
            var vmByBus = new System.Collections.Generic.Dictionary<int, double>();
            for (int i = 0; i < result.BusIds.Count; i++) vmByBus[result.BusIds[i]] = result.Vm[i];

            var branches = current.Branches.ToList();
            bool changed = false;

            foreach (Oltc o in model.Oltcs)
            {
                int idx = branches.FindIndex(b => b.Name == o.Transformer);
                if (idx < 0 || !vmByBus.TryGetValue(o.ControlsBus, out double v)) continue;

                double tap = branches[idx].TapRatio;
                double newTap = tap;
                if (v < o.TargetVpu - o.DeadbandPu) newTap = System.Math.Max(o.MinTap, tap - o.StepPu);
                else if (v > o.TargetVpu + o.DeadbandPu) newTap = System.Math.Min(o.MaxTap, tap + o.StepPu);

                if (System.Math.Abs(newTap - tap) > 1e-12)
                {
                    branches[idx] = branches[idx] with { TapRatio = newTap };
                    changed = true;
                }
            }

            if (!changed) break;
            current = Dispatcher.Clone(current, branches: branches);
            result = nr.Solve(current, opt);
            passes = pass + 1;
        }
        return (current, result, passes);
    }
}
