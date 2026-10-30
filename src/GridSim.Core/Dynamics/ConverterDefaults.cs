namespace GridSim.Core.Dynamics;

/// <summary>
/// The default control parameters GridSim's three converter representations fall back to when an asset class
/// (or a caller) does not author them (October 2026 review, WS13: R138, R140, R164, R172).
///
/// <para><b>One device, three models.</b> A grid-following converter appears as an average-value EMT model
/// (<see cref="Emt.ThreePhaseConverter"/>), as a dq admittance in the impedance scan
/// (<see cref="Impedance.GridFollowingImpedance"/>) and as a linear block in modal analysis
/// (<see cref="SmallSignal.LinearInverterModel"/>). Before this table each model carried its own bare literals.
/// They are named here, side by side, WITHOUT changing any value, so the differences are visible in one place.</para>
///
/// <para><b>One PLL default (R138, October 2026 review; stream B5; Ruling R1: Mark delegated EE calls 2026-10-04).</b>
/// The three models used to fall back to three differently tuned PLLs (EMT 20 Hz, impedance 5 Hz, small-signal 5 Hz;
/// damping 0.707, 0.707 and 1.0), so an EMT run, an impedance scan and a modal result of one unauthored device were not
/// comparable. All three now take <see cref="PllBandwidthHz"/> (20 Hz) and <see cref="PllDampingRatio"/> (0.707), and
/// the small-signal block reads the class's authored PLL damping as the impedance model does. Why 20 Hz and not 5 Hz:
/// (1) it is the value the EMT model, the only one that integrates the PLL itself, was built and validated with; (2) an
/// SRF-PLL of a transmission-connected converter is commonly tuned to a few tens of Hz (zeta = 1/sqrt(2), Kaura and
/// Blasko, "Operation of a phase locked loop system under distorted utility conditions", IEEE Trans. Ind. Appl. 33(1),
/// 1997; Teodorescu, Liserre and Rodriguez, Grid Converters for Photovoltaic and Wind Power Systems, Wiley, 2011,
/// ch. 4 [VERIFY the bandwidth range quoted there]), while a few Hz is a weak-grid retuning; (3) a faster PLL widens the
/// negative-resistance band that destabilises a weak grid (Wen, Boroyevich, Burgos, Mattavelli and Shen, "Analysis of
/// D-Q small-signal impedance of grid-tied inverters", IEEE Trans. Power Electron. 31(1), 2016; Harnefors, Wang, Yepes
/// and Blaabjerg, "Passivity-based stability assessment of grid-connected VSCs - an overview", IEEE JESTPE 4(1), 2016),
/// so for an UNAUTHORED device the screen must not assume the forgiving slow loop: the placeholder errs on the
/// conservative side, and a class that authors its own bandwidth is used as authored. One exception is kept on
/// purpose: the small-signal block's stand-in for the time-domain controller's detection DELAY (bandwidth
/// 1/(2 pi latency), used when a class gives a latency and no PLL bandwidth, as every bundled class does) is
/// critically damped, <see cref="SmallSignalDelayEstimatorDampingRatio"/>, because it represents a pure delay, which has
/// no overshoot, not a PLL.</para>
///
/// <para>Every value here is a CuratedPlaceholder (literature-typical, not a measured setting).</para>
///
/// <para>R140 (stream B5): the grid-forming impedance's two unit-inconsistent scalings are replaced by a per-unit
/// derivation on the converter's rating (see <see cref="Impedance.GridFormingImpedance"/>).</para>
/// </summary>
public static class ConverterDefaults
{
    // ---------------------------------------------------------------- phase-locked loop

    /// <summary>The ONE default SRF-PLL natural frequency (Hz) of a grid-following converter (R138): the EMT
    /// average-value converter's default (<see cref="Emt.ThreePhaseConverter"/>), the impedance model's placeholder when
    /// the class carries no <c>PllBandwidthHz</c> (the CLI's flag-built fallback repeats it), and the small-signal
    /// block's fallback when the class has neither a <c>PllBandwidthHz</c> nor a usable detection latency.
    /// CuratedPlaceholder [VERIFY]; see the class remarks for the choice. Replaces EmtPllBandwidthHz (20 Hz),
    /// ImpedancePllBandwidthHz (5 Hz) and SmallSignalPllBandwidthHz (5 Hz).</summary>
    public const double PllBandwidthHz = 20.0;

