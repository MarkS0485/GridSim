using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The batched DC power flow (<see cref="DcPowerFlow.SolveBatch"/>): every model's result must hold exactly the bits
/// <see cref="DcPowerFlow.Solve"/> gives for that model alone, whether its B' shared the multi-case lanes with others of
/// the same structure or sat in a family of its own, at any thread count; and a model that cannot be solved must fail the
/// batch loudly, naming its position, with the single solve's own error inside.
/// </summary>
public class DcPowerFlowBatchTests
{
    private static void SameBits(double[] a, double[] b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(a[i]) == BitConverter.DoubleToInt64Bits(b[i]), $"{what} [{i}]: {a[i]:R} vs {b[i]:R}");
    }

    private static void SameResult(DcPowerFlowResult single, DcPowerFlowResult batched, string what)
    {
        Assert.Equal(single.BusIds, batched.BusIds);
        SameBits(single.VaDeg, batched.VaDeg, what + " Va");
        SameBits(single.PInjMw, batched.PInjMw, what + " Pinj");
        SameBits(single.FlowMw, batched.FlowMw, what + " flow");
        SameBits(single.LoadingPercent, batched.LoadingPercent, what + " loading");
        Assert.Equal(single.Islands.Count, batched.Islands.Count);
        for (int k = 0; k < single.Islands.Count; k++)
        {
            DcIslandBalance a = single.Islands[k], b = batched.Islands[k];
            Assert.Equal(a.ReferenceBusId, b.ReferenceBusId);
            Assert.Equal(a.BusCount, b.BusCount);
            Assert.Equal(a.HasGeneration, b.HasGeneration);
            SameBits(new[] { a.LoadMw, a.ScheduledGenerationMw, a.ReferenceInjectionMw },
                new[] { b.LoadMw, b.ScheduledGenerationMw, b.ReferenceInjectionMw }, what + $" island {k}");
        }
    }

    /// <summary>Monte Carlo samples of <paramref name="m"/>: every in-service branch reactance scaled by a deterministic
    /// factor in [0.9, 1.1] (sign kept) and every bus demand by one in [0.95, 1.05].</summary>
    internal static GridModel[] Samples(GridModel m, int count, ulong seed)
    {
        ulong s = seed;
        double U() { s = s * 6364136223846793005UL + 1442695040888963407UL; return (s >> 11) * (1.0 / (1UL << 53)); }
        var result = new GridModel[count];
        for (int c = 0; c < count; c++)
        {
            Branch[] branches = m.Branches.Select(b => b.X == 0.0 ? b : b with { X = b.X * (0.9 + 0.2 * U()) }).ToArray();
            Bus[] buses = m.Buses.Select(b => b with { Pd = b.Pd * (0.95 + 0.1 * U()) }).ToArray();
            result[c] = m.CopyWith(buses: buses, branches: branches);
        }
        return result;
    }

    [Theory]
    [InlineData("case118", 10)]
    [InlineData("case300", 7)]
    [InlineData("case300", 1)]
    public void Every_batched_result_holds_the_bits_of_its_single_solve(string name, int count)
    {
        GridModel m = name == "case118" ? IeeeCases.Case118() : IeeeCases.Case300();
        GridModel[] samples = Samples(m, count, 41UL);
        IReadOnlyList<DcPowerFlowResult> parallel = DcPowerFlow.SolveBatch(samples);
        IReadOnlyList<DcPowerFlowResult> serial = DcPowerFlow.SolveBatch(samples, maxDegreeOfParallelism: 1);
        Assert.Equal(count, parallel.Count);
        for (int i = 0; i < count; i++)
        {
            DcPowerFlowResult single = DcPowerFlow.Solve(samples[i]);
            SameResult(single, parallel[i], $"{name} sample {i}");
            SameResult(single, serial[i], $"{name} sample {i} (one thread)");
        }
    }

    [Fact]
    public void Models_of_different_structures_form_their_own_families_and_still_match_the_single_solve()
    {
        // Samples 2 and 5 lose the same branch (another B' structure; they form a family of two), sample 4 loses a
        // different branch (a family of one); the rest share one family.
        GridModel m = IeeeCases.Case118();
        GridModel[] samples = Samples(m, 8, 5UL);
        int cutA = m.Branches.Select((b, i) => (b, i)).First(t => t.b.InService && t.b.From != t.b.To && t.i > 10).i;
        int cutB = m.Branches.Select((b, i) => (b, i)).First(t => t.b.InService && t.b.From != t.b.To && t.i > 40).i;
        GridModel Cut(GridModel g, int k) => g.CopyWith(branches: g.Branches.Select((b, i) => i == k ? b with { InService = false } : b).ToArray());
        samples[2] = Cut(samples[2], cutA);
        samples[5] = Cut(samples[5], cutA);
        samples[4] = Cut(samples[4], cutB);
        IReadOnlyList<DcPowerFlowResult> batched = DcPowerFlow.SolveBatch(samples);
        for (int i = 0; i < samples.Length; i++)
            SameResult(DcPowerFlow.Solve(samples[i]), batched[i], $"sample {i}");
    }

    [Fact]
    public void Gb_full_monte_carlo_samples_share_the_lanes_and_hold_their_single_solve_bits()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(CliProcess.Root(), "data", "gb-full"));
        GridModel[] samples = Samples(m, 9, 2026UL);   // two full batches and one of a single lane

        // Under plain partial pivoting the reactance-varied B' matrices do not keep the template's pivot order (near-ties
        // between equal-magnitude candidates are broken by rounding), so at most the template's own lane shares; under the
        // opt-in diagonal preference every lane shares. Both are measured facts of gb-full that the summary of
        // DcPowerFlow.SolveBatch states.
        CsrMatrix[] first4 = samples.Take(4).Select(s => DistributionFactors.Prepare(s).BPrime).ToArray();
        Assert.True(new SparseLuFamily(first4[0]).Factor(first4).SharedLanes.Count(s => s) <= 1);
        Assert.All(new SparseLuFamily(first4[0], 0.1).Factor(first4).SharedLanes, s => Assert.True(s));

        IReadOnlyList<DcPowerFlowResult> batched = DcPowerFlow.SolveBatch(samples);
        IReadOnlyList<DcPowerFlowResult> optIn = DcPowerFlow.SolveBatch(samples, diagonalPreference: 0.1);
        for (int i = 0; i < samples.Length; i++)
        {
            DcPowerFlowResult single = DcPowerFlow.Solve(samples[i]);
            SameResult(single, batched[i], $"gb-full sample {i}");
            DcPowerFlowResult singleOptIn = DcPowerFlow.Solve(samples[i], 0.1);
            SameResult(singleOptIn, optIn[i], $"gb-full sample {i} (diagonal preference)");
            // The opt-in rule agrees with the default to rounding (flows in MW).
            for (int l = 0; l < single.FlowMw.Length; l++)
                Assert.True(Math.Abs(single.FlowMw[l] - singleOptIn.FlowMw[l]) <= 1e-6, $"flow {l}: {single.FlowMw[l]:R} vs {singleOptIn.FlowMw[l]:R}");
        }
    }

    [Fact]
    public void A_model_the_dc_model_cannot_represent_fails_the_batch_naming_its_position()
    {
        GridModel m = IeeeCases.Case118();
        GridModel[] samples = Samples(m, 6, 9UL);
        // Model 3: an in-service zero-reactance branch inside the mesh, which the single solve refuses.
        int k = m.Branches.Select((b, i) => (b, i)).First(t => t.b.InService && t.i > 20).i;
        samples[3] = samples[3].CopyWith(branches: samples[3].Branches.Select((b, i) => i == k ? b with { X = 0.0 } : b).ToArray());
        var single = Assert.Throws<InvalidOperationException>(() => DcPowerFlow.Solve(samples[3]));
        var batch = Assert.Throws<InvalidOperationException>(() => DcPowerFlow.SolveBatch(samples));
        Assert.Contains("model 3", batch.Message, StringComparison.Ordinal);
        Assert.NotNull(batch.InnerException);
        Assert.Equal(single.Message, batch.InnerException!.Message);
    }

    [Theory]
    [InlineData("case118")]
    [InlineData("case300")]
    public void The_opt_in_rule_at_zero_is_the_default_solve_and_its_batches_match_its_single_solves(string name)
    {
        GridModel m = name == "case118" ? IeeeCases.Case118() : IeeeCases.Case300();
        GridModel[] samples = Samples(m, 6, 17UL);
        IReadOnlyList<DcPowerFlowResult> optIn = DcPowerFlow.SolveBatch(samples, diagonalPreference: 0.1);
        for (int i = 0; i < samples.Length; i++)
        {
            SameResult(DcPowerFlow.Solve(samples[i]), DcPowerFlow.Solve(samples[i], 0.0), $"{name} sample {i} at 0");
            SameResult(DcPowerFlow.Solve(samples[i], 0.1), optIn[i], $"{name} sample {i} opt-in");
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => DcPowerFlow.Solve(m, -1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DcPowerFlow.SolveBatch(samples, diagonalPreference: 1.5));
    }

    [Fact]
    public void Malformed_batches_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => DcPowerFlow.SolveBatch(null!));
        Assert.Throws<ArgumentException>(() => DcPowerFlow.SolveBatch(new GridModel[] { IeeeCases.Case9(), null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DcPowerFlow.SolveBatch(new[] { IeeeCases.Case9() }, maxDegreeOfParallelism: 0));
        Assert.Empty(DcPowerFlow.SolveBatch(Array.Empty<GridModel>()));
    }
}
