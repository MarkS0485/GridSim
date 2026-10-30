using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Forming;
using GridSim.Core.Fleet;
using GridSim.Core.Frequency;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R080: one swing kernel (<see cref="SwingResponders"/>) shared by the seven engines of the centre-of-inertia family.
/// The refactor must move no bit: each engine's pre-R080 loop is written out below VERBATIM (copied from 1.95.215, the
/// only edits being the class / method names) and compared with the engine bit for bit over configs that exercise every
/// term (governor, TSGB device, recorded services, equilibrium start, grid-forming). Then the behaviour R080 adds is
/// pinned: the live integrators now honour the services and the pre-event frequency (they reproduce the batch trace to
/// the bit when stepped at its step), the demand is no longer floored, and the multi-device integrator refuses a forming
/// config like its batch twin (moved 2026-10 by B5 N291: both fleet engines now run the forming step).
/// </summary>
public sealed partial class B1SwingKernelTests
{
    private static readonly FrequencyEvent[] R080Trip = { new(1.0, -1320.0), new(6.0, -200.0), new(12.0, 300.0) };

    /// <summary>Configs that between them reach every term of the swing.</summary>
    private static IEnumerable<(string Name, DynamicsConfig Cfg)> R080Configs()
    {
        yield return ("default", new DynamicsConfig());
        yield return ("device", new DynamicsConfig { DeviceRotationalInertiaMwS = 40_000, FastResponseMw = 1500 });
        yield return ("services", new DynamicsConfig { SystemResponseServices = Services() });
        yield return ("pre-event 49.88 + services + device", new DynamicsConfig
        {
            InitialFrequencyHz = 49.88, SystemResponseServices = Services(), FastResponseMw = 800, DeviceRotationalInertiaMwS = 10_000,
        });
        yield return ("60 Hz, other constants", new DynamicsConfig
        {
            NominalHz = 60.0, DroopPu = 0.04, DampingPctPerHz = 2.0, ResponsiveFraction = 0.1, GovernorTimeConstantS = 5.6,
            StepSeconds = 0.01, DurationSeconds = 20,
        });
        yield return ("vsm forming", new DynamicsConfig
        {
            Forming = FormingMode.Vsm, VirtualInertiaMwS = 20_000, FormingDampingMwPerHz = 500, FormingSetpointMw = 3,
            FastResponseMw = 600, SystemResponseServices = Services(),
        });
        // Moved 2026-10 (LF4 N311, Ruling R1: Mark delegated EE calls 2026-10-04, ruled by main): a droop former is a pure
        // P-f droop with no virtual inertia, and a droop config with VirtualInertiaMwS other than 0 is now refused by name.
        // Before: VirtualInertiaMwS = 5_000 (a VSM under the droop label). After: 0, the droop former by definition. The
        // old loop and the kernel are still compared bit for bit on it; only the config they both run moved.
        yield return ("droop forming from 50.07", new DynamicsConfig
        {
            Forming = FormingMode.Droop, VirtualInertiaMwS = 0.0, FormingDampingMwPerHz = 2_000, InitialFrequencyHz = 50.07,
            SystemResponseServices = Services(),
        });
    }

