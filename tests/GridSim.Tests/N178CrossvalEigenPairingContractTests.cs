using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N178 (October 2026): tools/crossval/crossval_smallsignal.py paired GridSim's eigenvalues with SciPy's by sorting both
/// on (Im, Re) and zipping. Real eigenvalues whose imaginary parts are rounding noise of opposite sign were then
/// mis-paired: on data/gb-synthetic with --enable-inverters (116 states) it reported max_rel 5.0 (-20 against -3.33)
/// where the correct pairing gives 6.187947918113335e-08. The script now pairs by an optimal assignment on the gate's
/// own relative distance. The test run has no SciPy, so these are source-level contracts (like N142's), measured
/// before / after on 19 exports.
/// </summary>
public sealed class N178CrossvalEigenPairingContractTests
{
    private static string Script() =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "tools", "crossval", "crossval_smallsignal.py"));

    /// <summary>The pairing is an optimal assignment, and the old sort-and-zip ordering is gone.</summary>
    [Fact]
    public void Pairs_by_optimal_assignment()
    {
        string src = Script();
        Assert.Contains("from scipy.optimize import linear_sum_assignment", src, StringComparison.Ordinal);
        Assert.Contains("rows, cols = linear_sum_assignment(cost)", src, StringComparison.Ordinal);
        // The cost is the gate's own per-mode relative distance |gs_i - sp_j| / max(|sp_j|, 1).
        Assert.Contains("cost = np.abs(gs[:, None] - sp[None, :]) / np.maximum(np.abs(sp), 1.0)[None, :]", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_canonical_order", src, StringComparison.Ordinal);
        Assert.DoesNotContain("np.lexsort", src, StringComparison.Ordinal);
    }

    /// <summary>The participation cross-check uses the same pairing (SciPy's vectors reordered by it).</summary>
    [Fact]
    public void Participation_uses_the_same_pairing()
    {
        string src = Script();
        Assert.Contains("vr_s = vr[:, cols]", src, StringComparison.Ordinal);
        Assert.Contains("vl_s = vl[:, cols]", src, StringComparison.Ordinal);
    }

    /// <summary>The fix does not loosen anything: the default stays 4096 machine epsilons, and a count mismatch
    /// between the export and SciPy is a setup error (exit 3 since N248, October 2026; it was exit 2 when this test was
    /// written), never a silent partial comparison. Comment only (N280): the assertions below pin the source text, not
    /// the exit code, and are unchanged.</summary>
    [Fact]
    public void Tolerance_unchanged_and_count_mismatch_fails_loud()
    {
        string src = Script();
        Assert.Contains("DEFAULT_RTOL_EPS = 4096", src, StringComparison.Ordinal);
        Assert.Contains("raise ValueError(f\"GridSim exported {gs.size} eigenvalues but SciPy found {sp.size}\")", src, StringComparison.Ordinal);
        Assert.Contains("guard = args.rtol * max(1.0, condV)", src, StringComparison.Ordinal);
    }
}
