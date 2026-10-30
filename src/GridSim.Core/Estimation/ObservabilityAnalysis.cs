using GridSim.Core.Model;

namespace GridSim.Core.Estimation;

/// <summary>
/// Whether - and where - a measurement set pins down the network state. <see cref="Observable"/>
/// is the gate: when false, the WLS normal equations are singular and no trustworthy estimate
/// exists. <see cref="UnmeasuredIslandBuses"/> lists buses in electrical islands no measurement
/// touches at all (bus ids); <see cref="UnobservableStates"/> names the state directions the gain
/// matrix cannot resolve (e.g. <c>"Va@bus 12"</c>), with the affected bus ids collected in
/// <see cref="UnobservableBuses"/>. On the sparse (above-threshold) backend the rank proof is not
/// computed - <see cref="Rank"/> is -1 there and singularity surfaces through the factorization.
/// </summary>
public sealed record ObservabilityReport(
    bool Observable,
    int Rank,
    int StateSize,
    IReadOnlyList<int> UnobservableBuses,
    IReadOnlyList<string> UnobservableStates,
    IReadOnlyList<int> UnmeasuredIslandBuses);

/// <summary>
/// Pragmatic numerical observability analysis for the M1 estimator: (a) a topological pre-check
/// for electrical islands carrying no measurements at all, and (b) a pivoted-elimination rank
/// check of the gain matrix H^TWH, whose collapsed pivot columns are exactly the state directions
/// the measurement set leaves free. The pivot floor is relative to the matrix scale, the same
/// doctrine as the LU solvers' near-singularity guards.
/// </summary>
internal static class ObservabilityAnalysis
{
    /// <summary>Relative pivot floor: a pivot at or below norm(G)*this is a collapsed direction.</summary>
    private const double RankFloor = Numerics.NumericTolerances.RelativePivotFloor;

    /// <summary>
    /// Sparse-backend gate: the unmeasured-island sweep only. The O(s^3) gain-rank proof does not
    /// scale past the dense threshold, so <see cref="ObservabilityReport.Rank"/> is reported as
    /// -1 (not computed) and numerical singularity is detected by the sparse factorization
    /// returning null instead.
    /// </summary>
    public static ObservabilityReport AnalyzeTopology(GridModel model, MeasurementModel mm)
    {
        List<int> islandBuses = UnmeasuredIslands(model, mm);
        return new ObservabilityReport(islandBuses.Count == 0, -1, mm.StateSize,
            Array.Empty<int>(), Array.Empty<string>(), islandBuses);
    }

    /// <summary>
    /// Sparse-backend diagnosis WITHIN a size cap: when the state size is small enough that an s x s dense
    /// rank proof is affordable (a control-room "area", not the whole estate), densify the sparse gain
    /// (held as its symmetric CSC arrays by <c>SparseWlsStep</c>) and run the same <see cref="GainRank"/> the
    /// dense backend uses - so the sparse path now names the free state directions ("Va@bus N") and
    /// distinguishes a metering-deficient bus from an island, instead of only reporting Rank = -1. Above the
    /// cap it falls back to the topology sweep (the O(s^3) proof does not scale, as documented there).
    /// </summary>
    public static ObservabilityReport AnalyzeSparse(
        GridModel model, MeasurementModel mm, int[] ap, int[] ai, double[] ax, int maxState)
    {
        int s = mm.StateSize;
        if (s > maxState) return AnalyzeTopology(model, mm);

        // CSC -> dense (the gain is symmetric, so the CSR arrays double as CSC).
        var gain = new double[s, s];
        for (int c = 0; c < s; c++)
            for (int k = ap[c]; k < ap[c + 1]; k++)
                gain[ai[k], c] = ax[k];
        // S5 (October 2026, R118 remainder): this dense copy belongs to this call alone, so the rank elimination runs
        // in it directly instead of on a second s x s copy (Analyze's public contract keeps the caller's matrix
        // intact). The elimination is the same code on the same values, so the rank and the dead columns are
        // unchanged; the gate allocates and fills one s x s matrix instead of two (case588_sdet: 1,175 states,
        // 11,045,000 B less per gate).
        return AnalyzeCore(model, mm, gain, gainIsScratch: true);
    }

