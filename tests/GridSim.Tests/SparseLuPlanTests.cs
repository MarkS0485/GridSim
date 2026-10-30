using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The sparse LU plan (<see cref="SparseLuPlan"/>) and its replay (<see cref="SparseLuFactorization.Refactor"/>): a
/// factorisation from the plan must hold exactly the bits a fresh factorisation of the same values holds, or not be
/// produced (a different pivot), and the power flow through the plan-reusing solver must give exactly the bits of the
/// solve that factors every Jacobian from scratch.
/// </summary>
public class SparseLuPlanTests
{
    /// <summary>A deterministic random sparse matrix in CSC form: a strong diagonal (scaled by
    /// <paramref name="diag"/>) plus about <paramref name="perCol"/> off-diagonal entries per column.</summary>
    private static (int[] Ap, int[] Ai, double[] Ax) Random(int n, int perCol, ulong seed, double diag = 4.0)
    {
        ulong s = seed;
        double Next() { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return ((s >> 11) * (1.0 / (1UL << 53))) - 0.5; }
        var cols = new List<(int Row, double V)>[n];
        for (int c = 0; c < n; c++)
        {
            var rows = new SortedDictionary<int, double> { [c] = diag + Next() };
            for (int k = 0; k < perCol; k++)
            {
                int r = (int)((s >> 33) % (ulong)n);
                Next();
                if (!rows.ContainsKey(r)) rows[r] = Next();
            }
            cols[c] = rows.Select(kv => (kv.Key, kv.Value)).ToList();
        }
        var ap = new int[n + 1];
        for (int c = 0; c < n; c++) ap[c + 1] = ap[c] + cols[c].Count;
        var ai = new int[ap[n]];
        var ax = new double[ap[n]];
        for (int c = 0, p = 0; c < n; c++) foreach ((int r, double v) in cols[c]) { ai[p] = r; ax[p] = v; p++; }
        return (ap, ai, ax);
    }

    private static int[] Identity(int n) => Enumerable.Range(0, n).ToArray();

