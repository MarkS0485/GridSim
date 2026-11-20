using System.Numerics;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Lines;

/// <summary>Complex and real small-matrix steps of the reduction (fixed elimination order, partial pivoting).</summary>
public static class SequenceReduction
{
    /// <summary>Solve A X = B for complex square A by Gaussian elimination with partial pivoting (inputs are not modified).</summary>
    public static Complex[,] Solve(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1);
        var A = (Complex[,])a.Clone(); var X = (Complex[,])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; double best = Complex.Abs(A[c, c]);
            for (int r = c + 1; r < n; r++) if (Complex.Abs(A[r, c]) > best) { best = Complex.Abs(A[r, c]); piv = r; }
            if (!(best > 0.0)) throw DomainRefusal.Mark(new InvalidOperationException("The matrix is singular."));
            if (piv != c)
            {
                for (int j = 0; j < n; j++) (A[c, j], A[piv, j]) = (A[piv, j], A[c, j]);
                for (int j = 0; j < m; j++) (X[c, j], X[piv, j]) = (X[piv, j], X[c, j]);
            }
            for (int r = c + 1; r < n; r++)
            {
                Complex f = A[r, c] / A[c, c];
                for (int j = c; j < n; j++) A[r, j] -= f * A[c, j];
                for (int j = 0; j < m; j++) X[r, j] -= f * X[c, j];
            }
        }
        for (int j = 0; j < m; j++)
            for (int r = n - 1; r >= 0; r--)
            {
                Complex s = X[r, j];
                for (int k = r + 1; k < n; k++) s -= A[r, k] * X[k, j];
                X[r, j] = s / A[r, r];
            }
        return X;
    }

    /// <summary>The submatrix with the given rows and columns.</summary>
    public static Complex[,] Sub(Complex[,] a, IReadOnlyList<int> rows, IReadOnlyList<int> cols)
    {
        var r = new Complex[rows.Count, cols.Count];
        for (int i = 0; i < rows.Count; i++) for (int j = 0; j < cols.Count; j++) r[i, j] = a[rows[i], cols[j]];
        return r;
    }

    /// <summary>
    /// Kron reduction of grounded wires: Z_red = Z_pp - Z_pg Z_gg^-1 Z_gp, with phases p and ground wires g kept in
    /// their input order.
    /// </summary>
    public static Complex[,] Kron(Complex[,] z, IReadOnlyList<int> phases, IReadOnlyList<int> grounds)
    {
        var zpp = Sub(z, phases, phases);
        if (grounds.Count == 0) return zpp;
        var zpg = Sub(z, phases, grounds); var zgg = Sub(z, grounds, grounds); var zgp = Sub(z, grounds, phases);
        var corr = Mul(zpg, Solve(zgg, zgp));
        for (int i = 0; i < zpp.GetLength(0); i++) for (int j = 0; j < zpp.GetLength(1); j++) zpp[i, j] -= corr[i, j];
        return zpp;
    }

    /// <summary>Matrix product.</summary>
    public static Complex[,] Mul(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), k = a.GetLength(1), m = b.GetLength(1);
        var r = new Complex[n, m];
        for (int i = 0; i < n; i++) for (int j = 0; j < m; j++)
        { Complex s = Complex.Zero; for (int l = 0; l < k; l++) s += a[i, l] * b[l, j]; r[i, j] = s; }
        return r;
    }

    /// <summary>Full transposition of three phases: the average over the three cyclic permutations (self terms averaged, mutual terms averaged).</summary>
    public static Complex[,] TransposeAverage3(Complex[,] z)
    {
        Complex s = (z[0, 0] + z[1, 1] + z[2, 2]) / 3.0;
        Complex m = (z[0, 1] + z[1, 2] + z[0, 2] + z[1, 0] + z[2, 1] + z[2, 0]) / 6.0;
        var r = new Complex[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i, j] = i == j ? s : m;
        return r;
    }

    /// <summary>Real-matrix version of <see cref="TransposeAverage3"/>.</summary>
    public static double[,] TransposeAverage3(double[,] c)
    {
        double s = (c[0, 0] + c[1, 1] + c[2, 2]) / 3.0;
        double m = (c[0, 1] + c[1, 2] + c[0, 2] + c[1, 0] + c[2, 1] + c[2, 0]) / 6.0;
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i, j] = i == j ? s : m;
        return r;
    }

    /// <summary>Positive and zero sequence of a transposed (symmetric) matrix: Z1 = Zs - Zm, Z0 = Zs + 2 Zm.</summary>
    public static (Complex Z1, Complex Z0) Sequence(Complex[,] sym) => (sym[0, 0] - sym[0, 1], sym[0, 0] + 2.0 * sym[0, 1]);

    /// <summary>Real-matrix version of <see cref="Sequence(Complex[,])"/>.</summary>
    public static (double C1, double C0) Sequence(double[,] sym) => (sym[0, 0] - sym[0, 1], sym[0, 0] + 2.0 * sym[0, 1]);

    /// <summary>Inverse of a real symmetric positive matrix by the complex solver (identity right-hand side).</summary>
    public static double[,] InvertReal(double[,] a)
    {
        int n = a.GetLength(0);
        var ca = new Complex[n, n]; var id = new Complex[n, n];
        for (int i = 0; i < n; i++) { id[i, i] = Complex.One; for (int j = 0; j < n; j++) ca[i, j] = a[i, j]; }
        var x = Solve(ca, id);
        var r = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) r[i, j] = x[i, j].Real;
        return r;
    }
}
