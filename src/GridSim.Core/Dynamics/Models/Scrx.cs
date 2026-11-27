namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// SCRX, the bus-fed or solid-fed static exciter of PSS/E (data TA/TB TB K TE EMIN EMAX CSWITCH rc/rfd). Structure (pu on
/// the machine base):
/// <code>
///   v1   = leadlag_{TA/TB}(Vref - Vt + Vs)                (TA = TB = 0 bypasses it)
///   x    = lag_{K, TE, [EMIN, EMAX]}(v1)                  (non-windup, as SEXS)
///   E    = x * Vt   (CSWITCH 0, bus fed)   or   x   (CSWITCH 1, solid fed)
///   Efd  = E, except when rc/rfd is above 0 and IFD is below 0: Efd = -rc/rfd * IFD
/// </code>
/// rc/rfd above 0 is a rectifier that cannot carry negative field current: the field then discharges through the crowbar
/// resistor rc. rc/rfd = 0 is a bridge with negative current capability, so the output is used as it is.
///
/// <para>Steady state: solid fed, x0 = Efd0 and the regulator error is Efd0 / K, set by <see cref="Initialize"/>. Bus fed,
/// x0 = Efd0 / Vt0 needs the terminal voltage, so the first step sets the states from it and completes the voltage
/// reference: the error the kernel passes is offset by x0 / K - (its first error), which is the reference
/// Vref = Vt0 + x0 / K. x0 outside EMIN..EMAX fails loud.</para>
/// </summary>
public sealed class Scrx : IRmsExciterModel
{
    private readonly double _k, _crowbar;
    private readonly bool _busFed;
    private readonly LeadLag? _shaping;
    private readonly FirstOrderLag _exciter;
    private double _efd0, _bias;
    private bool _pending;

    /// <param name="leadSeconds">TA (s), 0 or above.</param>
    /// <param name="lagSeconds">TB (s), 0 or above; TA = TB = 0 bypasses the lead-lag.</param>
    /// <param name="gain">K, above 0.</param>
    /// <param name="exciterTimeSeconds">TE (s), 0 or above.</param>
    /// <param name="fieldMinPu">EMIN.</param>
    /// <param name="fieldMaxPu">EMAX.</param>
    /// <param name="busFed">True for CSWITCH = 0 (the output scales with Vt), false for CSWITCH = 1.</param>
    /// <param name="crowbarRatio">rc/rfd, 0 or above.</param>
    public Scrx(double leadSeconds, double lagSeconds, double gain, double exciterTimeSeconds, double fieldMinPu, double fieldMaxPu,
        bool busFed, double crowbarRatio)
    {
        LibraryChecks.Positive("SCRX", "K", gain);
        LibraryChecks.NonNegative("SCRX", "TE", exciterTimeSeconds);
        LibraryChecks.NonNegative("SCRX", "rc/rfd", crowbarRatio);
        LibraryChecks.Limits("SCRX", "EMIN", fieldMinPu, "EMAX", fieldMaxPu);
        _k = gain; _busFed = busFed; _crowbar = crowbarRatio;
        _shaping = LibraryChecks.Stage("SCRX", leadSeconds, "TA", lagSeconds, "TB");
        _exciter = new FirstOrderLag(gain, exciterTimeSeconds, fieldMinPu, fieldMaxPu);
    }

    /// <inheritdoc/>
    public string Kind => "SCRX";

    /// <summary>True when bus fed (CSWITCH = 0).</summary>
    public bool NeedsTerminalQuantities => _busFed;

    /// <inheritdoc/>
    public double EquilibriumErrorPu(double fieldVoltagePu) => fieldVoltagePu / _k;

    private void Settle(double x0)
    {
        ControlBlocks.RequireEquilibriumWithinLimits("SCRX", "exciter output (EMIN..EMAX)", x0, _exciter.Min, _exciter.Max, "fieldVoltagePu");
        _shaping?.Initialize(x0 / _k);
        _exciter.Initialize(x0);
    }

    /// <inheritdoc/>
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu)) throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), fieldVoltagePu, "SCRX: the initial field is not finite.");
        _efd0 = fieldVoltagePu;
        _bias = 0.0;
        _pending = _busFed;
        if (!_busFed) Settle(fieldVoltagePu);
    }

    /// <inheritdoc/>
    public double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, double dtSeconds)
    {
        double vt = inputs.TerminalVoltagePu;
        if (_busFed && !double.IsFinite(vt))
            throw new InvalidOperationException("SCRX bus fed (CSWITCH 0) reads the terminal voltage; the caller must use the step that supplies it.");
        if (_pending)
        {
            if (!(vt > 0)) throw new InvalidOperationException($"SCRX bus fed: the initial terminal voltage is {vt} pu, so no field can be held.");
            double x0 = _efd0 / vt;
            Settle(x0);
            _bias = x0 / _k - voltageErrorPu;
            _pending = false;
        }
        double e = voltageErrorPu + stabiliserPu + _bias;
        double v1 = _shaping is null ? e : _shaping.Step(e, dtSeconds);
        double x = _exciter.Step(v1, dtSeconds);
        double efd = _busFed ? x * vt : x;
        if (_crowbar > 0.0)
        {
            double ifd = inputs.FieldCurrentPu;
            if (!double.IsFinite(ifd)) throw new InvalidOperationException("SCRX with rc/rfd above 0 reads the field current; the caller must use the step that supplies it.");
            if (ifd < 0.0) efd = -_crowbar * ifd;
        }
        return efd;
    }
}
