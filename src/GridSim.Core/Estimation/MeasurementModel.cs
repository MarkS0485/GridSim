using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Estimation;

/// <summary>
/// The measurement equation z = h(x) + e over a network: evaluates h and its Jacobian H for a
/// fixed (model, measurement set) pair. The state is x = [Va at every non-slack bus (radians);
/// Vm at every bus (pu)], in model bus order - the same conventions as
/// <see cref="NewtonShell.BusSpec"/>, so estimates are directly comparable to power-flow results.
/// Injection rows evaluate through <c>ComputeInjectionsSparse</c>: the solver's injection sum
/// (<see cref="NewtonRaphsonPowerFlow.ComputeInjections"/>) restricted to each bus's electrical neighbours, bit-identical
/// to it because the terms skipped are exact structural zeros (<c>SparseEstimatorTests</c> pins the agreement bitwise).
/// D-DOC-204 (October 2026): this summary used to say the rows call the solver's own method, which they have not since
/// the neighbourhood path was added for large networks. Branch-flow rows use the same pi-model admittance stamps as
/// <see cref="NewtonShell.ComputeLineFlows"/>. Everything here is in per-unit: measured values
/// and sigmas are converted once, at construction.
/// </summary>
internal sealed class MeasurementModel
{
    /// <summary>A branch whose apparent flow |S| (pu) is at or below this carries no flow: its current-magnitude
    /// Jacobian row is a structural near-zero and is left empty (R174).</summary>
    internal const double ZeroFlowMagnitudePu = 1e-12;

    /// <summary>The same no-flow test on |S|^2 (pu^2), i.e. <see cref="ZeroFlowMagnitudePu"/> squared, used by the
    /// current-angle row whose angle is undefined without flow.</summary>
    internal const double ZeroFlowMagnitudeSquaredPu = 1e-24;

    // Admittance stored SPARSELY, aligned with the per-bus neighbour lists (sparse-alignment):
    // _gN[i][idx]/_bN[i][idx] pair with _neighbors[i][idx]; diagonals ride separate arrays.
    // The dense [n,n] pair this replaces retained ~200 MB at gb-full scale.
    private readonly double[][] _gN;
    private readonly double[][] _bN;
    private readonly double[] _gDiag;
    private readonly double[] _bDiag;
    private readonly int _n;
    private readonly int[] _angleCol;      // per bus index; -1 = slack (angle fixed)
    private readonly int[] _angleColBus;   // reverse map: angle column -> bus index
    private readonly Row[] _rows;
    private readonly double[] _pBuf;
    private readonly double[] _qBuf;

    private readonly struct Row
    {
        public Row(MeasurementKind kind, int a, int b, Complex yaa, Complex yab)
        {
            Kind = kind; A = a; B = b; Yaa = yaa; Yab = yab;
        }

        public MeasurementKind Kind { get; }
        /// <summary>Bus index for bus rows; measured-end bus index for flow rows.</summary>
        public int A { get; }
        /// <summary>Far-end bus index for flow rows; unused for bus rows.</summary>
        public int B { get; }
        /// <summary>Self admittance stamp at the measured end (yff or ytt).</summary>
        public Complex Yaa { get; }
        /// <summary>Mutual admittance stamp toward the far end (yft or ytf).</summary>
        public Complex Yab { get; }
    }

