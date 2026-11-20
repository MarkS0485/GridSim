using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.FrequencyDependent;

/// <summary>
/// A rational function with REAL stable poles: <c>H(s) = D + sum_k Residues[k] / (s - Poles[k])</c>, every pole strictly
/// negative. Real poles are what a recursive-convolution line model needs (each term is one real exponential), and the
/// propagation function and characteristic admittance of a line are smooth enough for them (Marti's choice).
/// </summary>
public sealed record RationalFunction(double D, double[] Poles, double[] Residues)
{
    /// <summary>The value at complex frequency <paramref name="s"/>.</summary>
    public Complex Evaluate(Complex s)
    {
        Complex h = D;
        for (int k = 0; k < Poles.Length; k++) h += Residues[k] / (s - Poles[k]);
        return h;
    }

    /// <summary>The value at angular frequency <paramref name="omega"/> (s = j omega).</summary>
    public Complex AtOmega(double omega) => Evaluate(new Complex(0.0, omega));

    /// <summary>The value at s = infinity (the constant term).</summary>
    public double AtInfinity => D;
}

/// <summary>The outcome of a fit, with its error against the data it was fitted to.</summary>
/// <param name="Function">The fitted function.</param>
/// <param name="MaxRelativeError">Largest <c>|fit - data| / |data|</c> over the samples.</param>
/// <param name="RmsRelativeError">Root-mean-square of the same.</param>
/// <param name="Iterations">Pole-relocation iterations run.</param>
public sealed record RationalFitResult(RationalFunction Function, double MaxRelativeError, double RmsRelativeError, int Iterations);

/// <summary>
/// Vector fitting (Gustavsen and Semlyen) restricted to real poles: starting poles are real and log-spaced over the band;
/// each iteration solves the weighted (relative-error) linear least-squares problem for the residues of the function and of
/// the scaling function sigma, and the new poles are the zeros of sigma (the eigenvalues of <c>diag(a) - 1 c~^T</c>, by the
/// repository's Francis QR). A relocated COMPLEX pair cannot be represented by real poles; it is replaced by the two real poles
/// at 0.8 and 1.25 times its modulus (stated, deterministic), and every pole is forced stable (negative real part flipped) and
/// kept in a sane range. The iterate with the least relative error is returned, so the result is never worse than the first
/// fixed-pole fit. The error against the data is reported, not hidden.
/// </summary>
public static class RationalFit
{
    /// <summary>Fit <paramref name="data"/> (complex values at <c>s = j omega[i]</c>) with <paramref name="order"/> real poles.</summary>
    /// <exception cref="ArgumentException">The data are unusable (too few samples, non-increasing frequency, zero or non-finite value).</exception>
    public static RationalFitResult Fit(double[] omega, Complex[] data, int order, int iterations = 20)
    {
        ArgumentNullException.ThrowIfNull(omega);
        ArgumentNullException.ThrowIfNull(data);
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        int m = omega.Length;
        if (data.Length != m) throw R("The frequency and data arrays differ in length.");
        if (order < 1 || order > 40) throw R("The fit order must be between 1 and 40.");
        if (m < 2 * order + 4) throw R($"Order {order} needs at least {2 * order + 4} samples but {m} were given.");
        if (iterations < 0) throw R("The iteration count must not be negative.");
        for (int i = 0; i < m; i++)
        {
            if (!(omega[i] > 0) || !double.IsFinite(omega[i]) || (i > 0 && !(omega[i] > omega[i - 1])))
                throw R("The frequencies must be finite, positive and strictly increasing.");
            if (!double.IsFinite(data[i].Real) || !double.IsFinite(data[i].Imaginary) || Complex.Abs(data[i]) == 0.0)
                throw R($"Sample {i} is zero or not finite; a relative-error fit cannot use it.");
        }
        double wMin = omega[0], wMax = omega[m - 1];
        var poles = new double[order];
        for (int k = 0; k < order; k++)
        {
            double frac = order == 1 ? 0.5 : (double)k / (order - 1);
            poles[k] = -wMin * DetMath.Pow(wMax / wMin, frac);
        }
        Normalise(poles, wMin, wMax);

        RationalFunction best = ResidueFit(omega, data, poles, out double bestMax, out double bestRms);
        int bestIt = 0;
        for (int it = 1; it <= iterations; it++)
        {
            double[]? next = Relocate(omega, data, poles, wMin, wMax);
            if (next is null) break;
            poles = next;
            RationalFunction f = ResidueFit(omega, data, poles, out double mx, out double rms);
            if (mx < bestMax) { best = f; bestMax = mx; bestRms = rms; bestIt = it; }
        }
        return new RationalFitResult(best, bestMax, bestRms, bestIt);
    }

    private static void Normalise(double[] poles, double wMin, double wMax)
    {
        for (int k = 0; k < poles.Length; k++)
        {
            double a = Math.Abs(poles[k]);
            if (!(a > 0) || !double.IsFinite(a)) a = wMin;
            a = Math.Min(Math.Max(a, wMin * 1e-3), wMax * 1e3);
            poles[k] = -a;
        }
        Array.Sort(poles, (x, y) => Math.Abs(x).CompareTo(Math.Abs(y)));
        for (int k = 1; k < poles.Length; k++)
            if (Math.Abs(poles[k]) < Math.Abs(poles[k - 1]) * (1.0 + 1e-6)) poles[k] = poles[k - 1] * (1.0 + 1e-4);
    }

