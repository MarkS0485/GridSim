using System.Globalization;
using System.Text.Json.Nodes;
using GridSim.Core.Analysis;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.IO.Provenance;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Net;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>Post-RC3 audit batch C: readers and constructors that must refuse what they used to accept.</summary>
public sealed class PostRc3BatchCRefusalTests
{
    // ---- PSS/E ---------------------------------------------------------------------------------------------------

    private const string TinyHead =
        "0, 100, 33, 0, 0, 50 / tiny\nTITLE 1\nTITLE 2\n1, 'A', 132, 3\n2, 'B', 132, 1\n" +
        "0 / END OF BUS DATA, BEGIN LOAD DATA\n2, '1', 1, 1, 1, 10, 5\n" +
        "0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA\n";   // line 8 ends here; the next line is 9

    private const string TinyMid =
        "0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA\n1, '1', 10, 0, 100, -100, 1.0\n" +
        "0 / END OF GENERATOR DATA, BEGIN BRANCH DATA\n";

    private const string Branch12 = "1, 2, '1', 0.01, 0.1, 0.02\n";

    private const string BranchEnd = "0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA\n";

    private const string Xfmr12 =
        "1, 2, 0, '1', 1, 1, 1, 0, 0, 2, 'T', 1\n0.01, 0.1, 100\n1.0, 132, 0, 100, 100, 100\n1.0, 132\n";

    private const string TinyTail = "0 / END OF TRANSFORMER DATA, BEGIN AREA DATA\nQ\n";

