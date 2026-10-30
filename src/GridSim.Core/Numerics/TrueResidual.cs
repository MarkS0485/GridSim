namespace GridSim.Core.Numerics;

/// <summary>
/// The success check every Krylov solver applies before it hands back a solution: the TRUE residual b - A x, computed
/// with one more product, must meet the relative tolerance. BiCGSTAB's residual is updated by recurrence and drifts away
/// from b - A x in finite precision, worst on the ill-conditioned systems that reach an iterative fallback; deciding on
/// the recursive residual alone could return a step that was never solved to the stated tolerance.
/// </summary>
public static class TrueResidual
{
    /// <summary>
    /// <paramref name="x"/> when ||b - A x|| / ||b|| is below <paramref name="tol"/>, otherwise null (the caller's
    /// failure path: a fallback or a reported failure). A null <paramref name="x"/> passes through as null. A zero right
    /// side is met by any x that makes A x zero to the bit, which the solvers' own early return already covers.
    /// </summary>
    public static double[]? Verified(CsrMatrix a, double[] b, double[]? x, double tol)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (x is null) return null;
        var ax = new double[b.Length];
        a.Multiply(x, ax);
        double num = 0.0, den = 0.0;
        for (int i = 0; i < b.Length; i++)
        {
            double d = b[i] - ax[i];
            num += d * d;
            den += b[i] * b[i];
        }
        if (den == 0.0) return num == 0.0 ? x : null;
        return Math.Sqrt(num) / Math.Sqrt(den) < tol ? x : null;
    }
}
