using System;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the subtransient (GENROU/GENSAL-class) SMIB machine (<see cref="SubtransientMachine"/>)
/// against oracles at the machine-epsilon standard:
///   - REDUCTION TO TWO-AXIS: with X''d = X'd and X''q = X'q the damper drivers vanish and the fixed points
///     give E''q = E'q, E''d = E'd, so the currents, power and equilibrium are exactly the two-axis machine's;
///   - EXACT SUBTRANSIENT DECAY: with the axis' reactances collapsed the damper EMF is a pure first-order lag,
///     so it follows the analytic exponential to machine epsilon (the exact zero-order hold);
///   - EQUILIBRIUM: started at its six-state fixed point the machine is stationary;
///   - DETERMINISM: two identical runs are bit-for-bit equal.
/// </summary>
public class SubtransientMachineTests
{
    private const double F0 = 50.0;

    // A representative salient subtransient machine on a stiff SMIB network.
    private static SubtransientMachine.Parameters Salient() => new(
        InertiaSeconds: 4.0, DampingPu: 0.0,
        Xd: 1.8, Xdp: 0.3, Xdpp: 0.22, Xq: 1.7, Xqp: 0.55, Xqpp: 0.25,
        Tdop: 6.0, Tdopp: 0.04, Tqop: 0.4, Tqopp: 0.06,
        ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

    // ---- Reduction to two-axis: X'' = X' => E''q = E'q, E''d = E'd, same currents/power/equilibrium ----
    [Fact]
    public void With_subtransient_reactances_equal_to_transient_it_reduces_to_the_two_axis_machine()
    {
        var sub = new SubtransientMachine.Parameters(
            InertiaSeconds: 4.0, DampingPu: 0.0,
            Xd: 1.8, Xdp: 0.3, Xdpp: 0.3, Xq: 1.7, Xqp: 0.55, Xqpp: 0.55,   // X'' = X'
            Tdop: 6.0, Tdopp: 0.04, Tqop: 0.4, Tqopp: 0.06,
            ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);
        var two = new TwoAxisMachine.Parameters(
            InertiaSeconds: 4.0, DampingPu: 0.0,
            Xd: 1.8, Xdp: 0.3, Xq: 1.7, Xqp: 0.55, Tdop: 6.0, Tqop: 0.4,
            ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

        const double delta0 = 0.5, eqp0 = 1.1;
        (double pm, double efd, double eqp, double edp, double eqpp, double eddp) = SubtransientMachine.Equilibrium(sub, delta0, eqp0);
        (double pm2, double efd2, double edp2) = TwoAxisMachine.Equilibrium(two, delta0, eqp0);

        Assert.Equal(eqp0, eqpp, 12);        // E''q collapses to E'q
        Assert.Equal(edp2, eddp, 12);        // E''d collapses to E'd
        Assert.Equal(edp2, edp, 12);
        Assert.Equal(pm2, pm, 12);
        Assert.Equal(efd2, efd, 12);

        // The subtransient stator (on E''q, E''d) matches the two-axis stator (on E'q, E'd) at equilibrium.
        (double idSub, double iqSub) = SubtransientMachine.Currents(sub, delta0, eqpp, eddp);
        (double idTwo, double iqTwo) = TwoAxisMachine.Currents(two, delta0, eqp0, edp2);
        Assert.Equal(idTwo, idSub, 12);
        Assert.Equal(iqTwo, iqSub, 12);
        Assert.Equal(TwoAxisMachine.ElectricalPower(two, delta0, eqp0, edp2),
                     SubtransientMachine.ElectricalPower(sub, delta0, eqpp, eddp), 12);
    }

    // ---- Exact d-axis subtransient decay: a pure lag on E''q when Xd = X'd = X''d and E'q is held ----
    [Fact]
    public void D_axis_subtransient_flux_decays_on_the_exact_exponential()
    {
        // Xd = X'd = X''d makes the d-axis drivers vanish; with E'q started at Efd, E'q stays = Efd, so the
        // E''q equation is T''do dE''q/dt = E'q - E''q = Efd - E''q: a pure lag to Efd, independent of the rotor.
        var p = new SubtransientMachine.Parameters(
            InertiaSeconds: 4.0, DampingPu: 0.0,
            Xd: 0.3, Xdp: 0.3, Xdpp: 0.3, Xq: 1.7, Xqp: 0.55, Xqpp: 0.25,
            Tdop: 6.0, Tdopp: 0.05, Tqop: 0.4, Tqopp: 0.06,
            ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

        const double efd = 1.2, eqpp0 = 1.0;
        SubtransientMachine.Trace tr = SubtransientMachine.Simulate(
            p, delta0: 0.3, omega0: 0.0, eqp0: efd, edp0: 0.0, eqpp0, eddp0: 0.0,
            mechanicalPowerPu: 0.0, fieldVoltagePu: efd, F0, stepSeconds: 0.005, durationSeconds: 1.0);

        for (int k = 0; k < tr.EqppPu.Count; k++)
        {
            double analytic = efd + (eqpp0 - efd) * Math.Exp(-tr.TimeSeconds[k] / p.Tdopp);
            Assert.Equal(analytic, tr.EqppPu[k], 10);
        }
    }

    // ---- Exact q-axis subtransient decay: a pure lag on E''d when Xq = X'q = X''q and E'd starts at 0 ----
    [Fact]
    public void Q_axis_subtransient_flux_decays_on_the_exact_exponential()
    {
        var p = new SubtransientMachine.Parameters(
            InertiaSeconds: 4.0, DampingPu: 0.0,
            Xd: 1.8, Xdp: 0.3, Xdpp: 0.22, Xq: 0.55, Xqp: 0.55, Xqpp: 0.55,
            Tdop: 6.0, Tdopp: 0.04, Tqop: 0.4, Tqopp: 0.08,
            ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

        const double eddp0 = 0.4;
        SubtransientMachine.Trace tr = SubtransientMachine.Simulate(
            p, delta0: 0.3, omega0: 0.0, eqp0: 1.1, edp0: 0.0, eqpp0: 1.05, eddp0,
            mechanicalPowerPu: 0.0, fieldVoltagePu: 1.1, F0, stepSeconds: 0.005, durationSeconds: 1.0);

        for (int k = 0; k < tr.EddpPu.Count; k++)
        {
            double analytic = eddp0 * Math.Exp(-tr.TimeSeconds[k] / p.Tqopp);   // E'd stays 0 => E''d -> 0
            Assert.Equal(analytic, tr.EddpPu[k], 10);
        }
    }

    // ---- Equilibrium: started at its six-state fixed point, the machine is stationary ----
    [Fact]
    public void Started_in_equilibrium_the_machine_is_stationary()
    {
        var p = Salient();
        const double delta0 = 0.5, eqp0 = 1.1;
        (double pm, double efd, double eqp, double edp, double eqpp, double eddp) = SubtransientMachine.Equilibrium(p, delta0, eqp0);

        SubtransientMachine.Trace tr = SubtransientMachine.Simulate(
            p, delta0, omega0: 0.0, eqp, edp, eqpp, eddp, pm, efd, F0, stepSeconds: 0.005, durationSeconds: 10.0);

        int last = tr.SpeedPu.Count - 1;
        Assert.True(Math.Abs(tr.SpeedPu[last]) < 1e-9);      // no speed drift
        Assert.Equal(delta0, tr.DeltaRad[last], 8);          // no angle drift
        Assert.Equal(eqpp, tr.EqppPu[last], 8);              // damper fluxes at rest
        Assert.Equal(eddp, tr.EddpPu[last], 8);
    }

    // ---- Saturation curve: the scaled-quadratic fit passes through its two tabulated points ----
    [Fact]
    public void Saturation_curve_fits_its_two_points()
    {
        var sat = new SubtransientMachine.SaturationCurve(satAt1Pu: 0.10, satAt12Pu: 0.30);
        Assert.Equal(0.10, sat.Se(1.0), 12);
        Assert.Equal(0.30, sat.Se(1.2), 12);
        Assert.Equal(0.0, sat.Se(0.5), 12);                       // below the knee A: no saturation
        Assert.True(sat.Se(1.4) > sat.Se(1.2));                   // strictly rising above the knee

        var none = new SubtransientMachine.SaturationCurve(satAt1Pu: 0.0, satAt12Pu: 0.0);
        Assert.Equal(0.0, none.Se(1.2), 12);                      // an unsaturated curve is identically zero
    }

    // ---- Saturation raises the field voltage needed to hold the same operating point, by exactly E'q*Se(E'q) ----
    [Fact]
    public void Saturation_raises_the_equilibrium_field_by_the_saturation_term()
    {
        const double delta0 = 0.5, eqp0 = 1.1;
        var sat = new SubtransientMachine.SaturationCurve(0.10, 0.30);
        var pSat = Salient() with { Saturation = sat };

        (_, double efdUnsat, _, _, _, _) = SubtransientMachine.Equilibrium(Salient(), delta0, eqp0);
        (_, double efdSat, _, _, double eqppSat, double eddpSat) = SubtransientMachine.Equilibrium(pSat, delta0, eqp0);

        // Moved 2026-10 (Q9, authorised): the PSS/E GENROU convention (ruling Q9, R251). S(1.0), S(1.2) are
        // saturation FACTORS on the open-circuit curve, Ifd = x*(1 + Se(x)) in air-gap-line pu, so the field-current
        // increment at flux x is x*Se(x), not Se(x). The equilibrium field balance is
        // Efd = E'q + (Xd - X'd)*Id + E'q*Se(E'q), and Id depends only on (delta0, E'q0), which are the same for
        // both machines, so Efd_sat - Efd_unsat = E'q0*Se(E'q0) exactly. For this 0.10 / 0.30 curve at E'q0 = 1.1:
        // k = sqrt(3.6), A = (k - 1.2)/(k - 1) = 0.7771..., B = 0.1/(1 - A)^2 = 2.0131..., Se(1.1) =
        // B*(1.1 - A)^2/1.1 = 0.19078..., so the increment is 1.1*0.19078... = 0.20986... (it was 0.19078...).
        // Was: Assert.Equal(efdUnsat + sat.Se(eqp0), efdSat, 12);
        // Moved 2026-10 (B3 N045, Ruling R1: Mark delegated EE calls 2026-10-04; source: Siemens PTI PSS/E Model Library,
        // GENROU block diagram, saturation on |psi''| applied to both axes): the d-axis increment is psi''d * Se(|psi''|)
        // = E''q * Se(|E''|), evaluated on the subtransient flux magnitude, not E'q * Se(E'q) on the transient EMF alone.
        // Id still depends only on (delta0, E'q0), so Efd_sat - Efd_unsat is exactly that increment at the saturated
        // equilibrium's E'' (whose q-axis part E''d now carries the q-axis saturation too). For this machine (Xl = 0, so the
        // q-axis scale is Xq/Xd = 1.7/1.8): E''q = 1.06441320990246, E''d 0.365877384724260 -> 0.349342261501071 (the
        // q-axis saturation), |E''| = 1.12027474178683, increment 0.209868329805052 -> 0.225232592108473, Efd_sat
        // 1.97712064413393 -> 1.99248490643736 (Efd_unsat 1.76725231432888 unchanged).
        // Was (Q9): Assert.Equal(efdUnsat + eqp0 * sat.Se(eqp0), efdSat, 12);
        Assert.Equal(efdUnsat + eqppSat * sat.Se(Math.Sqrt(eqppSat * eqppSat + eddpSat * eddpSat)), efdSat, 12);
        Assert.True(efdSat > efdUnsat);                            // saturated iron needs more field
    }

    // ---- A saturated machine started at its (saturated) equilibrium is stationary ----
    [Fact]
    public void Saturated_machine_started_in_equilibrium_is_stationary()
    {
        var p = Salient() with { Saturation = new SubtransientMachine.SaturationCurve(0.10, 0.30) };
        const double delta0 = 0.5, eqp0 = 1.1;
        (double pm, double efd, double eqp, double edp, double eqpp, double eddp) = SubtransientMachine.Equilibrium(p, delta0, eqp0);

        SubtransientMachine.Trace tr = SubtransientMachine.Simulate(
            p, delta0, omega0: 0.0, eqp, edp, eqpp, eddp, pm, efd, F0, stepSeconds: 0.005, durationSeconds: 10.0);

        int last = tr.SpeedPu.Count - 1;
        Assert.True(Math.Abs(tr.SpeedPu[last]) < 1e-9);
        Assert.Equal(delta0, tr.DeltaRad[last], 8);
        Assert.Equal(eqp, tr.EqpPu[last], 8);                     // field flux held despite saturation
    }

    // ---- Determinism: two identical runs are bit-for-bit equal ----
    [Fact]
    public void Two_identical_runs_are_bit_for_bit_equal()
    {
        var p = Salient();
        const double delta0 = 0.35, eqp0 = 1.05;
        (double pm, double efd, double eqp, double edp, double eqpp, double eddp) = SubtransientMachine.Equilibrium(p, delta0, eqp0);

        SubtransientMachine.Trace RunOnce() => SubtransientMachine.Simulate(
            p, delta0, omega0: 0.02, eqp, edp, eqpp, eddp, pm, efd, F0, stepSeconds: 0.002, durationSeconds: 3.0);

        SubtransientMachine.Trace a = RunOnce(), b = RunOnce();
        for (int i = 0; i < a.DeltaRad.Count; i++)
        {
            Assert.True(a.DeltaRad[i] == b.DeltaRad[i]);
            Assert.True(a.SpeedPu[i] == b.SpeedPu[i]);
            Assert.True(a.EqppPu[i] == b.EqppPu[i]);
            Assert.True(a.EddpPu[i] == b.EddpPu[i]);
        }
    }
}
