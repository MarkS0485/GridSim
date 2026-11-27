namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// IEEEST, the IEEE standard stabiliser of PSS/E (ICONs ICS IB, data A1 A2 A3 A4 A5 A6 T1 T2 T3 T4 T5 T6 KS LSMAX LSMIN
/// VCU VCL), for the inputs GridSim measures: rotor speed deviation (ICS 1) or electrical power on the machine base
/// (ICS 3). Structure, in the PSS/E order:
/// <code>
///   f   = (1 + A5 s) / ((1 + A1 s)(1 + A3 s)) * u           (the filter with A2 = A4 = A6 = 0)
///   v   = leadlag_{T1/T2}(leadlag_{T3/T4}(f))               (a stage with both times 0 is bypassed)
///   w   = KS * sT5/(1 + sT6) * v  =  KS (T5/T6) * Washout_{T6}(v)
///   Vs  = clamp(w, LSMIN, LSMAX); Vs = 0 while VCU above 0 and Vt above VCU, or VCL above 0 and Vt below VCL
/// </code>
/// The filter is realised exactly with the library blocks: a lead-lag (A5 over A1, or over A3 when A1 = 0) and a lag.
/// The second-order terms A2, A4 and A6 have no block here, so they must be 0. The first step sets every state from its
/// input (the undisturbed operating point), so every stage sits at that input and the washout output is exactly 0.
/// </summary>
public sealed class Ieeest : IRmsStabiliser
{
    private readonly int _ics;
    private readonly LeadLag? _filterLead;
    private readonly FirstOrderLag? _filterLagA, _filterLagB;
    private readonly LeadLag? _stage1, _stage2;
    private readonly Washout _washout;
    private readonly double _gain, _min, _max, _vcu, _vcl;
    private bool _pending = true;

    /// <param name="inputCode">ICS: 1 rotor speed deviation, 3 electrical power (machine base).</param>
    /// <param name="a1">A1 (s), 0 or above.</param>
    /// <param name="a3">A3 (s), 0 or above.</param>
    /// <param name="a5">A5 (s), 0 or above; above 0 needs A1 or A3 above 0.</param>
    /// <param name="t1">T1 (s).</param>
    /// <param name="t2">T2 (s).</param>
    /// <param name="t3">T3 (s).</param>
    /// <param name="t4">T4 (s).</param>
    /// <param name="t5">T5 (s), 0 or above.</param>
    /// <param name="t6">T6 (s), above 0.</param>
    /// <param name="ks">KS.</param>
    /// <param name="lsMax">LSMAX.</param>
    /// <param name="lsMin">LSMIN.</param>
    /// <param name="vcu">VCU (pu); 0 ignores it.</param>
    /// <param name="vcl">VCL (pu); 0 ignores it.</param>
    public Ieeest(int inputCode, double a1, double a3, double a5, double t1, double t2, double t3, double t4, double t5, double t6,
        double ks, double lsMax, double lsMin, double vcu, double vcl)
    {
        if (inputCode is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(inputCode), inputCode, "IEEEST: GridSim measures rotor speed deviation (ICS 1) and electrical power (ICS 3) only.");
        LibraryChecks.NonNegative("IEEEST", "A1", a1);
        LibraryChecks.NonNegative("IEEEST", "A3", a3);
        LibraryChecks.NonNegative("IEEEST", "A5", a5);
        LibraryChecks.NonNegative("IEEEST", "T5", t5);
        LibraryChecks.Positive("IEEEST", "T6", t6);
        LibraryChecks.NonNegative("IEEEST", "VCU", vcu);
        LibraryChecks.NonNegative("IEEEST", "VCL", vcl);
        if (!double.IsFinite(ks)) throw new ArgumentOutOfRangeException(nameof(ks), ks, "IEEEST: KS must be finite.");
        LibraryChecks.Limits("IEEEST", "LSMIN", lsMin, "LSMAX", lsMax);
        _ics = inputCode;
        if (a5 > 0)
        {
            if (a1 > 0) { _filterLead = new LeadLag(a5, a1); _filterLagB = a3 > 0 ? new FirstOrderLag(1.0, a3) : null; }
            else if (a3 > 0) _filterLead = new LeadLag(a5, a3);
            else throw new ArgumentOutOfRangeException(nameof(a5), a5, "IEEEST: A5 above 0 with A1 = A3 = 0 is a pure derivative (improper).");
        }
        else
        {
            _filterLagA = a1 > 0 ? new FirstOrderLag(1.0, a1) : null;
            _filterLagB = a3 > 0 ? new FirstOrderLag(1.0, a3) : null;
        }
        _stage1 = LibraryChecks.Stage("IEEEST", t1, "T1", t2, "T2");
        _stage2 = LibraryChecks.Stage("IEEEST", t3, "T3", t4, "T4");
        _washout = new Washout(t6);
        _gain = ks * (t5 / t6);
        _min = lsMin; _max = lsMax; _vcu = vcu; _vcl = vcl;
    }

    /// <inheritdoc/>
    public string Kind => "IEEEST";

    /// <summary>True for the electrical power input or a voltage cut-off.</summary>
    public bool NeedsTerminalQuantities => _ics == 3 || _vcu > 0 || _vcl > 0;

    /// <summary>True for the electrical power input (ICS 3) only: the voltage cut-off is memoryless and sets no state.</summary>
    public bool ReadsNetworkInputsAtStart => _ics == 3;

    /// <summary>The output after the last step.</summary>
    public double Output { get; private set; }

    /// <inheritdoc/>
    public void Initialize() { _pending = true; Output = 0.0; }

    /// <inheritdoc/>
    public double Step(FieldControlInputs inputs, double dtSeconds)
    {
        double u = _ics == 1 ? inputs.SpeedDeviationPu : inputs.ElectricalPowerPu;
        if (!double.IsFinite(u))
            throw new InvalidOperationException($"IEEEST: its input (ICS {_ics}) is not available on this step; the caller must use the step that supplies the terminal quantities.");
        if (_pending)
        {
            _filterLead?.Initialize(u); _filterLagA?.Initialize(u); _filterLagB?.Initialize(u);
            _stage1?.Initialize(u); _stage2?.Initialize(u);
            _washout.Initialize(u);
            _pending = false;
        }
        double f = _filterLead is null ? u : _filterLead.Step(u, dtSeconds);
        f = _filterLagA is null ? f : _filterLagA.Step(f, dtSeconds);
        f = _filterLagB is null ? f : _filterLagB.Step(f, dtSeconds);
        double v = _stage1 is null ? f : _stage1.Step(f, dtSeconds);
        v = _stage2 is null ? v : _stage2.Step(v, dtSeconds);
        double w = _gain * _washout.Step(v, dtSeconds);
        double vs = Math.Clamp(w, _min, _max);
        if (_vcu > 0 || _vcl > 0)
        {
            double vt = inputs.TerminalVoltagePu;
            if (!double.IsFinite(vt)) throw new InvalidOperationException("IEEEST: the VCU / VCL cut-off reads the terminal voltage; the caller must use the step that supplies it.");
            if ((_vcu > 0 && vt > _vcu) || (_vcl > 0 && vt < _vcl)) vs = 0.0;
        }
        Output = vs;
        return vs;
    }
}
