namespace GridSim.Core.Dynamics.Emt.Nodal;

/// <summary>Dense LU with partial pivoting, row-major, fixed operation order (deterministic). Sized for the small networks
/// of an EMT study (tens of nodes).</summary>
internal sealed class DenseLu
{
    private readonly int _n;
    private readonly double[] _lu;
    private readonly int[] _piv;

    public DenseLu(int n)
    {
        _n = n;
        _lu = new double[n * n];
        _piv = new int[n];
    }

    /// <summary>Factor a copy of <paramref name="a"/> (n*n, row-major). False when a pivot is zero or negligible.</summary>
    public bool Factor(double[] a)
    {
        int n = _n;
        Array.Copy(a, _lu, n * n);
        double scale = 0.0;
        for (int i = 0; i < n * n; i++) scale = Math.Max(scale, Math.Abs(_lu[i]));
        double floor = scale * 1e-300;
        for (int c = 0; c < n; c++)
        {
            int p = c;
            double best = Math.Abs(_lu[c * n + c]);
            for (int r = c + 1; r < n; r++)
            {
                double v = Math.Abs(_lu[r * n + c]);
                if (v > best) { best = v; p = r; }
            }
            if (!(best > floor) || double.IsNaN(best)) return false;
            _piv[c] = p;
            if (p != c)
                for (int j = 0; j < n; j++) (_lu[c * n + j], _lu[p * n + j]) = (_lu[p * n + j], _lu[c * n + j]);
            double d = _lu[c * n + c];
            for (int r = c + 1; r < n; r++)
            {
                double f = _lu[r * n + c] / d;
                _lu[r * n + c] = f;
                if (f != 0.0)
                    for (int j = c + 1; j < n; j++) _lu[r * n + j] -= f * _lu[c * n + j];
            }
        }
        return true;
    }

    /// <summary>Solve in place: <paramref name="b"/> becomes x.</summary>
    public void Solve(double[] b)
    {
        int n = _n;
        // The factor swapped whole rows (multipliers included, LAPACK style), so every interchange is applied to b first.
        for (int c = 0; c < n; c++)
        {
            int p = _piv[c];
            if (p != c) (b[c], b[p]) = (b[p], b[c]);
        }
        for (int c = 0; c < n; c++)
        {
            double bc = b[c];
            if (bc != 0.0)
                for (int r = c + 1; r < n; r++) b[r] -= _lu[r * n + c] * bc;
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int j = r + 1; j < n; j++) s -= _lu[r * n + j] * b[j];
            b[r] = s / _lu[r * n + r];
        }
    }
}
