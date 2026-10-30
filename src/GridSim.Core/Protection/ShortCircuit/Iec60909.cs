using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Protection.ShortCircuit;

/// <summary>
/// Whether the voltage factor <c>c</c> is chosen for the <b>maximum</b> short-circuit current (equipment
/// duty, breaker rating) or the <b>minimum</b> (protection-sensitivity checks). IEC 60909-0 Table 1.
/// </summary>
public enum ShortCircuitObjective
{
    /// <summary>Maximum short-circuit current: c = 1.10 (HV) / 1.05 (LV) - the equipment-rating case.</summary>
    Maximum,
    /// <summary>Minimum short-circuit current: c = 1.00 (HV) / 0.95 (LV) - the protection-sensitivity case.</summary>
    Minimum,
}

/// <summary>
/// N134 (CF3, October 2026): how the peak factor kappa of a three-phase duty is found (IEC 60909-0:2001 subclause
/// 4.3.1.2; IEC 60909-0:2016 subclause 8.1.2 "Multiple-fed short circuit", checked against the 2016 contents list,
/// [VERIFY] 2026-10-11. IEC 60909-0:2026 Edition 3.0 now exists and its numbering is unchecked).
/// </summary>
public enum PeakFactorMethod
{
    /// <summary>Method B, uniform ratio R/X at the short-circuit location: kappa(R/X of Zk), times the safety factor 1.15
    /// (capped at 2.0, 1.8 at low voltage) when the fault network is meshed (D-DOC-098). The default; GridSim's only
    /// method before N134, and every result without a choice stays bit-for-bit as it was.</summary>
    MethodB,

    /// <summary>Method C, the equivalent frequency method: R/X from the driving-point impedance at fc = 0.4 f
    /// (<see cref="Iec60909.MethodCRoverX(GridModel, int, double)"/>), kappa(R/X) with no safety factor. The method NESO
    /// uses for peak-make (ETYS Appendix D, docs/reference S242 p.4).
    /// <para>N134 rest (CF4, October 2026): Method C now also gives the DC component idc (R/X from the network at the
    /// equivalent frequency fc / f set by f * tb, <see cref="Iec60909.MethodCDcFrequencyRatio"/>), and applies to the
    /// unbalanced duties (<see cref="Iec60909.LineToLine"/>, <see cref="Iec60909.SingleLineToGround"/>,
    /// <see cref="Iec60909.DoubleLineToGround"/>) on each fault's own sequence connection. Ik'', Ib, Ith and m are
    /// Method B's in every case.</para></summary>
    MethodC,
}

/// <summary>The unbalanced short-circuit types, each a different connection of the sequence networks.</summary>
public enum UnbalancedFaultType
{
    /// <summary>Single line to earth (Ik1''): the three sequence networks in series, current <c>|3 I1|</c>.</summary>
    SingleLineToGround,
    /// <summary>Line to line, no earth (Ik2''): positive and negative in series, faulted-phase <c>sqrt(3)|I1|</c>.</summary>
    LineToLine,
    /// <summary>Double line to earth (Ik2E''): negative in parallel with zero, in series with positive; the
    /// reported current is the earth current <c>|3 I0|</c>.</summary>
    DoubleLineToGround,
}

/// <summary>
/// D-DOC-097 (October 2026): how the steady-state short-circuit current <see cref="Iec60909.Duty.SteadyIkKa"/> (and with
/// it the thermal factor n inside <see cref="Iec60909.Duty.ThermalIthKa"/>) was obtained, so a reader can tell an exact
/// value from a bound. In every case the number reported is Ik'' (plus any converter share) and n = 1; what differs is
/// whether IEC 60909-0 says that IS the steady-state current.
/// </summary>
public enum SteadyStateBasis
{
    /// <summary>Far from every generator (each machine feeds at most twice its rated current): no AC decay, so
    /// Ik = Ik'' and n = 1 exactly (IEC 60909-0 far-from-generator short circuit).</summary>
    FarFromGenerator,
    /// <summary>Near to a generator, fed from several sources: IEC 60909-0 (2001 edition 4.6.3, "meshed networks";
    /// 2016 edition subclause 11.2.7, "Steady-state short-circuit current in case of multiple-fed short circuits", from
    /// the 2016 contents list, [VERIFY] 2026-10-11) approximates the steady-state current of a
    /// short circuit fed from several sources by the initial symmetrical current WITHOUT motor contributions,
    /// Ik = Ik''M. GridSim models no motors, so Ik''M = Ik'' and Ik = Ik'' is the standard's value, not an
    /// approximation of ours; with Ik''/Ik = 1 the factor n is 1.</summary>
    MeshedMultipleFed,
    /// <summary>Near to a generator that is the ONLY synchronous source of the fault (single-fed): the standard's
    /// Ik = lambda I_rG needs lambda from the machine curves (IEC 60909-0:2001 Figures 18 / 19; IEC 60909-0:2016
    /// subclause 11.2.1 and Figures 15 / 16, where Figures 18 / 19 are now the factors m and n; in the generator's
    /// saturated synchronous reactance x_dsat and excitation ceiling), which the model does not carry. The value
    /// reported is Ik'', an UPPER BOUND on Ik (the AC component only decays), and Ith with n = 1 is likewise an upper
    /// bound (n &lt;= 1 whenever Ik''/Ik &gt;= 1): conservative for an equipment rating, NOT for a minimum-current
    /// (protection sensitivity) study.</summary>
    SingleFedUpperBound,
    /// <summary>An unbalanced fault: IEC 60909-0 takes no flux decay into account for unbalanced short circuits, so the
    /// steady-state and breaking currents equal the initial current (the same rule that keeps Ib = Ik'' there,
    /// IEC 60909-0:2016 subclause 9.2 "Unbalanced short-circuits", from the 2016 contents list, [VERIFY] 2026-10-11; an
    /// earlier citation of 10.2 was wrong, clause 10 being the DC component).</summary>
    UnbalancedNoDecay,
}

/// <summary>
/// IEC 60909-0 short-circuit <b>duty</b> study - the standards calculation a protection or switchgear-rating
/// engineer needs, distinct from the operating-point superposition of <see cref="FaultAnalysis"/>. The two
/// differ deliberately:
/// <list type="bullet">
///   <item>60909 drives the fault from a single <b>equivalent voltage source</b> <c>c*Un/sqrt(3)</c> at the
///   fault location, with all other sources shorted and loads neglected, rather than superposing on the
///   solved prefault voltages;</item>
///   <item>it reports the family of standardised currents over time - the initial symmetrical
///   <c>Ik''</c>, the peak <c>ip</c>, the DC component <c>idc</c>, the symmetrical breaking <c>Ib</c>, the
///   thermal-equivalent <c>Ith</c> and the steady-state <c>Ik</c> - through the standard factors
///   <c>kappa, mu, m, n</c>, not a single fault-current magnitude.</item>
/// </list>
///
/// <para>The network entry points (<see cref="ThreePhase"/>, <see cref="LineToLine"/>) assemble the
/// positive-sequence Thevenin impedance. <see cref="ThreePhase"/> applies the near-to-generator breaking factor mu
/// per machine through the IEC 60909-0:2016 multiple-fed formula (R095), so its <c>Ib</c> equals <c>Ik''</c> only
/// when every machine is far from the fault; its steady-state <c>Ik</c> stays <c>Ik''</c> (lambda deferred). The
/// unbalanced entry points keep <c>Ib = Ik''</c>, as the standard directs for unbalanced faults (no flux decay is
/// taken into account, IEC 60909-0:2016 subclause 9.2 "Unbalanced short-circuits", within clause 9 "Calculation of
/// symmetrical breaking current"; checked against the 2016 contents list, [VERIFY] 2026-10-11, N066 / N236; the earlier
/// citation of 10.2 was wrong, clause 10 is the DC component). The duty formulae
/// themselves (<see cref="PeakFactor"/>, <see cref="ThermalDcFactor"/>, <see cref="FromThevenin"/>,
/// <see cref="FromSequenceImpedances"/>) are pure and validatable against the IEC closed forms to machine
/// epsilon. The <b>near-to-generator</b> AC-decay factors are provided as pure closed forms too - the
/// synchronous breaking factor <see cref="BreakingFactorMu"/> (<c>mu</c>) and the asynchronous-motor factor
/// <see cref="MotorBreakingFactorQ"/> (<c>q</c>) - with <see cref="SynchronousBreakingCurrentKa"/> applying
/// <c>mu</c> for a single-machine fault. The <b>impedance-correction factors</b>
/// <see cref="GeneratorCorrectionKg"/> (<c>K_G</c>) and <see cref="TransformerCorrectionKt"/> (<c>K_T</c>)
/// are closed forms too, correcting a machine or transformer impedance before it enters the fault network.
/// F21 (October 2026): the power-station-unit factors <see cref="PowerStationUnitCorrectionKs"/> (<c>K_S</c>) and
/// <see cref="PowerStationUnitCorrectionKso"/> (<c>K_SO</c>) are added, and <see cref="ApplyCorrectionFactors"/> applies
/// K_G / K_S / K_SO / K_T to a copy of the model on request (the generator rated power factor is the caller's input,
/// since the model carries none); the duty methods themselves are unchanged and still study the model as given.
/// Still deferred, and noted rather than silently assumed: the steady-state factor <c>lambda</c>
/// (<c>Ik = lambda * IrG</c>), which the standard reads from machine-dependent graphs (2001 Figures 18/19, 2016
/// Figures 15/16) rather
/// than a closed form. The multi-machine breaking current is delivered: <see cref="PartialThreePhase"/>
/// decomposes a fault into the per-machine partial currents (each with its own <c>mu_i * Ik''_i</c>) and reports the
/// fleet total by the meshed-network formula, the same value <see cref="ThreePhase"/> reports (N049).
/// The unbalanced faults are wired end-to-end from the model:
/// <see cref="LineToLine"/> through the positive/negative networks, and <see cref="SingleLineToGround"/> /
/// <see cref="DoubleLineToGround"/> through the zero-sequence network (<see cref="ZeroSequenceThevenin"/>)
/// built from the transformer winding connections.</para>
/// </summary>
public static class Iec60909
{
    /// <summary>The default synthesised generator subtransient reactance (pu on the machine base), matching
    /// <see cref="FaultAnalysis.DefaultSubtransientXdPu"/> - the fallback for a machine whose
    /// <see cref="Generator.SubtransientXdPu"/> is not supplied (per-machine real data is used when present).</summary>
    public const double DefaultSubtransientXdPu = FaultAnalysis.DefaultSubtransientXdPu;

    /// <summary>
    /// The IEC 60909-0 Table 1 voltage factor <c>c</c> for a nominal line-to-line voltage
    /// <paramref name="nominalKvLL"/> and the chosen <paramref name="objective"/>. Low voltage (&lt;= 1 kV)
    /// takes 1.05 / 0.95; medium and high voltage take 1.10 / 1.00.
    /// </summary>
    public static double VoltageFactor(double nominalKvLL, ShortCircuitObjective objective)
    {
        bool lowVoltage = nominalKvLL <= 1.0;
        return objective == ShortCircuitObjective.Maximum
            ? (lowVoltage ? 1.05 : 1.10)
            : (lowVoltage ? 0.95 : 1.00);
    }

    /// <summary>
    /// The peak-current factor <c>kappa = 1.02 + 0.98 * exp(-3 R/X)</c> (IEC 60909-0, the series-network
    /// Method B form). Bounded to (1.02, 2.0]: a purely reactive circuit (<c>R = 0</c>) gives exactly 2.0
    /// (full asymmetry), a highly resistive one tends to 1.02.
    /// </summary>
    public static double PeakFactor(double rOverX) => 1.02 + 0.98 * DetMath.Exp(-3.0 * rOverX);

    /// <summary>The safety factor IEC 60909-0 Method B (uniform ratio R/X) applies to kappa in a MESHED network.</summary>
    public const double MeshedPeakSafetyFactor = 1.15;

    /// <summary>
    /// D-DOC-098 (October 2026): the peak factor for a short circuit in a <b>meshed</b> network, IEC 60909-0 Method B
    /// (ratio R/X at the short-circuit location): <c>kappa_b = 1.15 kappa(R/X)</c>, where R/X is that of the
    /// driving-point impedance Zk and 1.15 is the standard's safety factor for the error of taking one R/X from a
    /// network reduction with complex impedances. The standard says the product need not exceed 1.8 in low-voltage
    /// networks (Un &lt;= 1 kV) nor 2.0 in medium- and high-voltage networks, so it is capped there, never below
    /// kappa(R/X) itself (the cap bounds the safety factor, it does not reduce the series kappa). (The standard also
    /// says the 1.15 is not needed while R/X stays below 0.3 in every branch; that is a permission, not a rule, and the
    /// factor is always applied here, the conservative side for a making-current rating.) Constants as quoted from
    /// IEC 60909-0:2001 subclause 4.3.1.2 b), which is IEC 60909-0:2016 subclause 8.1.2 "Multiple-fed short circuit"
    /// (the 2016 contents list has no 8.1.3; checked [VERIFY] 2026-10-11; the clause text itself is not on disk, and the
    /// 2026 Edition 3.0 numbering is unchecked). Grid practice: NESO computes peak-make with Method C (ETYS Appendix D, docs/reference S242 p.4),
    /// which GridSim does not implement; Method B with 1.15 is the documented alternative and errs high.
    /// </summary>
    public static double PeakFactorMeshed(double rOverX, double nominalKvLL)
    {
        double kappa = PeakFactor(rOverX);
        double cap = nominalKvLL <= 1.0 ? 1.8 : 2.0;
        // The cap limits what the SAFETY FACTOR adds; it never takes kappa below its own series value. At low voltage a
        // kappa(R/X) above 1.8 (R/X below about 0.07) would otherwise be cut to 1.8 and the peak reported LOWER than
        // the unmeshed formula gives (measured on case300's LV buses: ip down to 0.9898 of the series value before
        // this guard).
        return Math.Max(kappa, Math.Min(MeshedPeakSafetyFactor * kappa, cap));
    }

    /// <summary>
    /// The thermal DC-heat factor <c>m</c> (IEC 60909-0):
    /// <c>m = (e^(4 f Tk ln(kappa-1)) - 1) / (2 f Tk ln(kappa-1))</c>, the DC component's contribution to
    /// the thermal-equivalent current over a fault duration <paramref name="faultDurationSeconds"/> (Tk).
    /// At <c>kappa = 2</c> (R = 0, a non-decaying DC) the removable <c>0/0</c> has the limit 2; as
    /// <c>kappa -&gt; 1</c> (heavily resistive) <c>m -&gt; 0</c>.
    /// </summary>
    public static double ThermalDcFactor(double kappa, double f0Hz, double faultDurationSeconds)
    {
        double l = DetMath.Log(kappa - 1.0);                 // ln(kappa - 1)
        double denom = 2.0 * f0Hz * faultDurationSeconds * l;
        if (Math.Abs(l) < 1e-9) return 2.0;               // kappa -> 2 limit (see summary)
        return (DetMath.Exp(2.0 * denom) - 1.0) / denom;     // 4 f Tk l = 2 * denom
    }

    /// <summary>The IEC 60909-0 breaking-time break points for the decay factors mu and q, in seconds.</summary>
    private static readonly double[] BreakTimes = { 0.02, 0.05, 0.10, 0.25 };

    /// <summary>
    /// The <b>near-to-generator</b> breaking-current factor <c>mu</c> (IEC 60909-0), which decays the
    /// symmetrical breaking current of a synchronous machine below its initial value: <c>Ib = mu * Ik''</c>.
    /// It is a closed form in the ratio <paramref name="ikppOverRatedCurrent"/> (<c>Ik''/IrG</c>, the machine's
    /// contribution to the initial current over its rated current) and the breaker minimum time delay
    /// <paramref name="minTimeDelaySeconds"/> (<c>tmin</c>):
    /// <list type="bullet">
    ///   <item><c>tmin = 0.02 s: mu = 0.84 + 0.26 e^{-0.26 Ik''/IrG}</c></item>
    ///   <item><c>tmin = 0.05 s: mu = 0.71 + 0.51 e^{-0.30 Ik''/IrG}</c></item>
    ///   <item><c>tmin = 0.10 s: mu = 0.62 + 0.72 e^{-0.32 Ik''/IrG}</c></item>
    ///   <item><c>tmin &gt;= 0.25 s: mu = 0.56 + 0.94 e^{-0.38 Ik''/IrG}</c></item>
    /// </list>
    /// Intermediate break times are linearly interpolated, as the standard directs. When the machine is
    /// <b>far from the fault</b> (<c>Ik''/IrG &lt;= 2</c>) there is no AC decay and <c>mu = 1</c>; the factor is
    /// capped at 1 (a machine's breaking current never exceeds its initial current).
    /// </summary>
    public static double BreakingFactorMu(double ikppOverRatedCurrent, double minTimeDelaySeconds)
    {
        if (ikppOverRatedCurrent <= 2.0) return 1.0;      // far from generator: no AC envelope decay
        double MuAt(int i) => i switch
        {
            0 => 0.84 + 0.26 * DetMath.Exp(-0.26 * ikppOverRatedCurrent),
            1 => 0.71 + 0.51 * DetMath.Exp(-0.30 * ikppOverRatedCurrent),
            2 => 0.62 + 0.72 * DetMath.Exp(-0.32 * ikppOverRatedCurrent),
            _ => 0.56 + 0.94 * DetMath.Exp(-0.38 * ikppOverRatedCurrent),
        };
        return Math.Min(InterpolateOverBreakTimes(minTimeDelaySeconds, MuAt), 1.0);
    }

