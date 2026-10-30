using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Storage;
using GridSim.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>DOC-16 D183: see the summary of <see cref="Doc16NumericsDeterminismTests"/>.</summary>
public partial class Doc16NumericsDeterminismTests
{
    // =================================================================================================================
    // D-DOC-183: weak-mesh compensation and whole solves through every tier.
    // =================================================================================================================

    [Fact]
    public void D183_weak_mesh_sweep_closes_case33bw_ties_and_agrees_with_newton()
    {
        // The MATPOWER parser drops status-0 branches, so the five normally-open ties of Baran and Wu (21-8, 9-15, 12-22,
        // 18-33, 25-29, status 0 in the file) are closed in the TEXT: their status column "0" becomes "1". Every other row
        // already reads "1" there, so the replacement touches exactly the tie rows (the first `closed` of them).
        string text = S10TestSupport.MatpowerText("dist/case33bw.m");
        const string openTie = "\t0\t-360\t360;", closedTie = "\t1\t-360\t360;";
        Assert.Equal(5, text.Split(openTie).Length - 1);
        GridModel Parse(string t)
        {
            MatpowerParseResult r = MatpowerParser.ParseWithDiagnostics(t, "case33bw");
            return MatpowerUnitNormalizer.Normalize(r.Model);
        }
        GridModel radial = Parse(text);
        Assert.True(SolverSelector.LooksRadial(radial));
        var radialPairs = new HashSet<(int, int)>(radial.Branches.Select(b => (b.From, b.To)));
        var opt = new PowerFlowOptions();
        PowerFlowResult openTies = new BackwardForwardSweep().Solve(radial, opt);
        Assert.True(openTies.Converged);

        foreach (int closed in new[] { 1, 5 })
        {
            string t = text;
            for (int c = 0; c < closed; c++)
            {
                int at = t.IndexOf(openTie, StringComparison.Ordinal);
                t = t[..at] + closedTie + t[(at + openTie.Length)..];
            }
            GridModel meshed = Parse(t);
            Assert.Equal(radial.Branches.Count + closed, meshed.Branches.Count);
            Assert.False(SolverSelector.LooksRadial(meshed));   // weakly meshed: the selector would send it to Newton

            PowerFlowResult sweep = new BackwardForwardSweep().Solve(meshed, opt);
            PowerFlowResult again = new BackwardForwardSweep().Solve(meshed, opt);
            PowerFlowResult newton = new SparseNewtonRaphsonPowerFlow().Solve(meshed, opt);
            Assert.True(sweep.Converged, $"{closed} tie(s) closed: the compensated sweep did not converge ({sweep.Status})");
            Assert.True(newton.Converged);
            for (int i = 0; i < sweep.Vm.Length; i++)
            {
                Assert.Equal(Bits(sweep.Vm[i]), Bits(again.Vm[i]));         // deterministic, bit for bit
                Assert.Equal(Bits(sweep.VaDeg[i]), Bits(again.VaDeg[i]));
            }

            double dVm = 0, dVa = 0, moved = 0;
            for (int i = 0; i < sweep.Vm.Length; i++)
            {
                dVm = Math.Max(dVm, Math.Abs(sweep.Vm[i] - newton.Vm[i]));
                dVa = Math.Max(dVa, Math.Abs(sweep.VaDeg[i] - newton.VaDeg[i]));
                moved = Math.Max(moved, Math.Abs(sweep.Vm[i] - openTies.Vm[i]));
            }
            double dLoss = Math.Abs(sweep.TotalLossMw - newton.TotalLossMw);
            // The tie branches themselves: the compensated flows equal Newton's.
            double dTie = 0;
            for (int b = 0; b < meshed.Branches.Count; b++)
            {
                if (radialPairs.Contains((meshed.Branches[b].From, meshed.Branches[b].To))) continue;   // only the closed ties
                LineFlow fs = sweep.LineFlows.Single(f => f.From == meshed.Branches[b].From && f.To == meshed.Branches[b].To);
                LineFlow fn = newton.LineFlows.Single(f => f.From == meshed.Branches[b].From && f.To == meshed.Branches[b].To);
                dTie = Math.Max(dTie, Math.Abs(fs.PFromMw - fn.PFromMw));
            }
            _out.WriteLine($"case33bw, {closed} tie(s) closed: sweep {sweep.Iterations} it vs Newton {newton.Iterations}; max |dVm| {dVm:E3} pu, "
                + $"max |dVa| {dVa:E3} deg, |dLoss| {dLoss:E3} MW, tie |dP| {dTie:E3} MW; largest Vm move vs ties open {moved:E3} pu");
            Assert.True(moved > 1e-4, "closing the ties must change the voltages (otherwise nothing is compensated)");
            Assert.True(dVm <= 1e-6 && dVa <= 1e-4, $"compensated sweep vs Newton: |dVm| {dVm:E3}, |dVa| {dVa:E3}");
            Assert.True(dTie <= 1e-4 && dLoss <= 1e-4, $"compensated sweep vs Newton: tie |dP| {dTie:E3} MW, |dLoss| {dLoss:E3} MW");
        }
    }

    /// <summary>The dense LU as an <see cref="ILinearSolver"/> with its row update pinned to one vector tier (the R102
    /// seam), copying exactly as <see cref="DenseLuSolver.SolveInPlace"/> does.</summary>
    private sealed class TierPinnedDenseLu(SimdTier tier) : ILinearSolver
    {
        public string Name => $"dense-lu (cpu, {tier})";

        public void SolveInPlace(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = new double[n * n];
            Buffer.BlockCopy(a, 0, m, 0, n * n * sizeof(double));
            DenseLuSolver.FactorSolveInPlace(m, b, n, tier);
            Buffer.BlockCopy(m, 0, a, 0, n * n * sizeof(double));
        }
    }

