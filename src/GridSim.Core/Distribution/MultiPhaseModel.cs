using System.Numerics;

namespace GridSim.Core.Distribution;

/// <summary>The phases a bus or branch carries. A full three-wire feeder is <see cref="Abc"/>; single- and
/// two-phase laterals use the subsets (N062, October 2026: <see cref="PhaseBranch.Phases"/> carries them and the
/// three-phase sweep solves only the phases each bus has; a bus's phases are those of the branch feeding it).</summary>
[Flags]
public enum Phase
{
    /// <summary>No phase.</summary>
    None = 0,
    /// <summary>Phase A.</summary>
    A = 1,
    /// <summary>Phase B.</summary>
    B = 2,
    /// <summary>Phase C.</summary>
    C = 4,
    /// <summary>All three phases.</summary>
    Abc = A | B | C,
}

/// <summary>
/// A 3x3 complex phase-impedance (or admittance) matrix - the untransposed series impedance of a distribution
/// line segment, with self terms on the diagonal and mutual coupling off it. A balanced positive-sequence
/// line is <see cref="Diagonal"/> with zero mutuals.
/// </summary>
public sealed class PhaseImpedance
{
    private readonly Complex[,] _z; // 3x3

    /// <summary>Wrap a 3x3 complex matrix (copied).</summary>
    public PhaseImpedance(Complex[,] z)
    {
        if (z.GetLength(0) != 3 || z.GetLength(1) != 3)
            throw new ArgumentException("phase impedance must be 3x3.");
        _z = (Complex[,])z.Clone();
    }

    /// <summary>Element (row, col).</summary>
    public Complex this[int row, int col] => _z[row, col];

    /// <summary>A balanced diagonal matrix z*I (equal self impedance, zero mutual) - the lift of a
    /// positive-sequence series impedance into three decoupled phases.</summary>
    public static PhaseImpedance Diagonal(Complex z)
    {
        var m = new Complex[3, 3];
        m[0, 0] = m[1, 1] = m[2, 2] = z;
        return new PhaseImpedance(m);
    }

    /// <summary>The 3x3 complex matrix inverse (via cofactors) - the series admittance Y = Z^-1 of a line
    /// segment. Throws on a singular matrix.</summary>
    public PhaseImpedance Invert()
    {
        Complex a = _z[0, 0], b = _z[0, 1], c = _z[0, 2];
        Complex d = _z[1, 0], e = _z[1, 1], f = _z[1, 2];
        Complex g = _z[2, 0], h = _z[2, 1], i = _z[2, 2];

        Complex cof00 = e * i - f * h, cof01 = -(d * i - f * g), cof02 = d * h - e * g;
        Complex det = a * cof00 + b * cof01 + c * cof02;
        if (det == Complex.Zero) throw new InvalidOperationException("singular phase impedance matrix.");

        Complex cof10 = -(b * i - c * h), cof11 = a * i - c * g, cof12 = -(a * h - b * g);
        Complex cof20 = b * f - c * e, cof21 = -(a * f - c * d), cof22 = a * e - b * d;

        var m = new Complex[3, 3]; // inverse = adjugate (cofactor^T) / det
        m[0, 0] = cof00 / det; m[0, 1] = cof10 / det; m[0, 2] = cof20 / det;
        m[1, 0] = cof01 / det; m[1, 1] = cof11 / det; m[1, 2] = cof21 / det;
        m[2, 0] = cof02 / det; m[2, 1] = cof12 / det; m[2, 2] = cof22 / det;
        return new PhaseImpedance(m);
    }

    /// <summary>Matrix-vector product Z * v (v length 3), returning a length-3 vector.</summary>
    public Complex[] Multiply(Complex[] v)
    {
        var r = new Complex[3];
        for (int i = 0; i < 3; i++)
        {
            Complex s = Complex.Zero;
            for (int j = 0; j < 3; j++) s += _z[i, j] * v[j];
            r[i] = s;
        }
        return r;
    }
}

/// <summary>A bus in the unbalanced model. A source (substation) bus holds a fixed three-phase voltage; every
/// other bus's voltage is solved.</summary>
/// <param name="Id">Bus id (shared with the positive-sequence world where a bridge exists).</param>
/// <param name="IsSource">True for the reference/substation bus.</param>
/// <param name="SourceV">The fixed per-phase voltage (length 3, per-unit) when <paramref name="IsSource"/>; else null.</param>
public sealed record PhaseBus(int Id, bool IsSource, Complex[]? SourceV = null);