    private static InvalidDataException Refused(string raw) =>
        Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(raw, "t"));

    [Fact]
    public void A_raw_file_that_ends_inside_a_section_is_refused_naming_the_section()
    {
        var ex = Refused(TinyHead + "2, '1', 1, 0.0, 5.0\n");
        Assert.Contains("FIXED SHUNT", ex.Message, StringComparison.Ordinal);
        Assert.Contains("line 9", ex.Message, StringComparison.Ordinal);
        Assert.Contains("truncated", ex.Message, StringComparison.Ordinal);

        var cutBranch = Refused(TinyHead + TinyMid + "1, 2, '1', 0.01");
        Assert.Contains("BRANCH", cutBranch.Message, StringComparison.Ordinal);
        Assert.Contains("truncated", cutBranch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_raw_file_that_ends_straight_after_a_terminator_still_reads_with_the_no_Q_note()
    {
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(TinyHead + TinyMid + Branch12 + BranchEnd, "t");
        Assert.Contains(r.Notes, n => n == "the file has no Q end-of-data record; read to the end of the text.");
    }

    [Fact]
    public void A_duplicated_branch_record_is_refused_naming_both_lines()
    {
        string head = TinyHead + TinyMid;
        var ex = Refused(head + Branch12 + Branch12 + BranchEnd + TinyTail);
        Assert.Contains("is defined twice (also at line 12)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("line 13 (BRANCH data)", ex.Message, StringComparison.Ordinal);

        // Reversed ends, same circuit, and an out-of-service duplicate: the identity is the same.
        Assert.Contains("is defined twice", Refused(head + Branch12 + "2, 1, '1', 0.01, 0.1, 0.02\n" + BranchEnd + TinyTail).Message, StringComparison.Ordinal);
        Assert.Contains("is defined twice", Refused(head + Branch12 + "1, 2, '1', 0.01, 0.1, 0.02, 0, 0, 0, 0, 0, 0, 0, 0\n" + BranchEnd + TinyTail).Message, StringComparison.Ordinal);

        // A parallel circuit with its own id is fine.
        PsseRawParser.ParseWithDiagnostics(head + Branch12 + "1, 2, '2', 0.01, 0.1, 0.02\n" + BranchEnd + TinyTail, "t");
    }

    [Fact]
    public void A_duplicated_two_winding_transformer_is_refused()
    {
        string head = TinyHead + TinyMid + Branch12 + BranchEnd;
        PsseRawParser.ParseWithDiagnostics(head + Xfmr12 + TinyTail, "t");
        var ex = Refused(head + Xfmr12 + Xfmr12 + TinyTail);
        Assert.Contains("transformer 1-2 circuit '1' is defined twice (also at line 14)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_Q_is_a_name_and_only_a_bare_Q_ends_the_data()
    {
        string[] lines = { "'Q', 1, 2", "\"q\"", "Q", "q / end", "  Q" };
        var cur = new PsseLineCursor(lines, 0);
        Assert.False(cur.Next("X")!.IsEndOfData);
        Assert.False(cur.Next("X")!.IsEndOfData);
        Assert.True(cur.Next("X")!.IsEndOfData);
        Assert.True(cur.Next("X")!.IsEndOfData);
        Assert.True(cur.Next("X")!.IsEndOfData);
    }

    private static string Rev33WithSwitchedShunt(string shuntRecord)
        => TinyHead + TinyMid + Branch12 + BranchEnd + "0 / END OF TRANSFORMER DATA, BEGIN AREA DATA\n" +
           string.Concat(Enumerable.Repeat("0\n", 10)) + shuntRecord + "0\nQ\n";

    [Fact]
    public void A_revision_33_switched_shunt_has_the_id_1_whatever_its_control_mode()
    {
        // Revision 33 field 2 is MODSW (here 0), not an identifier.
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(Rev33WithSwitchedShunt("2, 0, 0, 1, 1.05, 0.95, 0, 100, '', 0.0, 1, 5.0\n"), "t");
        PsseShuntSource s = Assert.Single(r.ShuntSources, x => x.Kind == PsseShuntKind.Switched);
        Assert.Equal("1", s.Key.Id);
        PsseEquipmentSource e = Assert.Single(r.EquipmentSources, x => x.EquipmentType == "S");
        Assert.Equal("1", e.Id);
    }

    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    [Fact]
    public void A_seq_file_that_ends_inside_a_section_is_refused_but_one_ending_after_a_terminator_is_read()
    {
        var raw = PsseRawParser.ParseFile(Fixture("ukgds-style-v30.raw"), new PsseReadOptions { Revision = 30, FrequencyHz = 50 });
        string seq = File.ReadAllText(Fixture("ukgds-style-v30.seq"));
        string upTo = seq[..seq.IndexOf("@!Zero Sequence Generator Impedance Data", StringComparison.Ordinal)].TrimEnd();
        // upTo ends with the terminator of the negative-sequence generator section: read, with the note.
        PsseSeqResult ok = PsseSeqReader.Apply(upTo + "\r\n", raw);
        Assert.Contains(ok.Notes, n => n.StartsWith("the file stops after", StringComparison.Ordinal));
        // Drop that terminator line: the text now ends inside the section.
        string cut = upTo[..upTo.LastIndexOf('\n')];
        var ex = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(cut + "\r\n", raw));
        Assert.Contains("ends inside the NEGATIVE SEQUENCE GENERATOR data", ex.Message, StringComparison.Ordinal);
    }

    // ---- Harmonic source set (IO-08) -------------------------------------------------------------------------------

    [Fact]
    public void A_harmonic_source_set_with_null_sources_is_refused_naming_the_member()
    {
        string path = TestTemp.Under("gs-c-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"schema\":\"" + HarmonicSourceSet.SchemaId + "\",\"baseMva\":100,\"sources\":null}");
            var ex = Assert.Throws<InvalidDataException>(() => HarmonicSourceSet.Load(path));
            Assert.Contains("sources", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    // ---- Bridge / solve import (IO-24, IO-03) ----------------------------------------------------------------------

    private static JsonNode Case9Export()
    {
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult solved = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        var meta = new SolveExportMeta("case9", "cpu", "deadbeef", "2026-07-01T00:00:00Z", 1.0, "test");
        return JsonNode.Parse(SolveExport.ToJson(model, solved, trace, meta))!;
    }

    private static InvalidDataException ImportRefused(JsonNode doc) =>
        Assert.Throws<InvalidDataException>(() => SolveImport.Read(doc.ToJsonString()));

    [Fact]
    public void An_unmodified_solve_export_still_imports()
    {
        SolveSnapshot s = SolveImport.Read(Case9Export().ToJsonString());
        Assert.Equal(9, s.Result.BusIds.Count);
    }

    [Theory]
    [InlineData("missing", "missing coefficient 'cp'")]
    [InlineData("nan", "not a finite number")]
    [InlineData("sum", "sum to 1.1")]
    public void The_bridge_model_block_refuses_an_incomplete_or_non_finite_zip(string kind, string expected)
    {
        JsonNode doc = Case9Export();
        JsonObject bus = doc["model"]!["buses"]![0]!.AsObject();
        JsonObject zip = kind switch
        {
            "missing" => new JsonObject { ["ap"] = 0.5, ["bp"] = 0.5, ["aq"] = 0.5, ["bq"] = 0.5, ["cq"] = 0.0 },
            "nan" => new JsonObject { ["ap"] = "NaN", ["bp"] = 0.0, ["cp"] = 1.0, ["aq"] = 0.0, ["bq"] = 0.0, ["cq"] = 1.0 },
            _ => new JsonObject { ["ap"] = 0.1, ["bp"] = 0.0, ["cp"] = 1.0, ["aq"] = 0.0, ["bq"] = 0.0, ["cq"] = 1.0 },
        };
        bus["zip"] = zip;
        var ex = ImportRefused(doc);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus " + bus["id"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_import_refuses_a_result_bus_without_vm()
    {
        JsonNode doc = Case9Export();
        JsonObject row = doc["result"]!["buses"]![3]!.AsObject();
        int id = row["id"]!.GetValue<int>();
        row.Remove("vm");
        var ex = ImportRefused(doc);
        Assert.Contains("bus " + id.ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.Contains("vm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_import_refuses_result_buses_that_do_not_match_the_model()
    {
        JsonNode doc = Case9Export();
        doc["result"]!["buses"]!.AsArray().RemoveAt(8);
        var ex = ImportRefused(doc);
        Assert.Contains("8 bus row(s) but its model has 9", ex.Message, StringComparison.Ordinal);

        JsonNode noList = Case9Export();
        noList["result"]!.AsObject().Remove("buses");
        Assert.Contains("no 'buses' list", ImportRefused(noList).Message, StringComparison.Ordinal);

        JsonNode swapped = Case9Export();
        JsonArray rows = swapped["result"]!["buses"]!.AsArray();
        JsonNode first = rows[0]!; rows.RemoveAt(0); rows.Add(first);
        Assert.Contains("must follow the model's bus order", ImportRefused(swapped).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_import_refuses_a_non_finite_voltage_in_a_converged_document_only()
    {
        JsonNode doc = Case9Export();
        doc["result"]!["buses"]![2]!["vm"] = "NaN";
        Assert.Contains("non-finite", ImportRefused(doc).Message, StringComparison.Ordinal);

        doc["solve"]!["converged"] = false;
        doc["solve"]!.AsObject().Remove("status");
        SolveImport.Read(doc.ToJsonString());   // a non-converged document may carry NaN
    }

    // ---- Duplicate keys (IO-28, AN-21) -----------------------------------------------------------------------------

    [Fact]
    public void A_provenance_manifest_with_a_duplicated_key_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ProvenanceManifest.Parse(
            "{\"schema\":\"x\",\"case\":\"c\",\"sources\":[{\"id\":\"a\",\"published\":false,\"published\":true}]}"));
        Assert.Contains("not JSON", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ProvenanceManifest.Parse("{\"buses\":{},\"buses\":{}}"));
    }

    [Fact]
    public void A_qsts_file_with_a_duplicated_member_is_refused()
    {
        GridModel model = IeeeCases.Case9();
        var ex = Assert.Throws<InvalidDataException>(() => QstsFile.Parse(
            "{\"format\":\"gridsim-qsts/1\",\"stepSeconds\":60,\"steps\":96,\"steps\":48}", "dup.json", ".", model));
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_commitment_file_with_a_duplicated_member_is_refused()
    {
        GridModel model = IeeeCases.Case9();
        var ex = Assert.Throws<InvalidDataException>(() => UnitCommitmentFile.Read(
            "{\"format\":\"gridsim-uc/1\",\"contingencies\":[],\"contingencies\":[]}", "dup.json", model));
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    // ---- Dynamics input validation (DP-25, DP-22, DP-04) -----------------------------------------------------------

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_non_finite_forming_setpoint_is_refused(double value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new SwingResponders(new DynamicsConfig { FormingSetpointMw = value }, 1000.0));
        Assert.Contains("FormingSetpointMw", ex.Message, StringComparison.Ordinal);
        _ = new SwingResponders(new DynamicsConfig { FormingSetpointMw = -50.0 }, 1000.0);   // negative is a valid set point
    }

    private static ReecParameters ReecA() => new()
    {
        Variant = ReecVariant.A, PfFlag = 0, VFlag = 1, QFlag = 1, PFlag = 1, PqFlag = 0,
        Vdip = 0.9, Vup = 1.1, Trv = 0.02, Dbd1 = -0.05, Dbd2 = 0.05, Kqv = 2.0, Iqh1 = 1.05, Iql1 = -1.05, Vref0 = 0.0,
        Tp = 0.05, Qmax = 0.5, Qmin = -0.5, Vmax = 1.1, Vmin = 0.9, Kqp = 0.0, Kqi = 0.1, Kvp = 0.0, Kvi = 40.0, Tiq = 0.02,
        DPmax = 0.1, DPmin = -0.1, Pmax = 2.0, Pmin = 0.0, Imax = 2.0, Tpord = 0.02,
        Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 2.0, 2.0, 2.0, 2.0 }, Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 2.0, 2.0, 2.0, 2.0 },
    };

    [Fact]
    public void Negative_vdl_currents_in_reec_a_are_refused_by_the_parameter_check()
    {
        Assert.Empty(ReecA().Check());
        IReadOnlyList<string> iq = (ReecA() with { Iq = new[] { -0.1, 1.1, 1.1, 1.1 } }).Check();
        Assert.Contains(iq, p => p.Contains("iq1", StringComparison.Ordinal) && p.Contains("VDL1", StringComparison.Ordinal));
        IReadOnlyList<string> ip = (ReecA() with { Ip = new[] { 1.0, 1.0, -0.2, 1.0 } }).Check();
        Assert.Contains(ip, p => p.Contains("ip3", StringComparison.Ordinal) && p.Contains("VDL2", StringComparison.Ordinal));
        IReadOnlyList<string> c = (ReecA() with { Variant = ReecVariant.C, T = 1.0, SocIni = 0.5, SocMax = 1.0, SocMin = 0.0, Iq = new[] { -0.1, 1.1, 1.1, 1.1 } }).Check();
        Assert.Contains(c, p => p.Contains("iq1", StringComparison.Ordinal));
    }

    // Re-pinned (ruling 15, DYN-02): DP-04 refused Se(1.0) = 0 with Se(1.2) > 0 so the 1.2 point was not dropped silently.
    // Mark's ruling reads it as the curve that starts at 1.0 pu: A = 1, B = 1.2 Se(1.2) / 0.04, so Se(1.2) is the given 0.30
    // (Se(x) = B (x - A)^2 / x) and Se is 0 at and below 1.0. The 1.2 point is kept, which was DP-04's concern.
    [Fact]
    public void A_saturation_curve_with_zero_s10_and_positive_s12_starts_at_1_pu()
    {
        var sat = new SubtransientMachine.SaturationCurve(0.0, 0.30);
        Assert.Equal(0.30, sat.Se(1.2), 12);
        Assert.Equal(0.0, sat.Se(1.0));
        Assert.Equal(0.0, sat.Se(0.9));
        Assert.Equal(0.0, new SubtransientMachine.SaturationCurve(0.0, 0.0).Se(1.1));   // both zero stays unsaturated
    }
}
