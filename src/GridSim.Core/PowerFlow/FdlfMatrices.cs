using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>The two fast-decoupled variants (Stott and Alsac, IEEE Trans. PAS-93, 1974; van Amerongen, IEEE Trans. PWRS
/// 4(4), 1989), named as MATPOWER names them.</summary>
public enum FdlfVariant
{
    /// <summary>XB: series resistance left out of B' and kept in B''.</summary>
    XB,
    /// <summary>BX: series resistance kept in B' and left out of B''.</summary>
    BX,
}

/// <summary>B' and B'' over every bus of the model, in model order (row and column i = <c>model.Buses[i]</c>).</summary>
/// <param name="BPrime">B', the P-theta matrix.</param>
/// <param name="BDoublePrime">B'', the Q-V matrix.</param>
public sealed record FdlfMatrixPair(CsrMatrix BPrime, CsrMatrix BDoublePrime);

/// <summary>
/// The fast-decoupled matrices B' and B'', built as MATPOWER's <c>makeB.m</c> builds them. That file's rules, line by line:
/// <list type="number">
/// <item>B': copy the bus and branch data; zero the bus shunts (BS); zero the line charging (BR_B); set every tap ratio
/// to 1 (TAP); for XB also zero the series resistance (BR_R). Then <c>Bp = -imag(makeYbus(...))</c>. The phase shift
/// (SHIFT) is NOT zeroed for B', so a phase shifter's complex ratio e^(j shift) stays in B'.</item>
/// <item>B'': copy the branch data (the bus data, shunts included, is used as it is); zero the phase shift (SHIFT); for
/// BX also zero the series resistance (BR_R). Then <c>Bpp = -imag(makeYbus(...))</c>. Taps and charging stay.</item>
/// </list>
/// So, with Y's off-diagonal cell <c>-y / conj(t)</c> for a branch of series admittance y and complex ratio t, an
/// off-diagonal cell of B is <c>Im(y / conj(t))</c>, which is <c>Im(y)</c> for t = 1: XB's B' off-diagonal of a branch
/// with x = 0.1 is <c>Im(1 / j0.1) = -10</c>. The cells are formed with GridSim's own stamp
/// (<see cref="AdmittanceStamp.Branch"/>) from the modified data and summed branch by branch, then bus shunt by bus shunt,
/// in model order, exactly as <see cref="YBus.Build"/> accumulates Y; only in-service branches count. The bus shunt
/// conductance (GS) only reaches the real part, so it does not enter B.
/// <para>The spec for this package said XB's B' also ignores the phase shift; makeB.m does not, and this follows makeB.m.</para>
/// </summary>
public static class FdlfMatrices
{
    /// <summary>Build B' and B'' for <paramref name="variant"/> over every bus of <paramref name="model"/>.</summary>
    public static FdlfMatrixPair Build(GridModel model, FdlfVariant variant)
    {
        ArgumentNullException.ThrowIfNull(model);
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        var bp = new CsrMatrix.Builder(n);
        var bpp = new CsrMatrix.Builder(n);
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            int f = index[br.From], t = index[br.To];
            // NUM-03: both variants drop R from one of the matrices, so a purely resistive branch (X = 0, legal for the
            // Newton and Gauss-Seidel engines) has no admittance there (1 / 0). MATPOWER's makeB.m has the same division;
            // there is no standard B' for it, so the case is refused by name before any matrix is formed.
            if (br.X == 0.0)
                throw new InvalidOperationException(
                    $"The fast-decoupled method needs a reactance on every branch; branch {br.From}-{br.To}"
                    + (string.IsNullOrEmpty(br.Name) ? "" : $" ('{br.Name}')")
                    + " has X = 0 (purely resistive). Use a Newton or Gauss-Seidel engine, or give it a reactance.");
            // B': no charging, tap 1 (a 0 tap is MATPOWER's "line" and also 1), phase shift kept; XB drops r.
            BranchStamp p = AdmittanceStamp.Branch(variant == FdlfVariant.XB ? 0.0 : br.R, br.X, 0.0, 1.0, br.PhaseShiftDeg);
            AddNegImag(bp, f, t, p);
            // B'': charging and tap kept, phase shift zeroed; BX drops r.
            BranchStamp q = AdmittanceStamp.Branch(variant == FdlfVariant.BX ? 0.0 : br.R, br.X, br.B, br.TapRatio, 0.0);
            AddNegImag(bpp, f, t, q);
        }
        foreach (Bus b in model.Buses)
        {
            if (b.Bs == 0) continue;
            int i = index[b.Id];
            bpp.Add(i, i, -(new Complex(b.Gs, b.Bs) / model.BaseMva).Imaginary);
        }
        return new FdlfMatrixPair(bp.Build(), bpp.Build());
    }

    private static void AddNegImag(CsrMatrix.Builder m, int f, int t, BranchStamp s)
    {
        m.Add(f, f, -s.Yff.Imaginary);
        m.Add(t, t, -s.Ytt.Imaginary);
        m.Add(f, t, -s.Yft.Imaginary);
        m.Add(t, f, -s.Ytf.Imaginary);
    }

    /// <summary>The cell (<paramref name="row"/>, <paramref name="col"/>) of a CSR matrix; 0 where none is stored.</summary>
    public static double Entry(CsrMatrix m, int row, int col)
    {
        ArgumentNullException.ThrowIfNull(m);
        for (int k = m.RowPtr[row]; k < m.RowPtr[row + 1]; k++)
            if (m.ColIdx[k] == col) return m.Val[k];
        return 0.0;
    }

    /// <summary>The square sub-matrix of <paramref name="m"/> on the rows and columns <paramref name="keep"/> (in that
    /// order). Internal: the fast-decoupled engine reduces B' to the non-slack buses and B'' to the PQ buses.</summary>
    internal static CsrMatrix Reduce(CsrMatrix m, IReadOnlyList<int> keep)
    {
        var pos = new int[m.N];
        Array.Fill(pos, -1);
        for (int k = 0; k < keep.Count; k++) pos[keep[k]] = k;
        var b = new CsrMatrix.Builder(keep.Count);
        for (int r = 0; r < keep.Count; r++)
        {
            int row = keep[r];
            for (int k = m.RowPtr[row]; k < m.RowPtr[row + 1]; k++)
            {
                int c = pos[m.ColIdx[k]];
                if (c >= 0) b.Add(r, c, m.Val[k]);
            }
        }
        return b.Build();
    }
}

