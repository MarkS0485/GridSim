using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.IO;
using GridSim.Core.IO.Scl;
using GridSim.Core.Journal;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP30: IEC 61850 SCL substation import (SclReader, SclParams, SclImporter, the scl-import verb).
/// Sources: the element and attribute names are those of the public SCL schema 2007B4 (IEC 61850-6:2009/AMD1:2018 code
/// components: SCL_Substation.xsd, SCL_Enums.xsd); the type-code meanings are the names OpenSCD shows (Apache-2.0); the
/// grounded-terminal convention for an earth switch is OpenSCD's. The hand-made files below are written for these tests;
/// the public sample is OpenSCD's valid2007B4.scd (Apache-2.0, fixtures/openscd-scl/ATTRIBUTION.txt). Every expected
/// value is written out by hand from the files, not taken from the importer.
/// </summary>
public sealed class Rc5Wp30SclImportTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("scl");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static string T(string name, string node) =>
        $"<Terminal name=\"{name}\" connectivityNode=\"{node}\" cNodeName=\"{node[(node.LastIndexOf('/') + 1)..]}\"/>";

    private static string Earth(string bayPath) =>
        $"<Terminal name=\"T2\" connectivityNode=\"{bayPath}/grounded\" cNodeName=\"grounded\"/>";

    // A double busbar substation S1 (busbars BB1 and BB2, a coupler breaker, a feeder bay DIS-CBR-CT-DIS with an earth
    // switch, a line, and a generator on BB2) and a second substation S2 holding the far end of the line and an infeed.
    private static string HandScd(string extraGenBay = "", string extraRoot = "", bool duplicateNode = false)
    {
        string dup = duplicateNode ? "<ConnectivityNode name=\"BB1\" pathName=\"S1/V132/BB1/BB1\"/>" : "";
        return $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <SCL xmlns="http://www.iec.ch/61850/2003/SCL" version="2007" revision="B" release="4">
          <Header id="wp30-hand"/>
          <Substation name="S1">
            <VoltageLevel name="V132">
              <Voltage unit="V" multiplier="k">132</Voltage>
              <Bay name="BB1"><ConnectivityNode name="BB1" pathName="S1/V132/BB1/BB1"/></Bay>
              <Bay name="BB2"><ConnectivityNode name="BB2" pathName="S1/V132/BB2/BB2"/>{dup}</Bay>
              <Bay name="CPL">
                <ConductingEquipment type="CBR" name="QA1">{T("T1", "S1/V132/BB1/BB1")}{T("T2", "S1/V132/BB2/BB2")}</ConductingEquipment>
              </Bay>
              <Bay name="FDR">
                <ConductingEquipment type="DIS" name="QB1">{T("T1", "S1/V132/BB1/BB1")}{T("T2", "S1/V132/FDR/N1")}</ConductingEquipment>
                <ConductingEquipment type="CBR" name="QA1">{T("T1", "S1/V132/FDR/N1")}{T("T2", "S1/V132/FDR/N2")}</ConductingEquipment>
                <ConductingEquipment type="CTR" name="BA1">{T("T1", "S1/V132/FDR/N2")}{T("T2", "S1/V132/FDR/N3")}</ConductingEquipment>
                <ConductingEquipment type="DIS" name="QB9">{T("T1", "S1/V132/FDR/N3")}{T("T2", "S1/V132/FDR/N4")}</ConductingEquipment>
                <ConductingEquipment type="DIS" name="QC9">{T("T1", "S1/V132/FDR/N4")}{Earth("S1/V132/FDR")}</ConductingEquipment>
                <ConductingEquipment type="LIN" name="L1">{T("T1", "S1/V132/FDR/N4")}</ConductingEquipment>
                <ConnectivityNode name="N1" pathName="S1/V132/FDR/N1"/>
                <ConnectivityNode name="N2" pathName="S1/V132/FDR/N2"/>
                <ConnectivityNode name="N3" pathName="S1/V132/FDR/N3"/>
                <ConnectivityNode name="N4" pathName="S1/V132/FDR/N4"/>
                <Function name="Protection"><LNode iedName="IED1" ldInst="PROT" lnClass="PDIS" lnInst="1"/></Function>
              </Bay>
              <Bay name="GEN">
                <ConductingEquipment type="GEN" name="G1">{T("T1", "S1/V132/BB2/BB2")}</ConductingEquipment>{extraGenBay}
              </Bay>
            </VoltageLevel>
          </Substation>
          <Substation name="S2">
            <VoltageLevel name="V132">
              <Voltage unit="V" multiplier="k">132</Voltage>
              <Bay name="BB"><ConnectivityNode name="BB" pathName="S2/V132/BB/BB"/></Bay>
              <Bay name="L1"><ConductingEquipment type="LIN" name="L1">{T("T1", "S2/V132/BB/BB")}</ConductingEquipment></Bay>
              <Bay name="INF"><ConductingEquipment type="IFL" name="GRID">{T("T1", "S2/V132/BB/BB")}</ConductingEquipment></Bay>
            </VoltageLevel>
          </Substation>{extraRoot}
          <Communication>
            <SubNetwork name="StationBus" type="8-MMS"><ConnectedAP iedName="IED1" apName="AP1"/></SubNetwork>
          </Communication>
          <IED name="IED1">
            <AccessPoint name="AP1"><Server><Authentication/>
              <LDevice inst="PROT">
                <LN0 lnClass="LLN0" inst="" lnType="LLN0_T"><GSEControl name="GCB1" datSet="DS1" appID="A1"/></LN0>
                <LN lnClass="PDIS" inst="1" lnType="PDIS_T"/>
              </LDevice>
            </Server></AccessPoint>
          </IED>
          <DataTypeTemplates><LNodeType id="LLN0_T" lnClass="LLN0"/><LNodeType id="PDIS_T" lnClass="PDIS"/></DataTypeTemplates>
        </SCL>
        """;
    }

    // Every switch's state is stated: the SCL substation section holds none, and a switch without one is refused unless the
    // import assumes closed (see T09).
    private const string HandParams = """
        {
          "format": "gridsim-scl-params/1",
          "lines": [ { "ends": ["S1/V132/FDR/L1", "S2/V132/L1/L1"], "rPu": 0.01, "xPu": 0.1, "bPu": 0.02, "rateAMva": 150 } ],
          "generators": [ { "path": "S1/V132/GEN/G1", "pMw": 20, "vPu": 1.01 } ],
          "infeeds": [ { "path": "S2/V132/INF/GRID", "vPu": 1.0 } ],
          "loads": [ { "id": "LD1", "node": "S1/V132/BB1/BB1", "pMw": 40, "qMvar": 10 },
                     { "id": "LD2", "node": "S1/V132/BB2/BB2", "pMw": 30, "qMvar": 5 } ],
          "switches": [ { "path": "S1/V132/FDR/QC9", "normalOpen": true },
                        { "path": "S1/V132/CPL/QA1", "normalOpen": false }, { "path": "S1/V132/FDR/QB1", "normalOpen": false },
                        { "path": "S1/V132/FDR/QA1", "normalOpen": false }, { "path": "S1/V132/FDR/QB9", "normalOpen": false } ]
        }
        """;

    // The same values with only the earth switch's state given: the other four switches have none.
    private const string HandParamsUnstated = """
        {
          "format": "gridsim-scl-params/1",
          "lines": [ { "ends": ["S1/V132/FDR/L1", "S2/V132/L1/L1"], "rPu": 0.01, "xPu": 0.1, "bPu": 0.02, "rateAMva": 150 } ],
          "generators": [ { "path": "S1/V132/GEN/G1", "pMw": 20, "vPu": 1.01 } ],
          "infeeds": [ { "path": "S2/V132/INF/GRID", "vPu": 1.0 } ],
          "loads": [ { "id": "LD1", "node": "S1/V132/BB1/BB1", "pMw": 40, "qMvar": 10 },
                     { "id": "LD2", "node": "S1/V132/BB2/BB2", "pMw": 30, "qMvar": 5 } ],
          "switches": [ { "path": "S1/V132/FDR/QC9", "normalOpen": true } ]
        }
        """;

    // The same network written directly as a gridsim-substations/1 file, by hand: the current transformer BA1 joins N2
    // and N3, so N3 is merged into N2 (the lower pathName); N3 is gone and QB9 runs from N2.
    private const string HandDirect = """
        {
          "format": "gridsim-substations/1",
          "name": "wp30-hand",
          "substations": [
            { "id": "S1",
              "voltageLevels": [ { "id": "S1/V132", "nominalKv": 132 } ],
              "bays": [ { "id": "S1/V132/BB1", "voltageLevel": "S1/V132", "name": "BB1" }, { "id": "S1/V132/BB2", "voltageLevel": "S1/V132", "name": "BB2" },
                        { "id": "S1/V132/CPL", "voltageLevel": "S1/V132", "name": "CPL" }, { "id": "S1/V132/FDR", "voltageLevel": "S1/V132", "name": "FDR" },
                        { "id": "S1/V132/GEN", "voltageLevel": "S1/V132", "name": "GEN" } ],
              "nodes": [ { "id": "S1/V132/BB1/BB1", "voltageLevel": "S1/V132", "bay": "S1/V132/BB1" },
                         { "id": "S1/V132/BB2/BB2", "voltageLevel": "S1/V132", "bay": "S1/V132/BB2" },
                         { "id": "S1/V132/FDR/N1", "voltageLevel": "S1/V132", "bay": "S1/V132/FDR" },
                         { "id": "S1/V132/FDR/N2", "voltageLevel": "S1/V132", "bay": "S1/V132/FDR" },
                         { "id": "S1/V132/FDR/N4", "voltageLevel": "S1/V132", "bay": "S1/V132/FDR" } ],
              "switches": [
                { "id": "S1/V132/CPL/QA1", "device": "breaker", "from": "S1/V132/BB1/BB1", "to": "S1/V132/BB2/BB2", "normalOpen": false, "bay": "S1/V132/CPL" },
                { "id": "S1/V132/FDR/QB1", "device": "disconnector", "from": "S1/V132/BB1/BB1", "to": "S1/V132/FDR/N1", "normalOpen": false, "bay": "S1/V132/FDR" },
                { "id": "S1/V132/FDR/QA1", "device": "breaker", "from": "S1/V132/FDR/N1", "to": "S1/V132/FDR/N2", "normalOpen": false, "bay": "S1/V132/FDR" },
                { "id": "S1/V132/FDR/QB9", "device": "disconnector", "from": "S1/V132/FDR/N2", "to": "S1/V132/FDR/N4", "normalOpen": false, "bay": "S1/V132/FDR" },
                { "id": "S1/V132/FDR/QC9", "device": "earthSwitch", "from": "S1/V132/FDR/N4", "normalOpen": true, "bay": "S1/V132/FDR" } ] },
            { "id": "S2",
              "voltageLevels": [ { "id": "S2/V132", "nominalKv": 132 } ],
              "bays": [ { "id": "S2/V132/BB", "voltageLevel": "S2/V132", "name": "BB" }, { "id": "S2/V132/L1", "voltageLevel": "S2/V132", "name": "L1" },
                        { "id": "S2/V132/INF", "voltageLevel": "S2/V132", "name": "INF" } ],
              "nodes": [ { "id": "S2/V132/BB/BB", "voltageLevel": "S2/V132", "bay": "S2/V132/BB" } ],
              "switches": [] } ],
          "lines": [ { "id": "S1/V132/FDR/L1", "a": "S1/V132/FDR/N4", "b": "S2/V132/BB/BB", "rPu": 0.01, "xPu": 0.1, "bPu": 0.02, "rateAMva": 150 } ],
          "loads": [ { "id": "LD1", "node": "S1/V132/BB1/BB1", "pMw": 40, "qMvar": 10 }, { "id": "LD2", "node": "S1/V132/BB2/BB2", "pMw": 30, "qMvar": 5 } ],
          "generators": [ { "id": "S1/V132/GEN/G1", "node": "S1/V132/BB2/BB2", "pMw": 20, "vPu": 1.01 },
                          { "id": "S2/V132/INF/GRID", "node": "S2/V132/BB/BB", "pMw": 0, "vPu": 1.0, "slack": true } ]
        }
        """;

    private static SclImportResult ImportHand() => SclImporter.Import(SclReader.ParseText(HandScd()), SclParams.Read(HandParams));

    private static SclImportException Refused(string scd, string prms) =>
        Assert.Throws<SclImportException>(() => SclImporter.Import(SclReader.ParseText(scd), SclParams.Read(prms)));

    // ---- 1. hand SCD: double busbar, one feeder bay ----------------------------------------------------------------

    [Fact]
    public void T01_hand_scd_gives_the_exact_nodes_switches_line_and_sidecar_values()
    {
        SubstationModel m = ImportHand().Model;
        Assert.Equal("wp30-hand", m.Name);
        Assert.Equal(["S1", "S2"], m.Substations.Select(s => s.Id));
        Assert.Equal(["S1/V132/BB1/BB1", "S1/V132/BB2/BB2", "S1/V132/FDR/N1", "S1/V132/FDR/N2", "S1/V132/FDR/N4", "S2/V132/BB/BB"],
            m.AllNodes.Select(n => n.Id));
        Assert.Equal(132.0, m.Substations[0].VoltageLevels.Single().NominalKv);
        Assert.Equal(
        [
            new NbSwitch("S1/V132/CPL/QA1", SwitchKind.Breaker, "S1/V132/BB1/BB1", "S1/V132/BB2/BB2", false, "S1/V132/CPL"),
            new NbSwitch("S1/V132/FDR/QB1", SwitchKind.Disconnector, "S1/V132/BB1/BB1", "S1/V132/FDR/N1", false, "S1/V132/FDR"),
            new NbSwitch("S1/V132/FDR/QA1", SwitchKind.Breaker, "S1/V132/FDR/N1", "S1/V132/FDR/N2", false, "S1/V132/FDR"),
            new NbSwitch("S1/V132/FDR/QB9", SwitchKind.Disconnector, "S1/V132/FDR/N2", "S1/V132/FDR/N4", false, "S1/V132/FDR"),
            new NbSwitch("S1/V132/FDR/QC9", SwitchKind.EarthSwitch, "S1/V132/FDR/N4", null, true, "S1/V132/FDR"),
        ], m.AllSwitches);
        Assert.Equal(new NbLine("S1/V132/FDR/L1", "S1/V132/FDR/N4", "S2/V132/BB/BB", 0.01, 0.1, 0.02, 150), Assert.Single(m.Lines));
        Assert.Equal(
        [
            new NbGenerator("S1/V132/GEN/G1", "S1/V132/BB2/BB2", 20, 1.01),
            new NbGenerator("S2/V132/INF/GRID", "S2/V132/BB/BB", 0, 1.0, Slack: true),
        ], m.Generators);
        Assert.Equal([new NbLoad("LD1", "S1/V132/BB1/BB1", 40, 10), new NbLoad("LD2", "S1/V132/BB2/BB2", 30, 5)], m.Loads);
        Assert.Empty(m.Transformers);
        Assert.Empty(m.Shunts);
    }

    [Fact]
    public void T01b_projection_gives_two_buses_with_every_switch_closed_and_three_with_the_coupler_open()
    {
        SubstationModel m = ImportHand().Model;
        SubstationProjectionResult closed = SubstationProjection.ProjectNormal(m);
        Assert.Equal(2, closed.Buses.Count);
        Assert.Single(closed.Model.Branches);
        IReadOnlyDictionary<string, bool> open = SubstationProjection.With(m.NormalStates(), ["S1/V132/CPL/QA1"], []);
        SubstationProjectionResult split = SubstationProjection.Project(m, open);
        Assert.Equal(3, split.Buses.Count);
        Assert.Single(split.Model.Branches);
    }

    [Fact]
    public void T01c_report_lists_measurement_points_merges_defaults_not_imported_and_read_past()
    {
        SclImportResult r = ImportHand();
        SclMeasurementPoint ct = Assert.Single(r.MeasurementPoints);
        Assert.Equal(new SclMeasurementPoint("S1/V132/FDR/BA1", "CTR", "S1/V132/FDR/N2"), ct);
        Assert.Equal([("S1/V132/FDR/N3", "S1/V132/FDR/N2", "S1/V132/FDR/BA1")], r.MergedNodes);
        Assert.Empty(r.DefaultClosedSwitches);   // every state is in the params file (T09 covers the defaulted case)
        Assert.Empty(r.Model.Notes);
        Assert.Equal(new Dictionary<string, int> { ["Function"] = 1 }, r.Document.NotImported);
        Assert.Equal(["IED1/PROT/GCB1"], r.Document.GooseControlBlocks);
        Assert.Equal(
        [
            ("Communication sections", 1), ("SubNetwork", 1), ("ConnectedAP", 1), ("IED", 1), ("logical nodes (LN0 and LN)", 2),
            ("SampledValueControl", 0), ("DataTypeTemplates sections", 1), ("DataTypeTemplates entries", 2),
        ], r.Document.ReadPast);
        IReadOnlyList<string> lines = SclImporter.ReportLines(r);
        Assert.Contains("  NOT IMPORTED: Function x1", lines);
        Assert.Contains("  GOOSE control block IED1/PROT/GCB1 (listed only)", lines);
        Assert.Contains("  measurement point CTR S1/V132/FDR/BA1 at node 'S1/V132/FDR/N2' (ignored for topology)", lines);
    }

    [Fact]
    public void T01d_the_model_does_not_depend_on_the_order_of_the_params_file()
    {
        const string reordered = """
            {
              "switches": [ { "path": "S1/V132/FDR/QB9", "normalOpen": false }, { "path": "S1/V132/FDR/QA1", "normalOpen": false },
                            { "normalOpen": true, "path": "S1/V132/FDR/QC9" }, { "path": "S1/V132/FDR/QB1", "normalOpen": false },
                            { "path": "S1/V132/CPL/QA1", "normalOpen": false } ],
              "infeeds": [ { "vPu": 1.0, "path": "S2/V132/INF/GRID" } ],
              "generators": [ { "vPu": 1.01, "pMw": 20, "path": "S1/V132/GEN/G1" } ],
              "lines": [ { "rateAMva": 150, "bPu": 0.02, "xPu": 0.1, "rPu": 0.01, "ends": ["S1/V132/FDR/L1", "S2/V132/L1/L1"] } ],
              "loads": [ { "id": "LD2", "node": "S1/V132/BB2/BB2", "pMw": 30, "qMvar": 5 },
                         { "id": "LD1", "node": "S1/V132/BB1/BB1", "pMw": 40, "qMvar": 10 } ],
              "format": "gridsim-scl-params/1"
            }
            """;
        string a = SubstationJson.Write(ImportHand().Model);
        string b = SubstationJson.Write(SclImporter.Import(SclReader.ParseText(HandScd()), SclParams.Read(reordered)).Model);
        Assert.Equal(a, b);
    }

    // ---- 2. round trip: imported file -> projection -> pf equals the hand-written file, to the bit --------------------

    [Fact]
    public void T02_imported_model_round_trips_and_solves_bit_identical_to_the_hand_written_file()
    {
        SubstationModel inMemory = ImportHand().Model;
        SubstationModel imported = SubstationJson.Read(SubstationJson.Write(inMemory));
        SubstationModel direct = SubstationJson.Read(HandDirect);
        // The file oracle: the written import equals the hand-written file, byte for byte.
        Assert.Equal(SubstationJson.Write(direct), SubstationJson.Write(imported));

        // A separate claim: the importer's in-memory model (what a library caller uses, never written to a file) projects
        // and solves bit for bit like the hand-written file read back. This holds only if nothing in the in-memory model
        // (the unbounded generator limits, say, which the file omits) differs from what the file carries.
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(SubstationProjection.ProjectNormal(inMemory).Model);
        PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(SubstationProjection.ProjectNormal(direct).Model);
        Assert.True(a.Converged);
        Assert.True(b.Converged);
        Assert.Equal(b.BusIds, a.BusIds);
        Assert.Equal(b.Vm, a.Vm);
        Assert.Equal(b.VaDeg, a.VaDeg);
        Assert.Equal(b.PInjMw, a.PInjMw);
        Assert.Equal(b.QInjMvar, a.QInjMvar);
        Assert.Equal(b.TotalLossMw, a.TotalLossMw);
    }

    // ---- 3. refusals ------------------------------------------------------------------------------------------------

    [Fact]
    public void T03a_an_unsupported_type_code_is_refused_by_name()
    {
        string cap = "<ConductingEquipment type=\"CAP\" name=\"C1\">" + T("T1", "S1/V132/BB2/BB2") + "</ConductingEquipment>";
        SclImportException ex = Refused(HandScd(extraGenBay: cap), HandParams);
        Assert.Equal(["CAP 'S1/V132/GEN/C1': type CAP (Capacitor Bank) is not imported by GridSim's SCL import; refused."], ex.Problems);

        string ext = "<ConductingEquipment type=\"EXYZ\" name=\"X1\">" + T("T1", "S1/V132/BB2/BB2") + "</ConductingEquipment>";
        SclImportException ex2 = Refused(HandScd(extraGenBay: ext), HandParams);
        Assert.Equal(["EXYZ 'S1/V132/GEN/X1': type EXYZ is an extension code whose meaning the file's author defines; not imported, refused."], ex2.Problems);
    }

    [Fact]
    public void T03b_a_missing_sidecar_impedance_is_refused()
    {
        // A line entry without xPu is refused by the params reader, naming the JSON path.
        string noX = HandParams.Replace("\"xPu\": 0.1, ", "", StringComparison.Ordinal);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SclParams.Read(noX));
        Assert.Equal("$.lines[0].xPu: required number is missing.", ex.Message);

        // A line with no entry at all: both halves are named.
        string noLine = HandParams.Replace(
            "\"lines\": [ { \"ends\": [\"S1/V132/FDR/L1\", \"S2/V132/L1/L1\"], \"rPu\": 0.01, \"xPu\": 0.1, \"bPu\": 0.02, \"rateAMva\": 150 } ],", "", StringComparison.Ordinal);
        Assert.NotEqual(HandParams, noLine);
        SclImportException ex2 = Refused(HandScd(), noLine);
        Assert.Equal(
        [
            "LIN 'S1/V132/FDR/L1' has no entry in the params file's lines (its impedance is required).",
            "LIN 'S2/V132/L1/L1' has no entry in the params file's lines (its impedance is required).",
        ], ex2.Problems);
    }

    [Fact]
    public void T03c_a_duplicate_pathName_is_refused()
    {
        SclImportException ex = Refused(HandScd(duplicateNode: true), HandParams);
        Assert.Equal(["connectivity node pathName 'S1/V132/BB1/BB1' is repeated."], ex.Problems);
    }

    [Fact]
    public void T03d_a_top_level_line_with_equipment_and_an_unknown_params_path_are_refused()
    {
        string line = "<Line name=\"OHL1\"><ConductingEquipment type=\"LIN\" name=\"L\">" + T("T1", "S1/V132/BB1/BB1") + "</ConductingEquipment></Line>";
        SclImportException ex = Refused(HandScd(extraRoot: line), HandParams);
        Assert.Equal(["top-level Line 'OHL1' holds conducting equipment; Line sections are not imported, so its equipment would be lost."], ex.Problems);

        string badGen = HandParams.Replace("\"path\": \"S1/V132/GEN/G1\"", "\"path\": \"S1/V132/GEN/G9\"", StringComparison.Ordinal);
        SclImportException ex2 = Refused(HandScd(), badGen);
        Assert.Equal(
        [
            "$.generators[0]: 'S1/V132/GEN/G9' is not a generator (GEN) of the SCL file with a terminal on a node.",
            "GEN 'S1/V132/GEN/G1' has no entry in the params file's generators (its set point is required).",
        ], ex2.Problems);
    }

    [Fact]
    public void T03e_the_params_reader_refuses_unknown_and_repeated_members()
    {
        var unknown = Assert.Throws<InvalidDataException>(() => SclParams.Read(HandParams.Replace("\"format\"", "\"colour\": 1, \"format\"", StringComparison.Ordinal)));
        Assert.StartsWith("$.colour: unknown member (allowed: format, name, baseMva,", unknown.Message, StringComparison.Ordinal);
        var repeated = Assert.Throws<InvalidDataException>(() => SclParams.Read(HandParams.Replace("\"format\"", "\"format\": \"x\", \"format\"", StringComparison.Ordinal)));
        Assert.StartsWith("SCL params file is not valid JSON (or repeats a member): ", repeated.Message, StringComparison.Ordinal);
        var version = Assert.Throws<InvalidDataException>(() => SclParams.Read(HandParams.Replace("gridsim-scl-params/1", "gridsim-scl-params/2", StringComparison.Ordinal)));
        Assert.Equal("$.format: expected \"gridsim-scl-params/1\" but got \"gridsim-scl-params/2\".", version.Message);
    }

    // ---- 4. safe XML ------------------------------------------------------------------------------------------------

    [Fact]
    public void T04a_an_external_entity_is_refused_and_nothing_is_read_from_it()
    {
        string secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-WP30");
        string xxe = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE SCL [ <!ENTITY xxe SYSTEM "{new Uri(secret).AbsoluteUri}"> ]>
            <SCL xmlns="http://www.iec.ch/61850/2003/SCL"><Header id="&xxe;"/></SCL>
            """;
        string file = Path.Combine(_dir, "xxe.scd");
        File.WriteAllText(file, xxe);
        SclFormatException ex = Assert.Throws<SclFormatException>(() => SclReader.Read(file));
        Assert.Contains("has a DTD (<!DOCTYPE>)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-WP30", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void T04b_a_billion_laughs_entity_expansion_is_refused()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?>\n<!DOCTYPE SCL [\n  <!ENTITY lol0 \"lol\">\n");
        for (int i = 1; i <= 9; i++)
        {
            string previous = "&lol" + (i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
            sb.Append("  <!ENTITY lol").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" \"")
              .Append(string.Concat(Enumerable.Repeat(previous, 10))).Append("\">\n");
        }
        sb.Append("]>\n<SCL xmlns=\"http://www.iec.ch/61850/2003/SCL\"><Header id=\"&lol9;\"/></SCL>\n");
        SclFormatException ex = Assert.Throws<SclFormatException>(() => SclReader.ParseText(sb.ToString(), "laughs.scd"));
        Assert.StartsWith("laughs.scd: the file has a DTD (<!DOCTYPE>).", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T04c_deep_nesting_a_large_file_and_a_non_scl_root_are_refused()
    {
        string deep = "<SCL xmlns=\"http://www.iec.ch/61850/2003/SCL\">" + string.Concat(Enumerable.Repeat("<Private>", 70))
            + string.Concat(Enumerable.Repeat("</Private>", 70)) + "</SCL>";
        SclFormatException ex = Assert.Throws<SclFormatException>(() => SclReader.ParseText(deep, "deep.scd"));
        Assert.Equal("deep.scd: elements nest deeper than 64 levels (element 'Private'); refused.", ex.Message);

        // Exactly at the limit is read: the root is depth 0, so 64 nested elements reach depth 64.
        string atLimit = "<SCL xmlns=\"http://www.iec.ch/61850/2003/SCL\">" + string.Concat(Enumerable.Repeat("<Private>", 64))
            + string.Concat(Enumerable.Repeat("</Private>", 64)) + "</SCL>";
        Assert.Equal(64, SclReader.ParseText(atLimit).ElementCounts["Private"]);

        string file = Path.Combine(_dir, "big.scd");
        File.WriteAllText(file, HandScd());
        using (InputFileLimits.Scope(100))
            Assert.Throws<InputFileTooLargeException>(() => SclReader.Read(file));

        SclFormatException notScl = Assert.Throws<SclFormatException>(() => SclReader.ParseText("<CIM xmlns=\"urn:x\"/>", "x.xml"));
        Assert.StartsWith("x.xml: the root element is 'CIM'", notScl.Message, StringComparison.Ordinal);
    }

    // ---- 5. public sample (OpenSCD valid2007B4.scd, Apache-2.0) -------------------------------------------------------

    private static string Sample() => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "openscd-scl", "valid2007B4.scd");

    [Fact]
    public void T05_public_sample_counts_are_pinned_and_its_switches_without_terminals_are_refused()
    {
        SclDocument d = SclReader.Read(Sample());
        // The file's own hash, as recorded in ATTRIBUTION.txt.
        Assert.Equal("a0a5b2917b915f3bbbb54fef1ab6aa7618574d852dbefbb4e84cc3f3194ac4e6", d.Sha256);
        Assert.Equal(("2007", "B", "4", "TrainingIEC61850"), (d.Version, d.Revision, d.Release, d.HeaderId));
        // Counted by hand from the file (an independent XML parser gave the same numbers).
        Assert.Equal(1, d.ElementCounts["Substation"]);
        Assert.Equal(2, d.ElementCounts["VoltageLevel"]);
        Assert.Equal(3, d.ElementCounts["Bay"]);
        Assert.Equal(5, d.ElementCounts["ConductingEquipment"]);
        Assert.Equal(1, d.ElementCounts["ConnectivityNode"]);
        Assert.Equal(3, d.ElementCounts["Terminal"]);
        Assert.Equal(3, d.ElementCounts["IED"]);
        Assert.Equal(5, d.ElementCounts["LN0"]);
        Assert.Equal(29, d.ElementCounts["LN"]);
        Assert.Equal(3, d.ElementCounts["GSEControl"]);
        Assert.Equal(1, d.ElementCounts["{http://www.dummyURL.com/dummyNS}LNode"]);
        Assert.Equal(78, d.ElementCounts.Count);
        Assert.Equal(["CBR", "DIS", "DIS", "DIS", "DIS"], d.Equipment.Select(e => e.Type));
        Assert.Equal(["AA1/E1", "AA1/J1"], d.VoltageLevels.Select(v => v.Path));
        Assert.Equal(["AA1/E1/COUPLING_BAY/L2"], d.Nodes.Select(n => n.PathName));
        Assert.Equal(new Dictionary<string, int> { ["LNode"] = 3, ["Private"] = 1 }, d.NotImported);
        Assert.Equal(["IED1/CircuitBreaker_CB1/GCB", "IED1/CircuitBreaker_CB1/GCB2", "IED2/CBSW/GCB"], d.GooseControlBlocks);
        Assert.Equal(
        [
            ("Communication sections", 1), ("SubNetwork", 2), ("ConnectedAP", 3), ("IED", 3), ("logical nodes (LN0 and LN)", 34),
            ("SampledValueControl", 1), ("DataTypeTemplates sections", 1), ("DataTypeTemplates entries", 38),
        ], d.ReadPast);

        // Its circuit breaker and three of its disconnectors have no terminal on a node: their place in the topology is
        // unknown, so the import is refused, naming each. QC11 (one node, one grounded terminal) is an earth switch.
        SclImportException ex = Assert.Throws<SclImportException>(() => SclImporter.Import(d, SclParams.Read("{ \"format\": \"gridsim-scl-params/1\" }")));
        Assert.Equal(
        [
            "CBR 'AA1/E1/COUPLING_BAY/QA1' has 0 terminal(s) (0 grounded); a switch needs two terminals on connectivity nodes.",
            "DIS 'AA1/E1/COUPLING_BAY/QB1' has 0 terminal(s) (0 grounded); a switch needs two terminals on connectivity nodes, or one on a node and one grounded (an earth switch).",
            "DIS 'AA1/E1/COUPLING_BAY/QB2' has 0 terminal(s) (0 grounded); a switch needs two terminals on connectivity nodes, or one on a node and one grounded (an earth switch).",
            "DIS 'AA1/E1/COUPLING_BAY/QC21' has 1 terminal(s) (1 grounded); a switch needs two terminals on connectivity nodes, or one on a node and one grounded (an earth switch).",
            "switch 'AA1/E1/COUPLING_BAY/QC11' (earth switch) has no state: the SCL substation section holds none and the params file's switches give none. "
                + "Give its normalOpen in the params file, or import with --assume-closed to take every such switch as closed (each is then recorded in the output).",
        ], ex.Problems);
    }

    // ---- transformers -------------------------------------------------------------------------------------------------

    private const string TxScd = """
        <SCL xmlns="http://www.iec.ch/61850/2003/SCL" version="2007" revision="B" release="4">
          <Header id="wp30-tx"/>
          <Substation name="S3">
            <PowerTransformer name="T2" type="PTR">
              <TransformerWinding name="W1" type="PTW"><Terminal name="T1" connectivityNode="S3/HV/BB/HV" cNodeName="HV"/></TransformerWinding>
              <TransformerWinding name="W2" type="PTW"><Terminal name="T1" connectivityNode="S3/MV/BB/MV" cNodeName="MV"/></TransformerWinding>
              <TransformerWinding name="W3" type="PTW"><Terminal name="T1" connectivityNode="S3/LV/BB/LV" cNodeName="LV"/><Terminal name="N" connectivityNode="S3/LV/BB/grounded" cNodeName="grounded"/></TransformerWinding>
            </PowerTransformer>
            <VoltageLevel name="HV">
              <Voltage unit="V" multiplier="k">132</Voltage>
              <PowerTransformer name="T1" type="PTR">
                <TransformerWinding name="W1" type="PTW"><Terminal name="T1" connectivityNode="S3/HV/BB/HV" cNodeName="HV"/></TransformerWinding>
                <TransformerWinding name="W2" type="PTW"><Terminal name="T1" connectivityNode="S3/MV/BB/MV" cNodeName="MV"/></TransformerWinding>
              </PowerTransformer>
              <Bay name="BB"><ConnectivityNode name="HV" pathName="S3/HV/BB/HV"/></Bay>
              <Bay name="INF"><ConductingEquipment type="IFL" name="GRID"><Terminal name="T1" connectivityNode="S3/HV/BB/HV" cNodeName="HV"/></ConductingEquipment></Bay>
            </VoltageLevel>
            <VoltageLevel name="MV">
              <Voltage unit="V" multiplier="">33000</Voltage>
              <Bay name="BB"><ConnectivityNode name="MV" pathName="S3/MV/BB/MV"/></Bay>
            </VoltageLevel>
            <VoltageLevel name="LV">
              <Voltage unit="V" multiplier="k">11</Voltage>
              <Bay name="BB"><ConnectivityNode name="LV" pathName="S3/LV/BB/LV"/></Bay>
            </VoltageLevel>
          </Substation>
        </SCL>
        """;

    private const string TxParams = """
        {
          "format": "gridsim-scl-params/1",
          "transformers": [ { "path": "S3/HV/T1", "hv": "W1", "lv": "W2", "rPu": 0.002, "xPu": 0.08, "bPu": 0.001, "tapRatio": 1.025, "rateAMva": 90 } ],
          "transformers3w": [ { "path": "S3/T2", "hv": "W1", "mv": "W2", "lv": "W3", "r12Pu": 0.001, "x12Pu": 0.1, "r13Pu": 0.002, "x13Pu": 0.2, "r23Pu": 0.0015, "x23Pu": 0.12 } ],
          "infeeds": [ { "path": "S3/HV/INF/GRID", "vPu": 1.0 } ],
          "loads": [ { "id": "LDM", "node": "S3/MV/BB/MV", "pMw": 20, "qMvar": 5 }, { "id": "LDL", "node": "S3/LV/BB/LV", "pMw": 10, "qMvar": 2 } ]
        }
        """;

    [Fact]
    public void T06_two_and_three_winding_transformers_map_with_their_sidecar_impedances()
    {
        SclImportResult r = SclImporter.Import(SclReader.ParseText(TxScd), SclParams.Read(TxParams));
        SubstationModel m = r.Model;
        Assert.Equal(new NbTransformer("S3/HV/T1", "S3/HV/BB/HV", "S3/MV/BB/MV", 0.002, 0.08, 0.001, 1.025, 0, 90), Assert.Single(m.Transformers));
        NbTransformer3W t3 = Assert.Single(m.Transformers3W);
        Assert.Equal(("S3/T2", "S3/HV/BB/HV", "S3/MV/BB/MV", "S3/LV/BB/LV"), (t3.Id, t3.NodeHv, t3.NodeMv, t3.NodeLv));
        Assert.Equal((0.001, 0.1, 0.002, 0.2, 0.0015, 0.12, 0.0), (t3.R12, t3.X12, t3.R13, t3.X13, t3.R23, t3.X23, t3.B));
        Assert.Null(t3.Ratio);
        // 33000 V with no multiplier is 33 kV; 132 kV and 11 kV are written with multiplier k.
        Assert.Equal([132.0, 33.0, 11.0], m.Substations.Single().VoltageLevels.Select(v => v.NominalKv));
        Assert.Contains("winding 'S3/T2/W3': its grounded terminal (a neutral earth) is not modelled; the power flow is balanced.", r.Notes);
        Assert.True(SubstationProjection.ProjectNormal(m).StarPoints.ContainsKey("S3/T2"));
    }

    [Fact]
    public void T06b_transformer_refusals_name_the_entry()
    {
        string wrongCount = TxParams.Replace("\"path\": \"S3/HV/T1\"", "\"path\": \"S3/T2\"", StringComparison.Ordinal);
        SclImportException ex = Refused(TxScd, wrongCount);
        Assert.Equal(
        [
            "$.transformers[0]: power transformer 'S3/T2' has 3 windings, not 2.",
            "$.transformers3w[0]: power transformer 'S3/T2' is given twice.",
            "power transformer 'S3/HV/T1' (2 windings) has no entry in the params file's transformers (its impedance is required).",
        ], ex.Problems);

        string badWinding = TxParams.Replace("\"lv\": \"W2\", \"rPu\"", "\"lv\": \"W7\", \"rPu\"", StringComparison.Ordinal);
        Assert.NotEqual(TxParams, badWinding);
        SclImportException ex2 = Refused(TxScd, badWinding);
        Assert.Equal(["$.transformers[0]: power transformer 'S3/HV/T1' has no winding named 'W7'."], ex2.Problems);
    }

    // ---- 9. review fixes: unstated switches, hidden equipment, CT across levels, params ranges, voltage rounding --------

    private static string Msg(string path, string kind) =>
        $"switch '{path}' ({kind}) has no state: the SCL substation section holds none and the params file's switches give none. "
        + "Give its normalOpen in the params file, or import with --assume-closed to take every such switch as closed (each is then recorded in the output).";

    [Fact]
    public void T09a_a_switch_with_no_state_is_refused_by_default_each_named()
    {
        SclImportException ex = Refused(HandScd(), HandParamsUnstated);
        Assert.Equal(
        [
            Msg("S1/V132/CPL/QA1", "breaker"),
            Msg("S1/V132/FDR/QB1", "disconnector"),
            Msg("S1/V132/FDR/QA1", "breaker"),
            Msg("S1/V132/FDR/QB9", "disconnector"),
        ], ex.Problems);
    }

    [Fact]
    public void T09b_assume_closed_takes_them_closed_and_records_each_in_the_written_file()
    {
        SclImportResult r = SclImporter.Import(SclReader.ParseText(HandScd()), SclParams.Read(HandParamsUnstated), assumeClosed: true);
        string[] defaulted = ["S1/V132/CPL/QA1", "S1/V132/FDR/QB1", "S1/V132/FDR/QA1", "S1/V132/FDR/QB9"];
        Assert.Equal(defaulted, r.DefaultClosedSwitches);
        string[] expected = defaulted.Select(s => $"scl-import: switch '{s}' has no state in the params file (the SCL substation section holds none); assumed closed (--assume-closed).").ToArray();
        Assert.Equal(expected, r.Model.Notes);
        // Every switch state is as in the fully stated import; only the notes differ.
        Assert.Equal(ImportHand().Model.AllSwitches, r.Model.AllSwitches);
        string written = SubstationJson.Write(r.Model);
        using (JsonDocument d = JsonDocument.Parse(written))
            Assert.Equal(expected, d.RootElement.GetProperty("notes").EnumerateArray().Select(e => e.GetString()!));
        // The notes round-trip, and a model without notes writes no notes member.
        Assert.Equal(expected, SubstationJson.Read(written).Notes);
        Assert.Equal(written, SubstationJson.Write(SubstationJson.Read(written)));
        Assert.DoesNotContain("\"notes\"", SubstationJson.Write(ImportHand().Model), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => SubstationJson.Read(written.Replace("\"scl-import: switch 'S1/V132/CPL/QA1'", "1, \"x", StringComparison.Ordinal)));
    }

    [Fact]
    public void T09c_the_verb_refuses_unstated_switches_and_records_them_with_assume_closed()
    {
        string w = Path.Combine(_dir, "assume");
        Directory.CreateDirectory(w);
        File.WriteAllText(Path.Combine(w, "s.scd"), HandScd());
        File.WriteAllText(Path.Combine(w, "p.json"), HandParamsUnstated);
        CliProcess.Result refused = JournalTestSupport.Run(w, "scl-import", "s.scd", "--params", "p.json", "--out", "a.json");
        Assert.True(refused.ExitCode == 1, refused.ToString());
        Assert.Contains("error: scl-import: switch 'S1/V132/CPL/QA1' (breaker) has no state", refused.StdErr, StringComparison.Ordinal);
        Assert.Contains("error: scl-import: switch 'S1/V132/FDR/QB9' (disconnector) has no state", refused.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(w, "a.json")));

        CliProcess.Result ok = JournalTestSupport.Run(w, "scl-import", "s.scd", "--params", "p.json", "--out", "b.json", "--assume-closed");
        Assert.True(ok.ExitCode == 0, ok.ToString());
        string written = File.ReadAllText(Path.Combine(w, "b.json"));
        SubstationModel m = SubstationJson.Read(written);
        Assert.Equal(4, m.Notes.Count);
        Assert.Contains("scl-import: switch 'S1/V132/FDR/QB9' has no state in the params file (the SCL substation section holds none); assumed closed (--assume-closed).", m.Notes);
        Assert.Equal(SubstationJson.Write(SclImporter.Import(SclReader.ParseText(HandScd()), SclParams.Read(HandParamsUnstated), assumeClosed: true).Model), written);
        Assert.Contains("note: switch 'S1/V132/CPL/QA1' has no state in the params file", ok.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void T09d_equipment_inside_a_function_is_refused_by_name()
    {
        string inBay = "<Function name=\"F1\"><SubFunction name=\"SF1\"><ConductingEquipment type=\"CAP\" name=\"C1\">"
            + T("T1", "S1/V132/BB2/BB2") + "</ConductingEquipment></SubFunction></Function>";
        SclImportException ex = Refused(HandScd(extraGenBay: inBay), HandParams);
        Assert.Equal(["Function 'F1' in bay 'S1/V132/GEN' holds conducting equipment or a power transformer; Function elements are not imported, so that equipment would be lost; refused."],
            ex.Problems);

        // A Function under the substation holding a power transformer is refused the same way.
        string scd = HandScd().Replace("<Substation name=\"S2\">",
            "<Substation name=\"S2\"><Function name=\"F2\"><PowerTransformer name=\"T9\" type=\"PTR\"/></Function>", StringComparison.Ordinal);
        Assert.NotEqual(HandScd(), scd);
        SclImportException ex2 = Refused(scd, HandParams);
        Assert.Equal(["Function 'F2' in substation 'S2' holds conducting equipment or a power transformer; Function elements are not imported, so that equipment would be lost; refused."],
            ex2.Problems);
        // The hand file's own Function holds only an LNode: listed as NOT IMPORTED, not refused (T01c).
        Assert.Empty(SclReader.ParseText(HandScd()).SkippedWithEquipment);
    }

    private const string TwoLevelCtScd = """
        <SCL xmlns="http://www.iec.ch/61850/2003/SCL" version="2007" revision="B" release="4">
          <Header id="wp30-ct"/>
          <Substation name="S4">
            <VoltageLevel name="HV">
              <Voltage unit="V" multiplier="k">132</Voltage>
              <Bay name="B"><ConnectivityNode name="N1" pathName="S4/HV/B/N1"/>
                <ConductingEquipment type="CTR" name="BA1"><Terminal name="T1" connectivityNode="S4/HV/B/N1" cNodeName="N1"/><Terminal name="T2" connectivityNode="S4/MV/B/N2" cNodeName="N2"/></ConductingEquipment>
                <ConductingEquipment type="CTR" name="BA2"><Terminal name="T1" connectivityNode="S4/HV/B/N1" cNodeName="N1"/><Terminal name="T2" connectivityNode="S4/HV/B/NX" cNodeName="NX"/></ConductingEquipment>
              </Bay>
            </VoltageLevel>
            <VoltageLevel name="MV">
              <Voltage unit="V" multiplier="k">33</Voltage>
              <Bay name="B"><ConnectivityNode name="N2" pathName="S4/MV/B/N2"/></Bay>
            </VoltageLevel>
          </Substation>
        </SCL>
        """;

    [Fact]
    public void T09e_a_series_ct_across_voltage_levels_is_refused_and_an_undefined_ct_node_is_reported_once()
    {
        SclImportException ex = Refused(TwoLevelCtScd, "{ \"format\": \"gridsim-scl-params/1\" }");
        Assert.Equal(
        [
            "current transformer 'S4/HV/B/BA1' joins node 'S4/HV/B/N1' (voltage level 'S4/HV') and node 'S4/MV/B/N2' (voltage level 'S4/MV'); a series current transformer has no ratio, so it cannot join two voltage levels; refused.",
            "CTR 'S4/HV/B/BA2': terminal 'T2' names connectivity node 'S4/HV/B/NX', which the substation section does not define.",
        ], ex.Problems);
    }

    [Theory]
    [InlineData("\"generators\": [ { \"path\": \"G\", \"pMw\": 1, \"vPu\": 0 } ]", "$.generators[0].vPu: expected a number above 0.")]
    [InlineData("\"infeeds\": [ { \"path\": \"G\", \"vPu\": -1 } ]", "$.infeeds[0].vPu: expected a number above 0.")]
    [InlineData("\"transformers\": [ { \"path\": \"T\", \"hv\": \"W1\", \"lv\": \"W2\", \"rPu\": 0.01, \"xPu\": 0.1, \"tapRatio\": 0 } ]", "$.transformers[0].tapRatio: expected a number above 0.")]
    [InlineData("\"transformers\": [ { \"path\": \"T\", \"hv\": \"W1\", \"lv\": \"W2\", \"rPu\": 0, \"xPu\": 0 } ]", "$.transformers[0]: rPu and xPu are both 0; a zero impedance is not a branch (use a switch).")]
    [InlineData("\"lines\": [ { \"ends\": [\"L\"], \"rPu\": 0, \"xPu\": 0, \"bPu\": 0 } ]", "$.lines[0]: rPu and xPu are both 0; a zero impedance is not a branch (use a switch).")]
    [InlineData("\"lines\": [ { \"ends\": [\"L\"], \"rPu\": 0.01, \"xPu\": 0.1, \"bPu\": 0, \"rateAMva\": -1 } ]", "$.lines[0].rateAMva: expected a number at least 0.")]
    [InlineData("\"transformers3w\": [ { \"path\": \"T\", \"hv\": \"W1\", \"mv\": \"W2\", \"lv\": \"W3\", \"r12Pu\": 0, \"x12Pu\": 0.1, \"r13Pu\": 0, \"x13Pu\": 0, \"r23Pu\": 0, \"x23Pu\": 0.1 } ]", "$.transformers3w[0]: r13Pu and x13Pu are both 0; a zero impedance is not a branch (use a switch).")]
    [InlineData("\"generators\": [ { \"path\": \"G\", \"pMw\": 1, \"vPu\": 1, \"qMinMvar\": 5, \"qMaxMvar\": 1 } ]", "$.generators[0]: qMinMvar must not be above qMaxMvar.")]
    [InlineData("\"generators\": [ { \"path\": \"G\", \"pMw\": 1, \"vPu\": 1, \"pMinMw\": 5, \"pMaxMw\": 1 } ]", "$.generators[0]: pMinMw must not be above pMaxMw.")]
    public void T09f_params_values_out_of_range_are_refused_naming_the_json_path(string member, string message)
    {
        var ex = Assert.Throws<InvalidDataException>(() => SclParams.Read("{ \"format\": \"gridsim-scl-params/1\", " + member + " }"));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void T09g_a_voltage_in_volts_or_megavolts_is_rounded_once()
    {
        // A fractional volt value shows the double rounding: 33000.1 V parsed, then divided by 1000, is
        // 33.000099999999996, while the double nearest 33.0001 kV is 33.0001 (Python: 33000.1 / 1000 and
        // float("33000.1e-3")). The importer shifts the decimal exponent in the text and parses once, so it gives 33.0001,
        // also from the exponent form; 0.1 MV gives 100 kV.
        string Scd(string text, string mult) => TxScd.Replace("<Voltage unit=\"V\" multiplier=\"\">33000</Voltage>",
            $"<Voltage unit=\"V\" multiplier=\"{mult}\">{text}</Voltage>", StringComparison.Ordinal);
        double Mv(string text, string mult) => SclImporter.Import(SclReader.ParseText(Scd(text, mult)), SclParams.Read(TxParams)).Model.Substations.Single().VoltageLevels[1].NominalKv;
        Assert.NotEqual(33.0001, double.Parse("33000.1", System.Globalization.CultureInfo.InvariantCulture) / 1000);
        Assert.Equal(33.0001, Mv("33000.1", ""));
        Assert.Equal(33.0001, Mv("3.30001e4", ""));
        Assert.Equal(100.0, Mv("0.1", "M"));
        Assert.Equal(33.0, Mv("33000", ""));
    }

    // ---- the verb -----------------------------------------------------------------------------------------------------

    private static string WriteHand(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "s.scd"), HandScd());
        File.WriteAllText(Path.Combine(dir, "p.json"), HandParams);
        return dir;
    }

    [Fact]
    public void T07_the_verb_writes_the_library_model_and_prints_the_report()
    {
        string w = WriteHand(Path.Combine(_dir, "verb"));
        CliProcess.Result r = JournalTestSupport.Run(w, "scl-import", "s.scd", "--params", "p.json", "--out", "case.substations.json");
        Assert.True(r.ExitCode == 0, r.ToString());
        string written = File.ReadAllText(Path.Combine(w, "case.substations.json"));
        Assert.Equal(SubstationJson.Write(SubstationJson.Read(HandDirect)), written);
        Assert.Contains("  projection at these switch states: 2 bus(es), 1 branch(es)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("NOT IMPORTED: Function x1", r.StdOut, StringComparison.Ordinal);
        string paramsSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(w, "p.json"))));
        Assert.Contains("params sha256 " + paramsSha, r.StdOut, StringComparison.Ordinal);

        // The output is never replaced (usage error), and nothing is written on a refusal.
        CliProcess.Result again = JournalTestSupport.Run(w, "scl-import", "s.scd", "--params", "p.json", "--out", "case.substations.json");
        Assert.True(again.ExitCode == 2, again.ToString());
        File.WriteAllText(Path.Combine(w, "bad.json"), "{ \"format\": \"gridsim-scl-params/1\" }");
        CliProcess.Result refused = JournalTestSupport.Run(w, "scl-import", "s.scd", "--params", "bad.json", "--out", "x.json");
        Assert.True(refused.ExitCode == 1, refused.ToString());
        Assert.Contains("error: scl-import: LIN 'S1/V132/FDR/L1' has no entry in the params file's lines", refused.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(w, "x.json")));
    }

    [Theory]
    [InlineData("--params", "p.json")]
    [InlineData("--out", "o.json")]
    public void T07b_the_verb_refuses_a_missing_flag_with_exit_2(string flag, string value)
    {
        string w = WriteHand(Path.Combine(_dir, "usage-" + flag.TrimStart('-')));
        CliProcess.Result r = JournalTestSupport.Run(w, "scl-import", "s.scd", flag, value);
        Assert.True(r.ExitCode == 2, r.ToString());
    }

    [Fact]
    public void T08_the_journal_hashes_both_inputs_and_validate_re_runs_the_job_identical()
    {
        Assert.Null(ReplayPolicy.Refusal("scl-import", ["scl-import", "s.scd", "--params", "p.json", "--out", "o.json"], new HashSet<string>(StringComparer.Ordinal)));
        string w = WriteHand(Path.Combine(_dir, "journal"));
        string journal = Path.Combine(_dir, "j", "journal.jsonl");
        CliProcess.Result run = JournalTestSupport.Run(w, "--journal", journal, "scl-import", "s.scd", "--params", "p.json", "--out", "o.json");
        Assert.True(run.ExitCode == 0, run.ToString());
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        string scdSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(w, "s.scd"))));
        string paramsSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(w, "p.json"))));
        Assert.Contains(e.Tokens, t => t.Kind == "input" && t.Raw == "s.scd" && t.Sha256 == scdSha);
        Assert.Contains(e.Tokens, t => t.Kind == "input" && t.Raw == "p.json" && t.Sha256 == paramsSha);

        JournalTestSupport.FromAnotherBuild(journal);
        string report = Path.Combine(_dir, "v.json");
        CliProcess.Result v = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--json", report);
        Assert.True(v.ExitCode == 0, v.ToString());
        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(report));
        JsonElement entry = Assert.Single(d.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray());
        Assert.Equal(ReplayStatus.Identical, entry.GetProperty("status").GetString());
    }
}
