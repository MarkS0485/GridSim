namespace GridSim.Core.Analysis;

/// <summary>
/// A square-ish matrix (m rows, any number of columns) stored column by column, where every column is either
/// a SINGLE (exactly one stored entry, at a known row) or DENSE (a full array of m values). It exists so the
/// security-constrained DC-OPF simplex (<see cref="DcOpfSecuritySimplex"/>) can hold its basis inverse, and the
/// Gauss-Jordan working matrix it is rebuilt from, without an m x m dense array (about 276 MB on gb-full).
///
/// WHY THIS IS EXACT. The reference (<see cref="Numerics.BoundedSimplex"/>) keeps a dense m x m inverse and
/// updates it with row operations: scale one row, then for every other row with a non-zero multiplier subtract
/// multiplier x pivot row. Each matrix entry is updated independently of every other entry, so the order in
/// which this class visits entries cannot change any value. The only operations this class SKIPS are ones whose
/// result is provably the same value: x - f * 0 and x / d for x = 0. In IEEE 754 arithmetic those can change
/// only the SIGN of a zero (+0 versus -0), never a non-zero value, and a signed zero can never turn into a
/// different non-zero value downstream here: it only ever meets multiplication (giving a zero), addition to a
/// non-zero (no effect), comparison (+0 == -0), Math.Abs, or the refactorisation clamp, which maps both +0 and
/// -0 to the same +clamp. So every non-zero entry here is bit-for-bit the entry the dense reference holds.
///
/// A single column becomes dense ("promoted") the first time a row operation would give it a second non-zero.
/// Dense columns are never demoted; the simplex rebuilds this object from scratch at every refactorisation.
/// </summary>
internal sealed class DcOpfHybridColumns
{
    private readonly int _m;
    private readonly double[]?[] _dense;   // per column: the dense values, or null for a single
    private readonly int[] _singleRow;     // per single column: the row of its one entry
    private readonly double[] _singleVal;  // per single column: the value of its one entry
    private readonly List<int>[] _singlesAtRow; // per row: the single columns whose entry sits in that row
    private readonly List<int> _denseCols = new(); // indices of the dense columns (order irrelevant)

    /// <summary>Number of rows.</summary>
    public int Rows => _m;

    /// <summary>Number of columns.</summary>
    public int Columns => _dense.Length;

    /// <summary>How many columns are currently dense (a memory and work measure).</summary>
    public int DenseColumnCount => _denseCols.Count;

    /// <summary>Create an m-row matrix with <paramref name="columns"/> columns, all initially undefined; every
    /// column must be set with <see cref="SetSingle"/> or <see cref="SetDense"/> before use.</summary>
    public DcOpfHybridColumns(int m, int columns)
    {
        if (m <= 0 || columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(m), $"DcOpfHybridColumns needs positive sizes (got {m} x {columns}).");
        _m = m;
        _dense = new double[]?[columns];
        _singleRow = new int[columns];
        _singleVal = new double[columns];
        Array.Fill(_singleRow, -1);
        _singlesAtRow = new List<int>[m];
        for (int i = 0; i < m; i++) _singlesAtRow[i] = new List<int>(2);
    }

    /// <summary>Make column <paramref name="col"/> a single: value <paramref name="value"/> at row
    /// <paramref name="row"/>, zero elsewhere. Only for a column not yet set.</summary>
    public void SetSingle(int col, int row, double value)
    {
        if (_dense[col] is not null || _singleRow[col] >= 0)
            throw new InvalidOperationException($"DcOpfHybridColumns: column {col} was already set.");
        _singleRow[col] = row;
        _singleVal[col] = value;
        _singlesAtRow[row].Add(col);
    }

    /// <summary>Make column <paramref name="col"/> dense, adopting <paramref name="values"/> (length m, not
    /// copied). Only for a column not yet set.</summary>
    public void SetDense(int col, double[] values)
    {
        if (values.Length != _m) throw new ArgumentException($"DcOpfHybridColumns: dense column needs {_m} values, got {values.Length}.", nameof(values));
        if (_dense[col] is not null || _singleRow[col] >= 0)
            throw new InvalidOperationException($"DcOpfHybridColumns: column {col} was already set.");
        _dense[col] = values;
        _denseCols.Add(col);
    }

    /// <summary>The dense values of column <paramref name="col"/>, or null when it is a single.</summary>
    public double[]? DenseColumn(int col) => _dense[col];

    /// <summary>The row and value of single column <paramref name="col"/> (row -1 if it is dense).</summary>
    public (int Row, double Value) Single(int col) => (_singleRow[col], _singleVal[col]);

    /// <summary>The stored value at (<paramref name="row"/>, <paramref name="col"/>); zero where nothing is stored.</summary>
    public double Entry(int row, int col)
    {
        double[]? d = _dense[col];
        if (d is not null) return d[row];
        return _singleRow[col] == row ? _singleVal[col] : 0.0;
    }

