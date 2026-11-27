using System.Numerics;
using GridSim.Core.Stochastic;

namespace GridSim.Core.Dynamics.SmallSignal.Full;

/// <summary>A matrix the Arnoldi method can work with: a product with a vector and a factorisation of <c>A - sigma I</c>.</summary>
public interface IArnoldiOperator
{
    /// <summary>Order n.</summary>
    int Order { get; }

    /// <summary>The 1-norm (largest absolute column sum), used to scale residuals.</summary>
    double Norm1 { get; }

    /// <summary><c>y = A x</c>.</summary>
    void Multiply(Complex[] x, Complex[] y);

    /// <summary>Factorise <c>A - sigma I</c>. Throws <see cref="SingularShiftException"/> when a pivot falls below <paramref name="singularPivotRatio"/> times the matrix scale.</summary>
    IShiftedSolver Factor(Complex sigma, double singularPivotRatio = 1e-10);

    /// <summary>Factorise <c>(A - sigma I)^H</c> (the conjugate transpose), for left eigenvectors.</summary>
    IShiftedSolver FactorAdjoint(Complex sigma, double singularPivotRatio = 1e-10);
}

/// <summary>Solves <c>(A - sigma I) x = b</c> for a factorisation made by <see cref="IArnoldiOperator.Factor"/>.</summary>
public interface IShiftedSolver
{
    /// <summary>Solve in place for one right-hand side.</summary>
    void Solve(Complex[] b, Complex[] x);
}

/// <summary>The shifted matrix has a zero pivot, so the shift is (numerically) an eigenvalue.</summary>
public sealed class SingularShiftException : Exception
{
    /// <summary>Make the exception.</summary>
    public SingularShiftException(Complex sigma) : base(FormattableString.Invariant($"A - sigma I is singular at sigma = {sigma.Real:R} + j{sigma.Imaginary:R}.")) { }
}

/// <summary>Dense real matrix as an Arnoldi operator (partial-pivot complex LU for the shifted solves).</summary>
public sealed class DenseArnoldiOperator : IArnoldiOperator
{
    private readonly double[,] _a;
    private readonly int _n;

    /// <summary>Wrap a square real matrix (not copied; do not change it while in use).</summary>
    public DenseArnoldiOperator(double[,] a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.GetLength(0) != a.GetLength(1)) throw new ArgumentException("The matrix must be square.", nameof(a));
        _a = a; _n = a.GetLength(0);
        double best = 0.0;
        for (int j = 0; j < _n; j++)
        {
            double s = 0.0;
            for (int i = 0; i < _n; i++) s += Math.Abs(a[i, j]);
            if (s > best) best = s;
        }
        Norm1 = best;
    }

    /// <inheritdoc />
    public int Order => _n;

    /// <inheritdoc />
    public double Norm1 { get; }

    /// <inheritdoc />
    public void Multiply(Complex[] x, Complex[] y)
    {
        for (int i = 0; i < _n; i++)
        {
            double re = 0.0, im = 0.0;
            for (int j = 0; j < _n; j++)
            {
                double aij = _a[i, j];
                re += aij * x[j].Real;
                im += aij * x[j].Imaginary;
            }
            y[i] = new Complex(re, im);
        }
    }

    /// <inheritdoc />
    public IShiftedSolver Factor(Complex sigma, double singularPivotRatio = 1e-10) => new DenseLu(_a, sigma, adjoint: false, singularPivotRatio);

    /// <inheritdoc />
    public IShiftedSolver FactorAdjoint(Complex sigma, double singularPivotRatio = 1e-10) => new DenseLu(_a, sigma, adjoint: true, singularPivotRatio);

    private sealed class DenseLu : IShiftedSolver
    {
        private readonly Complex[,] _lu;
        private readonly int[] _piv;
        private readonly int _n;

        public DenseLu(double[,] a, Complex sigma, bool adjoint, double ratio)
        {
            _n = a.GetLength(0);
            _lu = new Complex[_n, _n];
            _piv = new int[_n];
            // (A - sigma I)^H = A^T - conj(sigma) I for real A.
            Complex shift = adjoint ? Complex.Conjugate(sigma) : sigma;
            double scale = 0.0;
            for (int i = 0; i < _n; i++)
                for (int j = 0; j < _n; j++)
                {
                    double v = adjoint ? a[j, i] : a[i, j];
                    _lu[i, j] = i == j ? new Complex(v - shift.Real, -shift.Imaginary) : new Complex(v, 0.0);
                    scale = Math.Max(scale, Math.Abs(v));
                }
            scale = Math.Max(scale, Math.Abs(shift.Real) + Math.Abs(shift.Imaginary));
            ComplexLinearAlgebra.LuFactor(_lu, _piv, _n, scale, sigma, ratio);
        }

        public void Solve(Complex[] b, Complex[] x) => ComplexLinearAlgebra.LuSolve(_lu, _piv, _n, b, x);
    }
}