    /// <summary>The ONE default PLL damping ratio (R138): the EMT converter's PI loop, the impedance model's
    /// placeholder and the small-signal block's PLL (1/sqrt(2) rounded to 0.707: the classic "optimally flat"
    /// second-order loop, Kaura and Blasko 1997).</summary>
    public const double PllDampingRatio = 0.707;

    /// <summary>Damping ratio of the small-signal grid-following block's stand-in for the time-domain controller's
    /// detection DELAY (critically damped: a pure delay has no overshoot). Used only when the bandwidth comes from the
    /// class's detection latency (1/(2 pi latency)) and the class authors no PLL damping; a PLL bandwidth (authored or the
    /// <see cref="PllBandwidthHz"/> placeholder) takes the PLL damping instead (R138; it was SmallSignalPllDampingRatio,
    /// applied to every small-signal PLL).</summary>
    public const double SmallSignalDelayEstimatorDampingRatio = 1.0;

    /// <summary>Lower clamp on the impedance model's PLL bandwidth (Hz) and damping, so a zero or negative input
    /// cannot produce a zero natural frequency (and a division by zero in the PI gains).</summary>
    public const double ImpedancePllParameterFloor = 1e-6;

    /// <summary>Lower clamp on the small-signal PLL bandwidth (Hz).</summary>
    public const double SmallSignalPllBandwidthFloorHz = 1e-3;

    /// <summary>A detection latency (s) at or below this is treated as "not authored" by the small-signal GFL
    /// block, which then falls back to <see cref="PllBandwidthHz"/> instead of <c>1/(2*pi*latency)</c>.</summary>
    public const double SmallSignalLatencyFloorSeconds = 1e-6;

    // ---------------------------------------------------------------- small-signal actuator / droop

    /// <summary>Default first-order actuator time constant (s) of the small-signal grid-forming block.</summary>
    public const double SmallSignalGfmActuateTauSeconds = 0.05;

    /// <summary>Default first-order actuator time constant (s) of the small-signal grid-following block.</summary>
    public const double SmallSignalGflActuateTauSeconds = 0.25;

    /// <summary>Default droop gain of the small-signal grid-following block, MW/Hz per MW of rating.</summary>
    public const double SmallSignalGflDroopMwPerHzPerMw = 2.0;

    // ---------------------------------------------------------------- grid-forming impedance placeholders

    /// <summary>Literature-typical GFM virtual reactance (pu) used when the class carries none (CuratedPlaceholder,
    /// [VERIFY]). The CLI's impedance fallback repeats it.</summary>
    public const double GfmVirtualReactancePu = 0.20;

    /// <summary>Literature-typical GFM power-loop bandwidth (Hz) used when the class carries none
    /// (CuratedPlaceholder, [VERIFY]).</summary>
    public const double GfmPowerLoopBandwidthHz = 5.0;

    /// <summary>GFM virtual resistance as a fraction of the virtual reactance (R/X = 0.05, i.e. X/R = 20).
    /// Dimensionless, so unit-consistent.</summary>
    public const double GfmVirtualROverX = 0.05;

    /// <summary>GFM power-loop synchronising resistance (pu) when the class carries no damping: read since R140 as the
    /// placeholder per-unit droop m_p (pu frequency per pu power on the converter's rating; see
    /// <see cref="Impedance.GridFormingImpedance"/>). Value unchanged.</summary>
    public const double GfmDefaultSyncResistancePu = 0.30;

    // R140 (October 2026 review, stream B5; Ruling R1: Mark delegated EE calls 2026-10-04): three constants are gone.
    // GfmDampingToSyncResistanceScale (0.01, MW/Hz read as pu) and GfmSynInertiaToTauScale (1e-4, MW.s/Hz read as
    // seconds) were the unit-inconsistent scalings; the power loop is now derived on the converter's rating (droop
    // m_p = S / (D f0), virtual inertia constant H_v = J f0 / (2 S); GridFormingImpedance). GfmMinSyncResistancePu (0.05)
    // was a floor on the old 0.01 D scaling that would override every realistic per-unit droop (1 to 10 %), so it is not
    // carried over: the virtual resistance R_v > 0 keeps the impedance non-singular on its own.

    /// <summary>Lower clamp on the GFM power-loop time constant (s).</summary>
    public const double GfmMinPowerLoopTauSeconds = 1e-3;

    /// <summary>Lower clamp on the GFM power-loop bandwidth (Hz) before it is turned into a time constant.</summary>
    public const double GfmMinPowerLoopBandwidthHz = 1e-3;
}