/// <summary>
/// The admittance matrix as sorted rows of complex cells (columns ascending), for the injection sums of the
/// fast-decoupled and Gauss-Seidel engines. Cells are <see cref="YBus.BuildSparse"/>'s, so each equals the dense build's
/// value bit for bit.
/// </summary>
internal sealed class SparseYRows
{
    public int N { get; }
    public int[] RowPtr { get; }
    public int[] Col { get; }
    public Complex[] Val { get; }
    /// <summary>The position of each row's diagonal cell in <see cref="Val"/>, or -1.</summary>
    public int[] Diag { get; }

    private SparseYRows(int n, int[] rowPtr, int[] col, Complex[] val, int[] diag)
    { N = n; RowPtr = rowPtr; Col = col; Val = val; Diag = diag; }

    public static SparseYRows Build(GridModel model)
    {
        SparseYBus y = YBus.BuildSparse(model);
        int n = y.N;
        var rows = new List<(int Col, Complex V)>[n];
        for (int i = 0; i < n; i++) rows[i] = new List<(int, Complex)>();
        foreach (KeyValuePair<(int Row, int Col), Complex> kv in y.Cells) rows[kv.Key.Row].Add((kv.Key.Col, kv.Value));
        var rowPtr = new int[n + 1];
        for (int i = 0; i < n; i++) rowPtr[i + 1] = rowPtr[i] + rows[i].Count;
        var col = new int[rowPtr[n]];
        var val = new Complex[rowPtr[n]];
        var diag = new int[n];
        for (int i = 0; i < n; i++)
        {
            // Columns are unique within a row (one dictionary cell each), so this order is total: no tie to break.
            rows[i].Sort((a, b) => a.Col.CompareTo(b.Col));
            diag[i] = -1;
            for (int k = 0; k < rows[i].Count; k++)
            {
                int p = rowPtr[i] + k;
                col[p] = rows[i][k].Col;
                val[p] = rows[i][k].V;
                if (col[p] == i) diag[i] = p;
            }
        }
        return new SparseYRows(n, rowPtr, col, val, diag);
    }

    /// <summary>(Y V)_i, summed over row i's cells in column order.</summary>
    public Complex RowTimes(int i, Complex[] v)
    {
        Complex s = Complex.Zero;
        for (int k = RowPtr[i]; k < RowPtr[i + 1]; k++) s += Val[k] * v[Col[k]];
        return s;
    }

    /// <summary>P and Q injections (pu) at the voltages <paramref name="v"/>: S_i = V_i conj((Y V)_i).</summary>
    public void Injections(Complex[] v, double[] p, double[] q)
    {
        for (int i = 0; i < N; i++)
        {
            Complex s = v[i] * Complex.Conjugate(RowTimes(i, v));
            p[i] = s.Real;
            q[i] = s.Imaginary;
        }
    }
}
