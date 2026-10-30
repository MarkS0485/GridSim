using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Protection;

/// <summary>
/// A factored fault-study admittance matrix: the network <see cref="YBus"/> (or any supplied cells) plus a
/// sequence of diagonal shunt stamps (machine reactances, loads as impedances), factored once and solved for
/// as many right-hand sides as a study needs (a Zbus column, a converter-injection response, ...).
///
/// <para><b>One engine at every size</b> (N233, Mark's determinism rule 2026-10-04: identical bits on every solver
/// path). Every fault network is factored on the real 2n-expansion sparse LU (<see cref="RealExpandedComplexSolver"/>),
/// the same stack the harmonic study and the RMS network run, whatever the bus count. Until October 2026 a network of
/// up to 1,500 buses was factored on the dense complex LU (<see cref="DenseComplexLuSolver.FactorDense"/>) and a larger
/// one on the sparse LU. The two solve the same matrix (the sparse cells are bit for bit the dense build's nonzero
/// cells) but they are different algorithms (complex partial pivoting against a real-expanded LU with its own ordering),
/// so they agreed only to a few units in the last place (at most 4.818E-15 relative over Ik'', ip, Ib and Ith on
/// case118, measured by DOC-4). Two answers to one question break the rule, so the size switch is gone, exactly as the
/// power flow lost its dense / sparse switch at N057: a 9-bus case and gb-full now run the same arithmetic. The few-ulp
/// moves this made in the small-case pins are recorded once, in the tests, under "Moved 2026-10 (N233, ...)". Networks
/// above 1,500 buses (gb-full) were already sparse, so their results are bit for bit unchanged.</para>
///
/// <para><b>The dense LU survives only as a test oracle</b> (<see cref="DenseReferenceScope"/>): an independent algorithm
/// the tests cross-check the sparse answers against, to a stated tolerance. No production code path can select it (the
/// scope is internal and only the test project opens it), so it is not a second engine.</para>
///
/// <para><b>Singular systems fail loud</b> (R285, October 2026): the sparse factor reports a singular matrix and this
/// throws. (The dense oracle clamps a vanishing pivot to 1e-14 rather than stopping, so every dense solve is checked
/// against the matrix it came from, and a solution that does not satisfy <c>Y x = b</c> to a backward-stable residual
/// is rejected with the same "singular" error. That check only reads the answer.)</para>
/// </summary>
internal sealed class FaultNetworkSystem
{
    /// <summary>
    /// N233 (October 2026): TEST ORACLE ONLY. While a test holds <see cref="DenseReferenceScope"/> on its own async flow,
    /// every fault network built on that flow (<see cref="ForModel"/>, <see cref="FromTriplets"/>) is factored on the
    /// historical dense complex LU instead of the sparse engine, so a test can compare the production (sparse) answer with
    /// an independent algorithm. An AsyncLocal, so tests running in parallel never see each other's setting. Production
    /// code never sets it (default false: always the sparse engine). It replaces D-DOC-231's ForceSparseScope, which
    /// forced the other way while small cases were still dense.
    /// </summary>
    private static readonly AsyncLocal<bool> DenseReferenceFlow = new();

    /// <summary>N233: use the dense test oracle on the current async flow until the returned scope is disposed. Tests
    /// only: the dense LU is not bit-identical to the sparse engine and production never uses it.</summary>
    internal static IDisposable DenseReferenceScope()
    {
        bool previous = DenseReferenceFlow.Value;
        DenseReferenceFlow.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => DenseReferenceFlow.Value = previous;
    }

    private readonly DenseComplexLu? _dense;
    private readonly Complex[,]? _denseMatrix;   // the matrix _dense factors, kept for the residual check
    private readonly IComplexLinearFactorization? _sparse;
    private readonly Func<FaultNetworkSystem> _transpose;

    public int N { get; }

    /// <summary>True when the sparse engine is in use: always, except under the test-only
    /// <see cref="DenseReferenceScope"/>.</summary>
    public bool IsSparse => _sparse is not null;

    private FaultNetworkSystem(int n, DenseComplexLu? dense, Complex[,]? denseMatrix, IComplexLinearFactorization? sparse, Func<FaultNetworkSystem> transpose)
    {
        N = n; _dense = dense; _denseMatrix = denseMatrix; _sparse = sparse; _transpose = transpose;
    }