    /// <summary>
    /// The <b>asynchronous-motor</b> breaking-current factor <c>q</c> (IEC 60909-0), applied alongside
    /// <c>mu</c> to a motor's contribution: <c>Ib_motor = q * mu * Ik''_motor</c>. It is a closed form in the
    /// motor's rated active power per pole pair <paramref name="ratedPowerPerPolePairMw"/> (MW) and the
    /// breaker minimum time delay:
    /// <list type="bullet">
    ///   <item><c>tmin = 0.02 s: q = 1.03 + 0.12 ln m</c></item>
    ///   <item><c>tmin = 0.05 s: q = 0.79 + 0.12 ln m</c></item>
    ///   <item><c>tmin = 0.10 s: q = 0.57 + 0.12 ln m</c></item>
    ///   <item><c>tmin &gt;= 0.25 s: q = 0.26 + 0.10 ln m</c></item>
    /// </list>
    /// Intermediate break times are linearly interpolated; the factor is capped at 1.
    /// </summary>
    public static double MotorBreakingFactorQ(double ratedPowerPerPolePairMw, double minTimeDelaySeconds)
    {
        if (!(ratedPowerPerPolePairMw > 0.0))
            throw new ArgumentOutOfRangeException(nameof(ratedPowerPerPolePairMw), "Rated power per pole pair must be > 0.");
        double lnM = DetMath.Log(ratedPowerPerPolePairMw);
        double QAt(int i) => i switch
        {
            0 => 1.03 + 0.12 * lnM,
            1 => 0.79 + 0.12 * lnM,
            2 => 0.57 + 0.12 * lnM,
            _ => 0.26 + 0.10 * lnM,
        };
        return Math.Min(InterpolateOverBreakTimes(minTimeDelaySeconds, QAt), 1.0);
    }

    /// <summary>
    /// The symmetrical breaking current <c>Ib = mu * Ik''</c> (kA) of a single synchronous machine that
    /// carries the whole fault current, from its initial symmetrical current <paramref name="ikppKa"/>, its
    /// rated current <paramref name="ratedCurrentKa"/> and the breaker minimum time delay. This is exact when
    /// one machine feeds the fault. In a network the per-machine mu enter through the IEC 60909-0:2016 multiple-fed
    /// formula, which both <see cref="ThreePhase"/> (R095) and the fleet total of <see cref="PartialThreePhase"/> (N049)
    /// apply; <see cref="PartialThreePhase"/> also lists each machine's own <c>mu_i * Ik''_i</c>.
    /// </summary>
    public static double SynchronousBreakingCurrentKa(double ikppKa, double ratedCurrentKa, double minTimeDelaySeconds)
    {
        if (!(ratedCurrentKa > 0.0))
            throw new ArgumentOutOfRangeException(nameof(ratedCurrentKa), "Rated current must be > 0.");
        return BreakingFactorMu(ikppKa / ratedCurrentKa, minTimeDelaySeconds) * ikppKa;
    }

    /// <summary>
    /// The <b>generator impedance-correction factor</b> <c>K_G</c> (IEC 60909-0, eq. for a generator directly
    /// connected to the network): the machine's impedance is corrected to <c>Z_GK = K_G * (R_G + jX''d)</c>
    /// before it enters the fault network, so that the equivalent source at rated conditions reproduces the
    /// machine's true short-circuit contribution. It is a closed form
    /// <c>K_G = (Un/UrG) * c_max / (1 + x''d * sin(phi_rG))</c> in the system nominal voltage
    /// <paramref name="nominalKvLL"/> (Un), the generator rated voltage <paramref name="ratedKvLL"/> (UrG),
    /// the subtransient reactance <paramref name="subtransientXdPuOnRating"/> (<c>x''d</c>, pu on the machine
    /// rating) and the rated power factor <paramref name="ratedPowerFactor"/> (<c>cos(phi_rG)</c>, whose sine
    /// gives the reactive loading correction). <paramref name="cMaxVoltageFactor"/> is the maximum-current
    /// voltage factor (see <see cref="VoltageFactor"/>).
    /// </summary>
    public static double GeneratorCorrectionKg(
        double nominalKvLL, double ratedKvLL, double subtransientXdPuOnRating, double ratedPowerFactor,
        double cMaxVoltageFactor)
    {
        if (!(ratedKvLL > 0.0)) throw new ArgumentOutOfRangeException(nameof(ratedKvLL), "Generator rated voltage must be > 0.");
        if (ratedPowerFactor < 0.0 || ratedPowerFactor > 1.0)
            throw new ArgumentOutOfRangeException(nameof(ratedPowerFactor), "Rated power factor must be in [0, 1].");
        double sinPhi = Math.Sqrt(1.0 - ratedPowerFactor * ratedPowerFactor);
        return (nominalKvLL / ratedKvLL) * cMaxVoltageFactor / (1.0 + subtransientXdPuOnRating * sinPhi);
    }

    /// <summary>
    /// The <b>transformer impedance-correction factor</b> <c>K_T</c> (IEC 60909-0, two-winding network
    /// transformer): the transformer impedance is corrected to <c>Z_TK = K_T * (R_T + jX_T)</c> before it
    /// enters the fault network. It is the closed form <c>K_T = 0.95 * c_max / (1 + 0.6 * x_T)</c>, where
    /// <paramref name="reactancePuOnRating"/> is the transformer relative reactance <c>x_T</c> (pu on the
    /// transformer's own rating) and <paramref name="cMaxVoltageFactor"/> the maximum-current voltage factor.
    /// The 0.95 and 0.6 are the standard's fixed constants.
    /// </summary>
    public static double TransformerCorrectionKt(double reactancePuOnRating, double cMaxVoltageFactor)
    {
        if (reactancePuOnRating < 0.0)
            throw new ArgumentOutOfRangeException(nameof(reactancePuOnRating), "Transformer reactance must be >= 0.");
        return 0.95 * cMaxVoltageFactor / (1.0 + 0.6 * reactancePuOnRating);
    }

    /// <summary>
    /// F21 (October 2026): the <b>power-station-unit correction factor with an on-load tap changer</b>, <c>K_S</c>
    /// (IEC 60909-0:2016, the power station unit with OLTC; equation number [VERIFY]: IEC 60909-0 is not in
    /// docs/reference). The unit's generator and step-up transformer are corrected together:
    /// <c>Z_S = K_S (t_r^2 Z_G + Z_THV)</c> with
    /// <c>K_S = (UnQ^2 / UrG^2) (UrTLV^2 / UrTHV^2) c_max / (1 + |x''d - x_T| sin(phi_rG))</c>, where UnQ is the
    /// network nominal voltage at the unit's HV point, UrG the generator rated voltage, UrTLV / UrTHV the transformer
    /// rated voltages, x''d and x_T the generator and transformer reactances (pu on their own ratings) and
    /// cos(phi_rG) the generator rated power factor. [VERIFY] the form against the standard's text.
    /// </summary>
    public static double PowerStationUnitCorrectionKs(
        double networkKvLL, double ratedGeneratorKvLL, double ratedTransformerLvKv, double ratedTransformerHvKv,
        double subtransientXdPuOnRating, double transformerXPuOnRating, double ratedPowerFactor, double cMaxVoltageFactor)
    {
        if (!(ratedGeneratorKvLL > 0.0) || !(ratedTransformerHvKv > 0.0) || !(ratedTransformerLvKv > 0.0))
            throw new ArgumentOutOfRangeException(nameof(ratedGeneratorKvLL), "Rated voltages must be > 0.");
        if (ratedPowerFactor < 0.0 || ratedPowerFactor > 1.0)
            throw new ArgumentOutOfRangeException(nameof(ratedPowerFactor), "Rated power factor must be in [0, 1].");
        double sinPhi = Math.Sqrt(1.0 - ratedPowerFactor * ratedPowerFactor);
        double g = networkKvLL / ratedGeneratorKvLL;
        double t = ratedTransformerLvKv / ratedTransformerHvKv;
        return g * g * (t * t) * cMaxVoltageFactor / (1.0 + Math.Abs(subtransientXdPuOnRating - transformerXPuOnRating) * sinPhi);
    }

    /// <summary>
    /// F21 (October 2026): the <b>power-station-unit correction factor without an on-load tap changer</b>,
    /// <c>K_SO</c> (IEC 60909-0:2016; equation number [VERIFY]):
    /// <c>K_SO = (UnQ / (UrG (1 + p_G))) (UrTLV / UrTHV) (1 +/- p_T) c_max / (1 + x''d sin(phi_rG))</c>, with p_G the
    /// generator voltage-regulation range and <paramref name="transformerTapFactor"/> the <c>(1 +/- p_T)</c> of the
    /// unit transformer's off-load tap position (1 at the main tap). [VERIFY] the form against the standard's text.
    /// </summary>
    public static double PowerStationUnitCorrectionKso(
        double networkKvLL, double ratedGeneratorKvLL, double generatorVoltageRangePG, double ratedTransformerLvKv,
        double ratedTransformerHvKv, double transformerTapFactor, double subtransientXdPuOnRating, double ratedPowerFactor,
        double cMaxVoltageFactor)
    {
        if (!(ratedGeneratorKvLL > 0.0) || !(ratedTransformerHvKv > 0.0) || !(ratedTransformerLvKv > 0.0))
            throw new ArgumentOutOfRangeException(nameof(ratedGeneratorKvLL), "Rated voltages must be > 0.");
        if (ratedPowerFactor < 0.0 || ratedPowerFactor > 1.0)
            throw new ArgumentOutOfRangeException(nameof(ratedPowerFactor), "Rated power factor must be in [0, 1].");
        double sinPhi = Math.Sqrt(1.0 - ratedPowerFactor * ratedPowerFactor);
        return networkKvLL / (ratedGeneratorKvLL * (1.0 + generatorVoltageRangePG)) * (ratedTransformerLvKv / ratedTransformerHvKv)
            * transformerTapFactor * cMaxVoltageFactor / (1.0 + subtransientXdPuOnRating * sinPhi);
    }

    /// <summary>F21: one impedance correction applied by <see cref="ApplyCorrectionFactors"/>.</summary>
    /// <param name="Element">"generator", "power station unit" or "transformer".</param>
    /// <param name="Name">The generator or branch name (or its bus / ends).</param>
    /// <param name="Factor">"K_G", "K_S", "K_SO" or "K_T".</param>
    /// <param name="Value">The factor applied (multiplies the element's impedance).</param>
    public sealed record CorrectionApplied(string Element, string Name, string Factor, double Value);

    /// <summary>F21: what <see cref="ApplyCorrectionFactors"/> did: the corrected model, each correction, and the
    /// elements it could not correct (with why).</summary>
    public sealed record CorrectionReport(GridModel Model, IReadOnlyList<CorrectionApplied> Applied, IReadOnlyList<string> NotApplied);

