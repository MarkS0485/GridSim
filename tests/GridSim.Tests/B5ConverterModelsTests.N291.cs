using GridSim.Core.Dynamics;
using GridSim.Core.Fleet;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N291 (grid-forming part): the fleet engines (batch <see cref="FleetDynamics"/>, live
/// <see cref="MultiDeviceFrequencyIntegrator"/>) refused a grid-forming config (R263 / R080). They now run the same
/// grid-forming step as the lumped engines (FormingKernel: an aggregate virtual synchronous machine, or a droop former,
/// over the real rotating mass) with the fleet's summed fast response in the device slot of the forcing. The pins: a
/// one-unit, zero-stagger forming fleet is the lumped forming model with that device, bit for bit, batch and live; a
/// staggered fleet forms the frequency on the virtual former (RoCoF set by E_real + Ev); a forming request with nothing
/// to form the frequency fails loud; and the CLI fleet verb takes the forming switches.
/// </summary>
public sealed partial class B5ConverterModelsTests
{
    private static readonly FrequencyEvent[] N291Trip = { new(1.0, -1320.0), new(6.0, -200.0), new(12.0, 300.0) };

    /// <summary>The GB-calibrated recorded services (DC / DM / DR shapes), so the forming forcing carries every term.</summary>
    private static IReadOnlyList<SystemResponseService> N291Services() => new[]
    {
        new SystemResponseService(950, 0.015, 0.5, 0.5, KneeDeviationHz: 0.2, KneeFraction: 0.05),
        new SystemResponseService(520, 0.1, 0.2, 1.0, KneeDeviationHz: 0.1, KneeFraction: 0.0),
        new SystemResponseService(490, 0.015, 0.2, 2.0, KneeDeviationHz: 0.015, KneeFraction: 0.0),
    };

    /// <summary>Forming configs reaching every forcing term (governor, services, pre-event start, setpoint, a 60 Hz
    /// system, a tiny former that forces many sub-steps).</summary>
    private static IEnumerable<(string Name, DynamicsConfig Cfg)> N291FormingConfigs()
    {
        yield return ("vsm", new DynamicsConfig { Forming = FormingMode.Vsm, VirtualInertiaMwS = 20_000, FormingDampingMwPerHz = 500 });
        yield return ("vsm + services + setpoint", new DynamicsConfig
        {
            Forming = FormingMode.Vsm, VirtualInertiaMwS = 20_000, FormingDampingMwPerHz = 500, FormingSetpointMw = 3,
            SystemResponseServices = N291Services(),
        });
        // Moved 2026-10 (LF4 N311, Ruling R1, ruled by main): a droop former carries no virtual inertia (a non-zero
        // VirtualInertiaMwS under Droop is now refused by name). Before: 5_000. After: 0. The fleet / lumped
        // equivalence is still asserted bit for bit on it.
        yield return ("droop from 50.07", new DynamicsConfig
        {
            Forming = FormingMode.Droop, VirtualInertiaMwS = 0.0, FormingDampingMwPerHz = 2_000, InitialFrequencyHz = 50.07,
            SystemResponseServices = N291Services(),
        });
        yield return ("vsm 60 Hz, 10 ms", new DynamicsConfig
        {
            NominalHz = 60.0, Forming = FormingMode.Vsm, VirtualInertiaMwS = 8_000, FormingDampingMwPerHz = 300,
            StepSeconds = 0.01, DurationSeconds = 20,
        });
        yield return ("tiny former (sub-stepped)", new DynamicsConfig
        {
            Forming = FormingMode.Vsm, VirtualInertiaMwS = 50, FormingDampingMwPerHz = 4_000, DurationSeconds = 15,
        });
    }

    [Fact]
    public void N291_a_one_unit_zero_stagger_forming_fleet_is_the_lumped_forming_model_with_that_device_bit_for_bit()
    {
        foreach ((string name, DynamicsConfig cfg) in N291FormingConfigs())
            foreach ((double unitInertia, double unitFfr, double tau, double deadband) in new[]
                     { (40_000.0, 1_500.0, 0.5, 0.015), (0.0, 800.0, 0.3, 0.02), (10_000.0, 0.0, 0.5, 0.015) })
                foreach (double baseInertia in new[] { 120_000.0, 2_000.0, 0.0 })
                {
                    if (baseInertia + unitInertia == 0.0 && cfg.VirtualInertiaMwS == 0.0) continue;   // nothing forms: refused, below
                    var fleet = new FleetSpec(new[] { new FleetUnit(unitInertia, unitFfr, tau, deadband, StaggerSeconds: 0.0) });
                    FleetResult r = FleetDynamics.Simulate(baseInertia, 30_000, N291Trip, fleet, cfg);
                    FrequencyResponse lumped = FrequencyDynamics.Simulate(baseInertia, 30_000, N291Trip, cfg with
                    {
                        DeviceRotationalInertiaMwS = unitInertia,
                        FastResponseMw = unitFfr,
                        FastResponseTimeConstantS = tau,
                        FastResponseDeadbandHz = deadband,
                    });
                    AssertSameBits(lumped, r.Response, $"{name}, unit ({unitInertia}, {unitFfr}), base {baseInertia}");
                }
    }

    [Fact]
    public void N291_the_live_fleet_under_forming_is_the_live_lumped_forming_integrator_with_that_device_bit_for_bit()
    {
        foreach ((string name, DynamicsConfig cfg) in N291FormingConfigs())
        {
            var fleet = new FleetSpec(new[] { new FleetUnit(40_000, 1_500, 0.5, 0.015, StaggerSeconds: 0.0) });
            var fleetI = new MultiDeviceFrequencyIntegrator(50_000, 30_000, fleet, cfg);
            var single = new FrequencyIntegrator(50_000, 30_000, cfg with
            {
                DeviceRotationalInertiaMwS = 40_000, FastResponseMw = 1_500, FastResponseTimeConstantS = 0.5, FastResponseDeadbandHz = 0.015,
            });
            for (int i = 0; i < 400; i++)
            {
                double dt = i % 5 == 0 ? 0.02 : i % 5 == 1 ? 0.001 : i % 5 == 2 ? 0.37 : 0.05;   // 1 ms to sub-stepped
                double ev = i < 15 ? 0.0 : i < 200 ? -1_320.0 : -900.0;
                fleetI.Step(dt, ev);
                single.Step(dt, ev);
                AssertBits(single.FrequencyHz, fleetI.FrequencyHz, $"{name} step {i} f");
                AssertBits(single.RocofHzPerS, fleetI.RocofHzPerS, $"{name} step {i} RoCoF");
            }
        }
    }

    [Fact]
    public void N291_a_staggered_forming_fleet_forms_the_frequency_on_the_virtual_former()
    {
        // 100 staggered units (10 s spread) splitting 40 GVA.s of real inertia and 1.5 GW of FFR on a 120 GVA.s system.
        FleetSpec fleet = FleetSpec.Uniform(100, 40_000, 1_500, 0.5, 0.015, maxStaggerS: 10.0);
        var vsm = new DynamicsConfig { Forming = FormingMode.Vsm, VirtualInertiaMwS = 60_000, FormingDampingMwPerHz = 1_000 };
        FleetResult formed = FleetDynamics.Simulate(120_000, 30_000, N291Trip, fleet, vsm);
        FleetResult plain = FleetDynamics.Simulate(120_000, 30_000, N291Trip, fleet, new DynamicsConfig());

        // First post-event step: no responder has moved, so RoCoF = f0 dP / (2 (E_real + Ev)) (round-off only).
        double expected = 50.0 * -1_320.0 / (2.0 * (120_000.0 + 40_000.0 + 60_000.0));
        Assert.True(Math.Abs(formed.Response.InitialRocofHzPerS - expected) <= 1e-9 * Math.Abs(expected),
            $"peak RoCoF {formed.Response.InitialRocofHzPerS:R} vs {expected:R}");
        // The virtual former adds inertia and damping: a shallower RoCoF and a higher nadir than the plain fleet.
        Assert.True(Math.Abs(formed.Response.InitialRocofHzPerS) < Math.Abs(plain.Response.InitialRocofHzPerS));
        Assert.True(formed.Response.NadirHz > plain.Response.NadirHz,
            $"forming nadir {formed.Response.NadirHz:R} should be above the plain fleet's {plain.Response.NadirHz:R}");
        // The staggered units still deliver (their clock runs with the forming sub-steps), within their cap.
        Assert.True(formed.PeakFleetFfrMw > 0.0 && formed.PeakFleetFfrMw <= 1_500.0 + 1e-9, $"peak FFR {formed.PeakFleetFfrMw:R}");
        Assert.Equal(40_000.0, formed.TotalInertiaMwS);   // the virtual former is never counted as real inertia
    }

    [Fact]
    public void N291_forming_with_nothing_to_form_the_frequency_fails_loud_in_both_fleet_engines()
    {
        var cfg = new DynamicsConfig { Forming = FormingMode.Droop, VirtualInertiaMwS = 0.0, FormingDampingMwPerHz = 500 };
        FleetSpec noInertia = FleetSpec.Uniform(3, 0.0, 300);
        Assert.Throws<InvalidOperationException>(() => FleetDynamics.Simulate(0.0, 30_000, N291Trip, noInertia, cfg));
        Assert.Throws<InvalidOperationException>(() => new MultiDeviceFrequencyIntegrator(0.0, 30_000, noInertia, cfg));
    }

    [Fact]
    public void N291_neither_fleet_engine_keeps_the_refusal()
    {
        foreach (string[] f in new[] { new[] { "Fleet", "FleetDynamics.cs" }, new[] { "Dynamics", "MultiDeviceFrequencyIntegrator.cs" } })
        {
            string src = CoreSrc(f);
            Assert.DoesNotContain("does not model grid-forming formation", src);
            Assert.Contains("FormingKernel.SemiImplicitStep(", src);
            Assert.Contains("FormingForcingMw(", src);
        }
    }

    [Fact]
    public void N291_the_cli_fleet_verb_takes_the_forming_switches()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-b5-n291-" + Guid.NewGuid().ToString("N"));
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("fleet", "--event", "gb-largest-loss-1320", "--forming", "vsm",
                "--forming-inertia", "20", "--forming-damping", "500", "--out", dir);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("grid-forming: vsm former, virtual inertia 20 GVA.s, damping 500 MW/Hz", r.StdOut, StringComparison.Ordinal);
            // The run description names the former (checked without the '=' a JSON encoder may escape).
            string meta = File.ReadAllText(Path.Combine(dir, "run-metadata.json"));
            Assert.Contains("Vsm", meta, StringComparison.Ordinal);
            Assert.Contains("formingInertia", meta, StringComparison.Ordinal);

            // Without the switches the line is absent (the old output).
            CliProcess.Result plain = CliProcess.RunBuilt("fleet", "--event", "gb-largest-loss-1320", "--out", Path.Combine(dir, "plain"));
            Assert.True(plain.ExitCode == 0, plain.ToString());
            Assert.DoesNotContain("grid-forming:", plain.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
