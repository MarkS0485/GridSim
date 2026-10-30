using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Frequency;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-10-29: frequency-dynamics, EMT and RMS-control findings (FB11 to FB27). See
/// BugHunt1029DynamicsTests.cs for the conventions. Each test fails on the code as it was and passes on the fix, except
/// where the test says it pins a documented convention.
/// </summary>
public sealed partial class BugHunt1029Tests
{
    // ---------------------------------------------------------------------------------------------------------------
    // BH11: SettleHz is the frequency one step after the last trace point (the documented convention, pinned).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH11_settle_frequency_is_one_step_past_the_last_trace_point_as_documented()
    {
        var cfg = new DynamicsConfig { DurationSeconds = 10.0 };
        FrequencyResponse r = FrequencyDynamics.Simulate(150_000, 30_000, new[] { new FrequencyEvent(1.0, -1000.0) }, cfg);
        FrequencyPoint last = r.Points[^1];
        // The loop records (t, f, rocof) and then advances f += rocof * dt; SettleHz is that advanced value, bit for bit.
        Assert.Equal(last.Hz + (last.RocofHzPerS * cfg.StepSeconds), r.SettleHz);
        Assert.NotEqual(last.Hz, r.SettleHz);   // the governor is still moving at 10 s, so the two differ
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH12: the RoCoF screen's stability pre-check and closed form use the inertia the swing integrates on.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH12_rocof_screen_counts_device_inertia_in_its_stability_check_and_matches_the_simulate()
    {
        // Demand 30 GW at the default 1 %/Hz damping: D = 300 MW/Hz, so the explicit-Euler bound at 20 ms and 50 Hz is
        // E > 0.02 * 50 * 300 / 4 = 75 MW.s. The system keeps 50 MW.s after the loss (below the bound on its own), and
        // a 1000 MW.s rotating device is fitted, so the swing integrates on 1050 MW.s, comfortably stable.
        var cfg = new DynamicsConfig { DeviceRotationalInertiaMwS = 1000.0, DurationSeconds = 5.0 };
        var loss = new Contingency("unit", 100.0, 50.0);
        RocofScreeningResult row = Assert.Single(RocofScreening.Screen(100.0, 30_000, new[] { loss }, cfg));

        // Before the fix the row was flagged not integrable (NaN nadir, every breach set) on 50 MW.s alone.
        Assert.True(row.Integrated);
        FrequencyResponse direct = FrequencyDynamics.Simulate(50.0, 30_000,
            new[] { new FrequencyEvent(FrequencyDynamics.DesignEventSeconds, -100.0) }, cfg);
        Assert.Equal(direct.NadirHz, row.NadirHz);
        Assert.Equal(direct.InitialRocofHzPerS, row.InitialRocofHzPerS);

        // Without the device the same row is not integrable, and its closed form is f0 * P / (2 E) on 50 MW.s.
        RocofScreeningResult bare = Assert.Single(RocofScreening.Screen(100.0, 30_000, new[] { loss }, new DynamicsConfig { DurationSeconds = 5.0 }));
        Assert.False(bare.Integrated);
        Assert.Equal(-50.0 * 100.0 / (2.0 * 50.0), bare.InitialRocofHzPerS);
    }

    [Fact]
    public void BH12_closed_form_rocof_includes_the_device_inertia_when_still_too_small()
    {
        // 10 MW.s left plus a 20 MW.s device: 30 MW.s, still under the 75 MW.s bound, so the closed form is reported,
        // now on 30 MW.s (it used to divide by 10 and ignore the device).
        var cfg = new DynamicsConfig { DeviceRotationalInertiaMwS = 20.0, DurationSeconds = 5.0 };
        RocofScreeningResult row = Assert.Single(RocofScreening.Screen(60.0, 30_000, new[] { new Contingency("u", 100.0, 50.0) }, cfg));
        Assert.False(row.Integrated);
        Assert.Equal(-50.0 * 100.0 / (2.0 * 30.0), row.InitialRocofHzPerS);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH18: the live swing integrators refuse an unstable damping step instead of publishing a diverging frequency.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH18_live_integrator_refuses_a_step_past_the_explicit_euler_damping_bound()
    {
        // 1 MW.s (the floor) against 300 MW/Hz of damping at a 20 ms sub-step: h f0 D / (2E) = 150, far above 2. The
        // batch Simulate refuses this; the live integrator used to step it into Infinity and NaN with no error.
        var fi = new FrequencyIntegrator(1.0, 30_000);
        var ex = Assert.Throws<InvalidOperationException>(() => fi.Step(0.02, -100.0));
        Assert.Contains("too small to integrate", ex.Message, StringComparison.Ordinal);
        Assert.Equal(50.0, fi.FrequencyHz);   // nothing was published
        Assert.Throws<InvalidOperationException>(() => FrequencyDynamics.Simulate(1.0, 30_000, new[] { new FrequencyEvent(1.0, -100.0) }));
    }

    [Fact]
    public void BH18_a_stable_live_run_is_unchanged_and_tracks_the_batch_swing()
    {
        // A GB-sized system: the guard only reads, so the live run still gives the batch trajectory step for step.
        var cfg = new DynamicsConfig { DurationSeconds = 3.0 };
        var fi = new FrequencyIntegrator(150_000, 30_000, cfg);
        FrequencyResponse batch = FrequencyDynamics.Simulate(150_000, 30_000, new[] { new FrequencyEvent(0.0, -1000.0) }, cfg);
        for (int k = 0; k < 100; k++)
        {
            Assert.Equal(batch.Points[k].Hz, fi.FrequencyHz);
            fi.Step(cfg.StepSeconds, -1000.0);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH19: an inertia constant on a base that is not a positive finite MVA is refused, not NaN.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void BH19_estimate_h_refuses_a_bad_base(double baseMva)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => InertiaEstimator.EstimateHSeconds(1000, -0.25, baseMva));
        Assert.Equal("baseMva", ex.ParamName);
        Assert.Equal(50.0 * 1000.0 / (2.0 * 0.25) / 200_000.0, InertiaEstimator.EstimateHSeconds(1000, -0.25, 200_000));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH16 / BH17: the three-phase EMT engine fails loud on a diverged trajectory and validates its network record.
    // ---------------------------------------------------------------------------------------------------------------

    private static ThreePhaseNetwork BHThreePhase(IReadOnlyList<ThreePhaseFault> faults, double amplitude = 1.0) => new(
        BusCount: 2,
        Sources: new[] { new ThreePhaseSource(0, amplitude, 50.0) },
        Branches: new[] { new ThreePhaseBranch(0, 1, 1.0, 1e-3, 0.2, 0.3e-3) },
        Shunts: new[] { new ThreePhaseShunt(1, G: 0.1) },
        Faults: faults);

    [Fact]
    public void BH16_a_non_finite_three_phase_trajectory_throws_instead_of_returning_nan()
    {
        // A NaN source amplitude drives every node NaN from the first step. It used to come back as a NaN-filled Vabc.
        var eng = new ThreePhaseEmtEngine(BHThreePhase(Array.Empty<ThreePhaseFault>(), double.NaN), 20e-6, 50.0);
        var ex = Assert.Throws<InvalidOperationException>(() => eng.Simulate(0.01, probeBus: 1));
        Assert.Contains("diverged", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BH17_a_fault_on_a_phase_or_bus_that_does_not_exist_or_with_zero_resistance_is_refused()
    {
        ThreePhaseFault Good() => new(1, new[] { 0 }, 0.01, 0.04, 0.09);
        Assert.Throws<ArgumentException>(() => new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() with { Phases = new[] { 3 } } }), 20e-6));
        Assert.Throws<ArgumentException>(() => new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() with { Phases = new[] { -1 } } }), 20e-6));
        Assert.Throws<ArgumentException>(() => new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() with { Phases = new[] { 0, 0 } } }), 20e-6));
        Assert.Throws<ArgumentException>(() => new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() with { Phases = Array.Empty<int>() } }), 20e-6));
        Assert.Throws<ArgumentException>(() => new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() with { Bus = 2 } }), 20e-6));
        Assert.Throws<ArgumentException>(() => new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() with { Rf = 0.0 } }), 20e-6));
        Assert.Throws<ArgumentException>(() => new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() with { Rf = -0.1 } }), 20e-6));
        Assert.Throws<ArgumentException>(() => new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() with { ClearSeconds = 0.04 } }), 20e-6));
        // Phase 3 on bus 0 used to land on bus 1 phase a (node 3 * 0 + 3): the same fault as this valid one.
        _ = new ThreePhaseEmtEngine(BHThreePhase(new[] { Good() }), 20e-6);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH21: a located event on the lumped (single-domain) path is recorded, with a rule saying its bus was not used.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH21_a_located_event_on_the_single_domain_path_says_its_location_was_not_used()
    {
        var m = new GridModel
        {
            Name = "bh21",
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq) },
            Generators = new Generator[] { new(Bus: 1, Pg: 500, MBase: 1000, H: 5.0, Type: "ccgt") },
            Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05) },
        };
        var cfg = new DynamicsConfig { InertiaModel = InertiaModel.Distributed, DurationSeconds = 5 };
        var dm = new DistributedInertiaModel();
        FrequencyResponse r = dm.Simulate(m, 500, new[] { new FrequencyEvent(1.0, -50.0, AtBus: 2) }, cfg);
        DistributedResult d = dm.LastDistributedResult!;
        DomainTrace only = Assert.Single(d.Domains);
        EventPlacement p = Assert.Single(d.EventPlacements!);   // used to be null: the location vanished without a word
        Assert.Equal(2, p.AtBus);
        Assert.Equal(new[] { 1.0 }, p.Shares);
        Assert.Equal(new[] { only.DomainId }, p.DomainIds);
        Assert.Contains("(bus 2) is not used", p.Rule, StringComparison.Ordinal);
        // The integration is the lumped swing, unchanged.
        FrequencyResponse lumped = FrequencyDynamics.Simulate(m.SystemInertiaGvaS * 1000.0, 500, new[] { new FrequencyEvent(1.0, -50.0, AtBus: 2) }, cfg);
        Assert.Equal(lumped.NadirHz, r.NadirHz);
        Assert.Equal(lumped.SettleHz, r.SettleHz);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH24: the CCT bracket carries how many trials had unsettled converter steps (0 for a fleet with no converters).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH24_cct_bracket_reports_trials_with_unsettled_converter_steps_and_is_zero_without_converters()
    {
        // No bundled fixture drives the converter iteration into its 50-pass cap, so this pins the zero path and the
        // record's equality: a settled search's bracket is the one it always was (the count defaults to 0).
        (GridModel m, PowerFlowResult pf) = BHCase9();
        CctBracket b = RmsTransientStability.CriticalClearingBracket(m, pf, BHGenrou(m), 7);
        Assert.Equal(0, b.TrialsWithConverterIterationFailures);
        Assert.Equal(b, b with { TrialsWithConverterIterationFailures = 0 });
        Assert.NotEqual(b, b with { TrialsWithConverterIterationFailures = 1 });
    }

    private static (GridModel, PowerFlowResult) BHCase9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    private static List<SubtransientInit.MachineData> BHGenrou(GridModel model) =>
        model.Generators.Select(g => new SubtransientInit.MachineData(
            H: g.H, Damping: 0.0, Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();

    // ---------------------------------------------------------------------------------------------------------------
    // BH26: a negative time constant is refused by the block's own constructor, under its own parameter name.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH26_a_negative_time_constant_is_refused_by_name_and_zero_is_instant()
    {
        Assert.Equal("exciterTimeSeconds", Assert.Throws<ArgumentOutOfRangeException>(() => new Sexs(0.1, 1.0, 50.0, -1.0)).ParamName);
        Assert.Equal("valveTimeSeconds", Assert.Throws<ArgumentOutOfRangeException>(() => new Tgov1(0.05, -1.0, 1.0, 5.0)).ParamName);
        Assert.Equal("valveTimeSeconds", Assert.Throws<ArgumentOutOfRangeException>(
            () => new Ieeeg1(0.05, 0.0, 0.0, -1.0, 0.3, 7.0, 0.5, 0.0)).ParamName);
        Assert.Equal("gateTimeSeconds", Assert.Throws<ArgumentOutOfRangeException>(() => new HydroGovernor(0.04, 0.3, 5.0, -1.0, 1.0)).ParamName);
        // 0 is the documented instant block and is accepted by each.
        _ = new Sexs(0.1, 1.0, 50.0, 0.0);
        _ = new Tgov1(0.05, 0.0, 1.0, 5.0);
        _ = new Ieeeg1(0.05, 0.0, 0.0, 0.0, 0.3, 7.0, 0.5, 0.0);
        _ = new HydroGovernor(0.04, 0.3, 5.0, 0.0, 1.0);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH27: on the legacy SEXS path a tripped machine's field is held, as on the field-controller path.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH27_a_tripped_machines_sexs_field_is_held_from_its_trip()
    {
        (GridModel m, PowerFlowResult pf) = BHCase9();
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(m, pf, BHGenrou(m));
        double Field0After(double duration)
        {
            var exc = p.Efd.Select(_ => new Sexs(0.1, 1.0, 1.0, 0.02)).ToArray();
            double[] vref = p.Efd.Select(e => 1.0 + e).ToArray();   // gain 1: the field settles near Vref - Vt
            MultiMachineSubtransient.Simulate(
                p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.Network!,
                60.0, 0.005, duration, exciters: exc, voltageReferencePu: vref,
                machineTrips: new[] { new RmsMachineTrip(0, 0.2) });
            return exc[0].FieldVoltage;
        }
        // Held from 0.2 s: the same field whether the run stops at 0.5 s or 1.0 s. It used to keep moving, driven by
        // |E''| of a machine with no terminal.
        Assert.Equal(Field0After(0.5), Field0After(1.0));
    }
}
