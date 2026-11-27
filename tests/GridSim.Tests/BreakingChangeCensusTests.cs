using System.Reflection;
using System.Text.Json;
using GridSim.Cli;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Journal;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using GridSim.Tests.Substations;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (run journal), T7, the volatile-field census. The fields left out of the bit-for-bit comparison are derived from
/// the result schemas (src/GridSim.Core/Journal/volatile-fields.json); this census checks that the list is complete.
/// Every writer verb that <c>breaking-change validate</c> re-runs is run once with a journal, in one folder, and then
/// re-run by <c>validate</c> itself: on the same build, in another folder (the scratch folder), at a later time. Every
/// entry must be IDENTICAL. A field the list misses (a time, a host name, a build stamp) shows as CHANGED and fails the
/// census, naming the verb and the field.
///
/// The corpus is split into four classes so each runs within a few minutes on the 8 GB build box.
/// </summary>
internal static class CensusCorpus
{
    /// <summary>The writer verbs validate never re-runs, with why (their journal entries are CANNOT-REPLAY).</summary>
    internal static readonly string[] NeverWriterVerbs = ["convert", "cgmes-export", "cycle", "breaking-change"];

    private static readonly Lazy<(string Dir, List<(string Name, string[] Args)> Commands)> Built = new(Build);

    internal static List<(string Name, string[] Args)> Commands => Built.Value.Commands;

    internal static string FixtureDir => Built.Value.Dir;

    private static string Root => CliProcess.Root();

    private static string Data(params string[] parts) => Path.Combine(new[] { Root, "data" }.Concat(parts).ToArray());

    private static string Fixture(params string[] parts) => Path.Combine(new[] { Root, "tests", "GridSim.Tests", "fixtures" }.Concat(parts).ToArray());

