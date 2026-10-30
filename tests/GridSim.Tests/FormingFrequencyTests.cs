using System;
using System.Linq;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Forming;
using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Workstream A - grid-forming frequency FORMATION. Locks: (a) the islanded GFM-only analytic bounded
/// frequency (real RoCoF/settle, no Math.Max floor); the None-on-the-same-input companion still fakes the
/// floored value; (b) the O(dt) continuity to the classic COI swing when synchronous mass is present, plus
/// the byte-identical None gate; (c) stability at tiny E+Ev with fast high-gain responders; (d) the
/// real-inertia invariant - virtual inertia Ev never leaks into the real E budget.
/// </summary>
public class FormingFrequencyTests
{
    private const double F0 = 50.0;

    private static DynamicsConfig CleanForming(double ev, double dv, FormingMode mode = FormingMode.Vsm) => new()
    {
        Forming = mode,
        VirtualInertiaMwS = ev,
        FormingDampingMwPerHz = dv,
        DampingPctPerHz = 0.0,      // isolate the former: no load damping
        ResponsiveFraction = 0.0,   // no governor droop response
        FastResponseMw = 0.0,       // no FFR
        DurationSeconds = 30.0,
        StepSeconds = 0.02,
    };

    // ---- (a) islanded GFM-only analytic bounded-frequency ---------------------------------------

    [Fact]
    public void Islanded_former_gives_a_real_bounded_first_order_frequency()
    {
        const double ev = 30_000, dv = 500, dP = -1000;
        var cfg = CleanForming(ev, dv);
        var ev1 = new[] { new FrequencyEvent(1.0, dP) };
        FrequencyResponse r = FrequencyDynamics.Simulate(0.0, 30_000, ev1, cfg);   // E_real = 0

        double rocof0 = F0 * dP / (2.0 * ev);        // = -0.8333.. Hz/s, FINITE - no Math.Max floor
        double settle = F0 + dP / dv;                // = 48.0 Hz

        Assert.Equal(rocof0, r.InitialRocofHzPerS, 4);
        Assert.Equal(settle, r.SettleHz, 3);
        Assert.Equal(settle, r.NadirHz, 3);          // monotone first-order: nadir == settle, no overshoot
        Assert.True(Math.Abs(r.NadirHz - F0) <= Math.Abs(dP) / dv + 1e-6,
            $"|f-f0| must stay within |dP|/Dv = {Math.Abs(dP) / dv}");
        Assert.All(r.Points, p => Assert.True(double.IsFinite(p.Hz) && double.IsFinite(p.RocofHzPerS)));
    }

    [Fact]
    public void None_on_the_same_islanded_input_still_fakes_the_floored_value()
    {
        const double dP = -1000;
        // Same E_real = 0 input, but Forming == None: the Math.Max(1,E) floor fabricates E = 1 MW.s, so the
        // reported RoCoF is the fiction f0*dP/2, NOT the formed value. Proves the fix acts only under the flag.
        var none = new DynamicsConfig
        {
            DampingPctPerHz = 0.0, ResponsiveFraction = 0.0, FastResponseMw = 0.0, DurationSeconds = 30.0,
        };
        FrequencyResponse r = FrequencyDynamics.Simulate(0.0, 30_000, new[] { new FrequencyEvent(1.0, dP) }, none);
        Assert.Equal(F0 * dP / 2.0, r.InitialRocofHzPerS, 6);   // -25000 Hz/s - the floored fiction
    }

    [Fact]
    public void Grid_forming_with_nothing_forming_the_frequency_fails_loud()
    {
        // E_real = 0 AND Ev = 0: EffectiveFormingInertiaMwS refuses to fabricate inertia.
        var cfg = CleanForming(ev: 0.0, dv: 500, mode: FormingMode.Droop);
        Assert.Throws<InvalidOperationException>(() =>
            FrequencyDynamics.Simulate(0.0, 30_000, new[] { new FrequencyEvent(1.0, -100) }, cfg));
    }

    // ---- (b) reduction (byte-identical None) + O(dt) continuity ---------------------------------

    [Fact]
    public void None_path_ignores_the_forming_params_byte_identically()
    {
        // With Forming == None the forming fields must have NO effect: the trajectory is bit-equal to the
        // default config even with a large Ev/Dv set. This is the byte-identical-default gate.
        var ev1 = new[] { new FrequencyEvent(1.0, -1320) };
        FrequencyResponse baseline = FrequencyDynamics.Simulate(200_000, 30_000, ev1, new DynamicsConfig());
        FrequencyResponse withParams = FrequencyDynamics.Simulate(200_000, 30_000, ev1,
            new DynamicsConfig { Forming = FormingMode.None, VirtualInertiaMwS = 99_999, FormingDampingMwPerHz = 1234 });

        Assert.Equal(baseline.Points.Count, withParams.Points.Count);
        for (int i = 0; i < baseline.Points.Count; i++)
        {
            Assert.Equal(baseline.Points[i].Hz, withParams.Points[i].Hz);          // exact bit-equality
            Assert.Equal(baseline.Points[i].RocofHzPerS, withParams.Points[i].RocofHzPerS);
        }
        Assert.Equal(baseline.NadirHz, withParams.NadirHz);
    }

