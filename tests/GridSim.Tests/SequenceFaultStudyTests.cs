using System;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The single-line-to-ground fault study (the earth fault the positive-sequence engine declines). For a
/// balanced diagonal line the three sequence impedances collapse to z, giving fault current E/(z + zf); the
/// study also accumulates Thevenin impedance down a radial feeder, and extracts sequence impedances of a
/// symmetric line correctly.
/// </summary>
public class SequenceFaultStudyTests
{
    private static MultiPhaseModel TwoBus(Complex z) => new(
        new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false) },
        new[] { new PhaseBranch(0, 1, PhaseImpedance.Diagonal(z)) },
        Array.Empty<PhaseLoad>());

    [Fact]
    public void Bolted_slg_on_a_diagonal_line_gives_E_over_z()
    {
        var z = new Complex(0.01, 0.05);
        SlgFaultResult r = SequenceFaultStudy.SingleLineToGround(TwoBus(z), faultBusId: 1);

        // Diagonal line: z0 = z1 = z2 = z, so If = 3E/(3z) = E/z with E = 1.
        Complex expected = Complex.One / z;
        Assert.True(Math.Abs(r.FaultCurrentPu - expected.Magnitude) < 1e-9,
            $"fault current {r.FaultCurrentPu} vs {expected.Magnitude}");
        Assert.True((r.I0 - r.I1).Magnitude < 1e-12 && (r.I1 - r.I2).Magnitude < 1e-12); // I0=I1=I2 for SLG
        Assert.True((r.Z0 - z).Magnitude < 1e-12 && (r.Z1 - z).Magnitude < 1e-12);
    }

    [Fact]
    public void Fault_impedance_reduces_the_current()
    {
        var z = new Complex(0.01, 0.05);
        double bolted = SequenceFaultStudy.SingleLineToGround(TwoBus(z), 1).FaultCurrentPu;
        double withZf = SequenceFaultStudy.SingleLineToGround(TwoBus(z), 1, faultZ: new Complex(0.1, 0)).FaultCurrentPu;
        Assert.True(withZf < bolted, $"fault-Z current {withZf} should be below bolted {bolted}");
    }

    [Fact]
    public void Thevenin_impedance_accumulates_down_a_radial_feeder()
    {
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false), new PhaseBus(2, false) },
            new[] { new PhaseBranch(0, 1, z), new PhaseBranch(1, 2, z) },
            Array.Empty<PhaseLoad>());

        double near = SequenceFaultStudy.SingleLineToGround(model, 1).FaultCurrentPu;
        SlgFaultResult far = SequenceFaultStudy.SingleLineToGround(model, 2);
        Assert.True(far.FaultCurrentPu < near, "a fault further down the feeder sees more impedance, less current");
        Assert.True((far.Z1 - new Complex(0.04, 0.12)).Magnitude < 1e-9, $"two-segment Z1 {far.Z1}");
    }

    [Fact]
    public void All_faults_summary_agrees_with_the_individual_studies()
    {
        var z = new Complex(0.01, 0.05);
        MultiPhaseModel model = TwoBus(z);
        SequenceFaultStudy.FaultSummary all = SequenceFaultStudy.AllFaults(model, 1);

        Assert.Equal(SequenceFaultStudy.ThreePhaseFaultCurrentPu(model, 1), all.ThreePhase, 12);
        Assert.Equal(SequenceFaultStudy.SingleLineToGround(model, 1).FaultCurrentPu, all.SingleLineGround, 12);
        Assert.Equal(SequenceFaultStudy.LineToLineFaultCurrentPu(model, 1), all.LineLine, 12);
        Assert.Equal(SequenceFaultStudy.LineToLineGroundFaultCurrentPu(model, 1), all.LineLineGround, 12);
    }

    [Fact]
    public void Three_phase_fault_on_a_diagonal_line_is_E_over_z1()
    {
        var z = new Complex(0.01, 0.05);
        double i3 = SequenceFaultStudy.ThreePhaseFaultCurrentPu(TwoBus(z), faultBusId: 1);
        Assert.True(Math.Abs(i3 - 1.0 / z.Magnitude) < 1e-9, $"3ph current {i3} vs {1.0 / z.Magnitude}");
    }

    [Fact]
    public void Line_to_line_fault_on_a_diagonal_line_is_sqrt3_times_positive_sequence()
    {
        var z = new Complex(0.01, 0.05);
        // Diagonal line: Z1 = Z2 = z, so I1 = E/(2z) and |I_faultB| = sqrt(3)|I1| = sqrt(3)/(2|z|).
        double ll = SequenceFaultStudy.LineToLineFaultCurrentPu(TwoBus(z), faultBusId: 1);
        double expected = Math.Sqrt(3.0) / (2.0 * z.Magnitude);
        Assert.True(Math.Abs(ll - expected) < 1e-9, $"LL current {ll} vs {expected}");

        // A line-to-line fault draws less than a bolted three-phase-equivalent SLG on this line.
        double slg = SequenceFaultStudy.SingleLineToGround(TwoBus(z), 1).FaultCurrentPu;
        Assert.True(ll < slg);
    }

    [Fact]
    public void Double_line_to_ground_fault_ground_current_on_a_diagonal_line()
    {
        var z = new Complex(0.01, 0.05);
        // Diagonal (Z0=Z1=Z2=z), bolted ground: |3 I0| works out to 1/|z|.
        double llg = SequenceFaultStudy.LineToLineGroundFaultCurrentPu(TwoBus(z), faultBusId: 1);
        Assert.True(Math.Abs(llg - 1.0 / z.Magnitude) < 1e-9, $"LLG ground current {llg} vs {1.0 / z.Magnitude}");
    }

    [Fact]
    public void Symmetric_line_sequence_impedances_separate_zero_from_positive()
    {
        // z_self on the diagonal, z_mutual off it: z1 = z_self - z_mutual, z0 = z_self + 2 z_mutual.
        Complex zs = new(0.1, 0.4), zm = new(0.03, 0.1);
        var m = new Complex[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) m[i, j] = i == j ? zs : zm;

        (Complex z0, Complex z1, Complex z2) = SequenceFaultStudy.LineSequenceImpedances(new PhaseImpedance(m));
        Assert.True((z1 - (zs - zm)).Magnitude < 1e-12);
        Assert.True((z2 - (zs - zm)).Magnitude < 1e-12);
        Assert.True((z0 - (zs + 2.0 * zm)).Magnitude < 1e-12);
    }
}