    private static (string, List<(string, string[])>) Build()
    {
        string fx = JournalTestSupport.NewDir("census-fixtures");
        string W(string name, string text)
        {
            string p = Path.Combine(fx, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            return p;
        }

        string ansi = W("ansi.json", """{"schema":"gridsim-ansi-data/1","sources":[{"bus":1,"name":"G1","class":"test","xOverR":40,"local":true,"firstCycleMultiplier":1,"interruptingMultiplier":1.0},{"bus":2,"name":"G2","class":"test","xOverR":40,"local":true,"firstCycleMultiplier":1,"interruptingMultiplier":1.0},{"bus":3,"name":"G3","class":"test","xOverR":40,"local":true,"firstCycleMultiplier":1,"interruptingMultiplier":1.0}]}""");
        string i61363 = W("i61363.json", """{"schema":"gridsim-iec61363-data/1","generators":[{"bus":1,"xdTransPu":0.3,"xdSyncPu":1.2,"tdSubS":0.05,"tdTransS":1.0,"taS":0.04},{"bus":2,"xdTransPu":0.3,"xdSyncPu":1.2,"tdSubS":0.05,"tdTransS":1.0,"taS":0.04},{"bus":3,"xdTransPu":0.3,"xdSyncPu":1.2,"tdSubS":0.05,"tdTransS":1.0,"taS":0.04}],"motors":[]}""");
        string dc = W("dc.json", """{"schema":"gridsim-dc-network/1","branches":[{"name":"cab","from":"1","to":"2","rOhm":0.03,"lH":0.002}],"sources":[{"name":"B1","kind":"battery","node":"1","eV":110,"rOhm":0.02,"lH":0.001}],"fault":{"node":"2","rOhm":0}}""");
        W(Path.Combine("models", "machines.json"), """
            {"format":"gridsim-modal-machines/1","machines":[
              {"bus":1,"model":"flux-decay","xdp":0.0608,"xd":0.5,"xq":0.4,"tdop":8.0,"damping":2.0,"exciter":{"model":"SEXS","parameters":{"K":20,"Ta":1,"Tb":1}}},
              {"bus":2,"model":"flux-decay","xdp":0.1198,"xd":0.9,"xq":0.8,"tdop":6.0,"damping":2.0,"exciter":{"model":"SEXS","parameters":{"K":20,"Ta":1,"Tb":1}}},
              {"bus":3,"model":"classical","xdp":0.1813,"damping":2.0,"governor":{"model":"TGOV1"}}
            ]}
            """);
        string mesh = W("mesh.json", """
            {"schema":"gridsim-dist3ph-mesh/1","name":"loop",
             "nodes":[{"name":"S","wires":["a","n"]},{"name":"L","wires":["a","n"]}],
             "sources":[{"node":"S","wires":["a"],"magnitude_v":[1000],"angle_deg":[0]}],
             "branches":[{"name":"line","from":"S","to":"L","wires":["a","n"],"r_ohm":[[2,0],[0,0.5]],"x_ohm":[[3,0],[0,0.5]]}],
             "loads":[{"name":"ld","node":"L","connection":"line-line","from":"a","to":"n","nominal_v":1000,"p_w":[100000],"q_var":[0],"zip":[0,0,1]}],
             "earthing":[{"node":"S","wire":"n","r_ohm":0}]}
            """);
        W("chain.network.json", """
            { "schema": "gridsim-network/2",
              "buses": [ { "id": 1, "name": "G", "baseKv": 20, "type": "slack", "vm": 1.0 }, { "id": 2, "name": "A", "baseKv": 132, "type": "pq" },
                         { "id": 3, "name": "B", "baseKv": 132, "type": "pq" }, { "id": 4, "name": "L", "baseKv": 20, "type": "pq" } ],
              "generators": [ { "name": "G1", "bus": 1, "type": "", "capacityMva": 100, "outputMw": 0, "voltageSetpoint": 1.0, "role": "slack", "subtransientXdPu": 0.2 } ],
              "demands": [ { "name": "load", "bus": 4, "pMw": 60, "qMvar": 20 } ],
              "lines": [ { "name": "L23", "from": 2, "to": 3, "rPu": 0.01, "xPu": 0.2, "ratingMva": 100 } ],
              "transformers": [
                { "name": "T1", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1, "ratingMva": 100, "windings": { "from": "Delta", "to": "WyeGrounded" } },
                { "name": "T2", "from": 3, "to": 4, "rPu": 0, "xPu": 0.15, "ratingMva": 100, "windings": { "from": "WyeGrounded", "to": "Delta" } } ] }
            """);
        string chain = W("chain.case.json", """{ "schema": "gridsim-case/2", "name": "chain4", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["chain.network.json"] }""");
        string devices = W("devices.json", """
            { "manifestVersion": "gridsim-devices/1", "devices": [
              { "id": "R1", "type": "relay", "branch": { "index": 0, "from": 1, "to": 4, "end": "from" },
                "ct": { "primaryA": 1000, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 0.1, "timeDial": 0.2 } ] },
              { "id": "R2", "type": "relay", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
                "shots": [ { "curve": "ieee-vi", "pickupA": 50, "timeDial": 0.1 } ] },
              { "id": "Z1", "type": "relay", "function": "21", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
                "shots": [ { "curve": "iec-si", "pickupA": 1 } ] } ] }
            """);
        string devices9 = W("devices9.json", """
            { "manifestVersion": "gridsim-devices/1", "devices": [
              { "id": "Z45", "type": "relay", "function": "21", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
                "ct": { "primaryA": 1000, "secondaryA": 1 }, "vt": { "primaryV": 345000, "secondaryV": 110 },
                "distance": { "characteristic": "mho", "zones": [ { "reachOhms": 10, "angleDeg": 80, "timeS": 0 }, { "reachOhms": 20, "angleDeg": 80, "timeS": 0.4 } ] } },
              { "id": "D45", "type": "relay", "function": "87", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
                "ct": { "primaryA": 1000, "secondaryA": 1 }, "differential": { "is1A": 0.2, "k1": 0.3, "is2A": 2.0, "k2": 1.5 } },
              { "id": "O45", "type": "relay", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
                "ct": { "primaryA": 1000, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 0.5, "timeDial": 0.1 } ] } ] }
            """);
        string audit = W("audit.json", """{ "manifestVersion": "gridsim-protection-audit/1", "case": "case9", "devices": "devices.json", "coordinationTimeIntervalS": 0.3 }""");
        string harm = W("harm.json", """{ "schemaVersion": 1, "study": "harmonic-assess", "pcc": { "lineKv": 11, "sscMva": 60, "xOverR": 10 }, "equipment": [ { "id": "drive", "technology": "active-front-end", "ratingKva": 600 } ] }""");
        string flicker = W("flicker.json", """{ "schemaVersion": 1, "study": "flicker", "nominalKv": 11, "sources": [ { "id": "A", "pst": 0.5, "plt": 0.4 } ], "flickermeter": { } }""");
        string filter = W("filter.json", """{ "schemaVersion": 1, "study": "filter-design", "filters": [ { "id": "F5", "kind": "single-tuned", "voltageKv": 11, "fundamentalMvar": 5, "tunedOrder": 4.9 } ], "network": { "source": { "sscMva": 250, "xOverR": 1000 } } }""");
        string conn = W("conn.json", """{ "schemaVersion": 1, "label": "x", "pccVoltageKv": 11, "quantities": { } }""");
        string prob = W("prob.json", """{ "schemaVersion": 1, "study": "adequacy", "seed": 7, "units": [ {"id":"g1","capacityMw":100,"availability":0.9}, {"id":"g2","capacityMw":100,"availability":0.9}, {"id":"g3","capacityMw":100,"availability":0.9} ], "load": { "constantMw": 150, "hours": 1 }, "methods": [ {"method":"convolution","samples":1} ] }""");
        string ra = W("ra.json", """{ "x": 1.0, "y": [1, 2, 3] }"""), rb = W("rb.json", """{ "x": 1.0000000000000002, "y": [1, 2, 3] }""");
        string shapes = W("shapes.ttl", """
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            @prefix cim: <http://iec.ch/TC57/2013/CIM-schema-cim16#> .
            @prefix ex: <http://example.org/> .
            ex:S a sh:NodeShape ; sh:targetClass cim:ACLineSegment ; sh:property [ sh:path cim:IdentifiedObject.name ; sh:minCount 1 ] .
            """);
        string geometry = W("geometry.json", R404LineConstantsCliTests.ThreePhaseGeometry);
        string qsts = W("qsts.json", QstsCliProcessTests.Study33);
        string substations = W("substations.json", SubstationJson.Write(SubstationFixtures.TwoFeed()));
        W("twobus.m", string.Join("\n",
            "function mpc = twobus", "mpc.version = '2';", "mpc.baseMVA = 100;",
            "mpc.bus = [", "1 2 0 0 0 0 1 1 0 33 1 1.1 0.9;", "2 3 0 0 0 0 1 1 0 33 1 1.1 0.9;", "];",
            "mpc.gen = [", "1 100 0 50 -50 1 100 1 100 0;", "2 0 0 999 -999 1 100 1 0 0;", "];",
            "mpc.branch = [", "1 2 0 0.1 0 0 0 0 0 0 1 -360 360;", "];", ""));
        string gridCode = W("gridcode.json", """
            { "schema": "gridsim-gridcode-study/1", "label": "two-bus", "case": "twobus.m", "unit": { "bus": 1 }, "pccBus": 2, "plantBuses": [1],
              "code": "gb-ecc", "moduleType": "B", "technology": "synchronous", "connectionKv": 33,
              "checks": [ { "id": "q-capability" },
                          { "id": "frequency-relays", "stages": [ { "device": "81U-1", "function": "81U", "pickup": 47.0, "delaySeconds": 0.5 } ] } ] }
            """);

        // A synchrophasor capture (C37.118: one CONFIG, two DATA frames, historical timestamps) for pmu-ingest.
        const uint oldSoc = 1_700_000_000;
        var pmu = new Doc15Frames.Pmu(101, "STN_A", new[] { Doc15Frames.Voltage });
        string capture = Path.Combine(fx, "capture.bin");
        File.WriteAllBytes(capture, new[]
        {
            Doc15Frames.Config(1_000_000, oldSoc, pmu),
            Doc15Frames.Data(oldSoc, 0, new[] { (9_500.0f, 0.0f) }),
            Doc15Frames.Data(oldSoc, 20_000, new[] { (9_500.0f, 0.0f) }),
        }.SelectMany(x => x).ToArray());

        // A measurement set of case9's own solved state, for pmu-estimate.
        GridModel m9 = IeeeCases.Case9();
        PowerFlowResult pf9 = new NewtonRaphsonPowerFlow().Solve(m9);
        var rows = new List<Measurement>();
        for (int i = 0; i < pf9.BusIds.Count; i++)
        {
            rows.Add(new Measurement(MeasurementKind.VmPu, pf9.Vm[i], 0.001, Bus: pf9.BusIds[i], Source: "pmu"));
            rows.Add(new Measurement(MeasurementKind.VaDeg, pf9.VaDeg[i], 0.05, Bus: pf9.BusIds[i], Source: "pmu"));
        }
        string pmuSet = Path.Combine(fx, "pmu9.json");
        new MeasurementSet { Case = "case9", BaseMva = 100.0, TakenUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Measurements = rows }.Save(pmuSet);

        // The study-case and reliability-network studies, built by their own tests' helpers.
        string scRoot = Path.Combine(fx, "studycase");
        Directory.CreateDirectory(scRoot);
        string twoBus = R401Cases.TwoBus(scRoot);
        string studyCase = W(Path.Combine("studycase", "w1.json"), R401Cases.TwoBusStudyCase(R401Cases.Bundle(twoBus)));
        string reliability = ReliabilityStudy();

        string cgmes = Fixture("cgmes", "p3-coupler");
        var c = new List<(string, string[])>
        {
            ("dcopf", ["dcopf", "case9"]),
            ("acopf", ["acopf", "case9"]),
            ("cpf", ["cpf", "case9"]),
            ("taps", ["taps", Data("uk")]),
            ("ptdf", ["ptdf", "case14", "--bus", "9", "--top", "2"]),
            ("lodf", ["lodf", "case14", "--branch", "3"]),
            ("iec60909", ["iec60909", "case9", "--bus", "5"]),
            ("g74", ["g74", "case9", "--bus", "5"]),
            ("ansi-sc", ["ansi-sc", "case9", "--bus", "5", "--ansi-data", ansi]),
            ("sc-dc", ["sc-dc", dc]),
            ("sc-61363", ["sc-61363", "case9", "--bus", "5", "--iec61363-data", i61363]),
            ("modal-full", ["modal-full", "case9", "--models", Path.Combine(fx, "models")]),
            ("fault", ["fault", "case9", "--bus", "5"]),
            ("protection-check", ["protection-check", "case9", "--fault-bus", "5"]),
            ("grade", ["grade", "--chain", "iec-si:100:0.1,iec-si:5000:0.3", "--current", "1000"]),
            ("nminus1", ["nminus1", "case14"]),
            ("nminus2", ["nminus2", "case9"]),
            ("n-1-1", ["n-1-1", "case9"]),
            ("freq", ["freq"]),
            ("domains", ["domains", "case14"]),
            ("dist3ph", ["dist3ph", Data("dist3ph", "ieee4-gyGy-stepdown-unbalanced.json")]),
            ("losses", ["losses", "case14"]),
            ("physics-check", ["physics-check", "case9"]),
            ("experimental", ["experimental", "wave", "case9"]),
            ("dcpf", ["dcpf", "case14"]),
            ("validate", ["validate", Fixture("v2", "kinds", "exciter.kind.json"), "--schemas", Path.Combine(Root, "schemas", "v2")]),
            ("zonal", ["zonal", "pypsa-gb-zonal"]),
            ("pf", ["pf", "case14"]),
            ("restore", ["restore", "case9", "--order", "#1,#2"]),
            ("pmu-ingest", ["pmu-ingest", capture]),
            ("pmu-estimate", ["pmu-estimate", "case9", "--measurements", pmuSet]),
            ("acopf-full", ["acopf-full", "case9"]),
            ("scuc", ["scuc", "case9"]),
            ("scopf", ["scopf", "case9"]),
            ("inertia-placement", ["inertia-placement", "uk", "--placements", "fleet,spine-t", "--budgets", "200,400"]),
            ("qsts", ["qsts", "case33bw.m", "--study", qsts]),
            ("hosting", ["hosting", "case33bw.m", "--buses", "6,18", "--max-mw", "10", "--tolerance-mw", "0.01", "--threads", "2"]),
            ("sensitivity", ["sensitivity", "case9", "--kind", "dvdq", "--bus", "5"]),
            ("qv-curve", ["qv-curve", "case9", "--bus", "5", "--vmin", "0.3"]),
            ("pv-contingency", ["pv-contingency", "case9", "--branches", "0,1"]),
            ("dc-contingency", ["dc-contingency", "case9"]),
            ("fault-level-n1", ["fault-level-n1", "case9", "--buses", "5,7", "--objective", "min"]),
            ("line-constants", ["line-constants", geometry]),
            ("dist3ph-mesh", ["dist3ph-mesh", mesh]),
            ("emt3", ["emt3", Path.Combine(Root, "docs", "examples", "emt3", "machine-short-circuit.json")]),
            ("devices-check", ["devices-check", "case9", "--devices", devices]),
            ("fault-line", ["fault-line", "case9", "--devices", devices, "--branch", "0", "--fractions", "0,0.5,1"]),
            ("sc-trace", ["sc-trace", "case9", "--devices", devices, "--bus", "5"]),
            ("protection-audit", ["protection-audit", audit]),
            ("fault-series", ["fault-series", chain, "--branch", "0", "--open", "1"]),
            ("fault-simultaneous", ["fault-simultaneous", chain, "--at", "2:slg,3:ll"]),
            ("substation-state", ["substation-state", substations, "--unspecified", "normal"]),
            ("cgmes-conform", ["cgmes-conform", cgmes]),
            ("cgmes-shacl", ["cgmes-shacl", cgmes, "--shapes", shapes]),
            ("probabilistic", ["probabilistic", prob, "--out", "own.json"]),
            ("reliability-network", ["reliability-network", reliability, "--out", "own.json"]),
            ("flicker", ["flicker", flicker, "--out", "own.json"]),
            ("filter-design", ["filter-design", filter, "--out", "own.json"]),
            ("harmonic-assess", ["harmonic-assess", harm, "--out", "own.json"]),
            ("connection-assess", ["connection-assess", conn, "--rules", "vde-ar-n-4110", "--out", "own.json"]),
            ("study-case", ["study-case", studyCase, "--out", "run"]),
            ("case-diff", ["case-diff", Data("uk"), Data("uk")]),
            ("result-diff", ["result-diff", ra, rb, "--out", "own.json"]),
            ("report", ["report", "--template-for", ra, "--out", "template.json"]),
            ("protection-eval", ["protection-eval", "case9", "--devices", devices9, "--branch", "1", "--fraction", "0.5"]),
            ("protection-grade", ["protection-grade", "case9", "--devices", devices9, "--types", "3ph"]),
            ("acopf-opt", ["acopf-opt", "case9", "--objective", "losses", "--out", "own.csv"]),
            ("acscopf", ["acscopf", "case9", "--contingencies", "1,2", "--out", "own.json"]),
            ("reconfig", ["reconfig", Data("matpower", "dist", "case33bw.m"), "--max-swaps", "2", "--out", "own.csv"]),
            ("grid-code", ["grid-code", gridCode, "--out", "own.json"]),
            ("reduce", ["reduce", "case14", "--keep", "1,2,3,4,5", "--method", "ward", "--out", "reduced.m"]),
        };
        return (fx, c);
    }

    // reliability-network: the R413 test's own case, groups, devices and study (its identity resolved by the verb).
    private static string ReliabilityStudy()
    {
        Type t = typeof(R413ReliabilityNetworkCliTests);
        var prepared = ((string Dir, string Sha))t.GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        MethodInfo study = t.GetMethod("Study", BindingFlags.NonPublic | BindingFlags.Static)!;
        ParameterInfo[] ps = study.GetParameters();
        string text = (string)study.Invoke(null, [prepared.Sha, "enumeration", ps[2].DefaultValue, ps[3].DefaultValue])!;
        string path = Path.Combine(prepared.Dir, "census.json");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// Run commands [from, from + count) once each with a journal (outputs named relative to a run folder), then let
    /// <c>breaking-change validate</c> re-run them. Returns the validate result document and the run folder.
    /// </summary>
    internal static void Census(int from, int count)
    {
        List<(string Name, string[] Args)> slice = Commands.Skip(from).Take(count).ToList();
        string work = JournalTestSupport.NewDir("census");
        try
        {
            string journal = Path.Combine(work, "journal", "journal.jsonl");
            string runs = Path.Combine(work, "runs");
            Directory.CreateDirectory(runs);
            var problems = new List<string>();
            foreach ((string name, string[] given) in slice)
            {
                // Each verb's own output gets a name of its own (a study verb never writes over an existing file).
                string[] args = given.Select(a => a is "own.json" or "own.csv" or "run" or "template.json" or "reduced.m" ? name + "-" + a : a).ToArray();
                string[] line = new[] { "--journal", journal }.Concat(args).Concat(["--json", name + ".json", "--csv", name + ".csv"]).ToArray();
                CliProcess.Result r = JournalTestSupport.Run(runs, line);
                if (r.ExitCode is not (0 or 3)) problems.Add($"{name}: the recorded run exited {r.ExitCode}: {r.StdErr}");
                else if (!File.Exists(Path.Combine(runs, name + ".json"))) problems.Add($"{name}: the recorded run wrote no result file: {r.StdErr}");
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
            Assert.Equal(slice.Count, JournalTestSupport.Lines(journal));

            JournalTestSupport.FromAnotherBuild(journal);
            string report = Path.Combine(work, "validate.json");
            CliProcess.Result v = JournalTestSupport.Run(work, "breaking-change", "validate", "--journal", journal, "--parallel", "4", "--json", report);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(report));
            var failures = new List<string>();
            foreach (JsonElement e in doc.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray())
                if (e.GetProperty("status").GetString() != ReplayStatus.Identical)
                    failures.Add($"{e.GetProperty("verb").GetString()}: {e.GetProperty("status").GetString()} {e.GetProperty("reason").GetString()} " +
                        $"worst field {e.GetProperty("worst_field").GetString()}");
            foreach (JsonElement f in doc.RootElement.GetProperty("tables").GetProperty("field_differences").EnumerateArray())
                failures.Add($"  seq {f.GetProperty("seq").GetInt64()} {f.GetProperty("file").GetString()} {f.GetProperty("field").GetString()} ({f.GetProperty("kind").GetString()})");
            Assert.True(failures.Count == 0, "the volatile-field list is incomplete or a verb is not deterministic:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
            Assert.True(v.ExitCode == 0, v.ToString());
            Assert.Equal(slice.Count, doc.RootElement.GetProperty("summary").GetProperty("identical").GetInt64());
        }
        finally { JournalTestSupport.Delete(work); }
    }
}

/// <summary>T7 census, first quarter of the writer verbs.</summary>
public sealed class BreakingChangeCensus1Tests
{
    [Fact]
    public void T7_census_part_1() => CensusCorpus.Census(0, 18);
}

/// <summary>T7 census, second quarter.</summary>
public sealed class BreakingChangeCensus2Tests
{
    [Fact]
    public void T7_census_part_2() => CensusCorpus.Census(18, 18);
}

/// <summary>T7 census, third quarter.</summary>
public sealed class BreakingChangeCensus3Tests
{
    [Fact]
    public void T7_census_part_3() => CensusCorpus.Census(36, 18);
}

/// <summary>T7 census, last quarter.</summary>
public sealed class BreakingChangeCensus4Tests
{
    [Fact]
    public void T7_census_part_4() => CensusCorpus.Census(54, 100);
}

/// <summary>T7: the census covers every writer verb, and the writer verbs outside it are refused by name.</summary>
public sealed class BreakingChangeCensusCoverageTests
{
    [Fact]
    public void Every_writer_verb_is_in_the_census_or_is_never_re_run_with_a_reason()
    {
        var canonical = CliOutput.WriterVerbs.Select(CliOutput.Canonical).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var census = CensusCorpus.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var missing = canonical.Where(v => !census.Contains(v) && !CensusCorpus.NeverWriterVerbs.Contains(v)).ToList();
        Assert.True(missing.Count == 0, "writer verbs not in the census: " + string.Join(", ", missing));
        var writer = canonical.ToHashSet(StringComparer.Ordinal);
        foreach (string v in CensusCorpus.NeverWriterVerbs)
            Assert.NotNull(ReplayPolicy.Refusal(v, [v], writer));
        foreach (string v in census)
            Assert.Null(ReplayPolicy.Refusal(v, [v], writer));
        Assert.Equal(canonical.Count, census.Count + CensusCorpus.NeverWriterVerbs.Length);
    }
}
