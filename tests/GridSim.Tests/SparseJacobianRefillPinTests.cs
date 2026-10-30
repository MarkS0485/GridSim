using System;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the sparse-NR Jacobian structure-once/refill refactor: the fixed-pattern
/// refill must reproduce the old per-iteration triplet build BIT-FOR-BIT at every cell the old build
/// produced, and any extra cell in the (superset) refill pattern must be an explicit 0.0 - the ones the
/// old builder dropped and the solver's CSR->CSC re-drops. Checked at flat start (where lossless-branch
/// N/M blocks are exactly zero) and at an off-solution state (where they are not).
/// </summary>
public class SparseJacobianRefillPinTests
{
    public static TheoryData<string> Cases => new() { "case14", "case30", "case118", "case300" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Refill_matches_the_old_triplet_build_bit_for_bit(string caseName)
    {
        GridModel model = Load(caseName);
        int n = model.BusCount;
        var index = new System.Collections.Generic.Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        ComplexCsr y = SparseNewtonRaphsonPowerFlow.BuildY(model, index);

        // Reduced-system positions for an all-PQ-except-slack layout (bus 0 is slack here).
        var type = new BusType[n];
        for (int i = 0; i < n; i++) type[i] = i == 0 ? BusType.Slack : BusType.Pq;
        var pPos = new int[n]; var qPos = new int[n];
        Array.Fill(pPos, -1); Array.Fill(qPos, -1);
        int m = 0;
        for (int i = 0; i < n; i++) if (type[i] != BusType.Slack) pPos[i] = m++;
        for (int i = 0; i < n; i++) if (type[i] == BusType.Pq) qPos[i] = m++;

        // Two states: flat start (angle diffs zero -> lossless N/M blocks exactly 0), and off-solution.
        CheckState(y, type, pPos, qPos, m, n, flat: true);
        CheckState(y, type, pPos, qPos, m, n, flat: false);
    }

    private static void CheckState(ComplexCsr y, BusType[] type, int[] pPos, int[] qPos, int m, int n, bool flat)
    {
        var Vm = new double[n]; var Va = new double[n];
        for (int i = 0; i < n; i++)
        {
            // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the test's
            // verbatim copy of the old builder and its inputs use DetMath like the production refill does (it called Math.SinCos,
            // the Windows C runtime, so it no longer matched the deterministic production trig in 4 of the 118-bus cells).
            Vm[i] = flat ? 1.0 : 1.0 + 0.02 * DetMath.Sin(i + 1);
            Va[i] = flat ? 0.0 : 0.03 * DetMath.Cos(2 * i + 1);
        }
        var P = new double[n]; var Q = new double[n];
        ComputeInjections(y, Vm, Va, P, Q);

        // Old path: the pre-refactor triplet builder (values dropped when exactly zero).
        CsrMatrix oldJ = OldBuildJacobian(y, Vm, Va, P, Q, pPos, qPos, m);

        // New path: structure once + refill.
        var slots = new int[4 * y.RowPtr[n]];
        CsrMatrix newJ = SparseNewtonRaphsonPowerFlow.BuildJacobianStructure(y, pPos, qPos, m, slots);
        SparseNewtonRaphsonPowerFlow.RefillJacobian(newJ, slots, y, Vm, Va, P, Q);

        // Every old cell must be present in new with a bit-identical value.
        for (int r = 0; r < m; r++)
            for (int p = oldJ.RowPtr[r]; p < oldJ.RowPtr[r + 1]; p++)
            {
                int col = oldJ.ColIdx[p];
                int at = Find(newJ, r, col);
                Assert.True(at >= 0, $"cell ({r},{col}) present in old build missing from refill");
                Assert.Equal(BitConverter.DoubleToInt64Bits(oldJ.Val[p]), BitConverter.DoubleToInt64Bits(newJ.Val[at]));
            }

        // Every extra cell in new (the superset) must be an explicit zero.
        for (int r = 0; r < m; r++)
            for (int p = newJ.RowPtr[r]; p < newJ.RowPtr[r + 1]; p++)
                if (Find(oldJ, r, newJ.ColIdx[p]) < 0)
                    Assert.Equal(0.0, newJ.Val[p]);
    }

    private static int Find(CsrMatrix a, int row, int col)
    {
        for (int p = a.RowPtr[row]; p < a.RowPtr[row + 1]; p++)
            if (a.ColIdx[p] == col) return p;
        return -1;
    }

    // Verbatim copy of the pre-refactor BuildJacobian (drops exactly-zero blocks via Builder.Add).
    private static CsrMatrix OldBuildJacobian(ComplexCsr y, double[] Vm, double[] Va, double[] P, double[] Q,
        int[] pPos, int[] qPos, int m)
    {
        int n = y.N; int[] rp = y.RowPtr, ci = y.ColIdx; double[] G = y.G, B = y.B;
        var jb = new CsrMatrix.Builder(m);
        for (int i = 0; i < n; i++)
        {
            int pi = pPos[i], qi = qPos[i];
            if (pi < 0 && qi < 0) continue;
            for (int k = rp[i]; k < rp[i + 1]; k++)
            {
                int j = ci[k];
                int pj = pPos[j], qj = qPos[j];
                double Gij = G[k], Bij = B[k];
                double H, N, M, L;
                if (i == j)
                {
                    H = -Q[i] - Bij * Vm[i] * Vm[i];
                    N = P[i] / Vm[i] + Gij * Vm[i];
                    M = P[i] - Gij * Vm[i] * Vm[i];
                    L = Q[i] / Vm[i] - Bij * Vm[i];
                }
                else
                {
                    double th = Va[i] - Va[j];
                    (double sin, double cos) = DetMath.SinCos(th);
                    H = Vm[i] * Vm[j] * (Gij * sin - Bij * cos);
                    N = Vm[i] * (Gij * cos + Bij * sin);
                    M = -Vm[i] * Vm[j] * (Gij * cos + Bij * sin);
                    L = Vm[i] * (Gij * sin - Bij * cos);
                }
                if (pi >= 0 && pj >= 0) jb.Add(pi, pj, H);
                if (pi >= 0 && qj >= 0) jb.Add(pi, qj, N);
                if (qi >= 0 && pj >= 0) jb.Add(qi, pj, M);
                if (qi >= 0 && qj >= 0) jb.Add(qi, qj, L);
            }
        }
        return jb.Build();
    }

    // Mirror of the solver's injection kernel (needed to feed the diagonal Jacobian terms).
    private static void ComputeInjections(ComplexCsr y, double[] Vm, double[] Va, double[] P, double[] Q)
    {
        int n = y.N; int[] rp = y.RowPtr, ci = y.ColIdx; double[] G = y.G, B = y.B;
        for (int i = 0; i < n; i++)
        {
            double pi = 0, qi = 0, vai = Va[i];
            for (int k = rp[i]; k < rp[i + 1]; k++)
            {
                int j = ci[k];
                double th = vai - Va[j];
                (double sin, double cos) = DetMath.SinCos(th);
                double vmj = Vm[j];
                pi += vmj * (G[k] * cos + B[k] * sin);
                qi += vmj * (G[k] * sin - B[k] * cos);
            }
            P[i] = Vm[i] * pi;
            Q[i] = Vm[i] * qi;
        }
    }

    private static GridModel Load(string caseName) => caseName switch
    {
        "case14" => IeeeCases.Case14(),
        "case30" => IeeeCases.Case30(),
        "case118" => IeeeCases.Case118(),
        "case300" => IeeeCases.Case300(),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
    };
}
