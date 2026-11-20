using System;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Models.Adaptive;

/// <summary>An initial-value problem <c>y' = f(t, y)</c> the adaptive controller can integrate.</summary>
public interface IAdaptiveOde
{
    /// <summary>The number of state variables.</summary>
    int Dimension { get; }

    /// <summary>Write <c>f(t, y)</c> into <paramref name="dydt"/>. Must be a pure function of its arguments and of the
    /// problem's own mode state (which only changes between steps), and must not touch <paramref name="y"/>.</summary>
    void Derivative(double t, double[] y, double[] dydt);
}

/// <summary>
/// RC4 R4-14: the tolerances and limits of the variable-step integration. Every field has a stated meaning and a
/// validated range; nothing is read from the environment.
/// </summary>
/// <param name="RelativeTolerance">Relative tolerance of the embedded error test.</param>
/// <param name="AbsoluteTolerance">Absolute tolerance of the embedded error test (the floor for states near zero).</param>
/// <param name="InitialStepSeconds">The first trial step (s).</param>
/// <param name="MinStepSeconds">The smallest step the controller will take (s). A step at this size that still fails the
/// error test stops the run with an error (it is never accepted silently).</param>
/// <param name="MaxStepSeconds">The largest step (s).</param>
/// <param name="EventTimeToleranceSeconds">The width (s) to which a relay or other state event is located by bisection.</param>
public sealed record AdaptiveOptions(
    double RelativeTolerance = 1e-6,
    double AbsoluteTolerance = 1e-8,
    double InitialStepSeconds = 1e-3,
    double MinStepSeconds = 1e-9,
    double MaxStepSeconds = 0.05,
    double EventTimeToleranceSeconds = 1e-6)
{
    /// <summary>Refuse an option set outside its range, naming the field.</summary>
    public void Validate()
    {
        static void Pos(double v, string n)
        {
            if (!(v > 0.0) || !double.IsFinite(v)) throw new ArgumentOutOfRangeException(n, v, $"{n} must be positive and finite.");
        }
        Pos(RelativeTolerance, nameof(RelativeTolerance));
        Pos(AbsoluteTolerance, nameof(AbsoluteTolerance));
        Pos(InitialStepSeconds, nameof(InitialStepSeconds));
        Pos(MinStepSeconds, nameof(MinStepSeconds));
        Pos(MaxStepSeconds, nameof(MaxStepSeconds));
        Pos(EventTimeToleranceSeconds, nameof(EventTimeToleranceSeconds));
        if (MinStepSeconds > MaxStepSeconds)
            throw new ArgumentOutOfRangeException(nameof(MinStepSeconds), MinStepSeconds, "MinStepSeconds must not exceed MaxStepSeconds.");
        if (RelativeTolerance >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(RelativeTolerance), RelativeTolerance, "RelativeTolerance must be below 1.");
    }
}

/// <summary>The controller was asked for a step below its floor, or the state went non-finite.</summary>
public sealed class AdaptiveStepException(string message) : InvalidOperationException(message);

/// <summary>
/// RC4 R4-14: the deterministic step-size controller of the variable-step RMS path.
/// <para>
/// <b>Scheme.</b> The Dormand-Prince 5(4) embedded pair (seven stages; the fifth-order solution is propagated, the
/// difference to the embedded fourth-order solution is the local error estimate). Its coefficients are the published
/// ones (Dormand and Prince 1980); the tests check the order of the scheme and the estimate against exact solutions.
/// </para>
/// <para>
/// <b>Error norm.</b> <c>err = max_i |y5_i - y4_i| / (atol + rtol * max(|y_i|, |y5_i|))</c>; the step is accepted when
/// <c>err &lt;= 1</c>.
/// </para>
/// <para>
/// <b>Step rule.</b> <c>h' = h * clamp(0.9 * err^(-1/5), 0.2, 5)</c>, limited to the option range: a pure function of the
/// step just tried and its error. The power uses <see cref="DetMath.Pow"/>. There is no wall-clock input, no thread
/// input and no memory of earlier steps beyond <c>h</c>, so the step sequence depends only on the state and the options:
/// the run is bit-identical across runs and thread counts.
/// </para>
/// </summary>
public sealed class AdaptiveStepController
{
    /// <summary>Order of the propagated solution; the step rule's exponent is <c>-1/(order)</c>.</summary>
    public const int Order = 5;

    /// <summary>The scheme as stated in result declarations.</summary>
    public const string Scheme = "Dormand-Prince 5(4) embedded pair, fifth-order solution propagated; err = max |y5 - y4| / (atol + rtol max(|y|,|y5|)), accept when err <= 1; h' = h clamp(0.9 err^(-1/5), 0.2, 5)";

    private const double Safety = 0.9, MinFactor = 0.2, MaxFactor = 5.0;

    private readonly AdaptiveOptions _o;
    private double[] _k1 = [], _k2 = [], _k3 = [], _k4 = [], _k5 = [], _k6 = [], _k7 = [], _tmp = [];

    /// <summary>Create a controller; the options are validated.</summary>
    public AdaptiveStepController(AdaptiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _o = options;
    }

    /// <summary>The options in force.</summary>
    public AdaptiveOptions Options => _o;

