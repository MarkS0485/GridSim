using System.Collections.Generic;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The restoration-sequence reactive-feasibility checker (<see cref="RestorationSequencer"/>): energising a
/// lightly-loaded EHV line drives a Ferranti over-voltage that a generator must absorb, and once the machine
/// hits its reactive-absorption limit the step is unsafe. A safe sequence reports all steps feasible.
/// </summary>
public class RestorationSequencerTests
{
    // 1 = black-start slack (generous). 2 = a PV machine with TIGHT reactive absorption (Qmin = -15).
    // 3 = a remote, unloaded bus reached only by a long high-charging line (the Ferranti source).
    private static GridModel Model() => new()
    {
        BaseMva = 100,
        Name = "restoration",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 400),
            new Bus(2, BusType.Pv, Pd: 40, Qd: 10, BaseKv: 400),
            new Bus(3, BusType.Pq, BaseKv: 400), // no load: the charging has nothing to soak it up
        },
        Generators = new[]
        {
            new Generator(1, Pg: 0,  Vg: 1.00, Qmin: -500, Qmax: 500),  // strong black-start source
            new Generator(2, Pg: 30, Vg: 1.00, Qmin: -15,  Qmax: 150),  // tight reactive-absorption limit
        },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.10, 0.10, Name: "L12"),          // normal loaded line
            new Branch(2, 3, 0.005, 0.05, 2.00, Name: "L23-charging"), // long, very high charging (Ferranti)
        },
    };

    // A fully loaded radial with only normal (low-charging) lines - every energisation step is safe.
    private static GridModel LoadedModel() => new()
    {
        BaseMva = 100,
        Name = "restoration-loaded",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 400),
            new Bus(2, BusType.Pv, Pd: 40, Qd: 12, BaseKv: 400),
            new Bus(3, BusType.Pq, Pd: 35, Qd: 10, BaseKv: 400), // loaded: soaks up any charging
        },
        Generators = new[]
        {
            new Generator(1, Pg: 0,  Vg: 1.00, Qmin: -500, Qmax: 500),
            new Generator(2, Pg: 40, Vg: 1.00, Qmin: -100, Qmax: 150),
        },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.10, 0.05, Name: "A"),
            new Branch(2, 3, 0.02, 0.12, 0.05, Name: "B"),
        },
    };

    [Fact]
    public void A_loaded_sequence_is_all_feasible()
    {
        // Energise both normal loaded lines in order: no over-voltage, generators within limits.
        RestorationReport report = RestorationSequencer.Evaluate(LoadedModel(), new[] { "A", "B" });
        Assert.True(report.AllFeasible, $"first unsafe step: {report.FirstInfeasibleStep}");
        Assert.Equal(-1, report.FirstInfeasibleStep);
        Assert.All(report.Steps, s => Assert.True(s.Solved && s.MaxVpu < 1.1));
    }

    [Fact]
    public void Energising_a_charging_line_flags_the_reactive_limited_step()
    {
        RestorationReport report = RestorationSequencer.Evaluate(Model(), new[] { "L12", "L23-charging" });

        // Step 0 (normal line) is safe; step 1 (the high-charging line) is not.
        Assert.True(report.Steps[0].Feasible, "energising the loaded line should be safe");
        Assert.False(report.Steps[1].Feasible, "energising the charging line should be flagged unsafe");
        Assert.Equal(1, report.FirstInfeasibleStep);
        Assert.False(report.AllFeasible);

        // The unsafe step is unsafe for the physical reason: an over-voltage and/or a generator that has run
        // out of reactive absorption.
        RestorationStepResult s1 = report.Steps[1];
        Assert.True(s1.OverVoltageBuses.Count > 0 || s1.GeneratorsAtReactiveLimit.Count > 0,
            $"expected an over-voltage or a reactive-limited generator; maxV {s1.MaxVpu:F3}, " +
            $"over {s1.OverVoltageBuses.Count}, pinned {s1.GeneratorsAtReactiveLimit.Count}");
    }
}
