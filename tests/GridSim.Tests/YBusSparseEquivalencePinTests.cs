using System.Numerics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the comment-only equivalence claim that <see cref="YBus.BuildSparse"/> produces cells that are
/// <b>bit-for-bit</b> the dense <see cref="YBus.Build"/> values (same stamps, same accumulation order).
/// The claim is load-bearing for the fault-study sparse-injection path (which walks the sparse cells in
/// place of the dense O(n^2) loop) and for the estimator's measurement model; a reordering of either loop
/// would break it silently. This turns that silent risk into a loud failure.
/// </summary>
public class YBusSparseEquivalencePinTests
{
    // Cases chosen for coverage: transformers/taps + phase context (118, 300), bus shunts (30), and the
    // small dense reference (9). Between them they exercise taps, shunts, and multiple branches per bus.
    public static TheoryData<string> Cases => new() { "case9", "case30", "case118", "case300" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BuildSparse_cells_are_bit_for_bit_the_dense_build(string caseName)
    {
        GridModel model = Load(caseName);
        YBus dense = YBus.Build(model);
        SparseYBus sparse = YBus.BuildSparse(model);
        int n = dense.N;

        Assert.Equal(n, sparse.N);

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                Complex d = dense.Y[i, j];
                if (sparse.Cells.TryGetValue((i, j), out Complex s))
                {
                    // Present in the sparse form: must equal the dense cell to the last bit.
                    Assert.Equal(Bits(d.Real), Bits(s.Real));
                    Assert.Equal(Bits(d.Imaginary), Bits(s.Imaginary));
                }
                else
                {
                    // Absent from the sparse form: the dense cell must be exactly zero (no lost mass).
                    Assert.True(d.Real == 0.0 && d.Imaginary == 0.0,
                        $"[{i},{j}] absent from sparse but dense = {d}");
                }
            }
        }
    }

    private static GridModel Load(string caseName) => caseName switch
    {
        "case9" => IeeeCases.Case9(),
        "case30" => IeeeCases.Case30(),
        "case118" => IeeeCases.Case118(),
        "case300" => IeeeCases.Case300(),
        _ => throw new System.ArgumentOutOfRangeException(nameof(caseName), caseName, "unknown case"),
    };

    private static long Bits(double v) => System.BitConverter.DoubleToInt64Bits(v);
}