/// <summary>
/// A radial branch: a series 3x3 phase impedance (per-unit), and, since N062 (October 2026), optionally the phases it
/// carries, a shunt (line-charging) admittance and an ideal step-voltage regulator at its From end.
///
/// The element, from the From bus to the To bus, is: an ideal per-phase regulator (when <paramref name="RegulatorRatio"/>
/// is set; otherwise a straight connection), then a pi section: half of <paramref name="ShuntY"/> to earth, the series
/// <paramref name="Z"/>, and the other half of <paramref name="ShuntY"/> to earth at the To end. With the three optional
/// parameters at their defaults it is exactly the v1 line (three phases, series impedance only), and the sweep does
/// exactly the arithmetic it did before.
/// </summary>
/// <param name="From">The endpoint nearer the source (the sweep orients every branch from the source outward).</param>
/// <param name="To">The other endpoint bus id.</param>
/// <param name="Z">The 3x3 series phase impedance (per-unit). Rows and columns of phases the branch does not carry
/// must be zero. A closed switch is a branch with Z all zero (no voltage drop).</param>
/// <param name="Phases">The phases (conductors) the branch carries; <see cref="Phase.Abc"/> for a three-phase line, a
/// subset for a single- or two-phase lateral. The bus at the To end has exactly these phases.</param>
/// <param name="ShuntY">The total 3x3 shunt admittance of the branch (per-unit), half at each end (the pi model of a
/// line's charging). Null: none. Rows and columns of absent phases must be zero.</param>
/// <param name="RegulatorRatio">The per-phase voltage ratio of an ideal step-voltage regulator at the From end
/// (length 3): the regulator's output voltage is RegulatorRatio[p] times the From bus voltage on phase p, and the
/// current it draws from the From bus is RegulatorRatio[p] times its output current (ideal, lossless). Null: no
/// regulator. For a regulator with tap n and step s (per unit per step) the ratio is 1 + s n (see
/// <see cref="StepRegulatorRatio"/>).</param>
/// <param name="DeltaWyeStepDown">1.95.251 (N168): the branch is a delta / grounded-wye step-down transformer at its
/// rated ratio (ANSI Dyn1: the From side is the delta, the To side the grounded wye and lags by 30 degrees), with
/// <paramref name="Z"/> its leakage impedance on the To side. In per-unit (each side's line-to-neutral base at its rated
/// voltage) the ideal part is Kersting's generalised matrices reduced to V_to = <see cref="DeltaWyeVoltage"/> V_from and
/// I_from = <see cref="DeltaWyeCurrent"/> I_to: lossless (one is the transpose of the other), no zero-sequence current on
/// the delta side. False (the default) is every branch GridSim modelled before, exactly.</param>
/// <param name="DeltaWyeStepUp">1.95.252 (N168): the branch is a delta / grounded-wye STEP-UP transformer at its rated
/// ratio (the From side is the lower-voltage delta, the To side the grounded wye, which leads by 30 degrees), with
/// <paramref name="Z"/> its leakage impedance on the To side: V_to = <see cref="DeltaWyeStepUpVoltage"/> V_from,
/// I_from = <see cref="DeltaWyeStepUpCurrent"/> I_to. At most one of the two delta / wye flags is set.</param>
/// <param name="WyeDeltaStepDown">1.95.253 (N168): an ungrounded-wye / delta step-down transformer (wye From side,
/// delta To side, the delta lagging by 30 degrees), equal winding impedances, Z on the To side. See
/// <see cref="TransformerOutput"/>.</param>
/// <param name="WyeDeltaStepUp">1.95.253: the ungrounded-wye / delta step-up (the delta To side is the higher voltage
/// and leads by 30 degrees).</param>
/// <param name="DeltaDelta">1.95.253: a delta / delta transformer (no phase shift, no zero sequence), equal winding
/// impedances. At most one transformer flag is set on a branch.</param>
/// <param name="TransformerRatio">1.95.254 (N168): the per-unit ideal ratio of a transformer element that is not at its
/// rated ratio, t = (kV_base,From / kV_rated,From) (kV_rated,To / kV_base,To); 1 (the default) is the rated ratio. Ignored
/// on a branch that is not a transformer element (an off-nominal gy-gy unit is an ideal ratio at its From end, the
/// <paramref name="RegulatorRatio"/> element).</param>
/// <param name="OpenWyeOpenDelta">N334 (CF5): an open wye / open delta bank (Kersting): two single-phase units, primaries
/// on From-side phases A and B to earth (the From bus needs phases a and b and an earthed system; phase c is not used),
/// secondaries forming an open delta (windings ab and bc, the ca side open) that serves a three-wire, three-phase To side.
/// V_ab = sqrt 3 V_A, V_bc = sqrt 3 V_B in per-unit at the rated ratio (the To side lags by 30 degrees);
/// I_A = sqrt 3 I_a, I_B = -sqrt 3 I_c, I_C = 0. <paramref name="Z"/> is the open-delta series impedance
/// (<see cref="TransformerWindings.OpenDeltaSeriesZ"/>). See <see cref="TransformerOutput"/>.</param>
/// <param name="OpenDeltaOpenDelta">N334 (CF5): an open delta / open delta bank: two units, primaries across From-side
/// A-B and B-C, secondaries an open delta (ab, bc). No phase shift: the To side's line-to-line voltages are the From
/// side's (per-unit, rated ratio); I_A = I_a, I_B = -(I_a + I_c), I_C = I_c. <paramref name="Z"/> as for
/// <paramref name="OpenWyeOpenDelta"/>. At most one transformer flag is set on a branch.</param>
public sealed record PhaseBranch(int From, int To, PhaseImpedance Z, Phase Phases = Phase.Abc,
    PhaseImpedance? ShuntY = null, double[]? RegulatorRatio = null, bool DeltaWyeStepDown = false, bool DeltaWyeStepUp = false,
    bool WyeDeltaStepDown = false, bool WyeDeltaStepUp = false, bool DeltaDelta = false, double TransformerRatio = 1.0,
    bool OpenWyeOpenDelta = false, bool OpenDeltaOpenDelta = false)
{
    /// <summary>1.95.253 (N168): true for a transformer whose To (secondary) side is a delta, a THREE-WIRE system with
    /// no zero-sequence path: <see cref="WyeDeltaStepDown"/>, <see cref="WyeDeltaStepUp"/> or <see cref="DeltaDelta"/>.
    /// Line-to-neutral voltages downstream are the equivalent ones of zero zero-sequence (Kersting's W convention), and
    /// no element there may need a neutral or earth (the sweep refuses wye loads, capacitors, line charging and
    /// regulators on such buses).</summary>
    public bool HasDeltaSecondary => WyeDeltaStepDown || WyeDeltaStepUp || DeltaDelta || IsOpenTransformer;

    /// <summary>N334 (CF5): true for an open-delta secondary bank (<see cref="OpenWyeOpenDelta"/> or
    /// <see cref="OpenDeltaOpenDelta"/>). Its To side is a three-wire system like any delta secondary.</summary>
    public bool IsOpenTransformer => OpenWyeOpenDelta || OpenDeltaOpenDelta;

    /// <summary>N334 (CF5): how many transformer flags are set (a well-formed branch has 0 or 1).</summary>
    public int TransformerFlagCount => (DeltaWyeStepDown ? 1 : 0) + (DeltaWyeStepUp ? 1 : 0) + (WyeDeltaStepDown ? 1 : 0)
        + (WyeDeltaStepUp ? 1 : 0) + (DeltaDelta ? 1 : 0) + (OpenWyeOpenDelta ? 1 : 0) + (OpenDeltaOpenDelta ? 1 : 0);

    /// <summary>1.95.253: the connection of this branch's transformer element in words ("delta / grounded-wye",
    /// "wye / delta", "delta / delta"), for messages; empty for a branch that is not one.</summary>
    public string TransformerName => IsDeltaWye ? "delta / grounded-wye" : WyeDeltaStepDown || WyeDeltaStepUp ? "wye / delta" : DeltaDelta ? "delta / delta"
        : OpenWyeOpenDelta ? "open wye / open delta" : OpenDeltaOpenDelta ? "open delta / open delta" : "";

    /// <summary>1.95.253: true for any transformer element (delta / grounded wye, ungrounded wye / delta, delta / delta).</summary>
    public bool IsTransformerElement => IsDeltaWye || HasDeltaSecondary;

    /// <summary>1.95.253 (N168): the To-side open-circuit voltage of this branch's transformer element from its From-side
    /// voltages. Delta / grounded wye uses <see cref="DeltaWyeOutput"/> exactly as before. Ungrounded wye / delta, with
    /// equal winding impedances, has the same sequence behaviour as a delta / grounded wye of the same direction (the
    /// higher-voltage side leads by 30 degrees; no zero sequence passes), so it uses the same matrices
    /// (<see cref="DeltaWyeVoltage"/> step-down, <see cref="DeltaWyeStepUpVoltage"/> step-up). Delta / delta passes the
    /// positive and negative sequences 1:1 and blocks the zero sequence: <see cref="RemoveZeroSequence"/>.</summary>
    public Complex[] TransformerOutput(Complex[] vFrom) => ScaleByRatio(
        IsDeltaWye ? DeltaWyeOutput(vFrom)
        : WyeDeltaStepDown ? DeltaWyeVoltage(vFrom)
        : WyeDeltaStepUp ? DeltaWyeStepUpVoltage(vFrom)
        : OpenWyeOpenDelta ? OpenWyeOpenDeltaVoltage(vFrom)      // N334 (CF5)
        : RemoveZeroSequence(vFrom));                            // delta / delta and (N334) open delta / open delta

    // 1.95.254 (N168): an off-nominal transformer's per-unit ideal ratio t (TransformerRatio) scales both the open-circuit
    // voltage and the From-side current (V_to = t A V_from, I_from = t A^T I_to: still lossless). At the rated ratio
    // (t exactly 1) nothing is multiplied, so a rated transformer runs exactly the arithmetic it always did.
    private Complex[] ScaleByRatio(Complex[] x)
    {
        if (TransformerRatio == 1.0) return x;
        return new[] { x[0] * TransformerRatio, x[1] * TransformerRatio, x[2] * TransformerRatio };
    }

    /// <summary>1.95.253: the From-side line currents of this branch's transformer element for its To-side current, the
    /// transpose of <see cref="TransformerOutput"/>'s matrix (lossless).</summary>
    public Complex[] TransformerInput(Complex[] iTo) => ScaleByRatio(
        IsDeltaWye ? DeltaWyeInput(iTo)
        : WyeDeltaStepDown ? DeltaWyeCurrent(iTo)
        : WyeDeltaStepUp ? DeltaWyeStepUpCurrent(iTo)
        : OpenWyeOpenDelta ? OpenWyeOpenDeltaCurrent(iTo)        // N334 (CF5)
        : OpenDeltaOpenDelta ? OpenDeltaOpenDeltaCurrent(iTo)    // N334 (CF5)
        : RemoveZeroSequence(iTo));

    /// <summary>N334 (CF5): the open wye / open delta bank's To-side open-circuit voltages (line-to-neutral of zero zero
    /// sequence) from the From-side phase voltages. The open-delta secondaries give V_ab = sqrt 3 V_A and V_bc = sqrt 3 V_B
    /// (per-unit, rated ratio: n_t = kV_LN,From / kV_LL,To makes the per-unit factor sqrt 3), V_ca = -(V_ab + V_bc); Kersting's
    /// W then gives (sqrt 3 / 3) [[2, 1, 0], [-1, 1, 0], [-1, -2, 0]] V_ABC. Balanced 1 pu in: 1 pu out at -30 degrees.
    /// Phase C of the From side is not used.</summary>
    public static Complex[] OpenWyeOpenDeltaVoltage(Complex[] vFrom)
    {
        double k = Math.Sqrt(3.0) / 3.0;
        return new[] { (2.0 * vFrom[0] + vFrom[1]) * k, (vFrom[1] - vFrom[0]) * k, (-vFrom[0] - 2.0 * vFrom[1]) * k };
    }

    /// <summary>N334 (CF5): the open wye / open delta bank's From-side line currents: I_A = sqrt 3 I_a (unit 1 carries line
    /// a), I_B = -sqrt 3 I_c (unit 2 carries line c reversed), I_C = 0; the earth return carries I_A + I_B. For three-wire
    /// To-side currents (summing to zero) this is the transpose of <see cref="OpenWyeOpenDeltaVoltage"/>'s matrix
    /// (lossless).</summary>
    public static Complex[] OpenWyeOpenDeltaCurrent(Complex[] iTo)
    {
        double k = Math.Sqrt(3.0);
        return new[] { iTo[0] * k, -iTo[2] * k, Complex.Zero };
    }

    /// <summary>N334 (CF5): the open delta / open delta bank's From-side line currents: unit 1 (A-B) carries I_a, unit 2
    /// (B-C) carries -I_c, so I_A = I_a, I_B = -(I_a + I_c), I_C = I_c (per-unit, rated ratio). The voltages are
    /// <see cref="RemoveZeroSequence"/> (the delta / delta's: line-to-line voltages pass 1:1).</summary>
    public static Complex[] OpenDeltaOpenDeltaCurrent(Complex[] iTo)
        => new[] { iTo[0], -(iTo[0] + iTo[2]), iTo[2] };

    /// <summary>1.95.253: the triple less its zero-sequence (mean) component, (I - J / 3) x: the delta / delta transformer's
    /// voltage and current matrix (symmetric, so it is its own transpose).</summary>
    public static Complex[] RemoveZeroSequence(Complex[] x)
    {
        Complex mean = (x[0] + x[1] + x[2]) / 3.0;
        return new[] { x[0] - mean, x[1] - mean, x[2] - mean };
    }

    /// <summary>1.95.252 (N168): true for either delta / grounded-wye transformer (<see cref="DeltaWyeStepDown"/> or
    /// <see cref="DeltaWyeStepUp"/>).</summary>
    public bool IsDeltaWye => DeltaWyeStepDown || DeltaWyeStepUp;

    /// <summary>1.95.252: the wye-side open-circuit voltage of this branch's delta / grounded-wye transformer from its
    /// delta-side (From) voltages: <see cref="DeltaWyeVoltage"/> for a step-down, <see cref="DeltaWyeStepUpVoltage"/> for
    /// a step-up.</summary>
    public Complex[] DeltaWyeOutput(Complex[] vFrom) => DeltaWyeStepUp ? DeltaWyeStepUpVoltage(vFrom) : DeltaWyeVoltage(vFrom);

    /// <summary>1.95.252: the delta-side (From) line currents this branch's delta / grounded-wye transformer draws for the
    /// wye-side current: <see cref="DeltaWyeCurrent"/> for a step-down, <see cref="DeltaWyeStepUpCurrent"/> for a step-up.</summary>
    public Complex[] DeltaWyeInput(Complex[] iTo) => DeltaWyeStepUp ? DeltaWyeStepUpCurrent(iTo) : DeltaWyeCurrent(iTo);

    /// <summary>1.95.252 (N168): the per-unit voltage matrix of the delta / grounded-wye STEP-UP transformer (ANSI: the
    /// higher-voltage wye side leads the delta side by 30 degrees), (1 / sqrt 3) [[1, -1, 0], [0, 1, -1], [-1, 0, 1]]
    /// (Kersting's step-up A_t, with n_t = kV_LL,delta / kV_LN,wye cancelling to sqrt 3 in per-unit of rated bases).
    /// Phase a: (V_A - V_B) / sqrt 3, which is V_A at +30 degrees.</summary>
    public static Complex[] DeltaWyeStepUpVoltage(Complex[] vFrom)
    {
        double k = 1.0 / Math.Sqrt(3.0);
        return new[] { (vFrom[0] - vFrom[1]) * k, (vFrom[1] - vFrom[2]) * k, (vFrom[2] - vFrom[0]) * k };
    }

    /// <summary>1.95.252: the per-unit current matrix of the delta / grounded-wye step-up transformer, the transpose of
    /// <see cref="DeltaWyeStepUpVoltage"/>'s, (1 / sqrt 3) [[1, 0, -1], [-1, 1, 0], [0, -1, 1]]: the delta-side line
    /// currents from the wye-side currents (summing to zero).</summary>
    public static Complex[] DeltaWyeStepUpCurrent(Complex[] iTo)
    {
        double k = 1.0 / Math.Sqrt(3.0);
        return new[] { (iTo[0] - iTo[2]) * k, (iTo[1] - iTo[0]) * k, (iTo[2] - iTo[1]) * k };
    }

    /// <summary>1.95.251: the per-unit voltage matrix of the delta / grounded-wye step-down transformer,
    /// (1 / sqrt 3) [[1, 0, -1], [-1, 1, 0], [0, -1, 1]] (Kersting, Distribution System Modeling and Analysis, the
    /// delta / grounded-wye step-down A_t = (1 / n_t) times the same pattern, with n_t = kV_LL,HV / kV_LN,LV; in per-unit of
    /// rated line-to-neutral bases n_t cancels to sqrt 3). Phase a: (V_A - V_C) / sqrt 3, which is V_A at -30 degrees.</summary>
    public static Complex[] DeltaWyeVoltage(Complex[] vFrom)
    {
        double k = 1.0 / Math.Sqrt(3.0);
        return new[] { (vFrom[0] - vFrom[2]) * k, (vFrom[1] - vFrom[0]) * k, (vFrom[2] - vFrom[1]) * k };
    }

    /// <summary>1.95.251: the per-unit current matrix of the delta / grounded-wye step-down transformer, the transpose of
    /// <see cref="DeltaWyeVoltage"/>'s, (1 / sqrt 3) [[1, -1, 0], [0, 1, -1], [-1, 0, 1]] (Kersting's d_t): the delta-side
    /// line currents from the wye-side currents. They sum to zero (the delta carries no zero sequence).</summary>
    public static Complex[] DeltaWyeCurrent(Complex[] iTo)
    {
        double k = 1.0 / Math.Sqrt(3.0);
        return new[] { (iTo[0] - iTo[1]) * k, (iTo[1] - iTo[2]) * k, (iTo[2] - iTo[0]) * k };
    }

    /// <summary>
    /// The voltage ratio of a step-voltage regulator at tap <paramref name="tap"/> with step <paramref name="stepPu"/>
    /// (per unit per step): 1 + stepPu * tap, so raising taps are positive. This is the convention of the IEEE 123 Node
    /// Test Feeder's published results (IEEE PES Distribution System Analysis Subcommittee; docs/reference S289): with
    /// the source at 1.0000 pu and regulator RG1 on tap 7, node RG1 reads 1.0437 / 1.0438 pu (1 + 0.00625 * 7 =
    /// 1.04375), and RG2 on tap -1 takes node 9's 1.0144 pu to 1.0080 pu (1.0144 * 0.99375 = 1.00806).
    /// </summary>
    public static double StepRegulatorRatio(int tap, double stepPu) => 1.0 + stepPu * tap;

    /// <summary>True when the branch has any of the N062 elements (fewer than three phases, a shunt or a regulator):
    /// the solvers built only for three-phase series-impedance lines refuse such a model rather than ignore them.</summary>
    public bool HasExtendedElements => Phases != Phase.Abc || ShuntY is not null || RegulatorRatio is not null || IsTransformerElement;
}