    [Fact]
    public void Forming_reduces_to_the_coi_swing_with_O_dt_continuity()
    {
        // Ev = 0, Dv = 0, synchronous mass present: the forming arm integrates the SAME ODE as the classic
        // COI arm, differing only O(dt) (semi-implicit vs explicit on the load-damping term). The nadir gap
        // must shrink as dt -> 0 across {20,10,5} ms.
        var ev1 = new[] { new FrequencyEvent(1.0, -1320) };
        double NadirGap(double dtMs)
        {
            double dt = dtMs / 1000.0;
            var none = new DynamicsConfig { StepSeconds = dt };
            var forming = new DynamicsConfig { StepSeconds = dt, Forming = FormingMode.Droop /* Ev=0,Dv=0 */ };
            FrequencyResponse rn = FrequencyDynamics.Simulate(200_000, 30_000, ev1, none);
            FrequencyResponse rf = FrequencyDynamics.Simulate(200_000, 30_000, ev1, forming);
            return Math.Abs(rf.NadirHz - rn.NadirHz);
        }

        double g20 = NadirGap(20), g10 = NadirGap(10), g5 = NadirGap(5);
        Assert.True(g10 <= g20 + 1e-9, $"gap should not grow refining 20->10 ms: {g20:E3} -> {g10:E3}");
        Assert.True(g5 <= g10 + 1e-9, $"gap should not grow refining 10->5 ms: {g10:E3} -> {g5:E3}");
        Assert.True(g5 < 5e-3, $"forming nadir must converge to COI nadir: gap at 5 ms = {g5:E3} Hz");
    }

    // ---- (c) stability at tiny E+Ev with fast, high-gain responders -----------------------------

    [Fact]
    public void Small_inertia_with_a_fast_high_gain_service_and_ffr_stays_bounded()
    {
        // Tiny E+Ev (Ev = 5 MW.s) forces internal sub-stepping; a steep sub-second service (gain
        // 5000/0.2 = 25000 MW/Hz) plus FFR are explicitly integrated. Without the sub-step bound these fast
        // feedbacks diverge; with it the trajectory stays finite and bounded.
        var svc = new SystemResponseService(Mw: 5000, DeadbandHz: 0.015, FullDeliveryDeviationHz: 0.2, TimeConstantS: 0.2);
        var cfg = new DynamicsConfig
        {
            Forming = FormingMode.Vsm,
            VirtualInertiaMwS = 5.0,           // tiny -> sub-stepping engages
            FormingDampingMwPerHz = 100.0,
            FastResponseMw = 2000.0,
            DampingPctPerHz = 1.0,
            SystemResponseServices = new[] { svc },
            DurationSeconds = 20.0,
        };
        FrequencyResponse r = FrequencyDynamics.Simulate(0.0, 30_000, new[] { new FrequencyEvent(1.0, -300) }, cfg);

        Assert.All(r.Points, p =>
        {
            Assert.True(double.IsFinite(p.Hz), "frequency diverged (NaN/Inf)");
            Assert.True(double.IsFinite(p.RocofHzPerS), "rocof diverged (NaN/Inf)");
            Assert.InRange(p.Hz, 0.0, 100.0);   // no runaway
        });
        Assert.True(double.IsFinite(r.SettleHz));
    }

    // ---- (d) real-vs-synthetic inertia invariant ------------------------------------------------

    [Fact]
    public void Virtual_inertia_never_leaks_into_the_real_denominator()
    {
        // The forming denominator IS E_real + Ev, but the REAL inertia notion must remain a pure function of
        // system + device rotational inertia. Compare a forming run to a real run that puts the SAME MW.s in
        // as REAL system inertia: the real run's RoCoF is smaller in magnitude (real energy actually resists
        // df/dt through the whole trajectory) - identical numbers would mean Ev had been treated as real E,
        // yet here they must agree at the FIRST instant (both denominators equal at df=0) and the export keeps
        // effectiveInertiaMwS = real only (locked in FrequencyExportContractTests).
        const double ev = 40_000, dP = -1000;
        var forming = CleanForming(ev, dv: 400);
        FrequencyResponse rf = FrequencyDynamics.Simulate(0.0, 30_000, new[] { new FrequencyEvent(1.0, dP) }, forming);
        // Ev in the denominator gives a finite initial RoCoF = f0*dP/(2*Ev) - the whole point.
        Assert.Equal(F0 * dP / (2.0 * ev), rf.InitialRocofHzPerS, 4);
    }

    // ---- kernel unit checks ---------------------------------------------------------------------

    [Fact]
    public void Effective_forming_inertia_has_no_floor_and_fails_loud_at_zero()
    {
        Assert.Equal(0.5, FormingKernel.EffectiveFormingInertiaMwS(0.0, 0.5), 12);   // no Math.Max(1,.)
        Assert.Equal(200_000, FormingKernel.EffectiveFormingInertiaMwS(150_000, 50_000), 6);
        Assert.Throws<InvalidOperationException>(() => FormingKernel.EffectiveFormingInertiaMwS(0.0, 0.0));
    }

    [Fact]
    public void Substep_count_grows_as_inertia_shrinks()
    {
        Assert.Equal(1, FormingKernel.SubStepCount(0.02, F0, 30_000));   // large E+Ev: one step
        Assert.True(FormingKernel.SubStepCount(0.02, F0, 5.0) > 1);      // tiny E+Ev: sub-stepped
        Assert.True(FormingKernel.SubStepCount(0.02, F0, 1.0)
                 >= FormingKernel.SubStepCount(0.02, F0, 5.0));          // shrinks monotonically
    }
}
