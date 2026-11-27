namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// PSS2C, the IEEE 421.5-2016 dual-input stabiliser: PSS2B with a fourth lead-lag stage T12/T13 and an output logic that
/// switches the stabiliser on and off with the unit's electrical power. Built from <see cref="Pss2"/> (PSS2B) without
/// changing it:
/// <code>
///   y   = PSS2B(u1, u2) without its output limits            (the PSS2B chain to the T10/T11 stage)
///   z   = leadlag_{T12/T13}(y)                                 (T12 = T13 = 0 bypasses it)
///   Vs  = clamp(z, VSTMIN, VSTMAX)    while the output logic is on, else 0
/// </code>
/// Output logic (optional; off when no thresholds are given): the stabiliser is on when the electrical power Pe (pu on the
/// machine base) rises above PPSSon and off when it falls below PPSSoff (PPSSoff at most PPSSon, a hysteresis band). At the
/// first step it is on when Pe is at or above PPSSon. The chain keeps running while the output is off, so switching on
/// passes its present value. With the stage bypassed and no output logic, PSS2C gives PSS2B's output bit for bit: the inner
/// PSS2B's own clamp is unlimited, and clamp(y, VSTMIN, VSTMAX) here is the same operation PSS2B applies.
/// <para>Not modelled: the 2016 options of other input signals (GridSim measures rotor speed deviation and electrical power).</para>
/// </summary>
public sealed class Pss2c : IRmsStabiliser
{
    private readonly Pss2 _inner;
    private readonly LeadLag? _ll4;
    private readonly double _vstMin, _vstMax;
    private readonly double _pOn, _pOff;
    private readonly bool _logic;
    private bool _on, _pending = true;

    /// <summary>PSS2C: the PSS2B values in PSS/E PSS2B order, then T12 and T13, then the optional output-logic thresholds
    /// (pu of the machine base; both NaN, the default, for no logic).</summary>
    public Pss2c(int ics1, int ics2, int m, int n, double tw1, double tw2, double t6, double tw3, double tw4, double t7,
        double ks2, double ks3, double t8, double t9, double ks1, double t1, double t2, double t3, double t4, double t10, double t11,
        double vs1Max, double vs1Min, double vs2Max, double vs2Min, double vstMax, double vstMin, double t12, double t13,
        double pssOnPu = double.NaN, double pssOffPu = double.NaN)
    {
        const string who = "PSS2C";
        LibraryChecks.Limits(who, "VSTMIN", vstMin, "VSTMAX", vstMax);
        _inner = Pss2.Pss2b(ics1, ics2, m, n, tw1, tw2, t6, tw3, tw4, t7, ks2, ks3, t8, t9, ks1, t1, t2, t3, t4, t10, t11,
            vs1Max, vs1Min, vs2Max, vs2Min, double.PositiveInfinity, double.NegativeInfinity);
        _ll4 = LibraryChecks.Stage(who, t12, "T12", t13, "T13");
        _vstMin = vstMin; _vstMax = vstMax;
        bool onGiven = !double.IsNaN(pssOnPu), offGiven = !double.IsNaN(pssOffPu);
        if (onGiven != offGiven) throw new ArgumentException("PSS2C: the output logic needs both PPSSon and PPSSoff, or neither.", nameof(pssOffPu));
        if (onGiven)
        {
            if (!double.IsFinite(pssOnPu) || !double.IsFinite(pssOffPu) || !(pssOffPu <= pssOnPu))
                throw new ArgumentException(FormattableString.Invariant($"PSS2C: PPSSoff must be finite and at most PPSSon (got PPSSon = {pssOnPu}, PPSSoff = {pssOffPu})."), nameof(pssOffPu));
        }
        _logic = onGiven;
        _pOn = pssOnPu; _pOff = pssOffPu;
    }

    /// <inheritdoc/>
    public string Kind => "PSS2C";

    /// <summary>True when an input is electrical power or the output logic is set.</summary>
    public bool NeedsTerminalQuantities => _inner.NeedsTerminalQuantities || _logic;

    /// <summary>True when an input is electrical power (the washouts start at the first step's value) or the output logic
    /// is set (it is switched on or off from the first step's power).</summary>
    public bool ReadsNetworkInputsAtStart => _inner.ReadsNetworkInputsAtStart || _logic;

    /// <summary>The output after the last step.</summary>
    public double Output { get; private set; }

    /// <summary>True while the output logic has the stabiliser on (always true with no logic).</summary>
    public bool IsOn => !_logic || _on;

    /// <inheritdoc/>
    public void Initialize()
    {
        _inner.Initialize();
        _pending = true;
        Output = 0.0;
    }

    /// <inheritdoc/>
    public double Step(FieldControlInputs inputs, double dtSeconds)
    {
        double y = _inner.Step(inputs, dtSeconds);
        if (_pending)
        {
            _ll4?.Initialize(0.0);
            if (_logic)
            {
                double pe0 = inputs.ElectricalPowerPu;
                if (!double.IsFinite(pe0)) throw new InvalidOperationException("PSS2C: the output logic reads the electrical power; the caller must use the step that supplies it.");
                _on = pe0 >= _pOn;
            }
            _pending = false;
        }
        double z = _ll4 is null ? y : _ll4.Step(y, dtSeconds);
        if (_logic)
        {
            double pe = inputs.ElectricalPowerPu;
            if (!double.IsFinite(pe)) throw new InvalidOperationException("PSS2C: the output logic reads the electrical power; the caller must use the step that supplies it.");
            if (_on && pe < _pOff) _on = false;
            else if (!_on && pe > _pOn) _on = true;
        }
        Output = IsOn ? Math.Clamp(z, _vstMin, _vstMax) : 0.0;
        return Output;
    }
}