/// <summary>How a <see cref="PhaseLoad"/>'s current depends on its voltage (N062, October 2026).</summary>
public enum PhaseLoadModel
{
    /// <summary>Constant power: I = conj(S / V). The v1 load.</summary>
    ConstantPower,

    /// <summary>Constant current: the magnitude |S| / |V_nominal| is fixed and the current keeps the load's power-factor
    /// angle to its own voltage, I = (|S| / |V_nominal|) at angle (angle(V) - angle(S)).</summary>
    ConstantCurrent,

    /// <summary>Constant impedance: Z = |V_nominal|^2 / conj(S), I = V / Z, so the power drawn scales with |V|^2.</summary>
    ConstantImpedance,
}

/// <summary>How a <see cref="PhaseLoad"/> is connected (N062, October 2026).</summary>
public enum PhaseLoadConnection
{
    /// <summary>Phase to neutral (earth): SPerPhase[p] is drawn between phase p and neutral at the line-to-neutral
    /// voltage; nominal voltage 1 pu.</summary>
    Wye,

    /// <summary>Phase to phase: SPerPhase[0], [1], [2] are drawn between phases a-b, b-c and c-a at the line-to-line
    /// voltages; nominal voltage sqrt(3) pu (the per-unit base is line-to-neutral). The line currents are the
    /// differences of the branch currents (I_a = I_ab - I_ca, and so on).</summary>
    Delta,
}