/// <summary>Banded real matrix as an Arnoldi operator: <c>O(n)</c> shifted solves for a narrow band (a Toeplitz or chain
/// structure), complex partial-pivot banded LU.</summary>
public sealed class BandedArnoldiOperator : IArnoldiOperator
{
    private readonly int _n, _kl, _ku;
    private readonly double[][] _rows;   // row i holds columns i-kl .. i+ku at index j - i + kl

    /// <summary>Make a banded operator. <paramref name="entry"/> gives A[i, j] for |band| positions only.</summary>
    public BandedArnoldiOperator(int n, int lower, int upper, Func<int, int, double> entry)
    {
        if (n < 1 || lower < 0 || upper < 0) throw new ArgumentOutOfRangeException(nameof(n));
        _n = n; _kl = lower; _ku = upper;
        _rows = new double[n][];
        var colSum = new double[n];
        for (int i = 0; i < n; i++)
        {
            _rows[i] = new double[lower + upper + 1];
            for (int j = Math.Max(0, i - lower); j <= Math.Min(n - 1, i + upper); j++)
            {
                double v = entry(i, j);
                _rows[i][j - i + lower] = v;
                colSum[j] += Math.Abs(v);
            }
        }
        double best = 0.0;
        foreach (double s in colSum) if (s > best) best = s;
        Norm1 = best;
    }

    /// <inheritdoc />
    public int Order => _n;

    /// <inheritdoc />
    public double Norm1 { get; }

    /// <inheritdoc />
    public void Multiply(Complex[] x, Complex[] y)
    {
        for (int i = 0; i < _n; i++)
        {
            double re = 0.0, im = 0.0;
            for (int j = Math.Max(0, i - _kl); j <= Math.Min(_n - 1, i + _ku); j++)
            {
                double v = _rows[i][j - i + _kl];
                re += v * x[j].Real;
                im += v * x[j].Imaginary;
            }
            y[i] = new Complex(re, im);
        }
    }

    /// <inheritdoc />
    public IShiftedSolver Factor(Complex sigma, double singularPivotRatio = 1e-10) => new BandLu(this, sigma, adjoint: false, singularPivotRatio);

    /// <inheritdoc />
    public IShiftedSolver FactorAdjoint(Complex sigma, double singularPivotRatio = 1e-10) => new BandLu(this, sigma, adjoint: true, singularPivotRatio);

    private sealed class BandLu : IShiftedSolver
    {
        private readonly int _n, _kl, _ku, _w;
        private readonly Complex[][] _w2;
        private readonly int[] _piv;

        public BandLu(BandedArnoldiOperator op, Complex sigma, bool adjoint, double ratio)
        {
            _n = op._n;
            // the adjoint of a banded matrix swaps the bands
            _kl = adjoint ? op._ku : op._kl;
            _ku = adjoint ? op._kl : op._ku;
            _w = 2 * _kl + _ku + 1;
            _w2 = new Complex[_n][];
            _piv = new int[_n];
            Complex shift = adjoint ? Complex.Conjugate(sigma) : sigma;
            double scale = Math.Abs(shift.Real) + Math.Abs(shift.Imaginary);
            for (int i = 0; i < _n; i++)
            {
                _w2[i] = new Complex[_w];
                for (int j = Math.Max(0, i - _kl); j <= Math.Min(_n - 1, i + _ku); j++)
                {
                    double v = adjoint ? op._rows[j][i - j + op._kl] : op._rows[i][j - i + op._kl];
                    scale = Math.Max(scale, Math.Abs(v));
                    _w2[i][j - i + _kl] = i == j ? new Complex(v - shift.Real, -shift.Imaginary) : new Complex(v, 0.0);
                }
            }
            double tiny = ratio * Math.Max(scale, 1e-300);
            for (int k = 0; k < _n; k++)
            {
                int last = Math.Min(_n - 1, k + _kl);
                int p = k; double best = ComplexLinearAlgebra.Abs(_w2[k][_kl]);
                for (int i = k + 1; i <= last; i++)
                {
                    double v = ComplexLinearAlgebra.Abs(_w2[i][k - i + _kl]);
                    if (v > best) { best = v; p = i; }
                }
                _piv[k] = p;
                if (!(best > tiny)) throw new SingularShiftException(sigma);
                int jmax = Math.Min(_n - 1, k + _kl + _ku);
                if (p != k)
                    for (int j = k; j <= jmax; j++)
                        (_w2[k][j - k + _kl], _w2[p][j - p + _kl]) = (_w2[p][j - p + _kl], _w2[k][j - k + _kl]);
                Complex pivot = _w2[k][_kl];
                for (int i = k + 1; i <= last; i++)
                {
                    Complex m = _w2[i][k - i + _kl] / pivot;
                    _w2[i][k - i + _kl] = m;
                    if (m == Complex.Zero) continue;
                    for (int j = k + 1; j <= jmax; j++)
                        _w2[i][j - i + _kl] -= m * _w2[k][j - k + _kl];
                }
            }
        }