    private static void SameBits(ReadOnlySpan<double> a, ReadOnlySpan<double> b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(a[i]) == BitConverter.DoubleToInt64Bits(b[i]), $"{what} [{i}]: {a[i]:R} vs {b[i]:R}");
    }

    [Theory]
    [InlineData(50, 3, 1UL)]
    [InlineData(300, 4, 7UL)]
    [InlineData(1000, 3, 99UL)]
    public void A_replayed_factorisation_holds_the_bits_of_a_fresh_one(int n, int perCol, ulong seed)
    {
        (int[] ap, int[] ai, double[] ax) = Random(n, perCol, seed);
        int[] q = Identity(n);
        SparseLuFactorization first = SparseLuFactorization.Factor(n, ap, ai, ax, q, keepPlan: true)!;
        SparseLuPlan plan = first.Plan!;
        // The plan changes nothing about the factorisation it records.
        SparseLuFactorization plain = SparseLuFactorization.Factor(n, ap, ai, ax, q)!;
        SameBits(plain.LValues, first.LValues, "L with plan");
        SameBits(plain.UValues, first.UValues, "U with plan");

        // New values on the same pattern (each entry nudged by a few percent): the replay against a fresh factorisation.
        ulong s = seed + 1000;
        double[] ax2 = ax.Select(v => { s = s * 6364136223846793005UL + 1442695040888963407UL; return v * (1 + 0.05 * (((s >> 11) * (1.0 / (1UL << 53))) - 0.5)); }).ToArray();
        SparseLuFactorization fresh = SparseLuFactorization.Factor(n, ap, ai, ax2, q)!;
        SparseLuFactorization? replay = SparseLuFactorization.Refactor(plan, ax2);
        Assert.NotNull(replay);
        SameBits(fresh.LValues, replay!.LValues, "L replayed");
        SameBits(fresh.UValues, replay.UValues, "U replayed");
        double[] b = Enumerable.Range(0, n).Select(i => Math.Sin(i)).ToArray();
        SameBits(fresh.Solve(b), replay.Solve(b), "solution");
    }

    [Fact]
    public void A_value_change_that_moves_a_pivot_is_refused_and_the_fresh_factorisation_differs()
    {
        // 2 x 2 with column 0 = (1, 3): the pivot is row 1. New values (3, 1): the pivot moves to row 0.
        int[] ap = { 0, 2, 4 }, ai = { 0, 1, 0, 1 }, q = { 0, 1 };
        double[] ax = { 1, 3, 2, 5 };
        SparseLuPlan plan = SparseLuFactorization.Factor(2, ap, ai, ax, q, keepPlan: true)!.Plan!;
        Assert.Equal(1, plan.PivotRow[0]);
        double[] moved = { 3, 1, 2, 5 };
        Assert.Null(SparseLuFactorization.Refactor(plan, moved));
        Assert.NotNull(SparseLuFactorization.Factor(2, ap, ai, moved, q));
        // A singular matrix is refused by both.
        double[] singular = { 1, 2, 2, 4 };
        Assert.Null(SparseLuFactorization.Refactor(plan, singular));
        Assert.Null(SparseLuFactorization.Factor(2, ap, ai, singular, q));
    }

    /// <summary>A pass-through solver that, at every Newton step, checks the opt-in fixed-pattern solver against a
    /// fresh structural factorisation of the same Jacobian (and then lets the default solver take the step, so the
    /// iteration is the default one).</summary>
    private sealed class CheckFixedPattern : ISparseLinearSolver
    {
        private readonly CpuSparseLuSolver _default = new();
        public readonly FixedPatternSparseLuSolver Fixed = new();
        public int Checked;
        public string Name => "check-fixed-pattern";
        public double[]? Solve(CsrMatrix a, double[] b)
        {
            SparseLuFactorization? viaPlan = Fixed.Factor(a);
            SparseLuFactorization? fresh = FixedPatternSparseLuSolver.FreshStructural(a);
            Assert.Equal(fresh is null, viaPlan is null);
            if (fresh is not null)
            {
                SameBits(fresh.LValues, viaPlan!.LValues, $"call {Checked} L");
                SameBits(fresh.UValues, viaPlan.UValues, $"call {Checked} U");
                SameBits(fresh.Solve(b), viaPlan.Solve(b), $"call {Checked} x");
            }
            Checked++;
            return _default.Solve(a, b);
        }
    }

    [Theory]
    [InlineData("case300")]
    [InlineData("gb-full")]
    public void The_opt_in_fixed_pattern_solver_equals_a_fresh_structural_factorisation_at_every_newton_step(string name)
    {
        GridModel m = name == "gb-full"
            ? JsonGridLoader.LoadDirectory(Path.Combine(CliProcess.Root(), "data", "gb-full"))
            : IeeeCases.Case300();
        var check = new CheckFixedPattern();
        Assert.True(new SparseNewtonRaphsonPowerFlow(check).Solve(m).Converged);
        Assert.True(check.Checked >= 3);
        // The plan is replayed (the whole point of the opt-in path): every step after the first on one structure.
        Assert.Equal(check.Checked, check.Fixed.PlanReplays + check.Fixed.FullFactorisations);
        Assert.True(2 * check.Fixed.PlanReplays > check.Checked, $"replays {check.Fixed.PlanReplays} of {check.Checked}");
    }

    [Theory]
    [InlineData("case300")]
    [InlineData("gb-full")]
    public void The_opt_in_power_flow_converges_to_the_default_answer_and_the_default_path_is_untouched(string name)
    {
        GridModel m = name == "gb-full"
            ? JsonGridLoader.LoadDirectory(Path.Combine(CliProcess.Root(), "data", "gb-full"))
            : IeeeCases.Case300();
        PowerFlowResult byDefault = new SparseNewtonRaphsonPowerFlow().Solve(m);
        PowerFlowResult byDefaultAgain = new SparseNewtonRaphsonPowerFlow(new CpuSparseLuSolver()).Solve(m);
        PowerFlowResult optIn = new SparseNewtonRaphsonPowerFlow(new FixedPatternSparseLuSolver()).Solve(m);
        Assert.True(byDefault.Converged && optIn.Converged);
        // The default path is unchanged by the new classes: the default solver gives the default bits.
        SameBits(byDefault.Vm, byDefaultAgain.Vm, "default Vm");
        SameBits(byDefault.VaDeg, byDefaultAgain.VaDeg, "default Va");
        // The opt-in path solves the same equations to the same tolerance; it is NOT bit-identical to the default (kept
        // zeros change the update order), so it is compared to the Newton tolerance scale, never to the bit.
        Assert.Equal(byDefault.Iterations, optIn.Iterations);
        for (int i = 0; i < byDefault.Vm.Length; i++)
        {
            Assert.True(Math.Abs(byDefault.Vm[i] - optIn.Vm[i]) <= 1e-9, $"Vm[{i}] {byDefault.Vm[i]:R} vs {optIn.Vm[i]:R}");
            Assert.True(Math.Abs(byDefault.VaDeg[i] - optIn.VaDeg[i]) <= 1e-7, $"Va[{i}] {byDefault.VaDeg[i]:R} vs {optIn.VaDeg[i]:R}");
        }
    }

    [Fact]
    public void The_diagonal_preference_rule_picks_the_diagonal_above_the_threshold_and_is_off_by_default()
    {
        // Column 0 = (1, 3): plain partial pivoting takes row 1 (|3| > |1|). With a preference of 0.1 the diagonal
        // (|1| >= 0.1 x 3) is taken; with 0.5 it is not (|1| < 1.5).
        int[] ap = { 0, 2, 4 }, ai = { 0, 1, 0, 1 }, q = { 0, 1 };
        double[] ax = { 1, 3, 2, 5 };
        Assert.Equal(1, SparseLuFactorization.Factor(2, ap, ai, ax, q, keepPlan: true)!.Plan!.PivotRow[0]);
        Assert.Equal(0, SparseLuFactorization.Factor(2, ap, ai, ax, q, keepPlan: true, diagonalPreference: 0.1)!.Plan!.PivotRow[0]);
        Assert.Equal(1, SparseLuFactorization.Factor(2, ap, ai, ax, q, keepPlan: true, diagonalPreference: 0.5)!.Plan!.PivotRow[0]);
        // Off by default: an explicit 0 is the plain rule, bit for bit.
        SparseLuFactorization plain = SparseLuFactorization.Factor(2, ap, ai, ax, q)!;
        SparseLuFactorization zero = SparseLuFactorization.Factor(2, ap, ai, ax, q, diagonalPreference: 0.0)!;
        SameBits(plain.LValues, zero.LValues, "L");
        SameBits(plain.UValues, zero.UValues, "U");
        // Both factorisations solve the system (to rounding).
        double[] b = { 1, 2 };
        double[] xPlain = plain.Solve(b), xPref = SparseLuFactorization.Factor(2, ap, ai, ax, q, diagonalPreference: 0.1)!.Solve(b);
        for (int i = 0; i < 2; i++) Assert.True(Math.Abs(xPlain[i] - xPref[i]) <= 1e-15, $"x[{i}]");
        // The range is checked.
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLuFactorization.Factor(2, ap, ai, ax, q, diagonalPreference: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLuFactorization.Factor(2, ap, ai, ax, q, diagonalPreference: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLuFactorization.Factor(2, ap, ai, ax, q, diagonalPreference: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedPatternSparseLuSolver(2.0));
    }

    [Theory]
    [InlineData(300, 4, 21UL, 0.1)]
    [InlineData(1000, 3, 22UL, 0.001)]
    [InlineData(1000, 3, 23UL, 0.5)]
    public void A_replay_under_the_diagonal_preference_rule_holds_the_bits_of_a_fresh_one(int n, int perCol, ulong seed, double tau)
    {
        (int[] ap, int[] ai, double[] ax) = Random(n, perCol, seed, diag: 0.8);   // weak diagonal: the rule matters
        int[] q = Identity(n);
        SparseLuFactorization? first = SparseLuFactorization.Factor(n, ap, ai, ax, q, keepPlan: true, diagonalPreference: tau);
        Assert.NotNull(first);
        Assert.Equal(tau, first!.Plan!.DiagonalPreference);
        ulong s = seed + 77;
        double[] ax2 = ax.Select(v => { s = s * 6364136223846793005UL + 1442695040888963407UL; return v * (1 + 1e-4 * (((s >> 11) * (1.0 / (1UL << 53))) - 0.5)); }).ToArray();
        SparseLuFactorization? fresh = SparseLuFactorization.Factor(n, ap, ai, ax2, q, diagonalPreference: tau);
        SparseLuFactorization? replay = SparseLuFactorization.Refactor(first.Plan!, ax2);
        Assert.NotNull(fresh);
        // These seeds keep the pivot order under the rule, so the replay is produced (not refused) and must match.
        Assert.NotNull(replay);
        SameBits(fresh!.LValues, replay!.LValues, "L replayed");
        SameBits(fresh.UValues, replay.UValues, "U replayed");
    }
}
