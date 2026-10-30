using System.Text;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// N299 (October 2026, LF5; Ruling R1): the JSON v2 case writer (<see cref="V2CaseWriter"/>), so <c>gridsim convert</c> can
/// round-trip a v2 case with its N234 fault sequence data. Pinned: every bundled case's v2 projection, loaded, written and
/// loaded again, gives the same model in every field (bar the bundles the writer reports as not written) and the same
/// solve to the bit; the sequence-data case keeps every sequence field; the output is byte-stable (writing the loaded-back
/// model again gives the same bytes, "\n" line ends, ASCII); the transformer split rule; the refusals; and the verb.
/// </summary>
public sealed partial class V2CaseLoaderTests
{
    /// <summary>The model the writer promises to give back, as a fresh model (so no cache the write may have filled in
    /// <paramref name="m"/> is compared): every part of <paramref name="m"/>, the asset-dynamics and protection bundles and
    /// the element classes included (N321).</summary>
    /// <remarks>Moved 2026-10 (LF7 N321, Ruling R1: Mark delegated EE / product calls 2026-10-04; ruled by main: "the v2
    /// writer writes templates and bindings"): this was WithoutBundles, the model less its asset-dynamics and protection
    /// bundles and every element's DynamicsClass, because the writer left them out (with a note). The writer now writes
    /// them as local templates, bindings, element templates and placed devices, so the promise is the whole model.
    /// Before: back compared with m less AssetDynamics, Protections and every DynamicsClass. After: with all of m.</remarks>
    private static GridModel WithBundles(GridModel m) => new()
    {
        Name = m.Name,
        BaseMva = m.BaseMva,
        FrequencyHz = m.FrequencyHz,
        Buses = m.Buses,
        Generators = m.AllGenerators.ToList(),
        Branches = m.Branches.ToList(),
        Oltcs = m.Oltcs,
        Boundaries = m.Boundaries,
        AssetDynamics = m.AssetDynamics,
        Protections = m.Protections,
    };

    private static (byte[] Manifest, byte[] Network) WrittenBytes(V2CaseWriteResult w) =>
        (File.ReadAllBytes(w.ManifestPath), File.ReadAllBytes(w.NetworkPath));

    [Theory]
    [MemberData(nameof(BundledCases))]
    public void N299_every_bundled_v2_case_round_trips_through_the_writer_bit_for_bit(string relativeDir)
    {
        using var temp = new TempFolder();
        string projected = Path.Combine(temp.Path, "projected");
        Directory.CreateDirectory(projected);
        string manifest = WriteProjection(Path.Combine(DataRoot, relativeDir), projected);
        GridModel original = V2CaseLoader.Load(manifest, SchemasDir, DataRoot);

        V2CaseWriteResult first = V2CaseWriter.Write(original, Path.Combine(temp.Path, "one", "case.case.json"));
        GridModel back = V2CaseLoader.Load(first.ManifestPath, SchemasDir);
        List<string> diffs = DeepDifferences(WithBundles(original), back);
        Assert.True(diffs.Count == 0, $"data/{relativeDir}: the written v2 case loads to a different model:{Environment.NewLine}{string.Join(Environment.NewLine, diffs)}");
        Assert.Null(V2CaseWriter.FirstSequenceDifference(original, back));
        AssertSameSolve(SolverSelector.CreateNewton(original).Solve(original), SolverSelector.CreateNewton(back).Solve(back), relativeDir);

        // Byte-stable: the loaded-back model writes exactly the same bytes.
        V2CaseWriteResult second = V2CaseWriter.Write(back, Path.Combine(temp.Path, "two", "case.case.json"));
        (byte[] m1, byte[] n1) = WrittenBytes(first);
        (byte[] m2, byte[] n2) = WrittenBytes(second);
        Assert.Equal(m1, m2);
        Assert.Equal(n1, n2);
        Assert.DoesNotContain((byte)'\r', n1);
        Assert.True(n1.All(b => b < 0x80), "the network file is plain ASCII");
        Assert.Equal((byte)'\n', n1[^1]);

        // Moved 2026-10 (LF7 N321, Ruling R1; see WithBundles): every bundled case is now written whole, bundles included,
        // so no bundled case leaves a note. Before: a case with an asset-dynamics bundle (gb-full, gb-synthetic) noted
        // "the authored asset-dynamics bundle ... is not written", one with protections (gb-synthetic) "the protection
        // bundle ... is not written", and the element template references were counted as not written; only a case with
        // none of them was required to give no note. After: no note on any bundled case, and the bundles read back equal
        // (DeepDifferences above includes them), with each class, rule and device where the model had it.
        Assert.Empty(first.Notes);
        if (original.AssetDynamics is not null)
        {
            Assert.NotNull(back.AssetDynamics);
            Assert.Equal(original.AssetDynamics.Library.Classes.Select(c => c.Name), back.AssetDynamics!.Library.Classes.Select(c => c.Name));
            Assert.Equal(original.AssetDynamics.Bindings.Rules, back.AssetDynamics.Bindings.Rules);
        }
        if (original.Protections is not null) Assert.Equal(original.Protections.Instances, back.Protections!.Instances);
    }

