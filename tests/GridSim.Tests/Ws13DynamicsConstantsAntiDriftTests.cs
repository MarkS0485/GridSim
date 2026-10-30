using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.SmallSignal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS13 (R138, R140, R160, R161, R164-R170, R172): bare literals in the EMT, impedance,
/// small-signal and RMS machine models were named. The naming must not move a single number, so each test pins a
/// new constant to EXACTLY the literal it replaced (exact <c>Assert.Equal</c> on doubles), and a few source-level
/// checks make sure the literal did not survive next to its new name.
/// </summary>
public sealed class Ws13DynamicsConstantsAntiDriftTests
{
    private static string Src(params string[] rel) =>
        PresentationConstantsAntiDriftTests.Source(new[] { "src", "GridSim.Core" }.Concat(rel).ToArray());

    [Fact]
    public void Converter_defaults_equal_the_literals_they_replaced()
    {
        // R138: the three converter models kept their DIFFERENT defaults (recorded for Mark), named.
        // Moved 2026-10 (B5 R138, Ruling R1: Mark delegated EE calls 2026-10-04; source: R138 in FULL-REVIEW-2026-10.md, "define
        // shared constants in one place ... and use them in all three models"; the 20 Hz choice and its literature are on
        // ConverterDefaults): EmtPllBandwidthHz (20.0), ImpedancePllBandwidthHz (5.0) and SmallSignalPllBandwidthHz (5.0) are
        // one constant, PllBandwidthHz = 20.0 (EMT unchanged; impedance and small-signal placeholders 5.0 -> 20.0);
        // SmallSignalPllDampingRatio (1.0) is renamed SmallSignalDelayEstimatorDampingRatio (1.0, now used only for the
        // small-signal delay stand-in; a PLL takes PllDampingRatio 0.707 in every model). The three old names are gone.
        Assert.Equal(20.0, ConverterDefaults.PllBandwidthHz);
        Assert.Equal(0.707, ConverterDefaults.PllDampingRatio);
        Assert.Equal(1.0, ConverterDefaults.SmallSignalDelayEstimatorDampingRatio);
        foreach (string gone in new[] { "EmtPllBandwidthHz", "ImpedancePllBandwidthHz", "SmallSignalPllBandwidthHz", "SmallSignalPllDampingRatio" })
            Assert.Null(typeof(ConverterDefaults).GetField(gone));
        Assert.Equal(1e-6, ConverterDefaults.ImpedancePllParameterFloor);
        Assert.Equal(1e-3, ConverterDefaults.SmallSignalPllBandwidthFloorHz);
        Assert.Equal(1e-6, ConverterDefaults.SmallSignalLatencyFloorSeconds);
        // R172: small-signal inverter defaults.
        Assert.Equal(0.05, ConverterDefaults.SmallSignalGfmActuateTauSeconds);
        Assert.Equal(0.25, ConverterDefaults.SmallSignalGflActuateTauSeconds);
        Assert.Equal(2.0, ConverterDefaults.SmallSignalGflDroopMwPerHzPerMw);
        // R140: grid-forming impedance placeholders (two of them unit-inconsistent, left for Mark).
        Assert.Equal(0.20, ConverterDefaults.GfmVirtualReactancePu);
        Assert.Equal(5.0, ConverterDefaults.GfmPowerLoopBandwidthHz);
        Assert.Equal(0.05, ConverterDefaults.GfmVirtualROverX);
        // Moved 2026-10 (B5 R140, Ruling R1: Mark delegated EE calls 2026-10-04; source: R140 in FULL-REVIEW-2026-10.md,
        // "derive dp and taup from per-unit quantities (droop in pu on rating, H_virtual from synInertia, f0 and
        // S_rated)", and the VSM / droop equivalence of D'Arco and Suul, IEEE Trans. Smart Grid 5(1), 2014): the two
        // unit-inconsistent scalings GfmDampingToSyncResistanceScale (0.01) and GfmSynInertiaToTauScale (1e-4) and the floor
        // that only made sense with the first, GfmMinSyncResistancePu (0.05), are deleted; GridFormingImpedance now derives
        // dp = S / (D f0) and tau += 2 H_v dp with H_v = J f0 / (2 S) on the converter's rating. Before: three pins
        // (0.05, 0.01, 1e-4). After: the constants do not exist (checked by reflection) and the placeholder droop is
        // unchanged.
        Assert.Equal(0.30, ConverterDefaults.GfmDefaultSyncResistancePu);
        foreach (string gone in new[] { "GfmMinSyncResistancePu", "GfmDampingToSyncResistanceScale", "GfmSynInertiaToTauScale" })
            Assert.Null(typeof(ConverterDefaults).GetField(gone));
        Assert.Equal(1e-3, ConverterDefaults.GfmMinPowerLoopTauSeconds);
        Assert.Equal(1e-3, ConverterDefaults.GfmMinPowerLoopBandwidthHz);
    }

