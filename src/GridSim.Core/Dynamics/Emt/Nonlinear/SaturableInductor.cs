namespace GridSim.Core.Dynamics.Emt.Nonlinear;

/// <summary>
/// A saturable inductor (core magnetising branch) from node a to node b, written in flux linkage: <c>lambda' = v</c> and
/// <c>i = f(lambda)</c> with <c>f</c> a monotone odd piecewise-linear current-flux curve (<see cref="PiecewiseCurve"/>,
/// x = Wb, y = A; the first segment's slope is the unsaturated 1/L and the last segment's the air-core 1/L).
/// The flux is integrated with the trapezoidal rule, <c>lambda(n) = lambda(n-1) + (dt/2)(v(n) + v(n-1))</c> (backward Euler
/// <c>dt v(n)</c> at an event step), so the current at the step is an algebraic function of the voltage and Newton works on
/// the voltage alone. <paramref name="InitialFlux"/> sets the starting (residual) flux linkage; the initial current is
/// <c>f(lambda0)</c>. There is NO hysteresis (a single-valued curve): remanence at zero current is not representable, which
/// is stated in the manual as not validated.
/// </summary>
public sealed class SaturableInductor : NonlinearElement
{
    private readonly PiecewiseCurve _curve;
    private readonly double _lambda0;
    private double _k = 1.0, _lambda, _vPrev;
    private bool _trap = true;

    /// <summary>Create the inductor; <paramref name="initialFlux"/> in Wb.</summary>
    public SaturableInductor(string name, int from, int to, PiecewiseCurve currentOfFlux, double initialFlux = 0.0) : base(name, from, to)
    {
        _curve = currentOfFlux ?? throw new ArgumentNullException(nameof(currentOfFlux));
        if (!double.IsFinite(initialFlux)) throw new ArgumentException($"{name}: the initial flux is not finite.");
        _lambda0 = initialFlux;
        _lambda = initialFlux;
    }

    /// <summary>The current at flux linkage <paramref name="lambda"/> (the curve the solver uses).</summary>
    public double CurrentAtFlux(double lambda) => _curve.Value(lambda);

    private double FluxAt(double v) => _lambda + _k * (v + (_trap ? _vPrev : 0.0));

    /// <inheritdoc />
    protected override void Evaluate(double v, out double current, out double conductance)
    {
        _curve.Evaluate(FluxAt(v), out current, out double slope);
        conductance = slope * _k;
    }

    /// <inheritdoc />
    protected override void ConfigureStep(double dt, IntegrationRule rule)
    {
        _trap = rule != IntegrationRule.BackwardEuler;
        _k = _trap ? 0.5 * dt : dt;
    }

    /// <inheritdoc />
    protected override void AcceptStep(double v, double current)
    {
        _lambda = FluxAt(v);
        _vPrev = v;
    }

    /// <inheritdoc />
    protected override void ResetState()
    {
        _lambda = _lambda0;
        _vPrev = 0.0;
    }

    /// <inheritdoc />
    protected override double InitialCurrent => _curve.Value(_lambda0);

    /// <inheritdoc />
    protected override double StateValue => _lambda;
}
