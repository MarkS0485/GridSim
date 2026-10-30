using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R251 (ruling Q9, 2026-10-02): machine saturation follows the PSS/E GENROU convention. S(1.0), S(1.2) are
/// saturation FACTORS (Se stays the dimensionless curve through them); the field-current increment that enters the
/// field balance is x * Se(x) = B * (x - A)^2. These tests pin:
///   - the increment at the two fit points: 1.0 * S(1.0) and 1.2 * S(1.2) (0.10 and 0.36 for a 0.10 / 0.30 curve,
///     where the old convention added 0.30 at 1.2 pu);
///   - the increment is exactly the product x * Se(x), and zero below the knee and for an unsaturated curve;
///   - consistency of every caller: a saturated multi-machine fleet seeded by SubtransientInit (which adds the
///     increment to Efd) and integrated by MultiMachineSubtransient (which subtracts it) stays at equilibrium, and
///     its seeded field is the unsaturated field plus exactly E'q * Se(E'q) per machine.
/// </summary>
public class SaturationFieldIncrementTests
{
    [Fact]
    public void Field_increment_at_the_fit_points_is_the_PSSE_standard()
    {
        var sat = new SubtransientMachine.SaturationCurve(satAt1Pu: 0.10, satAt12Pu: 0.30);
        Assert.Equal(1.0 * sat.Se(1.0), sat.FieldIncrement(1.0));
        Assert.Equal(1.2 * sat.Se(1.2), sat.FieldIncrement(1.2));
        Assert.Equal(0.10, sat.FieldIncrement(1.0), 12);   // 1.0 * S(1.0)
        Assert.Equal(0.36, sat.FieldIncrement(1.2), 12);   // 1.2 * S(1.2): the old convention added 0.30 here
        Assert.Equal(0.0, sat.FieldIncrement(0.5));        // below the knee A = 0.777...
        Assert.True(sat.FieldIncrement(1.4) > sat.FieldIncrement(1.2));

        var none = new SubtransientMachine.SaturationCurve(0.0, 0.0);
        Assert.Equal(0.0, none.FieldIncrement(1.2));
    }

    [Fact]
    public void Saturation_term_on_the_parameters_is_the_field_increment()
    {
        var sat = new SubtransientMachine.SaturationCurve(0.10, 0.30);
        var p = new SubtransientMachine.Parameters(3.5, 0.0, 1.8, 0.3, 0.25, 1.7, 0.55, 0.25,
            8.0, 0.03, 0.4, 0.05, 0.1, 1.0, sat);
        foreach (double x in new[] { 0.5, 0.9, 1.0, 1.1, 1.2, 1.3 })
            Assert.Equal(x * sat.Se(x), p.SaturationTerm(x));
    }

    private static (GridModel Model, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case9(
        SubtransientMachine.SaturationCurve? sat)
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
        {
            double xdp = IeeeCases.Wscc9TransientReactancePu[gen.Bus];
            data.Add(new SubtransientInit.MachineData(H: gen.H, Damping: 0.0,
                Xd: 3.0 * xdp, Xdp: xdp, Xdpp: 0.8 * xdp, Xq: 2.5 * xdp, Xqp: 1.5 * xdp, Xqpp: 0.8 * xdp,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05, Saturation: sat));
        }
        return (model, pf, data);
    }

    [Fact]
    public void Seeded_field_carries_exactly_the_field_increment()
    {
        var sat = new SubtransientMachine.SaturationCurve(0.10, 0.30);
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> plain) = Case9(null);
        (_, _, List<SubtransientInit.MachineData> saturated) = Case9(sat);
        SubtransientInit.Prepared p0 = SubtransientInit.FromSolvedCase(model, pf, plain);
        SubtransientInit.Prepared p1 = SubtransientInit.FromSolvedCase(model, pf, saturated);
        // Moved 2026-10 (B3 N045, Ruling R1: Mark delegated EE calls 2026-10-04; source: Siemens PTI PSS/E Model Library,
        // GENROU block diagram): saturation is now evaluated on |psi''| = |E''| and applied to BOTH axes. Two pins encoded
        // the old d-axis-only convention and move:
        //   (1) "the flux state does not depend on saturation" (p0.Eqp0 == p1.Eqp0): with q-axis saturation the q-axis
        //       behaves as Xq,sat = X''q + (Xq - X''q)/(1 + s), s = Se(|E''|)*(Xq - Xl)/(Xd - Xl), so the rotor angle (the
        //       angle of V + jXq,sat I) and with it every dq state move for a machine above the knee;
        //   (2) Efd = Efd_unsat + E'q*Se(E'q): the d-axis increment is now E''q*Se(|E''|) at the saturated state.
        // Was: Assert.Equal(p0.Eqp0[i], p1.Eqp0[i]); Assert.Equal(p0.Efd[i] + sat.FieldIncrement(p0.Eqp0[i]), p1.Efd[i], 12);
        // The relation that holds instead, per machine, on the saturated preparation itself: Id follows from the two
        // d-axis EMFs (E'q - E''q = (X'd - X''d) Id), and Efd = E'q + (Xd - X'd) Id + E''q * Se(|E''|).
        // (Old / new values of Eqp0, Delta0 and Efd per machine: recorded at the batch run, B3 notes.)
        int moved = 0;
        for (int i = 0; i < p1.Efd.Length; i++)
        {
            MultiMachineSubtransient.Machine mc = p1.Machines[i];
            double id = (p1.Eqp0[i] - p1.Eqpp0[i]) / (mc.Xdp - mc.Xdpp);
            double psi = Math.Sqrt(p1.Eqpp0[i] * p1.Eqpp0[i] + p1.Edpp0[i] * p1.Edpp0[i]);
            Assert.Equal(p1.Eqp0[i] + (mc.Xd - mc.Xdp) * id + p1.Eqpp0[i] * sat.Se(psi), p1.Efd[i], 12);
            if (sat.Se(psi) > 0.0)
            {
                // Above the knee the q-axis saturation pulls the load angle back: arg(V + jX I) rises with X for a unit
                // exporting active power, and Xq,sat < Xq.
                Assert.True(p1.Delta0[i] < p0.Delta0[i], $"machine {i}: saturated angle {p1.Delta0[i]:R} vs {p0.Delta0[i]:R}");
                moved++;
            }
        }
        Assert.True(moved > 0, "case9's machines should sit above the 0.10 / 0.30 curve's knee");
    }

    [Fact]
    public void A_saturated_fleet_seeded_from_a_solved_case_stays_at_equilibrium()
    {
        // The seeded field adds E'q*Se(E'q) and the integrator subtracts the same term, so with no disturbance the
        // fleet must not move. A mismatch between the two callers would show as a drift of E'q from t = 0.
        var sat = new SubtransientMachine.SaturationCurve(0.10, 0.30);
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(sat);
        // Fault scheduled after the run ends: the network never changes.
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(model, pf, data, faultBusId: 7,
            onsetSeconds: 100.0, clearingSeconds: 0.1, stepSeconds: 0.005, durationSeconds: 5.0);
        Assert.True(r.StayedInSynchronism);
        foreach (MultiMachineSubtransient.MachineTrace m in r.Machines)
        {
            int last = m.EqpPu.Count - 1;
            Assert.True(Math.Abs(m.EqpPu[last] - m.EqpPu[0]) < 1e-9, $"E'q drifted {m.EqpPu[last] - m.EqpPu[0]:R}");
            Assert.True(Math.Abs(m.SpeedPu[last]) < 1e-9, $"speed drifted to {m.SpeedPu[last]:R}");
        }
    }
}
