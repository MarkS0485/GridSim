using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.Nonlinear;

/// <summary>
/// A metal-oxide (ZnO) surge arrester from node a to node b: a memoryless, odd nonlinear resistor. Two characteristics:
/// the power law <c>i = k sign(v) (|v| / Vref)^alpha</c> (<see cref="PowerLaw"/>: <c>Iref</c> is the current at
/// <c>Vref</c>, <c>k = Iref</c>), and a piecewise-linear V-I table (<see cref="Piecewise"/>). There is no frequency or
/// thermal dependence and no temperature model (depth A); a real arrester's dynamic overshoot is not modelled.
/// </summary>
public sealed class SurgeArrester : NonlinearElement
{
    private readonly double _vref, _iref, _alpha;
    private readonly PiecewiseCurve? _curve;

    private SurgeArrester(string name, int from, int to, double vref, double iref, double alpha, PiecewiseCurve? curve) : base(name, from, to)
    {
        _vref = vref; _iref = iref; _alpha = alpha; _curve = curve;
    }

    /// <summary>A power-law arrester <c>i = iref (|v|/vref)^alpha</c> (vref, iref positive; alpha at least 1).</summary>
    public static SurgeArrester PowerLaw(string name, int from, int to, double vref, double iref, double alpha)
    {
        if (!(vref > 0 && double.IsFinite(vref)) || !(iref > 0 && double.IsFinite(iref)))
            throw new ArgumentException($"{name}: the reference voltage and current must be finite and positive.");
        if (!(alpha >= 1.0 && alpha <= 200.0)) throw new ArgumentException($"{name}: the exponent must lie between 1 and 200.");
        return new SurgeArrester(name, from, to, vref, iref, alpha, null);
    }

    /// <summary>A piecewise-linear arrester through the positive-branch points (volts, amps).</summary>
    public static SurgeArrester Piecewise(string name, int from, int to, IReadOnlyList<(double Volts, double Amps)> points)
        => new(name, from, to, 0, 0, 0, new PiecewiseCurve(points));

    /// <summary>The characteristic current at branch voltage <paramref name="v"/> (the same function the solver uses).</summary>
    public double CurrentAt(double v)
    {
        Evaluate(v, out double i, out _);
        return i;
    }

    /// <inheritdoc />
    protected override void Evaluate(double v, out double current, out double conductance)
    {
        if (_curve is not null)
        {
            _curve.Evaluate(v, out current, out conductance);
            return;
        }
        double a = Math.Abs(v);
        if (a == 0.0) { current = 0.0; conductance = 0.0; return; }
        double ratio = a / _vref;
        double mag = _iref * DetMath.Pow(ratio, _alpha);
        current = v < 0 ? -mag : mag;
        conductance = mag * _alpha / a;
    }

    /// <inheritdoc />
    protected override void ConfigureStep(double dt, IntegrationRule rule) { }
    /// <inheritdoc />
    protected override void AcceptStep(double v, double current) { }
    /// <inheritdoc />
    protected override void ResetState() { }
}