    /// <summary>Evaluations of the right-hand side so far (seven per step).</summary>
    public long Evaluations { get; private set; }

    private void Ensure(int n)
    {
        if (_k1.Length == n) return;
        _k1 = new double[n]; _k2 = new double[n]; _k3 = new double[n]; _k4 = new double[n];
        _k5 = new double[n]; _k6 = new double[n]; _k7 = new double[n]; _tmp = new double[n];
    }

    /// <summary>
    /// One Dormand-Prince step of length <paramref name="h"/> from <c>(t, y)</c>. <paramref name="yNew"/> receives the
    /// fifth-order solution (it must not alias <paramref name="y"/>). Returns the scaled error norm (infinite when the
    /// step produced a non-finite state, which the caller treats as a rejection).
    /// </summary>
    public double Step(IAdaptiveOde ode, double t, double[] y, double h, double[] yNew)
    {
        ArgumentNullException.ThrowIfNull(ode); ArgumentNullException.ThrowIfNull(y); ArgumentNullException.ThrowIfNull(yNew);
        int n = ode.Dimension;
        if (y.Length != n || yNew.Length != n) throw new ArgumentException("y and yNew must have the problem's dimension.");
        if (ReferenceEquals(y, yNew)) throw new ArgumentException("yNew must not alias y.", nameof(yNew));
        Ensure(n);
        double[] k1 = _k1, k2 = _k2, k3 = _k3, k4 = _k4, k5 = _k5, k6 = _k6, k7 = _k7, s = _tmp;

        ode.Derivative(t, y, k1);
        for (int i = 0; i < n; i++) s[i] = y[i] + h * (1.0 / 5.0 * k1[i]);
        ode.Derivative(t + h * (1.0 / 5.0), s, k2);
        for (int i = 0; i < n; i++) s[i] = y[i] + h * (3.0 / 40.0 * k1[i] + 9.0 / 40.0 * k2[i]);
        ode.Derivative(t + h * (3.0 / 10.0), s, k3);
        for (int i = 0; i < n; i++) s[i] = y[i] + h * (44.0 / 45.0 * k1[i] - 56.0 / 15.0 * k2[i] + 32.0 / 9.0 * k3[i]);
        ode.Derivative(t + h * (4.0 / 5.0), s, k4);
        for (int i = 0; i < n; i++)
            s[i] = y[i] + h * (19372.0 / 6561.0 * k1[i] - 25360.0 / 2187.0 * k2[i] + 64448.0 / 6561.0 * k3[i] - 212.0 / 729.0 * k4[i]);
        ode.Derivative(t + h * (8.0 / 9.0), s, k5);
        for (int i = 0; i < n; i++)
            s[i] = y[i] + h * (9017.0 / 3168.0 * k1[i] - 355.0 / 33.0 * k2[i] + 46732.0 / 5247.0 * k3[i] + 49.0 / 176.0 * k4[i] - 5103.0 / 18656.0 * k5[i]);
        ode.Derivative(t + h, s, k6);
        for (int i = 0; i < n; i++)
            yNew[i] = y[i] + h * (35.0 / 384.0 * k1[i] + 500.0 / 1113.0 * k3[i] + 125.0 / 192.0 * k4[i] - 2187.0 / 6784.0 * k5[i] + 11.0 / 84.0 * k6[i]);
        ode.Derivative(t + h, yNew, k7);
        Evaluations += 7;

        double err = 0.0;
        for (int i = 0; i < n; i++)
        {
            // y5 - y4 = h * sum (b5 - b4) k  (the seventh stage carries the fourth-order weight only)
            double e = h * (71.0 / 57600.0 * k1[i] - 71.0 / 16695.0 * k3[i] + 71.0 / 1920.0 * k4[i]
                - 17253.0 / 339200.0 * k5[i] + 22.0 / 525.0 * k6[i] - 1.0 / 40.0 * k7[i]);
            double scale = _o.AbsoluteTolerance + _o.RelativeTolerance * Math.Max(Math.Abs(y[i]), Math.Abs(yNew[i]));
            double r = Math.Abs(e) / scale;
            if (!(r <= err)) err = r;   // a NaN error wins (propagates as non-finite)
        }
        if (!double.IsFinite(err)) return double.PositiveInfinity;
        return err;
    }

    /// <summary>The next trial step from the step <paramref name="h"/> just tried and its error norm: the pure step rule.
    /// <paramref name="wanted"/> is the step that was wanted before a boundary (scheduled event or run end) cut it
    /// short, or <paramref name="h"/> itself when no cut applied; a cut step that passed is never allowed to shrink the
    /// next trial below the step that was wanted.</summary>
    public double NextStep(double h, double err, double wanted)
    {
        double factor;
        if (!double.IsFinite(err)) factor = MinFactor;
        else if (err == 0.0) factor = MaxFactor;
        else factor = Math.Min(MaxFactor, Math.Max(MinFactor, Safety * DetMath.Pow(err, -1.0 / Order)));
        double next = h * factor;
        if (wanted > h && err <= 1.0) next = Math.Max(next, wanted);
        return Math.Min(_o.MaxStepSeconds, Math.Max(_o.MinStepSeconds, next));
    }
}
