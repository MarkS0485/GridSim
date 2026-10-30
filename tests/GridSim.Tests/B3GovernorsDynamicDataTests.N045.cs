using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N045: GENROU saturation on |psi''| = |E''| applied to both axes (PSS/E GENROU), instead of E'q * Se(E'q) on the d-axis
/// only. Pinned: the open-circuit reduction to the R251 / Q9 increment, the saturated SMIB equilibrium (every flux
/// derivative zero), the exact collapse of an all-zero curve onto the unsaturated study, and the leakage check.
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    [Fact]
    public void N045_open_circuit_flux_reduces_to_the_q9_field_increment_exactly()
    {
        var sat = new SubtransientMachine.SaturationCurve(0.10, 0.30);
        foreach (double x in new[] { 0.5, 0.9, 1.0, 1.05, 1.1, 1.2, 1.3 })
        {
            (double d, double q) = sat.GenrouIncrements(x, 0.0, 0.9);
            AssertBits(sat.FieldIncrement(x), d, $"d-axis increment at {x}");
            Assert.Equal(0.0, q);
        }
    }

    [Fact]
    public void N045_the_increments_scale_with_each_axis_flux_and_the_q_axis_ratio()
    {
        var sat = new SubtransientMachine.SaturationCurve(0.10, 0.30);
        double eqpp = 0.9, edpp = 0.6, ratio = 0.85;
        double psi = Math.Sqrt(eqpp * eqpp + edpp * edpp);
        (double d, double q) = sat.GenrouIncrements(eqpp, edpp, ratio);
        Assert.Equal(eqpp * sat.Se(psi), d, 15);
        Assert.Equal(edpp * sat.Se(psi) * ratio, q, 15);
        // Below the knee there is nothing on either axis.
        (double d0, double q0) = sat.GenrouIncrements(0.5, 0.2, ratio);
        Assert.Equal(0.0, d0);
        Assert.Equal(0.0, q0);
        // The q-axis scale is (Xq - Xl)/(Xd - Xl); Xl must stay below X''d.
        Assert.Equal((1.7 - 0.15) / (1.8 - 0.15), SubtransientMachine.QAxisSaturationRatio(1.8, 1.7, 0.22, 0.15), 15);
        Assert.Throws<ArgumentOutOfRangeException>(() => SubtransientMachine.QAxisSaturationRatio(1.8, 1.7, 0.22, 0.22));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubtransientMachine.QAxisSaturationRatio(1.8, 1.7, 0.22, -0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubtransientMachine.QAxisSaturationRatio(1.8, 1.7, 0.22, double.NaN));
    }

    [Fact]
    public void N045_the_saturated_smib_equilibrium_has_every_flux_derivative_at_zero()
    {
        var p = new SubtransientMachine.Parameters(4.0, 0.0, 1.8, 0.3, 0.22, 1.7, 0.55, 0.25, 6.0, 0.04, 0.4, 0.06,
            0.2, 1.0, new SubtransientMachine.SaturationCurve(0.10, 0.30), LeakageReactance: 0.15);
        (double pm, double efd, double eqp, double edp, double eqpp, double eddp) = SubtransientMachine.Equilibrium(p, 0.5, 1.1);
        (double id, double iq) = SubtransientMachine.Currents(p, 0.5, eqpp, eddp);
        (double satD, double satQ) = p.SaturationIncrements(eqpp, eddp);
        Assert.True(satQ > 0.0, "the operating point should be above the knee with a q-axis flux");
        Assert.True(Math.Abs(efd - eqp - (p.Xd - p.Xdp) * id - satD) < 1e-12, "T'do driver");
        Assert.True(Math.Abs(eqp - eqpp - (p.Xdp - p.Xdpp) * id) < 1e-12, "T''do driver");
        Assert.True(Math.Abs(-edp + (p.Xq - p.Xqp) * iq - satQ) < 1e-12, "T'qo driver");
        Assert.True(Math.Abs(edp - eddp + (p.Xqp - p.Xqpp) * iq) < 1e-12, "T''qo driver");
        Assert.Equal(SubtransientMachine.ElectricalPower(p, 0.5, eqpp, eddp), pm);

        // And it is stationary when integrated.
        SubtransientMachine.Trace tr = SubtransientMachine.Simulate(p, 0.5, 0.0, eqp, edp, eqpp, eddp, pm, efd, 50.0, 0.005, 5.0);
        int last = tr.SpeedPu.Count - 1;
        Assert.True(Math.Abs(tr.SpeedPu[last]) < 1e-9);
        Assert.True(Math.Abs(tr.EdpPu[last] - edp) < 1e-9);
        Assert.True(Math.Abs(tr.EqpPu[last] - eqp) < 1e-9);
    }

    [Fact]
    public void N045_the_leakage_reactance_is_validated_on_the_smib_parameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubtransientMachine.Parameters(4.0, 0.0, 1.8, 0.3, 0.22, 1.7, 0.55,
            0.25, 6.0, 0.04, 0.4, 0.06, 0.2, 1.0, null, LeakageReactance: 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubtransientMachine.Parameters(4.0, 0.0, 1.8, 0.3, 0.22, 1.7, 0.55,
            0.25, 6.0, 0.04, 0.4, 0.06, 0.2, 1.0, null, LeakageReactance: -0.1));
    }

    [Fact]
    public void N045_an_all_zero_saturation_curve_is_exactly_the_unsaturated_rms_study()
    {
        // A curve with S(1.0) = 0 is identically zero, so the saturated code path (angle, field and both flux drivers)
        // must give exactly the unsaturated study's numbers: the two-axis form adds nothing where there is nothing.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        List<SubtransientInit.MachineData> Data(SubtransientMachine.SaturationCurve? sat)
            => model.Generators.Select(g => new SubtransientInit.MachineData(g.H, 0.0, 1.0, 0.30, 0.22, 0.65, 0.40, 0.22,
                8.0, 0.03, 0.40, 0.05, sat)).ToList();
        MultiMachineSubtransient.Result a = RmsTransientStability.Study(model, pf, Data(null), 7, 0.1, 0.08, durationSeconds: 2.0);
        MultiMachineSubtransient.Result b = RmsTransientStability.Study(model, pf, Data(new SubtransientMachine.SaturationCurve(0.0, 0.0)),
            7, 0.1, 0.08, durationSeconds: 2.0);
        for (int i = 0; i < a.Machines.Count; i++)
            for (int k = 0; k < a.Machines[i].SpeedPu.Count; k++)
            {
                Assert.True(a.Machines[i].SpeedPu[k] == b.Machines[i].SpeedPu[k], $"machine {i} speed {k}");
                Assert.True(a.Machines[i].EdpPu[k] == b.Machines[i].EdpPu[k], $"machine {i} E'd {k}");
                Assert.True(a.Machines[i].EqpPu[k] == b.Machines[i].EqpPu[k], $"machine {i} E'q {k}");
            }
    }
}