        public void Solve(Complex[] b, Complex[] x)
        {
            if (!ReferenceEquals(b, x)) Array.Copy(b, x, _n);
            for (int k = 0; k < _n; k++)
            {
                int p = _piv[k];
                if (p != k) (x[k], x[p]) = (x[p], x[k]);
                int last = Math.Min(_n - 1, k + _kl);
                for (int i = k + 1; i <= last; i++) x[i] -= _w2[i][k - i + _kl] * x[k];
            }
            for (int k = _n - 1; k >= 0; k--)
            {
                int jmax = Math.Min(_n - 1, k + _kl + _ku);
                Complex s = x[k];
                for (int j = k + 1; j <= jmax; j++) s -= _w2[k][j - k + _kl] * x[j];
                x[k] = s / _w2[k][_kl];
            }
        }
    }
}

/// <summary>Small deterministic complex dense kernels shared by the Arnoldi code: LU, Hessenberg QR (Schur) and Givens steps.
/// Scalar loops in a fixed order, no parallelism, no library transcendental functions.</summary>
internal static class ComplexLinearAlgebra
{
    /// <summary>A pivot below this fraction of the matrix scale marks the shift as an eigenvalue (the solve would be noise).</summary>
    public const double SingularPivotRatio = 1e-10;

    public static double Abs(Complex z) => Math.Sqrt(z.Real * z.Real + z.Imaginary * z.Imaginary);

    /// <summary>Principal complex square root with IEEE add, multiply, divide and sqrt only.</summary>
    public static Complex Sqrt(Complex z)
    {
        double x = z.Real, y = z.Imaginary;
        double r = Abs(z);
        if (r == 0.0) return Complex.Zero;
        if (x >= 0.0)
        {
            double re = Math.Sqrt((r + x) / 2.0);
            return new Complex(re, y / (2.0 * re));
        }
        double im = Math.Sqrt((r - x) / 2.0);
        if (y < 0.0) im = -im;
        return new Complex(y / (2.0 * im), im);
    }

    public static void LuFactor(Complex[,] a, int[] piv, int n, double scale, Complex sigma, double ratio)
    {
        double tiny = ratio * Math.Max(scale, 1e-300);
        for (int k = 0; k < n; k++)
        {
            int p = k; double best = Abs(a[k, k]);
            for (int i = k + 1; i < n; i++)
            {
                double v = Abs(a[i, k]);
                if (v > best) { best = v; p = i; }
            }
            piv[k] = p;
            if (!(best > tiny)) throw new SingularShiftException(sigma);
            if (p != k)
                for (int j = k; j < n; j++) (a[k, j], a[p, j]) = (a[p, j], a[k, j]);   // earlier multipliers stay in place (progressive solve)
            Complex pivot = a[k, k];
            for (int i = k + 1; i < n; i++)
            {
                Complex m = a[i, k] / pivot;
                a[i, k] = m;
                if (m == Complex.Zero) continue;
                for (int j = k + 1; j < n; j++) a[i, j] -= m * a[k, j];
            }
        }
    }