    /// <summary>
    /// Analyze at a formed gain matrix. <paramref name="gain"/> is not modified (the elimination
    /// runs on a copy).
    /// </summary>
    public static ObservabilityReport Analyze(GridModel model, MeasurementModel mm, double[,] gain)
        => AnalyzeCore(model, mm, gain, gainIsScratch: false);

    /// <summary><see cref="Analyze"/>; when <paramref name="gainIsScratch"/> the elimination overwrites
    /// <paramref name="gain"/> itself (the caller's own temporary), otherwise it runs on a copy.</summary>
    private static ObservabilityReport AnalyzeCore(GridModel model, MeasurementModel mm, double[,] gain, bool gainIsScratch)
    {
        List<int> islandBuses = UnmeasuredIslands(model, mm);
        (int rank, List<int> deadCols) = gainIsScratch
            ? GainRankInPlace(gain, mm.StateSize)
            : GainRank(gain, mm.StateSize);

        var states = new List<string>(deadCols.Count);
        var buses = new SortedSet<int>();
        foreach (int col in deadCols)
        {
            int busIdx = mm.StateBusIndex(col, out bool isAngle);
            int busId = mm.BusIds[busIdx];
            states.Add($"{(isAngle ? "Va" : "Vm")}@bus {busId}");
            buses.Add(busId);
        }

        bool observable = rank == mm.StateSize && islandBuses.Count == 0;
        return new ObservabilityReport(observable, rank, mm.StateSize, buses.ToArray(), states, islandBuses);
    }

    /// <summary>Buses (ids) of connected components that no measurement touches.</summary>
    private static List<int> UnmeasuredIslands(GridModel model, MeasurementModel mm)
    {
        int n = model.BusCount;
        var indexOf = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) indexOf[model.Buses[i].Id] = i;

        var adjacency = new List<int>[n];
        for (int i = 0; i < n; i++) adjacency[i] = new List<int>();
        foreach (Branch br in model.Branches)
        {
            // R258: an out-of-service branch is electrically absent (YBus.Build and the measurement model
            // skip it too). Joining islands through it merged a section isolated by open breakers into the
            // main component, so an unmeasured island was never reported - and above the rank-check size
            // limit this sweep is the ONLY observability gate. A bus left with no in-service branch is its
            // own component and is reported when nothing measures it.
            if (!br.InService) continue;
            int f = indexOf[br.From], t = indexOf[br.To];
            adjacency[f].Add(t);
            adjacency[t].Add(f);
        }

        var touched = new bool[n];
        for (int r = 0; r < mm.MeasurementCount; r++)
            foreach (int bus in mm.TouchedBuses(r))
                touched[bus] = true;

