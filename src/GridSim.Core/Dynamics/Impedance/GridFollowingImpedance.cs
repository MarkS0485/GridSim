using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Impedance;

/// <summary>
/// The small-signal dq admittance of a grid-following (current-source-behind-PLL) converter, derived from its
/// authored control law plus the inner-loop parameters the behavioural time-domain model lacks. This is the
/// device whose interaction with a weak grid produces the classic PLL / sub-synchronous instability the screen
/// is built to catch - a phenomenon that is IDENTICALLY ABSENT from the behavioural P-f droop model
/// (<see cref="Assets.GridFollowingInverter"/> has no PLL, no current loop), so it is genuinely new physics.
///
/// <para><b>Transfers.</b> The PLL is the STANDARD SRF-PLL closed loop from the terminal q-axis voltage
/// perturbation to the estimated angle:
/// <c>G_pll(s) = (k_p s + k_i) / (s^2 + Vd0 k_p s + Vd0 k_i)</c>, with the PI gains derived from the natural
/// frequency <c>wn = 2 pi f_bw</c> and damping <c>zeta</c> so the closed-loop denominator is
/// <c>s^2 + 2 zeta wn s + wn^2</c>: <c>k_p = 2 zeta wn / Vd0</c>, <c>k_i = wn^2 / Vd0</c>. There is NO free
/// integrator - <c>G_pll(0) = 1/Vd0</c> is finite - so the return ratio has no pole at the origin and the
/// minor-loop <c>P = 0</c> assumption holds (no Nyquist-contour indentation needed). The current loop is
/// <c>G_ci(s) = w_ci / (s + w_ci)</c>, or unity when the detail level omits it.</para>
///
/// <para><b>Full 2x2 admittance (drawn), system dq frame.</b> A constant-P/Q outer loop sets the
/// converter-frame current reference from the measured d-axis voltage, and the PLL rotates both the reference
/// and the injected current; the two mechanisms populate all four entries:
/// <code>
/// Y_dd =  G_ci * Id0/Vd0        Y_dq =  Iq0 * G_pll
/// Y_qd =  G_ci * Iq0/Vd0        Y_qq = -Id0 * G_pll
/// </code>
/// Both off-diagonals are proportional to <c>Iq0</c>, so a unity-power-factor unit (<c>Iq0 = 0</c>) decouples
/// - the honest decoupled limit the property test asserts. The <c>-Id0 * G_pll</c> term gives <c>Y_qq</c> a
/// NEGATIVE real part around the PLL bandwidth: the negative-resistance band that destabilises a weak grid.</para>
///
/// <para><b>Positive-sequence reduction.</b> The modified-sequence (Rygg/Molinas) transform
/// <c>Y_pp = 1/2[(Y_dd + Y_qq) + j(Y_qd - Y_dq)]</c>, which for this matrix collapses to the clean
/// <c>Y_pp = 1/2 (G_ci/Vd0 - G_pll)(Id0 + j Iq0)</c>.</para>
/// </summary>
public sealed class GridFollowingImpedance : IConverterImpedanceModel
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultCurrentLoopBandwidthHz = 0.0;   // AssetParams.CurrentLoopBandwidthHz

    /// <summary>A fast first-order current loop (Hz) standing in for an "ideal" current loop at the PllOuter
    /// detail level. A finite roll-off is both physical (real current loops roll off) and necessary for
    /// properness: it makes <c>Y_conv -&gt; 0</c> at high frequency, so the return ratio is bounded and the
    /// Nyquist contour closes at infinity with no arc contribution.</summary>
    public const double IdealCurrentLoopHz = 1000.0;

    private readonly double _kp;         // SRF-PLL proportional gain (derived)
    private readonly double _ki;         // SRF-PLL integral gain (derived)
    private readonly double _wci;        // current-loop bandwidth (rad/s), always > 0 (roll-off for properness)
    private readonly bool _idealPll;     // OuterLoopOnly: G_pll frozen at its DC value 1/Vd0
    private readonly double _vd0;
    private readonly double _id0;
    private readonly double _iq0;

    private GridFollowingImpedance(int bus, ImpedanceModelDetail detail, ProvenanceTag provenance,
        ConverterOperatingPoint op, double kp, double ki, double wci, bool idealPll)
    {
        Bus = bus;
        Detail = detail;
        Provenance = provenance;
        OperatingPoint = op;
        _kp = kp; _ki = ki; _wci = wci; _idealPll = idealPll;
        _vd0 = op.Vd0Pu; _id0 = op.Id0Pu; _iq0 = op.Iq0Pu;
    }

    /// <inheritdoc/>
    public int Bus { get; }
    /// <inheritdoc/>
    public AssetKind Kind => AssetKind.GridFollowingInverter;
    /// <inheritdoc/>
    public ImpedanceModelDetail Detail { get; }
    /// <inheritdoc/>
    public ProvenanceTag Provenance { get; }
    /// <inheritdoc/>
    public ConverterOperatingPoint OperatingPoint { get; }

    /// <summary>The derived SRF-PLL PI gains (for export / cross-validation).</summary>
    public (double Kp, double Ki) PllGains => (_kp, _ki);

    /// <summary>The current-loop roll-off bandwidth (rad/s).</summary>
    public double CurrentLoopWci => _wci;

    /// <summary>True when the PLL is ideal (OuterLoopOnly): <c>G_pll = 1/Vd0</c> constant.</summary>
    public bool IsIdealPll => _idealPll;

    /// <summary>
    /// Build a GFL admittance model from an authored asset class. Reads the existing behavioural params for
    /// provenance context, and the NEW opt-in inner-loop params (<see cref="AssetParams.PllBandwidthHz"/>,
    /// <see cref="AssetParams.PllDampingRatio"/>, <see cref="AssetParams.CurrentLoopBandwidthHz"/>). Absent
    /// PLL params degrade to a literature-typical placeholder bandwidth, carried as
    /// <see cref="ProvenanceTag.CuratedPlaceholder"/>.
    ///
    /// <para><b>Overrides (R244).</b> Each inner-loop parameter can be overridden independently; an override
    /// replaces only its own parameter, so overriding the PLL bandwidth keeps the class's authored damping
    /// (it used to force 0.707). Any override marks the model <see cref="ProvenanceTag.CuratedPlaceholder"/>:
    /// a typed-in value is not a recorded measurement, whatever the class's own provenance says. An
    /// override must be a finite number &gt; 0 and fails loud otherwise.</para>
    /// </summary>
    public static GridFollowingImpedance FromAssetClass(
        AssetClass cls, int bus, ConverterOperatingPoint op, ImpedanceModelDetail detail,
        double? pllBandwidthHzOverride = null, double? currentLoopBandwidthHzOverride = null,
        double? pllDampingOverride = null)
    {
        ArgumentNullException.ThrowIfNull(cls);
        RequirePositiveOverride(pllBandwidthHzOverride, nameof(pllBandwidthHzOverride));
        RequirePositiveOverride(currentLoopBandwidthHzOverride, nameof(currentLoopBandwidthHzOverride));
        RequirePositiveOverride(pllDampingOverride, nameof(pllDampingOverride));
        bool anyOverride = pllBandwidthHzOverride is not null || currentLoopBandwidthHzOverride is not null
            || pllDampingOverride is not null;

        double bwHz = pllBandwidthHzOverride ?? cls.Param(AssetParams.PllBandwidthHz, 0.0);
        // Recorded only when the bandwidth actually used came from the class's recorded data and nothing
        // was overridden; a CLI-typed value must never inherit the class's Recorded tag.
        bool measured = !anyOverride && bwHz > 0.0
            && cls.ProvenanceFor(AssetParams.PllBandwidthHz).Tag is ProvenanceTag.Recorded or ProvenanceTag.DerivedFromRecorded;
        // R138 (stream B5): the one grid-following PLL default the EMT and small-signal models also use (20 Hz; was a
        // separate 5 Hz placeholder here). CuratedPlaceholder [VERIFY].
        if (bwHz <= 0.0) bwHz = ConverterDefaults.PllBandwidthHz;
        double zeta = pllDampingOverride ?? cls.Param(AssetParams.PllDampingRatio, ConverterDefaults.PllDampingRatio);
        if (zeta <= 0.0) zeta = ConverterDefaults.PllDampingRatio;   // absent / unauthored class damping: literature-typical placeholder

        double ciHz = currentLoopBandwidthHzOverride ?? cls.Param(AssetParams.CurrentLoopBandwidthHz, DefaultCurrentLoopBandwidthHz);
        double wci = detail == ImpedanceModelDetail.PllCurrentOuter && ciHz > 0.0 ? 2.0 * Math.PI * ciHz : 0.0;

        return FromParameters(bus, op, detail,
            measured ? ProvenanceTag.Recorded : ProvenanceTag.CuratedPlaceholder,
            bwHz, zeta, wci);
    }

    /// <summary>An override, when supplied, must be a finite positive number (a zero or negative bandwidth or
    /// damping is not a physical control setting and used to be silently replaced).</summary>
    private static void RequirePositiveOverride(double? value, string name)
    {
        if (value is double v && (!double.IsFinite(v) || v <= 0.0))
            throw new ArgumentOutOfRangeException(name, v, $"{name} must be a finite number > 0 when supplied.");
    }

    /// <summary>Build directly from numeric inner-loop parameters (used by the analytic oracle and tests):
    /// PLL bandwidth (Hz), damping ratio, current-loop bandwidth (rad/s; &lt;=0 = ideal).</summary>
    public static GridFollowingImpedance FromParameters(
        int bus, ConverterOperatingPoint op, ImpedanceModelDetail detail, ProvenanceTag provenance,
        double pllBandwidthHz, double pllDamping, double currentLoopWci)
    {
        double wn = 2.0 * Math.PI * Math.Max(ConverterDefaults.ImpedancePllParameterFloor, pllBandwidthHz);
        double zeta = Math.Max(ConverterDefaults.ImpedancePllParameterFloor, pllDamping);
        double vd0 = op.Vd0Pu;   // already guarded >= MinVd0Pu by ConverterOperatingPoint
        double kp = 2.0 * zeta * wn / vd0;
        double ki = wn * wn / vd0;
        // The current loop always rolls off (properness): PllCurrentOuter uses the authored bandwidth, the
        // other levels a fast default standing in for an ideal loop.
        double wci = detail == ImpedanceModelDetail.PllCurrentOuter && currentLoopWci > 0.0
            ? currentLoopWci
            : 2.0 * Math.PI * IdealCurrentLoopHz;
        bool idealPll = detail == ImpedanceModelDetail.OuterLoopOnly;
        return new GridFollowingImpedance(bus, detail, provenance, op, kp, ki, wci, idealPll);
    }

    /// <summary>The SRF-PLL closed-loop transfer <c>G_pll(jw)</c> (v_q -&gt; estimated angle). At the
    /// OuterLoopOnly detail level the PLL is ideal and this is frozen at its DC value <c>1/Vd0</c>.</summary>
    public Complex PllTransfer(double omega)
    {
        if (_idealPll) return new Complex(1.0 / _vd0, 0.0);
        Complex s = new(0.0, omega);
        Complex num = (_kp * s) + _ki;
        Complex den = (s * s) + (_vd0 * _kp * s) + (_vd0 * _ki);
        return num / den;
    }

    /// <summary>The current-loop roll-off <c>G_ci(jw) = w_ci/(s+w_ci)</c> (always a finite roll-off).</summary>
    public Complex CurrentLoopTransfer(double omega)
    {
        Complex s = new(0.0, omega);
        return _wci / (s + _wci);
    }

    /// <inheritdoc/>
    public Complex2x2 AdmittanceDq(double omega)
    {
        Complex gPll = PllTransfer(omega);
        Complex gCi = CurrentLoopTransfer(omega);
        Complex ydd = gCi * (_id0 / _vd0);
        Complex ydq = _iq0 * gPll;
        Complex yqd = gCi * (_iq0 / _vd0);
        Complex yqq = -_id0 * gPll;
        return new Complex2x2(ydd, ydq, yqd, yqq);
    }

    /// <inheritdoc/>
    public Complex PositiveSequenceAdmittance(double omega)
    {
        // Modified-sequence transform Y_pp = 1/2[(Y_dd+Y_qq) + j(Y_qd - Y_dq)].
        Complex2x2 y = AdmittanceDq(omega);
        return 0.5 * ((y.M11 + y.M22) + (Complex.ImaginaryOne * (y.M21 - y.M12)));
    }
}