/// <summary>A load: per-phase complex power at nominal voltage (per-unit), and since N062 (October 2026) how it depends
/// on voltage and how it is connected. With the defaults it is the v1 constant-power wye load. A ZIP load is written as
/// up to three loads on the same bus (one constant-power, one constant-current, one constant-impedance), which is what
/// a ZIP load is: the sum of the three.</summary>
/// <param name="Bus">The bus the load sits on.</param>
/// <param name="SPerPhase">Per-phase complex power S = P + jQ at nominal voltage (length 3, per-unit of BaseMva / 3).
/// For a delta load the three entries are the a-b, b-c and c-a branches.</param>
/// <param name="Model">How the current depends on the voltage.</param>
/// <param name="Connection">Wye (phase to neutral) or delta (phase to phase).</param>
public sealed record PhaseLoad(int Bus, Complex[] SPerPhase, PhaseLoadModel Model = PhaseLoadModel.ConstantPower,
    PhaseLoadConnection Connection = PhaseLoadConnection.Wye)
{
    /// <summary>True when the load is anything but the v1 constant-power wye load.</summary>
    public bool IsExtended => Model != PhaseLoadModel.ConstantPower || Connection != PhaseLoadConnection.Wye;
}

/// <summary>
/// A wye-connected shunt capacitor bank (N062, October 2026): a constant susceptance per phase. QPerPhase[p] is the
/// reactive power (per-unit of BaseMva / 3) it delivers at 1 pu line-to-neutral voltage, so its susceptance is
/// B = QPerPhase[p] (per-unit) and it injects I = j B V; at a voltage V it delivers QPerPhase[p] |V|^2.
/// </summary>
/// <param name="Bus">The bus the bank sits on.</param>
/// <param name="QPerPhase">Rated reactive power per phase at 1 pu (length 3, per-unit, 0 or more; 0 on a phase without
/// a unit).</param>
public sealed record PhaseCapacitor(int Bus, double[] QPerPhase);