    [Fact]
    public void N299_a_case_with_fault_sequence_data_round_trips_every_sequence_field()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        GridModel original = f.Load().Model;
        using var temp = new TempFolder();
        V2CaseWriteResult w = V2CaseWriter.Write(original, Path.Combine(temp.Path, "earth.case.json"));
        Assert.Empty(w.Notes);
        Assert.Equal(Path.Combine(temp.Path, "earth.network.json"), w.NetworkPath);
        string network = Encoding.UTF8.GetString(File.ReadAllBytes(w.NetworkPath));
        Assert.Contains("\"transformers\"", network, StringComparison.Ordinal);
        Assert.Contains("\"windings\"", network, StringComparison.Ordinal);
        Assert.Contains("\"subtransientXdPu\": 0.18", network, StringComparison.Ordinal);
        Assert.Contains("\"negativeSeqXPu\": 0.22", network, StringComparison.Ordinal);
        Assert.Contains("\"neutralXPu\"", network, StringComparison.Ordinal);

        GridModel back = V2CaseLoader.Load(w.ManifestPath, SchemasDir);
        Assert.Null(V2CaseWriter.FirstSequenceDifference(original, back));
        List<string> diffs = DeepDifferences(original, back);
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
        Branch t = back.Branches.Single(b => b.Name == "T");
        Assert.Equal(WindingConnection.Delta, t.FromWinding);
        Assert.Equal(WindingConnection.WyeGrounded, t.ToWinding);
        Assert.Equal(0.001, t.R0);
        Assert.Equal(0.045, t.X0);
        Assert.Equal(0.01, t.ToNeutralXPu);
        Assert.Equal(0.0, t.FromNeutralXPu);
    }

    [Fact]
    public void N299_the_sequence_comparison_names_a_difference()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        GridModel m = f.Load().Model;
        Assert.Null(V2CaseWriter.FirstSequenceDifference(m, m));
        GridModel noX2 = m.CopyWith(generators: m.AllGenerators.Select(g => g with { NegativeSeqXPu = 0 }).ToList());
        Assert.Equal("generator 0 X2 0.22 vs 0", V2CaseWriter.FirstSequenceDifference(m, noX2));
        GridModel noWindings = m.CopyWith(branches: m.Branches.Select(b => b with { FromWinding = null, ToWinding = null }).ToList());
        Assert.StartsWith("branch 0 (1-2) windings Delta/WyeGrounded vs none/none", V2CaseWriter.FirstSequenceDifference(m, noWindings));
    }

    [Fact]
    public void N299_the_transformer_split_keeps_branch_order()
    {
        var line = new Branch(1, 2, 0.01, 0.1, B: 0.02, Name: "L");
        var tx = new Branch(1, 2, 0.01, 0.1, Name: "T", IsTransformer: true);
        var txW = tx with { Name = "TW", FromWinding = WindingConnection.Delta, ToWinding = WindingConnection.WyeGrounded, X0 = 0.09, ToNeutralXPu = 0.01 };
        var txR0Only = tx with { Name = "TR", R0 = 0.01, X0 = 0.08 };   // zero-sequence data without windings: a line in v2

        Assert.Equal(0, V2CaseWriter.TransformerSplit(Array.Empty<Branch>(), out int lost0));
        Assert.Equal(0, lost0);
        Assert.Equal(3, V2CaseWriter.TransformerSplit(new[] { line, tx, tx }, out int lost1));   // nothing needs one
        Assert.Equal(0, lost1);
        Assert.Equal(1, V2CaseWriter.TransformerSplit(new[] { line, txW, tx }, out int lost2));  // from the first that needs one
        Assert.Equal(0, lost2);
        Assert.Equal(2, V2CaseWriter.TransformerSplit(new[] { txW, line }, out int lost3));      // a line after it: lost
        Assert.Equal(1, lost3);
        Assert.Equal(2, V2CaseWriter.TransformerSplit(new[] { line, txR0Only, txW }, out int lost4));
        Assert.Equal(0, lost4);
        Assert.Equal(1, V2CaseWriter.TransformerSplit(new[] { txW, txW with { Name = "TW2" } }.Prepend(line).ToArray(), out int lost5));
        Assert.Equal(0, lost5);
        // A neutral reactance on a side that is not WyeGrounded cannot be a v2 transformer element.
        var badNeutral = txW with { FromNeutralXPu = 0.02 };
        Assert.Equal(1, V2CaseWriter.TransformerSplit(new[] { badNeutral }, out int lost6));
        Assert.Equal(1, lost6);
    }

    [Fact]
    public void N299_winding_data_that_cannot_keep_its_place_is_reported_and_the_rest_written()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        GridModel m = f.Load().Model;
        // The transformer first, then a line: the transformer cannot be a v2 transformer element without moving after it.
        var line = new Branch(1, 2, 0.01, 0.1, B: 0.02, Name: "L");
        GridModel mixed = m.CopyWith(branches: m.Branches.Append(line).ToList());
        using var temp = new TempFolder();
        V2CaseWriteResult w = V2CaseWriter.Write(mixed, Path.Combine(temp.Path, "mixed.case.json"));
        Assert.Contains(w.Notes, n => n.StartsWith("1 branch(es) carry transformer winding data that cannot be written without reordering", StringComparison.Ordinal));
        GridModel back = V2CaseLoader.Load(w.ManifestPath, SchemasDir);
        Assert.Equal("branch 0 (1-2) windings Delta/WyeGrounded vs none/none", V2CaseWriter.FirstSequenceDifference(mixed, back));
        AssertSameSolve(SolverSelector.CreateNewton(mixed).Solve(mixed), SolverSelector.CreateNewton(back).Solve(back), "mixed");
    }

    [Fact]
    public void N299_the_writer_refuses_what_v2_cannot_hold_and_never_overwrites()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        GridModel m = f.Load().Model;
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "a.case.json");
        V2CaseWriter.Write(m, path);
        Assert.Throws<IOException>(() => V2CaseWriter.Write(m, path));
        File.Delete(path);   // the network file is still there
        Assert.Throws<IOException>(() => V2CaseWriter.Write(m, path));

        GridModel noKv = m.CopyWith(buses: m.Buses.Select(b => b.Id == 2 ? b with { BaseKv = 0 } : b).ToList());
        Assert.Contains("bus 2 baseKv is 0", Assert.Throws<InvalidDataException>(() => V2CaseWriter.Write(noKv, Path.Combine(temp.Path, "b.case.json"))).Message, StringComparison.Ordinal);
        GridModel nan = m.CopyWith(generators: m.AllGenerators.Select(g => g with { Pg = double.NaN }).ToList());
        Assert.Contains("Pg is NaN", Assert.Throws<InvalidDataException>(() => V2CaseWriter.Write(nan, Path.Combine(temp.Path, "c.case.json"))).Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(temp.Path, "c.case.json")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "c.network.json")));
    }

    [Theory]
    [InlineData("uk.case.json", "uk.network.json")]
    [InlineData("x.json", "x.network.json")]
    [InlineData("dir/My.Case.JSON", "My.network.json")]
    public void N299_network_file_name(string manifest, string network) => Assert.Equal(network, V2CaseWriter.NetworkFileNameFor(manifest));

    // ---- The verb ----------------------------------------------------------------------------------------------------

    [Fact]
    public void N299_convert_round_trips_a_v2_case_with_sequence_data_and_is_byte_stable()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        using var temp = new TempFolder();
        // Two folders, one file name: the manifest names its network file, so the same name gives the same bytes.
        string one = Path.Combine(temp.Path, "a", "earth.case.json"), two = Path.Combine(temp.Path, "b", "earth.case.json");
        CliProcess.Result r = CliProcess.RunBuilt("convert", f.ManifestPath, one);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("round trip: the written case solves bit-identically", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("round trip: the fault sequence data of 1 generator(s) and 1 branch(es) read back bit for bit", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("note:", r.StdOut, StringComparison.Ordinal);

        // v2 -> v2 again: the same bytes.
        CliProcess.Result again = CliProcess.RunBuilt("convert", one, two);
        Assert.True(again.ExitCode == 0, again.ToString());
        Assert.Equal(File.ReadAllBytes(Path.Combine(temp.Path, "a", "earth.network.json")), File.ReadAllBytes(Path.Combine(temp.Path, "b", "earth.network.json")));
        Assert.Equal(File.ReadAllBytes(one), File.ReadAllBytes(two));

        // To v1, the sequence data is still reported as lost (v1 cannot hold it).
        CliProcess.Result v1 = CliProcess.RunBuilt("convert", one, Path.Combine(temp.Path, "v1"));
        Assert.True(v1.ExitCode == 0, v1.ToString());
        Assert.Contains("which JSON v1 cannot hold", v1.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void N299_convert_to_v2_from_a_bundled_folder_and_its_refusals()
    {
        using var temp = new TempFolder();
        string outPath = Path.Combine(temp.Path, "uk.case.json");
        CliProcess.Result r = CliProcess.RunBuilt("convert", Path.Combine(DataRoot, "uk"), outPath);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("round trip: the written case solves bit-identically", r.StdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(temp.Path, "uk.network.json")));

        // Never overwrites: the manifest exists (exit 2); only the network file exists (exit 2).
        CliProcess.Result again = CliProcess.RunBuilt("convert", Path.Combine(DataRoot, "uk"), outPath);
        Assert.Equal(2, again.ExitCode);
        File.Delete(outPath);
        CliProcess.Result netOnly = CliProcess.RunBuilt("convert", Path.Combine(DataRoot, "uk"), outPath);
        Assert.Equal(2, netOnly.ExitCode);
        Assert.Contains("the network file of the v2 case", netOnly.StdErr, StringComparison.Ordinal);
    }
}
