using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Distribution;
using GridSim.Core.IO.Json.Schema;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N339 (CF1, October 2026): the three-phase feeder gets a JSON v2 form, <c>gridsim-dist3ph/2</c>
/// (schemas/v2/dist3ph.schema.json; MultiPhaseJsonLoader.ParseV2 / LoadAny; MultiPhaseV2Writer). The v1 reader and
/// every v1 file are unchanged; these tests prove (a) every bundled v1 feeder written as v2 reads back to the same
/// model bit for bit and the writer is byte-stable, (b) LoadAny gives a v1 file exactly what Load gives, (c) the v2
/// rules: provenance required with the [VERIFY] rule, unknown members refused, the branch kind rules and the v1
/// network checks, and (d) the validate engine knows the tag.
/// </summary>
public sealed partial class Cf1OpenCodeFindingsTests
{
    private static readonly AssetClassProvenance N339Prov = new(ProvenanceTag.Recorded, "IEEE PES test feeder data, as the v1 file's description cites.");

    private static string N339Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    /// <summary>Every bundled v1 feeder file (not the measurement files), by file name, sorted.</summary>
    public static TheoryData<string> N339V1Feeders()
    {
        var data = new TheoryData<string>();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(N339Root(), "data", "dist3ph"), "*.json")
                     .Where(f => !f.EndsWith(".measurements.json", StringComparison.Ordinal))
                     .OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetFileName(f));
        return data;
    }

    private static string N339Feeder(string name) => Path.Combine(N339Root(), "data", "dist3ph", name);

    [Theory]
    [MemberData(nameof(N339V1Feeders))]
    public void N339_every_bundled_v1_feeder_written_as_v2_reads_back_bit_for_bit_and_rewrites_byte_for_byte(string name)
    {
        MultiPhaseFeeder v1 = MultiPhaseJsonLoader.Load(N339Feeder(name));
        string text = MultiPhaseV2Writer.ToJson(v1, N339Prov);
        Assert.True(MultiPhaseJsonLoader.IsV2Text(text));
        MultiPhaseFeeder v2 = MultiPhaseJsonLoader.ParseV2(text, name + ".v2.json");
        Assert.Null(MultiPhaseV2Writer.FirstDifference(v1, v2));
        Assert.Equal(N339Prov, v2.Provenance!.Default);
        Assert.Empty(v2.Provenance.ByElement);
        // Byte-stable: the loaded-back feeder carries the provenance, so it is written without a second one.
        Assert.Equal(text, MultiPhaseV2Writer.ToJson(v2));
        // ASCII, "\n" only, one final newline.
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        Assert.All(text, ch => Assert.True(ch < 0x7F, $"non-ASCII character U+{(int)ch:X4}"));
        // The v1 reader refuses the v2 text at the tag, as it always refused any other tag.
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(text, "x.json"));
        Assert.Contains("$.schema", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(N339V1Feeders))]
    public void N339_the_v1_and_v2_forms_solve_to_the_same_bits(string name)
    {
        MultiPhaseFeeder v1 = MultiPhaseJsonLoader.Load(N339Feeder(name));
        MultiPhaseFeeder v2 = MultiPhaseJsonLoader.ParseV2(MultiPhaseV2Writer.ToJson(v1, N339Prov), "v2.json");
        MultiPhaseResult a = new ThreePhaseSweep().Solve(v1.Model);
        MultiPhaseResult b = new ThreePhaseSweep().Solve(v2.Model);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.MaxDelta), BitConverter.DoubleToInt64Bits(b.MaxDelta));
        Assert.Equal(a.BusIds, b.BusIds);
        for (int i = 0; i < a.V.Length; i++)
        for (int p = 0; p < 3; p++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.V[i][p].Real), BitConverter.DoubleToInt64Bits(b.V[i][p].Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.V[i][p].Imaginary), BitConverter.DoubleToInt64Bits(b.V[i][p].Imaginary));
        }
    }

    [Theory]
    [MemberData(nameof(N339V1Feeders))]
    public void N339_load_any_gives_a_v1_file_exactly_what_load_gives(string name)
    {
        MultiPhaseFeeder viaLoad = MultiPhaseJsonLoader.Load(N339Feeder(name));
        MultiPhaseFeeder viaAny = MultiPhaseJsonLoader.LoadAny(N339Feeder(name));
        Assert.Null(MultiPhaseV2Writer.FirstDifference(viaLoad, viaAny));
        Assert.Null(viaAny.Provenance);
        Assert.Equal(viaLoad.SourceFile, viaAny.SourceFile);
    }

    [Fact]
    public void N339_load_any_reads_a_v2_file_and_sends_bad_json_and_other_tags_to_the_v1_reader_unchanged()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n339-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            MultiPhaseFeeder v1 = MultiPhaseJsonLoader.Load(N339Feeder("ieee4-gyGy-stepdown-unbalanced.json"));
            string v2Path = Path.Combine(dir, "f.v2.json");
            File.WriteAllText(v2Path, MultiPhaseV2Writer.ToJson(v1, N339Prov));
            Assert.Null(MultiPhaseV2Writer.FirstDifference(v1, MultiPhaseJsonLoader.LoadAny(v2Path)));

            // A wrong tag and bad JSON get the v1 reader's messages, word for word.
            string other = Path.Combine(dir, "other.json");
            File.WriteAllText(other, "{ \"schema\": \"gridsim-dist3ph/3\" }");
            Assert.Equal(Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Load(other)).Message,
                         Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.LoadAny(other)).Message);
            string bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{ \"schema\": ");
            Assert.Equal(Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Load(bad)).Message,
                         Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.LoadAny(bad)).Message);
            Assert.Throws<FileNotFoundException>(() => MultiPhaseJsonLoader.LoadAny(Path.Combine(dir, "missing.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- a small hand-written v2 feeder ----------------------------------------------------------------------------

    // Source 1 -> line 1-2 -> (closed switch 2-3) -> regulator 3-4 (phases ab) ... kept simple: a three-phase line,
    // a closed switch, a two-phase regulator and a two-phase line, a delta load, a capacitor and one open switch.
    private const string N339Doc = """
        {
          "schema": "gridsim-dist3ph/2",
          "name": "hand feeder",
          "description": "written for the N339 tests",
          "baseMva": 3,
          "provenance": { "_default": { "tag": "Synthetic", "note": "made up for a test" } },
          "buses": [
            { "id": 1, "baseKvLL": 4.16, "name": "SRC", "sourceVPu": { "re": [1, -0.5, -0.5], "im": [0, -0.8660254037844386, 0.8660254037844386] } },
            { "id": 2, "baseKvLL": 4.16 },
            { "id": 3, "baseKvLL": 4.16 },
            { "id": 4, "baseKvLL": 4.16 },
            { "id": 5, "baseKvLL": 4.16, "prov": { "tag": "CuratedPlaceholder", "note": "node [VERIFY]" } }
          ],
          "branches": [
            { "id": "L12", "kind": "line", "from": 1, "to": 2, "zPu": { "re": [[0.01, 0.002, 0.002], [0.002, 0.01, 0.002], [0.002, 0.002, 0.01]], "im": [[0.03, 0.01, 0.01], [0.01, 0.03, 0.01], [0.01, 0.01, 0.03]] }, "shuntYPu": { "re": [[0, 0, 0], [0, 0, 0], [0, 0, 0]], "im": [[0.001, 0, 0], [0, 0.001, 0], [0, 0, 0.001]] } },
            { "id": "S23", "kind": "switch", "from": 2, "to": 3, "zPu": { "re": [[0, 0, 0], [0, 0, 0], [0, 0, 0]], "im": [[0, 0, 0], [0, 0, 0], [0, 0, 0]] } },
            { "id": "R34", "kind": "regulator", "from": 3, "to": 4, "phases": "ab", "regulatorRatio": [1.025, 0.99375, 1], "zPu": { "re": [[0, 0, 0], [0, 0, 0], [0, 0, 0]], "im": [[0, 0, 0], [0, 0, 0], [0, 0, 0]] }, "prov": { "tag": "Recorded", "note": "tap log" } },
            { "id": "L45", "kind": "line", "from": 4, "to": 5, "phases": "ab", "zPu": { "re": [[0.02, 0.004, 0], [0.004, 0.02, 0], [0, 0, 0]], "im": [[0.05, 0.02, 0], [0.02, 0.05, 0], [0, 0, 0]] } }
          ],
          "openSwitches": ["TIE9"],
          "capacitors": [ { "id": "C2", "bus": 2, "qPu": [0.01, 0.01, 0.01] } ],
          "loads": [
            { "id": "D5", "bus": 5, "connection": "delta", "model": "constant-current", "sPu": { "re": [0.1, 0, 0], "im": [0.05, 0, 0] } },
            { "id": "Y2", "bus": 2, "connection": "wye", "model": "constant-power", "sPu": { "re": [0.1, 0.11, 0.12], "im": [0.02, 0.03, 0.04] } }
          ]
        }
        """;

    [Fact]
    public void N339_a_hand_written_v2_feeder_reads_to_the_expected_model_and_round_trips()
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.ParseV2(N339Doc, "hand.json");
        Assert.Equal("hand feeder", f.Name);
        Assert.Equal(3.0, f.BaseMva);
        Assert.Equal(new[] { "L12", "S23", "R34", "L45" }, f.BranchIds);
        Assert.Equal(new[] { "line", "switch", "regulator", "line" }, f.BranchKinds);
        Assert.Equal(new[] { "TIE9" }, f.OpenSwitchIds);
        Assert.Equal("SRC", f.BusLabel(1));
        PhaseBranch reg = f.Model.Branches[2];
        Assert.Equal(Phase.A | Phase.B, reg.Phases);
        Assert.Equal(new[] { 1.025, 0.99375, 1.0 }, reg.RegulatorRatio);
        Assert.NotNull(f.Model.Branches[0].ShuntY);
        Assert.Equal(new Complex(0.01, 0.03), f.Model.Branches[0].Z[0, 0]);
        Assert.Equal(new Complex(-0.5, -0.8660254037844386), f.Model.Buses[0].SourceV![1]);
        PhaseLoad d5 = f.Model.Loads[0];
        Assert.Equal(PhaseLoadConnection.Delta, d5.Connection);
        Assert.Equal(PhaseLoadModel.ConstantCurrent, d5.Model);
        Assert.False(f.Model.Loads[1].IsExtended);   // a constant-power wye load is the v1 record exactly
        Assert.Equal(new[] { 0.01, 0.01, 0.01 }, f.Model.Capacitors[0].QPerPhase);
        // Provenance: the default and the two element entries.
        Assert.Equal(new AssetClassProvenance(ProvenanceTag.Synthetic, "made up for a test"), f.Provenance!.Default);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, f.Provenance.For("bus:5").Tag);
        Assert.Equal(ProvenanceTag.Recorded, f.Provenance.For("branch:R34").Tag);
        Assert.Equal(ProvenanceTag.Synthetic, f.Provenance.For("branch:L12").Tag);
        Assert.Equal(2, f.Provenance.ByElement.Count);
        // The writer round-trips it (element provenance included).
        string text = MultiPhaseV2Writer.ToJson(f);
        MultiPhaseFeeder back = MultiPhaseJsonLoader.ParseV2(text, "back.json");
        Assert.Null(MultiPhaseV2Writer.FirstDifference(f, back));
        Assert.Equal(f.Provenance.ByElement.OrderBy(k => k.Key, StringComparer.Ordinal), back.Provenance!.ByElement.OrderBy(k => k.Key, StringComparer.Ordinal));
        Assert.Equal(text, MultiPhaseV2Writer.ToJson(back));
    }

    private static string N339Mutate(string from, string to)
    {
        Assert.Contains(from, N339Doc, StringComparison.Ordinal);   // the mutation must apply
        return N339Doc.Replace(from, to, StringComparison.Ordinal);
    }

    [Theory]
    // Schema-level (structural) refusals.
    [InlineData("\"provenance\": { \"_default\": { \"tag\": \"Synthetic\", \"note\": \"made up for a test\" } },", "", "provenance")]
    [InlineData("\"tag\": \"Synthetic\"", "\"tag\": \"synthetic\"", "/provenance/_default/tag")]
    [InlineData("\"note\": \"node [VERIFY]\"", "\"note\": \"node\"", "/buses/4/prov/note")]
    [InlineData("\"baseMva\": 3,", "\"baseMva\": 3, \"baseMVA\": 3,", "baseMVA")]
    [InlineData("\"kind\": \"switch\"", "\"kind\": \"breaker\"", "/branches/1/kind")]
    [InlineData("\"connection\": \"delta\"", "\"connection\": \"open-delta\"", "/loads/0/connection")]
    // Loader-level refusals (what the schema subset cannot say).
    [InlineData("\"qPu\": [0.01, 0.01, 0.01]", "\"qPu\": [0.01, 0.01, 0.01, 0.01]", "$.capacitors[0].qPu")]
    [InlineData("{ \"id\": 2, \"baseKvLL\": 4.16 },", "{ \"id\": 2, \"baseKvLL\": 4.16, \"sourceVPu\": { \"re\": [1, 1, 1], \"im\": [0, 0, 0] } },", "$.buses")]
    [InlineData("\"openSwitches\": [\"TIE9\"]", "\"openSwitches\": [\"S23\"]", "$.openSwitches[0]")]
    [InlineData("\"from\": 4, \"to\": 5", "\"from\": 4, \"to\": 9", "$.branches[3].to")]
    [InlineData("{ \"id\": 3, \"baseKvLL\": 4.16 },", "{ \"id\": 3, \"baseKvLL\": 0.416 },", "$.branches[1]")]          // switch across kV bases
    [InlineData("\"to\": 3, \"zPu\": { \"re\": [[0, 0, 0]", "\"to\": 3, \"zPu\": { \"re\": [[1e-9, 0, 0]", "$.branches[1].zPu")]
    [InlineData("\"phases\": \"ab\", \"regulatorRatio\": [1.025, 0.99375, 1]", "\"phases\": \"ab\", \"regulatorRatio\": [1.025, 0.99375, 1.1]", "$.branches[2].regulatorRatio[2]")]
    [InlineData("\"phases\": \"ab\", \"regulatorRatio\": [1.025, 0.99375, 1],", "\"phases\": \"ab\",", "$.branches[2].regulatorRatio")]
    [InlineData("\"kind\": \"line\", \"from\": 1, \"to\": 2,", "\"kind\": \"line\", \"from\": 1, \"to\": 2, \"regulatorRatio\": [1, 1, 1],", "$.branches[0].regulatorRatio")]
    [InlineData("\"kind\": \"line\", \"from\": 1, \"to\": 2,", "\"kind\": \"line\", \"from\": 1, \"to\": 2, \"transformerRatio\": 1.01,", "$.branches[0].transformerRatio")]
    [InlineData("\"kind\": \"line\", \"from\": 1, \"to\": 2,", "\"kind\": \"line\", \"from\": 1, \"to\": 2, \"transformerElement\": \"delta-delta\",", "$.branches[0].transformerElement")]
    [InlineData("\"kind\": \"line\", \"from\": 4, \"to\": 5, \"phases\": \"ab\",", "\"kind\": \"line\", \"from\": 4, \"to\": 5, \"phases\": \"a\",", "$.branches[3].phases")]   // phase b entries on a phase-a line
    [InlineData("\"connection\": \"delta\", \"model\": \"constant-current\", \"sPu\": { \"re\": [0.1, 0, 0]", "\"connection\": \"delta\", \"model\": \"constant-current\", \"sPu\": { \"re\": [0.1, 0.1, 0]", "$.loads[0].kw")] // b-c on an ab bus (the v1 check's path)
    public void N339_the_v2_reader_refuses_by_name(string from, string to, string where)
    {
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.ParseV2(N339Mutate(from, to), "mutated.json"));
        Assert.StartsWith("mutated.json: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(where, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N339_a_transformer_element_round_trips_with_its_ratio_and_a_gy_gy_off_nominal_ratio_stays_a_regulator_ratio()
    {
        // The bundled off-nominal and delta feeders cover the element kinds through the theory above; here the rules.
        string withTx = N339Mutate("\"id\": \"S23\", \"kind\": \"switch\", \"from\": 2, \"to\": 3, \"zPu\": { \"re\": [[0, 0, 0], [0, 0, 0], [0, 0, 0]], \"im\": [[0, 0, 0], [0, 0, 0], [0, 0, 0]] } }",
            "\"id\": \"S23\", \"kind\": \"transformer\", \"from\": 2, \"to\": 3, \"transformerElement\": \"delta-delta\", \"transformerRatio\": 1.0125, \"zPu\": { \"re\": [[0.01, 0, 0], [0, 0.01, 0], [0, 0, 0.01]], \"im\": [[0.06, 0, 0], [0, 0.06, 0], [0, 0, 0.06]] } }");
        // A delta-delta secondary is three-wire: the wye load and capacitor on bus 2 are upstream, the regulator downstream
        // is refused by the sweep, not the loader; the loader accepts the document.
        MultiPhaseFeeder f = MultiPhaseJsonLoader.ParseV2(withTx, "tx.json");
        Assert.True(f.Model.Branches[1].DeltaDelta);
        Assert.Equal(1.0125, f.Model.Branches[1].TransformerRatio);
        Assert.Null(MultiPhaseV2Writer.FirstDifference(f, MultiPhaseJsonLoader.ParseV2(MultiPhaseV2Writer.ToJson(f), "b.json")));
        // A transformer on two phases is refused.
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.ParseV2(
            withTx.Replace("\"kind\": \"transformer\", \"from\": 2, \"to\": 3,", "\"kind\": \"transformer\", \"from\": 2, \"to\": 3, \"phases\": \"ab\",", StringComparison.Ordinal), "t2.json"));
        Assert.Contains("$.branches[1].phases", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N339_the_writer_needs_exactly_one_provenance()
    {
        MultiPhaseFeeder v1 = MultiPhaseJsonLoader.Load(N339Feeder("ieee4-gyGy-stepdown-balanced.json"));
        Assert.Throws<ArgumentException>(() => MultiPhaseV2Writer.ToJson(v1));                                                    // none
        Assert.Throws<ArgumentException>(() => MultiPhaseV2Writer.ToJson(v1, new AssetClassProvenance(ProvenanceTag.CuratedPlaceholder, "no token")));
        MultiPhaseFeeder v2 = MultiPhaseJsonLoader.ParseV2(MultiPhaseV2Writer.ToJson(v1, N339Prov), "v2.json");
        Assert.Throws<ArgumentException>(() => MultiPhaseV2Writer.ToJson(v2, N339Prov));                                         // two
        // The CLI's default for a source without provenance is an honest placeholder carrying [VERIFY].
        AssetClassProvenance conv = MultiPhaseV2Writer.ConvertedWithoutProvenance("data/dist3ph/ieee4-gyGy-stepdown-balanced.json");
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, conv.Tag);
        Assert.Contains("[VERIFY]", conv.Note, StringComparison.Ordinal);
        Assert.Contains("ieee4-gyGy-stepdown-balanced.json", conv.Note, StringComparison.Ordinal);
        Assert.Null(MultiPhaseV2Writer.FirstDifference(v1, MultiPhaseJsonLoader.ParseV2(MultiPhaseV2Writer.ToJson(v1, conv), "c.json")));
    }

    [Fact]
    public void N339_first_difference_sees_one_bit()
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.ParseV2(N339Doc, "hand.json");
        string nudged = N339Doc.Replace("\"re\": [0.1, 0.11, 0.12]", "\"re\": [0.1, 0.11000000000000001, 0.12]", StringComparison.Ordinal);
        Assert.NotEqual(N339Doc, nudged);
        string? diff = MultiPhaseV2Writer.FirstDifference(f, MultiPhaseJsonLoader.ParseV2(nudged, "n.json"));
        Assert.NotNull(diff);
        Assert.Contains("Y2", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void N339_the_validate_engine_knows_the_tag_and_runs_the_reader()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n339v-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Contains("gridsim-dist3ph/2", V2DocumentValidator.KnownTags);
            string good = Path.Combine(dir, "good.json");
            File.WriteAllText(good, N339Doc);
            string twoSources = Path.Combine(dir, "two.json");
            File.WriteAllText(twoSources, N339Mutate("{ \"id\": 2, \"baseKvLL\": 4.16 },", "{ \"id\": 2, \"baseKvLL\": 4.16, \"sourceVPu\": { \"re\": [1, 1, 1], \"im\": [0, 0, 0] } },"));
            string noProv = Path.Combine(dir, "noprov.json");
            File.WriteAllText(noProv, N339Mutate("\"provenance\": { \"_default\": { \"tag\": \"Synthetic\", \"note\": \"made up for a test\" } },", ""));
            IReadOnlyList<V2DocumentReport> r = V2DocumentValidator.ValidateFiles(new[] { good, twoSources, noProv }, V2.V2TestSupport.SchemasDir);
            Assert.True(r[0].Valid, string.Join("; ", r[0].Errors));
            Assert.Equal("gridsim-dist3ph/2", r[0].SchemaTag);
            SchemaError e = Assert.Single(r[1].Errors);
            Assert.Contains("exactly one bus must carry", e.Message, StringComparison.Ordinal);
            Assert.False(r[2].Valid);
        }
        finally { Directory.Delete(dir, true); }
    }
}