/// <summary>
/// An unbalanced three-phase distribution network: per-phase buses, 3x3-impedance radial branches, and loads. A
/// separate model path from the positive-sequence <c>GridModel</c> - the transmission engine is untouched. v1 covered
/// radial three-phase line networks with wye constant-power loads; balanced diagonal impedances reduce it to three
/// decoupled positive-sequence sweeps (the non-regression bridge). Since N062 (October 2026) a branch may carry fewer
/// than three phases, a line-charging shunt and an ideal step-voltage regulator, a load may be constant-current or
/// constant-impedance and delta-connected, and shunt capacitors may be added; with none of those the model is exactly
/// the v1 model.
/// </summary>
public sealed class MultiPhaseModel
{
    /// <summary>The buses (exactly one <see cref="PhaseBus.IsSource"/>).</summary>
    public IReadOnlyList<PhaseBus> Buses { get; }
    /// <summary>The radial branches (lines, closed switches, regulators).</summary>
    public IReadOnlyList<PhaseBranch> Branches { get; }
    /// <summary>The loads.</summary>
    public IReadOnlyList<PhaseLoad> Loads { get; }
    /// <summary>The shunt capacitor banks (N062; empty for a v1 model).</summary>
    public IReadOnlyList<PhaseCapacitor> Capacitors { get; }
    /// <summary>System base, MVA (informational; the model works in per-unit).</summary>
    public double BaseMva { get; }

