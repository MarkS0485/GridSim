using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Impedance;

/// <summary>
/// The small-signal dq admittance of a grid-forming converter - a voltage source behind a virtual impedance
/// with a power-synchronisation loop. Where the grid-following unit presents a negative-resistance band that
/// can trip a weak grid, the GFM presents a POSITIVE-REAL (passive) impedance over the sub-synchronous band,
/// which is why a grid-forming fleet stabilises where a grid-following fleet destabilises - reproduced here as
/// a MEASURED impedance property, not an assertion.
///
/// <para><b>Inertia invariant.</b> The synthetic-inertia coefficient (<see cref="AssetParams.SynInertiaMwsPerHz"/>)
/// enters ONLY as a labelled control transfer inside the power-loop impedance <c>Z_p(s)</c>; it is never summed
/// into a swing energy E. The (1/2)Jw^2-vs-df/dt separation is preserved by construction - this model is
/// small-signal about a fixed operating point and touches no machine H or EMF.</para>
///
/// <para><b>Impedance.</b> The virtual impedance is an RL element in the dq frame,
/// <c>Z_v(s) = (R_v + s L_v) I + w0 L_v J</c> (<c>L_v = X_v / w0</c>, <c>J = [[0,-1],[1,0]]</c>); the power
/// loop adds a first-order synchronising resistance <c>Z_p(s) = D_p / (1 + s tau_p)</c> on the diagonal
/// (droop <c>D_p</c> from <c>dampingMwPerHz</c>, time constant from the power-loop bandwidth and the labelled
/// synthetic inertia). The admittance is the 2x2 inverse of the total.</para>
///
/// <para><b>Per-unit power loop (R140, October 2026 review; stream B5; Ruling R1: Mark delegated EE calls
/// 2026-10-04).</b> Every quantity of the loop is now on the converter's own rating S (MVA), the base the impedance
/// itself is on. The grid-forming law is a P-f droop with a virtual inertia, which in per unit on S reads
/// <c>2 H_v d(dw)/dt = -dp - k_d dw</c> with the per-unit droop gain <c>k_d = D f0 / S</c> (D the damping in MW/Hz) and
/// the virtual inertia constant <c>H_v = J f0 / (2 S)</c> (J the labelled synthetic inertia in MW.s/Hz; the same
/// <c>Ev = J f0 / 2</c> MW.s the frequency model's former uses, <see cref="Forming.FormingAggregate.FromFleet"/>). As a
/// frequency response to power this is <c>dw = -m_p dp / (1 + s 2 H_v m_p)</c> with the per-unit droop
/// <c>m_p = 1 / k_d = S / (D f0)</c>: a droop with a first-order lag, the VSM / droop equivalence of D'Arco and Suul
/// ("Equivalence of virtual synchronous machines and frequency-droops for converter-based microgrids", IEEE Trans.
/// Smart Grid 5(1), 2014, pp. 394-395). So the loop's synchronising resistance is the per-unit droop,
/// <c>D_p = m_p</c> (linearised at unit voltage), and its time constant is the power-loop filter
/// <c>1 / (2 pi f_pl)</c> plus the inertia lag <c>2 H_v m_p</c>, which with an authored damping is <c>J / D</c> seconds
/// (rating- and frequency-free, as a time constant must be). Before R140 the code used <c>D_p = 0.01 D</c> (MW/Hz read
/// straight as pu, so a 1000 MW/Hz fleet got 10 pu whatever its size) and <c>tau += 1e-4 J</c> (MW.s/Hz read as
/// seconds); both scaled with the absolute MW size of the unit instead of its per-unit control law. A unit with no
/// authored damping keeps the placeholder <see cref="ConverterDefaults.GfmDefaultSyncResistancePu"/> as its per-unit
/// droop (unchanged), and a unit with neither damping nor synthetic inertia (every flag-built model) is bit-identical to
/// before.</para>
/// </summary>
public sealed class GridFormingImpedance : IConverterImpedanceModel
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultDampingMwPerHz = 0.0;   // AssetParams.DampingMwPerHz
    internal const double DefaultPowerLoopBandwidthHz = 0.0;   // AssetParams.PowerLoopBandwidthHz
    internal const double DefaultSynInertiaMwsPerHz = 0.0;   // AssetParams.SynInertiaMwsPerHz
    internal const double DefaultVirtualReactancePu = 0.0;   // AssetParams.VirtualReactancePu

    private readonly double _rv;      // virtual resistance (pu)
    private readonly double _lv;      // virtual inductance (pu-s = X_v/w0)
    private readonly double _w0;      // nominal angular frequency (rad/s)
    private readonly double _dp;      // power-loop synchronising resistance (pu)
    private readonly double _taup;    // power-loop time constant (s)

    private GridFormingImpedance(int bus, ImpedanceModelDetail detail, ProvenanceTag provenance,
        ConverterOperatingPoint op, double rv, double lv, double w0, double dp, double taup)
    {
        Bus = bus;
        Detail = detail;
        Provenance = provenance;
        OperatingPoint = op;
        _rv = rv; _lv = lv; _w0 = w0; _dp = dp; _taup = taup;
    }

    /// <inheritdoc/>
    public int Bus { get; }
    /// <inheritdoc/>
    public AssetKind Kind => AssetKind.GridFormingInverter;
    /// <inheritdoc/>
    public ImpedanceModelDetail Detail { get; }
    /// <inheritdoc/>
    public ProvenanceTag Provenance { get; }
    /// <inheritdoc/>
    public ConverterOperatingPoint OperatingPoint { get; }

    /// <summary>
    /// Build a GFM admittance model from an authored class: the labelled synthetic inertia and droop
    /// (<see cref="AssetParams.SynInertiaMwsPerHz"/>, <see cref="AssetParams.DampingMwPerHz"/>), plus the NEW
    /// opt-in <see cref="AssetParams.VirtualReactancePu"/> and <see cref="AssetParams.PowerLoopBandwidthHz"/>.
    /// <paramref name="ratingMva"/> is the converter's rating (MVA), the base of the operating point and of the
    /// impedance, on which the damping and the synthetic inertia are turned into a per-unit droop and a virtual inertia
    /// constant (R140; see the class remarks).
    /// </summary>
    public static GridFormingImpedance FromAssetClass(
        AssetClass cls, int bus, double f0Hz, ConverterOperatingPoint op, ImpedanceModelDetail detail, double ratingMva,
        double? virtualReactancePuOverride = null, double? powerLoopBandwidthHzOverride = null)
    {
        double xv = virtualReactancePuOverride ?? cls.Param(AssetParams.VirtualReactancePu, DefaultVirtualReactancePu);
        bool measuredXv = xv > 0.0
            && cls.ProvenanceFor(AssetParams.VirtualReactancePu).Tag is ProvenanceTag.Recorded or ProvenanceTag.DerivedFromRecorded;
        if (xv <= 0.0) xv = ConverterDefaults.GfmVirtualReactancePu;   // literature-typical GFM virtual reactance (pu), CuratedPlaceholder [VERIFY]

        double plHz = powerLoopBandwidthHzOverride ?? cls.Param(AssetParams.PowerLoopBandwidthHz, DefaultPowerLoopBandwidthHz);
        if (plHz <= 0.0) plHz = ConverterDefaults.GfmPowerLoopBandwidthHz;   // literature-typical power-loop bandwidth (Hz), CuratedPlaceholder [VERIFY]

        double damping = cls.Param(AssetParams.DampingMwPerHz, DefaultDampingMwPerHz);
        double syn = cls.Param(AssetParams.SynInertiaMwsPerHz, DefaultSynInertiaMwsPerHz);   // labelled synthetic; never touches E

        return FromParameters(bus, f0Hz, op, detail,
            measuredXv ? ProvenanceTag.Recorded : ProvenanceTag.CuratedPlaceholder,
            xv, plHz, damping, syn, ratingMva);
    }

    /// <summary>Build directly from numeric parameters (used by tests): virtual reactance (pu on the rating), power-loop
    /// bandwidth (Hz), damping (MW/Hz), labelled synthetic inertia (MW.s/Hz) and the converter's rating (MVA), the base
    /// the damping and the synthetic inertia are expressed on (R140). Fails loud
    /// (<see cref="ArgumentOutOfRangeException"/>) on a rating or nominal frequency that is not a finite number above zero
    /// and on a damping or synthetic inertia that is negative or not finite (each used to be read silently: a negative
    /// damping fell back to the placeholder, a NaN went into the loop).</summary>
    public static GridFormingImpedance FromParameters(
        int bus, double f0Hz, ConverterOperatingPoint op, ImpedanceModelDetail detail, ProvenanceTag provenance,
        double virtualReactancePu, double powerLoopBandwidthHz, double dampingMwPerHz, double synInertiaMwsPerHz,
        double ratingMva)
    {
        if (!double.IsFinite(ratingMva) || !(ratingMva > 0.0))
            throw new ArgumentOutOfRangeException(nameof(ratingMva), ratingMva,
                "The grid-forming converter's rating (MVA) must be a finite number above zero: it is the base of the per-unit power loop.");
        if (!double.IsFinite(f0Hz) || !(f0Hz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(f0Hz), f0Hz, "The nominal frequency must be a finite number of Hz above zero.");
        if (!double.IsFinite(dampingMwPerHz) || dampingMwPerHz < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dampingMwPerHz), dampingMwPerHz,
                "The grid-forming damping (MW/Hz) must be a finite number, zero (none authored) or above.");
        if (!double.IsFinite(synInertiaMwsPerHz) || synInertiaMwsPerHz < 0.0)
            throw new ArgumentOutOfRangeException(nameof(synInertiaMwsPerHz), synInertiaMwsPerHz,
                "The grid-forming synthetic inertia (MW.s/Hz) must be a finite number, zero (none authored) or above.");
        // GR-561: the virtual reactance used to be taken as given. Zero or negative makes L_v and R_v zero or negative,
        // so the impedance can be singular or active, and a NaN went straight into the loop; refuse them. The power-loop
        // bandwidth is still clamped from below (GfmMinPowerLoopBandwidthHz) but must be a number: Math.Max passes NaN on.
        if (!double.IsFinite(virtualReactancePu) || !(virtualReactancePu > 0.0))
            throw new ArgumentOutOfRangeException(nameof(virtualReactancePu), virtualReactancePu,
                "The grid-forming virtual reactance (pu on the rating) must be a finite number above zero.");
        if (double.IsNaN(powerLoopBandwidthHz))
            throw new ArgumentOutOfRangeException(nameof(powerLoopBandwidthHz), powerLoopBandwidthHz,
                "The grid-forming power-loop bandwidth (Hz) must be a number.");

        double w0 = 2.0 * Math.PI * f0Hz;
        double lv = virtualReactancePu / w0;
        double rv = ConverterDefaults.GfmVirtualROverX * virtualReactancePu;   // small X/R ~ 20 default virtual resistance

        // Power-loop synchronising resistance = the per-unit droop m_p = S / (D f0) (R140: pu frequency per pu power, on
        // the converter's rating). A positive-real term, so the GFM stays passive. No damping authored: the placeholder
        // per-unit droop, exactly the old default.
        double dp = dampingMwPerHz > 0.0
            ? ratingMva / (dampingMwPerHz * f0Hz)
            : ConverterDefaults.GfmDefaultSyncResistancePu;

        // Time constant: the power-loop filter 1 / (2 pi f_pl) plus the virtual-inertia lag 2 H_v m_p, with the virtual
        // inertia constant H_v = J f0 / (2 S) seconds (R140). With an authored damping 2 H_v m_p = J / D exactly in exact
        // arithmetic; it is computed in the general form so the placeholder droop is covered by the same expression. No
        // synthetic inertia: the old filter-only time constant, operand for operand.
        double inertiaLagS = 0.0;
        if (synInertiaMwsPerHz > 0.0)
        {
            double hVirtualS = synInertiaMwsPerHz * f0Hz / (2.0 * ratingMva);   // H_v (s) on the converter's rating
            inertiaLagS = 2.0 * hVirtualS * dp;
        }
        double taup = Math.Max(ConverterDefaults.GfmMinPowerLoopTauSeconds,
            (1.0 / (2.0 * Math.PI * Math.Max(ConverterDefaults.GfmMinPowerLoopBandwidthHz, powerLoopBandwidthHz)))
            + inertiaLagS);
        return new GridFormingImpedance(bus, detail, provenance, op, rv, lv, w0, dp, taup);
    }

    /// <summary>The dq virtual+power-loop impedance <c>Z(jw)</c> (2x2, pu).</summary>
    public Complex2x2 ImpedanceDq(double omega)
    {
        Complex s = new(0.0, omega);
        Complex zRl = _rv + (s * _lv);              // R_v + jw L_v
        Complex zp = _dp / (1.0 + (s * _taup));     // first-order power-loop synchronising resistance
        Complex diag = zRl + zp;
        Complex off = _w0 * _lv;                     // dq cross-coupling of the inductance (w0 L_v)
        return new Complex2x2(diag, -off, off, diag);
    }

    /// <inheritdoc/>
    public Complex2x2 AdmittanceDq(double omega)
    {
        Complex2x2? inv = ImpedanceDq(omega).Inverse();
        // FromParameters refuses a virtual reactance that is not above zero, so R_v > 0 and the synchronising resistance
        // D_p > 0 keep the diagonal's real part positive and Z is never singular. GR-561: should it ever be, fail loud;
        // the old fallback (a zero admittance, an open circuit) leaned the stability verdict toward STABLE.
        return inv ?? throw new InvalidOperationException(
            $"grid-forming impedance at bus {Bus}: the dq impedance is singular at omega = {omega.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} rad/s.");
    }

    /// <inheritdoc/>
    public Complex PositiveSequenceAdmittance(double omega)
    {
        Complex2x2 y = AdmittanceDq(omega);
        return 0.5 * ((y.M11 + y.M22) + (Complex.ImaginaryOne * (y.M21 - y.M12)));
    }
}
