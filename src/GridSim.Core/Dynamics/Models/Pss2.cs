namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The IEEE 421.5-2005 dual-input stabilisers PSS2A and PSS2B (PSS/E PSS2A: ICONs ICS1 IB1 ICS2 IB2 M N, data TW1 TW2 T6
/// TW3 TW4 T7 KS2 KS3 T8 T9 KS1 T1 T2 T3 T4 VSTMAX VSTMIN; PSS2B adds T10 T11 and the input limits VS1MAX VS1MIN VS2MAX
/// VS2MIN), for the inputs GridSim measures: rotor speed deviation (ICS 1) and electrical power on the machine base
/// (ICS 3). Structure:
/// <code>
///   n1  = lag_{T6}(Washout_{TW2}(Washout_{TW1}(clamp(u1, VS1MIN, VS1MAX))))     (PSS2A: no input limits)
///   n2  = lag_{KS2, T7}(Washout_{TW4}(Washout_{TW3}(clamp(u2, VS2MIN, VS2MAX))))
///   r   = [(1 + sT8) / (1 + sT9)^M]^N (n1 + KS3 n2)          (the ramp-tracking filter; M = 0 or N = 0 bypasses it)
///   y   = KS1 (r - n2), then leadlag_{T1/T2}, leadlag_{T3/T4} (and PSS2B leadlag_{T10/T11})
///   Vs  = clamp(y, VSTMIN, VSTMAX)
/// </code>
/// TW2 = 0 or TW4 = 0 bypasses that washout; a lag time 0 is its gain exactly; a lead-lag stage with both times 0 is
/// bypassed. The ramp-tracking filter is N cascades of a lead-lag T8/T9 and M - 1 lags T9, the exact product of the
/// transfer function with the library blocks (M times N at most 8, as PSS/E allows). The first step sets every state
/// from its inputs: each first washout holds its input, so every later state is 0 and Vs is exactly 0.
/// </summary>
public sealed class Pss2 : IRmsStabiliser
{
    private readonly bool _b;
    private readonly int _ics1, _ics2;
    private readonly Washout _w1, _w3;
    private readonly Washout? _w2, _w4;
    private readonly FirstOrderLag _lag6, _lag7;
    private readonly double _ks3, _ks1;
    private readonly IReadOnlyList<object> _ramp;   // LeadLag or FirstOrderLag, in order
    private readonly LeadLag? _ll1, _ll2, _ll3;
    private readonly double _vs1Min, _vs1Max, _vs2Min, _vs2Max, _vstMin, _vstMax;
    private bool _pending = true;

    private Pss2(bool b, int ics1, int ics2, int m, int n, double tw1, double tw2, double t6, double tw3, double tw4, double t7,
        double ks2, double ks3, double t8, double t9, double ks1, double t1, double t2, double t3, double t4, double t10, double t11,
        double vs1Max, double vs1Min, double vs2Max, double vs2Min, double vstMax, double vstMin)
    {
        string who = b ? "PSS2B" : "PSS2A";
        foreach ((int ics, string name) in new[] { (ics1, "ICS1"), (ics2, "ICS2") })
            if (ics is not (1 or 3)) throw new ArgumentOutOfRangeException(name, ics, $"{who}: GridSim measures rotor speed deviation (1) and electrical power (3) only; {name} is {ics}.");
        if (m < 0 || n < 0 || m * n > 8) throw new ArgumentOutOfRangeException(nameof(m), m, $"{who}: M and N must be 0 or above with M x N at most 8 (got M = {m}, N = {n}).");
        LibraryChecks.Positive(who, "TW1", tw1);
        LibraryChecks.Positive(who, "TW3", tw3);
        LibraryChecks.NonNegative(who, "TW2", tw2);
        LibraryChecks.NonNegative(who, "TW4", tw4);
        LibraryChecks.NonNegative(who, "T6", t6);
        LibraryChecks.NonNegative(who, "T7", t7);
        LibraryChecks.NonNegative(who, "T8", t8);
        LibraryChecks.NonNegative(who, "T9", t9);
        foreach ((double v, string name) in new[] { (ks1, "KS1"), (ks2, "KS2"), (ks3, "KS3") })
            if (!double.IsFinite(v)) throw new ArgumentOutOfRangeException(name, v, $"{who}: {name} must be finite.");
        LibraryChecks.Limits(who, "VS1MIN", vs1Min, "VS1MAX", vs1Max);
        LibraryChecks.Limits(who, "VS2MIN", vs2Min, "VS2MAX", vs2Max);
        LibraryChecks.Limits(who, "VSTMIN", vstMin, "VSTMAX", vstMax);
        _b = b; _ics1 = ics1; _ics2 = ics2;
        _w1 = new Washout(tw1); _w2 = tw2 > 0 ? new Washout(tw2) : null;
        _w3 = new Washout(tw3); _w4 = tw4 > 0 ? new Washout(tw4) : null;
        _lag6 = new FirstOrderLag(1.0, t6);
        _lag7 = new FirstOrderLag(ks2, t7);
        _ks3 = ks3; _ks1 = ks1;
        var ramp = new List<object>();
        if (m > 0 && n > 0)
        {
            if (t9 == 0 && t8 > 0) throw new ArgumentOutOfRangeException(nameof(t9), t9, $"{who}: T9 = 0 with T8 = {t8} makes the ramp-tracking filter a pure derivative (improper).");
            if (t9 > 0)
                for (int k = 0; k < n; k++)
                {
                    ramp.Add(new LeadLag(t8, t9));
                    for (int j = 1; j < m; j++) ramp.Add(new FirstOrderLag(1.0, t9));
                }
        }
        _ramp = ramp;
        _ll1 = LibraryChecks.Stage(who, t1, "T1", t2, "T2");
        _ll2 = LibraryChecks.Stage(who, t3, "T3", t4, "T4");
        _ll3 = LibraryChecks.Stage(who, t10, "T10", t11, "T11");
        _vs1Min = vs1Min; _vs1Max = vs1Max; _vs2Min = vs2Min; _vs2Max = vs2Max; _vstMin = vstMin; _vstMax = vstMax;
    }

    /// <summary>PSS/E PSS2A, values in its record order.</summary>
    public static Pss2 Pss2a(int ics1, int ics2, int m, int n, double tw1, double tw2, double t6, double tw3, double tw4, double t7,
        double ks2, double ks3, double t8, double t9, double ks1, double t1, double t2, double t3, double t4, double vstMax, double vstMin)
        => new(false, ics1, ics2, m, n, tw1, tw2, t6, tw3, tw4, t7, ks2, ks3, t8, t9, ks1, t1, t2, t3, t4, 0.0, 0.0,
            double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity, vstMax, vstMin);

    /// <summary>PSS/E PSS2B, values in its record order.</summary>
    public static Pss2 Pss2b(int ics1, int ics2, int m, int n, double tw1, double tw2, double t6, double tw3, double tw4, double t7,
        double ks2, double ks3, double t8, double t9, double ks1, double t1, double t2, double t3, double t4, double t10, double t11,
        double vs1Max, double vs1Min, double vs2Max, double vs2Min, double vstMax, double vstMin)
        => new(true, ics1, ics2, m, n, tw1, tw2, t6, tw3, tw4, t7, ks2, ks3, t8, t9, ks1, t1, t2, t3, t4, t10, t11,
            vs1Max, vs1Min, vs2Max, vs2Min, vstMax, vstMin);

    /// <inheritdoc/>
    public string Kind => _b ? "PSS2B" : "PSS2A";

    /// <summary>True when either input is electrical power.</summary>
    public bool NeedsTerminalQuantities => _ics1 == 3 || _ics2 == 3;

    /// <summary>True when either input is electrical power: its washouts start at the first step's value.</summary>
    public bool ReadsNetworkInputsAtStart => _ics1 == 3 || _ics2 == 3;

    /// <summary>The output after the last step.</summary>
    public double Output { get; private set; }

    /// <inheritdoc/>
    public void Initialize() { _pending = true; Output = 0.0; }

    private double Signal(int ics, FieldControlInputs x)
    {
        double u = ics == 1 ? x.SpeedDeviationPu : x.ElectricalPowerPu;
        if (!double.IsFinite(u))
            throw new InvalidOperationException($"{Kind}: its input (ICS {ics}) is not available on this step; the caller must use the step that supplies the terminal quantities.");
        return u;
    }

    /// <inheritdoc/>
    public double Step(FieldControlInputs inputs, double dtSeconds)
    {
        double u1 = Math.Clamp(Signal(_ics1, inputs), _vs1Min, _vs1Max);
        double u2 = Math.Clamp(Signal(_ics2, inputs), _vs2Min, _vs2Max);
        if (_pending)
        {
            _w1.Initialize(u1); _w2?.Initialize(0.0); _lag6.Initialize(0.0);
            _w3.Initialize(u2); _w4?.Initialize(0.0); _lag7.Initialize(0.0);
            foreach (object blk in _ramp)
                if (blk is LeadLag l) l.Initialize(0.0); else ((FirstOrderLag)blk).Initialize(0.0);
            _ll1?.Initialize(0.0); _ll2?.Initialize(0.0); _ll3?.Initialize(0.0);
            _pending = false;
        }
        double a = _w1.Step(u1, dtSeconds);
        a = _w2 is null ? a : _w2.Step(a, dtSeconds);
        double n1 = _lag6.Step(a, dtSeconds);
        double b = _w3.Step(u2, dtSeconds);
        b = _w4 is null ? b : _w4.Step(b, dtSeconds);
        double n2 = _lag7.Step(b, dtSeconds);
        double r = n1 + _ks3 * n2;
        foreach (object blk in _ramp)
            r = blk is LeadLag l ? l.Step(r, dtSeconds) : ((FirstOrderLag)blk).Step(r, dtSeconds);
        double y = _ks1 * (r - n2);
        y = _ll1 is null ? y : _ll1.Step(y, dtSeconds);
        y = _ll2 is null ? y : _ll2.Step(y, dtSeconds);
        y = _ll3 is null ? y : _ll3.Step(y, dtSeconds);
        Output = Math.Clamp(y, _vstMin, _vstMax);
        return Output;
    }
}
