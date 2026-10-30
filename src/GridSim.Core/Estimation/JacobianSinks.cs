namespace GridSim.Core.Estimation;

/// <summary>Receives the measurement Jacobian's nonzero partials from
/// <see cref="MeasurementModel.EvaluateJacobian{TSink}"/> - the seam that lets one evaluation
/// path feed both the dense and the sparse estimator backends. Each (row, column) arrives at
/// most once per evaluation.</summary>
internal interface IJacobianSink
{
    void Set(int row, int col, double value);
}

/// <summary>Writes partials into a caller-cleared dense H (the dense backend, at or below SolverSelector.DenseSparseCrossoverBuses = 200 buses).</summary>
internal readonly struct DenseJacobianSink : IJacobianSink
{
    private readonly double[,] _j;

    public DenseJacobianSink(double[,] j) => _j = j;

    public void Set(int row, int col, double value) => _j[row, col] = value;
}

/// <summary>Collects partials as per-measurement sparse rows - the gb-full-scale backend's H.
/// An injection row touches only its bus's electrical neighbourhood, a flow row four columns,
/// a voltage row one, so H holds a few dozen entries per row instead of the full state width.</summary>
internal sealed class SparseJacobianRows : IJacobianSink
{
    public SparseJacobianRows(int measurementCount)
    {
        Rows = new List<(int Col, double Val)>[measurementCount];
        for (int r = 0; r < measurementCount; r++) Rows[r] = new List<(int, double)>(16);
    }

    public List<(int Col, double Val)>[] Rows { get; }

    public void Clear()
    {
        foreach (var row in Rows) row.Clear();
    }

    public void Set(int row, int col, double value)
    {
        if (value != 0.0) Rows[row].Add((col, value));
    }
}
