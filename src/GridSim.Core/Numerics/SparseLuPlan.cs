namespace GridSim.Core.Numerics;

/// <summary>
/// The symbolic record of one Gilbert-Peierls factorisation (<see cref="SparseLuFactorization.Factor"/> with a plan
/// requested): the CSC pattern of A it factored, the column ordering, each column's nonzero reach in the topological
/// order the depth-first search produced, each column's pivot row, and the patterns of L and U. A matrix with the same
/// pattern can then be factored by <see cref="SparseLuFactorization.Refactor"/>, which replays exactly the arithmetic the
/// full factorisation would do (same operations in the same order) without the depth-first searches, and returns null
/// whenever the full factorisation would have chosen a different pivot, so the caller falls back to it. A factorisation
/// from the plan is therefore bit-identical to a fresh one, or not produced at all.
/// <para>Immutable once built; one plan may serve many threads.</para>
/// </summary>
internal sealed class SparseLuPlan
{
    internal SparseLuPlan(int n, int[] ap, int[] ai, int[] q, int[] reachPtr, int[] reach, int[] pivotRow, int[] pivotStep,
        int[] lp, int[] liOriginal, int[] liStep, int[] up, int[] ui, double diagonalPreference)
    {
        N = n; Ap = ap; Ai = ai; Q = q; ReachPtr = reachPtr; Reach = reach; PivotRow = pivotRow; PivotStep = pivotStep;
        Lp = lp; LiOriginal = liOriginal; LiStep = liStep; Up = up; Ui = ui; DiagonalPreference = diagonalPreference;
    }

    /// <summary>The pivot rule the plan was recorded under: 0 for plain partial pivoting (the default), else the opt-in
    /// diagonal-preference threshold (see <see cref="SparseLuFactorization.Factor"/>). A replay applies the same rule.</summary>
    internal double DiagonalPreference { get; }

    /// <summary>The order of the matrix.</summary>
    internal int N { get; }

    /// <summary>The CSC column pointers and row indices of A (explicit zeros dropped) the plan was made for.</summary>
    internal int[] Ap { get; }
    internal int[] Ai { get; }

    /// <summary>The column ordering: pivot step to original column.</summary>
    internal int[] Q { get; }

    /// <summary>Column k's reach (original row indices, topological order) is Reach[ReachPtr[k] .. ReachPtr[k + 1]).</summary>
    internal int[] ReachPtr { get; }
    internal int[] Reach { get; }

    /// <summary>The original row chosen as pivot at each step.</summary>
    internal int[] PivotRow { get; }

    /// <summary>Original row to pivot step (the row permutation, P).</summary>
    internal int[] PivotStep { get; }

    /// <summary>L's column pointers, its row indices as original rows (for the replay) and as pivot steps (for the
    /// factor), and U's column pointers and row indices (pivot steps).</summary>
    internal int[] Lp { get; }
    internal int[] LiOriginal { get; }
    internal int[] LiStep { get; }
    internal int[] Up { get; }
    internal int[] Ui { get; }

    /// <summary>Whether CSC arrays <paramref name="ap"/>, <paramref name="ai"/> are exactly this plan's pattern.</summary>
    internal bool SamePattern(int[] ap, int[] ai)
    {
        if (ap.Length != Ap.Length) return false;
        if (!ap.AsSpan().SequenceEqual(Ap)) return false;
        int nnz = Ap[N];
        return ai.Length >= nnz && ai.AsSpan(0, nnz).SequenceEqual(Ai.AsSpan(0, nnz));
    }
}
