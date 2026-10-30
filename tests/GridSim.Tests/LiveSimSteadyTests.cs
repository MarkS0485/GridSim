using GridSim.Core.IO;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Covers LiveSim's hold-steady mode: for an IEEE/MATPOWER case (no GB demand profile) the sim
/// holds its own loads constant and keeps re-solving the same operating point, warm-started from
/// the previous result. Distinct from the default GB daily-curve behaviour exercised in LiveSimTests.
/// </summary>
public class LiveSimSteadyTests
{
    [Fact]
    public void HoldSteady_keeps_demand_flat_and_stays_converged()
    {
        var sim = new LiveSim(IeeeCases.Case30(), holdSteady: true);
        var demand = new List<double>();
        for (int t = 1; t <= 72; t++)
        {
            SimFrame f = sim.Step(t, stepMinutes: 20);
            Assert.True(f.Result.Converged, $"tick {t} should converge");
            Assert.InRange(f.FrequencyHz, 49.7, 50.3);
            demand.Add(f.DemandMw);
        }

        // Constant loads: demand must not follow any curve - it sits flat across the whole run.
        Assert.True(demand.Max() - demand.Min() < 1e-6,
            $"hold-steady demand should be constant (spread {demand.Max() - demand.Min()})");
        Assert.All(demand, d => Assert.True(d > 0));
    }

    [Fact]
    public void HoldSteady_warm_start_converges_in_very_few_iterations()
    {
        var sim = new LiveSim(IeeeCases.Case30(), holdSteady: true);

        // First tick is a cold solve from the base model's flat/initial voltages.
        SimFrame first = sim.Step(1);
        Assert.True(first.Result.Converged);

        // Every subsequent tick re-solves the same operating point warm-started from the last
        // result - that seed is essentially the answer, so NR should converge in a couple of steps.
        for (int t = 2; t <= 20; t++)
        {
            SimFrame f = sim.Step(t);
            Assert.True(f.Result.Converged);
            Assert.True(f.Result.Iterations <= 2,
                $"warm-started re-solve should take <=2 iterations, took {f.Result.Iterations} at tick {t}");
        }
    }

    [Fact]
    public void Default_mode_still_varies_demand_across_a_day()
    {
        // The no-flag (holdSteady:false) behaviour is unchanged: demand follows the GB day/night curve.
        var sim = new LiveSim(IeeeCases.Case30());
        var demand = new List<double>();
        for (int t = 1; t <= 72; t++) demand.Add(sim.Step(t, stepMinutes: 20).DemandMw);
        Assert.True(demand.Max() - demand.Min() > 1.0, "default mode should follow the daily load profile");
    }
}
