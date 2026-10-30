using System.Numerics;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N186 (October 2026): participation on gb-full took about 1.9 h because <see cref="GeneralEigen.Decompose"/> runs one
/// dense complex LU per eigenvalue. The speed-up (a cache-blocked, vectorised LU that is bit-identical to the old one,
/// and the per-eigenvalue inverse iterations run in parallel) must not move a single bit of any eigenvalue,
/// eigenvector, left vector or participation factor.
///
/// The digests below were produced by <see cref="N186ParticipationFixtures.Digest"/> linked against the 1.95.133
/// GridSim.Core (before the change), and were the same in normal mode, with DOTNET_EnableAVX2=0 and with
/// DOTNET_EnableHWIntrinsic=0. The test run in each mode therefore proves the new code gives the old bits in that mode.
/// </summary>
public sealed class N186ParticipationBitIdentityTests
{
    public static TheoryData<string, string> Pinned => new()
    {
        { "case9-undamped", "FA2DB1C2E8CB990BAF401C8791B73EFFE7CA9BC7C23693DCCBCDD0B4E5FDC5EF" },
        { "case9-damped", "30DF0E8A431E57D0A72A845172EF2A653CCA27B51139771A002C9ADE8ADD4F45" },
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the six case
        // digests below moved because the case state matrices are built from a power flow and machine angles that now use
        // GridSim's deterministic fdlibm sin / cos / atan2 instead of the Windows C runtime (the eigen-solver is unchanged: the
        // case9, random and repeated-block digests did not move). Before -> after: case14-undamped 2248411F... -> B9B0A716...,
        // case14-damped ABA613BF... -> 8966E035..., case118-undamped 71797CF3... -> 13BAB072..., case118-damped 76C0E80F... ->
        // 75531BF4..., case300-undamped 783070F3... -> B2A7966A..., case300-damped 4FAF2019... -> 2F1F11A4...
        // (hashes abbreviated).
        { "case14-undamped", "B9B0A71644EE61A2D58587971760909CEAC626F42DC6FCA576EE273034F442F4" },
        { "case14-damped", "8966E035A3CCF762266550F85D6727471A8864D447A84EAC7BC9D1426F4B1DFE" },
        { "case118-undamped", "13BAB072064A29C9979A5D26B35A25D51A3A4AF4BB73BC7BA4778AF392CEE82A" },
        { "case118-damped", "75531BF4198978DF0652A9506DA5543CDD5B581AE74E66AEAFD40B05FE0BC5CB" },
        { "case300-undamped", "B2A7966A4E1E0D3C862A18ADB1A46E7D934F02A3B0ADA4004BE6A9E939528AB2" },
        { "case300-damped", "2F1F11A48069CA50425BEF13BD6ACFB73C96ED490BF330352F3817D17A3136F3" },
        { "random-6", "C64DF06E57EE5F5C198F2CA555BF355DE0C801044602575172D92564460046FF" },
        { "random-23", "C5E4732422E9D71366ABAA0A558F5F34183DFB37E98C0182B0E23AB4DDF27E9B" },
        { "random-64", "DC087892BC34C03020AEBD945501EEA3174C7ABA88D06F2C068CDE317FAB6C5D" },
        { "random-130", "80FE19CCF1AE509779EFF17C4EF601056D03DF614BD2F6E36395D3001970F05C" },
        { "repeated-blocks-12", "17D23BB650601B31117B808806F7FD772D6C97CD6E211CCBD251DD6AAA97EDE0" },
    };

    [Theory]
    [MemberData(nameof(Pinned))]
    public void Every_eigenpair_and_participation_factor_is_bit_identical_to_1_95_133(string fixture, string digest) =>
        Assert.Equal(digest, N186ParticipationFixtures.Digest(fixture));

    [Fact]
    public void Every_pinned_fixture_is_listed_once()
    {
        var pinned = Pinned.Select(row => (string)row[0]).ToArray();
        Assert.Equal(N186ParticipationFixtures.Names, pinned);
    }

    [Fact]
    public void The_case_fixtures_really_carry_participation()
    {
        // A digest of all-zero participation would pin nothing useful: every case fixture must decompose.
        foreach (string name in N186ParticipationFixtures.Names.Where(n => n.StartsWith("case", StringComparison.Ordinal)))
        {
            var r = GridSim.Core.Dynamics.SmallSignal.ModalAnalysis.Analyze(N186ParticipationFixtures.CaseModel(name));
            Assert.False(r.ParticipationUnavailable, name);
            Assert.Contains(r.Modes, m => m.ParticipationByState.Any(p => p > 0.0));
        }
    }

    private static void AssertSamePairs(IReadOnlyList<GeneralEigen.Eigenpair> x, IReadOnlyList<GeneralEigen.Eigenpair> y)
    {
        Assert.Equal(x.Count, y.Count);
        static (long, long) B(Complex z) => (BitConverter.DoubleToInt64Bits(z.Real), BitConverter.DoubleToInt64Bits(z.Imaginary));
        for (int k = 0; k < x.Count; k++)
        {
            Assert.Equal(B(x[k].Value), B(y[k].Value));
            Assert.Equal(x[k].Right.Select(B), y[k].Right.Select(B));
            Assert.Equal(x[k].Left.Select(B), y[k].Left.Select(B));
        }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(130)]
    public void One_worker_and_many_workers_give_the_same_bits(int n)
    {
        double[,] a = N186ParticipationFixtures.RandomMatrix(n);
        var one = GeneralEigen.DecomposeWith(a, GeneralEigen.DefaultInverseIterations, GeneralEigen.DefaultBiorthonormalityTolerance, 1);
        var four = GeneralEigen.DecomposeWith(a, GeneralEigen.DefaultInverseIterations, GeneralEigen.DefaultBiorthonormalityTolerance, 4);
        var auto = GeneralEigen.Decompose(a);
        AssertSamePairs(one, four);
        AssertSamePairs(one, auto);
    }

    [Fact]
    public void The_repeated_eigenvalue_pass_is_unchanged_with_parallel_plain_iterations()
    {
        // 16 copies of one 3x3 block (order 48, above the parallel threshold): the R033 cluster deflation runs on
        // columns produced in parallel.
        double[,] b = { { -1.0, 2.0, 0.5 }, { -2.0, -0.5, 0.25 }, { 0.1, 0.3, -3.0 } };
        var a = new double[48, 48];
        for (int k = 0; k < 16; k++)
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) a[3 * k + i, 3 * k + j] = b[i, j];
        Assert.True(48 >= GeneralEigen.ParallelInverseIterationMinOrder);
        var one = GeneralEigen.DecomposeWith(a, GeneralEigen.DefaultInverseIterations, GeneralEigen.DefaultBiorthonormalityTolerance, 1);
        var many = GeneralEigen.DecomposeWith(a, GeneralEigen.DefaultInverseIterations, GeneralEigen.DefaultBiorthonormalityTolerance, 8);
        AssertSamePairs(one, many);
    }

    [Fact]
    public void The_worker_count_is_bounded_and_at_least_one()
    {
        Assert.Equal(1, GeneralEigen.InverseIterationWorkers(1902, 1));
        Assert.InRange(GeneralEigen.InverseIterationWorkers(1902, 0), 1, Environment.ProcessorCount);
        Assert.InRange(GeneralEigen.InverseIterationWorkers(3, 0), 1, 3);
        // An order whose two matrices exceed any machine's memory still gets one worker, never zero.
        Assert.Equal(1, GeneralEigen.InverseIterationWorkers(2_000_000, 0));
    }
}
