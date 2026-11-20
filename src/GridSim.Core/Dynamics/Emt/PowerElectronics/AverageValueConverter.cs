using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.PowerElectronics;

/// <summary>A converter that names its model class in a result.</summary>
public interface IConverterModel
{
    /// <summary>The converter name.</summary>
    string Name { get; }
    /// <summary>Switched or average-value.</summary>
    ConverterModelClass ModelClass { get; }
    /// <summary>One-line description of the model for the result.</summary>
    string Description { get; }
}

/// <summary>
/// The average-value model of a three-phase two-level voltage-source converter. Each leg is a controlled voltage source behind its
/// series resistance <c>Rs</c> (the switched leg's on resistance), referred to the DC mid-point (ground):
/// <c>e_k(t) = m_k(t) Vdc / 2</c>, with <c>m_k</c> the SAME reference the switched bridge's PWM modulator uses (clamped to +/-1: the
/// linear range of a real bridge), so the fundamental pole voltage is identical by construction and there is no switching ripple.
/// The DC side draws <c>i_dc = sum_k (m_k / 2) i_k</c> from the positive node and returns it to the negative node, which makes the AC
/// and DC powers equal at every step (the lossless balance, apart from the <c>Rs i^2</c> of the legs). <c>Vdc</c> is the DC voltage
/// at the end of the PREVIOUS step (the nominal value at the first step): a one-step explicit lag, exact when the DC side is a stiff
/// source and an O(dt) error otherwise.
/// <para>The matrix changes every step (the modulation changes), so the solver refactorises each step.</para>
/// </summary>
public sealed class AverageValueConverter : NodalElement, IConverterModel
{
    private readonly int[] _ac;
    private readonly int _p, _n;
    private readonly double _rs;
    private readonly Func<double, int, double> _mod;
    private readonly double _vdcNominal;
    private readonly double[] _m = new double[3], _e = new double[3], _iAc = new double[3];
    private double _vdcPrev, _iDc;

    /// <summary>Create the converter. <paramref name="modulation"/> is called with (time, leg) and gives the modulation signal in -1..1.</summary>
    public AverageValueConverter(string name, int dcPositive, int dcNegative, int[] ac, double rsOhm, double nominalVdcVolts, Func<double, int, double> modulation) : base(name)
    {
        if (ac is null || ac.Length != 3 || ac.Any(a => a < 0)) throw new ArgumentException($"{name}: three non-ground AC nodes are needed.");
        if (dcPositive < 0 && dcNegative < 0) throw new ArgumentException($"{name}: a DC terminal is needed.");
        if (dcPositive == dcNegative) throw new ArgumentException($"{name}: the DC terminals must differ.");
        if (!(rsOhm > 0) || !double.IsFinite(rsOhm)) throw new ArgumentException($"{name}: the series resistance must be finite and positive.");
        if (!(nominalVdcVolts > 0) || !double.IsFinite(nominalVdcVolts)) throw new ArgumentException($"{name}: the nominal DC voltage must be finite and positive.");
        _ac = (int[])ac.Clone(); _p = dcPositive; _n = dcNegative; _rs = rsOhm; _vdcNominal = nominalVdcVolts;
        _mod = modulation ?? throw new ArgumentNullException(nameof(modulation));
        Reset();
    }

    /// <inheritdoc />
    public ConverterModelClass ModelClass => ConverterModelClass.AverageValue;

    /// <inheritdoc />
    public string Description => "three-phase two-level voltage-source converter, average-value model (controlled voltage sources m Vdc/2 behind Rs, no switching ripple, "
        + "DC current from instantaneous power balance, Vdc from the previous step)";

    internal override bool IsMatrixVarying => true;
    internal override int CurrentChannels => 4;
    internal override void Configure(double dt, IntegrationRule rule) { }

    internal override void Reset()
    {
        Array.Clear(_m); Array.Clear(_e); Array.Clear(_iAc);
        _vdcPrev = _vdcNominal; _iDc = 0.0;
    }

    internal override void Validate(double dt, int nodeCount)
    {
        foreach (int a in _ac) if (a >= nodeCount) throw new ArgumentException("an AC node is out of range");
    }

    internal override void PrepareStep(double t, double dt)
    {
        for (int k = 0; k < 3; k++)
        {
            double m = _mod(t, k);
            _m[k] = Math.Max(-1.0, Math.Min(1.0, m));
            _e[k] = _m[k] * _vdcPrev * 0.5;
        }
    }

    internal override void StampMatrix(NodalStamper s)
    {
        double g = 1.0 / _rs;
        for (int k = 0; k < 3; k++)
        {
            s.Add(_ac[k], _ac[k], g);
            double c = _m[k] * 0.5 * g;               // DC current drawn = sum (m/2)(e - v)/Rs: the v part
            s.Add(_p, _ac[k], -c);
            s.Add(_n, _ac[k], c);
        }
    }

    internal override void StampSources(double t, NodalStamper s)
    {
        double g = 1.0 / _rs, a = 0.0;
        for (int k = 0; k < 3; k++)
        {
            s.AddCurrentInto(_ac[k], _e[k] * g);
            a += _m[k] * 0.5 * _e[k] * g;
        }
        s.AddCurrentInto(_p, -a);
        s.AddCurrentInto(_n, a);
    }

    internal override void Accept(double t, double[] v)
    {
        double idc = 0.0;
        for (int k = 0; k < 3; k++)
        {
            _iAc[k] = (_e[k] - V(v, _ac[k])) / _rs;
            idc += _m[k] * 0.5 * _iAc[k];
        }
        _iDc = idc;
        double vdc = V(v, _p) - V(v, _n);
        if (vdc > 0.0) _vdcPrev = vdc;
    }

    /// <summary>Channels 0 to 2: the current the leg delivers to the AC network; channel 3: the DC current drawn from the positive node.</summary>
    internal override double ProbeCurrent(int channel) => channel < 3 ? _iAc[channel] : _iDc;

    /// <summary>The modulation signal of leg <paramref name="channel"/> (0 to 2).</summary>
    internal override double ProbeStateAt(int channel) => channel is >= 0 and < 3 ? _m[channel] : double.NaN;
}