    [Fact]
    public void Converter_record_defaults_still_resolve_to_the_old_values()
    {
        // The record / ctor defaults now reference the named constants; their effective values must not move.
        var unit = new RmsConverterUnit(0, "u", 1.0, 0.5, 0.0, System.Numerics.Complex.One, new RmsConverterParameters());
        var conv = new ThreePhaseConverter(0, unit);
        Assert.Equal(20.0, conv.PllBandwidthHz);
        // Moved 2026-10 (D2 R138, Ruling R1: Mark delegated EE calls 2026-10-04; source: R138 in FULL-REVIEW-2026-10.md, "make
        // ThreePhaseSource inherit the engine frequency unless set explicitly"): the source default was an independent 50.0,
        // so an engine built at 60 Hz still drove 50 Hz sources. It is now null = "the engine's frequency"; the effective
        // value in an engine at the default 50 Hz is still 50.0 (D2NominalFrequencyTests.R138_*). Before: 50.0. After: null.
        Assert.Null(new ThreePhaseSource(0).FreqHz);
        Assert.Equal(1.2, new RmsConverterParameters().Imax);
        Assert.Equal(50.0, new RmsConverterParameters().NominalHz);
        Assert.Equal(1.2, RmsConverterParameters.GridFormingWithLimit(0.0).Imax);
        Assert.Equal(50.0, new ImpedanceStabilityOptions().F0Hz);
        Assert.Equal(1e-6, new SwitchElement(1, 1, 2).Ron);
        Assert.Equal(1e9, new SwitchElement(1, 1, 2).Roff);
    }

    [Fact]
    public void Emt_constants_equal_the_literals_they_replaced()
    {
        Assert.Equal(1e-6, EmtConstants.EventTimeToleranceFraction);
        Assert.Equal(1e-6, EmtConstants.DefaultSwitchOnResistancePu);
        Assert.Equal(1e9, EmtConstants.DefaultSwitchOffResistancePu);
        Assert.Equal(1e-9, EmtConstants.OpenFaultConductance);
        Assert.Equal(1e-3, EmtConstants.MinVoltageForControlPu);
        Assert.Equal(2.0, EmtConstants.LimitArmingRampMultiple);
        Assert.Equal(0.05, EmtConstants.LimitArmingMarginSeconds);
        Assert.Equal(0.95, EmtConstants.GfmLimitReleaseFraction);
        Assert.Equal(1e-9, EmtConstants.ZeroCurrentMagnitudePu);
    }

    [Fact]
    public void Impedance_scan_constants_equal_the_literals_they_replaced()
    {
        Assert.Equal(1e-3, ImpedanceStabilityScan.WindingGridMinHz);
        Assert.Equal(1e5, ImpedanceStabilityScan.WindingGridMaxHz);
        Assert.Equal(60, ImpedanceStabilityScan.WindingGridMinPointsPerDecade);
        Assert.Equal(2.0, ImpedanceStabilityScan.RealNetworkWindingGuardMultiple);
        Assert.Equal(400.0, GridImpedanceAssembler.CanonicalBaseKv);
        Assert.Equal(1e-3, GridImpedanceAssembler.CanonicalPccShuntPu);
        // The canonical model built with the defaults carries the same numbers as before.
        var m = GridImpedanceAssembler.CanonicalThevenin(3.0, 10.0, 1.0, 100.0);
        Assert.Equal(50.0, m.FrequencyHz);
        Assert.All(m.Buses, b => Assert.Equal(400.0, b.BaseKv));
        Assert.Equal(1e-3 * 100.0, m.Buses[1].Bs);
    }