        var component = new int[n];
        Array.Fill(component, -1);
        int nc = 0;
        var stack = new Stack<int>();
        for (int seed = 0; seed < n; seed++)
        {
            if (component[seed] >= 0) continue;
            stack.Push(seed);
            component[seed] = nc;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                foreach (int j in adjacency[i])
                    if (component[j] < 0) { component[j] = nc; stack.Push(j); }
            }
            nc++;
        }

        var componentTouched = new bool[nc];
        for (int i = 0; i < n; i++) componentTouched[component[i]] |= touched[i];

        var result = new List<int>();
        for (int i = 0; i < n; i++)
            if (!componentTouched[component[i]]) result.Add(model.Buses[i].Id);
        return result;
    }

    /// <summary>
    /// Row-echelon rank of the (symmetric, positive semi-definite) gain matrix with partial
    /// pivoting, tracking which columns never yield a usable pivot - those are the free
    /// state directions.
    /// </summary>
    /// <remarks>
    /// October 2026 review (R118): the trailing update used to be a scalar double[,] loop with bounds checks
    /// over the full row width, on every sparse-backend gate from 201 buses up to the 2,500-state cap. It now
    /// runs the bit-exact <see cref="Numerics.SimdKernels.SubtractScaledRow(ref double,int,ref double,int,int,double)"/>
    /// (a separate multiply and subtract per lane, never fused, so every element rounds exactly as the scalar
    /// <c>a[r,j] -= factor*a[row,j]</c> did, at every SIMD width) over the contiguous row-major storage, and
    /// stops each row update at the pivot row's last possibly-nonzero column. Entries past that extent are
    /// zero in the pivot row, so the skipped updates are <c>a[r,j] -= factor*(+-0)</c>: they leave every
    /// nonzero entry unchanged and can at most flip the sign of a zero entry. A zero's sign never reaches the
    /// result - the pivot search and floor test use |a|, and a zero below a pivot gives factor +-0, which is
    /// skipped either way - so the rank and the dead columns are exactly those of the full-width loop
    /// (pinned by a test against the original loop, kept verbatim in the test).
    /// </remarks>
    internal static (int Rank, List<int> DeadColumns) GainRank(double[,] gain, int s)
    {
        var a = new double[s, s];
        Array.Copy(gain, a, gain.Length);
        return GainRankInPlace(a, s);
    }

    /// <summary>
    /// <see cref="GainRank"/> eliminating in <paramref name="a"/> itself (an s x s matrix the caller owns and no longer
    /// needs; it is left holding the row-echelon working values). Identical arithmetic on identical values, so the rank
    /// and dead columns are those of <see cref="GainRank"/> on a copy (S5, R118 remainder).
    /// </summary>
    internal static (int Rank, List<int> DeadColumns) GainRankInPlace(double[,] a, int s)
    {
        if (a.GetLength(0) != s || a.GetLength(1) != s)
            throw new ArgumentException($"The gain must be {s} x {s}; it is {a.GetLength(0)} x {a.GetLength(1)}.", nameof(a));

        double norm = 0;
        foreach (double v in a) { double av = Math.Abs(v); if (av > norm) norm = av; }
        double floor = norm * RankFloor;

        // rowEnd[r]: one past the last column of row r that can be nonzero. Swaps carry it with the row; an
        // update of row r by the pivot row can only fill columns up to the pivot row's own extent.
        var rowEnd = new int[s];
        for (int r = 0; r < s; r++)
        {
            int end = 0;
            for (int j = s - 1; j >= 0; j--)
                if (a[r, j] != 0.0) { end = j + 1; break; }
            rowEnd[r] = end;
        }

        ref double origin = ref s > 0 ? ref a[0, 0] : ref norm;   // contiguous row-major storage
        var dead = new List<int>();
        int row = 0;
        for (int col = 0; col < s; col++)
        {
            int p = row;
            double max = row < s ? Math.Abs(a[row, col]) : 0;
            for (int r = row + 1; r < s; r++)
            {
                double v = Math.Abs(a[r, col]);
                if (v > max) { max = v; p = r; }
            }
            if (row >= s || max <= floor)
            {
                dead.Add(col);
                continue;
            }

            if (p != row)
            {
                for (int j = col; j < s; j++)
                    (a[row, j], a[p, j]) = (a[p, j], a[row, j]);
                (rowEnd[row], rowEnd[p]) = (rowEnd[p], rowEnd[row]);
            }

            double pivot = a[row, col];
            int pivotEnd = rowEnd[row];   // > col: the pivot itself is nonzero
            for (int r = row + 1; r < s; r++)
            {
                double factor = a[r, col] / pivot;
                if (factor == 0) continue;
                // a[r, j] -= factor * a[row, j] for j = col .. pivotEnd-1 (bit-exact vector kernel).
                Numerics.SimdKernels.SubtractScaledRow(ref origin, r * s + col, ref origin, row * s + col, pivotEnd - col, factor);
                if (pivotEnd > rowEnd[r]) rowEnd[r] = pivotEnd;
            }
            row++;
        }
        return (row, dead);
    }
}