    /// <summary>The message for a singular fault network, shared by the engine and the oracle so they fail identically.</summary>
    private const string SingularMessage =
        "the fault-network admittance matrix is singular (an island with no source or earth path?)";

    /// <summary>Relative residual above which a dense (oracle) solve is rejected as coming from a singular matrix. A
    /// backward-stable LU leaves <c>|Y x - b| ~ n * eps * |Y| |x|</c> (about 3e-13 relative at 1,500 buses); a clamped
    /// zero pivot leaves an O(1) relative residual. 1e-9 sits many orders from both.</summary>
    private const double SingularResidualRel = 1e-9;

    /// <summary>Solve <c>Y x = b</c>. Throws <see cref="InvalidOperationException"/> when Y is singular.</summary>
    public Complex[] Solve(Complex[] b)
    {
        if (_dense is not null)
        {
            Complex[] x = _dense.Solve(b);
            CheckDenseResidual(_denseMatrix!, x, b);
            return x;
        }
        return _sparse!.Solve(b) ?? throw new InvalidOperationException(SingularMessage);
    }

    /// <summary>
    /// <see cref="Solve"/> into the caller's <paramref name="x"/> (length <see cref="N"/>, not <paramref name="b"/>),
    /// bit-identical, failing loud the same way (N060 / R115). On the sparse engine nothing is allocated; the dense test
    /// oracle (<see cref="DenseReferenceScope"/>) copies <see cref="Solve"/>'s checked result.
    /// </summary>
    public void SolveInto(Complex[] b, Complex[] x)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Length != N) throw new ArgumentException($"x holds {x.Length} entries; the system has {N} buses.", nameof(x));
        if (ReferenceEquals(b, x)) throw new ArgumentException("x must not be the right-hand side array.", nameof(x));
        if (_dense is not null)
        {
            Complex[] r = Solve(b);
            Array.Copy(r, x, N);
            return;
        }
        if (!_sparse!.SolveInto(b, x)) throw new InvalidOperationException(SingularMessage);
    }

    /// <summary>Reject a dense (oracle) solution that does not satisfy <c>Y x = b</c> (the dense factor clamps a
    /// vanishing pivot instead of stopping, R285). Inf-norms: <c>|Y x - b| &lt;= rel * (|Y| |x| + |b|)</c>, and every
    /// entry of x finite.</summary>
    private static void CheckDenseResidual(Complex[,] y, Complex[] x, Complex[] b)
    {
        int n = x.Length;
        double normX = 0, normB = 0, normY = 0, normR = 0;
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(x[i].Real) || !double.IsFinite(x[i].Imaginary))
                throw new InvalidOperationException(SingularMessage);
            normX = Math.Max(normX, x[i].Magnitude);
            normB = Math.Max(normB, b[i].Magnitude);
        }
        for (int i = 0; i < n; i++)
        {
            Complex r = -b[i];
            double rowSum = 0;
            for (int j = 0; j < n; j++)
            {
                Complex yij = y[i, j];
                if (yij == Complex.Zero) continue;
                r += yij * x[j];
                rowSum += yij.Magnitude;
            }
            normY = Math.Max(normY, rowSum);
            normR = Math.Max(normR, r.Magnitude);
        }
        if (!(normR <= SingularResidualRel * (normY * normX + normB)))
            throw new InvalidOperationException(SingularMessage);
    }

    /// <summary>The factored transpose <c>Y^T</c> (for a Zbus row when Y is unsymmetric).</summary>
    public FaultNetworkSystem Transpose() => _transpose();

    /// <summary>The model's network <see cref="YBus"/> plus the diagonal <paramref name="shunts"/> (bus index,
    /// admittance), applied in order, factored on the sparse engine at every size (N233).
    /// <paramref name="seriesOnly"/> builds the IEC 60909-0 positive/negative-sequence network instead: the branch
    /// series admittances and taps only, with NO line charging (B) and NO bus shunts (Gs/Bs), which the standard
    /// neglects in those sequence systems (R284). The operating-point fault study keeps the full network.</summary>
    public static FaultNetworkSystem ForModel(GridModel model, IReadOnlyList<(int Index, Complex Y)> shunts, bool seriesOnly = false)
    {
        if (seriesOnly) model = SeriesOnly(model);
        int n = model.BusCount;
        if (DenseReferenceFlow.Value)
        {
            // Test oracle only (N233): the historical dense build and factor, statement for statement.
            Complex[,] y = YBus.Build(model).Y;
            foreach ((int i, Complex v) in shunts) y[i, i] += v;
            return FromDense(y, n);
        }
        // The engine: the sparse cells of the network admittance (bit for bit the dense build's nonzero cells), then each
        // shunt added to its diagonal cell in the order given (a cell the network lacks starts from zero).
        var cells = new Dictionary<(int Row, int Col), Complex>(YBus.BuildSparse(model).Cells);
        foreach ((int i, Complex v) in shunts)
            cells[(i, i)] = cells.TryGetValue((i, i), out Complex cur) ? cur + v : Complex.Zero + v;
        return FromCells(n, cells);
    }

    /// <summary>A system from explicit triplets (row, col, value), accumulated in order - for networks the study
    /// assembles itself (the zero sequence, the RMS network) - factored on the sparse engine at every size (N233).</summary>
    public static FaultNetworkSystem FromTriplets(int n, IReadOnlyList<(int Row, int Col, Complex V)> triplets)
    {
        if (DenseReferenceFlow.Value)
        {
            // Test oracle only (N233): the historical dense accumulation and factor.
            var y = new Complex[n, n];
            foreach ((int r, int c, Complex v) in triplets) y[r, c] += v;
            return FromDense(y, n);
        }
        // The engine: accumulate the triplets into cells in the order given (the first entry of a cell starts from zero).
        var cells = new Dictionary<(int Row, int Col), Complex>(triplets.Count);
        foreach ((int r, int c, Complex v) in triplets)
            cells[(r, c)] = cells.TryGetValue((r, c), out Complex cur) ? cur + v : Complex.Zero + v;
        return FromCells(n, cells);
    }

    /// <summary>The model with every branch's line charging and every bus shunt removed (the IEC 60909-0
    /// positive/negative-sequence network, R284). Series impedance, tap ratio and phase shift are kept exactly.</summary>
    private static GridModel SeriesOnly(GridModel model)
    {
        var buses = new List<Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses) buses.Add(b.Gs == 0 && b.Bs == 0 ? b : b with { Gs = 0, Bs = 0 });
        var branches = new List<Branch>(model.Branches.Count);
        foreach (Branch br in model.Branches) branches.Add(br.B == 0 ? br : br with { B = 0 });
        return model.CopyWith(buses: buses, branches: branches);
    }

    /// <summary>The dense test oracle (N233): factor <paramref name="y"/> on the dense complex LU.</summary>
    private static FaultNetworkSystem FromDense(Complex[,] y, int n)
    {
        DenseComplexLu lu = DenseComplexLuSolver.FactorDense(y, n);
        return new FaultNetworkSystem(n, lu, y, null, () =>
        {
            var yt = new Complex[n, n];
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++) yt[r, c] = y[c, r];
            return FromDense(yt, n);
        });
    }

    /// <summary>The engine: factor the cells on the real-expanded sparse LU (singular: throws).</summary>
    private static FaultNetworkSystem FromCells(int n, Dictionary<(int Row, int Col), Complex> cells)
    {
        var builder = new ComplexCsr.Builder(n);
        foreach (KeyValuePair<(int Row, int Col), Complex> kv in cells)
            builder.Add(kv.Key.Row, kv.Key.Col, kv.Value.Real, kv.Value.Imaginary);
        IComplexLinearFactorization lu = new RealExpandedComplexSolver().Factor(builder.Build())
            ?? throw new InvalidOperationException(SingularMessage);
        return new FaultNetworkSystem(n, null, null, lu, () =>
        {
            var t = new Dictionary<(int Row, int Col), Complex>(cells.Count);
            foreach (KeyValuePair<(int Row, int Col), Complex> kv in cells) t[(kv.Key.Col, kv.Key.Row)] = kv.Value;
            return FromCells(n, t);
        });
    }
}