    /// <summary>Swap two whole rows (the Gauss-Jordan partial-pivoting row exchange).</summary>
    public void SwapRows(int a, int b)
    {
        if (a == b) return;
        foreach (int j in _denseCols)
        {
            double[] d = _dense[j]!;
            (d[a], d[b]) = (d[b], d[a]);
        }
        foreach (int j in _singlesAtRow[a]) _singleRow[j] = b;
        foreach (int j in _singlesAtRow[b]) _singleRow[j] = a;
        (_singlesAtRow[a], _singlesAtRow[b]) = (_singlesAtRow[b], _singlesAtRow[a]);
    }

    /// <summary>Divide every entry of row <paramref name="row"/> by <paramref name="divisor"/> (the dense
    /// reference's <c>m[row, j] /= divisor</c> for every j). Stored entries only; an unstored zero stays zero.</summary>
    public void DivideRow(int row, double divisor)
    {
        foreach (int j in _denseCols) _dense[j]![row] /= divisor;
        foreach (int j in _singlesAtRow[row]) _singleVal[j] /= divisor;
    }

    /// <summary>
    /// The elimination half of a pivot: for every row r other than <paramref name="pivotRow"/> with a non-zero
    /// multiplier f[r], subtract f[r] x (row <paramref name="pivotRow"/>) from row r - exactly the dense
    /// reference's <c>m[r, j] -= f * m[pivotRow, j]</c> for every j.
    /// <paramref name="f"/> is a length-m multiplier vector whose entry at <paramref name="pivotRow"/> must be 0,
    /// and <paramref name="nonZeroRows"/> lists (in any order) every row r with f[r] != 0. When
    /// <paramref name="denseF"/> is true the update runs over all rows (a zero multiplier then subtracts 0 x e,
    /// which leaves a non-zero entry bit-for-bit unchanged), which is faster when most multipliers are non-zero.
    /// </summary>
    public void Eliminate(int pivotRow, double[] f, List<int> nonZeroRows, bool denseF)
    {
        if (f[pivotRow] != 0.0) throw new InvalidOperationException("DcOpfHybridColumns.Eliminate: the multiplier at the pivot row must be zero.");
        if (nonZeroRows.Count == 0) return;
        // 1. Dense columns: entry-wise update.
        foreach (int j in _denseCols)
        {
            double[] d = _dense[j]!;
            double e = d[pivotRow];
            if (e == 0.0) continue; // every update would be x - f * 0: value-neutral
            if (denseF)
            {
                for (int r = 0; r < _m; r++) d[r] -= f[r] * e;
            }
            else
            {
                foreach (int r in nonZeroRows) d[r] -= f[r] * e;
            }
        }
        // 2. Single columns whose entry sits in the pivot row: each gains an entry in every row with a non-zero
        //    multiplier, so it is promoted to dense. In the dense reference those entries were 0, and
        //    0 - f * e is computed here exactly as there.
        List<int> pivotSingles = _singlesAtRow[pivotRow];
        if (pivotSingles.Count == 0) return;
        int[] promote = pivotSingles.ToArray();
        pivotSingles.Clear();
        foreach (int j in promote)
        {
            double e = _singleVal[j];
            var d = new double[_m];
            d[pivotRow] = e;
            foreach (int r in nonZeroRows) d[r] -= f[r] * e;
            _dense[j] = d;
            _singleRow[j] = -1;
            _singleVal[j] = 0.0;
            _denseCols.Add(j);
        }
    }

    /// <summary>
    /// result[i] = sum over columns k in ASCENDING order of M[i, k] x v[k], each sum accumulated from +0 in that
    /// order - exactly the dense reference's <c>for k: sum += M[i, k] * v[k]</c>. Terms with v[k] == 0 or an
    /// unstored zero entry are skipped (value-neutral, see the class note). <paramref name="result"/> is
    /// overwritten.
    /// </summary>
    public void MultiplyVector(double[] v, double[] result)
    {
        Array.Clear(result);
        for (int k = 0; k < _dense.Length; k++)
        {
            double vk = v[k];
            if (vk == 0.0) continue;
            double[]? d = _dense[k];
            if (d is not null)
            {
                for (int i = 0; i < _m; i++) result[i] += d[i] * vk;
            }
            else
            {
                result[_singleRow[k]] += _singleVal[k] * vk;
            }
        }
    }

    /// <summary>
    /// The dot product of a row weight vector with column <paramref name="col"/>, summed over
    /// <paramref name="weightedRows"/> in the order given (which must be ascending): sum = +0, then
    /// sum += w[i] x M[i, col] for each listed row - the dense reference's <c>for i: sum += w[i] * M[i, col]</c>
    /// with the rows whose weight is zero left out (value-neutral). <paramref name="weightedRows"/> must list
    /// every row with a non-zero weight.
    /// </summary>
    public double WeightedColumnSum(int col, double[] w, List<int> weightedRows)
    {
        double sum = 0.0;
        double[]? d = _dense[col];
        if (d is not null)
        {
            foreach (int i in weightedRows) sum += w[i] * d[i];
        }
        else
        {
            sum += w[_singleRow[col]] * _singleVal[col];
        }
        return sum;
    }
}