    /// <summary>True when the model uses any N062 element (a branch with fewer than three phases, a shunt or a
    /// regulator; a load that is not constant-power wye; a capacitor). Since N167 (October 2026) the fault study, the
    /// estimators and the harmonic penetration check the element kinds they model one by one
    /// (<see cref="ElementsPresent"/>, <see cref="RequireSupportedElements"/>) instead of refusing every such model.</summary>
    public bool HasExtendedElements =>
        Capacitors.Count > 0 || Branches.Any(b => b.HasExtendedElements) || Loads.Any(l => l.IsExtended);

    /// <summary>Fail loud when a solver written for v1 models only is handed a model with N062 elements: the solver
    /// would otherwise treat every branch as a three-phase series impedance and every load as constant-power wye,
    /// silently leaving the rest out. Throws <see cref="ArgumentException"/> naming the solver. (N167: no GridSim solver
    /// calls this any more; each uses <see cref="RequireSupportedElements"/> with the kinds it models. Kept for callers
    /// that want the all-or-nothing v1 check.)</summary>
    public void RequireOnlyV1Elements(string solver)
    {
        if (HasExtendedElements)
            throw new ArgumentException(
                $"{solver} models three-phase series-impedance lines and constant-power wye loads only; this model has " +
                "single- or two-phase branches, line charging, regulators, capacitors, or constant-current, " +
                "constant-impedance or delta loads, which it would silently leave out. Use the three-phase sweep.");
    }

    /// <summary>
    /// N167 (October 2026): the kinds of element this model holds beyond three-phase series-impedance lines and
    /// constant-power wye loads, so each solver can accept exactly the kinds it models and refuse the rest BY NAME.
    /// <see cref="MultiPhaseElement.ClosedSwitch"/> (a three-phase branch whose series impedance is all zero) is
    /// reported here although it is not one of <see cref="HasExtendedElements"/>' kinds: a v1 model could always hold
    /// one, but a solver that inverts each branch impedance cannot.
    /// </summary>
    public MultiPhaseElement ElementsPresent
    {
        get
        {
            MultiPhaseElement e = MultiPhaseElement.None;
            foreach (PhaseBranch b in Branches)
            {
                if (b.Phases != Phase.Abc) e |= MultiPhaseElement.PartialPhaseBranch;
                if (b.ShuntY is not null) e |= MultiPhaseElement.LineCharging;
                if (b.RegulatorRatio is not null) e |= MultiPhaseElement.Regulator;
                if (IsZeroImpedance(b.Z)) e |= MultiPhaseElement.ClosedSwitch;
                if (b.IsDeltaWye) e |= MultiPhaseElement.DeltaWyeTransformer;
                if (b.HasDeltaSecondary && !b.IsOpenTransformer) e |= MultiPhaseElement.DeltaSecondaryTransformer;
                if (b.IsOpenTransformer) e |= MultiPhaseElement.OpenTransformer;   // N334 (CF5)
            }
            if (Capacitors.Count > 0) e |= MultiPhaseElement.Capacitor;
            foreach (PhaseLoad l in Loads)
            {
                if (l.Model != PhaseLoadModel.ConstantPower) e |= MultiPhaseElement.VoltageDependentLoad;
                if (l.Connection != PhaseLoadConnection.Wye) e |= MultiPhaseElement.DeltaLoad;
            }
            return e;
        }
    }

    /// <summary>True when every entry of a 3x3 series impedance is exactly zero: a closed switch (N167).</summary>
    public static bool IsZeroImpedance(PhaseImpedance z)
    {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (z[i, j] != Complex.Zero) return false;
        return true;
    }

    /// <summary>
    /// N167 (October 2026): fail loud when the model holds an element kind <paramref name="solver"/> does not model,
    /// naming every such kind present and saying what the solver does support. Throws <see cref="ArgumentException"/>.
    /// A model with only supported kinds passes untouched. (Replaces the all-or-nothing
    /// <see cref="RequireOnlyV1Elements"/> in the four solvers, which refused even the elements they model exactly.)
    /// </summary>
    /// <param name="solver">The solver's name, as the message's subject ("the sequence fault study").</param>
    /// <param name="supported">The element kinds the solver models.</param>
    /// <param name="treatment">A sentence on how the supported kinds enter the solver (for the message).</param>
    public void RequireSupportedElements(string solver, MultiPhaseElement supported, string treatment)
    {
        MultiPhaseElement unsupported = ElementsPresent & ~supported;
        if (unsupported == MultiPhaseElement.None) return;
        // D-DOC-156 (October 2026): the message used to say the solver "supports ... constant-power wye loads", and the
        // harmonic penetration's own treatment text then said "loads are not in Y(h)", so the refusal contradicted itself.
        // A solver that takes a load into its feeder without modelling it ACCEPTS the load; the treatment text says how
        // (or that it is left out). The refusal itself, the kinds it names and when it fires are unchanged.
        throw new ArgumentException(
            $"{solver} cannot model this feeder's {MultiPhaseElements.Describe(unsupported)}, and will not solve it with them left " +
            $"out. It supports three-phase series-impedance lines and accepts constant-power wye loads, plus {MultiPhaseElements.Describe(supported)} " +
            $"({treatment}). Use the three-phase sweep for this feeder.");
    }

