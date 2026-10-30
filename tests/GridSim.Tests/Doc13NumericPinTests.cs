using System.Numerics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-13 (October 2026): exact pins for stream 13's numeric and model-copy items. Every test is new; no existing test
/// is edited.
/// <list type="bullet">
/// <item>D-DOC-208: <c>ComplexCsrBuilderPinTests.Fundamental_Y_h_build_is_unchanged_by_the_sort_port</c> says
/// "bit-for-bit" in its summary but asserts 1e-9 (and its inline comment says it is not a bit-for-bit claim). The exact
/// claim is made here, beside it: Y(h = 1) equals the dense Y-bus cell for cell, every bit, on several IEEE cases (the
/// two builds visit the branches, then the shunts, in the same order, and the builder's sort is stable).</item>
/// <item>D-DOC-206: the protection cascade's post-trip copy and the inertia transforms keep the out-of-service units
/// (they listed the in-service fleet only), while the engines still read the very same in-service list.</item>
/// </list>
/// </summary>
public sealed class Doc13NumericPinTests
{
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "case9" }, new object[] { "case14" }, new object[] { "case30" },
        new object[] { "case118" }, new object[] { "case300" },
    };

    private static GridModel Case(string name) => name switch
    {
        "case9" => IeeeCases.Case9(),
        "case14" => IeeeCases.Case14(),
        "case30" => IeeeCases.Case30(),
        "case118" => IeeeCases.Case118(),
        "case300" => IeeeCases.Case300(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void D208_Y_of_h_1_equals_the_dense_Y_bus_bit_for_bit(string name)
    {
        GridModel model = Case(name);
        YBus dense = YBus.Build(model);
        ComplexCsr y1 = GridSim.Core.Harmonics.FrequencyDependentYBus.Build(model, 1.0, null);
        Assert.Equal(model.BusCount, y1.N);

        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < y1.N; i++)
            for (int p = y1.RowPtr[i]; p < y1.RowPtr[i + 1]; p++)
            {
                int j = y1.ColIdx[p];
                seen.Add((i, j));
                Complex d = dense.Y[i, j];
                Assert.True(BitConverter.DoubleToInt64Bits(y1.G[p]) == BitConverter.DoubleToInt64Bits(d.Real)
                         && BitConverter.DoubleToInt64Bits(y1.B[p]) == BitConverter.DoubleToInt64Bits(d.Imaginary),
                    $"{name} Y(1)[{i},{j}] = {y1.G[p]:R} + {y1.B[p]:R}i, dense {d.Real:R} + {d.Imaginary:R}i");
            }
        // And no dense non-zero is missing from the sparse pattern.
        for (int i = 0; i < model.BusCount; i++)
            for (int j = 0; j < model.BusCount; j++)
                if (dense.Y[i, j] != Complex.Zero)
                    Assert.True(seen.Contains((i, j)), $"{name}: dense Y[{i},{j}] = {dense.Y[i, j]} has no cell in Y(1)");
    }

    // ---- D-DOC-206 -------------------------------------------------------------------------------------------------

    /// <summary>IEEE 9 with one extra, out-of-service unit on bus 5 (kept in the model, never solved).</summary>
    private static GridModel Case9WithSpare()
    {
        GridModel m = IeeeCases.Case9();
        GridModel withOos = m.CopyWith(generators: m.Generators.Concat(new[] { new Generator(5, Pg: 0, H: 3, Name: "spare", InService: false) }).ToList());
        Assert.Single(withOos.OutOfServiceGenerators);
        return withOos;
    }

    [Fact]
    public void D206_the_inertia_transforms_keep_the_out_of_service_unit_and_solve_the_same_list()
    {
        GridModel m = Case9WithSpare();
        GridModel scaled = GridInertiaTransforms.ScaleSynchronousInertia(m, 0.5);
        Assert.Contains(scaled.AllGenerators, g => g.Name == "spare" && !g.InService);
        Assert.Equal(m.Generators.Count, scaled.Generators.Count);
        Assert.DoesNotContain(scaled.Generators, g => g.Name == "spare");
        // The out-of-service unit keeps its own data (the transform acts on the in-service fleet, as before).
        Assert.Equal(3.0, scaled.AllGenerators.Single(g => g.Name == "spare").H);
        for (int i = 0; i < m.Generators.Count; i++)
            Assert.Equal(m.Generators[i].H * 0.5, scaled.Generators[i].H);
    }

    [Fact]
    public void D206_the_inertia_copy_is_bit_identical_to_the_hand_written_copy_for_the_engines()
    {
        // With no out-of-service unit the copy's in-service list is the very list given (CopyWith keeps the reference).
        GridModel m = IeeeCases.Case9();
        var gens = m.Generators.Select(g => g with { H = g.H * 0.25 }).ToList();
        GridModel c = GridInertiaTransforms.With(m, gens);
        Assert.Same(gens, c.Generators);
        Assert.Equal(m.BaseMva, c.BaseMva);
        Assert.Same(m.Branches, c.Branches);
        Assert.Same(m.Buses, c.Buses);
    }

    [Fact]
    public void D206_the_cascade_source_copies_through_CopyWith()
    {
        // WithBranches is private; its body is the contract (the post-trip model must carry the whole fleet).
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Core", "Protection", "ProtectionCascade.cs");
        Assert.Contains("private static GridModel WithBranches(GridModel m, IReadOnlyList<Branch> branches) => m.CopyWith(branches: branches);", src, StringComparison.Ordinal);
        string tr = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Core", "Simulation", "GridInertiaTransforms.cs");
        Assert.Contains("internal static GridModel With(GridModel m, IReadOnlyList<Generator> generators) => m.CopyWith(generators: generators);", tr, StringComparison.Ordinal);
        Assert.DoesNotContain("Generators = m.Generators", src, StringComparison.Ordinal);
    }

    [Fact]
    public void D206_CopyWith_branches_keeps_the_out_of_service_unit_and_the_in_service_reference()
    {
        GridModel m = Case9WithSpare();
        GridModel c = m.CopyWith(branches: m.Branches.Skip(1).ToList());
        Assert.Same(m.Generators, c.Generators);
        Assert.Contains(c.AllGenerators, g => g.Name == "spare");
    }
}
