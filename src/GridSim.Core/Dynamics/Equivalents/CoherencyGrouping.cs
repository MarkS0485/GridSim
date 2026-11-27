using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Equivalents;

/// <summary>The slow-coherency grouping of a set of machines.</summary>
/// <param name="Groups">The coherent groups, each a list of generator indices (into <c>model.Generators</c>, or into the
/// machine arrays for <see cref="CoherencyGrouping.SlowCoherency"/>) in ascending order; groups ordered by their first
/// member.</param>
/// <param name="ReferenceMachines">The reference machine of each slow mode, in pivot order (same indexing as the groups).</param>
/// <param name="SlowEigenvalues">The r smallest eigenvalues of M^-1 K_s (1/s^2 up to the factor omega_s), ascending.</param>
/// <param name="NextEigenvalue">The (r+1)-th eigenvalue (NaN when r is the machine count): the gap to the fast modes.</param>
public sealed record CoherencyResult(
    IReadOnlyList<IReadOnlyList<int>> Groups,
    IReadOnlyList<int> ReferenceMachines,
    IReadOnlyList<double> SlowEigenvalues,
    double NextEigenvalue);

/// <summary>
/// RC5 WP31: coherent machine groups by the slow-coherency method of Chow (J. H. Chow, ed., <i>Time-Scale Modeling of
/// Dynamic Networks with Applications to Power Systems</i>, Lecture Notes in Control and Information Sciences 46,
/// Springer, 1982), with the grouping algorithm of B. Avramovic, P. V. Kokotovic, J. R. Winkelman and J. H. Chow, "Area
/// decomposition for electromechanical models of power systems", Automatica 16, 1980, as summarised in J. H. Chow,
/// R. Galarza, P. Accari and W. W. Price, "Inertial and slow coherency aggregation algorithms for power system dynamic
/// model reduction", IEEE Trans. Power Systems 10(2), 1995.
///
/// <para><b>The model.</b> The classical swing linearised at the operating point, <c>M d2(delta)/dt2 = -omega_s K delta</c>,
/// with M = diag(2H) on the system base and K the synchronising Laplacian <c>K_ij = -|E_i||E_j|(B_ij cos d_ij - G_ij sin
/// d_ij)</c> of the machine internal nodes: the matrix GridSim's swing-only small-signal assembler
/// (<see cref="SmallSignal.SmallSignalAssembler.FromSolvedCase"/>, the classical part of <c>modal</c> and <c>modal-full</c>)
/// builds, from the same preparation (<see cref="ClassicalInit.FromSolvedCase"/>: loads as constant admittances, each
/// machine behind X'd, Kron-reduced to the internal nodes). Transfer conductances make K unsymmetric (on Kundur's
/// two-area system by 13 % of max |K_ij|); the grouping uses its symmetric part K_s = (K + K^T)/2, so the eigenproblem is the symmetric
/// <c>M^-1/2 K_s M^-1/2</c> (cyclic Jacobi, <see cref="SymmetricEigen"/>), whose eigenvectors U give the modal shapes
/// V = M^-1/2 U.</para>
///
/// <para><b>The algorithm</b> (as stated in the 1995 summary, steps 1 to 4).
/// <list type="number">
///   <item>Take the r slowest modes: the columns of V for the r smallest eigenvalues (the first is the rigid
///   centre-of-inertia mode; K's rows sum to 0, but K_s's need not, so its eigenvalue is near 0 rather than 0: -0.0022 on
///   Kundur's system).</item>
///   <item>Gaussian elimination with complete pivoting on V (n x r): at each step the largest |entry| among the rows and
///   columns not yet chosen (first in row-major order on a tie) names a reference machine (its row) and that row is
///   eliminated from every other row.</item>
///   <item>With V1 the reference machines' rows, L = V V1^-1 (each reference machine's row of L is a unit row).</item>
///   <item>Each machine joins the group of the reference machine with the largest entry of its row of L (first on a
///   tie).</item>
/// </list>
/// The number of groups r is the user's choice. Refused: r below 1 or above the machine count, and an r for which the
/// r-th and (r+1)-th eigenvalues are equal (the slow subspace is then not defined).</para>
///
/// <para>Determinism: the eigen solver, the pivot search and the assignment run in fixed orders with first-on-tie rules.</para>
/// </summary>
public static class CoherencyGrouping
{
    /// <summary>
    /// The r coherent groups of the machines of <paramref name="model"/> at the converged operating point
    /// <paramref name="solved"/>. <paramref name="xdpMachineBase"/> is each generator's X'd on its own MBase (aligned to
    /// <c>model.Generators</c>); units that are not machines (converter plant, power-flow-only units, H &lt;= 0) are held as
    /// constant admittances and belong to no group. Group members are indices into <c>model.Generators</c>.
    /// </summary>
    public static CoherencyResult FromSolvedCase(GridModel model, PowerFlowResult solved, double[] xdpMachineBase, int groups)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(solved);
        ArgumentNullException.ThrowIfNull(xdpMachineBase);
        solved.RequireConverged("Slow-coherency grouping");
        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, solved, xdpMachineBase, holdNonMachines: true);
        int g = p.H.Length;
        var k = new double[g, g];
        for (int i = 0; i < g; i++)
        {
            double diag = 0.0;
            for (int j = 0; j < g; j++)
            {
                if (i == j) continue;
                double dij = p.Delta0[i] - p.Delta0[j];
                Complex y = p.YReduced[i, j];
                double kij = p.Eq[i] * p.Eq[j] * (y.Imaginary * DetMath.Cos(dij) - y.Real * DetMath.Sin(dij));
                k[i, j] = -kij;
                diag += kij;
            }
            k[i, i] = diag;
        }
        CoherencyResult r = SlowCoherency(p.H, k, groups);
        IReadOnlyList<int> map = p.MachineIndex;
        var mapped = r.Groups.Select(gr => (IReadOnlyList<int>)gr.Select(i => map[i]).ToList()).ToList();
        var refs = r.ReferenceMachines.Select(i => map[i]).ToList();
        return r with { Groups = mapped, ReferenceMachines = refs };
    }

    /// <summary>
    /// The r coherent groups of machines with inertia <paramref name="inertiaSystemBase"/> (H on the system base, s) and
    /// synchronising Laplacian <paramref name="synchronisingLaplacian"/> (per-unit power per radian on the system base).
    /// Group members are indices into the arrays.
    /// </summary>
    public static CoherencyResult SlowCoherency(double[] inertiaSystemBase, double[,] synchronisingLaplacian, int groups)
    {
        ArgumentNullException.ThrowIfNull(inertiaSystemBase);
        ArgumentNullException.ThrowIfNull(synchronisingLaplacian);
        int n = inertiaSystemBase.Length;
        if (synchronisingLaplacian.GetLength(0) != n || synchronisingLaplacian.GetLength(1) != n)
            throw new ArgumentException($"the synchronising matrix must be {n} x {n}, one row per machine.", nameof(synchronisingLaplacian));
        if (groups < 1 || groups > n)
            throw new ArgumentOutOfRangeException(nameof(groups), groups, $"the number of coherent groups must be 1 to {n} (the machine count).");
        for (int i = 0; i < n; i++)
            if (!(inertiaSystemBase[i] > 0) || !double.IsFinite(inertiaSystemBase[i]))
                throw new ArgumentOutOfRangeException(nameof(inertiaSystemBase), inertiaSystemBase[i], $"machine {i} has H = {inertiaSystemBase[i]}; it must be a finite number above 0.");

        // S = M^-1/2 K_s M^-1/2 with M = 2H and K_s the symmetric part of K.
        var invSqrtM = new double[n];
        for (int i = 0; i < n; i++) invSqrtM[i] = 1.0 / Math.Sqrt(2.0 * inertiaSystemBase[i]);
        var s = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double ks = 0.5 * (synchronisingLaplacian[i, j] + synchronisingLaplacian[j, i]);
                if (!double.IsFinite(ks)) throw new ArgumentException($"the synchronising matrix entry ({i}, {j}) is not finite.", nameof(synchronisingLaplacian));
                s[i, j] = invSqrtM[i] * ks * invSqrtM[j];
            }
        (double[] values, double[,] vectors) = SymmetricEigen.EigenDecomposition(s);
        if (groups < n && !(values[groups] > values[groups - 1]))
            throw new InvalidOperationException(
                $"the slow subspace of {groups} modes is not defined: eigenvalues {groups} and {groups + 1} are equal ({values[groups - 1]}); choose another number of groups.");

        // V: the r slow modal shapes, M^-1/2 U.
        var v = new double[n, groups];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < groups; c++) v[i, c] = invSqrtM[i] * vectors[i, c];

        // Gaussian elimination with complete pivoting: the reference machines.
        var w = (double[,])v.Clone();
        var rowUsed = new bool[n];
        var colUsed = new bool[groups];
        var refs = new int[groups];
        for (int step = 0; step < groups; step++)
        {
            int pr = -1, pc = -1;
            double best = -1.0;
            for (int i = 0; i < n; i++)
            {
                if (rowUsed[i]) continue;
                for (int c = 0; c < groups; c++)
                {
                    if (colUsed[c]) continue;
                    double a = Math.Abs(w[i, c]);
                    if (a > best) { best = a; pr = i; pc = c; }
                }
            }
            if (!(best > 0))
                throw new InvalidOperationException($"the slow modal shapes have rank below {groups}; no reference machine for slow mode {step + 1}.");
            rowUsed[pr] = true;
            colUsed[pc] = true;
            refs[step] = pr;
            for (int i = 0; i < n; i++)
            {
                if (i == pr) continue;
                double f = w[i, pc] / w[pr, pc];
                if (f == 0.0) continue;
                for (int c = 0; c < groups; c++) w[i, c] -= f * w[pr, c];
            }
        }

        // L = V V1^-1, V1 = the reference rows of V.
        var v1 = new double[groups, groups];
        for (int a = 0; a < groups; a++)
            for (int c = 0; c < groups; c++) v1[a, c] = v[refs[a], c];
        double[,] v1Inv = Invert(v1);
        var members = new List<int>[groups];
        for (int a = 0; a < groups; a++) members[a] = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int bestGroup = 0;
            double bestL = double.NegativeInfinity;
            for (int a = 0; a < groups; a++)
            {
                double l = 0.0;
                for (int c = 0; c < groups; c++) l += v[i, c] * v1Inv[c, a];
                if (l > bestL) { bestL = l; bestGroup = a; }
            }
            members[bestGroup].Add(i);
        }

        var ordered = members.Where(mm => mm.Count > 0).OrderBy(mm => mm[0]).Select(mm => (IReadOnlyList<int>)mm).ToList();
        var slow = new double[groups];
        Array.Copy(values, slow, groups);
        return new CoherencyResult(ordered, refs, slow, groups < n ? values[groups] : double.NaN);
    }

    /// <summary>The inverse of a small dense real matrix by Gauss-Jordan elimination with partial pivoting (largest
    /// magnitude, first on ties), in a fixed order; a singular matrix is refused.</summary>
    private static double[,] Invert(double[,] a)
    {
        int m = a.GetLength(0);
        var w = new double[m, 2 * m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++) w[i, j] = a[i, j];
            w[i, m + i] = 1.0;
        }
        for (int c = 0; c < m; c++)
        {
            int p = c;
            double best = Math.Abs(w[c, c]);
            for (int r = c + 1; r < m; r++) if (Math.Abs(w[r, c]) > best) { best = Math.Abs(w[r, c]); p = r; }
            if (!(best > 0)) throw new InvalidOperationException("the reference machines' modal shapes are singular; the slow-coherency grouping is not defined.");
            if (p != c) for (int j = 0; j < 2 * m; j++) (w[c, j], w[p, j]) = (w[p, j], w[c, j]);
            double piv = w[c, c];
            for (int j = 0; j < 2 * m; j++) w[c, j] /= piv;
            for (int r = 0; r < m; r++)
            {
                if (r == c || w[r, c] == 0.0) continue;
                double f = w[r, c];
                for (int j = 0; j < 2 * m; j++) w[r, j] -= f * w[c, j];
            }
        }
        var inv = new double[m, m];
        for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) inv[i, j] = w[i, m + j];
        return inv;
    }
}