    public MultiPhaseModel(
        IReadOnlyList<PhaseBus> buses, IReadOnlyList<PhaseBranch> branches, IReadOnlyList<PhaseLoad> loads,
        double baseMva = 100.0, IReadOnlyList<PhaseCapacitor>? capacitors = null)
    {
        Buses = buses;
        Branches = branches;
        Loads = loads;
        Capacitors = capacitors ?? Array.Empty<PhaseCapacitor>();
        BaseMva = baseMva;
        if (buses.Count(b => b.IsSource) != 1)
            throw new ArgumentException("a MultiPhaseModel needs exactly one source bus.");

        // Every load must sit on a bus that exists (R235). A load on an unknown id (a typo: 634 for 643) used
        // to be skipped by the sweep without a word, so the feeder solved "Converged" under-loaded with voltages
        // too high. Same rule as the JSON case loader: an orphan load is an error, never silently discarded.
        var ids = new HashSet<int>(buses.Select(b => b.Id));
        for (int k = 0; k < loads.Count; k++)
        {
            if (!ids.Contains(loads[k].Bus))
                throw new ArgumentException(
                    $"MultiPhaseModel load #{k} references bus {loads[k].Bus}, which is not one of the model's buses.");
            // GR-107: three finite per-phase powers (a short array threw IndexOutOfRangeException deep in the sweep).
            if (loads[k].SPerPhase is not { Length: 3 } s || s.Any(x => !IsFinite(x)))
                throw new ArgumentException($"MultiPhaseModel load #{k} on bus {loads[k].Bus} must give three finite per-phase powers.");
        }
        // GR-107: a given source voltage is three finite phasors.
        foreach (PhaseBus b in buses)
            if (b.SourceV is { } sv && (sv.Length != 3 || sv.Any(x => !IsFinite(x))))
                throw new ArgumentException($"MultiPhaseModel bus {b.Id}: the source voltage must be three finite phasors.");
        for (int k = 0; k < Capacitors.Count; k++)
        {
            PhaseCapacitor c = Capacitors[k];
            if (!ids.Contains(c.Bus))
                throw new ArgumentException($"MultiPhaseModel capacitor #{k} references bus {c.Bus}, which is not one of the model's buses.");
            if (c.QPerPhase is null || c.QPerPhase.Length != 3 || c.QPerPhase.Any(q => !double.IsFinite(q) || q < 0))
                throw new ArgumentException($"MultiPhaseModel capacitor #{k} must give three finite, non-negative per-phase ratings.");
        }

        // N062: each branch's extended parts must be well formed, so the sweep never has to guess.
        for (int k = 0; k < branches.Count; k++)
        {
            PhaseBranch br = branches[k];
            // GR-119: both ends must be buses of the model, as loads and capacitors must (R235). A branch to an unknown
            // id was taken by the three-phase harmonic study as a path to ground, skipped by the fault study and called
            // non-radial by the sweep, each without naming the typo.
            if (!ids.Contains(br.From) || !ids.Contains(br.To))
                throw new ArgumentException(
                    $"MultiPhaseModel branch #{k} ({br.From}-{br.To}) references bus {(ids.Contains(br.From) ? br.To : br.From)}, which is not one of the model's buses.");
            if (br.Phases == Phase.None || (br.Phases & ~Phase.Abc) != 0)
                throw new ArgumentException($"MultiPhaseModel branch #{k} ({br.From}-{br.To}) must carry one or more of phases a, b, c.");
            for (int i = 0; i < 3; i++)
            {
                bool present = PhaseIndex.Has(br.Phases, i);
                for (int j = 0; j < 3; j++)
                {
                    if (present && PhaseIndex.Has(br.Phases, j)) continue;
                    if (br.Z[i, j] != Complex.Zero || (br.ShuntY is not null && br.ShuntY[i, j] != Complex.Zero))
                        throw new ArgumentException(
                            $"MultiPhaseModel branch #{k} ({br.From}-{br.To}) has a non-zero impedance or shunt entry [{i},{j}] " +
                            $"on a phase it does not carry ({PhaseIndex.Name(br.Phases)}).");
                }
            }
            if (br.RegulatorRatio is { } a && (a.Length != 3 || a.Any(x => !double.IsFinite(x) || !(x > 0))))
                throw new ArgumentException($"MultiPhaseModel branch #{k} ({br.From}-{br.To}) regulator ratio must be three finite numbers above 0.");
            // GR-107: a transformer ratio that is not finite and above 0 (a negative one flipped every downstream angle by
            // 180 degrees and still converged), and a non-finite impedance or shunt entry, are refused.
            if (!(double.IsFinite(br.TransformerRatio) && br.TransformerRatio > 0))
                throw new ArgumentException($"MultiPhaseModel branch #{k} ({br.From}-{br.To}) transformer ratio {br.TransformerRatio} must be a finite number above 0.");
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (!IsFinite(br.Z[i, j]) || (br.ShuntY is not null && !IsFinite(br.ShuntY[i, j])))
                        throw new ArgumentException($"MultiPhaseModel branch #{k} ({br.From}-{br.To}) has a non-finite impedance or shunt entry [{i},{j}].");
        }
    }