    [Fact]
    public void Modal_and_rms_model_constants_equal_the_literals_they_replaced()
    {
        Assert.Equal(1e-9, ModalAnalysis.DefaultRhpTolerance);
        Assert.Equal(1e-12, ModalAnalysis.ZeroModeMagnitude);
        Assert.Equal(1e-9, ModalAnalysis.OscillatoryImagTolerance);

        Assert.Equal(1e-12, Ieeet1.ExciterPoleAtOriginTolerance);
        Assert.Equal(50, MultiMachineSubtransient.MaxConverterIterations);
        Assert.Equal(1e-10, MultiMachineSubtransient.ConverterVoltageTolerancePu);

        Assert.Equal(1e-6, RmsConverterState.MinVirtualReactancePu);
        Assert.Equal(1e-6, RmsConverterState.MinVoltageMagnitudePu);
        Assert.Equal(1e-6, RmsConverterState.MinMeasurementLagSeconds);
        Assert.Equal(1e-3, RmsConverterState.PllLockMinVoltagePu);
        Assert.Equal(0.5, RmsConverterState.GfmEmfMinPu);
        Assert.Equal(1.5, RmsConverterState.GfmEmfMaxPu);
        Assert.Equal(0.01, RmsConverterState.MinControlVoltagePu);

        Assert.Equal(0.005, RmsTransientStability.StudyDefaultStepSeconds);
        Assert.Equal(0.002, RmsTransientStability.CctDefaultStepSeconds);
        Assert.Equal(10.0, RmsTransientStability.StudyDefaultDurationSeconds);
        Assert.Equal(5.0, RmsTransientStability.PostClearWindowSeconds);
        Assert.Equal(1.0, RmsTransientStability.CctDefaultMaxSeconds);
        Assert.Equal(18, RmsTransientStability.CctDefaultBisectionIterations);
        Assert.Equal(0.1, RmsTransientStability.CctDefaultOnsetSeconds);
        Assert.Equal(2.0 * Math.PI, RmsTransientStability.DefaultSlipLimitRad);

        Assert.Equal(8.0, SubtransientInit.IslandReferenceTdop);
        Assert.Equal(0.03, SubtransientInit.IslandReferenceTdopp);
        Assert.Equal(0.4, SubtransientInit.IslandReferenceTqop);
        Assert.Equal(0.05, SubtransientInit.IslandReferenceTqopp);
        Assert.Equal(1e7, SubtransientInit.BoltedFaultShuntPu);
        Assert.Equal(1e-9, SubtransientInit.MinVoltageSquaredPu);
        Assert.Equal(1e-9, SubtransientInit.MinMBaseWeightMva);
        Assert.Equal(1e-9, SubtransientInit.MinScheduledPowerMw);

        Assert.Equal(1.0, SubtransientMachine.SaturationCurve.LowerFitPointPu);
        Assert.Equal(1.2, SubtransientMachine.SaturationCurve.UpperFitPointPu);
    }

    [Fact]
    public void Saturation_fit_still_passes_through_both_tabulated_points()
    {
        // Behavioural pin of R170: the named fit points reproduce Se(1.0) and Se(1.2) of the authored curve.
        var c = new SubtransientMachine.SaturationCurve(0.1, 0.4);
        Assert.Equal(0.1, c.Se(1.0), 12);
        Assert.Equal(0.4, c.Se(1.2), 12);
    }

    [Fact]
    public void Named_literals_are_gone_from_the_edited_sources()
    {
        string tp = Src("Dynamics", "Emt", "ThreePhaseEmt.cs");
        Assert.DoesNotContain("1e-9", tp);
        Assert.DoesNotContain("0.707", tp);
        Assert.DoesNotContain("_dt * 1e-6", tp);
        Assert.DoesNotContain("= 50.0", tp);
        string gfl = Src("Dynamics", "Impedance", "GridFollowingImpedance.cs");
        Assert.DoesNotContain(", 0.707)", gfl);     // (the doc comment still records the old forced 0.707)
        Assert.DoesNotContain("zeta = 0.707", gfl);
        Assert.DoesNotContain("bwHz = 5.0", gfl);
        string gfm = Src("Dynamics", "Impedance", "GridFormingImpedance.cs");
        Assert.DoesNotContain("0.01 * dampingMwPerHz", gfm);
        Assert.DoesNotContain("1e-4 * synInertia", gfm);
        Assert.DoesNotContain("<= ControlBlocks.InstantThresholdSeconds", Src("Dynamics", "Models", "Ieeet1.cs"));
        Assert.DoesNotContain("new Complex(1e7", Src("Dynamics", "Models", "SubtransientInit.cs"));
        string rts = Src("Dynamics", "Models", "RmsTransientStability.cs");
        Assert.DoesNotContain("+ 5.0", rts);
        Assert.DoesNotContain("f0 = 50.0", rts);
    }
}
