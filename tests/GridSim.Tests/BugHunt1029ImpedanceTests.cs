using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29 (GridSim 2.0.183), impedance-scan findings. See BugHunt1029DynamicsTests.cs.</summary>
public sealed partial class BugHunt1029Tests
{
    // ---------------------------------------------------------------------------------------------------------------
    // BH14: the SISO criterion must pair the converter's dq-frame Y_pp with the grid's dq-frame Z_pp = Z(f + f0).
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A converter with NO cross-sequence coupling: Y_dq(jw) = g(jw) I with g = -k wn^2 / (s^2 + 2 zeta wn s + wn^2) at
    /// s = jw (identical d and q channels: a stable second-order negative conductance, PLL-like). With Y = g I and
    /// the RL Thevenin Z_dq = (R + jwL) I + w0 L J, det(I + Y Z) = (1 + g Z_pp)(1 + g Z_nn) exactly, so the scalar
    /// sequence-domain criterion and the full MIMO criterion must give the same verdict at every SCR.
    /// </summary>
    private sealed class BH14DecoupledConverter(double k, double fnHz, double zeta) : IConverterImpedanceModel
    {
        public int Bus => 2;
        public AssetKind Kind => AssetKind.GridFollowingInverter;
        public ImpedanceModelDetail Detail => ImpedanceModelDetail.PllOuter;
        public ProvenanceTag Provenance => ProvenanceTag.CuratedPlaceholder;
        public ConverterOperatingPoint OperatingPoint => new(1.0, 0.0, 1.0, 0.0, 1.0, 0.0);
        private Complex G(double omega)
        {
            double wn = 2.0 * Math.PI * fnHz;
            return new Complex(-k * wn * wn, 0.0) / new Complex((wn * wn) - (omega * omega), 2.0 * zeta * wn * omega);
        }
        public Complex2x2 AdmittanceDq(double omega) => Complex2x2.Diagonal(G(omega));
        public Complex PositiveSequenceAdmittance(double omega)
        {
            Complex2x2 y = AdmittanceDq(omega);
            return 0.5 * ((y.M11 + y.M22) + (Complex.ImaginaryOne * (y.M21 - y.M12)));
        }
    }

    private static ImpedanceStabilityOptions BH14Options(StabilityCriterion c) => new()
    {
        FMinHz = 0.1, FMaxHz = 250.0, PointsPerDecade = 60, F0Hz = 50.0, Criterion = c,
        UseCanonicalThevenin = true, Scr = 3.0, XOverR = 10.0, PRatedPu = 1.0,
        ScrMin = 0.2, ScrMax = 6.0, ScrSteps = 59,
    };

    [Fact]
    public void BH14_siso_grid_impedance_is_the_dq_frame_positive_sequence_entry_z_of_f_plus_f0()
    {
        ImpedanceStabilityOptions opt = BH14Options(StabilityCriterion.Siso);
        ImpedanceStabilityResult r = ImpedanceStabilityScan.Run(opt, new BH14DecoupledConverter(0.5, 10.0, 0.7));
        foreach (FreqSample s in r.Frequency)
        {
            // Bit for bit the canonical Thevenin at the stationary frequency f + f0 ...
            Complex expected = GridImpedanceAssembler.CanonicalTheveninImpedance(opt.Scr, opt.XOverR, opt.PRatedPu, s.FHz + opt.F0Hz, opt.F0Hz);
            Assert.Equal(expected, s.ZGrid);
            // ... which is the modified-sequence reduction of the exact dq Thevenin at the same dq frequency (to rounding:
            // R + j(wL + w0L) formed two ways).
            Complex2x2 zdq = GridImpedanceAssembler.CanonicalTheveninDq(opt.Scr, opt.XOverR, opt.PRatedPu, 2.0 * Math.PI * s.FHz, opt.F0Hz);
            Complex zpp = 0.5 * ((zdq.M11 + zdq.M22) + (Complex.ImaginaryOne * (zdq.M21 - zdq.M12)));
            Assert.True((zpp - s.ZGrid).Magnitude <= 1e-13 * s.ZGrid.Magnitude, $"f = {s.FHz}: Z_pp {zpp} vs ZGrid {s.ZGrid}");
        }
    }

    [Fact]
    public void BH14_siso_and_mimo_agree_at_every_scr_for_a_converter_without_cross_sequence_coupling()
    {
        var conv = new BH14DecoupledConverter(0.5, 10.0, 0.7);
        ImpedanceStabilityResult siso = ImpedanceStabilityScan.Run(BH14Options(StabilityCriterion.Siso), conv);
        ImpedanceStabilityResult mimo = ImpedanceStabilityScan.Run(BH14Options(StabilityCriterion.Mimo), conv);

        Assert.Equal(mimo.Nyquist.UnstableModeCount, siso.Nyquist.UnstableModeCount);
        Assert.Equal(mimo.ScrSweep.Count, siso.ScrSweep.Count);
        for (int j = 0; j < mimo.ScrSweep.Count; j++)
        {
            Assert.Equal(mimo.ScrSweep[j].Stable, siso.ScrSweep[j].Stable);
            Assert.Equal(mimo.ScrSweep[j].Encirclements, siso.ScrSweep[j].Encirclements);
        }
        Assert.Equal(mimo.BoundaryFound, siso.BoundaryFound);
        Assert.Equal(mimo.CriticalScr, siso.CriticalScr);
        // The case is not trivial: the sweep holds both stable and unstable grids.
        Assert.Contains(mimo.ScrSweep, s => s.Stable);
        Assert.Contains(mimo.ScrSweep, s => !s.Stable);
    }

    [Fact]
    public void BH14_the_old_frame_mixed_pairing_disagrees_with_mimo_somewhere_in_the_sweep()
    {
        // The defect, reproduced by hand: L = Z(f) * Y_pp(f) wound with the plain conjugate mirror (the pairing the
        // SISO criterion used to form). Against the exact MIMO verdict it gets at least one SCR of this sweep wrong,
        // which is why the criterion was changed.
        var conv = new BH14DecoupledConverter(0.5, 10.0, 0.7);
        ImpedanceStabilityOptions opt = BH14Options(StabilityCriterion.Mimo);
        ImpedanceStabilityResult mimo = ImpedanceStabilityScan.Run(opt, conv);
        double[] wind = ImpedanceStabilityScan.LogGrid(ImpedanceStabilityScan.WindingGridMinHz, ImpedanceStabilityScan.WindingGridMaxHz,
            ImpedanceStabilityScan.WindingGridMinPointsPerDecade);
        int disagreements = 0;
        foreach (ScrSample s in mimo.ScrSweep)
        {
            var z = wind.Select(f => GridImpedanceAssembler.CanonicalTheveninImpedance(s.Scr, opt.XOverR, opt.PRatedPu, f, opt.F0Hz)).ToArray();
            var y = wind.Select(f => conv.PositiveSequenceAdmittance(2.0 * Math.PI * f)).ToArray();
            if (MinorLoopNyquist.Siso(wind, z, y).Stable != s.Stable) disagreements++;
        }
        Assert.True(disagreements > 0, "the old pairing was expected to misjudge at least one SCR");
    }
}