    private static readonly SimdTier[] AllTiers = { SimdTier.Scalar, SimdTier.Sse128, SimdTier.Avx256, SimdTier.Avx512 };

    private static void AssertSameResult(string label, PowerFlowResult a, PowerFlowResult b)
    {
        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(Bits(a.MaxMismatch), Bits(b.MaxMismatch));
        for (int i = 0; i < a.Vm.Length; i++)
        {
            Assert.True(Bits(a.Vm[i]) == Bits(b.Vm[i]), $"{label}: Vm[{i}] {R(a.Vm[i])} vs {R(b.Vm[i])}");
            Assert.True(Bits(a.VaDeg[i]) == Bits(b.VaDeg[i]), $"{label}: VaDeg[{i}] {R(a.VaDeg[i])} vs {R(b.VaDeg[i])}");
            Assert.True(Bits(a.PInjMw[i]) == Bits(b.PInjMw[i]) && Bits(a.QInjMvar[i]) == Bits(b.QInjMvar[i]), $"{label}: injection {i}");
        }
        Assert.Equal(Bits(a.TotalLossMw), Bits(b.TotalLossMw));
        Assert.Equal(Bits(a.TotalLossMvar), Bits(b.TotalLossMvar));
        for (int k = 0; k < a.LineFlows.Count; k++)
        {
            Assert.Equal(Bits(a.LineFlows[k].PFromMw), Bits(b.LineFlows[k].PFromMw));
            Assert.Equal(Bits(a.LineFlows[k].QToMvar), Bits(b.LineFlows[k].QToMvar));
        }
    }

    [Theory]
    [InlineData("9")]
    [InlineData("14")]
    [InlineData("30")]
    [InlineData("57")]
    [InlineData("118")]
    [InlineData("300")]
    public void D183_a_whole_newton_solve_on_the_dense_lu_gives_the_same_bits_at_every_vector_tier(string name)
    {
        GridModel m = name switch
        {
            "9" => IeeeCases.Case9(), "14" => IeeeCases.Case14(), "30" => IeeeCases.Case30(),
            "57" => IeeeCases.Case57(), "118" => IeeeCases.Case118(), _ => IeeeCases.Case300(),
        };
        var opt = new PowerFlowOptions { EnforceQLimits = true };
        // The tier the machine selects (what production runs) is the reference; every tier's arm must give its bits.
        PowerFlowResult reference = new NewtonRaphsonPowerFlow(new TierPinnedDenseLu(SimdCapabilities.Tier)).Solve(m, opt);
        Assert.True(reference.Converged);
        foreach (SimdTier tier in AllTiers)
            AssertSameResult($"case{name} at {tier}", reference, new NewtonRaphsonPowerFlow(new TierPinnedDenseLu(tier)).Solve(m, opt));
        _out.WriteLine($"case{name}: {reference.Iterations} iterations, identical at Scalar, Sse128, Avx256 and Avx512 (machine tier {SimdCapabilities.Tier})");
    }

    [Theory]
    [InlineData("14")]
    [InlineData("57")]
    [InlineData("118")]
    public void D183_a_whole_dense_wls_estimate_gives_the_same_bits_at_every_vector_tier(string name)
    {
        GridModel m = name switch { "14" => IeeeCases.Case14(), "57" => IeeeCases.Case57(), _ => IeeeCases.Case118() };
        PowerFlowResult truth = new SparseNewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 1.0 });
        // Test changed 2026-10-19: 1.95.299 (Mark's ruling 2026-10-15, GR-066/383/418) moved every CPU WLS estimate onto
        // the one canonical SPARSE step (CanonicalWlsStep), so production is no longer the dense LU this test pins per
        // tier, and the two differ in the last bits of J (the 2026-10-19 run, dense Scalar arm vs production:
        // case14 80.01052231478428 vs 80.01052231478407, case57 407.14427112449533 vs 407.1442711244965, case118
        // 877.8012390625249 vs 877.8012390625028). The tier proof of the dense step (still the GPU-fallback and injected
        // solver path) now takes the dense arm at the machine's own tier as its reference; every tier must give exactly
        // its bits. The canonical sparse step's own identity across the dense/sparse threshold is CanonicalWlsParityTests.
        EstimationResult production = new WlsStateEstimator().Estimate(m, set, new EstimationOptions());
        Assert.Equal(EstimationStatus.Converged, production.Status);
        EstimationResult reference = new WlsStateEstimator(new TierPinnedDenseLu(SimdCapabilities.Tier)).Estimate(m, set, new EstimationOptions());
        Assert.Equal(EstimationStatus.Converged, reference.Status);
        foreach (SimdTier tier in AllTiers)
        {
            EstimationResult e = new WlsStateEstimator(new TierPinnedDenseLu(tier)).Estimate(m, set, new EstimationOptions());
            Assert.Equal(reference.Status, e.Status);
            Assert.Equal(Bits(reference.ObjectiveJ), Bits(e.ObjectiveJ));
            for (int i = 0; i < reference.Vm.Length; i++)
            {
                Assert.True(Bits(reference.Vm[i]) == Bits(e.Vm[i]), $"case{name} at {tier}: Vm[{i}]");
                Assert.True(Bits(reference.VaDeg[i]) == Bits(e.VaDeg[i]), $"case{name} at {tier}: VaDeg[{i}]");
            }
        }
        _out.WriteLine($"case{name}: dense WLS J {R(reference.ObjectiveJ)}, identical at every tier (machine tier {SimdCapabilities.Tier}); "
            + $"production (canonical sparse) J {R(production.ObjectiveJ)}");
    }
}