    public static void LuSolve(Complex[,] lu, int[] piv, int n, Complex[] b, Complex[] x)
    {
        if (!ReferenceEquals(b, x)) Array.Copy(b, x, n);
        for (int k = 0; k < n; k++)
        {
            int p = piv[k];
            if (p != k) (x[k], x[p]) = (x[p], x[k]);
            for (int i = k + 1; i < n; i++) x[i] -= lu[i, k] * x[k];
        }
        for (int k = n - 1; k >= 0; k--)
        {
            Complex s = x[k];
            for (int j = k + 1; j < n; j++) s -= lu[k, j] * x[j];
            x[k] = s / lu[k, k];
        }
    }

    /// <summary>One explicit shifted QR step on the active window rows/columns [l, hi] of an upper Hessenberg matrix of order m,
    /// as a similarity on the whole matrix; the unitary factor is accumulated into <paramref name="q"/> (m x m, columns).</summary>
    public static void QrStep(Complex[,] h, Complex[,] q, int l, int hi, Complex mu, int m)
    {
        for (int i = l; i <= hi; i++) h[i, i] -= mu;
        int count = hi - l;
        var cs = new Complex[count];
        var sn = new Complex[count];
        for (int k = l; k < hi; k++)
        {
            Complex a = h[k, k], b = h[k + 1, k];
            double norm = Math.Sqrt(a.Real * a.Real + a.Imaginary * a.Imaginary + b.Real * b.Real + b.Imaginary * b.Imaginary);
            Complex c, s;
            if (norm == 0.0) { c = Complex.One; s = Complex.Zero; }
            else { c = a / norm; s = b / norm; }
            cs[k - l] = c; sn[k - l] = s;
            Complex cc = Complex.Conjugate(c), sc = Complex.Conjugate(s);
            for (int j = k; j < m; j++)
            {
                Complex x = h[k, j], y = h[k + 1, j];
                h[k, j] = cc * x + sc * y;
                h[k + 1, j] = -s * x + c * y;
            }
        }
        for (int k = l; k < hi; k++)
        {
            Complex c = cs[k - l], s = sn[k - l];
            Complex sc = Complex.Conjugate(s), cc = Complex.Conjugate(c);
            int top = Math.Min(k + 1, hi);
            for (int i = 0; i <= top; i++)
            {
                Complex x = h[i, k], y = h[i, k + 1];
                h[i, k] = x * c + y * s;
                h[i, k + 1] = -x * sc + y * cc;
            }
            for (int i = 0; i < m; i++)
            {
                Complex x = q[i, k], y = q[i, k + 1];
                q[i, k] = x * c + y * s;
                q[i, k + 1] = -x * sc + y * cc;
            }
        }
        for (int i = l; i <= hi; i++) h[i, i] += mu;
    }

    /// <summary>Complex Schur form of an upper Hessenberg matrix: <c>H = Z T Z^H</c> with T upper triangular. The input is
    /// overwritten with T. Returns false if the iteration did not converge.</summary>
    public static bool Schur(Complex[,] h, Complex[,] z, int m)
    {
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++) z[i, j] = i == j ? Complex.One : Complex.Zero;
        const double eps = 2.220446049250313e-16;
        int hi = m - 1, iter = 0, total = 0;
        double norm = 0.0;
        for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) norm = Math.Max(norm, Abs(h[i, j]));
        if (norm == 0.0) norm = 1.0;
        while (hi >= 0)
        {
            int l = hi;
            while (l > 0)
            {
                double s = Abs(h[l - 1, l - 1]) + Abs(h[l, l]);
                if (s == 0.0) s = norm;
                if (Abs(h[l, l - 1]) <= eps * s) { h[l, l - 1] = Complex.Zero; break; }
                l--;
            }
            if (l == hi) { hi--; iter = 0; continue; }
            if (++total > 40 * m * m + 1000) return false;
            Complex mu;
            iter++;
            if (iter % 11 == 10)
                mu = new Complex(Abs(h[hi, hi - 1]) + (hi > 1 ? Abs(h[hi - 1, hi - 2]) : 0.0), 0.0) + h[hi, hi];
            else
            {
                // Wilkinson shift: the eigenvalue of the trailing 2x2 block closest to its (hi, hi) entry
                Complex a = h[hi - 1, hi - 1], b = h[hi - 1, hi], c = h[hi, hi - 1], d = h[hi, hi];
                Complex half = (a - d) / 2.0;
                Complex disc = Sqrt(half * half + b * c);
                Complex e1 = (a + d) / 2.0 + disc, e2 = (a + d) / 2.0 - disc;
                mu = Abs(e1 - d) <= Abs(e2 - d) ? e1 : e2;
            }
            QrStep(h, z, l, hi, mu, m);
        }
        return true;
    }
}