    private static double[]? Relocate(double[] omega, Complex[] data, double[] poles, double wMin, double wMax)
    {
        int m = omega.Length, n = poles.Length;
        int cols = 2 * n + 1;
        var a = new double[2 * m, cols];
        var b = new double[2 * m];
        for (int i = 0; i < m; i++)
        {
            double w = 1.0 / Complex.Abs(data[i]);
            var s = new Complex(0.0, omega[i]);
            for (int k = 0; k < n; k++)
            {
                Complex phi = 1.0 / (s - poles[k]);
                Complex fphi = data[i] * phi;
                a[2 * i, k] = phi.Real * w;
                a[2 * i + 1, k] = phi.Imaginary * w;
                a[2 * i, n + 1 + k] = -fphi.Real * w;
                a[2 * i + 1, n + 1 + k] = -fphi.Imaginary * w;
            }
            a[2 * i, n] = w;
            b[2 * i] = data[i].Real * w;
            b[2 * i + 1] = data[i].Imaginary * w;
        }
        double[]? x = SolveScaled(a, b);
        if (x is null) return null;
        var h = new double[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) h[r, c] = (r == c ? poles[r] : 0.0) - x[n + 1 + c];
        Complex[] ev;
        try { ev = GeneralEigen.Eigenvalues(h); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
        var next = new List<double>(n);
        var used = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (used[i]) continue;
            Complex e = ev[i];
            if (Math.Abs(e.Imaginary) <= 1e-9 * Math.Max(Complex.Abs(e), 1e-300))
            {
                next.Add(-Math.Abs(e.Real));
                continue;
            }
            // A complex pair: find its conjugate and replace both by two real poles.
            int mate = -1;
            for (int j = i + 1; j < n; j++)
                if (!used[j] && Math.Abs(ev[j].Real - e.Real) <= 1e-6 * Complex.Abs(e) && Math.Abs(ev[j].Imaginary + e.Imaginary) <= 1e-6 * Complex.Abs(e)) { mate = j; break; }
            double mod = Complex.Abs(e);
            next.Add(-0.8 * mod);
            if (mate >= 0) { used[mate] = true; next.Add(-1.25 * mod); }
        }
        while (next.Count < n) next.Add(next[^1] * 1.5);
        if (next.Count > n) next.RemoveRange(n, next.Count - n);
        var arr = next.ToArray();
        Normalise(arr, wMin, wMax);
        return arr;
    }

    private static RationalFunction ResidueFit(double[] omega, Complex[] data, double[] poles, out double maxErr, out double rmsErr)
    {
        int m = omega.Length, n = poles.Length;
        var a = new double[2 * m, n + 1];
        var b = new double[2 * m];
        for (int i = 0; i < m; i++)
        {
            double w = 1.0 / Complex.Abs(data[i]);
            var s = new Complex(0.0, omega[i]);
            for (int k = 0; k < n; k++)
            {
                Complex phi = 1.0 / (s - poles[k]);
                a[2 * i, k] = phi.Real * w;
                a[2 * i + 1, k] = phi.Imaginary * w;
            }
            a[2 * i, n] = w;
            b[2 * i] = data[i].Real * w;
            b[2 * i + 1] = data[i].Imaginary * w;
        }
        double[]? x = SolveScaled(a, b);
        if (x is null) throw DomainRefusal.Mark(new InvalidOperationException("The residue least-squares problem is rank deficient."));
        var res = new double[n];
        Array.Copy(x, res, n);
        var f = new RationalFunction(x[n], (double[])poles.Clone(), res);
        maxErr = 0.0;
        double sum = 0.0;
        for (int i = 0; i < m; i++)
        {
            double e = Complex.Abs(f.AtOmega(omega[i]) - data[i]) / Complex.Abs(data[i]);
            maxErr = Math.Max(maxErr, e);
            sum += e * e;
        }
        rmsErr = Math.Sqrt(sum / m);
        return f;
    }

    /// <summary>Least squares with each column scaled to unit norm first (the basis functions differ by orders of magnitude).</summary>
    private static double[]? SolveScaled(double[,] a, double[] b)
    {
        int rows = a.GetLength(0), cols = a.GetLength(1);
        var scale = new double[cols];
        for (int c = 0; c < cols; c++)
        {
            double s = 0.0;
            for (int r = 0; r < rows; r++) s += a[r, c] * a[r, c];
            scale[c] = Math.Sqrt(s);
            if (!(scale[c] > 0)) scale[c] = 1.0;
            for (int r = 0; r < rows; r++) a[r, c] /= scale[c];
        }
        double[] x;
        try { x = HouseholderQr.SolveLeastSquares(a, b); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
        for (int c = 0; c < cols; c++)
        {
            x[c] /= scale[c];
            if (!double.IsFinite(x[c])) return null;
        }
        return x;
    }
}
