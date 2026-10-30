using System;
using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Property invariants of the impedance screen (critique FIXes 2-5): encirclement invariance under grid
/// refinement; the SISO positive-sequence == the correct modified-sequence dq-diagonal in the decoupled limit;
/// the full 2x2 eigenloci genuinely differ from the SISO loci under coupling; the low-SCR/low-Vd0 guard; and
/// the signed-h conjugate-symmetry of the grid driving-point on a network WITH a shunt reactor.
/// </summary>
public class ImpedanceStabilityPropertyTests
{
    private static GridFollowingImpedance Gfl(double id0, double iq0, double fbw = 5.0,
        ImpedanceModelDetail detail = ImpedanceModelDetail.PllOuter)
    {
        var op = new ConverterOperatingPoint(1.0, 0.0, id0, iq0, id0, -iq0);
        return GridFollowingImpedance.FromParameters(2, op, detail, ProvenanceTag.CuratedPlaceholder, fbw, 0.707, 0.0);
    }

    private static ImpedanceStabilityResult Scan(IConverterImpedanceModel conv, double scr, int ppd, StabilityCriterion c)
        => ImpedanceStabilityScan.Run(new ImpedanceStabilityOptions
        {
            FMinHz = 0.1, FMaxHz = 250, PointsPerDecade = ppd, F0Hz = 50, Criterion = c,
            UseCanonicalThevenin = true, Scr = scr, XOverR = 10.0, PRatedPu = 1.0, ScrSteps = 0,
        }, conv);

    [Theory]
    [InlineData(6.0)]   // stable
    [InlineData(0.3)]   // unstable (weak grid)
    public void Encirclement_count_is_invariant_under_grid_refinement(double scr)
    {
        var conv = Gfl(id0: 1.0, iq0: 0.2);
        int c1 = Scan(conv, scr, 80, StabilityCriterion.Mimo).Nyquist.UnstableModeCount;
        int c2 = Scan(conv, scr, 160, StabilityCriterion.Mimo).Nyquist.UnstableModeCount;
        int c4 = Scan(conv, scr, 320, StabilityCriterion.Mimo).Nyquist.UnstableModeCount;
        Assert.Equal(c1, c2);
        Assert.Equal(c1, c4);
    }

    [Fact]
    public void Siso_positive_sequence_equals_dq_diagonal_average_in_decoupled_limit()
    {
        // Iq0 = 0 => both off-diagonals vanish => Y_pp = 1/2 (Y_dd + Y_qq) (the correct modified-sequence limit).
        var conv = Gfl(id0: 0.8, iq0: 0.0);
        foreach (double f in new[] { 1.0, 8.0, 30.0, 90.0 })
        {
            double w = 2.0 * Math.PI * f;
            Complex2x2 y = conv.AdmittanceDq(w);
            Assert.Equal(0.0, y.M12.Magnitude, 12);
            Assert.Equal(0.0, y.M21.Magnitude, 12);
            Complex expected = 0.5 * (y.M11 + y.M22);
            Complex pp = conv.PositiveSequenceAdmittance(w);
            Assert.Equal(expected.Real, pp.Real, 12);
            Assert.Equal(expected.Imaginary, pp.Imaginary, 12);
        }
    }

    [Fact]
    public void Full_2x2_eigenloci_differ_from_the_diagonal_siso_loci_under_coupling()
    {
        // Iq0 != 0 => genuine cross-coupling => the 2x2 eigenvalues are NOT the diagonal entries.
        var conv = Gfl(id0: 1.0, iq0: 0.4);
        bool anyDiff = false;
        foreach (double f in new[] { 2.0, 10.0, 25.0, 60.0 })
        {
            double w = 2.0 * Math.PI * f;
            Complex2x2 y = conv.AdmittanceDq(w);
            Assert.True(y.OffDiagonalMagnitude > 1e-9);   // coupling present
            (Complex l1, Complex l2) = y.Eigenvalues();
            double dToDiag = Math.Min(
                Math.Min((l1 - y.M11).Magnitude, (l1 - y.M22).Magnitude),
                Math.Min((l2 - y.M11).Magnitude, (l2 - y.M22).Magnitude));
            if (dToDiag > 1e-6) anyDiff = true;
        }
        Assert.True(anyDiff, "the 2x2 eigenloci must differ from the diagonal SISO loci when cross-coupling is nonzero");
    }