/// <summary>Options of <see cref="ArnoldiEigen.Compute"/>.</summary>
/// <param name="Count">Number of eigenvalues wanted (nearest to the shift).</param>
/// <param name="Ncv">Krylov basis size (0: chosen as max(3 Count + 10, 40), at most n).</param>
/// <param name="MaxRestarts">Cap on implicit restarts.</param>
/// <param name="RelativeTolerance">Residual gate, relative to the 1-norm of A: a returned pair needs
/// <c>||A v - lambda v|| &lt;= RelativeTolerance * ||A||_1 * ||v||</c>.</param>
/// <param name="Seed">Seed of the start vector (R4-02 counter-based stream; recorded in the result).</param>
public sealed record ArnoldiOptions(int Count, int Ncv = 0, int MaxRestarts = 300, double RelativeTolerance = 1e-9, ulong Seed = ArnoldiOptions.DefaultSeed)
{
    /// <summary>The documented fixed seed of the start vector (the ASCII text "MODALFUL").</summary>
    public const ulong DefaultSeed = 0x4D4F44414C46554CUL;

    /// <summary>The R4-02 domain string of the start vector.</summary>
    public const string StartDomain = "modal-full/arnoldi/start";

    /// <summary>The R4-02 domain string of the vectors injected after a breakdown.</summary>
    public const string RestartDomain = "modal-full/arnoldi/breakdown";
}

/// <summary>One eigenpair returned by <see cref="ArnoldiEigen"/>.</summary>
/// <param name="Value">The eigenvalue.</param>
/// <param name="Vector">The unit right eigenvector.</param>
/// <param name="RelativeResidual"><c>||A v - lambda v|| / ||A||_1</c> evaluated explicitly.</param>
/// <param name="PassesGate">Whether the residual is within the requested tolerance.</param>
public sealed record ArnoldiPair(Complex Value, Complex[] Vector, double RelativeResidual, bool PassesGate);

/// <summary>The result of an Arnoldi run.</summary>
/// <param name="Pairs">The pairs nearest to the shift, ordered by distance, then real part, then imaginary part.</param>
/// <param name="Shift">The shift actually factorised (it differs from the requested one only after a singular pivot).</param>
/// <param name="Converged">Whether all requested pairs met the residual gate within the restart cap.</param>
/// <param name="Restarts">Implicit restarts performed.</param>
/// <param name="MatVecs">Operator applications (shift-invert solves).</param>
/// <param name="Ncv">Basis size used.</param>
/// <param name="ShiftPerturbed">True when the requested shift was moved because it was an eigenvalue.</param>
public sealed record ArnoldiResult(IReadOnlyList<ArnoldiPair> Pairs, Complex Shift, bool Converged, int Restarts, int MatVecs, int Ncv, bool ShiftPerturbed);

/// <summary>
/// R4-12: shift-invert implicitly restarted Arnoldi for the eigenvalues of a large real matrix nearest to a shift.
/// <para>The method is Sorensen's implicit restart with exact shifts, written out: build a Krylov basis of order ncv for
/// <c>(A - sigma I)^-1</c> with classical Gram-Schmidt run twice (DGKS); take the complex Schur form of the Hessenberg
/// matrix; the wanted Ritz values are the largest in modulus (nearest <c>sigma</c>); the other ncv - k Ritz values are applied as
/// shifted QR steps; the basis is compressed to k = Count + (ncv - Count) / 2 columns and expanded again. That restart rule is
/// fixed. Convergence is decided on the explicit residual <c>||A x - lambda x||</c> of the Rayleigh-quotient refined pair, never
/// on the Ritz estimate alone.</para>
/// <para>Determinism: single threaded, scalar loops in a fixed order, IEEE add/multiply/divide/sqrt only, the start vector is
/// drawn from the R4-02 counter-based stream (seed, domain and index recorded) and the ordering of Ritz values breaks every
/// tie on the (modulus, real, imaginary, index) key. The result is bit-identical across runs and thread counts.</para>
/// </summary>
public static class ArnoldiEigen
{
    /// <summary>Compute the <see cref="ArnoldiOptions.Count"/> eigenvalues of <paramref name="op"/> nearest <paramref name="sigma"/>.</summary>
    public static ArnoldiResult Compute(IArnoldiOperator op, Complex sigma, ArnoldiOptions options)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(options);
        int n = op.Order;
        int nev = Math.Min(options.Count, n);
        if (nev < 1) throw new ArgumentOutOfRangeException(nameof(options), "Count must be at least 1.");
        if (!(options.RelativeTolerance > 0)) throw new ArgumentOutOfRangeException(nameof(options), "RelativeTolerance must be above 0.");
        int ncv = options.Ncv > 0 ? options.Ncv : Math.Max(3 * nev + 10, 40);
        ncv = Math.Min(ncv, n);
        if (ncv <= nev && nev < n) ncv = Math.Min(n, nev + 2);

