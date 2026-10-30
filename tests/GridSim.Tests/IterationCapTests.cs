using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 docs-programme finding: <see cref="PowerFlowOptions.MaxIterations"/> was in effect the cap minus one.
/// The Newton loop tested the mismatch at the top of each iteration, so after the last allowed step it stopped without
/// evaluating the new state: a solve whose final step converged was reported <see cref="PowerFlowStatus.MaxIterations"/>
/// with the mismatch from before that step. Both engines now evaluate the state after every step, the last included.
/// </summary>
public class IterationCapTests
{
    public static IEnumerable<object[]> Engines => new[] { new object[] { "dense" }, new object[] { "sparse" } };

    private static IPowerFlowSolver Engine(string name) =>
        name == "dense" ? new NewtonRaphsonPowerFlow() : new SparseNewtonRaphsonPowerFlow();

    private static void SameBits(double[] a, double[] b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(a[i]) == BitConverter.DoubleToInt64Bits(b[i]), $"{what}[{i}]: {a[i]:R} vs {b[i]:R}");
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void A_solve_that_converges_on_the_last_allowed_step_is_converged(string engine)
    {
        foreach (GridModel m in new[] { IeeeCases.Case9(), IeeeCases.Case14(), IeeeCases.Case118() })
        {
            var free = new PowerFlowOptions { EnforceQLimits = false, FlatStart = true };
            PowerFlowResult uncapped = Engine(engine).Solve(m, free);
            Assert.True(uncapped.Converged, $"{m.Name}: precondition");
            int k = uncapped.Iterations;
            Assert.True(k >= 2, $"{m.Name}: needs at least two steps from a flat start, took {k}");

            // A cap of exactly k steps: the k-th step converges and must be recognised (it used to be MaxIterations).
            PowerFlowResult atCap = Engine(engine).Solve(m, free with { MaxIterations = k });
            Assert.True(atCap.Converged, $"{m.Name} [{engine}]: converged on step {k} of a {k}-step cap but reported {atCap.Status}");
            Assert.Equal(k, atCap.Iterations);
            // Same path, same arithmetic: the capped solve is bit-identical to the uncapped one.
            Assert.Equal(BitConverter.DoubleToInt64Bits(uncapped.MaxMismatch), BitConverter.DoubleToInt64Bits(atCap.MaxMismatch));
            SameBits(uncapped.Vm, atCap.Vm, $"{m.Name} Vm");
            SameBits(uncapped.VaDeg, atCap.VaDeg, $"{m.Name} VaDeg");

            // One step fewer cannot converge: an honest MaxIterations outcome with k-1 steps taken.
            PowerFlowResult short1 = Engine(engine).Solve(m, free with { MaxIterations = k - 1 });
            Assert.False(short1.Converged);
            Assert.Equal(PowerFlowStatus.MaxIterations, short1.Status);
            Assert.Equal(k - 1, short1.Iterations);
            Assert.True(short1.MaxMismatch >= free.Tolerance, $"{m.Name}: a non-converged state reads below tolerance");
        }
    }

    [Fact]
    public void Dense_and_sparse_still_agree_to_the_bit_on_a_capped_non_converged_solve()
    {
        // pglib case3_lmbd does not converge at any cap; the capped result (state and the now-honest mismatch) must
        // be bit-identical between the engines, as every other result is.
        string path = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "nesta", "pglib_opf_case3_lmbd.m");
        Assert.True(File.Exists(path), $"bundled case file missing: {path}");
        GridModel m = GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(path);
        var opt = new PowerFlowOptions { MaxIterations = 10, EnforceQLimits = false };
        PowerFlowResult d = new NewtonRaphsonPowerFlow().Solve(m, opt);
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);
        Assert.False(d.Converged);
        Assert.Equal(d.Status, s.Status);
        Assert.Equal(d.Iterations, s.Iterations);
        Assert.Equal(BitConverter.DoubleToInt64Bits(d.MaxMismatch), BitConverter.DoubleToInt64Bits(s.MaxMismatch));
        SameBits(d.Vm, s.Vm, "Vm");
        SameBits(d.VaDeg, s.VaDeg, "VaDeg");
    }
}
