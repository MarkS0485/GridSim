using System.Numerics;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.Numerics;

namespace GridSim.Core.Physics.Experimental;

/// <summary>
/// EXPERIMENTAL, opt-in structural-consistency checks on an assembled small-signal model. Two independent
/// residuals are surfaced DISTINCTLY (one <see cref="LawResult"/> each), mirroring the
/// <see cref="ReciprocityCheck"/> shape:
/// <list type="number">
/// <item><b>COI null mode</b> - the swing-only state matrix has an eigenvalue at the origin (the rigid-body
/// centre-of-inertia mode: a uniform angle shift is a genuine null of the synchronising Laplacian). Residual
/// = the smallest |eigenvalue|.</item>
/// <item><b>K_ext row-sum</b> - the synchronising Laplacian has zero row sums (uniform angle-shift
/// invariance forces sum_j dPe_i/ddelta_j = 0, valid even with transfer losses). Residual = max |row sum|.</item>
/// </list>
/// Like <see cref="ReciprocityCheck"/> this NEVER gates a state (it is not in the hard-gate set); it is a
/// build-time audit of the assembled model.
/// </summary>
public static class SmallSignalConsistency
{
    /// <summary>Both residuals for <paramref name="m"/>.</summary>
    public static SmallSignalConsistencyReport Evaluate(SmallSignalModel m, double tol = 1e-6)
    {
        // (1) COI null mode: smallest |eigenvalue|.
        double minAbs = double.PositiveInfinity;
        foreach (Complex s in GeneralEigen.Eigenvalues(m.A))
            if (s.Magnitude < minAbs) minAbs = s.Magnitude;
        if (double.IsPositiveInfinity(minAbs)) minAbs = 0.0;
        // Law 16 (was 12 until 1.95.006, which collided with harmonic KCL; R091).
        var coi = new LawResult(PhysicsCheckRegistry.CoiNullModeLaw, PhysicsCheckRegistry.ExperimentalLaws[PhysicsCheckRegistry.CoiNullModeLaw],
            "swing-only A has a rigid-body centre-of-inertia eigenvalue at s = 0",
            minAbs <= tol ? LawStatus.Holds : LawStatus.Violated,
            minAbs, "rad/s", tol,
            $"min |eigenvalue| = {minAbs:E3} rad/s");

        // (2) K_ext row sums.
        int kn = m.Kext.GetLength(0);
        double maxRow = 0.0;
        double kScale = 0.0;
        for (int i = 0; i < kn; i++)
        {
            double rs = 0.0;
            for (int j = 0; j < kn; j++) { rs += m.Kext[i, j]; double av = Math.Abs(m.Kext[i, j]); if (av > kScale) kScale = av; }
            if (Math.Abs(rs) > maxRow) maxRow = Math.Abs(rs);
        }
        double kTol = tol * Math.Max(1.0, kScale);
        var rowSum = new LawResult(PhysicsCheckRegistry.LaplacianRowSumLaw, PhysicsCheckRegistry.ExperimentalLaws[PhysicsCheckRegistry.LaplacianRowSumLaw],
            "K_ext row sums vanish (uniform angle-shift invariance)",
            maxRow <= kTol ? LawStatus.Holds : LawStatus.Violated,
            maxRow, "MW/rad", kTol,
            $"max |row sum| = {maxRow:E3} MW/rad (scale {kScale:E3})");

        return new SmallSignalConsistencyReport(coi, rowSum);
    }
}

/// <summary>The two distinct small-signal consistency residuals, plus a combined status.</summary>
public sealed record SmallSignalConsistencyReport(LawResult CoiNullMode, LawResult KextRowSum)
{
    /// <summary>Both residuals as a list (worst-residual first).</summary>
    public IReadOnlyList<LawResult> Laws => CoiNullMode.Residual >= KextRowSum.Residual
        ? new[] { CoiNullMode, KextRowSum }
        : new[] { KextRowSum, CoiNullMode };

    /// <summary>Holds only when BOTH residuals hold.</summary>
    public LawStatus Status => CoiNullMode.Status == LawStatus.Holds && KextRowSum.Status == LawStatus.Holds
        ? LawStatus.Holds : LawStatus.Violated;
}