    [Fact]
    public void Low_vd0_operating_point_is_clamped_and_produces_finite_admittance()
    {
        // A deeply depressed weak-grid terminal (Vd0 -> 0) must be clamped, not silently produce 1/Vd0 garbage.
        var op = ConverterOperatingPoint.Guarded(vmPu: 0.001, vaRad: 0.0, idPu: 1.0, iqPu: 0.2, pPu: 1.0, qPu: 0.2);
        Assert.True(op.Vd0WasClamped);
        Assert.Equal(ConverterOperatingPoint.MinVd0Pu, op.Vd0Pu, 12);

        var conv = GridFollowingImpedance.FromParameters(2, op, ImpedanceModelDetail.PllOuter,
            ProvenanceTag.CuratedPlaceholder, 20.0, 0.707, 0.0);
        foreach (double f in new[] { 0.1, 10.0, 100.0 })
        {
            Complex2x2 y = conv.AdmittanceDq(2.0 * Math.PI * f);
            Assert.True(double.IsFinite(y.M11.Real) && double.IsFinite(y.M22.Imaginary));
        }
        // And a full low-SCR scan yields a finite verdict (no NaN/Inf leakage).
        ImpedanceStabilityResult r = Scan(conv, 0.5, 80, StabilityCriterion.Mimo);
        Assert.True(double.IsFinite(r.Nyquist.MinDistToMinusOne));
    }

    [Fact]
    public void Driving_point_mirror_is_conjugate_symmetric_on_a_shunt_reactor_network()
    {
        // FIX 5: a network WITH a shunt reactor (Bs < 0) - the sign-dependent Bs scaling makes the naive |h|
        // mirror non-conjugate-symmetric; passing the SIGNED h keeps Z(-f) = conj(Z(f)) exact.
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 400, Vm: 1.0),
                new Bus(2, BusType.Pq, BaseKv: 400, Bs: -80.0),   // shunt REACTOR (inductive, Bs < 0)
                new Bus(3, BusType.Pq, BaseKv: 400, Bs: 60.0),    // shunt capacitor (Bs > 0)
            },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(1, 2, 0.01, 0.10, 0.02), new Branch(2, 3, 0.02, 0.15, 0.03) },
            FrequencyHz = 50,
        };
        var opt = new HarmonicModelOptions();
        IComplexLinearSolver solver = new RealExpandedComplexSolver();
        foreach (int probeBus in new[] { 2, 3 })
            foreach (double h in new[] { 0.2, 0.7, 1.3, 2.4, 5.0 })
            {
                Complex zPos = GridImpedanceAssembler.DrivingPointAtRatio(model, probeBus, h, opt, solver);
                Complex zNeg = GridImpedanceAssembler.DrivingPointAtRatio(model, probeBus, -h, opt, solver);
                Assert.Equal(zPos.Real, zNeg.Real, 9);
                Assert.Equal(zPos.Imaginary, -zNeg.Imaginary, 9);   // Z(-f) = conj(Z(f))
            }
    }

    [Fact]
    public void Driving_point_mirror_stays_conjugate_symmetric_with_skin_effect_on()
    {
        // 2026-10-19: the skin factor took max(h - 1, 0) on the SIGNED order, so every negative order got factor 1 and
        // the mirror broke as soon as a skin coefficient was set (h = 2.4 and 5.0 below). It now uses |h|; the positive
        // side is bit-identical to before.
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 400, Vm: 1.0),
                new Bus(2, BusType.Pq, BaseKv: 400, Bs: -80.0),
                new Bus(3, BusType.Pq, BaseKv: 400, Bs: 60.0),
            },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(1, 2, 0.01, 0.10, 0.02), new Branch(2, 3, 0.02, 0.15, 0.03) },
            FrequencyHz = 50,
        };
        var opt = new HarmonicModelOptions { SkinCoefficient = 0.1 };
        Assert.Equal(opt.SkinFactor(2.4), opt.SkinFactor(-2.4));
        Assert.True(opt.SkinFactor(5.0) > 1.0);
        IComplexLinearSolver solver = new RealExpandedComplexSolver();
        foreach (int probeBus in new[] { 2, 3 })
            foreach (double h in new[] { 0.7, 1.3, 2.4, 5.0 })
            {
                Complex zPos = GridImpedanceAssembler.DrivingPointAtRatio(model, probeBus, h, opt, solver);
                Complex zNeg = GridImpedanceAssembler.DrivingPointAtRatio(model, probeBus, -h, opt, solver);
                Assert.Equal(zPos.Real, zNeg.Real, 9);
                Assert.Equal(zPos.Imaginary, -zNeg.Imaginary, 9);   // Z(-f) = conj(Z(f))
            }
    }

    [Fact]
    public void Dc_arm_driving_point_is_an_explicit_error_not_a_silent_zero()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, BaseKv: 400, Bs: -50) },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, 0.02) },
            FrequencyHz = 50,
        };
        IComplexLinearSolver solver = new RealExpandedComplexSolver();
        Assert.Throws<InvalidOperationException>(() =>
            GridImpedanceAssembler.DrivingPointAtRatio(model, 2, 0.0, new HarmonicModelOptions(), solver));
    }
}