    /// <summary>
    /// F21 (October 2026): apply the IEC 60909-0 impedance-correction factors to a copy of <paramref name="model"/>,
    /// for a MAXIMUM short-circuit study (opt-in; every existing study runs on the uncorrected model, unchanged). The
    /// corrected copy is then studied by the unchanged duty methods, so nothing else in the calculation moves.
    /// <list type="bullet">
    /// <item><b>Generator directly connected</b> (no unit transformer modelled, <see cref="Generator.UnitTransformerXPu"/>
    /// 0): <c>K_G</c> (<see cref="GeneratorCorrectionKg"/>) multiplies its X''d and X2.</item>
    /// <item><b>Power station unit</b> (a unit transformer modelled): <c>K_S</c> (with OLTC,
    /// <paramref name="unitTransformersHaveOltc"/>) or <c>K_SO</c> (without, main tap, p_G = 0) multiplies X''d, X2 and
    /// the unit transformer reactance together.</item>
    /// <item><b>Network transformer</b> (<see cref="Branch.IsTransformer"/>): <c>K_T</c>
    /// (<see cref="TransformerCorrectionKt"/>) multiplies R, X, R0 and X0, with x_T taken on the transformer's own
    /// rating as <c>|X| RateA / BaseMva</c> (RateA standing for the rated power S_rT); a transformer with no RateA is
    /// left uncorrected and listed in <see cref="CorrectionReport.NotApplied"/>.</item>
    /// <item>GR-126: an element on a bus with no base voltage (BaseKv 0 or below) is likewise left uncorrected and
    /// listed, since c_max cannot be chosen; it used to be taken as low voltage (1.05).</item>
    /// </list>
    /// The model is per unit on nominal voltages, so the voltage ratios of the formulas (Un/UrG, UrTLV/UrTHV) are taken
    /// as 1: a generator rated at its bus's nominal voltage and a unit transformer on nominal ratios. GridSim carries no
    /// generator rated power factor, so <paramref name="ratedPowerFactor"/> is required from the caller (never assumed).
    /// c_max is the IEC Table 1 maximum factor at the element's bus nominal voltage (the higher of a transformer's two).
    /// Converter-interfaced and power-flow-only units are not corrected (they are current sources / not sources).
    /// [VERIFY]: whether the standard also corrects the zero-sequence impedances and the minimum-current case; here
    /// R0 / X0 of a transformer are corrected with K_T, and the correction is offered for the maximum case only.
    /// </summary>
    public static CorrectionReport ApplyCorrectionFactors(
        GridModel model, double ratedPowerFactor, bool unitTransformersHaveOltc = true, double subtransientXdPu = DefaultSubtransientXdPu)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!(ratedPowerFactor > 0.0) || ratedPowerFactor > 1.0)
            throw new ArgumentOutOfRangeException(nameof(ratedPowerFactor), "The generator rated power factor must be in (0, 1].");
        var kvOf = new Dictionary<int, double>(model.Buses.Count);
        foreach (Bus b in model.Buses) kvOf.TryAdd(b.Id, b.BaseKv);
        double CMax(int bus) => VoltageFactor(kvOf.GetValueOrDefault(bus), ShortCircuitObjective.Maximum);
        // GR-126: a bus with no base voltage (BaseKv 0 or below, or a bus not in the model) has no IEC Table 1 row.
        // CMax used to read it as low voltage (c_max 1.05) without a word; an element touching such a bus is now left
        // uncorrected and listed in NotApplied, like a transformer with no rating.
        bool HasKv(int bus) => kvOf.GetValueOrDefault(bus) > 0.0;

        var applied = new List<CorrectionApplied>();
        var notApplied = new List<string>();
        var fleet = new List<Generator>(model.AllGenerators.Count);
        foreach (Generator g in model.AllGenerators)
        {
            if (!g.InService || g.PowerFlowOnly || g.IsConverterInterfaced) { fleet.Add(g); continue; }
            string name = g.Name ?? $"gen@bus {g.Bus}";
            if (!HasKv(g.Bus))
            {
                notApplied.Add($"{name}: bus {g.Bus} has no base voltage, so c_max is unknown; "
                    + (g.UnitTransformerXPu > 0.0 ? "K_S / K_SO" : "K_G") + " not applied");
                fleet.Add(g);
                continue;
            }
            double xd = g.EffectiveSubtransientXdPu(subtransientXdPu);
            double x2 = g.EffectiveNegativeSeqXPu(subtransientXdPu);
            double c = CMax(g.Bus);
            if (g.UnitTransformerXPu > 0.0)
            {
                double k = unitTransformersHaveOltc
                    ? PowerStationUnitCorrectionKs(1.0, 1.0, 1.0, 1.0, xd, g.UnitTransformerXPu, ratedPowerFactor, c)
                    : PowerStationUnitCorrectionKso(1.0, 1.0, 0.0, 1.0, 1.0, 1.0, xd, ratedPowerFactor, c);
                fleet.Add(g with { SubtransientXdPu = k * xd, NegativeSeqXPu = k * x2, UnitTransformerXPu = k * g.UnitTransformerXPu });
                applied.Add(new CorrectionApplied("power station unit", name, unitTransformersHaveOltc ? "K_S" : "K_SO", k));
            }
            else
            {
                double k = GeneratorCorrectionKg(1.0, 1.0, xd, ratedPowerFactor, c);
                fleet.Add(g with
                {
                    SubtransientXdPu = k * xd, NegativeSeqXPu = k * x2,
                    // IEC 60909-0 corrects the machine's zero-sequence impedance with the same K_G.
                    ZeroSequenceZPu = g.ZeroSequenceZPu is MachineZeroSequence z0 ? new MachineZeroSequence(k * z0.RPu, k * z0.XPu) : null,
                });
                applied.Add(new CorrectionApplied("generator", name, "K_G", k));
            }
        }

        var branches = new List<Branch>(model.Branches.Count);
        foreach (Branch br in model.Branches)
        {
            if (!br.IsTransformer || !br.InService) { branches.Add(br); continue; }
            string name = string.IsNullOrEmpty(br.Name) ? $"transformer {br.From}-{br.To}" : br.Name!;
            if (!(br.RateAMva > 0.0))
            {
                notApplied.Add($"{name}: no rating (RateA), so its reactance on its own rating is unknown; K_T not applied");
                branches.Add(br);
                continue;
            }
            if (!HasKv(br.From) || !HasKv(br.To))
            {
                notApplied.Add($"{name}: bus {(HasKv(br.From) ? br.To : br.From)} has no base voltage, so c_max is unknown; K_T not applied");
                branches.Add(br);
                continue;
            }
            double xT = Math.Abs(br.X) * br.RateAMva / model.BaseMva;
            double c = Math.Max(CMax(br.From), CMax(br.To));
            double k = TransformerCorrectionKt(xT, c);
            branches.Add(br with { R = k * br.R, X = k * br.X, R0 = k * br.R0, X0 = k * br.X0 });
            applied.Add(new CorrectionApplied("transformer", name, "K_T", k));
        }
        return new CorrectionReport(model.CopyWith(generators: fleet, branches: branches), applied, notApplied);
    }

    /// <summary>Linearly interpolate a decay factor tabulated at <see cref="BreakTimes"/> to a break time
    /// <paramref name="tmin"/>, clamping to the end values outside the tabulated range (IEC 60909-0).</summary>
    private static double InterpolateOverBreakTimes(double tmin, Func<int, double> valueAt)
    {
        // GR-609: a NaN break time fell through every comparison and silently used the 0.25 s value.
        if (double.IsNaN(tmin))
            throw new ArgumentOutOfRangeException(nameof(tmin), tmin, "The minimum time delay (break time) must be a number of seconds.");
        if (tmin <= BreakTimes[0]) return valueAt(0);
        int last = BreakTimes.Length - 1;
        if (tmin >= BreakTimes[last]) return valueAt(last);
        for (int i = 1; i <= last; i++)
        {
            if (tmin <= BreakTimes[i])
            {
                double frac = (tmin - BreakTimes[i - 1]) / (BreakTimes[i] - BreakTimes[i - 1]);
                return valueAt(i - 1) + frac * (valueAt(i) - valueAt(i - 1));
            }
        }
        return valueAt(last);   // unreachable: tmin < BreakTimes[last] handled above
    }

    /// <summary>The full IEC 60909 duty at a fault, currents in kA (and the driving <c>Ik''</c> in pu).</summary>
    public sealed record Duty(
        double IkppKa,           // initial symmetrical short-circuit current Ik''
        double PeakIpKa,         // peak short-circuit current ip = kappa * sqrt(2) * Ik''
        double BreakingIbKa,     // symmetrical breaking current Ib (ThreePhase: per-machine mu, multiple-fed formula, R095; = Ik'' far from generator and for unbalanced faults)
        double ThermalIthKa,     // thermal-equivalent current Ith = Ik'' * sqrt(m + n)
        double SteadyIkKa,       // steady-state short-circuit current Ik (= Ik'' far from generator)
        double DcComponentKa,    // DC component idc at the breaking time
        double Kappa,            // peak factor
        double ThermalM,         // DC thermal factor m
        double RoverX,           // fault-loop R/X
        double IkppPu,           // Ik'' in per-unit on the system base
        double TheveninRPu, double TheveninXPu,
        double BreakTimeSeconds,      // the breaker minimum time delay tb the DC component idc is taken at
        double FaultDurationSeconds,  // the fault duration Tk the thermal Ith is taken over
        double ConverterIkppKa = 0.0, // the part of Ik'' fed by converter-interfaced plant (current sources)
        bool MeshedPeakFactor = false, // D-DOC-098: Kappa is the meshed-network kappa_b = 1.15 kappa (capped), Method B
        SteadyStateBasis SteadyBasis = SteadyStateBasis.FarFromGenerator, // D-DOC-097: how Ik (and n) was obtained
        PeakFactorMethod PeakMethod = PeakFactorMethod.MethodB,  // N134: which method gave Kappa (and, CF4, idc)
        double PeakRoverX = double.NaN,  // N134: the R/X Kappa was taken from under Method C (R/X_C); NaN under Method B
        double DcRoverX = double.NaN,    // N134 rest (CF4): the R/X idc was taken from under Method C; NaN under Method B
        double DlgLineToEarthRatio = double.NaN); // GR-617: double-line-to-earth only, the largest line-conductor current over the earth current 3 I0; NaN otherwise

    /// <summary>A switchgear device's short-circuit ratings (IEC 62271 / IEEE C37), to check a computed
    /// <see cref="Duty"/> against - the rated maximum voltage (kV, line-to-line), the peak making current, the
    /// symmetrical breaking current, and the short-time withstand current over its rated duration (kA rms).
    /// <para>GR-124: <c>BreakingAsymKa</c> is the optional rated ASYMMETRICAL breaking current (kA rms, the IEC 62271-100
    /// <c>I_sc sqrt(1 + 2 (dc%)^2)</c> figure). When it is given the check also tests the DC (asymmetrical) breaking duty
    /// <c>sqrt(Ib^2 + idc^2)</c>; NaN (the default) leaves that check out and says so in
    /// <see cref="BreakerAdequacy.AsymmetricalChecked"/>.</para></summary>
    public sealed record BreakerRating(
        double RatedVoltageKv,
        double MakingPeakKa,
        double BreakingSymKa,
        double ShortTimeWithstandKa,
        double ShortTimeSeconds = 1.0,
        double BreakingAsymKa = double.NaN);

    /// <summary>The adequacy of a breaker against a duty: each rating checked, with the signed kA margin
    /// (rating minus demand, so <c>&gt;= 0</c> is adequate) and the overall verdict. GR-606: <c>RequiredVoltageKv</c> is
    /// the highest system voltage the rated voltage was compared with. GR-124: the asymmetrical (DC) breaking check is
    /// made only when the rating carries one (<c>AsymmetricalChecked</c>); otherwise <c>AsymmetricalOk</c> is true and
    /// its margin NaN.</summary>
    public sealed record BreakerAdequacy(
        bool VoltageOk, bool MakingOk, bool BreakingOk, bool ThermalOk, bool Adequate,
        double MakingMarginKa, double BreakingMarginKa, double ThermalMarginKa,
        double RequiredVoltageKv = double.NaN,
        bool AsymmetricalChecked = false, bool AsymmetricalOk = true, double AsymmetricalMarginKa = double.NaN);

    /// <summary>The IEC 60038 highest voltages for equipment <c>Um</c> (kV, line-to-line) above 1 kV, ascending.</summary>
    private static readonly double[] StandardUmKv =
        { 3.6, 7.2, 12.0, 17.5, 24.0, 36.0, 52.0, 72.5, 100.0, 123.0, 145.0, 170.0, 245.0, 300.0, 362.0, 420.0, 550.0, 800.0, 1100.0, 1200.0 };

    /// <summary>
    /// GR-606: the highest system voltage a switchgear rated voltage must cover for a system of nominal voltage
    /// <paramref name="nominalKv"/> (line-to-line). IEC rates a device by the highest voltage for equipment <c>Um</c>, not
    /// by the nominal voltage (a 400 kV system needs a 420 kV device). Above 1 kV this is the lowest IEC 60038 standard
    /// <c>Um</c> at or above the nominal voltage (11 kV to 12, 33 to 36, 132 to 145, 275 to 300, 345 to 362, 400 to
    /// 420); at or below 1 kV it is the nominal voltage plus the IEC 60038 +10 % tolerance; above the largest standard
    /// <c>Um</c> (1200 kV) it is the nominal voltage itself.
    /// </summary>
    public static double HighestSystemVoltageKv(double nominalKv)
    {
        if (!(nominalKv > 0.0) || !double.IsFinite(nominalKv))
            throw new ArgumentOutOfRangeException(nameof(nominalKv), nominalKv, "The nominal system voltage must be a finite number of kV above 0.");
        if (nominalKv <= 1.0) return 1.1 * nominalKv;
        foreach (double um in StandardUmKv)
            if (um >= nominalKv) return um;
        return nominalKv;
    }

    /// <summary>
    /// Check a <see cref="BreakerRating"/> against a computed <see cref="Duty"/> at a system voltage
    /// <paramref name="systemVoltageKv"/>. Four checks, each with a signed kA margin (rating minus demand):
    /// the rated voltage must cover the system voltage; the making (peak) rating must cover <c>ip</c>; the
    /// symmetrical breaking rating must cover <c>Ib</c>; and the short-time withstand must cover the thermal
    /// duty on an <b>equal-energy</b> (<c>I^2 t</c>) basis - for a fault at least as long as the rated duration the
    /// rating is referred to the fault duration by <c>I_thr * sqrt(Tkr / Tk)</c> before comparing to <c>Ith</c>; for a
    /// shorter fault the rating is used as it stands (<c>I_thr * sqrt(min(1, Tkr / Tk))</c>: never raised). The
    /// device is adequate only when all four pass.
    /// <para>GR-606 (October 2026): <paramref name="systemVoltageKv"/> is the system's NOMINAL voltage, and the rated
    /// voltage is compared with the highest system voltage <see cref="HighestSystemVoltageKv"/> (IEC 60038 <c>Um</c>),
    /// not with the nominal: a 400 kV-rated device on a 400 kV system is not adequate, a 420 kV one is.</para>
    /// <para>GR-124: a rating with an asymmetrical breaking current is also checked against the asymmetrical breaking
    /// duty <c>sqrt(Ib^2 + idc^2)</c>, the rms of the AC breaking current with the DC component at <c>tb</c>.</para>
    /// <para>GR-617: for a double-line-to-earth duty (whose <see cref="Duty.IkppKa"/> is the earth current <c>3 I0</c>)
    /// every current demand is raised to the largest line-conductor current, by <see cref="Duty.DlgLineToEarthRatio"/>
    /// when that is above 1, because a breaker pole breaks the line current, which can exceed the earth current.</para>
    /// </summary>
    public static BreakerAdequacy CheckBreaker(Duty duty, BreakerRating rating, double systemVoltageKv)
    {
        ArgumentNullException.ThrowIfNull(duty);
        ArgumentNullException.ThrowIfNull(rating);
        double requiredKv = HighestSystemVoltageKv(systemVoltageKv);
        bool voltageOk = rating.RatedVoltageKv >= requiredKv;

        // GR-617: the line-conductor scale of a double-line-to-earth duty (1 for every other fault type). The line
        // currents are collinear with the earth current (one sequence connection driven by real source voltages, the
        // converter uplift included), so the peak, breaking, DC and thermal demands all scale by the same ratio.
        double s = double.IsNaN(duty.DlgLineToEarthRatio) ? 1.0 : Math.Max(1.0, duty.DlgLineToEarthRatio);

        double makingMargin = rating.MakingPeakKa - s * duty.PeakIpKa;
        double breakingMargin = rating.BreakingSymKa - s * duty.BreakingIbKa;

        // Thermal check (IEC 60865-1 clause 7, with the IEC 60909 thermal equivalent current): for a fault at least as
        // long as the rated duration (Tk >= Tkr) the rating is referred on an equal-energy basis,
        // I_thr^2 Tkr >= Ith^2 Tk  <=>  I_thr sqrt(Tkr/Tk) >= Ith; for a shorter fault (Tk < Tkr) the criterion is
        // Ith <= I_thr, never a raised rating, because the rated short-time current is also an electromechanical
        // ceiling. Hence sqrt(min(1, Tkr/Tk)): the referral only ever scales the rating DOWN. (Before, a 40 kA / 3 s
        // rating was compared at a 0.5 s fault against 98 kA, so a 60 kA duty read adequate.)
        double ithRatingAtFault = rating.ShortTimeSeconds > 0.0 && duty.FaultDurationSeconds > 0.0
            ? rating.ShortTimeWithstandKa * Math.Sqrt(Math.Min(1.0, rating.ShortTimeSeconds / duty.FaultDurationSeconds))
            : rating.ShortTimeWithstandKa;
        double thermalMargin = ithRatingAtFault - s * duty.ThermalIthKa;

        // GR-124: the asymmetrical breaking check, only when the rating carries one. A rating that is given but is not a
        // finite number above 0 is refused, not skipped.
        bool asymChecked = !double.IsNaN(rating.BreakingAsymKa);
        double asymMargin = double.NaN;
        bool asymOk = true;
        if (asymChecked)
        {
            if (!(rating.BreakingAsymKa > 0.0) || !double.IsFinite(rating.BreakingAsymKa))
                throw new ArgumentOutOfRangeException(nameof(rating), rating.BreakingAsymKa,
                    "The rated asymmetrical breaking current must be a finite number of kA above 0 (or NaN for no asymmetrical check).");
            double ib = s * duty.BreakingIbKa, idc = s * duty.DcComponentKa;
            asymMargin = rating.BreakingAsymKa - Math.Sqrt(ib * ib + idc * idc);
            asymOk = asymMargin >= 0.0;
        }

        bool makingOk = makingMargin >= 0.0, breakingOk = breakingMargin >= 0.0, thermalOk = thermalMargin >= 0.0;
        return new BreakerAdequacy(voltageOk, makingOk, breakingOk, thermalOk,
            voltageOk && makingOk && breakingOk && thermalOk && asymOk, makingMargin, breakingMargin, thermalMargin,
            requiredKv, asymChecked, asymOk, asymMargin);
    }

    /// <summary>
    /// The duty from an already-reduced positive-sequence Thevenin impedance <c>Zk = R + jX</c> (pu on the
    /// system base) at a bus of nominal line-to-line voltage <paramref name="nominalKvLL"/>. Pure and
    /// closed-form - the standards core. <paramref name="cVoltageFactor"/> is the IEC voltage factor (see
    /// <see cref="VoltageFactor"/>); <paramref name="breakTimeSeconds"/> is the minimum time delay of the
    /// breaker (for <c>idc</c>) and <paramref name="faultDurationSeconds"/> the fault duration Tk (for
    /// <c>Ith</c>). Far-from-generator: <c>mu = 1</c>, <c>n = 1</c>, so <c>Ib = Ik = Ik''</c>.
    /// <para>D-DOC-098 (October 2026): <paramref name="meshedNetwork"/> true applies the meshed-network peak factor
    /// <see cref="PeakFactorMeshed"/> (Method B, 1.15 kappa, capped) to ip (the thermal factor m keeps kappa(R/X)); false (the
    /// default, and the pure closed form) keeps kappa = <see cref="PeakFactor"/>, exact for a single series path.</para>
    /// </summary>
    public static Duty FromThevenin(
        double cVoltageFactor, double nominalKvLL, double theveninRPu, double theveninXPu, double baseMva,
        double f0Hz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz, double breakTimeSeconds = 0.10, double faultDurationSeconds = 1.0,
        bool meshedNetwork = false)
    {
        if (nominalKvLL <= 0.0) throw new ArgumentOutOfRangeException(nameof(nominalKvLL), "Nominal voltage must be > 0.");
        if (baseMva <= 0.0) throw new ArgumentOutOfRangeException(nameof(baseMva), "System base MVA must be > 0.");
        // The time and frequency inputs of the pure duty formulae, checked (they were taken as given): Tk = 0 made the
        // thermal factor NaN, a negative breaker time put idc above sqrt(2) Ik'', and f0 scales every exponent.
        if (!(f0Hz > 0.0) || !double.IsFinite(f0Hz))
            throw new ArgumentOutOfRangeException(nameof(f0Hz), f0Hz, "The nominal frequency must be a finite number of Hz above 0.");
        if (!(breakTimeSeconds >= 0.0) || !double.IsFinite(breakTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(breakTimeSeconds), breakTimeSeconds, "The breaker time tb must be a finite number of seconds, 0 or more.");
        if (!(faultDurationSeconds > 0.0) || !double.IsFinite(faultDurationSeconds))
            throw new ArgumentOutOfRangeException(nameof(faultDurationSeconds), faultDurationSeconds, "The fault duration Tk must be a finite number of seconds above 0.");
        // A fault loop is inductive: X must be above 0. X = 0 used to give R/X = 0 silently (the opposite of a purely
        // resistive loop's infinite R/X), and a negative X was taken as given.
        if (!(theveninXPu > 0.0) || !double.IsFinite(theveninXPu) || !double.IsFinite(theveninRPu))
            throw new ArgumentOutOfRangeException(nameof(theveninXPu), theveninXPu, "The Thevenin reactance must be a finite number above 0 pu (and R finite).");

        double zMag = Math.Sqrt(theveninRPu * theveninRPu + theveninXPu * theveninXPu);
        if (!(zMag > 0.0)) throw new ArgumentOutOfRangeException(nameof(theveninXPu), "Thevenin impedance magnitude must be > 0.");

        // Ik'' = c / |Zk| in pu; the fault-loop R/X (which drives every asymmetry factor) is that of Zk.
        double ikppPu = cVoltageFactor / zMag;
        double rOverX = theveninXPu != 0.0 ? theveninRPu / theveninXPu : 0.0;
        return Assemble(ikppPu, rOverX, theveninRPu, theveninXPu, nominalKvLL, baseMva, f0Hz, breakTimeSeconds, faultDurationSeconds,
            meshedNetwork);
    }

    /// <summary>
    /// The initial symmetrical current and full duty for an <b>unbalanced</b> fault from the sequence
    /// Thevenin impedances at the fault bus (<paramref name="z1"/>, <paramref name="z2"/>,
    /// <paramref name="z0"/>, pu on the system base), driven by the equivalent source
    /// <c>c*Un/sqrt(3)</c>. The sequence connection sets the initial current: single-line-to-earth
    /// <c>|3c/(Z1+Z2+Z0)|</c>, line-to-line <c>sqrt(3)|c/(Z1+Z2)|</c>, double-line-to-earth the earth
    /// current <c>|3 I0|</c> with the negative and zero networks in parallel. The peak/DC/thermal factors
    /// use the R/X of that connection's total series impedance. Far-from-generator (<c>mu = n = 1</c>).
    /// The record's Thevenin fields carry the positive-sequence <c>Z1</c> for reference.
    /// <para>D-DOC-098: <paramref name="meshedNetwork"/> as for <see cref="FromThevenin"/> (the meshed-network kappa on
    /// the connection's loop R/X).</para>
    /// </summary>
    public static Duty FromSequenceImpedances(
        UnbalancedFaultType type, double cVoltageFactor, double nominalKvLL,
        Complex z1, Complex z2, Complex z0, double baseMva,
        double f0Hz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz, double breakTimeSeconds = 0.10, double faultDurationSeconds = 1.0,
        bool meshedNetwork = false)
    {
        if (nominalKvLL <= 0.0) throw new ArgumentOutOfRangeException(nameof(nominalKvLL), "Nominal voltage must be > 0.");
        if (baseMva <= 0.0) throw new ArgumentOutOfRangeException(nameof(baseMva), "System base MVA must be > 0.");

        double c = cVoltageFactor;
        double dlgRatio = double.NaN;   // GR-617: set for a double-line-to-earth fault only
        Complex ikpp;     // initial symmetrical fault current phasor (pu, source c on the phase base)
        Complex loopZ;    // the series impedance whose R/X drives the asymmetry factors
        switch (type)
        {
            case UnbalancedFaultType.SingleLineToGround:
                loopZ = z1 + z2 + z0;
                ikpp = 3.0 * c / loopZ;                       // |3 I1|, I1 = c/(Z1+Z2+Z0)
                break;
            case UnbalancedFaultType.LineToLine:
                loopZ = z1 + z2;
                ikpp = Math.Sqrt(3.0) * c / loopZ;            // sqrt(3)|I1|, I1 = c/(Z1+Z2)
                break;
            case UnbalancedFaultType.DoubleLineToGround:
                Complex zPar = z2 * z0 / (z2 + z0);
                loopZ = z1 + zPar;
                Complex i1 = c / loopZ;
                Complex i0 = -i1 * z2 / (z2 + z0);
                ikpp = 3.0 * i0;                              // earth current |3 I0|
                // GR-617: the line-conductor currents I_L2 = I0 + a^2 I1 + a I2 and I_L3 = I0 + a I1 + a^2 I2, so the
                // breaker check can judge the largest pole current, not only the earth current.
                Complex i2 = -i1 * z0 / (z2 + z0);
                Complex a = new(-0.5, Math.Sqrt(3.0) / 2.0);
                Complex a2 = a * a;
                double lineMax = Math.Max((i0 + a2 * i1 + a * i2).Magnitude, (i0 + a * i1 + a2 * i2).Magnitude);
                double earth = ikpp.Magnitude;
                dlgRatio = earth > 0.0 ? lineMax / earth : double.NaN;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown fault type.");
        }

        double zMag = loopZ.Magnitude;
        if (!(zMag > 0.0)) throw new ArgumentOutOfRangeException(nameof(z1), "Sequence loop impedance magnitude must be > 0.");
        double rOverX = loopZ.Imaginary != 0.0 ? loopZ.Real / loopZ.Imaginary : 0.0;
        // An unbalanced fault has no flux decay in IEC 60909-0 (Ib = Ik = Ik''), D-DOC-097's UnbalancedNoDecay basis.
        return Assemble(ikpp.Magnitude, rOverX, z1.Real, z1.Imaginary, nominalKvLL, baseMva, f0Hz, breakTimeSeconds, faultDurationSeconds,
            meshedNetwork) with { SteadyBasis = SteadyStateBasis.UnbalancedNoDecay, DlgLineToEarthRatio = dlgRatio };
    }

    /// <summary>
    /// Line-to-line IEC 60909 duty at <paramref name="faultBusId"/> from the model. It needs only the
    /// positive- and negative-sequence networks (no earth path). <c>Z1</c> is
    /// <see cref="PositiveSequenceThevenin"/>'s network; <c>Z2</c> is
    /// <see cref="NegativeSequenceThevenin"/>'s, which uses each machine's <c>X2</c> and defaults to
    /// <c>X2 = X''d</c> (so <c>Z2 = Z1</c>) until real negative-sequence data is supplied. Single-line-to-earth is
    /// <see cref="SingleLineToGround"/>, on the zero-sequence network from <see cref="ZeroSequenceThevenin"/>
    /// (R095: this doc used to say the model did not carry the zero-sequence data); a double-line-to-earth duty
    /// can be formed with <see cref="FromSequenceImpedances"/> from those Z0, Z1 and Z2.
    /// </summary>
    public static Duty LineToLine(
        GridModel model, int faultBusId,
        ShortCircuitObjective objective = ShortCircuitObjective.Maximum,
        double subtransientXdPu = DefaultSubtransientXdPu,
        double f0Hz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz, double breakTimeSeconds = 0.10, double faultDurationSeconds = 1.0,
        PeakFactorMethod peakFactorMethod = PeakFactorMethod.MethodB)
    {
        RequireKnownMethod(peakFactorMethod);
        PositiveNetwork net = BuildPositiveNetwork(model, faultBusId, subtransientXdPu);
        (Complex z2, _) = NegativeSequenceThevenin(model, faultBusId, subtransientXdPu);
        double c = VoltageFactor(net.Un, objective);
        Duty sync = FromSequenceImpedances(UnbalancedFaultType.LineToLine, c, net.Un, net.Zkk, z2, Complex.Zero,
            model.BaseMva, f0Hz, breakTimeSeconds, faultDurationSeconds, net.Meshed);   // D-DOC-098: meshed kappa
        // N134 rest (CF4): Method C on the line-to-line connection (Z1 + Z2 at the equivalent frequencies). The default
        // (Method B) skips this line, so its duty is bit for bit as before.
        if (peakFactorMethod == PeakFactorMethod.MethodC)
            sync = WithMethodC(sync, UnbalancedFaultType.LineToLine, model, faultBusId, subtransientXdPu, DefaultLineZeroSeqRatio,
                f0Hz, breakTimeSeconds);
        return AddConverterShare(sync, net, UnbalancedFaultType.LineToLine, z2, Complex.Zero, model.BaseMva,
            f0Hz, breakTimeSeconds, faultDurationSeconds);
    }

    /// <summary>
    /// Single-line-to-earth IEC 60909 duty at <paramref name="faultBusId"/> from the model, wired end-to-end
    /// through all three sequence networks: <c>Z1</c> from <see cref="PositiveSequenceThevenin"/>, <c>Z2</c>
    /// from <see cref="NegativeSequenceThevenin"/>, and <c>Z0</c> from <see cref="ZeroSequenceThevenin"/>
    /// (built from the transformer winding connections). When the fault bus is <b>ungrounded</b> in the zero
    /// sequence - no earthed neutral reaches it - no earth-fault current can flow and the returned duty is
    /// all-zero. <paramref name="lineZeroSeqRatio"/> is the standard multiple of a line's positive-sequence
    /// impedance used where it carries no explicit zero-sequence impedance (default 3).
    /// </summary>
    public static Duty SingleLineToGround(
        GridModel model, int faultBusId,
        ShortCircuitObjective objective = ShortCircuitObjective.Maximum,
        double subtransientXdPu = DefaultSubtransientXdPu, double lineZeroSeqRatio = 3.0,
        double f0Hz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz, double breakTimeSeconds = 0.10, double faultDurationSeconds = 1.0,
        PeakFactorMethod peakFactorMethod = PeakFactorMethod.MethodB)
        => UnbalancedFromModel(UnbalancedFaultType.SingleLineToGround, model, faultBusId, objective,
            subtransientXdPu, lineZeroSeqRatio, f0Hz, breakTimeSeconds, faultDurationSeconds, peakFactorMethod);

    /// <summary>
    /// Double-line-to-earth IEC 60909 duty at <paramref name="faultBusId"/> from the model, wired end-to-end
    /// through all three sequence networks (the reported current is the earth current <c>|3 I0|</c>). An
    /// ungrounded fault bus yields an all-zero duty, as for the single-line fault. See
    /// <see cref="SingleLineToGround"/> for the parameters.
    /// </summary>
    public static Duty DoubleLineToGround(
        GridModel model, int faultBusId,
        ShortCircuitObjective objective = ShortCircuitObjective.Maximum,
        double subtransientXdPu = DefaultSubtransientXdPu, double lineZeroSeqRatio = 3.0,
        double f0Hz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz, double breakTimeSeconds = 0.10, double faultDurationSeconds = 1.0,
        PeakFactorMethod peakFactorMethod = PeakFactorMethod.MethodB)
        => UnbalancedFromModel(UnbalancedFaultType.DoubleLineToGround, model, faultBusId, objective,
            subtransientXdPu, lineZeroSeqRatio, f0Hz, breakTimeSeconds, faultDurationSeconds, peakFactorMethod);

    /// <summary>Shared driver for the earth-fault duties from the model: builds all three sequence Thevenins,
    /// and returns an all-zero duty (via <see cref="Assemble"/> with a zero current) when the fault bus is
    /// ungrounded in the zero sequence.</summary>
    private static Duty UnbalancedFromModel(
        UnbalancedFaultType type, GridModel model, int faultBusId, ShortCircuitObjective objective,
        double subtransientXdPu, double lineZeroSeqRatio, double f0Hz, double breakTimeSeconds, double faultDurationSeconds,
        PeakFactorMethod peakFactorMethod = PeakFactorMethod.MethodB)
    {
        RequireKnownMethod(peakFactorMethod);
        PositiveNetwork net = BuildPositiveNetwork(model, faultBusId, subtransientXdPu);
        Complex z1 = net.Zkk;
        double un = net.Un;
        (Complex z2, _) = NegativeSequenceThevenin(model, faultBusId, subtransientXdPu);
        (Complex z0, _, bool grounded) = ZeroSequenceThevenin(model, faultBusId, lineZeroSeqRatio);
        double c = VoltageFactor(un, objective);

        if (!grounded)
        {
            // No earth path: the earth-fault current is exactly zero. Report a well-formed all-zero duty
            // (the R/X shown is the positive sequence's; every current is zero regardless).
            double rOverX = z1.Imaginary != 0.0 ? z1.Real / z1.Imaginary : 0.0;
            return Assemble(0.0, rOverX, z1.Real, z1.Imaginary, un, model.BaseMva, f0Hz, breakTimeSeconds, faultDurationSeconds,
                net.Meshed) with { SteadyBasis = SteadyStateBasis.UnbalancedNoDecay };
        }
        Duty sync = FromSequenceImpedances(type, c, un, z1, z2, z0, model.BaseMva, f0Hz, breakTimeSeconds, faultDurationSeconds,
            net.Meshed);   // D-DOC-098: meshed kappa
        // N134 rest (CF4): Method C on this fault's own sequence connection; skipped under Method B (bit for bit). An
        // ungrounded bus returned its all-zero duty above, where no method changes anything.
        if (peakFactorMethod == PeakFactorMethod.MethodC)
            sync = WithMethodC(sync, type, model, faultBusId, subtransientXdPu, lineZeroSeqRatio, f0Hz, breakTimeSeconds);
        return AddConverterShare(sync, net, type, z2, z0, model.BaseMva, f0Hz, breakTimeSeconds, faultDurationSeconds);
    }

    /// <summary>Assemble the duty family from the initial symmetrical current (pu), the fault-loop R/X, and a
    /// reference impedance to expose. Far-from-generator: mu = 1 and n = 1, so Ib = Ik = Ik''.</summary>
    private static Duty Assemble(
        double ikppPu, double rOverX, double refRPu, double refXPu,
        double nominalKvLL, double baseMva, double f0Hz, double breakTimeSeconds, double faultDurationSeconds,
        bool meshedNetwork = false)
    {
        double iBaseKa = baseMva / (Math.Sqrt(3.0) * nominalKvLL);
        double ikppKa = ikppPu * iBaseKa;

        // D-DOC-098 (October 2026): in a meshed network the PEAK factor is Method B's 1.15 kappa (capped at 2.0, or 1.8
        // at low voltage), PeakFactorMeshed. Not meshed (a single series path from one source), kappa(R/X) is exact and
        // is kept, so every radial study (and every pure closed-form call, which defaults to not meshed) is bit for bit
        // as before.
        // The thermal DC factor m keeps kappa(R/X), the network's own R/X, in both cases. The 1.15 is a safety margin on
        // the PEAK for the error of one R/X taken from a meshed reduction; carried into m it is not a small margin but a
        // different physics: once the cap of 2.0 binds (R/X below about 0.05, common on transmission), m becomes its
        // non-decaying-DC limit of 2 and Ith = sqrt(3) Ik'' for any fault duration (case9 bus 1 measured: Ith 3.1810 ->
        // 5.0825 kA with Tk = 1 s, for a DC component whose time constant is X / (omega R) = 164 ms). The DC heat is set
        // by the DC time constant, i.e. by R/X itself, so m stays on kappa(R/X) and Ith does not move. EE judgement,
        // recorded in the DOC-4 notes; [VERIFY] against IEC 60909-0:2016 clause 14 "Joule integral and thermal equivalent
        // short-circuit current" and Annex A (the formula for m; Figure 18) when the standard is on disk. (Clause 12 of
        // the 2016 edition is the transformer open-conductor case, not m: the earlier citation was wrong.)
        double kappaSeries = PeakFactor(rOverX);
        double kappa = meshedNetwork ? PeakFactorMeshed(rOverX, nominalKvLL) : kappaSeries;
        double ip = kappa * Math.Sqrt(2.0) * ikppKa;
        double idc = Math.Sqrt(2.0) * ikppKa * DetMath.Exp(-2.0 * Math.PI * f0Hz * breakTimeSeconds * rOverX);
        double m = ThermalDcFactor(kappaSeries, f0Hz, faultDurationSeconds);
        double ith = ikppKa * Math.Sqrt(m + 1.0);
        return new Duty(ikppKa, ip, ikppKa, ith, ikppKa, idc, kappa, m, rOverX, ikppPu, refRPu, refXPu,
            breakTimeSeconds, faultDurationSeconds, MeshedPeakFactor: meshedNetwork);
    }

    /// <summary>
    /// Balanced three-phase IEC 60909 duty at <paramref name="faultBusId"/>. Assembles the positive-sequence
    /// fault network - the network branches plus each generator as its subtransient reactance to ground, with
    /// <b>loads neglected</b> (the 60909 convention) - solves one Zbus column for the driving-point impedance
    /// at the fault bus, and applies <see cref="FromThevenin"/> with the equivalent source <c>c*Un/sqrt(3)</c>.
    /// Converter-interfaced generators (<see cref="Generator.IsConverterInterfaced"/>) follow IEC 60909-0:2016:
    /// they are not reactances to ground but current sources <c>I_skPF = k * I_rG</c>, adding
    /// <c>sum_j |Z_kj| I_skPF,j / |Z_kk|</c> to <c>Ik''</c> (reported in <see cref="Duty.ConverterIkppKa"/>).
    /// Treating them as X''d sources overstated their fault infeed several-fold.
    ///
    /// <para>The symmetrical breaking current <see cref="Duty.BreakingIbKa"/> carries the near-to-generator decay
    /// (R095): each synchronous machine's factor mu (<see cref="BreakingFactorMu"/>, at
    /// <paramref name="breakTimeSeconds"/> as the breaker minimum time delay) enters through the IEC 60909-0:2016
    /// multiple-fed (meshed-network) formula of <see cref="WithNearGeneratorBreaking"/>. Before R095 this reported
    /// <c>Ib = Ik''</c> (mu = 1) for every fault. Far from every generator (all mu = 1) it is still exactly
    /// <c>Ik''</c>. The steady-state current <see cref="Duty.SteadyIkKa"/> stays <c>Ik''</c>: the factor lambda is
    /// read from machine graphs, not a closed form (see the class summary).</para>
    /// </summary>
    public static Duty ThreePhase(
        GridModel model, int faultBusId,
        ShortCircuitObjective objective = ShortCircuitObjective.Maximum,
        double subtransientXdPu = DefaultSubtransientXdPu,
        double f0Hz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz, double breakTimeSeconds = 0.10, double faultDurationSeconds = 1.0,
        PeakFactorMethod peakFactorMethod = PeakFactorMethod.MethodB)
    {
        PositiveNetwork net = BuildPositiveNetwork(model, faultBusId, subtransientXdPu);
        double c = VoltageFactor(net.Un, objective);
        if (peakFactorMethod == PeakFactorMethod.MethodB)   // the default: bit for bit as before N134
            return ThreePhaseDuty(net, c, model.BaseMva, f0Hz, breakTimeSeconds, faultDurationSeconds);
        if (peakFactorMethod != PeakFactorMethod.MethodC)
            throw new ArgumentOutOfRangeException(nameof(peakFactorMethod), peakFactorMethod, "unknown peak factor method");
        double rOverXc = MethodCRoverX(model, faultBusId, subtransientXdPu);
        // N134 rest (CF4): the DC component's R/X by Method C, at the equivalent frequency of f * tb.
        double rOverXdc = MethodCDcRoverX(null, model, faultBusId, subtransientXdPu, DefaultLineZeroSeqRatio, f0Hz, breakTimeSeconds);
        return ThreePhaseDuty(net, c, model.BaseMva, f0Hz, breakTimeSeconds, faultDurationSeconds, methodCRoverX: rOverXc,
            methodCDcRoverX: rOverXdc);
    }

    /// <summary>
    /// N134 (CF3, October 2026; Ruling R1): the ratio <c>fc / f</c> of IEC 60909-0's equivalent-frequency method
    /// (Method C): <c>fc = 20 Hz</c> on a 50 Hz system and <c>24 Hz</c> on a 60 Hz system, 0.4 of nominal in both. As
    /// quoted from IEC 60909-0:2001 subclause 4.3.1.2 c), which is IEC 60909-0:2016 subclause 8.1.2 "Multiple-fed short
    /// circuit" (the 2016 contents list has no 8.1.3; checked [VERIFY] 2026-10-11; the 0.4 itself is also given by Phase to
    /// Phase Vision help and EasyPower; the 2026 Edition 3.0 numbering is unchecked); ETYS Appendix D (docs/reference S242 p.4) records that NESO computes its
    /// initial and break X/R "using the equivalent frequency method (IEC 60909-0 (2001-07) Method C)".
    /// </summary>
    public const double MethodCFrequencyRatio = 0.4;

    /// <summary>
    /// N134: the IEC 60909-0 Method C ratio R/X at <paramref name="faultBusId"/>. The fault network (the same positive
    /// sequence network as <see cref="ThreePhase"/>: series branches, taps, each synchronous machine's X''d plus unit
    /// transformer; converters as open-circuited current sources) is built with every reactance at the equivalent
    /// frequency, <c>X_c = X * fc/f</c>, and resistances unchanged; its driving-point impedance is
    /// <c>Z_c = R_c + j X_c</c>, and <c>R/X = (R_c / X_c) * (fc / f)</c>. The peak factor is then
    /// <c>kappa = 1.02 + 0.98 exp(-3 R/X)</c> with no safety factor and no cap (Method C replaces Method B's 1.15). For
    /// a single series path (one R/X everywhere) it gives exactly the network's own R/X, so it agrees with Method B
    /// without the 1.15; in a meshed network with branches of different R/X it weights them by their frequency
    /// response, which is why NESO uses it (S242).
    /// </summary>
    public static double MethodCRoverX(GridModel model, int faultBusId, double subtransientXdPu = DefaultSubtransientXdPu)
    {
        ArgumentNullException.ThrowIfNull(model);
        GridModel scaled = AtEquivalentFrequency(model, subtransientXdPu);
        Complex zc = BuildPositiveNetwork(scaled, faultBusId, subtransientXdPu * MethodCFrequencyRatio).Zkk;   // every machine now carries its own (scaled) X''d
        if (!(zc.Imaginary > 0.0))
            throw new InvalidOperationException(
                $"IEC 60909 Method C at bus {faultBusId}: the equivalent-frequency impedance has reactance {zc.Imaginary:R} pu; it must be positive.");
        return zc.Real / zc.Imaginary * MethodCFrequencyRatio;
    }

    /// <summary>N134: <paramref name="model"/> with every reactance of the 60909 fault network at the equivalent frequency
    /// (<see cref="MethodCFrequencyRatio"/> times its nominal value): branch series X, each machine's X''d (its own, or
    /// the fallback written onto it) and unit-transformer X. Resistances, taps and phase shifts are unchanged; line
    /// charging and shunts are not part of the 60909 network.</summary>
    private static GridModel AtEquivalentFrequency(GridModel model, double subtransientXdPu)
    {
        double r = MethodCFrequencyRatio;
        return model.CopyWith(
            branches: model.Branches.Select(b => b with { X = b.X * r }).ToList(),
            generators: model.AllGenerators.Select(g => g with
            {
                SubtransientXdPu = g.EffectiveSubtransientXdPu(subtransientXdPu) * r,
                UnitTransformerXPu = Math.Max(0.0, g.UnitTransformerXPu) * r,
            }).ToList());
    }


    /// <summary>The standard line zero-sequence multiple the model-based duties default to (a line with no Z0 of its own
    /// is taken as 3 x Z1, the textbook overhead-line ratio; the literal default of <see cref="SingleLineToGround"/>).</summary>
    public const double DefaultLineZeroSeqRatio = 3.0;

    // ---- N134 rest (CF4, October 2026; Ruling R1): Method C for idc and for the unbalanced duties ----------------------
    //
    // IEC 60909-0's equivalent-frequency method (Method C) finds an R/X by solving the fault network with every reactance
    // at a lower frequency fc and scaling back: R/X = (Rc / Xc) * (fc / f). For the PEAK, fc / f = 0.4 (N134). For the DC
    // COMPONENT at time t, fc / f depends on f * t (the table below), quoted from IEC 60909-0:2001 subclause 4.4 [VERIFY
    // the values: no public source reproduces them; in IEC 60909-0:2016 the DC component is clause 10, which has no
    // numbered table, so the ratios sit in its text; the 2026 Edition 3.0 is unchecked]. For an
    // unbalanced fault the network is that fault's own sequence connection (the loop the duty's current flows round:
    // Z1 + Z2 + Z0, Z1 + Z2, or Z1 + Z2 || Z0), every sequence network at fc, exactly as the balanced Method C uses the
    // positive-sequence network alone. Consistent with GridSim's Method B, which takes the R/X of the same loop.

    /// <summary>N134 rest: fc / f of Method C for the DC component while f * t is below 1 [VERIFY].</summary>
    public const double MethodCDcRatioBelow1 = 0.27;
    /// <summary>N134 rest: fc / f for 1 &lt;= f * t &lt; 2.5 [VERIFY].</summary>
    public const double MethodCDcRatioBelow2p5 = 0.15;
    /// <summary>N134 rest: fc / f for 2.5 &lt;= f * t &lt; 5 [VERIFY].</summary>
    public const double MethodCDcRatioBelow5 = 0.092;
    /// <summary>N134 rest: fc / f for 5 &lt;= f * t &lt; 10 [VERIFY].</summary>
    public const double MethodCDcRatioBelow10 = 0.055;
    /// <summary>N134 rest: the end of the Method C DC table (f * t). Beyond it the standard gives no ratio (the DC component
    /// has decayed to a negligible share); a Method C duty there is refused rather than extrapolated.</summary>
    public const double MethodCDcTableEndFt = 10.0;

    /// <summary>
    /// N134 rest (CF4): the Method C equivalent-frequency ratio fc / f for the DC component at time <c>t</c>, from
    /// <paramref name="fTimesT"/> = f * t (the number of cycles since the fault): 0.27 below 1, 0.15 below 2.5, 0.092
    /// below 5, 0.055 below 10 (IEC 60909-0:2001 4.4, IEC 60909-0:2016 clause 10 [VERIFY the values]). Fails loud for a value that is not finite, is negative or
    /// is at or beyond the end of the table.
    /// </summary>
    public static double MethodCDcFrequencyRatio(double fTimesT)
    {
        if (!double.IsFinite(fTimesT) || fTimesT < 0.0)
            throw new ArgumentOutOfRangeException(nameof(fTimesT), fTimesT, "f * t must be finite and not negative.");
        if (fTimesT < 1.0) return MethodCDcRatioBelow1;
        if (fTimesT < 2.5) return MethodCDcRatioBelow2p5;
        if (fTimesT < 5.0) return MethodCDcRatioBelow5;
        if (fTimesT < MethodCDcTableEndFt) return MethodCDcRatioBelow10;
        throw new ArgumentOutOfRangeException(nameof(fTimesT), fTimesT,
            $"IEC 60909 Method C gives the DC component's equivalent frequency only for f * t below {MethodCDcTableEndFt} "
            + "cycles (the DC component has decayed by then); use a shorter breaker time or Method B.");
    }

    /// <summary>
    /// N134 rest (CF4): the Method C R/X of an UNBALANCED fault's own sequence connection at
    /// <paramref name="faultBusId"/>, at fc = 0.4 f (the peak factor's ratio): every sequence network (positive, negative,
    /// zero) with its reactances at fc, the connection's loop impedance Zc formed from them, and
    /// <c>R/X = (Rc / Xc) * 0.4</c>. A zero-sequence fault on an ungrounded bus has no loop and is refused.
    /// </summary>
    public static double MethodCRoverX(UnbalancedFaultType type, GridModel model, int faultBusId,
        double subtransientXdPu = DefaultSubtransientXdPu, double lineZeroSeqRatio = DefaultLineZeroSeqRatio)
        => EquivalentFrequencyRoverX(EquivalentFrequencyLoop(type, model, faultBusId, subtransientXdPu, lineZeroSeqRatio,
            MethodCFrequencyRatio), MethodCFrequencyRatio, faultBusId);

    /// <summary>N134 rest (CF4): the Method C R/X for the DC component at breaker time <paramref name="breakTimeSeconds"/>:
    /// the loop of <paramref name="type"/> (null: the three-phase fault's positive-sequence network) at
    /// fc / f = <see cref="MethodCDcFrequencyRatio"/>(f * tb).</summary>
    public static double MethodCDcRoverX(UnbalancedFaultType? type, GridModel model, int faultBusId, double subtransientXdPu,
        double lineZeroSeqRatio, double f0Hz, double breakTimeSeconds)
    {
        if (!(f0Hz > 0.0) || !double.IsFinite(f0Hz))
            throw new ArgumentOutOfRangeException(nameof(f0Hz), f0Hz, "The nominal frequency must be positive and finite.");
        if (!(breakTimeSeconds >= 0.0) || !double.IsFinite(breakTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(breakTimeSeconds), breakTimeSeconds, "The breaker time must be finite and not negative.");
        double ratio = MethodCDcFrequencyRatio(f0Hz * breakTimeSeconds);
        return EquivalentFrequencyRoverX(EquivalentFrequencyLoop(type, model, faultBusId, subtransientXdPu, lineZeroSeqRatio, ratio),
            ratio, faultBusId);
    }

    /// <summary>N134 rest: R/X = (Rc / Xc) * ratio of an equivalent-frequency loop, failing loud on a loop without a
    /// positive reactance (no inductive path: the method has nothing to scale).</summary>
    private static double EquivalentFrequencyRoverX(Complex loop, double ratio, int faultBusId)
    {
        if (!(loop.Imaginary > 0.0) || !double.IsFinite(loop.Real))
            throw new InvalidOperationException(
                $"IEC 60909 Method C at bus {faultBusId}: the equivalent-frequency loop impedance is {loop.Real:R} + j{loop.Imaginary:R} pu; "
                + "its reactance must be positive and finite.");
        return loop.Real / loop.Imaginary * ratio;
    }

    /// <summary>N134 rest: the loop impedance of a fault's sequence connection with every reactance at
    /// <paramref name="ratio"/> times nominal (<see cref="AtFrequencyRatio"/>). <paramref name="type"/> null is the
    /// three-phase fault (the positive-sequence driving-point impedance alone).</summary>
    private static Complex EquivalentFrequencyLoop(UnbalancedFaultType? type, GridModel model, int faultBusId,
        double subtransientXdPu, double lineZeroSeqRatio, double ratio)
    {
        ArgumentNullException.ThrowIfNull(model);
        GridModel scaled = AtFrequencyRatio(model, subtransientXdPu, ratio);
        double xdScaled = subtransientXdPu * ratio;   // every machine already carries its own scaled X''d
        Complex z1 = BuildPositiveNetwork(scaled, faultBusId, xdScaled).Zkk;
        if (type is null) return z1;
        (Complex z2, _) = NegativeSequenceThevenin(scaled, faultBusId, xdScaled);
        if (type == UnbalancedFaultType.LineToLine) return z1 + z2;
        (Complex z0, _, bool grounded) = ZeroSequenceThevenin(scaled, faultBusId, lineZeroSeqRatio);
        if (!grounded)
            throw new InvalidOperationException(
                $"IEC 60909 Method C at bus {faultBusId}: the bus is ungrounded in the zero sequence, so a {type} fault has no loop.");
        return type switch
        {
            UnbalancedFaultType.SingleLineToGround => z1 + z2 + z0,
            UnbalancedFaultType.DoubleLineToGround => z1 + z2 * z0 / (z2 + z0),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown fault type."),
        };
    }

    /// <summary>
    /// N134 rest: <paramref name="model"/> with every reactance of the 60909 sequence networks at <paramref name="ratio"/>
    /// times nominal: branch series X and zero-sequence X0, transformer neutral reactances, each machine's X''d (its own
    /// or the fallback written onto it), its X2 when it carries one, and its unit-transformer X. Resistances, taps and
    /// phase shifts are unchanged. (The positive-sequence part is exactly <see cref="AtEquivalentFrequency"/>'s scaling;
    /// that method is kept as it is so the N134 peak R/X stays bit for bit.)
    /// </summary>
    private static GridModel AtFrequencyRatio(GridModel model, double subtransientXdPu, double ratio)
        => model.CopyWith(
            branches: model.Branches.Select(b => b with
            {
                X = b.X * ratio, X0 = b.X0 * ratio,
                FromNeutralXPu = b.FromNeutralXPu * ratio, ToNeutralXPu = b.ToNeutralXPu * ratio,
            }).ToList(),
            generators: model.AllGenerators.Select(g => g with
            {
                SubtransientXdPu = g.EffectiveSubtransientXdPu(subtransientXdPu) * ratio,
                NegativeSeqXPu = g.NegativeSeqXPu > 0.0 ? g.NegativeSeqXPu * ratio : g.NegativeSeqXPu,
                UnitTransformerXPu = Math.Max(0.0, g.UnitTransformerXPu) * ratio,
                ZeroSequenceZPu = g.ZeroSequenceZPu is MachineZeroSequence z0 ? z0 with { XPu = z0.XPu * ratio } : null,
            }).ToList(),
            zeroSequenceMutuals: model.ZeroSequenceMutuals.Count == 0 ? null : model.ZeroSequenceMutuals.Select(mu => mu with { XPu = mu.XPu * ratio }).ToList());

    /// <summary>N134 rest: the DC component <c>idc = sqrt(2) Ik'' exp(-2 pi f tb R/X)</c>, the expression of
    /// <see cref="Assemble"/>, on a given R/X.</summary>
    private static double DcComponentKa(double ikppKa, double f0Hz, double breakTimeSeconds, double rOverX)
        => Math.Sqrt(2.0) * ikppKa * DetMath.Exp(-2.0 * Math.PI * f0Hz * breakTimeSeconds * rOverX);

    /// <summary>N134 rest (CF4): Method C on an unbalanced synchronous duty: the peak factor from the connection's R/X at
    /// fc = 0.4 f (no safety factor, no cap) and the DC component from its R/X at the f * tb equivalent frequency. Ik'',
    /// Ib, Ith and m are left as they are; the converter share is added afterwards by the caller, as under Method B.</summary>
    private static Duty WithMethodC(Duty sync, UnbalancedFaultType type, GridModel model, int faultBusId, double subtransientXdPu,
        double lineZeroSeqRatio, double f0Hz, double breakTimeSeconds)
    {
        double rxPeak = MethodCRoverX(type, model, faultBusId, subtransientXdPu, lineZeroSeqRatio);
        double rxDc = MethodCDcRoverX(type, model, faultBusId, subtransientXdPu, lineZeroSeqRatio, f0Hz, breakTimeSeconds);
        double kappaC = PeakFactor(rxPeak);
        return sync with
        {
            Kappa = kappaC, PeakIpKa = kappaC * Math.Sqrt(2.0) * sync.IkppKa, MeshedPeakFactor = false,
            PeakMethod = PeakFactorMethod.MethodC, PeakRoverX = rxPeak,
            DcComponentKa = DcComponentKa(sync.IkppKa, f0Hz, breakTimeSeconds, rxDc), DcRoverX = rxDc,
        };
    }

    /// <summary>N134 rest: refuse a peak-factor method value outside the enum (the same refusal as <see cref="ThreePhase"/>).</summary>
    private static void RequireKnownMethod(PeakFactorMethod method)
    {
        if (method != PeakFactorMethod.MethodB && method != PeakFactorMethod.MethodC)
            throw new ArgumentOutOfRangeException(nameof(method), method, "unknown peak factor method");
    }

    /// <summary>The three-phase duty on an assembled positive-sequence network: the equivalent-source duty, the
    /// near-generator breaking current (R095) and the converter infeed, in that order. Shared by
    /// <see cref="ThreePhase"/> and <see cref="PartialThreePhase"/> (N049), so the two report the same breaking
    /// current bit for bit.</summary>
    private static Duty ThreePhaseDuty(PositiveNetwork net, double c, double baseMva, double f0Hz,
        double breakTimeSeconds, double faultDurationSeconds, double methodCRoverX = double.NaN,
        double methodCDcRoverX = double.NaN)
    {
        Duty sync = FromThevenin(c, net.Un, net.Zkk.Real, net.Zkk.Imaginary, baseMva, f0Hz, breakTimeSeconds, faultDurationSeconds,
            net.Meshed);   // D-DOC-098: the meshed-network kappa where the fault network is meshed
        // N134: under Method C the peak factor is kappa(R/X_C), no safety factor, no cap; ip = kappa sqrt(2) Ik'' with the
        // same arithmetic as Assemble. Only the peak moves: Ik'', Ib, Ith (m keeps kappa(R/X)) and idc are unchanged.
        // The converter share is added below exactly as under Method B.
        if (!double.IsNaN(methodCRoverX))
        {
            double kappaC = PeakFactor(methodCRoverX);
            sync = sync with
            {
                Kappa = kappaC, PeakIpKa = kappaC * Math.Sqrt(2.0) * sync.IkppKa, MeshedPeakFactor = false,
                PeakMethod = PeakFactorMethod.MethodC, PeakRoverX = methodCRoverX,
            };
            // N134 rest (CF4): the DC component by Method C, with Assemble's expression on the Method C R/X.
            if (!double.IsNaN(methodCDcRoverX))
                sync = sync with { DcComponentKa = DcComponentKa(sync.IkppKa, f0Hz, breakTimeSeconds, methodCDcRoverX), DcRoverX = methodCDcRoverX };
        }
        sync = WithNearGeneratorBreaking(sync, net, c, baseMva, breakTimeSeconds);   // R095: mu folded in
        sync = sync with { SteadyBasis = SteadyBasisOf(net, c, baseMva) };          // D-DOC-097: what Ik is
        return WithConverterInfeed(sync, net.UpliftPu / net.Zkk.Magnitude, net.Un, baseMva);
    }

    /// <summary>
    /// D-DOC-097 (October 2026): which IEC 60909-0 rule the steady-state current of a three-phase fault on
    /// <paramref name="net"/> follows (see <see cref="SteadyStateBasis"/>). Far from every generator when each synchronous
    /// machine's partial initial current is at most twice its rated current (the same criterion as mu = 1); otherwise
    /// several feeding synchronous sources make it the meshed (multiple-fed) rule Ik = Ik''M, and a single one the
    /// single-fed rule Ik = lambda I_rG, which this model cannot evaluate (no x_dsat). The numbers are not changed here.
    /// </summary>
    private static SteadyStateBasis SteadyBasisOf(PositiveNetwork net, double c, double baseMva)
    {
        bool near = false;
        int feeding = 0;
        foreach (SyncPartial sp in SynchronousPartials(net, c, baseMva, minTimeDelaySeconds: 0.10))
        {
            if (sp.IPu > 0.0) feeding++;
            if (sp.RatedKa > 0.0 && sp.IKa / sp.RatedKa > 2.0) near = true;
        }
        if (!near) return SteadyStateBasis.FarFromGenerator;
        return feeding >= 2 ? SteadyStateBasis.MeshedMultipleFed : SteadyStateBasis.SingleFedUpperBound;
    }

    /// <summary>One source's contribution to a three-phase fault: its partial initial symmetrical current (kA and
    /// pu), its rated current, the near-generator factor <c>mu</c> for its own contribution ratio, and the partial
    /// symmetrical breaking current <c>mu * Ik''_g</c>. A converter-interfaced source
    /// (<see cref="IsConverter"/>) is a current source: its share is <c>I_skPF * |Z_kj| / |Z_kk|</c> with
    /// <c>mu = 1</c>.</summary>
    public sealed record MachinePartialCurrent(
        int Bus, string Name, double IkppKa, double IkppPu, double RatedCurrentKa, double Mu, double BreakingIbKa,
        bool IsConverter = false);

    /// <summary>A three-phase fault decomposed into the per-machine partial currents that feed it, with the
    /// totals. Each machine's own <see cref="MachinePartialCurrent.BreakingIbKa"/> is its single-fed
    /// <c>mu_i * Ik''_i</c>; the fleet <see cref="TotalBreakingIbKa"/> is the meshed-network (multiple-fed) breaking
    /// current that <see cref="ThreePhase"/> reports, bit for bit (N049), so it is generally NOT the sum of the
    /// per-machine values.</summary>
    public sealed record FaultContribution(
        double TotalIkppKa, double TotalIkppPu, double NominalKvLL,
        IReadOnlyList<MachinePartialCurrent> Machines, double TotalBreakingIbKa);

    /// <summary>
    /// Decompose the balanced three-phase fault at <paramref name="faultBusId"/> into the partial short-circuit
    /// currents each generator feeds into it, and assemble the fleet breaking current. Using the same
    /// equivalent-source model as <see cref="ThreePhase"/>, the source holds the fault bus at
    /// <c>V_k = c</c> (pu), the during-fault voltage at bus <c>i</c> is <c>V_i = c * Z_ik / Z_kk</c>, and a
    /// generator's partial current is the current its subtransient reactance carries to ground,
    /// <c>I_g = V_g / (j X''d_g)</c> (pu). Each machine's contribution ratio <c>|I_g| / Ir_g</c> (both in
    /// system pu, so base-independent) drives its near-generator factor <see cref="BreakingFactorMu"/>, and each
    /// machine reports its single-fed breaking current <c>mu_i * |I_g|</c>. For a lossless network every partial
    /// current is in phase, so the partial magnitudes sum to the total <c>Ik''</c>.
    /// <para>N049 (October 2026): the fleet <b>breaking</b> current <see cref="FaultContribution.TotalBreakingIbKa"/> is
    /// the IEC 60909-0:2016 meshed-network (multiple-fed) formula of <see cref="WithNearGeneratorBreaking"/>, exactly
    /// the <see cref="Duty.BreakingIbKa"/> that <see cref="ThreePhase"/> reports with <c>breakTimeSeconds</c> equal to
    /// <paramref name="minTimeDelaySeconds"/> (the same code computes both). It used to be the sum of the per-machine
    /// <c>mu_i * |I_g|</c> (the single-fed form, right only when each machine feeds the fault on its own), which
    /// disagreed with <see cref="ThreePhase"/> and, in a lossy network, could even exceed <c>Ik''</c> (WSCC-9 bus 5:
    /// 1.4690796589338537 kA against Ik'' 1.4683989584292976 kA with every mu = 1).</para>
    /// </summary>
    public static FaultContribution PartialThreePhase(
        GridModel model, int faultBusId,
        ShortCircuitObjective objective = ShortCircuitObjective.Maximum,
        double subtransientXdPu = DefaultSubtransientXdPu, double minTimeDelaySeconds = 0.10)
    {
        PositiveNetwork net = BuildPositiveNetwork(model, faultBusId, subtransientXdPu);
        double un = net.Un;
        double baseMva = model.BaseMva;
        double c = VoltageFactor(un, objective);
        double iBaseKa = baseMva / (Math.Sqrt(3.0) * un);

        Complex[] zColumn = net.ZColumn;
        Complex zkk = net.Zkk;
        Complex iFault = c / zkk;                    // pu phasor into the fault (synchronous sources)
        double totalIkppPu = iFault.Magnitude;

        var machines = new List<MachinePartialCurrent>(net.Machines.Count + net.Converters.Count);
        foreach (SyncPartial sp in SynchronousPartials(net, c, baseMva, minTimeDelaySeconds))
            machines.Add(new MachinePartialCurrent(sp.Bus, sp.Name, sp.IKa, sp.IPu, sp.RatedKa, sp.Mu, sp.IbKa));

        // Converter infeeds: each current source I_skPF,j reaches the fault as |Z_kj|/|Z_kk| of itself
        // (IEC 60909-0:2016). A controlled current does not decay like a machine's AC envelope (mu = 1),
        // so its breaking current is its initial current.
        double zkkMag = zkk.Magnitude;
        foreach (ConverterInfeed cv in net.Converters)
        {
            double iPu = cv.CurrentPu * cv.Zkj.Magnitude / zkkMag;
            double iKa = iPu * iBaseKa;
            double ratedKa = cv.MBase / baseMva * iBaseKa;
            machines.Add(new MachinePartialCurrent(cv.Bus, cv.Name, iKa, iPu, ratedKa, 1.0, iKa, IsConverter: true));
            totalIkppPu += iPu;
        }

        // N049: the fleet breaking current is ThreePhase's, computed by the same code on the same network (the
        // nominal frequency and fault duration only feed the peak, DC and thermal terms, never Ib).
        double totalIbKa = ThreePhaseDuty(net, c, baseMva, global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz,
            minTimeDelaySeconds, faultDurationSeconds: 1.0).BreakingIbKa;
        return new FaultContribution(totalIkppPu * iBaseKa, totalIkppPu, un, machines, totalIbKa);
    }

    /// <summary>One synchronous machine's partial current for a three-phase fault (R095): its partial initial current
    /// (pu and kA), its rated current (kA), its near-generator factor mu, its single-fed breaking current mu * Ik''_G
    /// (kA), and <see cref="VoltageRatio"/>, the magnitude of the initial voltage difference across its subtransient
    /// reactance relative to the equivalent source, <c>|dU''_G| / (c Un / sqrt 3) = |V_G| / c</c> (pu), which the
    /// meshed-network breaking current weights its decay by.</summary>
    private readonly record struct SyncPartial(
        int Bus, string Name, double IPu, double IKa, double RatedKa, double Mu, double IbKa, double VoltageRatio);

    /// <summary>
    /// The per-machine partial currents of a three-phase fault on the equivalent-source network (shared by
    /// <see cref="PartialThreePhase"/> and the near-generator breaking current of <see cref="ThreePhase"/>, so the two
    /// cannot drift). The source holds the fault bus at <c>V_k = c</c>; the during-fault voltage at machine bus g is
    /// <c>V_g = c * Z_gk / Z_kk</c> and its partial current is <c>I_g = V_g / (j X''d_g)</c>. The contribution ratio
    /// <c>|I_g| / I_rG</c> (both in system pu) sets mu at the breaker minimum time delay. Machines in network order.
    /// </summary>
    private static List<SyncPartial> SynchronousPartials(PositiveNetwork net, double c, double baseMva, double minTimeDelaySeconds)
    {
        double iBaseKa = baseMva / (Math.Sqrt(3.0) * net.Un);
        Complex[] zColumn = net.ZColumn;
        Complex zkk = net.Zkk;
        var partials = new List<SyncPartial>(net.Machines.Count);
        foreach ((int bus, string name, Complex yShunt, double mBase) in net.Machines)
        {
            int gi = net.Index[bus];
            Complex vG = c * zColumn[gi] / zkk;       // during-fault voltage at the machine bus (pu)
            Complex iG = yShunt * vG;                 // current its reactance carries to ground (pu)
            double iGpu = iG.Magnitude;
            double iGka = iGpu * iBaseKa;
            double ratedPu = mBase / baseMva;         // rated current in system pu (= S_r/base at 1 pu volts)
            double ratio = ratedPu > 0.0 ? iGpu / ratedPu : 0.0;
            double mu = BreakingFactorMu(ratio, minTimeDelaySeconds);
            double ibKa = mu * iGka;
            double ratedKa = ratedPu * iBaseKa;
            partials.Add(new SyncPartial(bus, name, iGpu, iGka, ratedKa, mu, ibKa, vG.Magnitude / c));
        }
        return partials;
    }

    /// <summary>
    /// The near-to-generator symmetrical breaking current of a balanced fault in a MESHED network (R095: the mu of
    /// <see cref="PartialThreePhase"/> folded into <see cref="ThreePhase"/>), IEC 60909-0:2016 subclause 9.1.7
    /// "Symmetrical breaking current in case of multiple-fed short circuits" (clause 9 is breaking current; the 1.95.094
    /// citation of 10.1.6 was wrong, clause 10 being the DC component; 9.1.7 is read from the 2016 contents list, whose
    /// text layer is garbled at 9.1.5 to 9.1.7 but whose labels run 9.1.4 to 9.1.7 against the four titles in order,
    /// [VERIFY] 2026-10-11, N066 / N236; ETYS Appendix D (S242, S245) cites only IEC 60909-0 (2001-07) Method C for X/R],
    /// breaking current in multiple-fed short circuits (the meshed-network formula of the 2001 edition):
    /// <code>
    ///   Ib = Ik'' - sum_i ( dU''_Gi / (c Un / sqrt 3) ) (1 - mu_i) I''_kGi
    /// </code>
    /// with <c>I''_kGi</c> machine i's partial initial current, mu_i its factor at the breaker minimum time delay, and
    /// <c>dU''_Gi = j X''_d,i I''_kGi</c> the initial voltage difference at its connection point. (The standard's
    /// asynchronous-motor term, <c>(1 - mu_j q_j)</c>, has no counterpart: the model carries no motor data.) A
    /// machine AT the fault bus has <c>dU'' = c Un / sqrt 3</c>, weight 1, so a single machine at its own terminals
    /// gives exactly <c>Ib = mu Ik''</c>, the single-fed form (2016 subclause 9.1.1). A remote machine's decay counts
    /// only in proportion to its terminal voltage dip, so Ib here is never below the multiple-single-fed sum of the
    /// per-machine <c>mu_i * Ik''_i</c> (listed by <see cref="PartialThreePhase"/>) for in-phase partials, the
    /// conservative side for a breaker rating. When every
    /// mu is 1 (every machine far from the fault, <c>Ik''_G / I_rG &lt;= 2</c>) the deduction is exactly zero and
    /// <paramref name="sync"/> is returned unchanged, so far-from-generator duties are bit-for-bit as before.
    /// Converter infeeds are added afterwards with mu = 1 (<see cref="WithConverterInfeed"/>).
    /// </summary>
    private static Duty WithNearGeneratorBreaking(Duty sync, PositiveNetwork net, double c, double baseMva, double minTimeDelaySeconds)
    {
        double deductionKa = 0.0;
        foreach (SyncPartial sp in SynchronousPartials(net, c, baseMva, minTimeDelaySeconds))
            deductionKa += sp.VoltageRatio * (1.0 - sp.Mu) * sp.IKa;
        if (deductionKa == 0.0) return sync;   // every mu = 1: far from generator, Ib = Ik''

        double ibKa = sync.IkppKa - deductionKa;
        // Fail loud: mu >= 0.56 and every weight is a voltage ratio, so the deduction is well inside Ik''. A breaking
        // current outside (0, Ik''] means the partials are inconsistent with the driving-point current.
        if (!(ibKa > 0.0) || !(ibKa <= sync.IkppKa) || !double.IsFinite(deductionKa))
            throw new InvalidOperationException(
                $"IEC 60909 breaking current {ibKa} kA is outside (0, Ik'' = {sync.IkppKa} kA] (deduction {deductionKa} kA); "
                + "the machine partial currents are inconsistent with the fault current.");
        return sync with { BreakingIbKa = ibKa };
    }

    /// <summary>One branch's through-current for a three-phase fault at the study bus: the from- and to-end
    /// current magnitudes (kA and pu). A protective relay on that branch measures its own end's current, so
    /// this is the fault current an overcurrent element on the branch sees - the input a coordination study
    /// (<see cref="Coordination.OvercurrentCoordination"/>) needs from the network.</summary>
    public sealed record BranchFaultCurrent(
        string Name, int From, int To, double FromEndKa, double ToEndKa, double FromEndPu, double ToEndPu);

    /// <summary>
    /// The per-branch through-currents for a balanced three-phase fault at <paramref name="faultBusId"/> -
    /// the current each branch carries when the fault is applied, and hence the current a relay on that branch
    /// sees. It reuses the same equivalent-source network and single Zbus column as <see cref="ThreePhase"/>:
    /// the during-fault bus voltages are <c>V_i = c * Z_ik / Z_kk</c> (pu), and each branch current follows
    /// from the off-nominal-tap series model (<see cref="AdmittanceStamp"/> with the line charging left out, as in
    /// the 60909 network), so a transformer
    /// with a tap is handled exactly. Feed a relay chain's currents to <see cref="Coordination.OvercurrentCoordination.GradeChain"/>
    /// to grade a scheme against a real network fault.
    /// </summary>
    public static IReadOnlyList<BranchFaultCurrent> BranchFaultCurrents(
        GridModel model, int faultBusId, double subtransientXdPu = DefaultSubtransientXdPu)
    {
        PositiveNetwork net = BuildPositiveNetwork(model, faultBusId, subtransientXdPu);
        Dictionary<int, int> index = net.Index;
        double un = net.Un;
        double iBaseKa = model.BaseMva / (Math.Sqrt(3.0) * un);
        Complex[] zColumn = net.ZColumn;
        Complex zkk = net.Zkk;
        double c = VoltageFactor(un, ShortCircuitObjective.Maximum);   // the maximum-current source, as ThreePhase
        Complex[] vConv = net.ConverterVoltages();                      // converter infeeds' share (zero if none)

        var currents = new List<BranchFaultCurrent>();
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            if (!index.TryGetValue(br.From, out int f) || !index.TryGetValue(br.To, out int t)) continue;

            Complex vf = c * zColumn[f] / zkk + vConv[f];
            Complex vt = c * zColumn[t] / zkk + vConv[t];

            // The branch's series-plus-tap admittance from the one shared stamp (R285, October 2026): this used to
            // re-implement the pi-model and divide by TapRatio^2 with no "ratio <= 0 means 1" guard, so a branch
            // stored with ratio 0 gave NaN/Inf currents. Line charging is left OUT (b = 0), matching the IEC 60909
            // positive-sequence network these voltages were solved on (R284).
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, 0.0, br.TapRatio, br.PhaseShiftDeg);
            Complex iFrom = s.Yff * vf + s.Yft * vt;
            Complex iTo = s.Ytf * vf + s.Ytt * vt;

            string name = string.IsNullOrEmpty(br.Name) ? $"branch {br.From}-{br.To}" : br.Name!;
            currents.Add(new BranchFaultCurrent(name, br.From, br.To,
                iFrom.Magnitude * iBaseKa, iTo.Magnitude * iBaseKa, iFrom.Magnitude, iTo.Magnitude));
        }
        return currents;
    }

    /// <summary>
    /// The positive-sequence Thevenin impedance <c>Z1</c> (pu) at <paramref name="faultBusId"/> and that
    /// bus's nominal line-to-line voltage. Assembles the positive-sequence fault network - the network
    /// branches' series impedances and taps (line charging and bus shunts neglected, as IEC 60909-0 directs for
    /// the positive and negative sequence; R284) plus each synchronous generator as its subtransient reactance to
    /// ground, with <b>loads neglected</b> (the 60909 convention) - and solves one Zbus column for the driving-point
    /// impedance the equivalent source sees. Converter-interfaced plant is open here (it is a current source,
    /// not a reactance); its infeed is added on top by the duty entry points.
    /// </summary>
    public static (Complex Z1Pu, double NominalKvLL) PositiveSequenceThevenin(
        GridModel model, int faultBusId, double subtransientXdPu = DefaultSubtransientXdPu)
    {
        PositiveNetwork net = BuildPositiveNetwork(model, faultBusId, subtransientXdPu);
        return (net.Zkk, net.Un);
    }

    /// <summary>
    /// The negative-sequence Thevenin impedance <c>Z2</c> (pu) at <paramref name="faultBusId"/>. It is the
    /// same passive network as the positive sequence (static plant has <c>Z2 = Z1</c>), but each generator
    /// contributes its negative-sequence reactance <c>X2</c> (<see cref="Generator.EffectiveNegativeSeqXPu"/>)
    /// instead of <c>X''d</c>. A machine carrying no separate <c>X2</c> defaults to its <c>X''d</c>, so
    /// <c>Z2 = Z1</c> exactly - the network solves identically until real negative-sequence data is supplied.
    /// </summary>
    public static (Complex Z2Pu, double NominalKvLL) NegativeSequenceThevenin(
        GridModel model, int faultBusId, double subtransientXdPu = DefaultSubtransientXdPu)
        => SequenceThevenin(model, faultBusId, g => g.FaultSourceNegativeSeqReactancePu(subtransientXdPu));

    /// <summary>
    /// The zero-sequence Thevenin impedance <c>Z0</c> (pu) at <paramref name="faultBusId"/>, its nominal
    /// line-to-line voltage, and whether the fault bus is <b>grounded</b> in the zero sequence at all. The
    /// zero-sequence network is assembled from the transformer winding connections
    /// (<see cref="WindingConnection"/>) and the branch zero-sequence series impedances:
    /// <list type="bullet">
    ///   <item>a <b>line</b> (no winding data) is a series <c>Z0 = R0 + jX0</c> between its ends; where a line
    ///   carries no zero-sequence impedance the standard multiple <paramref name="lineZeroSeqRatio"/> of its
    ///   positive-sequence impedance is used (default 3, the textbook overhead-line ratio);</item>
    ///   <item>a transformer with <b>both windings grounded-wye</b> is a series <c>Z0T</c> (plus <c>3Xn</c> at
    ///   each earthed neutral) between its buses;</item>
    ///   <item>a transformer with <b>one delta and one grounded-wye</b> winding is a shunt <c>Z0T + 3Xn</c>
    ///   from the grounded-wye bus to earth, with the delta side isolated - this is what injects a ground
    ///   reference into the zero sequence;</item>
    ///   <item>every other pairing (a delta or ungrounded winding with no grounded-wye return) blocks the
    ///   zero sequence and contributes nothing.</item>
    /// </list>
    /// Generators contribute no direct zero-sequence path (a machine reaches the transmission zero-sequence
    /// network only through a grounded-wye transformer neutral; unit step-ups are typically delta on the
    /// machine side). If the fault bus's zero-sequence component reaches no earthed neutral it is ungrounded:
    /// <c>Grounded</c> is false and <c>Z0</c> is <see cref="double.PositiveInfinity"/> (no single-line-to-earth
    /// current can flow), returned rather than a singular solve.
    /// </summary>
    public static (Complex Z0Pu, double NominalKvLL, bool Grounded) ZeroSequenceThevenin(
        GridModel model, int faultBusId, double lineZeroSeqRatio = 3.0)
    {
        int n = model.BusCount;

        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        if (!index.TryGetValue(faultBusId, out int k))
            throw new ArgumentException($"fault bus {faultBusId} is not in the model", nameof(faultBusId));

        double un = model.Buses[k].BaseKv;
        if (un <= 0.0)
            throw new InvalidOperationException($"fault bus {faultBusId} has no base kV; IEC 60909 needs a nominal voltage.");

        // F21 (October 2026): the zero-sequence stamps are built by BuildZeroSequenceNetwork, shared with the
        // operating-point earth-fault study (FaultAnalysis.Unbalanced). It is this method's former loop moved
        // verbatim: the same stamps in the same order, so this driving-point impedance is unchanged to the bit.
        ZeroSequenceNetwork zs = BuildZeroSequenceNetwork(model, index, lineZeroSeqRatio);
        List<(int Row, int Col, Complex V)> trip = zs.Trip;
        bool[] hasGround = zs.HasGround;
        List<int>[] adj = zs.Adjacency;

        // Does the fault bus's zero-sequence component reach an earthed neutral? BFS over the series edges.
        var comp = new List<int> { k };
        var seen = new bool[n];
        seen[k] = true;
        var queue = new Queue<int>();
        queue.Enqueue(k);
        bool grounded = hasGround[k];
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int v in adj[u])
                if (!seen[v]) { seen[v] = true; comp.Add(v); grounded |= hasGround[v]; queue.Enqueue(v); }
        }
        if (!grounded)
            return (new Complex(double.PositiveInfinity, double.PositiveInfinity), un, false);

        // Solve the driving-point impedance on the (self-contained) grounded component: Y_sub x = e_k.
        int m = comp.Count;
        var local = new Dictionary<int, int>(m);
        for (int a = 0; a < m; a++) local[comp[a]] = a;
        var subTrip = new List<(int Row, int Col, Complex V)>(trip.Count);
        foreach ((int r, int c, Complex v) in trip)
            if (local.TryGetValue(r, out int lr) && local.TryGetValue(c, out int lc)) subTrip.Add((lr, lc, v));
        var e = new Complex[m];
        e[local[k]] = Complex.One;
        Complex[] zColumn = FaultNetworkSystem.FromTriplets(m, subTrip).Solve(e);
        return (zColumn[local[k]], un, true);
    }

    /// <summary>F21: how one branch appears in the zero-sequence network.</summary>
    internal enum ZeroSeqBranchKind
    {
        /// <summary>Out of service, an end bus missing, a zero impedance, or a winding pairing that blocks it.</summary>
        None,
        /// <summary>A series admittance between its ends (a line, or a grounded-wye / grounded-wye transformer).</summary>
        Series,
        /// <summary>A shunt to earth at the from bus (grounded-wye from side, delta to side).</summary>
        ShuntFrom,
        /// <summary>A shunt to earth at the to bus (delta from side, grounded-wye to side).</summary>
        ShuntTo,
        /// <summary>A line mutually coupled to others in the zero sequence: its current is the sum over its group's lines
        /// of the inverse impedance matrix row times each line's voltage difference (<see cref="ZeroSequenceNetwork.Coupled"/>).</summary>
        Coupled,
    }

    /// <summary>F21: the zero-sequence network: the admittance stamps in order, which buses have an earth path, the
    /// series adjacency, and each branch's own element (kind and admittance) for its zero-sequence current.</summary>
    internal sealed record ZeroSequenceNetwork(
        List<(int Row, int Col, Complex V)> Trip, bool[] HasGround, List<int>[] Adjacency,
        ZeroSeqBranchKind[] Kind, Complex[] Admittance)
    {
        /// <summary>For each <see cref="ZeroSeqBranchKind.Coupled"/> branch: its group's branches and its row of the
        /// group's inverse impedance matrix (empty when the model has no mutual coupling).</summary>
        public Dictionary<int, (int[] Branches, Complex[] Row)> Coupled { get; init; } = new();
    }

    /// <summary>
    /// F21 (October 2026): build the zero-sequence network of <paramref name="model"/> (bus index map
    /// <paramref name="index"/>) by the winding rules documented on <see cref="ZeroSequenceThevenin"/>. This is that
    /// method's former loop, moved here unchanged so the operating-point earth-fault study can use the same network.
    /// </summary>
    internal static ZeroSequenceNetwork BuildZeroSequenceNetwork(GridModel model, Dictionary<int, int> index, double lineZeroSeqRatio)
    {
        int n = model.BusCount;
        var trip = new List<(int Row, int Col, Complex V)>();   // zero-sequence admittance stamps, in order
        var hasGround = new bool[n];
        var adj = new List<int>[n];               // series-edge adjacency (buses joined by a zero-seq path)
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        var kind = new ZeroSeqBranchKind[model.Branches.Count];
        var adm = new Complex[model.Branches.Count];

        void AddSeries(int a, int b, Complex z)
        {
            Complex ya = Complex.One / z;
            trip.Add((a, a, ya)); trip.Add((b, b, ya)); trip.Add((a, b, -ya)); trip.Add((b, a, -ya));
            adj[a].Add(b); adj[b].Add(a);
        }

        // Mutually coupled lines (PSS/E sequence data): grouped, and stamped after the loop. A model with none takes the
        // loop exactly as before.
        List<List<int>>? groups = model.ZeroSequenceMutuals.Count > 0 ? MutualGroups(model, index) : null;
        var coupledLine = new HashSet<int>();
        if (groups is not null) foreach (List<int> gp in groups) foreach (int b in gp) coupledLine.Add(b);

        for (int l = 0; l < model.Branches.Count; l++)
        {
            Branch br = model.Branches[l];
            if (!br.InService) continue;
            if (!index.TryGetValue(br.From, out int f) || !index.TryGetValue(br.To, out int t)) continue;
            if (coupledLine.Contains(l)) continue;   // stamped with its group below

            bool hasZ0 = br.R0 != 0.0 || br.X0 != 0.0;
            if (br.FromWinding is null || br.ToWinding is null)
            {
                // A line: series zero-sequence impedance between its ends (supplied Z0, else the standard ratio).
                Complex z0 = hasZ0 ? new Complex(br.R0, br.X0) : lineZeroSeqRatio * new Complex(br.R, br.X);
                if (z0 != Complex.Zero) { AddSeries(f, t, z0); kind[l] = ZeroSeqBranchKind.Series; adm[l] = Complex.One / z0; }
                continue;
            }

            // A transformer: its zero-sequence leakage impedance (supplied Z0, else Z0T ~ Z1).
            Complex z0t = hasZ0 ? new Complex(br.R0, br.X0) : new Complex(br.R, br.X);
            Complex znF = new(0.0, 3.0 * br.FromNeutralXPu);
            Complex znT = new(0.0, 3.0 * br.ToNeutralXPu);
            WindingConnection wf = br.FromWinding.Value, wt = br.ToWinding.Value;
            // GR-631: a zero-sequence path through a transformer with zero impedance (Z0 and the neutral reactance both 0)
            // used to divide by zero and stamp an infinite admittance into the network; refused, naming the branch.
            Complex PathZ(Complex z) => z != Complex.Zero ? z
                : throw new InvalidDataException(
                    $"Zero-sequence network: transformer {br.From}-{br.To}{(br.Name is null ? "" : $" ('{br.Name}')")} has a zero-impedance " +
                    "zero-sequence path; give it a zero-sequence or leakage impedance.");

            if (wf == WindingConnection.WyeGrounded && wt == WindingConnection.WyeGrounded)
            {
                Complex zs = PathZ(z0t + znF + znT);
                AddSeries(f, t, zs);          // both grounded: a through series path
                kind[l] = ZeroSeqBranchKind.Series; adm[l] = Complex.One / zs;
            }
            else if (wf == WindingConnection.WyeGrounded && wt == WindingConnection.Delta)
            {
                Complex zs = PathZ(z0t + znF);
                trip.Add((f, f, Complex.One / zs));   // grounded-wye bus shunts to earth; delta side open
                hasGround[f] = true;
                kind[l] = ZeroSeqBranchKind.ShuntFrom; adm[l] = Complex.One / zs;
            }
            else if (wf == WindingConnection.Delta && wt == WindingConnection.WyeGrounded)
            {
                Complex zs = PathZ(z0t + znT);
                trip.Add((t, t, Complex.One / zs));
                hasGround[t] = true;
                kind[l] = ZeroSeqBranchKind.ShuntTo; adm[l] = Complex.One / zs;
            }
            // any other pairing blocks the zero sequence: no contribution.
        }

        // Each group of mutually coupled lines: the branch currents are I = Z^-1 dV with Z the group's impedance matrix
        // (each line's own Z0 on the diagonal, the mutual impedances off it), so each pair (a, b) stamps Y_ab into the
        // rows and columns of a's and b's end buses.
        var coupledRows = new Dictionary<int, (int[] Branches, Complex[] Row)>();
        if (groups is not null)
            foreach (List<int> gp in groups)
            {
                int m = gp.Count;
                var z = new Complex[m, m];
                for (int a = 0; a < m; a++)
                {
                    Branch br = model.Branches[gp[a]];
                    bool hasZ0 = br.R0 != 0.0 || br.X0 != 0.0;
                    z[a, a] = hasZ0 ? new Complex(br.R0, br.X0) : lineZeroSeqRatio * new Complex(br.R, br.X);
                }
                foreach (ZeroSequenceMutual mu in model.ZeroSequenceMutuals)
                {
                    int a = gp.IndexOf(mu.BranchA), b = gp.IndexOf(mu.BranchB);
                    if (a < 0 || b < 0) continue;
                    z[a, b] += new Complex(mu.RPu, mu.XPu);
                    z[b, a] += new Complex(mu.RPu, mu.XPu);
                }
                Complex[,] y = InvertComplex(z, gp, model);
                int[] branchesOf = gp.ToArray();
                for (int a = 0; a < m; a++)
                {
                    Branch ba = model.Branches[gp[a]];
                    int fa = index[ba.From], ta = index[ba.To];
                    var row = new Complex[m];
                    for (int b = 0; b < m; b++)
                    {
                        Branch bb = model.Branches[gp[b]];
                        int fb = index[bb.From], tb = index[bb.To];
                        Complex v = y[a, b];
                        row[b] = v;
                        trip.Add((fa, fb, v)); trip.Add((fa, tb, -v)); trip.Add((ta, fb, -v)); trip.Add((ta, tb, v));
                    }
                    adj[fa].Add(ta); adj[ta].Add(fa);
                    kind[gp[a]] = ZeroSeqBranchKind.Coupled;
                    coupledRows[gp[a]] = (branchesOf, row);
                }
            }

        // Machines with a zero-sequence impedance of their own (a grounded neutral): a shunt to earth at the bus, on the
        // system base.
        foreach (Generator g in model.Generators)
        {
            if (g.ZeroSequenceZPu is not MachineZeroSequence z0g || g.PowerFlowOnly) continue;
            if (!index.TryGetValue(g.Bus, out int b)) continue;
            double scale = model.BaseMva / (g.MBase <= 0 ? model.BaseMva : g.MBase);
            Complex zs = new Complex(z0g.RPu, z0g.XPu) * scale;
            trip.Add((b, b, Complex.One / zs));
            hasGround[b] = true;
        }
        return new ZeroSequenceNetwork(trip, hasGround, adj, kind, adm) { Coupled = coupledRows };
    }

    /// <summary>The groups of mutually coupled lines (connected by <see cref="GridModel.ZeroSequenceMutuals"/>), each in
    /// ascending branch order, groups in order of their first branch. A coupling naming a branch that is not that line
    /// (index out of range, other end buses, a transformer) is refused; one with an out-of-service line or an end bus
    /// outside <paramref name="index"/> is left out.</summary>
    private static List<List<int>> MutualGroups(GridModel model, Dictionary<int, int> index)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        bool Live(int b) => model.Branches[b].InService && index.ContainsKey(model.Branches[b].From) && index.ContainsKey(model.Branches[b].To);
        foreach (ZeroSequenceMutual mu in model.ZeroSequenceMutuals)
        {
            foreach ((int b, int f, int t) in new[] { (mu.BranchA, mu.FromA, mu.ToA), (mu.BranchB, mu.FromB, mu.ToB) })
            {
                if (b < 0 || b >= model.Branches.Count || model.Branches[b].From != f || model.Branches[b].To != t)
                    throw new InvalidDataException($"Zero-sequence mutual coupling: branch {b} is not the line {f}-{t} it was given for; the model's branches changed since the coupling was read.");
                if (model.Branches[b].FromWinding is not null || model.Branches[b].ToWinding is not null)
                    throw new InvalidDataException($"Zero-sequence mutual coupling: branch {f}-{t} is a transformer; only lines are coupled.");
            }
            if (mu.BranchA == mu.BranchB) throw new InvalidDataException($"Zero-sequence mutual coupling: line {mu.FromA}-{mu.ToA} is coupled to itself.");
            if (!Live(mu.BranchA) || !Live(mu.BranchB)) continue;
            parent.TryAdd(mu.BranchA, mu.BranchA);
            parent.TryAdd(mu.BranchB, mu.BranchB);
            int ra = Find(mu.BranchA), rb = Find(mu.BranchB);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
        var byRoot = new SortedDictionary<int, List<int>>();
        foreach (int b in parent.Keys.OrderBy(x => x))
        {
            int r = Find(b);
            if (!byRoot.TryGetValue(r, out List<int>? list)) byRoot[r] = list = new List<int>();
            list.Add(b);
        }
        return byRoot.Values.ToList();
    }

    /// <summary>The inverse of a small complex matrix by Gauss-Jordan elimination with partial pivoting (largest
    /// magnitude, first on ties), a fixed order of operations; a singular matrix is refused naming the lines.</summary>
    private static Complex[,] InvertComplex(Complex[,] a, List<int> branches, GridModel model)
    {
        int m = a.GetLength(0);
        var w = new Complex[m, 2 * m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++) w[i, j] = a[i, j];
            w[i, m + i] = Complex.One;
        }
        for (int c = 0; c < m; c++)
        {
            int p = c;
            double best = w[c, c].Magnitude;
            for (int r = c + 1; r < m; r++) if (w[r, c].Magnitude > best) { best = w[r, c].Magnitude; p = r; }
            if (!(best > 0))
                throw new InvalidDataException("Zero-sequence mutual coupling: the impedance matrix of the coupled lines " +
                    string.Join(", ", branches.Select(b => $"{model.Branches[b].From}-{model.Branches[b].To}")) + " is singular.");
            if (p != c) for (int j = 0; j < 2 * m; j++) (w[c, j], w[p, j]) = (w[p, j], w[c, j]);
            Complex piv = w[c, c];
            for (int j = 0; j < 2 * m; j++) w[c, j] /= piv;
            for (int r = 0; r < m; r++)
            {
                if (r == c || w[r, c] == Complex.Zero) continue;
                Complex f = w[r, c];
                for (int j = 0; j < 2 * m; j++) w[r, j] -= f * w[c, j];
            }
        }
        var inv = new Complex[m, m];
        for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) inv[i, j] = w[i, m + j];
        return inv;
    }

    /// <summary>
    /// The driving-point Thevenin impedance at <paramref name="faultBusId"/> for a sequence network whose
    /// passive part is the branch series network (no line charging, no bus shunts, loads neglected per 60909) and whose
    /// sources are each generator as a reactance to ground given by <paramref name="machineReactancePu"/>
    /// (pu on the machine's <c>MBase</c>). Shared by the positive- and negative-sequence entry points, which
    /// differ only in that reactance selector.
    /// </summary>
    private static (Complex ZPu, double NominalKvLL) SequenceThevenin(
        GridModel model, int faultBusId, Func<Generator, double> machineReactancePu)
    {
        int n = model.BusCount;

        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        if (!index.TryGetValue(faultBusId, out int k))
            throw new ArgumentException($"fault bus {faultBusId} is not in the model", nameof(faultBusId));

        // D-DOC-096 (October 2026): the fault bus's island only, as the positive sequence (BuildPositiveNetwork): a
        // sourceless island elsewhere would make the matrix singular on the sparse path. A connected model is used as
        // it is (same object), so its Z2 is bit for bit as before.
        GridModel whole = model;
        model = FaultIsland(model, faultBusId);
        if (!ReferenceEquals(model, whole))
        {
            n = model.BusCount;
            index = new Dictionary<int, int>(n);
            for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
            k = index[faultBusId];
        }

        double baseMva = model.BaseMva;

        // Sequence fault admittance: the passive series network (no line charging, no bus shunts - IEC 60909-0
        // neglects both in the positive and negative sequence, R284) with each generator as a reactance to ground
        // (X''d for positive sequence, X2 for negative). Loads are NOT converted to shunts.
        var shunts = new List<(int Index, Complex Y)>();
        foreach (Generator g in model.Generators)
        {
            if (!index.TryGetValue(g.Bus, out int i)) continue;
            if (g.PowerFlowOnly || g.IsConverterInterfaced) continue;   // no reactance to ground (open in Z1/Z2)
            double xSys = machineReactancePu(g) * baseMva / RatedMva(g);   // GR-603: a machine with no rating is refused
            if (xSys <= 0) continue;
            shunts.Add((i, Complex.One / new Complex(0.0, xSys)));
        }
        if (shunts.Count == 0) throw NoSynchronousSource(model, ReferenceEquals(model, whole) ? int.MinValue : faultBusId);

        // One column of Zbus: solve y . x = e_k. The driving-point impedance Zkk is the sequence Thevenin
        // impedance seen from the fault bus by the single equivalent source there.
        var e = new Complex[n];
        e[k] = Complex.One;
        Complex[] zColumn = FaultNetworkSystem.ForModel(model, shunts, seriesOnly: true).Solve(e);

        double un = model.Buses[k].BaseKv;
        if (un <= 0.0)
            throw new InvalidOperationException($"fault bus {faultBusId} has no base kV; IEC 60909 needs a nominal voltage.");

        return (zColumn[k], un);
    }

    /// <summary>A converter-interfaced unit in the positive-sequence fault network: its IEC 60909-0:2016 source
    /// current <c>I_skPF = k * I_rG</c> (pu on the system base), its rating, and the transfer impedance
    /// <c>Z_kj</c> from its bus to the fault bus.</summary>
    private readonly record struct ConverterInfeed(int Bus, int BusIndex, string Name, double CurrentPu, double MBase, Complex Zkj);

    /// <summary>
    /// The positive-sequence fault network seen from one fault bus, with synchronous machines as X''d shunts and
    /// converter-interfaced units split out as current sources. <see cref="UpliftPu"/> is the IEC 60909-0:2016
    /// converter term <c>sum_j |Z_kj| * I_skPF,j</c>: the three-phase current is
    /// <c>(c + UpliftPu) / |Z_kk|</c> (in pu, source <c>c*Un/sqrt(3)</c>).
    /// </summary>
    private sealed class PositiveNetwork
    {
        public required Dictionary<int, int> Index { get; init; }
        public required int K { get; init; }
        public required double Un { get; init; }
        public required Complex[] ZColumn { get; init; }
        public required FaultNetworkSystem Lu { get; init; }
        public required List<(int Bus, string Name, Complex YShunt, double MBase)> Machines { get; init; }
        public required List<ConverterInfeed> Converters { get; init; }

        /// <summary>D-DOC-098: the fault network is MESHED in the IEC 60909 sense (see <see cref="IsMeshed"/>), so the
        /// peak factor takes Method B's 1.15.</summary>
        public required bool Meshed { get; init; }

        public Complex Zkk => ZColumn[K];

        public double UpliftPu
        {
            get
            {
                double u = 0.0;
                foreach (ConverterInfeed cv in Converters) u += cv.Zkj.Magnitude * cv.CurrentPu;
                return u;
            }
        }

        /// <summary>
        /// The during-fault bus voltages (pu) caused by the converter current sources alone, with the fault bus
        /// held at zero (the equivalent-source superposition). Each converter is phased so its contribution to the
        /// fault current is in phase with the synchronous one (<c>arg I_j = -arg Z_kj</c>), which is what makes the
        /// standard's arithmetic sum of magnitudes exact. All zeros when there are no converters.
        /// </summary>
        public Complex[] ConverterVoltages()
        {
            int n = ZColumn.Length;
            var v = new Complex[n];
            if (Converters.Count == 0) return v;
            var inj = new Complex[n];
            foreach (ConverterInfeed cv in Converters)
                inj[cv.BusIndex] += DetMath.FromPolar(cv.CurrentPu, -DetMath.Phase(cv.Zkj));
            Complex[] x = Lu.Solve(inj);                       // open-circuit response Z * I
            Complex scale = x[K] / Zkk;                        // then short the fault bus (compensation)
            for (int i = 0; i < n; i++) v[i] = x[i] - ZColumn[i] * scale;
            return v;
        }
    }

    /// <summary>
    /// Assemble the positive-sequence fault network at <paramref name="faultBusId"/>: the network branches' series
    /// impedances and taps (no line charging, no bus shunts, loads neglected per 60909; R284) plus each
    /// <b>synchronous</b> generator as its X''d to ground.
    /// Converter-interfaced generators (<see cref="Generator.IsConverterInterfaced"/>) are left open - they have
    /// no internal EMF behind a reactance - and recorded as current sources <c>I_skPF = k * I_rG</c> with their
    /// transfer impedance <c>Z_kj</c> (row k of Zbus; column k by symmetry unless a phase shifter breaks it).
    /// Factors Y once (on the sparse engine at every size, N233) and reuses it for every
    /// right-hand side.
    /// </summary>
    private static PositiveNetwork BuildPositiveNetwork(GridModel model, int faultBusId, double subtransientXdPu)
    {
        {
            // The fault bus must be a bus of the WHOLE model (the same error as before for a bus that is not there),
            // and have a nominal voltage, before the network is cut down to its island.
            int kAll = -1;
            for (int i = 0; i < model.BusCount; i++) if (model.Buses[i].Id == faultBusId) { kAll = i; break; }
            if (kAll < 0) throw new ArgumentException($"fault bus {faultBusId} is not in the model", nameof(faultBusId));
            if (model.Buses[kAll].BaseKv <= 0.0)
                throw new InvalidOperationException($"fault bus {faultBusId} has no base kV; IEC 60909 needs a nominal voltage.");
        }
        // D-DOC-096 (October 2026): study only the fault bus's island (see FaultIsland). Every Zbus entry outside it is
        // exactly zero, so nothing is lost; a connected model is used as it is, so its results are bit for bit as before.
        GridModel whole = model;
        model = FaultIsland(model, faultBusId);
        int n = model.BusCount;

        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        int k = index[faultBusId];

        double un = model.Buses[k].BaseKv;

        double baseMva = model.BaseMva;
        var shunts = new List<(int Index, Complex Y)>();
        var machines = new List<(int Bus, string Name, Complex YShunt, double MBase)>();
        var converterRaw = new List<(int Bus, int Index, string Name, double CurrentPu, double MBase)>();
        foreach (Generator g in model.Generators)
        {
            if (!index.TryGetValue(g.Bus, out int i)) continue;
            string name = g.Name ?? $"gen@bus {g.Bus}";
            if (g.PowerFlowOnly) continue;          // a power-flow device (slack balancer, voltage control): no source
            double mBase = RatedMva(g);             // GR-603: a fault source with no rating is refused, not given the system base
            if (g.IsConverterInterfaced)
            {
                converterRaw.Add((g.Bus, i, name, g.EffectiveConverterFaultCurrentPu * mBase / baseMva, mBase));
                continue;
            }
            double xdSys = g.FaultSourceReactancePu(subtransientXdPu) * baseMva / mBase;
            if (xdSys <= 0) continue;
            Complex yShunt = Complex.One / new Complex(0.0, xdSys);
            shunts.Add((i, yShunt));
            machines.Add((g.Bus, name, yShunt, mBase));
        }
        if (machines.Count == 0) throw NoSynchronousSource(model, ReferenceEquals(model, whole) ? int.MinValue : faultBusId);

        FaultNetworkSystem lu = FaultNetworkSystem.ForModel(model, shunts, seriesOnly: true);   // R284: 60909 network
        var e = new Complex[n];
        e[k] = Complex.One;
        Complex[] zColumn = lu.Solve(e);

        var converters = new List<ConverterInfeed>(converterRaw.Count);
        if (converterRaw.Count > 0)
        {
            // Row k of Zbus. Y is symmetric unless a phase-shifting transformer is in service; then solve the
            // transposed system (Y^T x = e_k gives x_j = Z_kj).
            bool symmetric = true;
            foreach (Branch br in model.Branches)
                if (br.InService && br.PhaseShiftDeg != 0.0) { symmetric = false; break; }
            Complex[] zRow = symmetric ? zColumn : lu.Transpose().Solve(e);
            foreach ((int bus, int bi, string name, double iPu, double mBase) in converterRaw)
                converters.Add(new ConverterInfeed(bus, bi, name, iPu, mBase, zRow[bi]));
        }

        return new PositiveNetwork
        {
            Index = index, K = k, Un = un, ZColumn = zColumn, Lu = lu, Machines = machines, Converters = converters,
            Meshed = IsMeshed(model, machines.Count),
        };
    }

    /// <summary>
    /// D-DOC-096 (October 2026): the part of <paramref name="model"/> a short circuit at <paramref name="faultBusId"/> can
    /// draw current from - the buses connected to it through in-service branches, the branches between them and the
    /// generators on them - in the model's own order. When that is the whole model the model itself is returned (the
    /// same object), so a connected case is studied exactly as before.
    /// <para>Why: the IEC 60909 sequence networks have no loads, line charging or bus shunts, so a separate island
    /// with no generator has no path to earth and the fault-network admittance matrix is singular. The sparse LU
    /// (then used above 1,500 buses; at every size since N233) refused it, so `iec60909 gb-full` exited 1 at every bus; the dense
    /// LU clamped the island's vanishing pivots and, because the fault right-hand side is zero there, still passed its
    /// residual check, so the two paths disagreed on the same network. A Zbus column of the fault bus is exactly zero
    /// on every bus outside its island (no admittance couples them), so dropping those buses changes no number: the
    /// dense path gives the same bits it gave before for a bus in a sourced island, and both paths now solve the same
    /// matrix. A fault bus whose OWN island has no source is refused by name (<see cref="NoSynchronousSource"/>).</para>
    /// </summary>
    internal static GridModel FaultIsland(GridModel model, int faultBusId)
    {
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        int k = index[faultBusId];
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            if (!index.TryGetValue(br.From, out int f) || !index.TryGetValue(br.To, out int t)) continue;
            adj[f].Add(t); adj[t].Add(f);
        }
        var seen = new bool[n];
        seen[k] = true;
        int count = 1;
        var queue = new Queue<int>();
        queue.Enqueue(k);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int w in adj[u])
                if (!seen[w]) { seen[w] = true; count++; queue.Enqueue(w); }
        }
        if (count == n) return model;   // connected: the model itself, untouched

        var keep = new HashSet<int>();
        var buses = new List<Bus>(count);
        for (int i = 0; i < n; i++) if (seen[i]) { buses.Add(model.Buses[i]); keep.Add(model.Buses[i].Id); }
        var branches = new List<Branch>();
        foreach (Branch br in model.Branches)
            if (keep.Contains(br.From) && keep.Contains(br.To)) branches.Add(br);
        var generators = new List<Generator>();
        foreach (Generator g in model.Generators) if (keep.Contains(g.Bus)) generators.Add(g);
        return new GridModel
        {
            BaseMva = model.BaseMva, Buses = buses, Branches = branches, Generators = generators,
            FrequencyHz = model.FrequencyHz, Name = model.Name,
        };
    }

    /// <summary>
    /// D-DOC-098 (October 2026): whether a fault network is MESHED in the IEC 60909 sense, i.e. the short-circuit current
    /// can reach the fault over more than one path. The network is taken as a graph whose nodes are its buses plus one
    /// earth node, and whose edges are its in-service branches (each parallel circuit counted) and one edge from each
    /// synchronous source to earth (<paramref name="sourceCount"/>, the machines' X''d shunts). A connected graph is a
    /// tree, the radial single-source case for which kappa(R/X of Zk) is exact, exactly when it has one edge fewer than
    /// nodes; any extra edge closes a loop (a ring, a parallel circuit, or a second source, whose two paths meet at the
    /// fault through earth), and the network is meshed. A radial network fed from several sources counts as meshed:
    /// the standard sums each source's partial peak there, and Method B with 1.15 is the conservative stand-in [VERIFY].
    /// <paramref name="model"/> is the fault bus's island (<see cref="FaultIsland"/>), so it is connected.
    /// </summary>
    internal static bool IsMeshed(GridModel model, int sourceCount)
    {
        var ids = new HashSet<int>();
        foreach (Bus b in model.Buses) ids.Add(b.Id);
        long edges = sourceCount;
        foreach (Branch br in model.Branches)
            if (br.InService && br.From != br.To && ids.Contains(br.From) && ids.Contains(br.To)) edges++;
        long nodes = ids.Count + (sourceCount > 0 ? 1 : 0);
        return edges > nodes - 1;
    }

    /// <summary>
    /// Add a converter-fed initial current <paramref name="converterIkppPu"/> (pu) to a synchronous duty per
    /// IEC 60909-0:2016. A controlled current source has no DC offset and no AC decay, so its share adds
    /// <c>sqrt(2) * I''k,PF</c> to the peak (no kappa), its full value to the breaking and steady-state currents,
    /// and enters the thermal current as <c>Ith = sqrt(m * I''k,s^2 + I''k^2)</c> (n = 1). The DC component,
    /// kappa and m stay those of the synchronous part. Returns <paramref name="sync"/> unchanged when there is no
    /// converter share, so a model without converters is bit-for-bit as before.
    /// </summary>
    private static Duty WithConverterInfeed(Duty sync, double converterIkppPu, double nominalKvLL, double baseMva)
    {
        if (!(converterIkppPu > 0.0)) return sync;
        double iBaseKa = baseMva / (Math.Sqrt(3.0) * nominalKvLL);
        double pfKa = converterIkppPu * iBaseKa;
        double ikpp = sync.IkppKa + pfKa;
        double ith = Math.Sqrt(sync.ThermalM * sync.IkppKa * sync.IkppKa + ikpp * ikpp);
        return sync with
        {
            IkppKa = ikpp,
            PeakIpKa = sync.PeakIpKa + Math.Sqrt(2.0) * pfKa,
            BreakingIbKa = sync.BreakingIbKa + pfKa,
            SteadyIkKa = sync.SteadyIkKa + pfKa,
            ThermalIthKa = ith,
            IkppPu = sync.IkppPu + converterIkppPu,
            ConverterIkppKa = pfKa,
        };
    }

    /// <summary>The converter share of an unbalanced fault. The converters are taken to inject balanced
    /// positive-sequence current (negative-sequence contribution neglected, the IEC 60909-0:2016 default without
    /// manufacturer data), which raises the positive-sequence driving voltage by <c>UpliftPu</c>; every
    /// sequence-connection formula is linear in that voltage, so the share is the same formula driven by it.</summary>
    private static Duty AddConverterShare(
        Duty sync, PositiveNetwork net, UnbalancedFaultType type, Complex z2, Complex z0, double baseMva,
        double f0Hz, double breakTimeSeconds, double faultDurationSeconds)
    {
        double uplift = net.UpliftPu;
        if (!(uplift > 0.0)) return sync;
        Duty pf = FromSequenceImpedances(type, uplift, net.Un, net.Zkk, z2, z0, baseMva, f0Hz, breakTimeSeconds, faultDurationSeconds);
        return WithConverterInfeed(sync, pf.IkppPu, net.Un, baseMva);
    }

    /// <summary>
    /// GR-603 (October 2026): the rating (MVA) of a machine that feeds an IEC 60909 fault. The study needs a real rating:
    /// the near-to-generator criterion compares each machine's partial current with its rated current
    /// (<c>I''kG &gt; 2 I_rG</c>), mu and the steady-state factors take that ratio, and a converter's infeed is
    /// <c>k x I_rG</c>. A machine with <c>MBase &lt;= 0</c> (the MATPOWER "not given") used to be rated at the system
    /// base, so a 1000 MVA unit read as 100 MVA and its breaking current was decayed as if it were near a small machine.
    /// It is now refused, naming the unit; so is a NaN or infinite MBase. Every machine with a finite MBase above 0
    /// returns that MBase, so its numbers are unchanged bit for bit.
    /// </summary>
    private static double RatedMva(Generator g)
    {
        if (!(g.MBase > 0.0) || !double.IsFinite(g.MBase))
            throw new InvalidOperationException(
                $"IEC 60909: generator {g.Name ?? "(unnamed)"} at bus {g.Bus} has MBase = {g.MBase.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}: "
                + "a short-circuit study needs each fault source's rating (its rated current sets the near-to-generator "
                + "criterion, mu and a converter's k x I_rG infeed). Give the unit its MBase in MVA (or mark it power-flow only).");
        return g.MBase;
    }

    /// <summary>The equivalent-voltage-source method needs a synchronous source (or network infeed) to form the
    /// fault network; converter-only plant has no reactance to ground.
    /// <para>D-DOC-096 (October 2026): <paramref name="model"/> is the fault bus's island (<see cref="FaultIsland"/>), so
    /// the message says "the island of bus X" when the island is part of a larger network: a bus in a separate island
    /// with no generator is refused by name instead of making the whole network singular.</para></summary>
    private static InvalidOperationException NoSynchronousSource(GridModel model, int faultBusId = int.MinValue)
    {
        bool anyConverter = false;
        foreach (Generator g in model.Generators) if (g.IsConverterInterfaced) { anyConverter = true; break; }
        string where = faultBusId == int.MinValue ? "this model has"
            : $"the island of fault bus {faultBusId} ({model.BusCount} bus(es) connected to it through in-service branches) has";
        // In an island the reason is also stated as what it means for the fault network: with no source it has no earth
        // path, so its admittance matrix is singular (the wording both factor paths used before D-DOC-096).
        string singular = faultBusId == int.MinValue ? ""
            : " With no source the island's fault-network admittance matrix is singular (no earth path), so no current can be formed.";
        return new InvalidOperationException((anyConverter
            ? $"IEC 60909 needs at least one synchronous source or network infeed to feed the fault; {where} "
              + "only converter-interfaced plant, which contributes current-limited infeeds (k x rated), not a source impedance."
            : $"IEC 60909 needs at least one source (generator) to feed the fault; {where} none.") + singular);
    }
}