    private static bool IsFinite(Complex c) => double.IsFinite(c.Real) && double.IsFinite(c.Imaginary);

    /// <summary>The three balanced unit phasors [1&lt;0, 1&lt;-120, 1&lt;+120] per-unit - a balanced source voltage.</summary>
    public static Complex[] BalancedNominal(double magnitude = 1.0) => new[]
    {
        DetMath.FromPolar(magnitude, BalancedAngleRad(0)),
        DetMath.FromPolar(magnitude, BalancedAngleRad(1)),
        DetMath.FromPolar(magnitude, BalancedAngleRad(2)),
    };

    /// <summary>The balanced phase angles (radians) <see cref="BalancedNominal"/> uses: 0, -2 pi/3, +2 pi/3 for a, b, c.
    /// D1 (October 2026): shared so the JSON loader builds a default source from exactly these angles instead of
    /// recovering them with atan2 (which returns the angle only to within an ulp).</summary>
    internal static double BalancedAngleRad(int phase) => phase switch
    {
        0 => 0,
        1 => -2.0 * Math.PI / 3.0,
        2 => +2.0 * Math.PI / 3.0,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "phase index must be 0, 1 or 2"),
    };
}

/// <summary>N167 (October 2026): the element kinds of a <see cref="MultiPhaseModel"/> beyond three-phase series-impedance
/// lines and constant-power wye loads (see <see cref="MultiPhaseModel.ElementsPresent"/>).</summary>
[Flags]
public enum MultiPhaseElement
{
    /// <summary>None: three-phase lines and constant-power wye loads only.</summary>
    None = 0,
    /// <summary>A single- or two-phase branch (lateral).</summary>
    PartialPhaseBranch = 1,
    /// <summary>A branch with a line-charging shunt (pi section).</summary>
    LineCharging = 2,
    /// <summary>A branch with an ideal step-voltage regulator.</summary>
    Regulator = 4,
    /// <summary>A shunt capacitor bank.</summary>
    Capacitor = 8,
    /// <summary>A constant-current or constant-impedance load (one part of a ZIP load).</summary>
    VoltageDependentLoad = 16,
    /// <summary>A delta-connected load.</summary>
    DeltaLoad = 32,
    /// <summary>A closed switch: a three-phase branch with zero series impedance.</summary>
    ClosedSwitch = 64,
    /// <summary>A delta / grounded-wye step-down transformer (1.95.251, N168).</summary>
    DeltaWyeTransformer = 128,
    /// <summary>An ungrounded-wye / delta or delta / delta transformer (1.95.253, N168): a three-wire secondary.</summary>
    DeltaSecondaryTransformer = 256,
    /// <summary>An open wye / open delta or open delta / open delta bank (N334, CF5): a three-wire open-delta secondary.</summary>
    OpenTransformer = 512,
}

/// <summary>N167: plain-English names of <see cref="MultiPhaseElement"/> sets, for refusal messages.</summary>
public static class MultiPhaseElements
{
    /// <summary>The kinds in <paramref name="e"/> as a readable list ("regulators and shunt capacitors"); "none".</summary>
    public static string Describe(MultiPhaseElement e)
    {
        var parts = new List<string>();
        if ((e & MultiPhaseElement.PartialPhaseBranch) != 0) parts.Add("single- or two-phase branches");
        if ((e & MultiPhaseElement.LineCharging) != 0) parts.Add("line charging");
        if ((e & MultiPhaseElement.Regulator) != 0) parts.Add("step-voltage regulators");
        if ((e & MultiPhaseElement.Capacitor) != 0) parts.Add("shunt capacitors");
        if ((e & MultiPhaseElement.VoltageDependentLoad) != 0) parts.Add("constant-current or constant-impedance (ZIP) loads");
        if ((e & MultiPhaseElement.DeltaLoad) != 0) parts.Add("delta loads");
        if ((e & MultiPhaseElement.ClosedSwitch) != 0) parts.Add("closed switches (zero-impedance branches)");
        if ((e & MultiPhaseElement.DeltaWyeTransformer) != 0) parts.Add("delta / grounded-wye transformers");
        if ((e & MultiPhaseElement.DeltaSecondaryTransformer) != 0) parts.Add("wye / delta and delta / delta transformers");
        if ((e & MultiPhaseElement.OpenTransformer) != 0) parts.Add("open wye / open delta and open delta / open delta transformers");
        return parts.Count switch
        {
            0 => "none",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }
}

/// <summary>Helpers between the <see cref="Phase"/> flags and the 0 / 1 / 2 phase indices the solvers use (N062).</summary>
public static class PhaseIndex
{
    /// <summary>The flag of phase index <paramref name="p"/> (0 = a, 1 = b, 2 = c).</summary>
    public static Phase Flag(int p) => p switch
    {
        0 => Phase.A,
        1 => Phase.B,
        2 => Phase.C,
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "a phase index is 0, 1 or 2."),
    };

    /// <summary>True when <paramref name="phases"/> includes phase index <paramref name="p"/>.</summary>
    public static bool Has(Phase phases, int p) => (phases & Flag(p)) != 0;

    /// <summary>The phases as lower-case letters in a, b, c order ("abc", "ac", "b"); "none" for no phase.</summary>
    public static string Name(Phase phases)
    {
        string s = (Has(phases, 0) ? "a" : "") + (Has(phases, 1) ? "b" : "") + (Has(phases, 2) ? "c" : "");
        return s.Length == 0 ? "none" : s;
    }
}
