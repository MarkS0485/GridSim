using System.Numerics;

namespace GridSim.Core.PowerFlow.Equivalents;

/// <summary>
/// The pure arithmetic of the network equivalents (<see cref="NetworkReduction"/>), each piece callable on its own for
/// direct tests. Matrices are given as admittance cells keyed (row, column) by matrix index, as
/// <see cref="SparseKronReduction.Reduce"/> takes them; every position without a cell is zero.
/// </summary>
public static class WardMath
{
    /// <summary>
    /// The Ward equivalent admittance Y_eq = Y_KK - Y_KE (Y_EE)^-1 Y_EK among the kept nodes <paramref name="keep"/>, in
    /// that order: the Kron reduction of <see cref="SparseKronReduction.Reduce"/> (called unchanged), which eliminates
    /// every other node in ascending index order.
    /// </summary>
    public static Complex[,] EquivalentAdmittance(int n, IReadOnlyDictionary<(int Row, int Col), Complex> cells, IReadOnlyList<int> keep)
        => SparseKronReduction.Reduce(n, cells, keep);

    /// <summary>
    /// The external currents transferred to the kept nodes, dI_K = -Y_KE (Y_EE)^-1 I_E, where <paramref name="injections"/>
    /// holds the current injected at each node (length n; the entries at kept nodes are not read).
    ///
    /// <para><b>How.</b> The same Kron reduction on the matrix bordered by one extra kept column z (index n) that holds
    /// I_E in the external rows, with a unit diagonal and an otherwise zero row z:
    /// <c>M = [[Y_KK, Y_KE, 0], [Y_EK, Y_EE, I_E], [0, 0, 1]]</c>. Its Schur complement on the kept nodes and z has, in
    /// column z, <c>0 - Y_KE (Y_EE)^-1 I_E = dI_K</c>. Row z is zero off the diagonal, so it is never an update row and
    /// column z never feeds another column: the kept block of the bordered reduction is the plain reduction's, value for
    /// value (a test checks it bit for bit), and column z is the forward elimination of I_E in the same order with the same
    /// pivots. So dI_K comes from the reviewed elimination code, sparse and in a fixed order.</para>
    /// </summary>
    public static Complex[] TransferredCurrents(int n, IReadOnlyDictionary<(int Row, int Col), Complex> cells,
        IReadOnlyList<int> keep, IReadOnlyList<Complex> injections)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(injections);
        if (injections.Count != n) throw new ArgumentException($"injections has {injections.Count} entries for {n} nodes.", nameof(injections));
        var keepSet = new HashSet<int>(keep);
        var bordered = new Dictionary<(int Row, int Col), Complex>(cells.Count + n + 1);
        foreach (KeyValuePair<(int Row, int Col), Complex> c in cells) bordered[c.Key] = c.Value;
        for (int e = 0; e < n; e++)
            if (!keepSet.Contains(e) && injections[e] != Complex.Zero) bordered[(e, n)] = Complex.Zero + injections[e];
        bordered[(n, n)] = Complex.One;
        var keepZ = new List<int>(keep) { n };
        Complex[,] r = SparseKronReduction.Reduce(n + 1, bordered, keepZ);
        int z = keep.Count;
        var dI = new Complex[keep.Count];
        for (int a = 0; a < keep.Count; a++) dI[a] = r[a, z];
        return dI;
    }

    /// <summary>The sum of row <paramref name="row"/> of <paramref name="m"/>, columns in ascending order: the equivalent
    /// shunt to ground at that node.</summary>
    public static Complex RowSum(Complex[,] m, int row)
    {
        Complex s = Complex.Zero;
        for (int j = 0; j < m.GetLength(1); j++) s += m[row, j];
        return s;
    }

    /// <summary>
    /// The series admittance of the equivalent branch between kept nodes i and j: <c>y = -(Y_ij + Y_ji) / 2</c>. Its pi
    /// stamp (tap 1, no charging) puts -y off the diagonal and +y on both diagonals; the row-sum shunt
    /// (<see cref="RowSum"/>) then restores each diagonal to Y_ii.
    /// </summary>
    public static Complex BranchAdmittance(Complex[,] yEq, int i, int j) => -0.5 * (yEq[i, j] + yEq[j, i]);

    /// <summary>True when Y_eq is reciprocal to the given relative tolerance: |Y_ij - Y_ji| &lt;= tol |Y_ij| for every pair.
    /// <paramref name="pair"/> is the first pair that fails.</summary>
    public static bool IsReciprocal(Complex[,] yEq, double relativeTolerance, out (int I, int J) pair)
    {
        int m = yEq.GetLength(0);
        for (int i = 0; i < m; i++)
            for (int j = i + 1; j < m; j++)
                if ((yEq[i, j] - yEq[j, i]).Magnitude > relativeTolerance * yEq[i, j].Magnitude) { pair = (i, j); return false; }
        pair = (-1, -1);
        return true;
    }

    /// <summary>The zero-power-balance network of one REI group (Dimo 1975; Tinney and Powell 1977).</summary>
    /// <param name="Sr">The aggregated injection S_R = sum S_k (pu).</param>
    /// <param name="Ir">The aggregated current I_R = sum I_k, I_k = conj(S_k / V_k).</param>
    /// <param name="Vr">The REI bus voltage V_R = S_R / conj(I_R).</param>
    /// <param name="Yk">The admittance of each branch k-0, y_k = -conj(S_k) / |V_k|^2.</param>
    /// <param name="Yr">The admittance of the branch 0-R, y_R = conj(S_R) / |V_R|^2.</param>
    public sealed record Zpb(Complex Sr, Complex Ir, Complex Vr, IReadOnlyList<Complex> Yk, Complex Yr);

    /// <summary>
    /// The REI zero-power-balance network for injections <paramref name="s"/> (pu) at buses of voltage
    /// <paramref name="v"/>. The branch k-0 delivers exactly I_k into bus k when node 0 is at zero voltage
    /// (y_k V_k = -conj(S_k) / conj(V_k) = -I_k), and the branch 0-R carries I_R out of R, so KCL at node 0 reads
    /// sum y_k V_k + y_R V_R = -I_R + I_R = 0: node 0 sits at V_0 = 0 and the network absorbs no power. Sums run in the
    /// order given.
    /// </summary>
    public static Zpb ZeroPowerBalance(IReadOnlyList<Complex> s, IReadOnlyList<Complex> v)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(v);
        if (s.Count != v.Count || s.Count == 0) throw new ArgumentException("one voltage per injection, and at least one injection, are needed.");
        Complex sr = Complex.Zero, ir = Complex.Zero;
        var yk = new Complex[s.Count];
        for (int k = 0; k < s.Count; k++)
        {
            sr += s[k];
            ir += Complex.Conjugate(s[k] / v[k]);
            yk[k] = -Complex.Conjugate(s[k]) / Norm(v[k]);
        }
        Complex vr = sr / Complex.Conjugate(ir);
        Complex yr = Complex.Conjugate(sr) / Norm(vr);
        return new Zpb(sr, ir, vr, yk, yr);
    }

    /// <summary>The ZPB node voltage V_0 = (sum y_k V_k + y_R V_R) / (sum y_k + y_R): zero in exact arithmetic.</summary>
    public static Complex ZpbNodeVoltage(Zpb z, IReadOnlyList<Complex> v)
    {
        ArgumentNullException.ThrowIfNull(z);
        Complex num = Complex.Zero, den = Complex.Zero;
        for (int k = 0; k < v.Count; k++) { num += z.Yk[k] * v[k]; den += z.Yk[k]; }
        num += z.Yr * z.Vr;
        den += z.Yr;
        return num / den;
    }

    /// <summary>|z|^2 as Re^2 + Im^2 (no square root, so no extra rounding).</summary>
    public static double Norm(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;
}
