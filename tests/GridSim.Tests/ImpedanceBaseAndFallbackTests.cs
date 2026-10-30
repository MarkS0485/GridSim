using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R008: the return ratio must put the grid impedance (system base) and the converter admittance (converter
/// base) on one base, so the verdict and the critical SCR do not depend on the converter rating.
/// R245: the real-network grid impedance is never silently replaced by the canonical Thevenin.
/// R244: an inner-loop override replaces only its own parameter and never inherits a Recorded tag.
/// </summary>
public class ImpedanceBaseAndFallbackTests
{
    private static GridFollowingImpedance Gfl(int bus = 2) =>
        GridFollowingImpedance.FromParameters(bus, new ConverterOperatingPoint(1.0, 0.0, 1.0, 0.2, 1.0, -0.2),
            ImpedanceModelDetail.PllOuter, ProvenanceTag.CuratedPlaceholder, 5.0, 0.707, 0.0);

    private static ImpedanceStabilityOptions Canonical(double pRated, StabilityCriterion crit = StabilityCriterion.Siso) => new()
    {
        FMinHz = 0.1, FMaxHz = 250.0, PointsPerDecade = 50, F0Hz = 50.0, Criterion = crit,
        UseCanonicalThevenin = true, Scr = 3.0, XOverR = 10.0, PRatedPu = pRated,
        ScrMin = 0.05, ScrMax = 10.0, ScrSteps = 80,
    };

    [Theory]
    [InlineData(StabilityCriterion.Siso)]
    [InlineData(StabilityCriterion.Mimo)]
    public void Verdict_and_critical_scr_do_not_depend_on_the_converter_rating(StabilityCriterion crit)
    {
        // The R008 failure: a 1200 MVA unit on a 100 MVA base (PRatedPu = 12) used to see a loop gain 12x too
        // small. With both terms on the system base, L = Y_conv / SCR for every rating.
        ImpedanceStabilityResult r1 = ImpedanceStabilityScan.Run(Canonical(1.0, crit), Gfl());
        // The MIMO reference crosses the boundary inside the sweep (so CriticalScr is a real number there).
        if (crit == StabilityCriterion.Mimo) Assert.True(r1.BoundaryFound, "the MIMO reference scan must cross the stability boundary");
        foreach (double p in new[] { 12.0, 0.25, 3.7 })
        {
            ImpedanceStabilityResult rp = ImpedanceStabilityScan.Run(Canonical(p, crit), Gfl());
            Assert.Equal(r1.Nyquist.Stable, rp.Nyquist.Stable);
            Assert.Equal(r1.Nyquist.Encirclements, rp.Nyquist.Encirclements);
            Assert.Equal(r1.BoundaryFound, rp.BoundaryFound);
            Assert.Equal(r1.CriticalScr, rp.CriticalScr);   // a midpoint of grid SCRs: exact
            for (int j = 0; j < r1.ScrSweep.Count; j++)
{ Assert.Equal(r1.ScrSweep[j].Stable, rp.ScrSweep[j].Stable); Assert.Equal(r1.ScrSweep[j].Encirclements, rp.ScrSweep[j].Encirclements); }
            // The return ratio itself agrees to rounding (Z scales by 1/P, Y by P).
            for (int i = 0; i < r1.Frequency.Count; i++)
            {
                Complex a = r1.Frequency[i].L, b = rp.Frequency[i].L;
                Assert.True((a - b).Magnitude <= 1e-12 * Math.Max(1.0, a.Magnitude),
                    $"P={p}, f={r1.Frequency[i].FHz}: L {a} vs {b}");
            }
        }
    }

    [Fact]
    public void Samples_are_on_the_system_base_and_unchanged_at_unit_rating()
    {
        GridFollowingImpedance conv = Gfl();
        // PRatedPu = 1: the stored admittance is the converter's own, bit for bit (no arithmetic moved).
        ImpedanceStabilityResult r1 = ImpedanceStabilityScan.Run(Canonical(1.0), conv);
        foreach (FreqSample s in r1.Frequency)
        {
            Complex y = conv.PositiveSequenceAdmittance(2.0 * Math.PI * s.FHz);
            Assert.Equal(y, s.YConv);
            Assert.Equal(s.ZGrid * s.YConv, s.L);
        }
        // PRatedPu = 12: YConv is the converter admittance times the rating ratio, and L = ZGrid * YConv.
        ImpedanceStabilityResult r12 = ImpedanceStabilityScan.Run(Canonical(12.0), conv);
        foreach (FreqSample s in r12.Frequency)
        {
            Complex y = conv.PositiveSequenceAdmittance(2.0 * Math.PI * s.FHz);
            Assert.Equal(new Complex(y.Real * 12.0, y.Imaginary * 12.0), s.YConv);
            Assert.Equal(s.ZGrid * s.YConv, s.L);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_non_positive_or_non_finite_rating_fails_loud(double pRated)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImpedanceStabilityScan.Run(Canonical(pRated) with { ScrSteps = 0 }, Gfl()));
    }

    // ------------------------------------------------------------------ R245 real-network path

    private static ImpedanceStabilityOptions Real(StabilityCriterion crit = StabilityCriterion.Siso) => new()
    {
        FMinHz = 0.1, FMaxHz = 250.0, PointsPerDecade = 20, F0Hz = 50.0, Criterion = crit,
        UseCanonicalThevenin = false, Scr = 3.0, XOverR = 10.0, PRatedPu = 1.0, ScrSteps = 0,
    };

    [Fact]
    public void Real_network_path_uses_the_real_driving_point_at_every_reporting_frequency()
    {
        // A 2-bus network whose driving point differs from the canonical Thevenin (it carries a PCC shunt):
        // every reported ZGrid must be the real network's value, never the Thevenin substitute.
        GridModel net = GridImpedanceAssembler.CanonicalThevenin(3.0, 10.0, 1.0, 100.0);
        ImpedanceStabilityOptions opt = Real();
        ImpedanceStabilityResult r = ImpedanceStabilityScan.Run(opt, Gfl(), net);
        Assert.False(r.Params!.Canonical);
        var solver = new GridSim.Core.Numerics.RealExpandedComplexSolver();
        foreach (FreqSample s in r.Frequency)
        {
            // The SISO grid impedance is the positive-sequence entry Z_pp(f) = Z(f + f0) in the converter's dq frame
            // (bug hunt 2026-10-29, finding 14: the modified-sequence reduction of the grid's dq impedance, the same
            // reduction the converter admittance Y_pp is formed by). This line used to read Z(f), the old frame-mixed
            // pairing; what it pins (the real network's value, never the Thevenin substitute) is unchanged.
            Complex real = GridImpedanceAssembler.PositiveSequence(net, 2, s.FHz + opt.F0Hz, opt.F0Hz, opt.Harmonic, solver);
            Assert.Equal(real, s.ZGrid);
        }
    }

    [Fact]
    public void Real_network_path_without_a_model_fails_loud()
    {
        Assert.Throws<ArgumentNullException>(() => ImpedanceStabilityScan.Run(Real(), Gfl(), realModel: null));
    }

    [Fact]
    public void Real_network_band_reaching_the_dc_arm_fails_loud()
    {
        GridModel net = GridImpedanceAssembler.CanonicalThevenin(3.0, 10.0, 1.0, 100.0);
        // 1e-3 Hz at f0 = 50 Hz is h = 2e-5, inside the 1e-4 DC-arm guard: it used to be silently canonical.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImpedanceStabilityScan.Run(Real() with { FMinHz = 1e-3 }, Gfl(), net));
        Assert.Contains("DC arm", ex.Message);
    }

    [Fact]
    public void Real_network_reporting_point_on_f0_fails_loud_for_the_dq_arm()
    {
        GridModel net = GridImpedanceAssembler.CanonicalThevenin(3.0, 10.0, 1.0, 100.0);
        // FMaxHz = f0 puts the last reporting point exactly on f0, where the dq negative arm is at DC.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ImpedanceStabilityScan.Run(Real() with { FMaxHz = 50.0 }, Gfl(), net));
        Assert.Contains("f0", ex.Message);
    }

    [Fact]
    public void Real_network_failure_propagates_with_its_frequency_instead_of_a_thevenin_substitute()
    {
        // A converter bus that is not in the network: every driving-point solve fails. The old code caught it
        // and silently returned the canonical Thevenin at every frequency, reporting a plausible verdict.
        GridModel net = GridImpedanceAssembler.CanonicalThevenin(3.0, 10.0, 1.0, 100.0);
        var ex = Assert.Throws<InvalidOperationException>(() => ImpedanceStabilityScan.Run(Real(), Gfl(bus: 999), net));
        Assert.Contains("Hz", ex.Message);
        Assert.Contains("does not substitute", ex.Message);
    }

    // ------------------------------------------------------------------ R244 overrides and provenance

    private static AssetClass RecordedGflClass(double bwHz, double zeta) => new(
        "gfl-recorded", AssetKind.GridFollowingInverter, "grid-following",
        new Dictionary<string, double> { [AssetParams.PllBandwidthHz] = bwHz, [AssetParams.PllDampingRatio] = zeta },
        new Dictionary<string, AssetClassProvenance>
        {
            [AssetClass.DefaultKey] = new(ProvenanceTag.Recorded, "test: recorded inner-loop parameters"),
        });

    private static ConverterOperatingPoint Op => new(1.0, 0.0, 1.0, 0.2, 1.0, -0.2);

    [Fact]
    public void Bandwidth_override_keeps_the_authored_damping()
    {
        AssetClass cls = RecordedGflClass(bwHz: 8.0, zeta: 0.35);
        GridFollowingImpedance withOverride = GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllOuter,
            pllBandwidthHzOverride: 12.0);
        GridFollowingImpedance reference = GridFollowingImpedance.FromParameters(2, Op, ImpedanceModelDetail.PllOuter,
            ProvenanceTag.CuratedPlaceholder, 12.0, 0.35, 0.0);
        // Same gains as the authored damping 0.35 at the overridden 12 Hz (the old code forced 0.707).
        Assert.Equal(reference.PllGains, withOverride.PllGains);
    }

    [Fact]
    public void Damping_override_is_honoured()
    {
        AssetClass cls = RecordedGflClass(bwHz: 8.0, zeta: 0.35);
        GridFollowingImpedance m = GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllOuter,
            pllDampingOverride: 0.9);
        GridFollowingImpedance reference = GridFollowingImpedance.FromParameters(2, Op, ImpedanceModelDetail.PllOuter,
            ProvenanceTag.CuratedPlaceholder, 8.0, 0.9, 0.0);
        Assert.Equal(reference.PllGains, m.PllGains);
    }

    [Fact]
    public void Any_override_drops_the_recorded_provenance()
    {
        AssetClass cls = RecordedGflClass(bwHz: 8.0, zeta: 0.35);
        Assert.Equal(ProvenanceTag.Recorded,
            GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllOuter).Provenance);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder,
            GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllOuter, pllBandwidthHzOverride: 12.0).Provenance);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder,
            GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllOuter, pllDampingOverride: 0.5).Provenance);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder,
            GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllCurrentOuter, currentLoopBandwidthHzOverride: 300.0).Provenance);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    public void Invalid_overrides_fail_loud(double bad)
    {
        AssetClass cls = RecordedGflClass(bwHz: 8.0, zeta: 0.35);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllOuter, pllBandwidthHzOverride: bad));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllOuter, pllDampingOverride: bad));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GridFollowingImpedance.FromAssetClass(cls, 2, Op, ImpedanceModelDetail.PllCurrentOuter, currentLoopBandwidthHzOverride: bad));
    }
}
