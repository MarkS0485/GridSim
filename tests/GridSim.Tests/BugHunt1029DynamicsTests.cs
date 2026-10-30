using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-10-29 (GridSim 2.0.183), dynamics findings. Each test pins one confirmed defect: it fails
/// on the code as it was and passes on the fix. The BHnn number in each test name is the finding's place in the hunt
/// list (local review records), so a reader can trace it back.
/// </summary>
public sealed partial class BugHunt1029Tests
{
    // ---------------------------------------------------------------------------------------------------------------
    // BH22: machine damping D is on the machine's own base and must be carried to the system base like H.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH22_machine_damping_is_converted_from_machine_base_to_system_base_like_inertia()
    {
        // IEEE case 9 on its 100 MVA base, with generator 0 re-rated to 600 MVA so its MBase differs from the system
        // base by an exact factor of 6.
        GridModel baseCase = IeeeCases.Case9();
        var gens = baseCase.Generators.Select((g, k) => k == 0 ? g with { MBase = 600.0 } : g).ToList();
        GridModel model = baseCase.CopyWith(generators: gens);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);

        // D = 2 pu power per pu speed on each machine's OWN base for every machine.
        var data = new List<SubtransientInit.MachineData>();
        for (int k = 0; k < model.Generators.Count; k++)
            data.Add(new SubtransientInit.MachineData(
                H: 3.0, Damping: 2.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05));

        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, pf, data, 7);

        // On the system base: H 3 x 600/100 = 18 s and D 2 x 600/100 = 12 pu, both exact in binary. Before the fix the
        // damping was passed raw (2.0), six times too little damping torque for this unit.
        Assert.Equal(18.0, p.H[0]);
        Assert.Equal(12.0, p.Damping[0]);
        // Every machine: D x MBase / BaseMva, the same expression (so the same rounding) as the inertia conversion.
        for (int k = 0; k < model.Generators.Count; k++)
            Assert.Equal(2.0 * model.Generators[k].MBase / model.BaseMva, p.Damping[k]);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH28: an event at the trace's second sample is refused, not dropped from the angle-step handling.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH28_an_event_at_the_second_sample_is_refused_and_later_events_still_measure()
    {
        // A flat 50 Hz bus (angle constant) sampled every 5 ms for 1 s, with a 10 degree angle step at sample 1.
        const double dt = 0.005;
        int n = 201;
        var t = Enumerable.Range(0, n).Select(k => k * dt).ToArray();
        double[] angle = Enumerable.Range(0, n).Select(k => k >= 1 ? 10.0 * Math.PI / 180.0 : 0.0).ToArray();
        var trace = new RmsBusTrace(new[] { 1 }, t, new[] { angle });

        var ex = Assert.Throws<ArgumentException>(() => BusFrequencyMeter.Measure(trace, 50.0, dt, new[] { dt }, 0.1));
        Assert.Contains("second sample", ex.Message, StringComparison.Ordinal);

        // The same step at sample 40 is measured and removed: the bus reads 50 Hz throughout and the jump is 10 degrees.
        double[] later = Enumerable.Range(0, n).Select(k => k >= 40 ? 10.0 * Math.PI / 180.0 : 0.0).ToArray();
        BusFrequencyResult ok = BusFrequencyMeter.Measure(new RmsBusTrace(new[] { 1 }, t, new[] { later }), 50.0, dt, new[] { 40 * dt }, 0.1);
        Assert.Equal(10.0, ok.AngleJumpDeg[0], 12);
        Assert.All(ok.FrequencyHz[0], f => Assert.Equal(50.0, f, 12));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH10: a diverged multi-domain swing must fail loud, as the lumped twin does.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH10_a_diverged_multi_domain_swing_throws_instead_of_returning_a_nan_trace()
    {
        // Three swing domains (thousands of MW.s each) tied by an absurdly stiff synchronising coefficient of 1e9 MW/rad:
        // omega * dt is in the hundreds at the 20 ms step, far past the symplectic step's stability limit of 2, so the
        // angles grow without bound to Infinity then NaN within a few dozen steps.
        GridModel m = new()
        {
            Name = "bh10-chain",
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Pq), new(4, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 4000, MBase: 5000, H: 5.0, Type: "ccgt"),
                new(Bus: 3, Pg: 1500, MBase: 2000, H: 1.0, Type: "recip"),
                new(Bus: 4, Pg: 800, MBase: 1000, H: 3.0, Type: "hydro"),
            },
            Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05), new(2, 3, R: 0.0, X: 0.15), new(3, 4, R: 0.0, X: 0.05) },
        };
        var cfg = new GridSim.Core.Dynamics.DynamicsConfig
        {
            InertiaModel = GridSim.Core.Dynamics.InertiaModel.Distributed, DurationSeconds = 10,
        };
        var dist = new GridSim.Core.Dynamics.Distributed.DistributedConfig();
        (IReadOnlyList<GridSim.Core.Dynamics.Distributed.SpeedDomain> domains, _) =
            GridSim.Core.Dynamics.Distributed.DomainAssignment.Assign(m, dist);
        int n = domains.Count(d => d.IsSwingMass);
        Assert.Equal(3, n);
        var k = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j) k[i, j] = 1e9;

        var loss = new[] { new GridSim.Core.Dynamics.FrequencyEvent(1.0, -300.0) };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GridSim.Core.Dynamics.Distributed.MultiDomainSwing.Simulate(domains, k, new double[n, n], 6300, loss, cfg, dist));
        Assert.Contains("diverged", ex.Message, StringComparison.Ordinal);
    }
}
