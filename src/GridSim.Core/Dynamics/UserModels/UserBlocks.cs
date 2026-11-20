using GridSim.Core.Dynamics.Models;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>
/// Primitive blocks the declarative vocabulary needs that <see cref="ControlBlocks"/> does not have (R4-08). Each is a
/// pure function of (state, input, step) with no shared or static state, written with the same discipline as
/// <see cref="ControlBlocks"/>: transcendental maths only through <see cref="DetMath"/>, and the exponential pole
/// reproduces <see cref="Ieeet1"/>'s own step operation for operation so it can be proved bit-identical to it.
/// The coded classes are not edited; the blocks that duplicate arithmetic carry a bit-identity test against them.
/// </summary>
internal static class UserBlocks
{
    /// <summary>Dead band: zero inside [lower, upper], the excess outside.</summary>
    public static double DeadBand(double u, double lower, double upper)
        => u > upper ? u - upper : u < lower ? u - lower : 0.0;

    /// <summary>
    /// One-dimensional lookup with linear interpolation between breakpoints and the end values held outside the range
    /// (no extrapolation). The segment is found by a linear scan from the left, so the answer does not depend on the
    /// table length through any search strategy.
    /// </summary>
    public static double Table(double u, double[] x, double[] y)
    {
        int n = x.Length;
        if (!(u > x[0])) return y[0];
        if (!(u < x[n - 1])) return y[n - 1];
        int k = 1;
        while (k < n - 1 && !(u < x[k])) k++;
        double x0 = x[k - 1], x1 = x[k], y0 = y[k - 1], y1 = y[k];
        return y0 + (y1 - y0) * (u - x0) / (x1 - x0);
    }

    /// <summary>Saturation function Se = a * exp(b * u), the exponential form IEEE exciter models use (as <see cref="Ieeet1"/>).</summary>
    public static double ExpSat(double u, double a, double b) => a * DetMath.Exp(b * u);
}

/// <summary>Rate limiter: the output follows the input but may rise by at most <c>rise*dt</c> and fall by at most <c>fall*dt</c> per step (fall is negative).</summary>
internal sealed class RateLimiter
{
    private readonly double _rise;
    private readonly double _fall;
    private double _x;

    public RateLimiter(double rise, double fall)
    {
        if (!(rise > 0.0)) throw new ArgumentOutOfRangeException(nameof(rise), rise, "A rate limiter's rise rate must be above 0 (use +inf for none).");
        if (!(fall < 0.0)) throw new ArgumentOutOfRangeException(nameof(fall), fall, "A rate limiter's fall rate must be below 0 (use -inf for none).");
        _rise = rise;
        _fall = fall;
    }

    public double Output => _x;

    public void Initialize(double output) => _x = output;

    public double Step(double input, double dt)
    {
        _x += Math.Clamp(input - _x, _fall * dt, _rise * dt);
        return _x;
    }
}

/// <summary>Fixed delay of a whole number of steps (a ring buffer, filled with the initial value).</summary>
internal sealed class StepDelay
{
    private readonly double[] _buf;
    private int _head;
    private double _last;

    public StepDelay(int steps)
    {
        if (steps < 1) throw new ArgumentOutOfRangeException(nameof(steps), steps, "A fixed delay needs at least 1 step.");
        _buf = new double[steps];
    }

    public double Output => _last;

    public void Initialize(double value)
    {
        Array.Fill(_buf, value);
        _head = 0;
        _last = value;
    }

    public double Step(double input)
    {
        double outv = _buf[_head];
        _buf[_head] = input;
        _head = (_head + 1) % _buf.Length;
        _last = outv;
        return outv;
    }
}

/// <summary>
/// The saturated exciter pole of the IEEE Type-1 exciter: <c>Te dEfd/dt = Vr - (Ke + Se(Efd)) Efd</c> with
/// <c>Se = Aex exp(Bex Efd)</c>, advanced by the exact zero-order hold of the pole linearised with Se frozen at the start
/// of the step (a pole at the origin becomes a forward-Euler integrator). The step is <see cref="Ieeet1"/>'s own,
/// operation for operation; proved bit-identical by <c>UserModelBitIdentityTests</c>.
/// </summary>
internal sealed class SaturatedExciterPole
{
    private readonly double _te, _ke, _aex, _bex;
    private double _efd;

    public SaturatedExciterPole(double te, double ke, double aex, double bex)
    {
        if (!(te > 0.0)) throw new ArgumentOutOfRangeException(nameof(te), te, "The exciter time constant Te must be above 0.");
        if (aex < 0.0) throw new ArgumentOutOfRangeException(nameof(aex), aex, "The saturation scale Aex must be 0 or above.");
        _te = te; _ke = ke; _aex = aex; _bex = bex;
    }

    public double Output => _efd;

    public void Initialize(double efd) => _efd = efd;

    public double Step(double vr, double dt)
    {
        double keSe = _ke + _aex * DetMath.Exp(_bex * _efd);
        if (Math.Abs(keSe) <= Ieeet1.ExciterPoleAtOriginTolerance)
            _efd += vr / _te * dt;
        else
        {
            double a = DetMath.Exp(-dt * keSe / _te);
            _efd = a * _efd + (1.0 - a) * vr / keSe;
        }
        return _efd;
    }
}
