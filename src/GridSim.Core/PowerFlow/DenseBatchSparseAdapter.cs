using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// R071 (October 2026): lets a dense <see cref="IBatchedLinearSolver"/> (the GPU batched LU, or its CPU reference
/// <see cref="CpuBatchedLuSolver"/>) serve the batched contingency sweep, which posts SPARSE systems of possibly
/// different sizes. The systems of one batch are grouped by size (in their batch order), each group is expanded to
/// the dense row-major layout the dense seam takes, and the group is solved in one <c>SolveBatch</c> call.
/// <para>
/// <b>Not bit-identical to the canonical sweep, by design and on the record.</b> The dense LU pivots column by column
/// in natural order, the canonical sparse LU in reverse Cuthill-McKee order, so the two solutions of the same system
/// agree to rounding, not to the bit. This adapter is therefore the GPU route into the sweep, held to the same
/// standard as the single-system GPU solvers (which are outside the CPU bit-identity contract); the bit-identical
/// batched route is <see cref="CpuBatchedSparseLuSolver"/>, the default of
/// <see cref="Analysis.ContingencyAnalysis.ScreenBranchesBatched"/>.
/// </para>
/// </summary>
public sealed class DenseBatchSparseAdapter : IBatchedSparseLinearSolver
{
    private readonly IBatchedLinearSolver _dense;

    public DenseBatchSparseAdapter(IBatchedLinearSolver dense)
    {
        ArgumentNullException.ThrowIfNull(dense);
        _dense = dense;
    }

    public string Name => $"{_dense.Name} (dense batch behind the sparse seam)";

    public double[]?[] SolveBatch(IReadOnlyList<CsrMatrix> matrices, IReadOnlyList<double[]> rhs, Exception?[] errors)
    {
        BatchedSparseArguments.Require(matrices, rhs, errors);

        int count = matrices.Count;
        var results = new double[]?[count];

        // Group the systems by size, keeping batch order inside each group (ascending sizes, so the grouping itself
        // is deterministic). The dense seam needs every system of one call to share n.
        var bySize = new SortedDictionary<int, List<int>>();
        for (int s = 0; s < count; s++)
        {
            int n = matrices[s].N;
            if (!bySize.TryGetValue(n, out List<int>? members)) bySize[n] = members = new List<int>();
            members.Add(s);
        }

        foreach ((int n, List<int> members) in bySize)
        {
            if (n == 0)
            {
                // An empty system has the empty solution (as CpuSparseLuSolver reports it).
                foreach (int s in members) results[s] = Array.Empty<double>();
                continue;
            }

            // Expand each sparse system into its dense row-major block. Entries are ADDED, so a CSR that repeats a
            // (row, column) pair still stands for the same matrix. The caller's arrays are never written.
            long nn = (long)n * n;
            if (nn * members.Count > Array.MaxLength)
                throw new ArgumentException(
                    $"dense batch adapter: {members.Count} systems of size {n} need {nn * members.Count} dense entries, "
                    + "more than one array can hold; use the sparse batched solver for systems this large.",
                    nameof(matrices));
            var aFlat = new double[nn * members.Count];
            var bFlat = new double[(long)n * members.Count];
            for (int g = 0; g < members.Count; g++)
            {
                CsrMatrix a = matrices[members[g]];
                long aOff = g * nn;
                for (int i = 0; i < n; i++)
                    for (int k = a.RowPtr[i]; k < a.RowPtr[i + 1]; k++)
                        aFlat[aOff + (long)i * n + a.ColIdx[k]] += a.Val[k];
                Array.Copy(rhs[members[g]], 0, bFlat, (long)g * n, n);
            }

            bool[] ok;
            try
            {
                ok = _dense.SolveBatch(members.Count, n, aFlat, bFlat);
            }
            catch (Exception ex)
            {
                // The dense seam throws only for malformed arguments; hand the failure to every system of the group,
                // so each lane fails loud with the real cause instead of reading a stale or NaN step.
                foreach (int s in members) errors[s] = ex;
                continue;
            }
            if (ok is null || ok.Length != members.Count)
                throw new InvalidOperationException(
                    $"dense batch adapter: '{_dense.Name}' returned {(ok is null ? "no" : ok.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))} "
                    + $"success flags for {members.Count} systems.");

            for (int g = 0; g < members.Count; g++)
            {
                if (!ok[g]) { results[members[g]] = null; continue; }   // singular: the caller takes its fall-back
                var x = new double[n];
                Array.Copy(bFlat, (long)g * n, x, 0, n);
                results[members[g]] = x;
            }
        }
        return results;
    }
}