        // factorise, moving a singular shift by a fixed rule (relative offsets 1e-6, 1e-5, 1e-4)
        IShiftedSolver? solver = null;
        Complex used = sigma;
        bool perturbed = false;
        double[] offsets = { 0.0, 1e-6, 1e-5, 1e-4 };
        for (int t = 0; t < offsets.Length && solver is null; t++)
        {
            used = t == 0 ? sigma : sigma + new Complex(offsets[t] * (1.0 + ComplexLinearAlgebra.Abs(sigma)), 0.5 * offsets[t] * (1.0 + ComplexLinearAlgebra.Abs(sigma)));
            try { solver = op.Factor(used); perturbed = t > 0; }
            catch (SingularShiftException) { solver = null; }
        }
        if (solver is null) throw new SingularShiftException(sigma);

        var v = new Complex[ncv + 1][];
        for (int i = 0; i <= ncv; i++) v[i] = new Complex[n];
        var h = new Complex[ncv + 1, ncv];
        int matvecs = 0;
        var w = new Complex[n];
        var tmp = new Complex[n];

        // start vector from the R4-02 stream: real and imaginary parts are consecutive draws
        var stream = new CounterStream(new StreamKey(options.Seed, ArnoldiOptions.StartDomain));
        double nrm = 0.0;
        for (int i = 0; i < n; i++)
        {
            v[0][i] = new Complex(stream.Uniform((ulong)(2 * i)) - 0.5, stream.Uniform((ulong)(2 * i + 1)) - 0.5);
            nrm += v[0][i].Real * v[0][i].Real + v[0][i].Imaginary * v[0][i].Imaginary;
        }
        nrm = Math.Sqrt(nrm);
        for (int i = 0; i < n; i++) v[0][i] /= nrm;

        int breakdowns = 0;
        int k = 0;                  // columns currently held as a valid Arnoldi factorisation
        int restarts = 0;
        IReadOnlyList<ArnoldiPair> best = Array.Empty<ArnoldiPair>();
        bool converged = false;