    [Fact]
    public void R080_FrequencyDynamics_is_bit_identical_to_its_pre_kernel_loop()
    {
        foreach ((string name, DynamicsConfig cfg) in R080Configs())
            foreach (double e in new[] { 200_000.0, 50_000.0, 0.0 })
            {
                if (e == 0.0 && cfg.Forming == FormingMode.None && cfg.DeviceRotationalInertiaMwS == 0.0) continue;   // the floor's artefact, covered below
                // LF4 N311: a pure droop former (Ev = 0) over no real mass forms nothing; both loops refuse it
                // (InvalidOperationException from the forming denominator), pinned in LF4LeftoversTests.N311.
                if (e == 0.0 && cfg.Forming == FormingMode.Droop && cfg.DeviceRotationalInertiaMwS == 0.0) continue;
                AssertSameBits(OldLumped.Simulate(e, 30_000, R080Trip, cfg),
                    FrequencyDynamics.Simulate(e, 30_000, R080Trip, cfg), $"{name} E={e}");
            }
        // The 1 MW.s inertia floor path (E = 0, non-forming).
        // Test changed 2026-10-19: since 1.95.300 (commit 31ab597c, integration stability guards) FrequencyDynamics
        // refuses an inertia below the explicit swing's damping stability bound (dt*f0*D/(2E) < 2; here 150) instead of
        // integrating a diverging trajectory. The old pre-kernel copy has no such guard, so there is nothing left to
        // compare bit for bit on this path: the kernel's refusal is pinned instead.
        var floored = Assert.Throws<InvalidOperationException>(
            () => FrequencyDynamics.Simulate(0.0, 30_000, R080Trip, new DynamicsConfig()));
        Assert.Contains("too small to integrate", floored.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void R080_AssetFrequencyDynamics_with_an_empty_fleet_is_bit_identical_to_the_pre_kernel_lumped_loop()
    {
        // The asset engine's old loop is the lumped loop with the fleet terms appended as exact 0.0 when the fleet is
        // empty (its own reduction gate), so the old lumped copy is its oracle on an empty fleet.
        GridModel m = AssetTestHelpers.Model(assetDynamics: null);
        foreach ((string name, DynamicsConfig cfg) in R080Configs())
        {
            if (cfg.NominalHz != m.FrequencyHz) continue;   // N275: a model-based entry point runs at the model's nominal
            AssertSameBits(OldLumped.Simulate(m.SystemInertiaGvaS * 1000.0, AssetTestHelpers.BaseDemandMw, R080Trip, cfg),
                AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, R080Trip, cfg).Response, name);
        }
    }

    [Fact]
    public void R080_FrequencyIntegrator_is_bit_identical_to_its_pre_kernel_loop_without_services()
    {
        // Configs the old live integrator modelled fully: no services, no pre-event frequency (it ignored both).
        DynamicsConfig[] cfgs =
        {
            new(),
            new() { DeviceRotationalInertiaMwS = 40_000, FastResponseMw = 1500 },
            new() { NominalHz = 60.0, DroopPu = 0.04, GovernorTimeConstantS = 0.01 },   // T below the step: the guarded lag
            new() { Forming = FormingMode.Vsm, VirtualInertiaMwS = 20_000, FormingDampingMwPerHz = 500, FormingSetpointMw = 3, FastResponseMw = 600 },
        };
        double[] dts = { 0.02, 0.001, 0.37, 60.0, 0.02, 1200.0, 0.005 };
        foreach (DynamicsConfig cfg in cfgs)
            foreach (double demand in new[] { 30_000.0, 1.0, 7.5 })
            {
                var oldI = new OldLiveIntegrator(200_000, demand, cfg);
                var newI = new FrequencyIntegrator(200_000, demand, cfg);
                for (int i = 0; i < 300; i++)
                {
                    double dt = dts[i % dts.Length];
                    double ev = i < 5 ? 0.0 : (i < 150 ? -1320.0 : -700.0);
                    // Test changed 2026-10-19: since 1.95.300 (commit 31ab597c, "the forming sub-step count is never
                    // silently capped") FormingKernel.SubStepCount refuses a forming step that needs more than its 4000
                    // sub-step cap, where both loops used to cap it silently and run an unstable sub-step. The 1200 s
                    // step on the forming config (E + Ev = 220000 MW.s needs 60000) is now refused by BOTH the old copy
                    // and the kernel, before either touches its state, with the same message; pin that and go on. Every
                    // other step is still compared bit for bit.
                    if (cfg.Forming != FormingMode.None && dt == 1200.0)
                    {
                        var oldEx = Assert.Throws<InvalidOperationException>(() => oldI.Step(dt, ev));
                        var newEx = Assert.Throws<InvalidOperationException>(() => newI.Step(dt, ev));
                        Assert.Contains("above the 4000 cap", newEx.Message, StringComparison.Ordinal);
                        Assert.Equal(oldEx.Message, newEx.Message);
                        AssertBits(oldI.FrequencyHz, newI.FrequencyHz, $"step {i} f after refusal (demand {demand})");
                        continue;
                    }
                    oldI.Step(dt, ev);
                    newI.Step(dt, ev);
                    AssertBits(oldI.FrequencyHz, newI.FrequencyHz, $"step {i} f (demand {demand})");
                    AssertBits(oldI.RocofHzPerS, newI.RocofHzPerS, $"step {i} RoCoF (demand {demand})");
                }
            }
    }

    [Fact]
    public void R080_MultiDeviceFrequencyIntegrator_is_bit_identical_to_its_pre_kernel_loop()
    {
        foreach (FleetSpec fleet in new[]
                 {
                     FleetSpec.Uniform(1, 40_000, 1500),
                     FleetSpec.Uniform(100, 10_000, 2000, maxStaggerS: 4.0),
                     FleetSpec.Uniform(7, 3_000, 700, ffrTauS: 0.1, deadbandHz: 0.05, maxStaggerS: 0.6),
                 })
        {
            var oldI = new OldMultiDevice(50_000, 30_000, fleet, null);
            var newI = new MultiDeviceFrequencyIntegrator(50_000, 30_000, fleet);
            for (int i = 0; i < 400; i++)
            {
                double dt = i % 3 == 0 ? 0.02 : 0.05;
                double ev = i < 20 ? 0.0 : -1320.0;
                oldI.Step(dt, ev);
                newI.Step(dt, ev);
                AssertBits(oldI.FrequencyHz, newI.FrequencyHz, $"step {i} f");
                AssertBits(oldI.RocofHzPerS, newI.RocofHzPerS, $"step {i} RoCoF");
                AssertBits(oldI.FleetFfrMw, newI.FleetFfrMw, $"step {i} fleet FFR");
            }
        }
    }

    [Fact]
    public void R080_FleetDynamics_is_bit_identical_to_its_pre_kernel_loop()
    {
        DynamicsConfig[] cfgs =
        {
            new(),
            new() { SystemResponseServices = Services(), InitialFrequencyHz = 49.9 },
            new() { NominalHz = 60.0, DurationSeconds = 15, StepSeconds = 0.01 },
        };
        foreach (DynamicsConfig cfg in cfgs)
            foreach (FleetSpec fleet in new[] { FleetSpec.Uniform(1, 40_000, 1500), FleetSpec.Uniform(100, 10_000, 2000, maxStaggerS: 4.0) })
            {
                (FrequencyResponse oldR, double oldPeak, double oldAtNadir) = OldFleet.Simulate(50_000, 30_000, R080Trip, fleet, cfg);
                FleetResult newR = FleetDynamics.Simulate(50_000, 30_000, R080Trip, fleet, cfg);
                AssertSameBits(oldR, newR.Response, "fleet");
                AssertBits(oldPeak, newR.PeakFleetFfrMw, "peak fleet FFR");
                AssertBits(oldAtNadir, newR.FfrAtNadirMw, "FFR at nadir");
            }
    }

    [Fact]
    public void R080_MultiDomainSwing_is_bit_identical_to_its_pre_kernel_loop()
    {
        GridModel model = new()
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 4000, MBase: 5000, H: 5.0, Type: "ccgt"),
                new(Bus: 2, Pg: 1500, MBase: 2000, H: 1.0, Type: "recip"),
                new(Bus: 3, Pg: 800, MBase: 1000, H: 3.0, Type: "hydro"),
            },
            Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05), new(2, 3, R: 0.0, X: 0.05) },
        };
        var dist = new DistributedConfig();
        (IReadOnlyList<SpeedDomain> domains, _) = DomainAssignment.Assign(model, dist);
        List<SpeedDomain> masses = domains.Where(d => d.IsSwingMass).ToList();
        double[,] k = DomainCoupling.SynchronisingMatrix(model, masses, dist);
        var tau = new double[masses.Count, masses.Count];
        foreach (DynamicsConfig cfg in new[]
                 {
                     new DynamicsConfig(),
                     new DynamicsConfig { SystemResponseServices = Services(), InitialFrequencyHz = 49.93, FastResponseMw = 300, DeviceRotationalInertiaMwS = 2_000 },
                 })
        {
            (FrequencyResponse oldCoi, IReadOnlyList<DomainTrace> oldDomains) = OldMultiDomain.Simulate(domains, k, tau, 6300, R080Trip, cfg);
            MultiDomainSwing.Result r = MultiDomainSwing.Simulate(domains, k, tau, 6300, R080Trip, cfg, dist);
            AssertSameBits(oldCoi, r.Coi, "COI");
            Assert.Equal(oldDomains.Count, r.Domains.Count);
            for (int i = 0; i < oldDomains.Count; i++)
            {
                AssertBits(oldDomains[i].NadirHz, r.Domains[i].NadirHz, $"domain {i} nadir");
                AssertBits(oldDomains[i].InitialRocofHzPerS, r.Domains[i].InitialRocofHzPerS, $"domain {i} RoCoF");
                AssertBits(oldDomains[i].SettleHz, r.Domains[i].SettleHz, $"domain {i} settle");
                for (int p = 0; p < oldDomains[i].Points.Count; p++)
                    AssertBits(oldDomains[i].Points[p].Hz, r.Domains[i].Points[p].Hz, $"domain {i} point {p}");
            }
        }
    }

    // ---- What R080 adds (behaviour fixes, Ruling R1) ----

    [Fact]
    public void R080_the_live_integrator_now_reproduces_the_batch_trace_bit_for_bit_with_services_and_a_pre_event_frequency()
    {
        // Before R080 the live integrator ignored SystemResponseServices and InitialFrequencyHz, so the same event gave a
        // different trajectory live and in batch. Stepped at the batch step (one sub-step per call) under the same event
        // power, the two now perform the same operations on the same states: identical to the bit.
        var cfg = new DynamicsConfig
        {
            InitialFrequencyHz = 49.88, SystemResponseServices = Services(), FastResponseMw = 800, DurationSeconds = 20,
        };
        var events = new[] { new FrequencyEvent(0.0, -1000.0) };   // active from the first step, as the live call sees it
        FrequencyResponse batch = FrequencyDynamics.Simulate(200_000, 30_000, events, cfg);
        var live = new FrequencyIntegrator(200_000, 30_000, cfg);
        for (int k = 0; k < batch.Points.Count; k++)
        {
            AssertBits(batch.Points[k].Hz, live.FrequencyHz, $"step {k}");
            live.Step(cfg.StepSeconds, -1000.0);
            AssertBits(batch.Points[k].RocofHzPerS, live.RocofHzPerS, $"step {k} RoCoF");
        }
        AssertBits(batch.SettleHz, live.FrequencyHz, "settle");
    }

    [Fact]
    public void R080_the_live_integrator_starts_at_the_recorded_pre_event_frequency_in_equilibrium()
    {
        var cfg = new DynamicsConfig { InitialFrequencyHz = 49.9, SystemResponseServices = Services() };
        var live = new FrequencyIntegrator(200_000, 30_000, cfg);
        Assert.Equal(49.9, live.FrequencyHz);
        for (int i = 0; i < 500; i++) live.Step(0.02, 0.0);
        // The standing imbalance balances the pre-loaded responders and the damping, so with no event the frequency stays.
        Assert.True(Math.Abs(live.FrequencyHz - 49.9) < 1e-9, $"drifted to {live.FrequencyHz:R}");
    }

    [Fact]
    public void R080_the_demand_is_no_longer_floored_live_so_a_small_demand_matches_the_batch_engine()
    {
        // R163 demand floor, unified on the raw figure: 0.5 MW used to be floored to 1 MW in the live integrators only.
        var cfg = new DynamicsConfig { DurationSeconds = 5 };
        FrequencyResponse batch = FrequencyDynamics.Simulate(1_000, 0.5, new[] { new FrequencyEvent(0.0, -0.3) }, cfg);
        var live = new FrequencyIntegrator(1_000, 0.5, cfg);
        for (int k = 0; k < batch.Points.Count; k++)
        {
            AssertBits(batch.Points[k].Hz, live.FrequencyHz, $"step {k}");
            live.Step(cfg.StepSeconds, -0.3);
        }
        var multi = new MultiDeviceFrequencyIntegrator(1_000, 0.5, FleetSpec.Uniform(1, 0, 0));
        FleetResult fleetBatch = FleetDynamics.Simulate(1_000, 0.5, new[] { new FrequencyEvent(0.0, -0.3) }, FleetSpec.Uniform(1, 0, 0), cfg);
        for (int k = 0; k < fleetBatch.Response.Points.Count; k++)
        {
            AssertBits(fleetBatch.Response.Points[k].Hz, multi.FrequencyHz, $"fleet step {k}");
            multi.Step(cfg.StepSeconds, -0.3);
        }
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void R080_a_negative_or_non_finite_demand_is_refused_by_every_engine(double demand)
    {
        var ev = new[] { new FrequencyEvent(1.0, -100.0) };
        Assert.Equal("demandMw", Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyDynamics.Simulate(1_000, demand, ev)).ParamName);
        Assert.Equal("demandMw", Assert.Throws<ArgumentOutOfRangeException>(() => new FrequencyIntegrator(1_000, demand)).ParamName);
        Assert.Equal("demandMw", Assert.Throws<ArgumentOutOfRangeException>(() => new MultiDeviceFrequencyIntegrator(1_000, demand, FleetSpec.Uniform(1, 0, 0))).ParamName);
        Assert.Equal("demandMw", Assert.Throws<ArgumentOutOfRangeException>(() => FleetDynamics.Simulate(1_000, demand, ev, FleetSpec.Uniform(1, 0, 0))).ParamName);
    }

    [Theory]
    [InlineData(FormingMode.Vsm)]
    [InlineData(FormingMode.Droop)]
    public void R080_the_multi_device_integrator_runs_a_forming_config_like_its_single_mass_twin(FormingMode mode)
    {
        // Moved 2026-10 (B5 N291, Ruling R1: Mark delegated EE calls 2026-10-04; source: the grid-forming step of
        // FrequencyIntegrator, FormingKernel): the refusal was the interim answer to "the integrator ignores the forming
        // fields"; N291 routes the fleet through the forming step. Before: ArgumentException (param "config", naming the
        // mode). After: an empty-device fleet (one unit, no inertia, no FFR) under a forming config is FrequencyIntegrator
        // under the same config, bit for bit (the unit adds +0.0 inertia and +0.0 response, exactly as the absent device).
        // Moved 2026-10 (LF4 N311, Ruling R1, ruled by main): droop carries no virtual inertia, so the droop case runs
        // with VirtualInertiaMwS = 0 (before: 20_000, now refused for droop); the Vsm case is unchanged.
        var cfg = new DynamicsConfig { Forming = mode, VirtualInertiaMwS = mode == FormingMode.Vsm ? 20_000 : 0.0, FormingDampingMwPerHz = 500 };
        var fleetI = new MultiDeviceFrequencyIntegrator(50_000, 30_000, FleetSpec.Uniform(1, 0, 0), cfg);
        var single = new FrequencyIntegrator(50_000, 30_000, cfg);
        for (int i = 0; i < 300; i++)
        {
            double dt = i % 4 == 0 ? 0.02 : 0.25;
            double ev = i < 10 ? 0.0 : -1_320.0;
            fleetI.Step(dt, ev);
            single.Step(dt, ev);
            AssertBits(single.FrequencyHz, fleetI.FrequencyHz, $"{mode} step {i} f");
            AssertBits(single.RocofHzPerS, fleetI.RocofHzPerS, $"{mode} step {i} RoCoF");
        }
    }

    [Fact]
    public void R080_every_engine_of_the_family_builds_the_one_kernel_and_keeps_no_responder_copy()
    {
        string[][] engines =
        {
            new[] { "Dynamics", "FrequencyDynamics.cs" },
            new[] { "Dynamics", "AssetFrequencyDynamics.cs" },
            new[] { "Dynamics", "FrequencyIntegrator.cs" },
            new[] { "Dynamics", "AssetAwareFrequencyIntegrator.cs" },
            new[] { "Dynamics", "MultiDeviceFrequencyIntegrator.cs" },
            new[] { "Dynamics", "Distributed", "MultiDomainSwing.cs" },
            new[] { "Fleet", "FleetDynamics.cs" },
        };
        foreach (string[] f in engines)
        {
            string src = CoreSrc(f);
            Assert.Contains("new SwingResponders(", src);
            Assert.DoesNotContain("GovernorLagIncrement(", src);       // the governor lag lives in the kernel only
            Assert.DoesNotContain("SystemResponseServices[", src);     // so do the service lags
            Assert.DoesNotContain("FastResponseTimeConstantS) *", src); // and the device lag
        }
    }

    // =================================================================================================================
    // The pre-R080 loops, verbatim from 1.95.215 (names changed only). Do not edit: they are the oracle.
    // =================================================================================================================

    private static class OldLumped
    {
        public static FrequencyResponse Simulate(
            double systemInertiaMwS, double baseDemandMw, IEnumerable<FrequencyEvent> events, DynamicsConfig? config = null)
        {
            var cfg = config ?? new DynamicsConfig();
            double f0 = cfg.NominalHz;
            double e = Math.Max(SwingKernel.MinInertiaMwS, systemInertiaMwS + cfg.DeviceRotationalInertiaMwS);
            bool forming = cfg.Forming != FormingMode.None;
            double eForming = forming
                ? FormingKernel.EffectiveFormingInertiaMwS(systemInertiaMwS + cfg.DeviceRotationalInertiaMwS, cfg.VirtualInertiaMwS)
                : e;
            double d = cfg.DampingPctPerHz / 100.0 * baseDemandMw;
            double pResp = cfg.ResponsiveFraction * baseDemandMw;
            double ffrGain = cfg.FastResponseMw / SwingKernel.DeviceFullDeliveryHz;
            double dt = cfg.StepSeconds;
            int steps = (int)Math.Round(cfg.DurationSeconds / dt);
            var ev = events.OrderBy(x => x.AtSeconds).ToArray();

            var baseTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var points = new List<FrequencyPoint>(steps + 1);

            double f = f0, pGov = 0.0, pTsgb = 0.0, pStanding = 0.0;
            double[] pSvc = new double[cfg.SystemResponseServices.Count];
            if (cfg.InitialFrequencyHz is { } fInit)
            {
                f = fInit;
                double df0 = fInit - f0;
                pGov = -(df0 / f0) / cfg.DroopPu * pResp;
                for (int s = 0; s < pSvc.Length; s++)
                {
                    SystemResponseService svc = cfg.SystemResponseServices[s];
                    pSvc[s] = SwingKernel.FastResponseTarget(
                        df0, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
                }
                double dEquilibrium = forming ? d + cfg.FormingDampingMwPerHz : d;
                pStanding = -(pGov + pSvc.Sum() - dEquilibrium * df0);
            }
            double nadir = f, nadirT = 0;
            double peakRocof = 0;

            for (int k = 0; k <= steps; k++)
            {
                double t = k * dt;
                double df = f - f0;

                double pEvent = 0;
                foreach (FrequencyEvent x in ev) if (x.AtSeconds <= t + dt * 1e-6) pEvent += x.DeltaPMw;   // GR-540: the engines' tolerant event rule

                if (!forming)
                {
                    double pGovTarget = -(df / f0) / cfg.DroopPu * pResp;
                    pGov += SwingKernel.GovernorLagIncrement(pGovTarget, pGov, cfg.GovernorTimeConstantS, dt);

                    double ffrTarget = 0;
                    if (cfg.FastResponseMw > 0 && Math.Abs(df) > cfg.FastResponseDeadbandHz)
                        ffrTarget = Math.Clamp(-ffrGain * df, -cfg.FastResponseMw, cfg.FastResponseMw);
                    pTsgb += (ffrTarget - pTsgb) / Math.Max(dt, cfg.FastResponseTimeConstantS) * dt;

                    double pServices = 0;
                    for (int s = 0; s < pSvc.Length; s++)
                    {
                        SystemResponseService svc = cfg.SystemResponseServices[s];
                        double target = SwingKernel.FastResponseTarget(
                            df, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
                        pSvc[s] += (target - pSvc[s]) / Math.Max(dt, svc.TimeConstantS) * dt;
                        pServices += pSvc[s];
                    }

                    double pImbalance = pEvent + pStanding + pGov + pTsgb + pServices - d * df;
                    double rocof = f0 / (2.0 * e) * pImbalance;

                    points.Add(new FrequencyPoint(baseTime.AddSeconds(t), f, rocof));
                    if (f < nadir) { nadir = f; nadirT = t; }
                    if (Math.Abs(rocof) > Math.Abs(peakRocof)) peakRocof = rocof;

                    f += rocof * dt;
                }
                else
                {
                    double fStart = f;
                    double dampTotal = d + cfg.FormingDampingMwPerHz;
                    int nSub = FormingKernel.SubStepCount(dt, f0, eForming);
                    double h = dt / nSub;
                    double rocofReported = 0.0;
                    for (int sub = 0; sub < nSub; sub++)
                    {
                        double dfSub = f - f0;

                        double pGovTarget = -(dfSub / f0) / cfg.DroopPu * pResp;
                        pGov += SwingKernel.GovernorLagIncrement(pGovTarget, pGov, cfg.GovernorTimeConstantS, h);

                        double ffrTarget = 0;
                        if (cfg.FastResponseMw > 0 && Math.Abs(dfSub) > cfg.FastResponseDeadbandHz)
                            ffrTarget = Math.Clamp(-ffrGain * dfSub, -cfg.FastResponseMw, cfg.FastResponseMw);
                        pTsgb += (ffrTarget - pTsgb) / Math.Max(h, cfg.FastResponseTimeConstantS) * h;

                        double pServices = 0;
                        for (int s = 0; s < pSvc.Length; s++)
                        {
                            SystemResponseService svc = cfg.SystemResponseServices[s];
                            double target = SwingKernel.FastResponseTarget(
                                dfSub, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
                            pSvc[s] += (target - pSvc[s]) / Math.Max(h, svc.TimeConstantS) * h;
                            pServices += pSvc[s];
                        }

                        double pForcing = pEvent + pStanding + pGov + pTsgb + pServices + cfg.FormingSetpointMw;
                        (double fNext, double rocof) = FormingKernel.SemiImplicitStep(f, f0, eForming, pForcing, dampTotal, h);
                        if (sub == 0) rocofReported = rocof;
                        f = fNext;
                    }

                    points.Add(new FrequencyPoint(baseTime.AddSeconds(t), fStart, rocofReported));
                    if (fStart < nadir) { nadir = fStart; nadirT = t; }
                    if (Math.Abs(rocofReported) > Math.Abs(peakRocof)) peakRocof = rocofReported;
                }
            }

            return new FrequencyResponse(peakRocof, nadir, nadirT, f, points);
        }
    }

    private sealed class OldLiveIntegrator
    {
        private readonly DynamicsConfig _cfg;
        private readonly double _f0, _e, _d, _pResp, _ffrGain, _eForming, _formingDamp, _formingSetpoint;
        private readonly bool _forming;
        private double _pGov, _pTsgb;
        private const double MaxInternalDt = 0.02;
        private const int MaxSubSteps = 4000;
        public double FrequencyHz { get; private set; }
        public double RocofHzPerS { get; private set; }

        public OldLiveIntegrator(double systemInertiaMwS, double baseDemandMw, DynamicsConfig? config = null)
        {
            _cfg = config ?? new DynamicsConfig();
            _f0 = _cfg.NominalHz;
            double demand = Math.Max(SwingKernel.MinDemandMw, baseDemandMw);
            _e = Math.Max(SwingKernel.MinInertiaMwS, systemInertiaMwS + _cfg.DeviceRotationalInertiaMwS);
            _d = _cfg.DampingPctPerHz / 100.0 * demand;
            _pResp = _cfg.ResponsiveFraction * demand;
            _ffrGain = _cfg.FastResponseMw / SwingKernel.DeviceFullDeliveryHz;
            _forming = _cfg.Forming != FormingMode.None;
            _eForming = _forming
                ? FormingKernel.EffectiveFormingInertiaMwS(systemInertiaMwS + _cfg.DeviceRotationalInertiaMwS, _cfg.VirtualInertiaMwS)
                : _e;
            _formingDamp = _cfg.FormingDampingMwPerHz;
            _formingSetpoint = _cfg.FormingSetpointMw;
            FrequencyHz = _f0;
        }

        public void Step(double dtSeconds, double eventMw)
        {
            if (dtSeconds <= 0) return;
            int nSub = _forming
                ? FormingKernel.SubStepCount(dtSeconds, _f0, _eForming)
                : Math.Clamp((int)Math.Ceiling(dtSeconds / Math.Min(dtSeconds, MaxInternalDt)), 1, MaxSubSteps);
            double h = dtSeconds / nSub;
            double f = FrequencyHz;

            for (int s = 0; s < nSub; s++)
            {
                double df = f - _f0;

                double pGovTarget = -(df / _f0) / _cfg.DroopPu * _pResp;
                _pGov += SwingKernel.GovernorLagIncrement(pGovTarget, _pGov, _cfg.GovernorTimeConstantS, h);

                double ffrTarget = 0;
                if (_cfg.FastResponseMw > 0 && Math.Abs(df) > _cfg.FastResponseDeadbandHz)
                    ffrTarget = Math.Clamp(-_ffrGain * df, -_cfg.FastResponseMw, _cfg.FastResponseMw);
                _pTsgb += (ffrTarget - _pTsgb) / Math.Max(h, _cfg.FastResponseTimeConstantS) * h;

                if (!_forming)
                {
                    double pImbalance = eventMw + _pGov + _pTsgb - _d * df;
                    double rocof = _f0 / (2.0 * _e) * pImbalance;
                    f += rocof * h;
                    RocofHzPerS = rocof;
                }
                else
                {
                    double pForcing = eventMw + _pGov + _pTsgb + _formingSetpoint;
                    (double fNext, double rocof) = FormingKernel.SemiImplicitStep(f, _f0, _eForming, pForcing, _d + _formingDamp, h);
                    f = fNext;
                    RocofHzPerS = rocof;
                }
            }
            FrequencyHz = f;
        }
    }

    private sealed class OldMultiDevice
    {
        private readonly DynamicsConfig _cfg;
        private readonly IReadOnlyList<FleetUnit> _units;
        private readonly double _f0, _e, _d, _pResp;
        private readonly double[] _pFfr;
        private readonly double[] _armAtS;
        private double _pGov, _clock;
        private const double MaxInternalDt = 0.02;
        private const int MaxSubSteps = 4000;
        public double FrequencyHz { get; private set; }
        public double RocofHzPerS { get; private set; }

        public OldMultiDevice(double baseInertiaMwS, double baseDemandMw, FleetSpec fleet, DynamicsConfig? config)
        {
            _cfg = (config ?? new DynamicsConfig()) with { DeviceRotationalInertiaMwS = 0.0, FastResponseMw = 0.0 };
            _units = fleet.Units;
            _f0 = _cfg.NominalHz;
            double demand = Math.Max(SwingKernel.MinDemandMw, baseDemandMw);
            _e = Math.Max(SwingKernel.MinInertiaMwS, baseInertiaMwS + fleet.TotalInertiaMwS);
            _d = _cfg.DampingPctPerHz / 100.0 * demand;
            _pResp = _cfg.ResponsiveFraction * demand;
            _pFfr = new double[_units.Count];
            _armAtS = new double[_units.Count];
            for (int u = 0; u < _units.Count; u++) _armAtS[u] = double.PositiveInfinity;
            FrequencyHz = _f0;
        }

        public double FleetFfrMw { get { double s = 0; foreach (double p in _pFfr) s += p; return s; } }

        public void Step(double dtSeconds, double eventMw)
        {
            if (dtSeconds <= 0) return;
            int nSub = Math.Clamp((int)Math.Ceiling(dtSeconds / Math.Min(dtSeconds, MaxInternalDt)), 1, MaxSubSteps);
            double h = dtSeconds / nSub;
            double f = FrequencyHz;

            for (int s = 0; s < nSub; s++)
            {
                double df = f - _f0;

                double pGovTarget = SwingKernel.GovernorTarget(df, _f0, _cfg.DroopPu, _pResp);
                _pGov += SwingKernel.GovernorLagIncrement(pGovTarget, _pGov, _cfg.GovernorTimeConstantS, h);

                double pFleet = 0;
                for (int u = 0; u < _units.Count; u++)
                {
                    FleetUnit unit = _units[u];
                    if (double.IsPositiveInfinity(_armAtS[u]) && Math.Abs(df) > unit.FastResponseDeadbandHz)
                        _armAtS[u] = _clock + unit.StaggerSeconds;
                    // GR-843 (test changed 2026-10-19): the engines compare the arming time with the clock to a millionth of
                    // a step, so this copy does too and stays their bit-for-bit twin (a stagger that rounds just above the
                    // step grid used to arm one step late).
                    double target = _clock >= _armAtS[u] - 1e-6 * h
                        ? SwingKernel.FastResponseTarget(df, unit.FastResponseMw, unit.FastResponseDeadbandHz)
                        : 0.0;
                    _pFfr[u] += (target - _pFfr[u]) / Math.Max(h, unit.FastResponseTimeConstantS) * h;
                    pFleet += _pFfr[u];
                }

                double pImbalance = eventMw + _pGov + pFleet - _d * df;
                double rocof = _f0 / (2.0 * _e) * pImbalance;
                f += rocof * h;
                RocofHzPerS = rocof;
                _clock += h;
            }
            FrequencyHz = f;
        }
    }

    private static class OldFleet
    {
        public static (FrequencyResponse Response, double PeakFleetFfr, double FfrAtNadir) Simulate(
            double baseInertiaMwS, double baseDemandMw, IEnumerable<FrequencyEvent> events, FleetSpec fleet, DynamicsConfig? config)
        {
            DynamicsConfig cfg = (config ?? new DynamicsConfig()) with { DeviceRotationalInertiaMwS = 0.0, FastResponseMw = 0.0 };
            double f0 = cfg.NominalHz;
            double e = Math.Max(SwingKernel.MinInertiaMwS, baseInertiaMwS + fleet.TotalInertiaMwS);
            double d = cfg.DampingPctPerHz / 100.0 * baseDemandMw;
            double pResp = cfg.ResponsiveFraction * baseDemandMw;
            double dt = cfg.StepSeconds;
            int steps = (int)Math.Round(cfg.DurationSeconds / dt);
            FrequencyEvent[] ev = events.OrderBy(x => x.AtSeconds).ToArray();

            var baseTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var points = new List<FrequencyPoint>(steps + 1);
            var pFfr = new double[fleet.Count];
            var armAtS = new double[fleet.Count];
            for (int u = 0; u < fleet.Count; u++) armAtS[u] = double.PositiveInfinity;

            double f = f0, pGov = 0.0, pStanding = 0.0;
            var pSvc = new double[cfg.SystemResponseServices.Count];
            if (cfg.InitialFrequencyHz is { } fInit)
            {
                f = fInit;
                double df0 = fInit - f0;
                pGov = SwingKernel.GovernorTarget(df0, f0, cfg.DroopPu, pResp);
                for (int s = 0; s < pSvc.Length; s++)
                {
                    SystemResponseService svc = cfg.SystemResponseServices[s];
                    pSvc[s] = SwingKernel.FastResponseTarget(
                        df0, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
                }
                pStanding = -(pGov + pSvc.Sum() - d * df0);
            }
            double peakRocof = 0, nadir = f, nadirT = 0, peakFleetFfr = 0, ffrAtNadir = 0;

            for (int k = 0; k <= steps; k++)
            {
                double t = k * dt;
                double df = f - f0;

                double pEvent = 0;
                foreach (FrequencyEvent x in ev) if (x.AtSeconds <= t + dt * 1e-6) pEvent += x.DeltaPMw;   // GR-540: the engines' tolerant event rule

                double pGovTarget = SwingKernel.GovernorTarget(df, f0, cfg.DroopPu, pResp);
                pGov += SwingKernel.GovernorLagIncrement(pGovTarget, pGov, cfg.GovernorTimeConstantS, dt);

                double pFleet = 0;
                for (int u = 0; u < fleet.Count; u++)
                {
                    FleetUnit unit = fleet.Units[u];
                    if (double.IsPositiveInfinity(armAtS[u]) && Math.Abs(df) > unit.FastResponseDeadbandHz)
                        armAtS[u] = t + unit.StaggerSeconds;
                    double target = t >= armAtS[u]
                        ? SwingKernel.FastResponseTarget(df, unit.FastResponseMw, unit.FastResponseDeadbandHz)
                        : 0.0;
                    pFfr[u] += (target - pFfr[u]) / Math.Max(dt, unit.FastResponseTimeConstantS) * dt;
                    pFleet += pFfr[u];
                }
                if (Math.Abs(pFleet) > Math.Abs(peakFleetFfr)) peakFleetFfr = pFleet;

                double pServices = 0;
                for (int s = 0; s < pSvc.Length; s++)
                {
                    SystemResponseService svc = cfg.SystemResponseServices[s];
                    double target = SwingKernel.FastResponseTarget(
                        df, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
                    pSvc[s] += (target - pSvc[s]) / Math.Max(dt, svc.TimeConstantS) * dt;
                    pServices += pSvc[s];
                }

                double pImbalance = pEvent + pStanding + pGov + pFleet + pServices - d * df;
                double rocof = f0 / (2.0 * e) * pImbalance;

                points.Add(new FrequencyPoint(baseTime.AddSeconds(t), f, rocof));
                if (f < nadir) { nadir = f; nadirT = t; ffrAtNadir = pFleet; }
                if (Math.Abs(rocof) > Math.Abs(peakRocof)) peakRocof = rocof;

                f += rocof * dt;
            }

            return (new FrequencyResponse(peakRocof, nadir, nadirT, f, points), peakFleetFfr, ffrAtNadir);
        }
    }

    private static class OldMultiDomain
    {
        // Pre-R080 MultiDomainSwing.Simulate with the old event placement (the domain with the largest summed RatedMva)
        // and the per-step responder copy. Propagation delay is zero in the comparison (tau = 0).
        public static (FrequencyResponse Coi, IReadOnlyList<DomainTrace> Domains) Simulate(
            IReadOnlyList<SpeedDomain> allDomains, double[,] couplingK, double[,] tauSeconds, double baseDemandMw,
            IEnumerable<FrequencyEvent> events, DynamicsConfig cfg)
        {
            SpeedDomain[] masses = allDomains.Where(d => d.IsSwingMass).ToArray();
            int n = masses.Length;
            double f0 = cfg.NominalHz;
            double dt = cfg.StepSeconds;
            int steps = (int)Math.Round(cfg.DurationSeconds / dt);
            FrequencyEvent[] ev = events.OrderBy(x => x.AtSeconds).ToArray();

            double[] eMass = masses.Select(m => m.AggregateInertiaMwS).ToArray();
            double eSync = eMass.Sum();
            double eTot = Math.Max(SwingKernel.MinInertiaMwS, eSync + cfg.DeviceRotationalInertiaMwS);
            if (eSync > 0 && cfg.DeviceRotationalInertiaMwS > 0)
            {
                double scale = eTot / eSync;
                for (int i = 0; i < n; i++) eMass[i] *= scale;
            }
            double eMassTot = eMass.Sum();
            double[] share = eMass.Select(e => e / eMassTot).ToArray();

            double d = cfg.DampingPctPerHz / 100.0 * baseDemandMw;
            double pResp = cfg.ResponsiveFraction * baseDemandMw;
            double ffrGain = cfg.FastResponseMw / SwingKernel.DeviceFullDeliveryHz;

            int lossDomain = 0;
            for (int i = 1; i < masses.Length; i++)
                if (masses[i].RatedMva > masses[lossDomain].RatedMva) lossDomain = i;

            var delaySteps = new int[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double tau = tauSeconds[i, j];
                    int ds = double.IsFinite(tau) && tau > 0.0 ? (int)Math.Round(tau / dt) : 0;
                    delaySteps[i, j] = Math.Clamp(ds, 0, steps);
                }
            var deltaHist = new List<double[]>(steps + 1);

            var baseTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double fStart = cfg.InitialFrequencyHz ?? f0;
            double[] fd = Enumerable.Repeat(fStart, n).ToArray();
            double[] delta = new double[n];
            var pts = new List<FrequencyPoint>[n];
            for (int i = 0; i < n; i++) pts[i] = new List<FrequencyPoint>(steps + 1);
            var coiPts = new List<FrequencyPoint>(steps + 1);

            double pGov = 0.0, pTsgb = 0.0, pStanding = 0.0;
            double[] pSvc = new double[cfg.SystemResponseServices.Count];
            if (cfg.InitialFrequencyHz is { } fInit)
            {
                double df0 = fInit - f0;
                pGov = SwingKernel.GovernorTarget(df0, f0, cfg.DroopPu, pResp);
                for (int s = 0; s < pSvc.Length; s++)
                {
                    SystemResponseService svc = cfg.SystemResponseServices[s];
                    pSvc[s] = SwingKernel.FastResponseTarget(
                        df0, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
                }
                pStanding = -(pGov + pSvc.Sum() - d * df0);
            }
            double[] peakRocof = new double[n];
            double[] nadir = Enumerable.Repeat(fStart, n).ToArray();
            double[] nadirT = new double[n];
            double coiPeak = 0.0, coiNadir = fStart, coiNadirT = 0.0;

            for (int k = 0; k <= steps; k++)
            {
                double t = k * dt;
                deltaHist.Add((double[])delta.Clone());

                double fCoi = 0.0;
                for (int i = 0; i < n; i++) fCoi += eMass[i] * fd[i];
                fCoi /= eMassTot;
                double dfCoi = fCoi - f0;

                double pEvent = 0.0;
                foreach (FrequencyEvent x in ev) if (x.AtSeconds <= t + dt * 1e-6) pEvent += x.DeltaPMw;   // GR-540: the engines' tolerant event rule

                double pGovTarget = SwingKernel.GovernorTarget(dfCoi, f0, cfg.DroopPu, pResp);
                pGov += SwingKernel.GovernorLagIncrement(pGovTarget, pGov, cfg.GovernorTimeConstantS, dt);

                double ffrTarget = 0.0;
                if (cfg.FastResponseMw > 0 && Math.Abs(dfCoi) > cfg.FastResponseDeadbandHz)
                    ffrTarget = Math.Clamp(-ffrGain * dfCoi, -cfg.FastResponseMw, cfg.FastResponseMw);
                pTsgb += (ffrTarget - pTsgb) / Math.Max(dt, cfg.FastResponseTimeConstantS) * dt;

                double pServices = 0.0;
                for (int s = 0; s < pSvc.Length; s++)
                {
                    SystemResponseService svc = cfg.SystemResponseServices[s];
                    double target = SwingKernel.FastResponseTarget(
                        dfCoi, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
                    pSvc[s] += (target - pSvc[s]) / Math.Max(dt, svc.TimeConstantS) * dt;
                    pServices += pSvc[s];
                }

                double pResponse = pStanding + pGov + pTsgb + pServices - d * dfCoi;

                double[] rocof = new double[n];
                double sumErocof = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double tie = 0.0;
                    for (int j = 0; j < n; j++)
                        if (j != i)
                        {
                            double dj = deltaHist[Math.Max(0, k - delaySteps[i, j])][j];
                            tie += couplingK[i, j] * (delta[i] - dj);
                        }
                    double fEvent = (i == lossDomain) ? pEvent : 0.0;
                    double fi = fEvent + share[i] * pResponse - tie;
                    rocof[i] = f0 / (2.0 * eMass[i]) * fi;
                    sumErocof += eMass[i] * rocof[i];

                    pts[i].Add(new FrequencyPoint(baseTime.AddSeconds(t), fd[i], rocof[i]));
                    if (fd[i] < nadir[i]) { nadir[i] = fd[i]; nadirT[i] = t; }
                    if (Math.Abs(rocof[i]) > Math.Abs(peakRocof[i])) peakRocof[i] = rocof[i];
                }

                double coiRocof = sumErocof / eMassTot;
                coiPts.Add(new FrequencyPoint(baseTime.AddSeconds(t), fCoi, coiRocof));
                if (fCoi < coiNadir) { coiNadir = fCoi; coiNadirT = t; }
                if (Math.Abs(coiRocof) > Math.Abs(coiPeak)) coiPeak = coiRocof;

                for (int i = 0; i < n; i++)
                {
                    fd[i] += rocof[i] * dt;
                    delta[i] += 2.0 * Math.PI * (fd[i] - f0) * dt;
                }
            }

            double settleCoi = 0.0;
            for (int i = 0; i < n; i++) settleCoi += eMass[i] * fd[i];
            settleCoi /= eMassTot;

            var domainTraces = new List<DomainTrace>(n);
            for (int i = 0; i < n; i++)
                domainTraces.Add(new DomainTrace(masses[i].Id, peakRocof[i], nadir[i], nadirT[i], fd[i], pts[i]));
            return (new FrequencyResponse(coiPeak, coiNadir, coiNadirT, settleCoi, coiPts), domainTraces);
        }
    }
}
