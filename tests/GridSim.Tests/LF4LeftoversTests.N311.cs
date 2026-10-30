using GridSim.Core.Dynamics;
using GridSim.Core.Fleet;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// LF4 (October 2026, leftovers stream). One partial file per finding.
///
/// N311 (Ruling R1: Mark delegated EE calls 2026-10-04, ruled by main): <see cref="FormingMode.Droop"/> is a pure P-f
/// droop former (the power loop of REGFM_A1) and carries NO virtual inertia. Before N311 every swing engine used
/// <see cref="DynamicsConfig.VirtualInertiaMwS"/> for Droop exactly as for Vsm, so "droop with inertia" silently ran a
/// VSM. Now a Droop config with a virtual inertia other than 0 is refused by name (Core and CLI).
/// </summary>
public sealed partial class LF4LeftoversTests
{
    private static readonly FrequencyEvent[] N311Trip = { new(1.0, -1_320.0) };

    [Theory]
    [InlineData(5_000.0)]
    [InlineData(1e-300)]
    [InlineData(-20_000.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void N311_droop_with_a_virtual_inertia_is_refused_by_every_swing_engine(double ev)
    {
        var cfg = new DynamicsConfig { Forming = FormingMode.Droop, VirtualInertiaMwS = ev, FormingDampingMwPerHz = 500 };
        FleetSpec fleet = FleetSpec.Uniform(2, 4_000, 300);
        var refusals = new List<ArgumentException>
        {
            Assert.Throws<ArgumentException>(() => FrequencyDynamics.Simulate(120_000, 30_000, N311Trip, cfg)),
            Assert.Throws<ArgumentException>(() => new FrequencyIntegrator(120_000, 30_000, cfg)),
            Assert.Throws<ArgumentException>(() => new MultiDeviceFrequencyIntegrator(120_000, 30_000, fleet, cfg)),
            Assert.Throws<ArgumentException>(() => FleetDynamics.Simulate(120_000, 30_000, N311Trip, fleet, cfg)),
            Assert.Throws<ArgumentException>(() => SwingKernel.SwingInertia(120_000, cfg)),
        };
        foreach (ArgumentException ex in refusals)
        {
            // The refusal names the field, the mode and the finding, so the user knows what to change.
            Assert.Contains("VirtualInertiaMwS", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Droop", ex.Message, StringComparison.Ordinal);
            Assert.Contains("N311", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void N311_a_pure_droop_former_runs_and_its_first_rocof_is_the_real_mass_alone()
    {
        // Ev = 0 (and -0.0, which is equal to 0) is the droop former by definition and runs. With no responder moved
        // at the first post-event step, RoCoF = f0 dP / (2 E_real) = 50 * -1320 / (2 * 120,000) = -0.275 Hz/s; the
        // relative tolerance covers only the round-off of the nominal-to-round-off frequency at that step.
        foreach (double zero in new[] { 0.0, -0.0 })
        {
            var cfg = new DynamicsConfig { Forming = FormingMode.Droop, VirtualInertiaMwS = zero, FormingDampingMwPerHz = 2_000 };
            FrequencyResponse r = FrequencyDynamics.Simulate(120_000, 30_000, N311Trip, cfg);
            double expected = 50.0 * -1_320.0 / (2.0 * 120_000.0);
            Assert.True(Math.Abs(r.InitialRocofHzPerS - expected) <= 1e-9 * Math.Abs(expected),
                $"droop first RoCoF {r.InitialRocofHzPerS:R} vs {expected:R}");
            Assert.True(double.IsFinite(r.NadirHz) && r.NadirHz < 50.0, $"nadir {r.NadirHz:R}");
        }
    }

    [Fact]
    public void N311_vsm_still_takes_its_virtual_inertia_and_none_still_ignores_it()
    {
        // The gate is Droop-only: Vsm keeps Ev in the forming denominator, None keeps ignoring the forming fields.
        var vsm = new DynamicsConfig { Forming = FormingMode.Vsm, VirtualInertiaMwS = 20_000, FormingDampingMwPerHz = 500 };
        Assert.Equal(140_000.0, SwingKernel.SwingInertia(120_000, vsm).Forming);
        var none = new DynamicsConfig { Forming = FormingMode.None, VirtualInertiaMwS = 20_000 };
        (double floored, double forming) = SwingKernel.SwingInertia(120_000, none);
        Assert.Equal(120_000.0, floored);
        Assert.Equal(120_000.0, forming);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0")]
    public void N311_the_cli_refuses_forming_inertia_with_droop_by_name(string gvaS)
    {
        CliProcess.Result r = CliProcess.RunBuilt("fleet", "--event", "gb-largest-loss-1320", "--forming", "droop",
            "--forming-inertia", gvaS);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--forming-inertia does not apply to --forming droop", r.StdErr, StringComparison.Ordinal);
    }
}
