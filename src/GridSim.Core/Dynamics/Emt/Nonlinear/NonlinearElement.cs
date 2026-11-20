using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.Nonlinear;

/// <summary>
/// A two-terminal nonlinear branch from node a to node b, solved by Newton iteration INSIDE the time step. The branch
/// current at the step is <c>i = f(v)</c> of the branch voltage <c>v = V(a) - V(b)</c> and of the element's own
/// accepted history; each Newton iteration linearises it as a conductance <c>g = di/dv</c> in parallel with a current
/// source <c>i0 - g v0</c>. The iteration policy (fixed maximum, voltage and residual tolerances, fixed number of
/// step-halvings) is the run's <see cref="NewtonPolicy"/>; failure to converge ends the run with
/// <see cref="NodalStatus.NewtonNotConverged"/> and a message naming the step. A conductance below
/// <see cref="MinTangentSiemens"/> is raised to it in the TANGENT only (so a flat characteristic cannot make the matrix
/// singular); the current itself is never altered, so the converged answer is unaffected.
/// </summary>
public abstract class NonlinearElement : NodalElement
{
    /// <summary>The floor on the Newton tangent conductance, siemens.</summary>
    public const double MinTangentSiemens = 1e-12;

    private double _scale;
    private double _iLast;

    /// <summary>Create the element between two nodes (distinct; either may be ground).</summary>
    protected NonlinearElement(string name, int from, int to) : base(name)
    {
        if (from == to) throw new ArgumentException($"{name}: both terminals are the same node.");
        From = from; To = to;
    }

    /// <summary>Node a.</summary>
    public int From { get; }
    /// <summary>Node b.</summary>
    public int To { get; }

    /// <summary>The branch current and its slope at branch voltage <paramref name="v"/> for the step being solved.</summary>
    protected abstract void Evaluate(double v, out double current, out double conductance);

    /// <summary>Called when the integration rule or step changes.</summary>
    protected abstract void ConfigureStep(double dt, IntegrationRule rule);

    /// <summary>Called when the step converged, with the final branch voltage and current.</summary>
    protected abstract void AcceptStep(double v, double current);

    /// <summary>Back to the initial state.</summary>
    protected abstract void ResetState();

    /// <summary>The current at the initial state (0 unless the element starts with stored flux).</summary>
    protected virtual double InitialCurrent => 0.0;

    /// <summary>The state variable (flux linkage for a saturable inductor; NaN if none).</summary>
    protected virtual double StateValue => double.NaN;

    internal sealed override bool IsNonlinear => true;
    internal sealed override void Configure(double dt, IntegrationRule rule) => ConfigureStep(dt, rule);
    internal sealed override void Reset() { ResetState(); _scale = 0.0; _iLast = InitialCurrent; }
    internal sealed override double CurrentScale => _scale;
    internal sealed override double ProbeCurrent(int channel) => _iLast;
    internal sealed override double ProbeState() => StateValue;

    internal sealed override void StampNewton(double[] v, NodalStamper s)
    {
        double vb = V(v, From) - V(v, To);
        Evaluate(vb, out double i, out double g);
        double ge = Math.Max(g, MinTangentSiemens);
        double ieq = i - ge * vb;
        s.AddConductance(From, To, ge);
        s.AddCurrentInto(From, -ieq);
        s.AddCurrentInto(To, ieq);
    }

    internal sealed override void AddNonlinearCurrent(double[] v, double[] f)
    {
        double vb = V(v, From) - V(v, To);
        Evaluate(vb, out double i, out _);
        _scale = Math.Max(_scale, Math.Abs(i));
        if (From >= 0) f[From] += i;
        if (To >= 0) f[To] -= i;
    }

    internal sealed override void Accept(double t, double[] v)
    {
        double vb = V(v, From) - V(v, To);
        Evaluate(vb, out double i, out _);
        _iLast = i;
        AcceptStep(vb, i);
    }
}

/// <summary>
/// A monotone, odd piecewise-linear characteristic <c>y = f(x)</c>: the points <c>(x_k, y_k)</c> are the positive branch
/// with <c>0 &lt; x_1 &lt; x_2 ...</c> and <c>0 &lt; y_1 &lt; y_2 ...</c>; the curve runs from the origin to the first
/// point, between points, and beyond the last point along the last segment. The negative branch is the odd mirror. Used for
/// the V-I curve of a ZnO arrester (x = volts, y = amps) and for the current-flux curve of a saturable inductor
/// (x = flux linkage in Wb, y = amps).
/// </summary>
public sealed class PiecewiseCurve
{
    private readonly double[] _x, _y;

    /// <summary>Create from the positive-branch points (at least one).</summary>
    public PiecewiseCurve(IReadOnlyList<(double X, double Y)> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) throw new ArgumentException("A characteristic needs at least one point.", nameof(points));
        _x = new double[points.Count + 1];
        _y = new double[points.Count + 1];
        for (int k = 0; k < points.Count; k++)
        {
            (double x, double y) = points[k];
            if (!double.IsFinite(x) || !double.IsFinite(y) || !(x > _x[k]) || !(y > _y[k]))
                throw new ArgumentException($"Characteristic point {k + 1} must be finite and strictly above the previous point (both coordinates increasing, from the origin).", nameof(points));
            _x[k + 1] = x; _y[k + 1] = y;
        }
        if (points.Count == 1)
        {
            // One point: a straight line through the origin.
            _x = new[] { 0.0, points[0].X, points[0].X * 2.0 };
            _y = new[] { 0.0, points[0].Y, points[0].Y * 2.0 };
        }
    }

    /// <summary>The knots from the origin: x then y.</summary>
    public IReadOnlyList<(double X, double Y)> Knots => Enumerable.Range(0, _x.Length).Select(i => (_x[i], _y[i])).ToArray();

    /// <summary>The value and slope at <paramref name="x"/>.</summary>
    public void Evaluate(double x, out double y, out double slope)
    {
        double ax = Math.Abs(x);
        int k = 1;
        int last = _x.Length - 1;
        while (k < last && ax > _x[k]) k++;
        slope = (_y[k] - _y[k - 1]) / (_x[k] - _x[k - 1]);
        double ya = _y[k - 1] + slope * (ax - _x[k - 1]);
        y = x < 0 ? -ya : ya;
    }

    /// <summary>The value at <paramref name="x"/>.</summary>
    public double Value(double x)
    {
        Evaluate(x, out double y, out _);
        return y;
    }
}