        while (true)
        {
            // expand from k to ncv
            for (int j = k; j < ncv; j++)
            {
                solver.Solve(v[j], w);
                matvecs++;
                double w0 = Norm(w);
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 0; i <= j; i++)
                    {
                        Complex d = Complex.Zero;
                        for (int r = 0; r < n; r++) d += Complex.Conjugate(v[i][r]) * w[r];
                        h[i, j] = pass == 0 ? d : h[i, j] + d;
                        for (int r = 0; r < n; r++) w[r] -= d * v[i][r];
                    }
                }
                double beta = Norm(w);
                if (j + 1 >= n) { h[j + 1, j] = Complex.Zero; break; }
                if (beta <= 1e-13 * w0 || beta == 0.0)
                {
                    // invariant subspace: decouple and continue with a fresh deterministic vector
                    h[j + 1, j] = Complex.Zero;
                    var rs = new CounterStream(new StreamKey(options.Seed, ArnoldiOptions.RestartDomain, (ulong)breakdowns++));
                    for (int r = 0; r < n; r++) tmp[r] = new Complex(rs.Uniform((ulong)(2 * r)) - 0.5, rs.Uniform((ulong)(2 * r + 1)) - 0.5);
                    for (int pass = 0; pass < 2; pass++)
                        for (int i = 0; i <= j; i++)
                        {
                            Complex d = Complex.Zero;
                            for (int r = 0; r < n; r++) d += Complex.Conjugate(v[i][r]) * tmp[r];
                            for (int r = 0; r < n; r++) tmp[r] -= d * v[i][r];
                        }
                    double tn = Norm(tmp);
                    for (int r = 0; r < n; r++) v[j + 1][r] = tmp[r] / tn;
                }
                else
                {
                    h[j + 1, j] = new Complex(beta, 0.0);
                    for (int r = 0; r < n; r++) v[j + 1][r] = w[r] / beta;
                }
            }

            int m = ncv;
            // Schur form of the leading m x m Hessenberg block
            var t = new Complex[m, m];
            for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) t[i, j] = i <= j + 1 ? h[i, j] : Complex.Zero;
            var z = new Complex[m, m];
            if (!ComplexLinearAlgebra.Schur(t, z, m))
                return new ArnoldiResult(best, used, false, restarts, matvecs, ncv, perturbed);

            // order the Ritz values: largest modulus first (nearest the shift)
            var order = new int[m];
            for (int i = 0; i < m; i++) order[i] = i;
            Complex[] theta = new Complex[m];
            for (int i = 0; i < m; i++) theta[i] = t[i, i];
            Array.Sort(order, (a, b) => CompareRitz(theta, a, b));

            // explicit residuals for the wanted pairs
            var pairs = new List<ArnoldiPair>(nev);
            for (int wi = 0; wi < nev; wi++)
            {
                int idx = order[wi];
                Complex[] y = RitzVectorInSchurBasis(t, idx, m);       // eigenvector of T
                var yv = new Complex[m];
                for (int i = 0; i < m; i++)
                {
                    Complex s = Complex.Zero;
                    for (int j = 0; j <= idx; j++) s += z[i, j] * y[j];
                    yv[i] = s;
                }
                var x = new Complex[n];
                for (int j = 0; j < m; j++)
                    for (int r = 0; r < n; r++) x[r] += yv[j] * v[j][r];
                double xn = Norm(x);
                for (int r = 0; r < n; r++) x[r] /= xn;
                op.Multiply(x, tmp);
                matvecs++;
                Complex rq = Complex.Zero;
                for (int r = 0; r < n; r++) rq += Complex.Conjugate(x[r]) * tmp[r];
                double res = 0.0;
                for (int r = 0; r < n; r++)
                {
                    Complex d = tmp[r] - rq * x[r];
                    res += d.Real * d.Real + d.Imaginary * d.Imaginary;
                }
                res = Math.Sqrt(res) / Math.Max(op.Norm1, 1e-300);
                pairs.Add(new ArnoldiPair(rq, x, res, res <= options.RelativeTolerance));
            }
            pairs.Sort((a, b) => CompareEigen(a.Value, b.Value, sigma));
            best = pairs;
            if (pairs.All(p => p.PassesGate)) { converged = true; break; }
            if (ncv >= n || restarts >= options.MaxRestarts) break;

            // implicit restart with exact shifts: keep k0 columns, shift away the other m - k0 Ritz values
            int k0 = nev + (m - nev) / 2;
            if (k0 > m - 2) k0 = m - 2;
            if (k0 < nev) k0 = nev;
            var hm = new Complex[m, m];
            for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) hm[i, j] = i <= j + 1 ? h[i, j] : Complex.Zero;
            var q = new Complex[m, m];
            for (int i = 0; i < m; i++) q[i, i] = Complex.One;
            for (int s = k0; s < m; s++)
                ComplexLinearAlgebra.QrStep(hm, q, 0, m - 1, theta[order[s]], m);

            // compress: V_k = V_m Q[:, 0..k0-1]; residual direction from column k0 and the old residual vector
            var vn = new Complex[k0 + 1][];
            for (int c = 0; c <= k0; c++)
            {
                vn[c] = new Complex[n];
                for (int j = 0; j < m; j++)
                {
                    Complex qj = q[j, c];
                    if (qj == Complex.Zero) continue;
                    for (int r = 0; r < n; r++) vn[c][r] += qj * v[j][r];
                }
            }
            Complex betaM = h[m, m - 1];
            Complex hk = hm[k0, k0 - 1];
            Complex qLast = q[m - 1, k0 - 1];
            var rvec = new Complex[n];
            for (int r = 0; r < n; r++) rvec[r] = vn[k0][r] * hk + v[m][r] * (betaM * qLast);
            double rn = Norm(rvec);
            Complex[] oldResidual = v[m];
            for (int c = 0; c < k0; c++) Array.Copy(vn[c], v[c], n);
            if (rn <= 1e-13 || double.IsNaN(rn))
            {
                // the kept space is invariant: converged on it, or break down and refill
                for (int c = 0; c < k0; c++) for (int d = 0; d < k0; d++) h[c, d] = d <= c + 1 ? hm[c, d] : Complex.Zero;
                h[k0, k0 - 1] = Complex.Zero;
                var rs = new CounterStream(new StreamKey(options.Seed, ArnoldiOptions.RestartDomain, (ulong)breakdowns++));
                for (int r = 0; r < n; r++) tmp[r] = new Complex(rs.Uniform((ulong)(2 * r)) - 0.5, rs.Uniform((ulong)(2 * r + 1)) - 0.5);
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < k0; i++)
                    {
                        Complex d = Complex.Zero;
                        for (int r = 0; r < n; r++) d += Complex.Conjugate(v[i][r]) * tmp[r];
                        for (int r = 0; r < n; r++) tmp[r] -= d * v[i][r];
                    }
                double tn = Norm(tmp);
                for (int r = 0; r < n; r++) v[k0][r] = tmp[r] / tn;
            }
            else
            {
                for (int c = 0; c < k0; c++) for (int d = 0; d < k0; d++) h[c, d] = d <= c + 1 ? hm[c, d] : Complex.Zero;
                h[k0, k0 - 1] = new Complex(rn, 0.0);
                for (int r = 0; r < n; r++) v[k0][r] = rvec[r] / rn;
            }
            _ = oldResidual;
            // clear stale entries below the new factorisation
            for (int i = 0; i <= ncv; i++)
                for (int j = k0; j < ncv; j++)
                    if (!(i == k0 && j == k0 - 1)) h[i, j] = Complex.Zero;
            k = k0;
            restarts++;
        }

        // convert Ritz values of the inverse operator to... (pairs already hold Rayleigh quotients of A)
        return new ArnoldiResult(best, used, converged, restarts, matvecs, ncv, perturbed);
    }

    private static double Norm(Complex[] x)
    {
        double s = 0.0;
        for (int i = 0; i < x.Length; i++) s += x[i].Real * x[i].Real + x[i].Imaginary * x[i].Imaginary;
        return Math.Sqrt(s);
    }

    private static int CompareRitz(Complex[] theta, int a, int b)
    {
        double ma = ComplexLinearAlgebra.Abs(theta[a]), mb = ComplexLinearAlgebra.Abs(theta[b]);
        if (ma != mb) return mb.CompareTo(ma);                                  // larger modulus first
        if (theta[a].Real != theta[b].Real) return theta[b].Real.CompareTo(theta[a].Real);
        if (theta[a].Imaginary != theta[b].Imaginary) return theta[b].Imaginary.CompareTo(theta[a].Imaginary);
        return a.CompareTo(b);
    }

    private static int CompareEigen(Complex a, Complex b, Complex sigma)
    {
        double da = ComplexLinearAlgebra.Abs(a - sigma), db = ComplexLinearAlgebra.Abs(b - sigma);
        if (da != db) return da.CompareTo(db);
        if (a.Real != b.Real) return a.Real.CompareTo(b.Real);
        return a.Imaginary.CompareTo(b.Imaginary);
    }

    /// <summary>The eigenvector of an upper triangular T for its diagonal entry <paramref name="idx"/>, by back substitution
    /// (zero below the entry), scaled to unit length.</summary>
    private static Complex[] RitzVectorInSchurBasis(Complex[,] t, int idx, int m)
    {
        var y = new Complex[m];
        y[idx] = Complex.One;
        Complex lam = t[idx, idx];
        double scale = 0.0;
        for (int i = 0; i < m; i++) scale = Math.Max(scale, ComplexLinearAlgebra.Abs(t[i, i]));
        double floor = 2.220446049250313e-16 * Math.Max(scale, 1e-300);
        for (int j = idx - 1; j >= 0; j--)
        {
            Complex s = Complex.Zero;
            for (int l = j + 1; l <= idx; l++) s += t[j, l] * y[l];
            Complex den = t[j, j] - lam;
            if (ComplexLinearAlgebra.Abs(den) < floor) den = new Complex(floor, 0.0);
            y[j] = -s / den;
        }
        double nrm = 0.0;
        for (int i = 0; i <= idx; i++) nrm += y[i].Real * y[i].Real + y[i].Imaginary * y[i].Imaginary;
        nrm = Math.Sqrt(nrm);
        for (int i = 0; i <= idx; i++) y[i] /= nrm;
        return y;
    }
}