    public MeasurementModel(GridModel model, MeasurementSet set)
    {
        // Sparse cells only - the dense [n,n] build cost a ~200 MB transient at gb-full scale
        // for a consumer that never reads the zero cells. Cell values are bit-for-bit the dense
        // build's (same stamps, same accumulation order), and the neighbour lists are assembled
        // in the same ascending-k order the dense scan produced, so downstream summations are
        // unchanged to the bit (the injection agreement test pins this).
        SparseYBus ybus = YBus.BuildSparse(model);
        _n = ybus.N;
        _neighbors = new int[_n][];
        _gN = new double[_n][];
        _bN = new double[_n][];
        _gDiag = new double[_n];
        _bDiag = new double[_n];

        var rowCells = new List<(int Col, Complex Y)>[_n];
        for (int i = 0; i < _n; i++) rowCells[i] = new List<(int, Complex)>(8);
        foreach (var kv in ybus.Cells)
            rowCells[kv.Key.Row].Add((kv.Key.Col, kv.Value));

        for (int i = 0; i < _n; i++)
        {
            List<(int Col, Complex Y)> row = rowCells[i];
            row.Sort(static (a, b) => a.Col.CompareTo(b.Col));
            // Match the dense scan exactly: k included when k == i (always) or the summed cell
            // is nonzero in either part.
            var kBuf = new List<int>(row.Count + 1);
            var gBuf = new List<double>(row.Count + 1);
            var bBuf = new List<double>(row.Count + 1);
            bool selfSeen = false;
            foreach ((int col, Complex y) in row)
            {
                if (col == i)
                {
                    selfSeen = true;
                    kBuf.Add(col); gBuf.Add(y.Real); bBuf.Add(y.Imaginary);
                    _gDiag[i] = y.Real;
                    _bDiag[i] = y.Imaginary;
                }
                else if (y.Real != 0 || y.Imaginary != 0)
                {
                    kBuf.Add(col); gBuf.Add(y.Real); bBuf.Add(y.Imaginary);
                }
            }
            if (!selfSeen)
            {
                // An isolated bus with no branch stamp: the dense scan still lists self with 0.
                int pos = kBuf.FindIndex(k => k > i);
                if (pos < 0) pos = kBuf.Count;
                kBuf.Insert(pos, i); gBuf.Insert(pos, 0.0); bBuf.Insert(pos, 0.0);
            }
            _neighbors[i] = kBuf.ToArray();
            _gN[i] = gBuf.ToArray();
            _bN[i] = bBuf.ToArray();
        }
        _pBuf = new double[_n];
        _qBuf = new double[_n];

        BaseMva = model.BaseMva;
        BusIds = model.Buses.Select(bus => bus.Id).ToArray();

        _angleCol = new int[_n];
        int na = 0;
        for (int i = 0; i < _n; i++)
            _angleCol[i] = model.Buses[i].Type == BusType.Slack ? -1 : na++;
        AngleCount = na;
        _angleColBus = new int[na];
        for (int i = 0; i < _n; i++)
            if (_angleCol[i] >= 0) _angleColBus[_angleCol[i]] = i;
        StateSize = na + _n;

        _rows = new Row[set.Measurements.Count];
        ZPu = new double[_rows.Length];
        SigmaPu = new double[_rows.Length];
        Measurements = set.Measurements;
        for (int r = 0; r < _rows.Length; r++)
        {
            Measurement m = set.Measurements[r];
            // Internal units: voltage rows stay pu, power rows normalise by BaseMva, angle rows
            // convert degrees -> radians (the state's angle unit).
            (ZPu[r], SigmaPu[r]) = m.Kind switch
            {
                MeasurementKind.VmPu => (m.Value, m.Sigma),
                MeasurementKind.IMagPu => (m.Value, m.Sigma), // current magnitude is already per-unit
                MeasurementKind.VaDeg => (m.Value * Math.PI / 180.0, m.Sigma * Math.PI / 180.0),
                MeasurementKind.IAngDeg => (m.Value * Math.PI / 180.0, m.Sigma * Math.PI / 180.0), // current angle deg->rad
                _ => (m.Value / BaseMva, m.Sigma / BaseMva),
            };

            if (!m.IsBranch)
            {
                _rows[r] = new Row(m.Kind, ybus.IndexOf(m.Bus), 0, Complex.Zero, Complex.Zero);
                continue;
            }

            if (!MeasurementSet.TryResolveBranch(model, m, out int bk, out string? error))
                throw new InvalidDataException($"measurement #{r}: {error}");
            Branch br = model.Branches[bk];

            // The same pi-model stamps as YBus.Build / NewtonShell.ComputeLineFlows. An out-of-service
            // branch carries no current, and YBus/injection rows already exclude it, so stamp zero
            // admittance here too - otherwise h(x) would predict the flow the branch WOULD carry if
            // closed while the rest of the model says it is open (and SyntheticMeasurements emits ~0
            // placeholder flows for exactly these branches).
            BranchStamp s = br.InService
                ? AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg)
                : default;

            int f = ybus.IndexOf(br.From);
            int t = ybus.IndexOf(br.To);
            _rows[r] = m.End == BranchEnd.From
                ? new Row(m.Kind, f, t, s.Yff, s.Yft)
                : new Row(m.Kind, t, f, s.Ytt, s.Ytf);
        }
    }

    public double BaseMva { get; }
    public int StateSize { get; }
    /// <summary>Number of angle states (non-slack buses); Vm columns start here.</summary>
    public int AngleCount { get; }
    public int MeasurementCount => _rows.Length;
    public double[] ZPu { get; }
    public double[] SigmaPu { get; }
    public IReadOnlyList<int> BusIds { get; }
    public IReadOnlyList<Measurement> Measurements { get; }

    /// <summary>State column carrying bus <paramref name="busIndex"/>'s angle, or -1 for a slack.</summary>
    public int AngleColumn(int busIndex) => _angleCol[busIndex];

    /// <summary>State column carrying bus <paramref name="busIndex"/>'s voltage magnitude.</summary>
    public int VmColumn(int busIndex) => AngleCount + busIndex;

    /// <summary>Which bus (index) and quantity a state column describes - for observability reporting.</summary>
    public int StateBusIndex(int stateCol, out bool isAngle)
    {
        isAngle = stateCol < AngleCount;
        return isAngle ? _angleColBus[stateCol] : stateCol - AngleCount;
    }

    /// <summary>Buses touched by a measurement row (one for bus rows, both ends for flow rows).</summary>
    public IEnumerable<int> TouchedBuses(int row)
    {
        yield return _rows[row].A;
        if (Measurements[row].IsBranch) yield return _rows[row].B;
    }

    private readonly int[][] _neighbors; // per bus: electrical neighbours incl. self (structural, built at construction)

    /// <summary>
    /// The state columns a measurement row can EVER write (its structural Jacobian pattern) -
    /// independent of the operating point, so a sparse backend can fix its gain sparsity
    /// pattern once per pass instead of rediscovering it each iteration. Injection rows touch
    /// their bus's electrical neighbourhood (incl. self), flow rows their two ends, voltage
    /// rows one column. Appends distinct, unsorted columns to <paramref name="cols"/>.
    /// </summary>
    public void StructuralColumns(int row, List<int> cols)
    {
        Row r = _rows[row];
        switch (r.Kind)
        {
            case MeasurementKind.VmPu:
                cols.Add(VmColumn(r.A));
                break;
            case MeasurementKind.VaDeg:
                // A slack-bus angle row has NO state column (the reference is fixed): structurally
                // empty, numerically a constant residual - legal, just uninformative.
                int acVa = _angleCol[r.A];
                if (acVa >= 0) cols.Add(acVa);
                break;
            case MeasurementKind.PInjMw:
            case MeasurementKind.QInjMvar:
                foreach (int k in _neighbors[r.A])
                {
                    int ac = _angleCol[k];
                    if (ac >= 0) cols.Add(ac);
                    cols.Add(VmColumn(k));
                }
                break;
            case MeasurementKind.PFlowMw:
            case MeasurementKind.QFlowMvar:
            case MeasurementKind.IMagPu:
            case MeasurementKind.IAngDeg:
                // |I| and ang(I) depend on the same four columns the P/Q flow rows touch.
                int acA = _angleCol[r.A], acB = _angleCol[r.B];
                if (acA >= 0) cols.Add(acA);
                if (acB >= 0) cols.Add(acB);
                cols.Add(VmColumn(r.A));
                cols.Add(VmColumn(r.B));
                break;
            default:
                throw new InvalidOperationException($"Unhandled measurement kind {r.Kind}.");
        }
    }

    /// <summary>
    /// Bus injections over the ELECTRICAL NEIGHBOURHOOD only - numerically identical to
    /// <see cref="NewtonRaphsonPowerFlow.ComputeInjections"/> (the skipped terms are exact
    /// structural zeros, and adding 0.0 is exact in IEEE754, so the summation is bit-for-bit
    /// the same; an agreement test pins this). O(nnz) instead of O(n^2): the difference between
    /// 12.5M and ~15k multiplies per call at gb-full scale.
    /// </summary>
    private void ComputeInjectionsSparse(double[] vm, double[] va, double[] p, double[] q)
    {
        for (int i = 0; i < _n; i++)
        {
            int[] nbr = _neighbors[i];
            double[] gRow = _gN[i], bRow = _bN[i];
            double pi = 0, qi = 0;
            for (int idx = 0; idx < nbr.Length; idx++)
            {
                int k = nbr[idx];
                double th = va[i] - va[k];
                (double sin, double cos) = DetMath.SinCos(th);
                double vmk = vm[k];
                pi += vmk * (gRow[idx] * cos + bRow[idx] * sin);
                qi += vmk * (gRow[idx] * sin - bRow[idx] * cos);
            }
            p[i] = vm[i] * pi;
            q[i] = vm[i] * qi;
        }
    }

    /// <summary>Evaluate h(x) (pu) into <paramref name="hOut"/> at the given voltages.</summary>
    public void EvaluateH(double[] vm, double[] va, double[] hOut)
    {
        ComputeInjectionsSparse(vm, va, _pBuf, _qBuf);
        for (int r = 0; r < _rows.Length; r++)
        {
            Row row = _rows[r];
            double h = row.Kind switch
            {
                MeasurementKind.VmPu => vm[row.A],
                MeasurementKind.VaDeg => va[row.A],
                MeasurementKind.PInjMw => _pBuf[row.A],
                MeasurementKind.QInjMvar => _qBuf[row.A],
                MeasurementKind.PFlowMw => FlowP(row, vm, va),
                MeasurementKind.QFlowMvar => FlowQ(row, vm, va),
                MeasurementKind.IMagPu => CurrentMag(row, vm, va),
                MeasurementKind.IAngDeg => va[row.A] - DetMath.Atan2(FlowQ(row, vm, va), FlowP(row, vm, va)),
                _ => throw new InvalidOperationException($"Unhandled measurement kind {row.Kind}."),
            };
            // Angle kinds (VaDeg/IAngDeg, held in radians) live on a +/-pi branch cut: a PMU reports the
            // wrapped principal angle, and atan2 in h can land on the opposite side, so the raw z - h can
            // read ~2*pi instead of the true small mismatch. Fold h toward z so that EVERY downstream
            // residual (z - h) is wrapped to (-pi, pi] - one fix covers the WLS rhs, the step backends and
            // any consumer of this h. The Jacobian dh/dx is unchanged.
            if (row.Kind is MeasurementKind.VaDeg or MeasurementKind.IAngDeg)
                h = ZPu[r] - Math.IEEERemainder(ZPu[r] - h, 2.0 * Math.PI);
            hOut[r] = h;
        }
    }

    /// <summary>Branch current magnitude at the measured end: |I| = |S|/|V| = sqrt(P^2 + Q^2) / Vm.</summary>
    private static double CurrentMag(Row row, double[] vm, double[] va)
    {
        double p = FlowP(row, vm, va), q = FlowQ(row, vm, va);
        return Math.Sqrt(p * p + q * q) / vm[row.A];
    }

    /// <summary>
    /// Evaluate the measurement Jacobian H = dh/dx (rows = measurements, columns = state) into
    /// <paramref name="jOut"/>. The injection partials are the standard polar power-flow forms -
    /// identical algebra to <see cref="NewtonRaphsonPowerFlow"/>'s Jacobian, laid out over the
    /// full estimator state instead of the solver's pvpq/pq subset.
    /// </summary>
    public void EvaluateJacobian(double[] vm, double[] va, double[,] jOut)
    {
        Array.Clear(jOut);
        EvaluateJacobian(vm, va, new DenseJacobianSink(jOut));
    }

    /// <summary>
    /// The single Jacobian evaluation path, writing each nonzero partial into
    /// <paramref name="sink"/> (dense array or per-row sparse lists) - one implementation for
    /// both estimator backends, so the dense and sparse math cannot drift apart. Each
    /// (row, column) is emitted at most once per evaluation.
    /// </summary>
    public void EvaluateJacobian<TSink>(double[] vm, double[] va, TSink sink)
        where TSink : IJacobianSink
    {
        ComputeInjectionsSparse(vm, va, _pBuf, _qBuf);

        for (int r = 0; r < _rows.Length; r++)
        {
            Row row = _rows[r];
            switch (row.Kind)
            {
                case MeasurementKind.VmPu:
                    sink.Set(r, VmColumn(row.A), 1.0);
                    break;
                case MeasurementKind.VaDeg:
                    int acRow = _angleCol[row.A];
                    if (acRow >= 0) sink.Set(r, acRow, 1.0);
                    break;
                case MeasurementKind.PInjMw:
                case MeasurementKind.QInjMvar:
                    InjectionRow(sink, r, row.A, row.Kind == MeasurementKind.PInjMw, vm, va);
                    break;
                case MeasurementKind.PFlowMw:
                case MeasurementKind.QFlowMvar:
                    FlowRow(sink, r, row, row.Kind == MeasurementKind.PFlowMw, vm, va);
                    break;
                case MeasurementKind.IMagPu:
                    CurrentMagRow(sink, r, row, vm, va);
                    break;
                case MeasurementKind.IAngDeg:
                    CurrentAngleRow(sink, r, row, vm, va);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled measurement kind {row.Kind}.");
            }
        }
    }

    /// <summary>
    /// Reactive injection Qinj (pu) at bus <paramref name="busIndex"/> and its Jacobian row dQinj/dx
    /// written into <paramref name="rowOut"/> (length <see cref="StateSize"/>, cleared first). Reuses
    /// the EXACT injection partials the measurement Jacobian emits (<see cref="InjectionRow"/>), so the
    /// feasible estimator's generator Q-capability constraint cannot drift from the measurement math.
    /// Recomputes the injection buffers, so call it per constraint bus, sparingly.
    /// </summary>
    public double InjectionQRow(int busIndex, double[] vm, double[] va, double[] rowOut)
    {
        ComputeInjectionsSparse(vm, va, _pBuf, _qBuf);
        Array.Clear(rowOut);
        InjectionRow(new RowSink(rowOut), 0, busIndex, real: false, vm, va);
        return _qBuf[busIndex];
    }

    /// <summary>The complex power flow of a branch at one end and its P/Q Jacobian rows, computed from
    /// the branch's own pi-model stamps - identical to <see cref="YBus.Build"/> and the flow measurement
    /// rows - so a thermal-rating constraint can be linearised even when no flow measurement is present.
    /// <paramref name="f"/>/<paramref name="t"/> are the branch's state/ybus bus indices;
    /// <paramref name="fromEnd"/> selects the FROM (f->t) or TO (t->f) end. Returns (P,Q) in pu and
    /// scatters dP/dx into <paramref name="pRow"/>, dQ/dx into <paramref name="qRow"/> (both length
    /// <see cref="StateSize"/>, cleared first). Reuses the private flow kernels, so there is no
    /// copy-drift from the measurement path.</summary>
    public (double P, double Q) BranchFlowRow(Branch br, int f, int t, bool fromEnd,
        double[] vm, double[] va, double[] pRow, double[] qRow)
    {
        BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);

        Row row = fromEnd
            ? new Row(MeasurementKind.PFlowMw, f, t, s.Yff, s.Yft)
            : new Row(MeasurementKind.PFlowMw, t, f, s.Ytt, s.Ytf);
        Array.Clear(pRow);
        Array.Clear(qRow);
        FlowRow(new RowSink(pRow), 0, row, real: true, vm, va);
        FlowRow(new RowSink(qRow), 0, row, real: false, vm, va);
        return (FlowP(row, vm, va), FlowQ(row, vm, va));
    }

    /// <summary>An <see cref="IJacobianSink"/> that scatters a single Jacobian row into a flat array
    /// (row index ignored) - for <see cref="InjectionQRow"/>.</summary>
    private readonly struct RowSink : IJacobianSink
    {
        private readonly double[] _row;
        public RowSink(double[] row) => _row = row;
        public void Set(int rowIndex, int col, double value) => _row[col] = value;
    }

    private void InjectionRow<TSink>(TSink j, int r, int i, bool real, double[] vm, double[] va)
        where TSink : IJacobianSink
    {
        int[] nbrI = _neighbors[i];
        double[] gRowI = _gN[i], bRowI = _bN[i];
        for (int idx = 0; idx < nbrI.Length; idx++)
        {
            int k = nbrI[idx];
            double gik = gRowI[idx], bik = bRowI[idx];
            double th = va[i] - va[k];
            (double sin, double cos) = DetMath.SinCos(th);

            int ac = _angleCol[k];
            if (real)
            {
                if (ac >= 0)
                    j.Set(r, ac, k == i
                        ? -_qBuf[i] - _bDiag[i] * vm[i] * vm[i]
                        : vm[i] * vm[k] * (gik * sin - bik * cos));
                j.Set(r, VmColumn(k), k == i
                    ? _pBuf[i] / vm[i] + _gDiag[i] * vm[i]
                    : vm[i] * (gik * cos + bik * sin));
            }
            else
            {
                if (ac >= 0)
                    j.Set(r, ac, k == i
                        ? _pBuf[i] - _gDiag[i] * vm[i] * vm[i]
                        : -vm[i] * vm[k] * (gik * cos + bik * sin));
                j.Set(r, VmColumn(k), k == i
                    ? _qBuf[i] / vm[i] - _bDiag[i] * vm[i]
                    : vm[i] * (gik * sin - bik * cos));
            }
        }
    }

    private void FlowRow<TSink>(TSink j, int r, Row row, bool real, double[] vm, double[] va)
        where TSink : IJacobianSink
    {
        int a = row.A, b = row.B;
        double gaa = row.Yaa.Real, baa = row.Yaa.Imaginary;
        double gab = row.Yab.Real, bab = row.Yab.Imaginary;
        double vA = vm[a], vB = vm[b];
        double th = va[a] - va[b];
        (double sin, double cos) = DetMath.SinCos(th);

        int acA = _angleCol[a], acB = _angleCol[b];
        if (real)
        {
            // P = gaa*vA^2 + vA*vB*(gab*cos + bab*sin)
            if (acA >= 0) j.Set(r, acA, vA * vB * (-gab * sin + bab * cos));
            if (acB >= 0) j.Set(r, acB, vA * vB * (gab * sin - bab * cos));
            j.Set(r, VmColumn(a), 2.0 * gaa * vA + vB * (gab * cos + bab * sin));
            j.Set(r, VmColumn(b), vA * (gab * cos + bab * sin));
        }
        else
        {
            // Q = -baa*vA^2 + vA*vB*(gab*sin - bab*cos)
            if (acA >= 0) j.Set(r, acA, vA * vB * (gab * cos + bab * sin));
            if (acB >= 0) j.Set(r, acB, -vA * vB * (gab * cos + bab * sin));
            j.Set(r, VmColumn(a), -2.0 * baa * vA + vB * (gab * sin - bab * cos));
            j.Set(r, VmColumn(b), vA * (gab * sin - bab * cos));
        }
    }

    /// <summary>Captures up to four (col, value) Jacobian partials from a single <see cref="FlowRow{TSink}"/>
    /// evaluation, so the current-magnitude row can combine the P and Q flow partials without duplicating the
    /// flow algebra (no copy-drift).</summary>
    private sealed class PairSink : IJacobianSink
    {
        public readonly int[] Cols = new int[4];
        public readonly double[] Vals = new double[4];
        public int Count;
        public void Set(int rowIndex, int col, double value) { Cols[Count] = col; Vals[Count] = value; Count++; }
    }

    /// <summary>The Jacobian row of |I| = sqrt(P^2 + Q^2)/Vm. With S = sqrt(P^2+Q^2),
    /// d|I|/dx = (P dP/dx + Q dQ/dx)/(S Vm_a) - (S/Vm_a^2) dVm_a/dx. dP/dx and dQ/dx come from the exact flow
    /// kernels; dVm_a/dx is 1 on the from-end Vm column. A branch carrying no flow contributes a ~zero row.</summary>
    private void CurrentMagRow<TSink>(TSink sink, int r, Row row, double[] vm, double[] va)
        where TSink : IJacobianSink
    {
        double p = FlowP(row, vm, va), q = FlowQ(row, vm, va);
        double vmA = vm[row.A];
        double sMag = Math.Sqrt(p * p + q * q);
        if (sMag <= ZeroFlowMagnitudePu) return; // no flow: |I| ~ 0, the row is a structural near-zero

        var pPairs = new PairSink();
        FlowRow(pPairs, 0, row, real: true, vm, va);
        var qPairs = new PairSink();
        FlowRow(qPairs, 0, row, real: false, vm, va);

        int vmColA = VmColumn(row.A);
        for (int i = 0; i < pPairs.Count; i++)
        {
            int col = pPairs.Cols[i];        // P and Q rows emit the same columns in the same order
            double val = (p * pPairs.Vals[i] + q * qPairs.Vals[i]) / (sMag * vmA);
            if (col == vmColA) val -= sMag / (vmA * vmA);
            sink.Set(r, col, val);
        }
    }

    /// <summary>The Jacobian row of ang(I) = Va_end - atan2(Q, P). d/dx = d(Va_end)/dx - (P dQ/dx - Q dP/dx)/(P^2+Q^2),
    /// where d(Va_end)/dx is 1 on the from-end angle column (absent at a slack). dP/dx, dQ/dx come from the exact
    /// flow kernels. A branch carrying no flow has an undefined angle and contributes only the Va term.</summary>
    private void CurrentAngleRow<TSink>(TSink sink, int r, Row row, double[] vm, double[] va)
        where TSink : IJacobianSink
    {
        double p = FlowP(row, vm, va), q = FlowQ(row, vm, va);
        double s2 = p * p + q * q;
        int acA = _angleCol[row.A];
        if (s2 <= ZeroFlowMagnitudeSquaredPu)
        {
            if (acA >= 0) sink.Set(r, acA, 1.0);
            return;
        }

        var pPairs = new PairSink();
        FlowRow(pPairs, 0, row, real: true, vm, va);
        var qPairs = new PairSink();
        FlowRow(qPairs, 0, row, real: false, vm, va);

        for (int i = 0; i < pPairs.Count; i++)
        {
            int col = pPairs.Cols[i];
            double val = -(p * qPairs.Vals[i] - q * pPairs.Vals[i]) / s2;
            if (col == acA) val += 1.0; // + d(Va_end)/dx
            sink.Set(r, col, val);
        }
    }

    private static double FlowP(Row row, double[] vm, double[] va)
    {
        double th = va[row.A] - va[row.B];
        return row.Yaa.Real * vm[row.A] * vm[row.A]
             + vm[row.A] * vm[row.B] * (row.Yab.Real * DetMath.Cos(th) + row.Yab.Imaginary * DetMath.Sin(th));
    }

    private static double FlowQ(Row row, double[] vm, double[] va)
    {
        double th = va[row.A] - va[row.B];
        return -row.Yaa.Imaginary * vm[row.A] * vm[row.A]
             + vm[row.A] * vm[row.B] * (row.Yab.Real * DetMath.Sin(th) - row.Yab.Imaginary * DetMath.Cos(th));
    }
}
