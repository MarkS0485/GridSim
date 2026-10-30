namespace GridSim.Core.Estimation.Dse;

/// <summary>
/// The classical persistence (random-walk) process model x_k = x_{k-1} + w, w ~ N(0, Q). The state is
/// expected to hold from one window to the next, with a per-unit-time process-noise variance capturing how
/// fast it can drift. This is the standard forecasting-aided / tracking state-estimation model (Debs-Larson):
/// no dynamics, just "the grid state changes slowly between telemetry snapshots." The state-transition
/// Jacobian is the identity, so it makes no forward claim about the real grid - it only sets how much the
/// filter trusts the previous estimate as a prior for the present one.
/// </summary>
public sealed class RandomWalkProcessModel : IProcessModel
{
    private readonly double _angleVarPerSec;
    private readonly double _vmVarPerSec;
    private readonly int _angleCount;

    /// <summary>
    /// </summary>
    /// <param name="angleCount">Number of leading angle states (non-slack bus angles); the rest are Vm states.</param>
    /// <param name="angleProcessStdPerSqrtSec">Angle random-walk std (radians) per sqrt-second.</param>
    /// <param name="vmProcessStdPerSqrtSec">Voltage-magnitude random-walk std (pu) per sqrt-second.</param>
    public RandomWalkProcessModel(int angleCount, double angleProcessStdPerSqrtSec = 0.05, double vmProcessStdPerSqrtSec = 0.01)
    {
        _angleCount = angleCount;
        _angleVarPerSec = angleProcessStdPerSqrtSec * angleProcessStdPerSqrtSec;
        _vmVarPerSec = vmProcessStdPerSqrtSec * vmProcessStdPerSqrtSec;
    }

    public string Name => "random-walk";

    public void Predict(double[] x, double dtSeconds, double[,] f, double[,] q)
    {
        int s = x.Length;
        // R256: dt = 0 (two windows at the same instant: a duplicate file, a re-emitted PDC superframe,
        // timestamps rounded equal) is a pure update with ZERO process noise; it used to be coerced to a full
        // second of drift (angle std 0.05 rad), discounting the prior for nothing. A negative or non-finite dt
        // (an out-of-order window) is an error, not a step. An unknown cadence (the first window) is the
        // caller's default of 1 s, never a coercion here.
        if (!double.IsFinite(dtSeconds) || dtSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dtSeconds), dtSeconds,
                "The elapsed time since the previous window must be a finite number >= 0 (a negative value means the windows are out of order).");
        double dt = dtSeconds;
        for (int i = 0; i < s; i++)
        {
            for (int j = 0; j < s; j++) { f[i, j] = 0.0; q[i, j] = 0.0; }
            f[i, i] = 1.0;                                   // persistence: x^- = x
            q[i, i] = (i < _angleCount ? _angleVarPerSec : _vmVarPerSec) * dt;
        }
        // x is unchanged (identity transition).
    }
}
